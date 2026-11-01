// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using CombatSimulator.Animation;
using CombatSimulator.Rendering.WorldGeometry;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Effects;

/// <summary>
/// Arrows that stay lodged in the body they struck. Every damaging hit from a bow wielder, player or
/// enemy, queues an arrow that lands when the shot would arrive; the arrow is then parented to the
/// struck bone in that bone's own frame, so it rides along with animation, ragdoll and scale, and is
/// drawn each render frame as a small shaded mesh through the world geometry layer.
///
/// Where it lands: the body is approximated by one capsule per spot (a bone-to-bone segment with a
/// flesh radius). With preferred spots configured, an arrow lands on one of them most of the time,
/// in the flesh around one of them some of the time, and anywhere otherwise; with none, anywhere.
/// Arrows favour the side facing the shooter and enter at an angle between the flight line and the
/// surface normal. Non-human skeletons fall back to a random body bone.
/// </summary>
public sealed unsafe class ArrowImpactController : IDisposable
{
    public readonly record struct BodySpot(string Key, string Label, string Bone, string? ToBone, float Radius,
        float HalfLength, string[] Neighbours);

    public static readonly BodySpot[] HumanSpots =
    {
        new("head", "Head", "j_kao", null, 0.095f, 0.09f, new[] { "neck" }),
        new("neck", "Neck", "j_kubi", "j_kao", 0.055f, 0f, new[] { "head", "chest" }),
        new("chest", "Chest", "j_sebo_c", "j_kubi", 0.15f, 0f, new[] { "neck", "abdomen", "shoulder_l", "shoulder_r" }),
        new("abdomen", "Abdomen", "j_sebo_b", "j_sebo_c", 0.14f, 0f, new[] { "chest", "hips" }),
        new("hips", "Hips", "j_kosi", "j_sebo_b", 0.15f, 0f, new[] { "abdomen", "thigh_l", "thigh_r" }),
        new("shoulder_l", "Left shoulder", "j_sako_l", "j_ude_a_l", 0.06f, 0f, new[] { "chest", "upperarm_l" }),
        new("shoulder_r", "Right shoulder", "j_sako_r", "j_ude_a_r", 0.06f, 0f, new[] { "chest", "upperarm_r" }),
        new("upperarm_l", "Left upper arm", "j_ude_a_l", "j_ude_b_l", 0.05f, 0f, new[] { "shoulder_l", "forearm_l" }),
        new("upperarm_r", "Right upper arm", "j_ude_a_r", "j_ude_b_r", 0.05f, 0f, new[] { "shoulder_r", "forearm_r" }),
        new("forearm_l", "Left forearm", "j_ude_b_l", "j_te_l", 0.042f, 0f, new[] { "upperarm_l", "hand_l" }),
        new("forearm_r", "Right forearm", "j_ude_b_r", "j_te_r", 0.042f, 0f, new[] { "upperarm_r", "hand_r" }),
        new("hand_l", "Left hand", "j_te_l", null, 0.035f, 0.06f, new[] { "forearm_l" }),
        new("hand_r", "Right hand", "j_te_r", null, 0.035f, 0.06f, new[] { "forearm_r" }),
        new("thigh_l", "Left thigh", "j_asi_a_l", "j_asi_b_l", 0.085f, 0f, new[] { "hips", "shin_l" }),
        new("thigh_r", "Right thigh", "j_asi_a_r", "j_asi_b_r", 0.085f, 0f, new[] { "hips", "shin_r" }),
        new("shin_l", "Left shin", "j_asi_b_l", "j_asi_d_l", 0.058f, 0f, new[] { "thigh_l", "foot_l" }),
        new("shin_r", "Right shin", "j_asi_b_r", "j_asi_d_r", 0.058f, 0f, new[] { "thigh_r", "foot_r" }),
        new("foot_l", "Left foot", "j_asi_d_l", "j_asi_e_l", 0.045f, 0f, new[] { "shin_l" }),
        new("foot_r", "Right foot", "j_asi_d_r", "j_asi_e_r", 0.045f, 0f, new[] { "shin_r" }),
    };

    public static BodySpot? FindSpot(string key)
    {
        foreach (var spot in HumanSpots)
            if (spot.Key == key) return spot;
        return null;
    }

    private const float ArrowLength = 0.72f;      // m, nock to point
    private const float Penetration = 0.07f;      // m of shaft buried past the skin
    private const float ShaftRadius = 0.0045f;
    private const float FlightSpeed = 45f;        // m/s, for the landing delay
    private const float ReleaseDelay = 0.4f;      // s from the shot motion starting to the arrow leaving
    private const int MaxArrows = 240;            // keeps the mesh inside one layer
    private const float PreferredExact = 0.55f;   // chance a preferred spot itself is hit
    private const float PreferredNear = 0.30f;    // chance a neighbour of a preferred spot is hit
    private const float ShooterSideBias = 0.75f;  // chance the entry is on the half facing the shooter

    private static readonly Vector4 ShaftColor = new(0.36f, 0.24f, 0.13f, 1f);
    private static readonly Vector4 NockColor = new(0.12f, 0.10f, 0.09f, 1f);
    private static readonly Vector4 FinColor = new(0.86f, 0.83f, 0.76f, 1f);
    private static readonly Vector4 FinAccentColor = new(0.62f, 0.13f, 0.10f, 1f);

    private sealed class Arrow
    {
        public nint Target;
        public uint TargetEntityId;
        public int BoneIndex;
        public Vector3 LocalTip, LocalDirection;
        public float FinRoll;
        public double Expires;
    }

    private sealed class PendingShot
    {
        public Vector3 ShooterPosition;
        public nint Target;
        public uint TargetEntityId;
        public double Due;
    }

    private readonly record struct SkeletonFrame(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private readonly Configuration config;
    private readonly BoneTransformService bones;
    private readonly IPluginLog log;
    private readonly WorldGeometryLayer layer;
    private readonly WorldGeometryBuilder builder = new(WorldGeometryRenderer.MaxVertices);
    private readonly List<Arrow> arrows = new();
    private readonly List<PendingShot> pending = new();
    private readonly Dictionary<nint, uint> trackedTargets = new();
    private readonly HashSet<nint> liveTargets = new();
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private bool layerEnabled, hadGeometry, disposed;
    private int arrowCount;

    public ArrowImpactController(Configuration config, BoneTransformService bones, WorldGeometryRenderer renderer, IPluginLog log)
    {
        this.config = config;
        this.bones = bones;
        this.log = log;
        layer = renderer.CreateLayer("Arrow impacts");
        bones.OnRenderFrame += OnRenderFrame;
    }

    public bool Enabled => config.ArrowImpactEnabled;
    public int ArrowCount => Volatile.Read(ref arrowCount);
    public string LayerStatus => layer.Status;

    private double Now => clock.Elapsed.TotalSeconds;

    /// <summary>A bow attack landed on <paramref name="targetAddr"/>; an arrow sticks once it would arrive.</summary>
    public void NotifyShot(nint casterAddr, uint casterEntityId, nint targetAddr, uint targetEntityId, float feedbackDelay)
    {
        if (!Enabled || disposed || casterAddr == nint.Zero || targetAddr == nint.Zero)
            return;

        var shooter = ((GameObject*)casterAddr)->Position;
        var struck = ((GameObject*)targetAddr)->Position;
        var distance = Vector3.Distance(shooter, struck);
        if (!float.IsFinite(distance))
            return;
        var delay = MathF.Max(feedbackDelay, ReleaseDelay + distance / FlightSpeed);

        lock (gate)
        {
            pending.Add(new PendingShot
            {
                ShooterPosition = shooter,
                Target = targetAddr,
                TargetEntityId = targetEntityId,
                Due = Now + delay,
            });
        }
    }

    /// <summary>Framework tick: drop arrows whose target left the object table, and gate the layer.</summary>
    public void Tick()
    {
        if (disposed) return;

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

    public void ClearAll()
    {
        lock (gate)
        {
            arrows.Clear();
            pending.Clear();
        }
    }

    private void OnRenderFrame()
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
                    return;
                }

                BuildGeometry();
                layer.SubmitFrame(builder.Vertices);
                hadGeometry = true;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Arrow impacts: render frame failed.");
        }
    }

    // ---- placement -----------------------------------------------------------------------------

    private void TryPlace(PendingShot shot, double now)
    {
        var skel = bones.TryGetSkeleton(shot.Target);
        if (skel == null) return;
        var access = skel.Value;
        if (!TryReadFrame(access, out var frame)) return;

        if (!TrySegment(access, frame, shot, out var boneIndex, out var a, out var b, out var radius))
            return;

        var rng = Random.Shared;
        var axis = b - a;
        var axisLength = axis.Length();
        if (axisLength < 1e-4f) return;
        axis /= axisLength;

        var along = a + axis * (axisLength * (0.15f + 0.7f * rng.NextSingle()));

        // Radial entry direction: perpendicular to the segment, usually on the shooter's side.
        var toShooter = shot.ShooterPosition - along;
        var side = toShooter - axis * Vector3.Dot(toShooter, axis);
        if (side.LengthSquared() < 1e-6f)
            side = Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        side = Vector3.Normalize(side);
        var angle = rng.NextSingle() < ShooterSideBias
            ? (rng.NextSingle() * 2f - 1f) * 1.2f
            : (rng.NextSingle() * 2f - 1f) * MathF.PI;
        var normal = Vector3.Normalize(Vector3.Transform(side, Quaternion.CreateFromAxisAngle(axis, angle)));

        var surface = along + normal * radius;

        // Shaft direction: between the flight line and straight into the surface, with a little scatter,
        // and always pointing into the body.
        var flight = along - shot.ShooterPosition;
        flight = flight.LengthSquared() > 1e-6f ? Vector3.Normalize(flight) : -normal;
        var direction = Vector3.Normalize(Vector3.Lerp(flight, -normal, 0.45f) + RandomUnit(rng) * 0.12f);
        // Never flatter than 60° off the normal, or the buried shaft would skim back out of the curve.
        var into = Vector3.Dot(direction, normal);
        if (into > -0.5f)
            direction = Vector3.Normalize(direction - normal * (into + 0.7f));

        var tip = surface + direction * Penetration;

        var (bonePos, boneRot) = BoneWorld(access, frame, boneIndex);
        var inverse = Quaternion.Inverse(boneRot);
        var localTip = Vector3.Transform(tip - bonePos, inverse) / frame.Scale;
        var localDir = Vector3.Transform(direction, inverse);
        if (!Finite(localTip) || !Finite(localDir)) return;

        // Caps: oldest arrow on this body, then oldest anywhere.
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
        if (arrows.Count >= MaxArrows)
            arrows.RemoveAt(0);

        arrows.Add(new Arrow
        {
            Target = shot.Target,
            TargetEntityId = shot.TargetEntityId,
            BoneIndex = boneIndex,
            LocalTip = localTip,
            LocalDirection = localDir,
            FinRoll = rng.NextSingle() * MathF.Tau,
            Expires = now + Math.Max(1f, config.ArrowImpactLingerSeconds),
        });
    }

    /// <summary>Pick the capsule (bone segment + flesh radius) this arrow lands in.</summary>
    private bool TrySegment(SkeletonAccess access, SkeletonFrame frame, PendingShot shot,
        out int boneIndex, out Vector3 a, out Vector3 b, out float radius)
    {
        boneIndex = -1; a = b = default; radius = 0f;

        var human = bones.ResolveBoneIndex(access, "j_sebo_c") >= 0 && bones.ResolveBoneIndex(access, "j_kosi") >= 0;
        if (human)
        {
            // A few attempts, since a modded skeleton may lack one of the spot bones.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var spot = ChooseSpot(Random.Shared);
                if (TrySpotSegment(access, frame, spot, out boneIndex, out a, out b, out radius))
                    return true;
            }
        }

        return TryGenericSegment(access, frame, out boneIndex, out a, out b, out radius);
    }

    private BodySpot ChooseSpot(Random rng)
    {
        var preferred = config.ArrowImpactPreferredSpots;
        if (preferred.Count > 0)
        {
            var roll = rng.NextSingle();
            var pick = FindSpot(preferred[rng.Next(preferred.Count)]);
            if (pick is { } exact)
            {
                if (roll < PreferredExact)
                    return exact;
                if (roll < PreferredExact + PreferredNear && exact.Neighbours.Length > 0)
                {
                    var near = FindSpot(exact.Neighbours[rng.Next(exact.Neighbours.Length)]);
                    if (near is { } neighbour) return neighbour;
                }
            }
        }

        return HumanSpots[rng.Next(HumanSpots.Length)];
    }

    private bool TrySpotSegment(SkeletonAccess access, SkeletonFrame frame, BodySpot spot,
        out int boneIndex, out Vector3 a, out Vector3 b, out float radius)
    {
        a = b = default; radius = 0f;
        boneIndex = bones.ResolveBoneIndex(access, spot.Bone);
        if (boneIndex < 0 || boneIndex >= access.BoneCount) return false;
        var scale = MathF.Max(0.05f, (frame.Scale.X + frame.Scale.Y + frame.Scale.Z) / 3f);
        radius = spot.Radius * scale;
        (a, _) = BoneWorld(access, frame, boneIndex);

        if (spot.ToBone != null)
        {
            var toIndex = bones.ResolveBoneIndex(access, spot.ToBone);
            if (toIndex < 0 || toIndex >= access.BoneCount) return false;
            (b, _) = BoneWorld(access, frame, toIndex);
            return Vector3.DistanceSquared(a, b) > 1e-6f;
        }

        // Leaf spot (head, hand): continue outward from the parent through this bone.
        var parent = ParentIndex(access, boneIndex);
        Vector3 outward;
        if (parent >= 0)
        {
            var (parentPos, _) = BoneWorld(access, frame, parent);
            outward = a - parentPos;
        }
        else outward = Vector3.UnitY;
        if (outward.LengthSquared() < 1e-6f) outward = Vector3.UnitY;
        outward = Vector3.Normalize(outward);
        b = a + outward * (2f * spot.HalfLength * scale);
        return true;
    }

    /// <summary>Non-human skeleton: a random body bone with its parent segment as the capsule.</summary>
    private bool TryGenericSegment(SkeletonAccess access, SkeletonFrame frame,
        out int boneIndex, out Vector3 a, out Vector3 b, out float radius)
    {
        boneIndex = -1; a = b = default; radius = 0f;
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
            if (parent <= 0) continue;
            var (pa, _) = BoneWorld(access, frame, i);
            var (pb, _) = BoneWorld(access, frame, parent);
            var length = Vector3.Distance(pa, pb);
            if (length < 0.05f || length > 1.5f) continue;
            boneIndex = i; a = pa; b = pb;
            radius = Math.Clamp(length * 0.3f, 0.04f, 0.35f);
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
            if (arrow.BoneIndex < 0 || arrow.BoneIndex >= access.BoneCount) continue;

            var (bonePos, boneRot) = BoneWorld(access, f, arrow.BoneIndex);
            var tip = bonePos + Vector3.Transform(arrow.LocalTip * f.Scale, boneRot);
            var direction = Vector3.Transform(arrow.LocalDirection, boneRot);
            if (!Finite(tip) || !Finite(direction) || direction.LengthSquared() < 0.5f) continue;
            AddArrow(tip, Vector3.Normalize(direction), arrow.FinRoll);
        }
    }

    private void AddArrow(Vector3 tip, Vector3 direction, float finRoll)
    {
        var surface = tip - direction * Penetration;
        var tail = tip - direction * ArrowLength;

        builder.AddTube(tail + direction * 0.012f, surface + direction * 0.012f, ShaftRadius, ShaftColor);
        builder.AddTube(tail - direction * 0.006f, tail + direction * 0.014f, ShaftRadius * 1.35f, NockColor);

        var u = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(direction, u);
        var finStart = tail + direction * 0.02f;
        var finEnd = tail + direction * 0.105f;
        for (var k = 0; k < 3; k++)
        {
            var angle = finRoll + k * (MathF.Tau / 3f);
            var side = u * MathF.Cos(angle) + v * MathF.Sin(angle);
            var color = k == 0 ? FinAccentColor : FinColor;
            var p0 = finStart + side * ShaftRadius;
            var p1 = finEnd + side * ShaftRadius;
            var p1o = finEnd + side * 0.017f;
            var p0o = finStart + side * 0.012f;
            var normal = Vector3.Normalize(Vector3.Cross(side, direction));
            Fin(p0, p1, p1o, p0o, normal, color);
        }
    }

    private void Fin(Vector3 p0, Vector3 p1, Vector3 p1o, Vector3 p0o, Vector3 normal, Vector4 color)
    {
        builder.AddTriangle(new WorldVertex(p0, normal, color), new WorldVertex(p1, normal, color), new WorldVertex(p1o, normal, color));
        builder.AddTriangle(new WorldVertex(p0, normal, color), new WorldVertex(p1o, normal, color), new WorldVertex(p0o, normal, color));
        var back = -normal;
        builder.AddTriangle(new WorldVertex(p0, back, color), new WorldVertex(p1o, back, color), new WorldVertex(p1, back, color));
        builder.AddTriangle(new WorldVertex(p0, back, color), new WorldVertex(p0o, back, color), new WorldVertex(p1o, back, color));
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
        bones.OnRenderFrame -= OnRenderFrame;
        lock (gate)
        {
            arrows.Clear();
            pending.Clear();
        }
        layer.Dispose();
    }
}
