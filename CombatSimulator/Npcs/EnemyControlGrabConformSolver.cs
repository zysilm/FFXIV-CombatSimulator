using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Animation.SurfaceProfiles;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Npcs;

/// <summary>
/// Makes a EnemyControl's grab land ON the body instead of inside it.
///
/// The old grab pinned the player's neck RIGID BODY CENTRE to the EnemyControl's hand BONE ORIGIN — two
/// points made to coincide — so the neck sank into the palm by however thick the two of them were,
/// and by a different amount for every creature size. The fingers, meanwhile, were just playing the
/// grab animation and went straight through the throat.
///
/// Two fixes, independent of each other:
///
/// 1. <see cref="GripAnchor"/> — aim the grab at the middle of the hand's grip volume rather than at
///    the wrist joint. The offset is measured once, at grab time, from where the creature's own digits
///    sit relative to its hand, and held in hand-local space. Measuring it from the creature's own
///    bones is what makes it scale: a hand twice the size has digits twice as far out, so the grip
///    volume it reports is twice as deep, with nothing to tune per EnemyControl. It is frozen at grab time
///    rather than tracked live because the digits are about to be conformed onto the very body the
///    anchor is positioning — reading them every frame would close that loop and let it oscillate.
///
/// 2. <see cref="OnRenderFrame"/> — conform the digits to the grabbed body's surface. For each digit
///    joint, the child joint is rotated onto the skin: pushed out if it was inside, pulled in if it
///    was hovering. Fingers that end up inside a throat come out; fingers that were floating close
///    land on it. Joints far from the body are left to the animation, so this degrades to a no-op on
///    creatures whose "hand" is nowhere near what they grabbed.
///
/// The surface is the grabbed bone's race/body profile, with the legacy physics capsule as fallback,
/// not the skinned mesh. The profile uses a few elliptical rings per bone, retaining race/body
/// proportions without a per-frame re-skin and full triangle query.
///
/// Nothing here assumes a humanoid. Digits are discovered by walking the skeleton below the grab
/// bone, so claws, mandibles and three-fingered talons all work; a creature with no digits at all
/// still gets fix 1 and simply skips fix 2.
///
/// Multiple grabs can be armed at once (e.g. one hand on the neck, the other on the pelvis, for a
/// carry pose) — each gets its own <see cref="ConformInstance"/>, keyed by the same slot id the
/// ragdoll's grab constraint uses, so the two stay 1:1 for their whole lifetime.
/// </summary>
public sealed unsafe class EnemyControlGrabConformSolver : IDisposable
{
    /// <summary>Cap on how far below the hand a chain is followed. Keeps a weapon, or a pathological
    /// skeleton, from being mistaken for a very long finger.</summary>
    private const int MaxDigitJoints = 5;
    private const int MaxDigits = 8;

    /// <summary>A digit's own half-thickness, as a fraction of its bone length. Fingers are roughly
    /// this stubby across every creature, and deriving it from the bone keeps it scale-free.</summary>
    private const float DigitThicknessFraction = 0.35f;

    /// <summary>How far a joint may be from the skin and still be pulled onto it, in multiples of its
    /// own bone length. Beyond this the digit clearly isn't part of the grip and the animation keeps
    /// it.</summary>
    private const float ReachSegments = 2.5f;

    /// <summary>
    /// How long to let the grab animation blend in before the grip volume is measured for real.
    /// At the instant the grab is triggered the hand is still in whatever pose it was walking around
    /// with — usually open — and an open hand describes a grip volume far too deep. Measuring once
    /// the grasp pose has actually arrived is the difference between the body sitting in the hand and
    /// floating off the fingertips. The conform is held off until then too, so the measurement is
    /// taken from the animation's own pose and not from digits this class has already bent.
    /// </summary>
    private const float GripSettleSeconds = 0.3f;

    private readonly BoneTransformService boneService;
    private readonly RagdollController playerRagdoll;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private sealed class ConformInstance
    {
        public nint ControlledAddress;
        public string HandBone = string.Empty;
        public string PlayerBone = string.Empty;

        // Bone indices are only valid for the skeleton they were read from; a gear or glamour change
        // rebuilds the draw object and renumbers everything. Bail rather than curl whatever bones now
        // happen to sit at those indices.
        public int ArmedBoneCount;

        // Digit chains under the grab hand (bone indices, knuckle → tip), resolved once per grab.
        public readonly List<int[]> Digits = new();

        // Grip volume in hand-local space; see the type comment for why it is captured, not tracked.
        public Vector3 LocalGripOffset;
        public bool GripCaptured;

        // Grip is measured once the grasp animation has arrived — see GripSettleSeconds.
        public float SettleElapsed;
        public bool GripSettled;
    }

    private readonly Dictionary<int, ConformInstance> instances = new();
    // Scratch space for one digit-joint solve pass; cleared and reused per depth, per instance —
    // safe to share because instances are processed one at a time, never concurrently.
    private readonly Dictionary<int, Quaternion> deltas = new();
    // Instances that failed mid-frame (skeleton renumbered, conform threw) get torn down after the
    // loop that found them, rather than mutating the dictionary while enumerating it.
    private readonly List<int> pendingRemovals = new();

    public EnemyControlGrabConformSolver(BoneTransformService boneService, RagdollController playerRagdoll,
        Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.playerRagdoll = playerRagdoll;
        this.config = config;
        this.log = log;

        boneService.OnRenderFrame += OnRenderFrame;
    }

    /// <summary>
    /// Arm the solver for one grab: find the hand's digits and measure its grip volume from the pose
    /// the animation is holding right now. False if the hand bone can't be resolved — the caller
    /// should fall back to the raw bone position. Re-arming an already-armed slot id replaces it.
    /// </summary>
    public bool Begin(int slotId, nint EnemyControl, string hand, string player)
    {
        End(slotId);

        var skelN = boneService.TryGetSkeleton(EnemyControl);
        if (skelN == null) return false;
        var skel = skelN.Value;

        var handIndex = boneService.ResolveBoneIndex(skel, hand);
        if (handIndex < 0) return false;

        var inst = new ConformInstance
        {
            ControlledAddress = EnemyControl,
            HandBone = hand,
            PlayerBone = player,
            ArmedBoneCount = skel.BoneCount,
        };

        DiscoverDigits(skel, handIndex, inst);
        // A provisional measurement off the current pose, so the grab has something to aim at from the
        // first frame; Tick replaces it with the real one once the grasp pose has blended in.
        CaptureGrip(skel, handIndex, inst);

        instances[slotId] = inst;

        log.Info($"GrabConform: armed #{slotId} on '{hand}' — {inst.Digits.Count} digit(s), " +
                 $"grip offset {(inst.GripCaptured ? inst.LocalGripOffset.Length().ToString("F3") + "m" : "none (using bone origin)")}");
        return true;
    }

    /// <summary>
    /// Re-measures every armed grip once its grasp animation has actually reached the hand. Drive it
    /// from the grab's own tick.
    /// </summary>
    public void Tick(float dt)
    {
        if (instances.Count == 0) return;

        foreach (var (slotId, inst) in instances)
        {
            if (inst.GripSettled) continue;

            inst.SettleElapsed += dt;
            if (inst.SettleElapsed < GripSettleSeconds) continue;
            inst.GripSettled = true;

            var skelN = boneService.TryGetSkeleton(inst.ControlledAddress);
            if (skelN == null) continue;
            var skel = skelN.Value;
            if (skel.BoneCount != inst.ArmedBoneCount) { pendingRemovals.Add(slotId); continue; }

            var handIndex = boneService.ResolveBoneIndex(skel, inst.HandBone);
            if (handIndex < 0) continue;

            CaptureGrip(skel, handIndex, inst);
            log.Info($"GrabConform: grip #{slotId} settled — offset {inst.LocalGripOffset.Length():F3}m");
        }

        FlushPendingRemovals();
    }

    /// <summary>Disarm one grab.</summary>
    public void End(int slotId) => instances.Remove(slotId);

    /// <summary>Disarm every grab (session end / despawn).</summary>
    public void EndAll() => instances.Clear();

    /// <summary>
    /// Where the grabbed body should sit: the centre of the hand's grip volume, in world space. Null
    /// when the slot isn't armed or the hand has gone away — the caller then keeps its old behaviour.
    /// </summary>
    public Vector3? GripAnchor(int slotId)
    {
        if (!instances.TryGetValue(slotId, out var inst)) return null;

        var hand = boneService.GetBoneWorldTransform(inst.ControlledAddress, inst.HandBone);
        if (hand == null) return null;
        if (!inst.GripCaptured) return hand.Value.Position;

        var reach = MathF.Max(0f, config.EnemyControlGrabGripReach);
        return hand.Value.Position + Vector3.Transform(inst.LocalGripOffset * reach, hand.Value.Rotation);
    }

    /// <summary>
    /// Every root-to-leaf path below the hand is a digit. Bones whose names start with "n_" are
    /// skipped: that is the game's prefix for helper/attachment bones (weapon mounts hang off the
    /// hand exactly like a finger would), and curling a sword into someone's neck is not the goal.
    /// </summary>
    private static void DiscoverDigits(SkeletonAccess skel, int handIndex, ConformInstance inst)
    {
        var count = Math.Min(skel.BoneCount, skel.ParentCount);
        if (handIndex < 0 || handIndex >= count) return;

        var parents = skel.HavokSkeleton->ParentIndices;
        var bones = skel.HavokSkeleton->Bones;

        var children = new List<int>?[count];
        for (int i = 1; i < count; i++)
        {
            var parent = parents[i];
            if (parent < 0 || parent >= count) continue;
            (children[parent] ??= new List<int>()).Add(i);
        }

        var roots = children[handIndex];
        if (roots == null) return;

        foreach (var root in roots)
        {
            if (inst.Digits.Count >= MaxDigits) break;
            if (root >= bones.Length) continue;
            if (bones[root].Name.String is { } name && name.StartsWith("n_", StringComparison.Ordinal))
                continue;

            var chain = new List<int> { root };
            var current = root;
            while (chain.Count < MaxDigitJoints)
            {
                var kids = children[current];
                if (kids == null || kids.Count == 0) break;
                current = kids[0]; // digits don't branch; take the first child
                chain.Add(current);
            }

            // A single joint has no segment to rotate — nothing to conform.
            if (chain.Count >= 2) inst.Digits.Add(chain.ToArray());
        }
    }

    private static void CaptureGrip(SkeletonAccess skel, int handIndex, ConformInstance inst)
    {
        inst.GripCaptured = false;
        if (inst.Digits.Count == 0) return;

        if (!TryGetSkeletonTransform(skel, out var skelPos, out var skelRot)) return;

        var sum = Vector3.Zero;
        var n = 0;
        foreach (var chain in inst.Digits)
            foreach (var bone in chain)
            {
                sum += BoneWorldPosition(skel, skelPos, skelRot, bone);
                n++;
            }
        if (n == 0) return;

        var (handPos, handRot) = BoneWorldTransform(skel, skelPos, skelRot, handIndex);
        inst.LocalGripOffset = Vector3.Transform(sum / n - handPos, Quaternion.Inverse(handRot));
        inst.GripCaptured = true;
    }

    /// <summary>
    /// Runs in the render pass, where the ragdoll and the dismemberment writes already live — the
    /// animation has re-driven the pose by then, so a write from the framework tick would be
    /// overwritten before it ever reached the screen. Every armed, settled instance gets its own
    /// conform pass; one hand's failure doesn't take the others down with it.
    /// </summary>
    private void OnRenderFrame()
    {
        if (instances.Count == 0 || !config.EnemyControlGrabConformFingers) return;

        foreach (var (slotId, inst) in instances)
        {
            // Held off until the grip has been measured, so that measurement is taken from the
            // animation's pose rather than from digits this pass has already curled.
            if (inst.Digits.Count == 0 || !inst.GripSettled) continue;

            var skelN = boneService.TryGetSkeleton(inst.ControlledAddress);
            if (skelN == null) continue;
            var skel = skelN.Value;
            if (skel.BoneCount != inst.ArmedBoneCount) { pendingRemovals.Add(slotId); continue; }

            try
            {
                ConformOne(inst, skel);
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"GrabConform: conform pass failed for #{slotId}; disarming");
                pendingRemovals.Add(slotId);
            }
        }

        FlushPendingRemovals();
    }

    private void ConformOne(ConformInstance inst, SkeletonAccess skel)
    {
        if (!TryGetSkeletonTransform(skel, out var skelPos, out var skelRot)) return;

        // Keep the old capsule path for unsupported/generic actors. Supported humanoids use the
        // same profile triangles as traversal and physical contact, so fingers no longer conform
        // to a different (usually rounder and larger) body than the one they visibly hold.
        var hasCapsule = playerRagdoll.TryGetBoneCapsule(inst.PlayerBone, out var capsule);
        var axis = hasCapsule
            ? Vector3.Transform(Vector3.UnitY, capsule.Orientation)
            : Vector3.UnitY;
        var axisStart = hasCapsule ? capsule.Center - axis * capsule.HalfLength : Vector3.Zero;
        var axisEnd = hasCapsule ? capsule.Center + axis * capsule.HalfLength : Vector3.Zero;

        var strength = Math.Clamp(config.EnemyControlGrabFingerStrength, 0f, 1f);
        var maxAngle = float.DegreesToRadians(Math.Clamp(config.EnemyControlGrabFingerMaxAngle, 0f, 120f));
        if (strength <= 0f || maxAngle <= 0f) return;

        var deepest = 0;
        foreach (var chain in inst.Digits)
            deepest = Math.Max(deepest, chain.Length - 1);

        // Solve joint by joint, knuckle outwards. Each pass writes the pose, so the next joint
        // out reads a hand that has already been partly closed — the chain curls rather than
        // every joint independently stabbing at the same spot.
        for (int depth = 0; depth < deepest; depth++)
        {
            deltas.Clear();

            foreach (var chain in inst.Digits)
            {
                if (depth + 1 >= chain.Length) continue;

                var (jointPos, jointRot) = BoneWorldTransform(skel, skelPos, skelRot, chain[depth]);
                var tipPos = BoneWorldPosition(skel, skelPos, skelRot, chain[depth + 1]);

                var segment = tipPos - jointPos;
                var segmentLength = segment.Length();
                if (segmentLength < 1e-4f) continue;

                var clearance = segmentLength * DigitThicknessFraction +
                                MathF.Max(0f, config.EnemyControlGrabFingerClearance);
                Vector3 surface;
                if (playerRagdoll.TryGetCharacterSurface(
                        inst.PlayerBone, tipPos, CharacterSurfaceUsage.Grab, out var profileHit))
                {
                    surface = profileHit.Point + profileHit.Normal * clearance;
                }
                else if (hasCapsule)
                {
                    surface = SurfacePoint(axisStart, axisEnd, axis, capsule.Radius + clearance, tipPos);
                }
                else
                {
                    continue;
                }

                // Nowhere near the body: this digit isn't part of the grip, leave it animating.
                if (Vector3.Distance(tipPos, surface) > segmentLength * ReachSegments) continue;

                var toSurface = surface - jointPos;
                if (toSurface.LengthSquared() < 1e-8f) continue;

                // Swing the joint so its tip lands on the skin, without letting the bone stretch.
                var swing = RotationBetween(segment / segmentLength, Vector3.Normalize(toSurface));
                swing = ClampAngle(swing, maxAngle);
                swing = Quaternion.Slerp(Quaternion.Identity, swing, strength);

                // ApplyRotationDeltas right-multiplies in the bone's own frame, so a world-space
                // swing has to be conjugated into it.
                deltas[chain[depth]] = Quaternion.Normalize(
                    Quaternion.Inverse(jointRot) * swing * jointRot);
            }

            if (deltas.Count > 0)
                boneService.ApplyRotationDeltas(skel, deltas);
        }
    }

    private void FlushPendingRemovals()
    {
        if (pendingRemovals.Count == 0) return;
        foreach (var id in pendingRemovals) instances.Remove(id);
        pendingRemovals.Clear();
    }

    /// <summary>Nearest point on the capsule's surface to <paramref name="point"/>, at the given
    /// radius. When the point sits exactly on the axis the radial direction is undefined, so any
    /// perpendicular will do — the joint is buried and every way out is as good as another.</summary>
    private static Vector3 SurfacePoint(Vector3 axisStart, Vector3 axisEnd, Vector3 axis, float radius, Vector3 point)
    {
        var onAxis = ClosestPointOnSegment(axisStart, axisEnd, point);
        var radial = point - onAxis;
        var length = radial.Length();
        var outward = length > 1e-4f ? radial / length : AnyPerpendicular(axis);
        return onAxis + outward * radius;
    }

    private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-8f) return a;
        var t = Math.Clamp(Vector3.Dot(point - a, ab) / lengthSq, 0f, 1f);
        return a + ab * t;
    }

    private static Vector3 AnyPerpendicular(Vector3 v)
    {
        var candidate = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(v, candidate));
    }

    private static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(AnyPerpendicular(from), MathF.PI);
        var axis = Vector3.Normalize(Vector3.Cross(from, to));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }

    private static Quaternion ClampAngle(Quaternion rotation, float maxRadians)
    {
        rotation = Quaternion.Normalize(rotation);
        var angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(rotation.W), -1f, 1f));
        if (angle <= maxRadians || angle < 1e-5f) return rotation;
        return Quaternion.Slerp(Quaternion.Identity, rotation, maxRadians / angle);
    }

    private static bool TryGetSkeletonTransform(SkeletonAccess skel,
        out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.Zero;
        rotation = Quaternion.Identity;

        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return false;

        position = new Vector3(
            skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z);
        rotation = new Quaternion(
            skeleton->Transform.Rotation.X, skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z, skeleton->Transform.Rotation.W);
        return true;
    }

    private static Vector3 BoneWorldPosition(SkeletonAccess skel,
        Vector3 skelPos, Quaternion skelRot, int boneIndex)
    {
        ref var model = ref skel.Pose->ModelPose.Data[boneIndex];
        var local = new Vector3(model.Translation.X, model.Translation.Y, model.Translation.Z);
        return skelPos + Vector3.Transform(local, skelRot);
    }

    private static (Vector3 Position, Quaternion Rotation) BoneWorldTransform(
        SkeletonAccess skel, Vector3 skelPos, Quaternion skelRot, int boneIndex)
    {
        ref var model = ref skel.Pose->ModelPose.Data[boneIndex];
        var local = new Vector3(model.Translation.X, model.Translation.Y, model.Translation.Z);
        var rotation = new Quaternion(
            model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W);
        return (skelPos + Vector3.Transform(local, skelRot), Quaternion.Normalize(skelRot * rotation));
    }

    public void Dispose()
    {
        boneService.OnRenderFrame -= OnRenderFrame;
        EndAll();
    }
}
