using System;
using System.Numerics;

namespace CombatSimulator.Animation;

// Soft-tissue squash & stretch (EXPERIMENTAL, config.RagdollSquashStretch).
//
// The SoftBody jiggle bodies move the flesh, but bone rotation/translation alone cannot show
// COMPRESSION — flesh flattening under a hard landing or under the corpse's own weight at rest.
// This pass fakes that through the one channel skinning still gives us: per-bone scale in the
// hkaPose model pose. (The plugin has never written Scale before; Customize+ demonstrates the
// game's skinning consumes runtime pose scale, including non-uniform.)
//
// Model: each SoftBody rig bone carries a scalar compression c driven as a damped spring
// (underdamped, so a hit overshoots and rebounds — the springy "duang"). Two inputs feed it:
//   - impact: a per-frame jump in the body's linear velocity (Δv) kicks c proportionally,
//   - rest weight: a slow-moving bone whose outward axis points DOWN (the corpse is lying on
//     that flesh) eases toward a sustained partial compression — the lying-flat look.
// c maps to scale along the capsule's outward axis (compressed) and the two perpendicular
// axes (inflated, roughly volume-preserving). Scale can only act along the bone's LOCAL axes
// (hkQsTransform has no shear), so the compression happens on whichever local axis lies
// closest to the outward direction — constant per bone, so nothing pops frame to frame.
//
// The base scale is snapshotted before our first write and multiplied, never overwritten:
// racial / Customize+ scaling survives. Deactivate restores every touched bone exactly.
public unsafe partial class RagdollController
{
    private struct SquashState
    {
        public float Compression;     // c, 0 = relaxed, 1 = max
        public float CompressionVel;
        public Vector3 PrevVel;
        public bool HasPrevVel;
        public Vector3 BaseScale;     // animation pose scale captured before our first write
        public bool TouchedScale;
        public int CompressAxis;      // 0/1/2 = bone-local X/Y/Z, -1 = not yet resolved
        public Vector3 OutwardLocal;  // capsule outward (+Y) axis in bone-local space
    }

    private SquashState[]? squashStates;

    private const float SquashImpactDeltaV = 1.5f;   // m/s of per-frame Δv before a kick registers
    // Kick goes STRAIGHT into the compression value, not its velocity: a velocity impulse into
    // a 9 Hz spring peaks at only v0/ω of displacement (~0.2% for any realistic hit — the
    // "no visible effect at all" bug). A direct displacement bump then relaxes through the
    // underdamped spring, which still yields the rebound overshoot on the way back.
    private const float SquashImpactKick = 0.15f;    // compression per m/s of excess Δv
    private const float SquashRestSpeed = 0.15f;     // below this body speed the rest weight engages
    // Rest flatten keys on |vertical| of the outward axis, not on pointing DOWN: a face-up
    // corpse's chest flesh flattens under its own weight exactly like flesh pressed into the
    // ground — and face-up is the most common death pose, which the down-only test never hit.
    private const float SquashRestMinVertical = 0.2f;
    private const float SquashRestCompression = 0.7f;
    private const float SquashSpringFreq = 9f;       // Hz
    private const float SquashSpringZeta = 0.45f;    // underdamped → one visible rebound
    private const float SquashMaxAlong = 0.4f;       // max axis compression at c=1, intensity=1
    private const float SquashMaxPerp = 0.18f;       // max perpendicular inflation

    /// <summary>
    /// Per-frame squash update. Runs after every positional write of the frame (Pass 2 +
    /// propagation) so the scale annotation is never clobbered downstream. Cheap no-op when
    /// the feature is off and nothing needs restoring.
    /// </summary>
    private void UpdateSquashAndStretch(SkeletonAccess skel, float dt)
    {
        if (!config.RagdollSquashStretch)
        {
            // Feature switched off mid-ragdoll: put every touched bone back once.
            if (squashStates != null) { RestoreSquashScales(skel); squashStates = null; }
            return;
        }
        if (simulation == null || ragdollBones.Count == 0 || dt <= 0f) return;

        if (squashStates == null || squashStates.Length < ragdollBones.Count)
        {
            squashStates = new SquashState[ragdollBones.Count];
            // -1 = "axis not yet resolved". A default-zero struct array reads as axis X,
            // which silently skipped the outward-axis resolution forever.
            for (int s = 0; s < squashStates.Length; s++)
                squashStates[s].CompressAxis = -1;
        }

        var intensity = Math.Clamp(config.RagdollSquashIntensity, 0f, 1f);
        var pose = skel.Pose;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.BoneIndex < 0 || rb.BoneIndex >= skel.BoneCount) continue;
            if (!activeDefByName.TryGetValue(rb.Name, out var def) || !def.SoftBody) continue;

            ref var state = ref squashStates[i];
            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var vel = bodyRef.Velocity.Linear;

            // Impact: per-frame Δv beyond threshold bumps the compression directly (see
            // SquashImpactKick — a velocity impulse would be invisibly small).
            if (state.HasPrevVel)
            {
                var dv = (vel - state.PrevVel).Length();
                if (dv > SquashImpactDeltaV)
                    state.Compression = Math.Min(1f,
                        state.Compression + (dv - SquashImpactDeltaV) * SquashImpactKick);
            }
            state.PrevVel = vel;
            state.HasPrevVel = true;

            // Outward axis: capsule +Y (points from the anchor out through the flesh).
            if (state.CompressAxis < 0)
            {
                state.OutwardLocal = Vector3.Normalize(
                    Vector3.Transform(Vector3.UnitY, Quaternion.Inverse(rb.CapsuleToBoneOffset)));
                var a = Vector3.Abs(state.OutwardLocal);
                state.CompressAxis = a.X >= a.Y ? (a.X >= a.Z ? 0 : 2) : (a.Y >= a.Z ? 1 : 2);
            }

            // Rest weight: a slow bone flattens along the vertical component of its outward
            // axis — pressed into the ground when facing down, sagging flat under its own
            // weight when facing up.
            var target = 0f;
            if (vel.Length() < SquashRestSpeed)
            {
                var outwardWorld = Vector3.Transform(Vector3.UnitY, bodyRef.Pose.Orientation);
                var vertical = MathF.Abs(outwardWorld.Y);
                if (vertical > SquashRestMinVertical)
                    target = SquashRestCompression * vertical;
            }

            // Damped spring toward target, sub-stepped at a fixed h. Semi-implicit Euler is
            // only stable for ω·h < 2 (h < 35ms at 9 Hz), and render dt reaches 0.1s during
            // the death-frame hitches (clone spawns, victory cam) — the un-substepped version
            // diverged, slammed the clamped compression between 0 and 1, overflowed the
            // velocity into NaN, and wrote NaN scale = the "squash-on mesh vanishes" bug.
            var omega = 2f * MathF.PI * SquashSpringFreq;
            var steps = Math.Max(1, (int)MathF.Ceiling(dt / 0.008f));
            var h = dt / steps;
            for (int step = 0; step < steps; step++)
            {
                state.CompressionVel += (-omega * omega * (state.Compression - target)
                                         - 2f * SquashSpringZeta * omega * state.CompressionVel) * h;
                state.CompressionVel = Math.Clamp(state.CompressionVel, -20f, 20f);
                state.Compression = Math.Clamp(state.Compression + state.CompressionVel * h, 0f, 1f);
            }
            // Belt-and-braces: never let a poisoned state reach the scale write.
            if (float.IsNaN(state.Compression + state.CompressionVel))
            {
                state.Compression = 0f;
                state.CompressionVel = 0f;
            }

            var k = state.Compression * intensity;
            if (k < 0.005f && !state.TouchedScale)
                continue; // never visibly compressed — leave the bone's scale untouched

            ref var mt = ref pose->ModelPose.Data[rb.BoneIndex];
            if (!state.TouchedScale)
            {
                state.BaseScale = new Vector3(mt.Scale.X, mt.Scale.Y, mt.Scale.Z);
                // A degenerate captured scale (another system zeroed/NaN'd it) would make every
                // write of ours invisible or wrong forever — snap to 1 rather than trust it.
                if (!(state.BaseScale.X > 0.01f) || !(state.BaseScale.Y > 0.01f) || !(state.BaseScale.Z > 0.01f))
                    state.BaseScale = Vector3.One;
                state.TouchedScale = true;
            }

            var along = Math.Clamp(1f - SquashMaxAlong * k, 0.6f, 1f);
            var perp = 1f + SquashMaxPerp * k;
            var s = new Vector3(perp, perp, perp);
            switch (state.CompressAxis)
            {
                case 0: s.X = along; break;
                case 1: s.Y = along; break;
                default: s.Z = along; break;
            }
            mt.Scale.X = state.BaseScale.X * s.X;
            mt.Scale.Y = state.BaseScale.Y * s.Y;
            mt.Scale.Z = state.BaseScale.Z * s.Z;

            // Field diagnostic: proves whether the scale write is actually happening while the
            // user reports "no visible change" — separates a tuning problem from the game
            // ignoring model-pose scale entirely.
            if (config.RagdollVerboseLog && k > 0.02f && frameCount % 30 == 0)
                log.Info($"[SoftTissue F{frameCount}] '{rb.Name}' c={state.Compression:F3} k={k:F3} " +
                         $"axis={state.CompressAxis} scale=({mt.Scale.X:F3},{mt.Scale.Y:F3},{mt.Scale.Z:F3})");
        }
    }

    /// <summary>
    /// Write every touched bone's captured base scale back. The skeleton is frozen during
    /// ragdoll, so the game will NOT refresh scale for us until animation resumes — without
    /// this a revived character keeps squashed flesh.
    /// </summary>
    private void RestoreSquashScales(SkeletonAccess skel)
    {
        if (squashStates == null) return;
        var pose = skel.Pose;
        for (int i = 0; i < squashStates.Length && i < ragdollBones.Count; i++)
        {
            ref var state = ref squashStates[i];
            if (!state.TouchedScale) continue;
            var boneIndex = ragdollBones[i].BoneIndex;
            if (boneIndex < 0 || boneIndex >= skel.BoneCount) continue;
            ref var mt = ref pose->ModelPose.Data[boneIndex];
            mt.Scale.X = state.BaseScale.X;
            mt.Scale.Y = state.BaseScale.Y;
            mt.Scale.Z = state.BaseScale.Z;
            state.TouchedScale = false;
        }
    }

    // --- Soft-body containment (the leash) ---
    //
    // Soft-tissue bodies are tiny (0.1 kg, centimeter capsules → very high inverse inertia),
    // collision-free, and hang on stiff springs; under a violent enough corpse whip the solver
    // can overshoot and fling one — the skinned vertices weighted to it stretch toward the
    // horizon and that flesh region visually VANISHES (the randomly-disappearing-mesh report).
    // The leash snaps any soft body that strays past its assembly distance (or NaNs) straight
    // back to its assembly-time offset from its anchor and zeroes its velocity. Every firing
    // is logged (budgeted) so field reports can tell containment from other causes.

    private struct SoftBodyLeash
    {
        public BepuPhysics.BodyHandle Child;
        public BepuPhysics.BodyHandle Parent;
        public string Name;
        public float MaxDistance;
        public Vector3 RelPos;      // child body position in parent body local space at assembly
        public Quaternion RelRot;   // child body orientation in parent local space at assembly
    }

    private readonly System.Collections.Generic.List<SoftBodyLeash> softBodyLeashes = new();
    private int softLeashLogBudget;

    private void ContainSoftTissueBodies()
    {
        if (simulation == null || softBodyLeashes.Count == 0) return;
        for (int i = 0; i < softBodyLeashes.Count; i++)
        {
            var leash = softBodyLeashes[i];
            var child = simulation.Bodies.GetBodyReference(leash.Child);
            var parent = simulation.Bodies.GetBodyReference(leash.Parent);
            var pp = parent.Pose.Position;
            if (float.IsNaN(pp.X + pp.Y + pp.Z))
                continue; // anchor itself is broken — the main NaN guard deals with the rig

            var cp = child.Pose.Position;
            var broken = float.IsNaN(cp.X + cp.Y + cp.Z +
                                     child.Pose.Orientation.X + child.Pose.Orientation.Y +
                                     child.Pose.Orientation.Z + child.Pose.Orientation.W);
            if (!broken && Vector3.Distance(cp, pp) <= leash.MaxDistance)
                continue;

            child.Pose.Position = pp + Vector3.Transform(leash.RelPos, parent.Pose.Orientation);
            child.Pose.Orientation = Quaternion.Normalize(parent.Pose.Orientation * leash.RelRot);
            child.Velocity.Linear = parent.Velocity.Linear;
            child.Velocity.Angular = Vector3.Zero;

            if (softLeashLogBudget > 0)
            {
                softLeashLogBudget--;
                log.Warning($"SoftTissue: contained '{leash.Name}' — " +
                            (broken ? "NaN pose" : $"strayed {Vector3.Distance(cp, pp):F2}m from anchor (leash {leash.MaxDistance:F2}m)") +
                            "; snapped back to assembly offset.");
            }
        }
    }

    /// <summary>Deactivate-time restore: guarded skeleton re-acquisition (the actor may be
    /// mid-teardown), then base-scale write-back and state drop.</summary>
    private void RestoreSquashScalesOnDeactivate()
    {
        if (squashStates == null) return;
        try
        {
            if (targetCharacterAddress != nint.Zero &&
                Core.Services.ObjectTable.LocalPlayer != null &&
                boneService.TryGetSkeleton(targetCharacterAddress) is { } skel)
            {
                RestoreSquashScales(skel);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RagdollController: failed to restore soft-tissue scales on deactivate");
        }
        squashStates = null;
    }
}
