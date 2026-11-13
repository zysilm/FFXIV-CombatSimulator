using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Npcs;

/// <summary>
/// Foot placement for an actor walking over a corpse, split between the pelvis (root) and each leg's
/// own two-bone IK — the same three-phase shape most game foot-IK systems use (ground-detect per foot,
/// pick a shared pelvis offset from whichever foot needs it least, solve each leg's remaining reach):
///
/// 1. Each foot's required height is read straight down from wherever the walk cycle already put that
///    foot this frame — not searched for. The animation is already moving the feet forward; IK only
///    has to correct the vertical alignment of a foot that's already there.
/// 2. <see cref="GetPelvisOffset"/> exposes the smaller of the two feet's requirements (0 when only one
///    foot needs anything, which is also the ordinary single-foot case) for the caller's own root
///    height to add on top of terrain — so root and legs split one stance instead of the root ignoring
///    the corpse entirely and every foot forced to close the full distance alone.
/// 3. Each leg then only has to close whatever its own requirement still exceeds that shared offset by,
///    via an analytic two-bone solve whose bend plane comes from the leg's own current pole vector
///    (where the knee already sits relative to a straight hip→ankle line) rather than an assumed
///    "standing" shape — so it holds up in a wide combat stance or a crouch, not just idle.
///
/// Feature-detected per actor: a skeleton missing any of the six leg bones (non-humanoid monsters,
/// demihumans, etc.) is left untouched, and <see cref="GetPelvisOffset"/> stays 0 — callers fall back
/// to whatever their own root correction already does for those.
/// </summary>
public sealed unsafe class NpcCorpseFootIkSolver : IDisposable
{
    /// <summary>How far above the animated ankle a corpse surface has to sit before the leg bends for
    /// it — filters out surface noise so a foot resting almost flush with a corpse doesn't twitch.</summary>
    private const float EngageThreshold = 0.03f;

    /// <summary>Once a leg is bending for a corpse it keeps doing so until the surface has dropped to
    /// this close to the animated ankle. Without the gap between this and <see cref="EngageThreshold"/>
    /// a foot whose requirement hovers around the engage height (every foot does, once the root has
    /// risen to meet it) would blend in and out every other frame.</summary>
    private const float DisengageThreshold = 0.01f;

    /// <summary>Base ceiling on how far a leg will reach up for a corpse surface, scaled by the actor's
    /// visual size. Matches the ceiling the root correction already used for the same reason: crawling
    /// over a corpse is an opt-in movement policy, not a literal standing leg-length test.</summary>
    private const float MaxStepHeightBase = 0.65f;

    /// <summary>Guard against a degenerate solve swinging the hip or knee into an unnatural pose (e.g. a
    /// target almost behind the actor). Above this the swing is softly capped rather than forced
    /// further, so a momentarily-extreme target eases the leg to its limit instead of popping the
    /// correction on/off.</summary>
    private const float MaxJointSwingRadians = 1.22f; // ~70 degrees

    /// <summary>Caps how fast the target height itself may travel, in metres/second. The corpse query
    /// can wobble slightly frame to frame (settling physics, evolving mesh pose); this keeps that from
    /// reading as a twitch without meaningfully delaying a genuine step-up.</summary>
    private const float TargetHeightMaxSpeed = 3.0f;

    private const float BlendInSeconds = 0.18f;
    private const float BlendOutSeconds = 0.35f;
    private const int StaleAfterMissedTicks = 3;

    private readonly BoneTransformService boneService;
    private readonly IPluginLog log;

    /// <summary>Same seam NpcAiController/CombatCompanionManager use: world-space surface height under
    /// a point, aggregated across every active ragdoll. Null = nothing walkable there.</summary>
    public Func<nint, Vector3, float?>? CorpseSupportHeightProvider { private get; set; }

    private sealed class LegBones
    {
        public int Hip = -1;
        public int Knee = -1;
        public int Ankle = -1;
        public bool Resolved => Hip >= 0 && Knee >= 0 && Ankle >= 0;
    }

    private sealed class LegBlend
    {
        public float Weight;
        public float LastTargetY;
        public bool HasTarget;
        // What the leg is actually reaching for: LastTargetY rate-limited (see TargetHeightMaxSpeed) so
        // small frame-to-frame noise in the corpse query reads as a slide, not a twitch.
        public float SmoothedTargetY;
        public bool HasSmoothedTarget;
    }

    private sealed class ActorState
    {
        public int ArmedBoneCount = -1;
        public bool Supported;
        public readonly LegBones Left = new();
        public readonly LegBones Right = new();
        public readonly LegBlend BlendL = new();
        public readonly LegBlend BlendR = new();
        public float PelvisOffsetY;
        // How far the caller has already raised this actor's root above its terrain stance for a
        // corpse (see ReportRootLift). The ankles are measured on that raised pose, so a foot's
        // remaining gain plus this lift is the absolute height the stance needs; the remaining gain
        // alone would shrink by exactly what the root just rose, and the root would chase it back down.
        public float AppliedRootLift;
        public float LastDeltaTime = 1f / 60f;
        public int MissedTicks;
    }

    private readonly Dictionary<nint, ActorState> actors = new();
    private readonly Dictionary<int, Quaternion> deltas = new();
    private readonly List<nint> pendingRemovals = new();

    public NpcCorpseFootIkSolver(BoneTransformService boneService, IPluginLog log)
    {
        this.boneService = boneService;
        this.log = log;
        boneService.OnRenderFrame += OnRenderFrame;
    }

    /// <summary>Mark an actor as active for this frame. Cheap and idempotent — call every tick for
    /// every actor that might be walking over a corpse; entries age out on their own once calls stop.</summary>
    public void Track(nint actorAddress, float deltaTime)
    {
        if (actorAddress == nint.Zero) return;

        if (!actors.TryGetValue(actorAddress, out var state))
        {
            state = new ActorState();
            actors[actorAddress] = state;
        }

        state.MissedTicks = 0;
        state.LastDeltaTime = deltaTime > 0f ? deltaTime : state.LastDeltaTime;
    }

    /// <summary>
    /// Tell the solver how far the caller currently holds this actor's root above its plain terrain
    /// stance on account of a corpse (0 when it stands on terrain). Call it every tick right after
    /// the root height is decided, with the value the skeleton will be rendered at. The solver
    /// measures the feet on that raised pose; adding the lift back turns each foot's remaining gain
    /// into the absolute stance height it asks for, so <see cref="GetPelvisOffset"/> is a fixed
    /// target rather than a correction the root then eats and re-requests every frame.
    /// </summary>
    public void ReportRootLift(nint actorAddress, float liftAboveTerrain)
    {
        if (actorAddress == nint.Zero || !actors.TryGetValue(actorAddress, out var state)) return;
        state.AppliedRootLift = float.IsFinite(liftAboveTerrain) ? MathF.Max(0f, liftAboveTerrain) : 0f;
    }

    /// <summary>True once this actor's leg bones have been resolved and this solver is actively
    /// covering its foot placement — callers that also raise root height for a corpse (e.g.
    /// EnemyControl's directly-driven creatures) should add <see cref="GetPelvisOffset"/> instead of
    /// running their own independent corpse query while this is true. False (including "not tracked
    /// yet") means the caller's own root-height handling is the only thing covering this actor.</summary>
    public bool IsSupported(nint actorAddress) =>
        actors.TryGetValue(actorAddress, out var state) && state.Supported;

    /// <summary>Shared height both legs agree the body could rise by without either one overreaching —
    /// the smaller of what each leg's own target currently needs (0 counts as "doesn't need anything"
    /// for a leg that isn't engaging, so a single foot on a corpse naturally yields 0 here and that
    /// leg's own IK does all the work, exactly as when a real stance leg stays planted while the other
    /// swings up). Add this to terrain height instead of deciding independently whether to rise, so
    /// root and legs split one stance instead of double-reacting to the same contact.</summary>
    public float GetPelvisOffset(nint actorAddress) =>
        actors.TryGetValue(actorAddress, out var state) ? state.PelvisOffsetY : 0f;

    private static long costTicks;

    /// <summary>Milliseconds spent placing feet on corpses since the last call.</summary>
    public static double TakeCostMs()
    {
        var ms = costTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        costTicks = 0;
        return ms;
    }

    private void OnRenderFrame()
    {
        if (actors.Count == 0 || CorpseSupportHeightProvider == null) return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            SolveAll();
        }
        finally
        {
            costTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
    }

    private void SolveAll()
    {

        foreach (var (address, state) in actors)
        {
            if (++state.MissedTicks > StaleAfterMissedTicks)
            {
                pendingRemovals.Add(address);
                continue;
            }

            try
            {
                ProcessActor(address, state);
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"NpcCorpseFootIk: solve failed for 0x{address:X}; dropping");
                pendingRemovals.Add(address);
            }
        }

        FlushPendingRemovals();
    }

    private void ProcessActor(nint address, ActorState state)
    {
        var skelN = boneService.TryGetSkeleton(address);
        if (skelN == null) return;
        var skel = skelN.Value;

        if (skel.BoneCount != state.ArmedBoneCount)
        {
            state.ArmedBoneCount = skel.BoneCount;
            ResolveLeg(skel, "_l", state.Left);
            ResolveLeg(skel, "_r", state.Right);
            state.Supported = state.Left.Resolved && state.Right.Resolved;
            log.Info($"NpcCorpseFootIk: leg bones for 0x{address:X} — " +
                     $"L(hip={state.Left.Hip},knee={state.Left.Knee},ankle={state.Left.Ankle}) " +
                     $"R(hip={state.Right.Hip},knee={state.Right.Knee},ankle={state.Right.Ankle}) " +
                     $"supported={state.Supported}");
        }

        if (!state.Supported)
        {
            state.PelvisOffsetY = 0f;
            return;
        }

        if (!TryGetSkeletonTransform(skel, out var skelPos, out var skelRot)) return;

        var maxStepHeight = MaxStepHeightBase * MathF.Max(1f, GetVisualScale(address));

        var neededL = SolveLeg(address, skel, skelPos, skelRot, state.Left, state.BlendL, maxStepHeight, state.LastDeltaTime, state.AppliedRootLift);
        var neededR = SolveLeg(address, skel, skelPos, skelRot, state.Right, state.BlendR, maxStepHeight, state.LastDeltaTime, state.AppliedRootLift);
        state.PelvisOffsetY = MathF.Min(neededL, neededR);
    }

    /// <summary>
    /// One leg's IK pass. Returns the absolute stance height this foot asks the root for (0 when
    /// there is no corpse under it): the surface height above the animated ankle plus the lift the
    /// caller already applies, so it stays the same number while the root rises to it. It is
    /// independent of this leg's own blend weight/timing below, which only governs how the leg's
    /// own correction eases in and out once applied.
    /// </summary>
    private float SolveLeg(
        nint address, SkeletonAccess skel, Vector3 skelPos, Quaternion skelRot,
        LegBones bones, LegBlend blend, float maxStepHeight, float deltaTime, float appliedRootLift)
    {
        var hipPos = BoneWorldPosition(skel, skelPos, skelRot, bones.Hip);
        var kneePos = BoneWorldPosition(skel, skelPos, skelRot, bones.Knee);
        var (anklePos, ankleRotOriginal) = BoneWorldTransform(skel, skelPos, skelRot, bones.Ankle);

        var thighLen = Vector3.Distance(hipPos, kneePos);
        var shinLen = Vector3.Distance(kneePos, anklePos);
        if (thighLen < 1e-4f || shinLen < 1e-4f) return 0f;
        var thighDir0 = (kneePos - hipPos) / thighLen;

        // Straight down from wherever the walk cycle already put this foot — the animation is already
        // moving it forward each frame; this only has to catch the vertical alignment up.
        var groundY = CorpseSupportHeightProvider!(address, anklePos);
        var neededGain = 0f;
        var wantsEngage = false;
        if (groundY.HasValue)
        {
            var gain = groundY.Value - anklePos.Y;
            // The pelvis asks for the whole stance height, lift included, whenever a corpse is under
            // the foot at all; the leg's own bend only engages for what the root has not covered,
            // and stays engaged down to DisengageThreshold so it does not flicker at the edge.
            if (gain + appliedRootLift <= maxStepHeight)
                neededGain = MathF.Max(0f, gain + appliedRootLift);
            var engageAt = blend.Weight > 0.001f ? DisengageThreshold : EngageThreshold;
            wantsEngage = gain > engageAt && gain <= maxStepHeight;
        }

        if (wantsEngage && blend.Weight < 0.01f)
        {
            log.Info($"NpcCorpseFootIk: 0x{address:X} leg engaging — ankleY={anklePos.Y:F3} " +
                     $"groundY={groundY!.Value:F3} maxStepHeight={maxStepHeight:F3}");
        }

        if (wantsEngage)
        {
            blend.LastTargetY = groundY!.Value;
            blend.HasTarget = true;
        }

        var blendSeconds = wantsEngage ? BlendInSeconds : BlendOutSeconds;
        var target = wantsEngage ? 1f : 0f;
        blend.Weight = MoveTowards(blend.Weight, target, deltaTime / MathF.Max(0.01f, blendSeconds));

        if (blend.Weight <= 0.001f || !blend.HasTarget)
        {
            blend.HasSmoothedTarget = false;
            return neededGain;
        }
        if (blend.Weight <= 0f) blend.HasTarget = false;

        if (!blend.HasSmoothedTarget)
        {
            blend.SmoothedTargetY = blend.LastTargetY;
            blend.HasSmoothedTarget = true;
        }
        else
        {
            blend.SmoothedTargetY = MoveTowards(blend.SmoothedTargetY, blend.LastTargetY, TargetHeightMaxSpeed * deltaTime);
        }

        var targetPos = anklePos with { Y = blend.SmoothedTargetY };
        var reachVec = targetPos - hipPos;
        var reachLenRaw = reachVec.Length();
        if (reachLenRaw < 1e-4f) return neededGain;

        var maxReach = thighLen + shinLen - 0.001f;
        var minReach = MathF.Abs(thighLen - shinLen) + 0.001f;
        var reachLen = Math.Clamp(reachLenRaw, minReach, maxReach);
        var toTargetDir = reachVec / reachLenRaw;

        // Where the knee currently sits off the straight hip→ankle line, as a direction — this is the
        // pole vector: it says which side of the hip→target line the knee is allowed to bulge toward,
        // and it comes straight from whatever the base animation is doing right now (braced, crouched,
        // a wide combat stance, anything), rather than assuming any one "neutral standing" shape.
        var hipToAnkleDir0 = Vector3.Normalize(anklePos - hipPos);
        var hipToKnee0 = kneePos - hipPos;
        var poleVector = hipToKnee0 - hipToAnkleDir0 * Vector3.Dot(hipToKnee0, hipToAnkleDir0);
        if (poleVector.LengthSquared() < 1e-6f) poleVector = AnyPerpendicular(hipToAnkleDir0);
        poleVector = Vector3.Normalize(poleVector);

        // Solve the knee position directly in the plane containing hip→target and the pole vector,
        // rather than composing an "aim the chain, then bend by a delta" pair of rotations — the two
        // don't commute the way that seems like it should for anything but a small bend, and silently
        // land the ankle well off target with the knee bulging on the wrong side once the required
        // bend gets large. Standard circle-circle intersection: knee is thighLen from hip and shinLen
        // from target, in-plane, on the pole's side.
        var planeNormal = Vector3.Cross(toTargetDir, poleVector);
        if (planeNormal.LengthSquared() < 1e-8f) planeNormal = AnyPerpendicular(toTargetDir);
        var inPlaneUp = Vector3.Normalize(Vector3.Cross(planeNormal, toTargetDir));
        if (Vector3.Dot(inPlaneUp, poleVector) < 0f) inPlaneUp = -inPlaneUp;

        var kneeAlong = (thighLen * thighLen - shinLen * shinLen + reachLen * reachLen) / (2f * reachLen);
        var kneeUp = MathF.Sqrt(MathF.Max(0f, thighLen * thighLen - kneeAlong * kneeAlong));
        var solvedKneePos = hipPos + toTargetDir * kneeAlong + inPlaneUp * kneeUp;

        var newThighDir = Vector3.Normalize(solvedKneePos - hipPos);
        var hipSwing = ClampSwingAngle(RotationBetween(thighDir0, newThighDir), MaxJointSwingRadians);
        var blendedHip = Quaternion.Slerp(Quaternion.Identity, hipSwing, blend.Weight);

        var (_, hipRotNow) = BoneWorldTransform(skel, skelPos, skelRot, bones.Hip);
        deltas.Clear();
        deltas[bones.Hip] = Quaternion.Normalize(Quaternion.Inverse(hipRotNow) * blendedHip * hipRotNow);
        boneService.ApplyRotationDeltas(skel, deltas);

        // The hip rotation just applied carries the (old) shin along with it rigidly; from there, swing
        // the knee so the shin points at the target exactly the way the triangulation above intends.
        var shinDirAfterHip = Vector3.Transform(Vector3.Normalize(anklePos - kneePos), blendedHip);
        var newShinDir = Vector3.Normalize(targetPos - solvedKneePos);
        var kneeSwing = ClampSwingAngle(RotationBetween(shinDirAfterHip, newShinDir), MaxJointSwingRadians);
        var blendedKnee = Quaternion.Slerp(Quaternion.Identity, kneeSwing, blend.Weight);

        var (_, kneeRotNow) = BoneWorldTransform(skel, skelPos, skelRot, bones.Knee);
        deltas.Clear();
        deltas[bones.Knee] = Quaternion.Normalize(Quaternion.Inverse(kneeRotNow) * blendedKnee * kneeRotNow);
        boneService.ApplyRotationDeltas(skel, deltas);

        // The hip/knee swings above carry the foot's orientation along with them (a rigid rotation
        // about each pivot), so without this the sole ends up tilted to whatever angle the bend left
        // it at — heel or toe touching down instead of the flat sole. Restore the ankle's own world
        // orientation back to what the base animation had it at (flat, weight-bearing), independent
        // of how the leg above it had to twist to get here.
        var (_, ankleRotNow) = BoneWorldTransform(skel, skelPos, skelRot, bones.Ankle);
        var levelSwing = Quaternion.Normalize(ankleRotOriginal * Quaternion.Inverse(ankleRotNow));
        var blendedLevel = Quaternion.Slerp(Quaternion.Identity, levelSwing, blend.Weight);
        deltas.Clear();
        deltas[bones.Ankle] = Quaternion.Normalize(Quaternion.Inverse(ankleRotNow) * blendedLevel * ankleRotNow);
        boneService.ApplyRotationDeltas(skel, deltas);

        return neededGain;
    }

    private void ResolveLeg(SkeletonAccess skel, string suffix, LegBones bones)
    {
        bones.Hip = boneService.ResolveBoneIndex(skel, "j_asi_a" + suffix);
        bones.Knee = boneService.ResolveBoneIndex(skel, "j_asi_b" + suffix);
        bones.Ankle = boneService.ResolveBoneIndex(skel, "j_asi_d" + suffix);
    }

    private void FlushPendingRemovals()
    {
        if (pendingRemovals.Count == 0) return;
        foreach (var address in pendingRemovals) actors.Remove(address);
        pendingRemovals.Clear();
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (MathF.Abs(target - current) <= maxDelta) return target;
        return current + MathF.Sign(target - current) * maxDelta;
    }

    private static float SwingAngle(Quaternion q) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), -1f, 1f));

    private static Quaternion ClampSwingAngle(Quaternion rotation, float maxRadians)
    {
        rotation = Quaternion.Normalize(rotation);
        var angle = SwingAngle(rotation);
        if (angle <= maxRadians || angle < 1e-5f) return rotation;
        return Quaternion.Slerp(Quaternion.Identity, rotation, maxRadians / angle);
    }

    private static float GetVisualScale(nint actorAddress)
    {
        if (actorAddress == nint.Zero) return 1f;
        var gameObject = (GameObject*)actorAddress;
        if (gameObject->DrawObject == null) return 1f;
        var scale = gameObject->DrawObject->Scale;
        var max = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
        return float.IsFinite(max) && max > 0f ? max : 1f;
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

    private static bool TryGetSkeletonTransform(SkeletonAccess skel, out Vector3 position, out Quaternion rotation)
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

    private static Vector3 BoneWorldPosition(SkeletonAccess skel, Vector3 skelPos, Quaternion skelRot, int boneIndex)
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
        var rotation = new Quaternion(model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W);
        return (skelPos + Vector3.Transform(local, skelRot), Quaternion.Normalize(skelRot * rotation));
    }

    public void Dispose()
    {
        boneService.OnRenderFrame -= OnRenderFrame;
        actors.Clear();
    }
}
