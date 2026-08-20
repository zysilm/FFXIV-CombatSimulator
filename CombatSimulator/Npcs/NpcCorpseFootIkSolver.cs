using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Npcs;

/// <summary>
/// Bends a single leg onto a corpse surface instead of raising the whole actor's root for it.
///
/// The root-height correction (see NpcAiController.CorrectMovingRootHeight) used to sample a single
/// point at the actor's centre and, the moment a corpse showed up there, lift the entire body to the
/// corpse's surface height. That reads fine when both feet are genuinely standing on a pile, but a
/// foot merely brushing a corpse's edge while the other stays on real ground lifted the whole actor —
/// both feet floating above the true ground by the corpse's thickness.
///
/// This solver handles the single-foot case instead: each tracked actor gets an analytic two-bone IK
/// pass per leg (hip → knee → ankle) run after the game's own animation, so only the foot that is
/// actually over a corpse bends up to meet it; the planted foot is left exactly as animated. The
/// root-height correction still owns the "both feet on the pile" case (see the paired stance-offset
/// check there) — this class never touches root position, only leg bones.
///
/// Feature-detected per actor: a skeleton missing any of the six leg bones (non-humanoid monsters,
/// demihumans, etc.) is left untouched and simply falls back to whatever the root correction does.
/// </summary>
public sealed unsafe class NpcCorpseFootIkSolver : IDisposable
{
    /// <summary>How far above the animated ankle a corpse surface has to sit before the leg bends for
    /// it — filters out surface noise so a foot resting almost flush with a corpse doesn't twitch.</summary>
    private const float EngageThreshold = 0.03f;

    /// <summary>Base ceiling on how far a leg will reach up for a corpse surface, scaled by the actor's
    /// visual size. Matches the ceiling the root correction already used for the same reason: crawling
    /// over a corpse is an opt-in movement policy, not a literal standing leg-length test.</summary>
    private const float MaxStepHeightBase = 0.65f;

    /// <summary>Radius around the animated ankle position to search for a foothold, rather than only
    /// ever querying the exact point the walk cycle happened to place the foot at. A real step lands
    /// somewhere deliberately chosen nearby — usually forward and up — not straight above where the
    /// foot already was, which is also what let the reach look like a pure knee-straighten with almost
    /// no hip involvement: holding the target's X/Z fixed leaves little for the hip to actually do.</summary>
    private const float FootholdSearchRadius = 0.18f;
    private const int FootholdSearchSamples = 8;

    /// <summary>Guard against a degenerate solve swinging the hip into an unnatural pose (e.g. a target
    /// almost behind the actor). Above this the leg is left animated rather than forced.</summary>
    private const float MaxHipSwingRadians = 1.22f; // ~70 degrees

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
        // Last foothold (world position) this leg was reaching for. Kept so a blend-out (foot walking
        // off the corpse) still eases the leg back down even on the frame the search stops finding
        // anything, instead of snapping straight the instant it does.
        public Vector3 LastTargetPos;
        public bool HasTarget;
    }

    private sealed class ActorState
    {
        public int ArmedBoneCount = -1;
        public bool Supported;
        public readonly LegBones Left = new();
        public readonly LegBones Right = new();
        public readonly LegBlend BlendL = new();
        public readonly LegBlend BlendR = new();
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

    /// <summary>True once this actor's leg bones have been resolved and this solver is actively
    /// covering its foot placement — callers that also raise root height for a corpse (e.g.
    /// EnemyControl's directly-driven creatures) should skip that while this is true, so the two
    /// mechanisms don't both react to the same contact. False (including "not tracked yet") means
    /// the caller's own root-height handling is the only thing covering this actor.</summary>
    public bool IsSupported(nint actorAddress) =>
        actors.TryGetValue(actorAddress, out var state) && state.Supported;

    private void OnRenderFrame()
    {
        if (actors.Count == 0 || CorpseSupportHeightProvider == null) return;

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

        if (!state.Supported) return;

        if (!TryGetSkeletonTransform(skel, out var skelPos, out var skelRot)) return;

        var maxStepHeight = MaxStepHeightBase * MathF.Max(1f, GetVisualScale(address));

        SolveLeg(address, skel, skelPos, skelRot, state.Left, state.BlendL, maxStepHeight, state.LastDeltaTime);
        SolveLeg(address, skel, skelPos, skelRot, state.Right, state.BlendR, maxStepHeight, state.LastDeltaTime);
    }

    /// <summary>
    /// One leg's IK pass: engage weight moves toward 1 while the corpse surface under this foot's own
    /// (animated) position is meaningfully above it, and back toward 0 otherwise. At weight &gt; 0 the
    /// hip is aimed at the target and the knee bent to close the remaining distance, both scaled by
    /// weight so the leg eases in and out instead of snapping. The last target height is kept so a
    /// blend-out still eases down even on the frame the corpse surface stops being sampled at all
    /// (foot walked past the edge) rather than vanishing the instant it does.
    /// </summary>
    private void SolveLeg(
        nint address, SkeletonAccess skel, Vector3 skelPos, Quaternion skelRot,
        LegBones bones, LegBlend blend, float maxStepHeight, float deltaTime)
    {
        var hipPos = BoneWorldPosition(skel, skelPos, skelRot, bones.Hip);
        var kneePos = BoneWorldPosition(skel, skelPos, skelRot, bones.Knee);
        var (anklePos, ankleRotOriginal) = BoneWorldTransform(skel, skelPos, skelRot, bones.Ankle);

        var foothold = FindBestFoothold(address, anklePos, maxStepHeight);
        var wantsEngage = foothold.HasValue;

        if (wantsEngage && blend.Weight < 0.01f)
        {
            log.Info($"NpcCorpseFootIk: 0x{address:X} leg engaging — ankle={anklePos:F3} " +
                     $"foothold={foothold!.Value:F3} maxStepHeight={maxStepHeight:F3}");
        }

        if (wantsEngage)
        {
            blend.LastTargetPos = foothold!.Value;
            blend.HasTarget = true;
        }

        var blendSeconds = wantsEngage ? BlendInSeconds : BlendOutSeconds;
        var target = wantsEngage ? 1f : 0f;
        blend.Weight = MoveTowards(blend.Weight, target, deltaTime / MathF.Max(0.01f, blendSeconds));

        if (blend.Weight <= 0.001f || !blend.HasTarget) return;
        if (blend.Weight <= 0f) blend.HasTarget = false;

        var thighLen = Vector3.Distance(hipPos, kneePos);
        var shinLen = Vector3.Distance(kneePos, anklePos);
        if (thighLen < 1e-4f || shinLen < 1e-4f) return;

        var targetPos = blend.LastTargetPos;
        var reachVec = targetPos - hipPos;
        var reachLenRaw = reachVec.Length();
        if (reachLenRaw < 1e-4f) return;

        var maxReach = thighLen + shinLen - 0.001f;
        var minReach = MathF.Abs(thighLen - shinLen) + 0.001f;
        var reachLen = Math.Clamp(reachLenRaw, minReach, maxReach);
        var toTargetDir = reachVec / reachLenRaw;

        var thighDir0 = (kneePos - hipPos) / thighLen;
        var shinDir0 = (anklePos - kneePos) / shinLen;

        var aimSwing = RotationBetween(thighDir0, toTargetDir);
        if (SwingAngle(aimSwing) > MaxHipSwingRadians) return;
        var blendedAim = Quaternion.Slerp(Quaternion.Identity, aimSwing, blend.Weight);

        var (_, hipRotNow) = BoneWorldTransform(skel, skelPos, skelRot, bones.Hip);
        deltas.Clear();
        deltas[bones.Hip] = Quaternion.Normalize(Quaternion.Inverse(hipRotNow) * blendedAim * hipRotNow);
        boneService.ApplyRotationDeltas(skel, deltas);

        // Desired knee interior angle for the (clamped) reach distance, via the law of cosines.
        var cosKnee = Math.Clamp(
            (thighLen * thighLen + shinLen * shinLen - reachLen * reachLen) / (2f * thighLen * shinLen),
            -1f, 1f);
        var desiredInterior = MathF.Acos(cosKnee);
        var foldAngle = MathF.PI - desiredInterior;

        var bendNormal = Vector3.Cross(thighDir0, shinDir0);
        if (bendNormal.LengthSquared() < 1e-8f) bendNormal = Vector3.Cross(thighDir0, toTargetDir);
        if (bendNormal.LengthSquared() < 1e-8f) bendNormal = AnyPerpendicular(thighDir0);
        bendNormal = Vector3.Normalize(bendNormal);

        var currentInterior = MathF.Acos(Math.Clamp(Vector3.Dot(-thighDir0, shinDir0), -1f, 1f));
        var check = Vector3.Transform(thighDir0, Quaternion.CreateFromAxisAngle(bendNormal, MathF.PI - currentInterior));
        if (Vector3.Dot(check, shinDir0) < 0.9f) bendNormal = -bendNormal;

        var aimedNormal = Vector3.Transform(bendNormal, blendedAim);
        var shinDirAfterAim = Vector3.Transform(shinDir0, blendedAim);
        var newShinDir = Vector3.Transform(toTargetDir, Quaternion.CreateFromAxisAngle(aimedNormal, foldAngle));

        var bendSwing = RotationBetween(shinDirAfterAim, newShinDir);
        var blendedBend = Quaternion.Slerp(Quaternion.Identity, bendSwing, blend.Weight);

        var (_, kneeRotNow) = BoneWorldTransform(skel, skelPos, skelRot, bones.Knee);
        deltas.Clear();
        deltas[bones.Knee] = Quaternion.Normalize(Quaternion.Inverse(kneeRotNow) * blendedBend * kneeRotNow);
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
    }

    /// <summary>
    /// Look for the highest qualifying corpse surface within a step's reach of the animated ankle,
    /// rather than only ever checking the exact point the walk cycle happened to place the foot at.
    /// The centre point is included as a candidate so a foot already squarely on a flat corpse surface
    /// doesn't get needlessly nudged sideways by a marginally higher ring sample.
    /// </summary>
    private Vector3? FindBestFoothold(nint address, Vector3 anklePos, float maxStepHeight)
    {
        Vector3? best = null;
        var bestY = float.MinValue;

        void Consider(Vector3 probeXZOffset)
        {
            var probe = anklePos + probeXZOffset;
            var y = CorpseSupportHeightProvider!(address, probe);
            if (!y.HasValue) return;
            if (y.Value <= anklePos.Y + EngageThreshold) return;
            if (y.Value > anklePos.Y + maxStepHeight) return;
            if (y.Value <= bestY) return;

            bestY = y.Value;
            best = new Vector3(probe.X, y.Value, probe.Z);
        }

        Consider(Vector3.Zero);
        for (var i = 0; i < FootholdSearchSamples; i++)
        {
            var angle = i * (MathF.Tau / FootholdSearchSamples);
            Consider(new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * FootholdSearchRadius);
        }

        return best;
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
