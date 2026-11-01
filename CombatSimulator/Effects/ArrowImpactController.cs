// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using CombatSimulator.Animation;
using CombatSimulator.Rendering.WorldGeometry;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Effects;

/// <summary>A projectile has lodged in a body: where it went in and the way its shaft points.</summary>
public readonly record struct ArrowWound(nint Target, uint TargetEntityId, Vector3 Entry, Vector3 Direction);

/// <summary>
/// Arrows that stay lodged in the body they struck. Every damaging hit from a bow wielder, player or
/// enemy, queues an arrow that lands when the shot arrives. The arrow drawn is the shooter's own
/// bow's arrow (<see cref="ArrowMeshLibrary"/>), submitted each frame through the world geometry
/// layer as an opaque prop.
///
/// Where it lands: the body is approximated by one capsule per bone (the bone to its parent, with a
/// flesh radius). Preferred bones are raw skeleton bones; with some configured, an arrow lands on one
/// of them most of the time, on a neighbouring bone some of the time, and anywhere otherwise. The
/// entry is always on the side facing the shooter and the shaft points back at the shooter.
///
/// How it rides: the arrow is pinned by two points, its tip and a point 30 cm back along the shaft,
/// each held in the frames of two bones (the struck bone and its parent) and blended by where along
/// the limb it hit, the way skin is weighted. A joint bending or twisting then moves the arrow as it
/// moves the flesh, instead of swinging it with one bone.
///
/// Poses are read in the final read-only pass, after every bone modifier (ragdoll, grabs, conform
/// solvers) has written, so an arrow follows the body the player actually sees.
/// </summary>
public sealed unsafe class ArrowImpactController : IDisposable
{
    private readonly record struct BoneShape(float Radius, float Extend);

    /// <summary>Flesh radius and, for end bones, how far the body continues past the bone.</summary>
    private static readonly Dictionary<string, BoneShape> HumanBoneShapes = new(StringComparer.Ordinal)
    {
        ["j_kao"] = new(0.095f, 0.16f), ["j_ago"] = new(0.05f, 0.05f), ["j_kubi"] = new(0.055f, 0f),
        ["j_sebo_c"] = new(0.15f, 0f), ["j_sebo_b"] = new(0.14f, 0f), ["j_sebo_a"] = new(0.14f, 0f), ["j_kosi"] = new(0.15f, 0f),
        ["j_mune_l"] = new(0.07f, 0.04f), ["j_mune_r"] = new(0.07f, 0.04f),
        ["j_sako_l"] = new(0.06f, 0f), ["j_sako_r"] = new(0.06f, 0f),
        ["j_ude_a_l"] = new(0.05f, 0f), ["j_ude_a_r"] = new(0.05f, 0f),
        ["j_ude_b_l"] = new(0.042f, 0f), ["j_ude_b_r"] = new(0.042f, 0f),
        ["j_te_l"] = new(0.035f, 0.12f), ["j_te_r"] = new(0.035f, 0.12f),
        ["j_asi_a_l"] = new(0.085f, 0f), ["j_asi_a_r"] = new(0.085f, 0f),
        ["j_asi_b_l"] = new(0.058f, 0f), ["j_asi_b_r"] = new(0.058f, 0f),
        ["j_asi_c_l"] = new(0.055f, 0f), ["j_asi_c_r"] = new(0.055f, 0f),
        ["j_asi_d_l"] = new(0.045f, 0.12f), ["j_asi_d_r"] = new(0.045f, 0.12f),
        ["j_asi_e_l"] = new(0.03f, 0.05f), ["j_asi_e_r"] = new(0.03f, 0.05f),
    };

    /// <summary>Bones an unpreferred shot may land on, on a human skeleton: the body, not fingers,
    /// face, hair or gear bones.</summary>
    private static readonly string[] HumanRandomBones =
    {
        "j_kao", "j_kubi", "j_sebo_c", "j_sebo_b", "j_sebo_a", "j_kosi",
        "j_sako_l", "j_sako_r", "j_ude_a_l", "j_ude_a_r", "j_ude_b_l", "j_ude_b_r", "j_te_l", "j_te_r",
        "j_asi_a_l", "j_asi_a_r", "j_asi_b_l", "j_asi_b_r", "j_asi_d_l", "j_asi_d_r",
    };

    /// <summary>Earlier builds stored named spots; map them onto the bone they meant.</summary>
    private static readonly Dictionary<string, string> LegacySpotBones = new(StringComparer.Ordinal)
    {
        ["head"] = "j_kao", ["neck"] = "j_kubi", ["chest"] = "j_sebo_c", ["abdomen"] = "j_sebo_b", ["hips"] = "j_kosi",
        ["shoulder_l"] = "j_sako_l", ["shoulder_r"] = "j_sako_r", ["upperarm_l"] = "j_ude_a_l", ["upperarm_r"] = "j_ude_a_r",
        ["forearm_l"] = "j_ude_b_l", ["forearm_r"] = "j_ude_b_r", ["hand_l"] = "j_te_l", ["hand_r"] = "j_te_r",
        ["thigh_l"] = "j_asi_a_l", ["thigh_r"] = "j_asi_a_r", ["shin_l"] = "j_asi_b_l", ["shin_r"] = "j_asi_b_r",
        ["foot_l"] = "j_asi_d_l", ["foot_r"] = "j_asi_d_r",
    };

    private const float PenetrationMin = 0.26f;   // m of shaft buried past the skin: a third of the arrow,
    private const float PenetrationMax = 0.40f;   // a little more for a square hit, a little less for a glancing one
    private const float TailAnchorDistance = 0.30f; // m back from the tip, the second pin point
    private const float FlightSpeed = 45f;        // m/s, for the landing delay when the engine gave none
    private const float ReleaseDelay = 0.6f;      // s from the shot motion starting to the arrow leaving
    private const float PreferredExact = 0.55f;   // chance a preferred bone itself is hit
    private const float PreferredNear = 0.30f;    // chance a neighbour of a preferred bone is hit
    private const float EntryHalfAngle = 0.8f;    // rad either side of the shooter's bearing the entry may sit
    private const float PlainArrowLength = 0.72f;
    private const float PlainShaftRadius = 0.0045f;
    private const int PlainVertexBudget = 8 * 6 * 2 + 3 * 4 * 3;

    private static readonly Vector4 PlainShaftColor = new(0.36f, 0.24f, 0.13f, 1f);
    private static readonly Vector4 PlainFinColor = new(0.86f, 0.83f, 0.76f, 1f);

    private sealed class Arrow
    {
        public nint Target;
        public uint TargetEntityId;
        /// <summary>The struck bone and the bone whose flesh shares the hit (its parent); -1 = none.</summary>
        public int BoneA, BoneB;
        /// <summary>How much of the arrow rides with BoneB (0 = all BoneA).</summary>
        public float WeightB;
        /// <summary>Tip and tail pins in each bone's frame, unscaled.</summary>
        public Vector3 TipA, TipB, TailA, TailB;
        public float Roll;
        public double Expires;
        public ArrowMesh? Mesh;
        public int VertexBudget => Mesh?.VertexBudget ?? PlainVertexBudget;
    }

    private sealed class PendingShot
    {
        public Vector3 ShooterPosition;
        public nint Target;
        public uint TargetEntityId;
        public double Due;
        public ArrowMesh? Mesh;
    }

    private readonly record struct SkeletonFrame(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private readonly record struct Capsule(int Bone, int Parent, Vector3 Start, Vector3 End, Vector3 Joint, float Radius);

    private readonly Configuration config;
    private readonly BoneTransformService bones;
    private readonly IPluginLog log;
    private readonly WorldGeometryLayer layer;
    private readonly WorldGeometryBuilder builder = new(WorldGeometryRenderer.MaxVertices);
    private readonly ArrowMeshLibrary meshes;
    private readonly List<Arrow> arrows = new();
    private readonly List<PendingShot> pending = new();
    private readonly List<ArrowWound> newWounds = new();
    private readonly Dictionary<nint, uint> trackedTargets = new();
    private readonly HashSet<nint> liveTargets = new();
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private bool layerEnabled, hadGeometry, disposed, spotsNormalized;
    private int arrowCount;

    public ArrowImpactController(Configuration config, BoneTransformService bones, WorldGeometryRenderer renderer, IPluginLog log)
    {
        this.config = config;
        this.bones = bones;
        this.log = log;
        meshes = new ArrowMeshLibrary(log);
        layer = renderer.CreateLayer("Arrow impacts");
        bones.OnPosePrepared += OnPosePrepared;
    }

    public bool Enabled => config.ArrowImpactEnabled;
    public int ArrowCount => Volatile.Read(ref arrowCount);
    public string LayerStatus => layer.Status;

    /// <summary>Raised on the render thread, outside this controller's lock, when an arrow has
    /// lodged. A consumer that makes wounds bleed subscribes here; it must only queue.</summary>
    public event Action<ArrowWound>? Wounded;

    /// <summary>True when something is listening for wounds, so bleeding can be offered.</summary>
    public bool BleedAvailable => Wounded != null;

    private double Now => clock.Elapsed.TotalSeconds;

    /// <summary>
    /// A bow attack landed on <paramref name="targetAddr"/>; an arrow sticks after <paramref name="delay"/>
    /// seconds. A negative delay means the caller does not know the contact time, and the shot's own
    /// release plus flight time is used.
    /// </summary>
    public void NotifyShot(nint casterAddr, nint targetAddr, uint targetEntityId, float delay)
    {
        if (!Enabled || disposed || casterAddr == nint.Zero || targetAddr == nint.Zero)
            return;

        // The arrow leaves the bow at chest height, not from the shooter's feet; aiming the shaft at
        // the ground position pointed every arrow up out of the body.
        Vector3 shooterFeet = ((GameObject*)casterAddr)->Position;
        var shooter = bones.GetBoneWorldPos(casterAddr, "j_sebo_c") ?? shooterFeet + Vector3.UnitY * 1.3f;
        Vector3 struck = ((GameObject*)targetAddr)->Position;
        var distance = Vector3.Distance(shooterFeet, struck);
        if (!float.IsFinite(distance))
            return;
        if (delay < 0f)
            delay = ReleaseDelay + distance / FlightSpeed;

        // Resolved here, on the framework thread: the first arrow of a bow reads its model files.
        var mesh = meshes.ForShooter((Character*)casterAddr);

        lock (gate)
        {
            pending.Add(new PendingShot
            {
                ShooterPosition = shooter,
                Target = targetAddr,
                TargetEntityId = targetEntityId,
                Due = Now + delay,
                Mesh = mesh,
            });
        }
    }

    /// <summary>Framework tick: drop arrows whose target left the object table, and gate the layer.</summary>
    public void Tick()
    {
        if (disposed) return;
        NormalizeSpots();

        if (!Enabled)
        {
            lock (gate)
            {
                arrows.Clear();
                pending.Clear();
            }
        }

        lock (gate)
        {
            trackedTargets.Clear();
            foreach (var a in arrows) trackedTargets[a.Target] = a.TargetEntityId;
            foreach (var p in pending) trackedTargets[p.Target] = p.TargetEntityId;
        }

        liveTargets.Clear();
        if (trackedTargets.Count > 0)
        {
            foreach (var obj in Core.Services.ObjectTable)
            {
                if (trackedTargets.TryGetValue(obj.Address, out var id) && obj.EntityId == id)
                    liveTargets.Add(obj.Address);
            }
        }

        bool any;
        lock (gate)
        {
            arrows.RemoveAll(a => !liveTargets.Contains(a.Target));
            pending.RemoveAll(p => !liveTargets.Contains(p.Target));
            any = arrows.Count > 0 || pending.Count > 0;
            Volatile.Write(ref arrowCount, arrows.Count);
        }

        if (any != layerEnabled)
        {
            layer.SetEnabled(any);
            layerEnabled = any;
        }
    }

    /// <summary>Preferred entries are raw bone names; older saves held spot keys.</summary>
    private void NormalizeSpots()
    {
        if (spotsNormalized) return;
        spotsNormalized = true;
        var spots = config.ArrowImpactPreferredSpots;
        var changed = false;
        for (var i = 0; i < spots.Count; i++)
        {
            if (LegacySpotBones.TryGetValue(spots[i], out var bone)) { spots[i] = bone; changed = true; }
        }
        for (var i = spots.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(spots[i]) || spots.IndexOf(spots[i]) != i) { spots.RemoveAt(i); changed = true; }
        }
        if (changed) config.Save();
    }

    public void ClearAll()
    {
        lock (gate)
        {
            arrows.Clear();
            pending.Clear();
        }
    }

    private void OnPosePrepared()
    {
        if (disposed) return;
        try
        {
            lock (gate)
            {
                var now = Now;
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].Due > now) continue;
                    var shot = pending[i];
                    pending.RemoveAt(i);
                    TryPlace(shot, now);
                }

                arrows.RemoveAll(a => a.Expires <= now);
                Volatile.Write(ref arrowCount, arrows.Count);

                if (arrows.Count == 0)
                {
                    if (hadGeometry) layer.Clear();
                    hadGeometry = false;
                }
                else
                {
                    BuildGeometry();
                    layer.SubmitFrame(builder.Vertices, opaque: true);
                    hadGeometry = true;
                }
            }

            if (newWounds.Count > 0)
            {
                var listeners = Wounded;
                if (listeners != null)
                    foreach (var wound in newWounds) listeners(wound);
                newWounds.Clear();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Arrow impacts: pose pass failed.");
        }
    }

    // ---- placement -----------------------------------------------------------------------------

    private void TryPlace(PendingShot shot, double now)
    {
        var skel = bones.TryGetSkeleton(shot.Target);
        if (skel == null) return;
        var access = skel.Value;
        if (!TryReadFrame(access, out var frame)) return;

        if (!TrySegment(access, frame, out var capsule))
            return;

        var rng = Random.Shared;
        var axis = capsule.End - capsule.Start;
        var axisLength = axis.Length();
        if (axisLength < 1e-4f) return;
        axis /= axisLength;

        var alongFraction = 0.15f + 0.7f * rng.NextSingle();
        var along = capsule.Start + axis * (axisLength * alongFraction);

        // Entry: on the side of the limb that faces the shooter, within a wedge about that bearing.
        var toShooter = shot.ShooterPosition - along;
        var side = toShooter - axis * Vector3.Dot(toShooter, axis);
        if (side.LengthSquared() < 1e-6f)
            side = Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        side = Vector3.Normalize(side);
        var angle = (rng.NextSingle() * 2f - 1f) * EntryHalfAngle;
        var normal = Vector3.Normalize(Vector3.Transform(side, Quaternion.CreateFromAxisAngle(axis, angle)));

        var surface = along + normal * capsule.Radius;

        // Shaft: mostly the flight line, bent a little towards the surface normal with slight scatter,
        // so the nock points back at the shooter. Never flatter than ~70° off the normal, or the
        // buried shaft would skim back out of the curve.
        var flight = along - shot.ShooterPosition;
        flight = flight.LengthSquared() > 1e-6f ? Vector3.Normalize(flight) : -normal;
        var direction = Vector3.Normalize(Vector3.Lerp(flight, -normal, 0.15f) + RandomUnit(rng) * 0.06f);
        var into = Vector3.Dot(direction, normal);
        if (into > -0.35f)
            direction = Vector3.Normalize(direction - normal * (into + 0.55f));

        // Deeper the squarer the hit: dot is -1 straight in, -0.35 at the flattest allowed angle.
        var squareness = Math.Clamp((-Vector3.Dot(direction, normal) - 0.35f) / 0.65f, 0f, 1f);
        var penetration = PenetrationMin + (PenetrationMax - PenetrationMin) * (0.4f * squareness + 0.6f * rng.NextSingle());
        var tip = surface + direction * penetration;
        var tail = tip - direction * TailAnchorDistance;

        // Pin to the struck bone and its parent, weighted like skin: flesh at the parent joint rides
        // the parent, flesh at the far joint is shared, flesh past the joint (head, hands) is the bone's.
        var jointFraction = Vector3.Distance(along, capsule.Start) / MathF.Max(1e-4f, Vector3.Distance(capsule.Joint, capsule.Start));
        var weightBone = Math.Clamp(0.5f * jointFraction, 0f, 1f);
        var boneA = capsule.Bone;
        var boneB = capsule.Parent;
        var weightB = boneB >= 0 ? 1f - weightBone : 0f;

        var (posA, rotA) = BoneWorld(access, frame, boneA);
        var invA = Quaternion.Inverse(rotA);
        var tipA = Vector3.Transform(tip - posA, invA) / frame.Scale;
        var tailA = Vector3.Transform(tail - posA, invA) / frame.Scale;
        Vector3 tipB = default, tailB = default;
        if (boneB >= 0)
        {
            var (posB, rotB) = BoneWorld(access, frame, boneB);
            var invB = Quaternion.Inverse(rotB);
            tipB = Vector3.Transform(tip - posB, invB) / frame.Scale;
            tailB = Vector3.Transform(tail - posB, invB) / frame.Scale;
        }
        if (!Finite(tipA) || !Finite(tailA) || !Finite(tipB) || !Finite(tailB)) return;

        // Caps: oldest arrow on this body, then oldest anywhere, then whatever the layer can hold.
        var onTarget = 0;
        Arrow? oldestOnTarget = null;
        foreach (var arrow in arrows)
        {
            if (arrow.Target != shot.Target) continue;
            onTarget++;
            if (oldestOnTarget == null || arrow.Expires < oldestOnTarget.Expires) oldestOnTarget = arrow;
        }
        if (onTarget >= Math.Max(1, config.ArrowImpactMaxPerTarget) && oldestOnTarget != null)
            arrows.Remove(oldestOnTarget);
        var incoming = shot.Mesh?.VertexBudget ?? PlainVertexBudget;
        while (arrows.Count > 0 && TotalVertexBudget() + incoming > WorldGeometryRenderer.MaxVertices)
            arrows.RemoveAt(0);

        arrows.Add(new Arrow
        {
            Target = shot.Target,
            TargetEntityId = shot.TargetEntityId,
            BoneA = boneA, BoneB = boneB, WeightB = weightB,
            TipA = tipA, TipB = tipB, TailA = tailA, TailB = tailB,
            Roll = rng.NextSingle() * MathF.Tau,
            Expires = config.ArrowImpactFallAway ? now + Math.Max(1f, config.ArrowImpactLingerSeconds) : double.MaxValue,
            Mesh = shot.Mesh,
        });

        if (config.ArrowImpactBleed && Wounded != null)
            newWounds.Add(new ArrowWound(shot.Target, shot.TargetEntityId, surface, direction));
    }

    private int TotalVertexBudget()
    {
        var total = 0;
        foreach (var arrow in arrows) total += arrow.VertexBudget;
        return total;
    }

    /// <summary>Pick the capsule (bone-to-parent segment + flesh radius) this arrow lands in.</summary>
    private bool TrySegment(SkeletonAccess access, SkeletonFrame frame, out Capsule capsule)
    {
        capsule = default;

        var human = bones.ResolveBoneIndex(access, "j_sebo_c") >= 0 && bones.ResolveBoneIndex(access, "j_kosi") >= 0;
        var rng = Random.Shared;

        // Preferred bones first: the bone itself, then one of its neighbours, then anywhere.
        var preferred = config.ArrowImpactPreferredSpots;
        if (preferred.Count > 0)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var roll = rng.NextSingle();
                var wanted = bones.ResolveBoneIndex(access, preferred[rng.Next(preferred.Count)]);
                if (wanted < 0) continue;
                if (roll < PreferredExact)
                {
                    if (TryBoneCapsule(access, frame, wanted, out capsule)) return true;
                }
                else if (roll < PreferredExact + PreferredNear)
                {
                    var near = Neighbour(access, wanted, rng);
                    if (near >= 0 && TryBoneCapsule(access, frame, near, out capsule)) return true;
                }
                else break;
            }
        }

        if (human)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var index = bones.ResolveBoneIndex(access, HumanRandomBones[rng.Next(HumanRandomBones.Length)]);
                if (index >= 0 && TryBoneCapsule(access, frame, index, out capsule))
                    return true;
            }
        }

        return TryGenericCapsule(access, frame, out capsule);
    }

    /// <summary>The parent or a child of <paramref name="bone"/>, or -1 when it has neither.</summary>
    private static int Neighbour(SkeletonAccess access, int bone, Random rng)
    {
        var candidates = new List<int>(4);
        var parent = ParentIndex(access, bone);
        if (parent > 0) candidates.Add(parent);
        var count = Math.Min(access.BoneCount, access.ParentCount);
        for (var i = 1; i < count; i++)
            if (access.HavokSkeleton->ParentIndices[i] == bone) candidates.Add(i);
        return candidates.Count == 0 ? -1 : candidates[rng.Next(candidates.Count)];
    }

    /// <summary>
    /// The capsule of a bone: from its parent to the bone, extended past the bone for end bones such
    /// as the head, hands and feet whose flesh continues beyond the joint. Radius from the human table
    /// when the bone is known, otherwise from the segment length.
    /// </summary>
    private bool TryBoneCapsule(SkeletonAccess access, SkeletonFrame frame, int index, out Capsule capsule)
    {
        capsule = default;
        if (index <= 0 || index >= access.BoneCount || index >= access.HavokSkeleton->Bones.Length) return false;
        var name = access.HavokSkeleton->Bones[index].Name.String ?? string.Empty;
        var scale = MathF.Max(0.05f, (frame.Scale.X + frame.Scale.Y + frame.Scale.Z) / 3f);
        var (bonePos, _) = BoneWorld(access, frame, index);

        Vector3 parentPos;
        var parent = ParentIndex(access, index);
        if (parent >= access.BoneCount) return false;
        if (parent > 0) (parentPos, _) = BoneWorld(access, frame, parent);
        else { parent = -1; parentPos = bonePos - Vector3.UnitY * 0.1f * scale; }
        var length = Vector3.Distance(bonePos, parentPos);
        if (length < 0.02f * scale || length > 2f * scale) return false;

        var outward = Vector3.Normalize(bonePos - parentPos);
        float radius;
        Vector3 end;
        if (HumanBoneShapes.TryGetValue(name, out var shape))
        {
            radius = shape.Radius * scale;
            end = bonePos + outward * (shape.Extend * scale);
        }
        else
        {
            radius = Math.Clamp(length * 0.3f, 0.03f * scale, 0.2f * scale);
            end = bonePos;
        }
        if (Vector3.DistanceSquared(parentPos, end) <= 1e-6f) return false;
        capsule = new Capsule(index, parent, parentPos, end, bonePos, radius);
        return true;
    }

    /// <summary>Non-human skeleton: a random body bone with its parent segment as the capsule.</summary>
    private bool TryGenericCapsule(SkeletonAccess access, SkeletonFrame frame, out Capsule capsule)
    {
        capsule = default;
        var havok = access.HavokSkeleton;
        var count = Math.Min(access.BoneCount, havok->Bones.Length);
        var rng = Random.Shared;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var i = rng.Next(count);
            var name = havok->Bones[i].Name.String;
            if (string.IsNullOrEmpty(name) || !name.StartsWith("j_", StringComparison.Ordinal)) continue;
            if (name.StartsWith("j_f_", StringComparison.Ordinal) || name.Contains("buki") || name.Contains("ex_")) continue;
            var parent = ParentIndex(access, i);
            if (parent <= 0 || parent >= access.BoneCount) continue;
            var (bonePos, _) = BoneWorld(access, frame, i);
            var (parentPos, _) = BoneWorld(access, frame, parent);
            var length = Vector3.Distance(bonePos, parentPos);
            if (length < 0.05f || length > 1.5f) continue;
            capsule = new Capsule(i, parent, parentPos, bonePos, bonePos, Math.Clamp(length * 0.3f, 0.04f, 0.35f));
            return true;
        }
        return false;
    }

    // ---- skeleton access -----------------------------------------------------------------------

    private static bool TryReadFrame(SkeletonAccess access, out SkeletonFrame frame)
    {
        frame = default;
        if (access.CharBase == null || access.CharBase->Skeleton == null) return false;
        var t = access.CharBase->Skeleton->Transform;
        var scale = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
        if (!Finite(scale) || MathF.Abs(scale.X) < 1e-4f || MathF.Abs(scale.Y) < 1e-4f || MathF.Abs(scale.Z) < 1e-4f)
            scale = Vector3.One;
        frame = new SkeletonFrame(
            new Vector3(t.Position.X, t.Position.Y, t.Position.Z),
            Quaternion.Normalize(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W)),
            scale);
        return Finite(frame.Position);
    }

    private static (Vector3 Position, Quaternion Rotation) BoneWorld(SkeletonAccess access, SkeletonFrame frame, int index)
    {
        ref var mt = ref access.Pose->ModelPose.Data[index];
        var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z) * frame.Scale;
        var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
        return (frame.Position + Vector3.Transform(modelPos, frame.Rotation), Quaternion.Normalize(frame.Rotation * modelRot));
    }

    private static int ParentIndex(SkeletonAccess access, int index)
    {
        if (index <= 0 || index >= access.ParentCount) return -1;
        return access.HavokSkeleton->ParentIndices[index];
    }

    // ---- drawing -------------------------------------------------------------------------------

    private readonly Dictionary<nint, (SkeletonAccess Access, SkeletonFrame Frame)?> frameCache = new();

    private void BuildGeometry()
    {
        builder.Reset();
        frameCache.Clear();
        foreach (var arrow in arrows)
        {
            if (!frameCache.TryGetValue(arrow.Target, out var cached))
            {
                cached = null;
                var skel = bones.TryGetSkeleton(arrow.Target);
                if (skel != null && TryReadFrame(skel.Value, out var frame))
                    cached = (skel.Value, frame);
                frameCache[arrow.Target] = cached;
            }
            if (cached == null) continue;
            var (access, f) = cached.Value;
            if (arrow.BoneA < 0 || arrow.BoneA >= access.BoneCount || arrow.BoneB >= access.BoneCount) continue;

            var (posA, rotA) = BoneWorld(access, f, arrow.BoneA);
            var tip = posA + Vector3.Transform(arrow.TipA * f.Scale, rotA);
            var tail = posA + Vector3.Transform(arrow.TailA * f.Scale, rotA);
            if (arrow.BoneB >= 0 && arrow.WeightB > 0f)
            {
                var (posB, rotB) = BoneWorld(access, f, arrow.BoneB);
                var tipB = posB + Vector3.Transform(arrow.TipB * f.Scale, rotB);
                var tailB = posB + Vector3.Transform(arrow.TailB * f.Scale, rotB);
                tip = Vector3.Lerp(tip, tipB, arrow.WeightB);
                tail = Vector3.Lerp(tail, tailB, arrow.WeightB);
            }
            var direction = tip - tail;
            if (!Finite(tip) || !Finite(direction) || direction.LengthSquared() < 1e-6f) continue;
            direction = Vector3.Normalize(direction);
            if (arrow.Mesh != null) AddMeshArrow(arrow.Mesh, tip, direction, arrow.Roll);
            else AddPlainArrow(tip, direction, arrow.Roll);
        }
    }

    /// <summary>The bow's own arrow: mesh frame has the point at the origin and the shaft along -Z.</summary>
    private void AddMeshArrow(ArrowMesh mesh, Vector3 tip, Vector3 direction, float roll)
    {
        var u = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(direction, u);
        var cos = MathF.Cos(roll);
        var sin = MathF.Sin(roll);
        var ru = u * cos + v * sin;
        var rv = v * cos - u * sin;
        var indices = mesh.Indices;
        var positions = mesh.Positions;
        var normals = mesh.Normals;
        var colors = mesh.Colors;
        for (var i = 0; i + 2 < indices.Length; i += 3)
            builder.AddTriangle(Vertex(indices[i]), Vertex(indices[i + 1]), Vertex(indices[i + 2]));

        WorldVertex Vertex(int index)
        {
            var p = positions[index];
            var n = normals[index];
            return new WorldVertex(
                tip + ru * p.X + rv * p.Y + direction * p.Z,
                ru * n.X + rv * n.Y + direction * n.Z,
                index < colors.Length ? colors[index] : PlainShaftColor);
        }
    }

    /// <summary>Last resort when no arrow model can be read: a plain shaft with three fins.</summary>
    private void AddPlainArrow(Vector3 tip, Vector3 direction, float finRoll)
    {
        var surface = tip - direction * PenetrationMin;
        var tail = tip - direction * PlainArrowLength;

        builder.AddTube(tail + direction * 0.012f, surface + direction * 0.012f, PlainShaftRadius, PlainShaftColor);
        builder.AddTube(tail - direction * 0.006f, tail + direction * 0.014f, PlainShaftRadius * 1.35f, PlainShaftColor);

        var u = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(direction, u);
        var finStart = tail + direction * 0.02f;
        var finEnd = tail + direction * 0.105f;
        for (var k = 0; k < 3; k++)
        {
            var angle = finRoll + k * (MathF.Tau / 3f);
            var side = u * MathF.Cos(angle) + v * MathF.Sin(angle);
            var p0 = finStart + side * PlainShaftRadius;
            var p1 = finEnd + side * PlainShaftRadius;
            var p1o = finEnd + side * 0.017f;
            var p0o = finStart + side * 0.012f;
            var normal = Vector3.Normalize(Vector3.Cross(side, direction));
            builder.AddTriangle(new WorldVertex(p0, normal, PlainFinColor), new WorldVertex(p1, normal, PlainFinColor), new WorldVertex(p1o, normal, PlainFinColor));
            builder.AddTriangle(new WorldVertex(p0, normal, PlainFinColor), new WorldVertex(p1o, normal, PlainFinColor), new WorldVertex(p0o, normal, PlainFinColor));
            builder.AddTriangle(new WorldVertex(p0, -normal, PlainFinColor), new WorldVertex(p1o, -normal, PlainFinColor), new WorldVertex(p1, -normal, PlainFinColor));
            builder.AddTriangle(new WorldVertex(p0, -normal, PlainFinColor), new WorldVertex(p0o, -normal, PlainFinColor), new WorldVertex(p1o, -normal, PlainFinColor));
        }
    }

    private static Vector3 RandomUnit(Random rng)
    {
        var z = rng.NextSingle() * 2f - 1f;
        var t = rng.NextSingle() * MathF.Tau;
        var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(t), z, r * MathF.Sin(t));
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        bones.OnPosePrepared -= OnPosePrepared;
        lock (gate)
        {
            arrows.Clear();
            pending.Clear();
        }
        layer.Dispose();
    }
}
