// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Collections.Immutable;
using BepuPhysics.Constraints;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    public bool IsStandingSupportActive => standingActive;

    private static ImmutableArray<string> StandingSpineBones => HumanoidSkeleton.StandingSupportSpine;

    private bool CreateStandingSupportCore(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName = FfxivHumanoidProfile.PelvisBoneName)
    {
        if (simulation == null || !isActive || grabSlots.Count > 0) return false;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
            bodyRef.Activity.SleepThreshold = -1f;
            bodyRef.Awake = true;
        }
        BeginBiomechanicalSettle();

        BuildStandingConstraints(anchorWorldPos, uprightRot, anchorBoneName);
        standingActive = true;
        ApplyStandingAnchorCorrection();
        log.Info($"RagdollController: Standing support created — {standingConstraints.Count} constraints, anchor={anchorBoneName} ({anchorWorldPos.X:F2},{anchorWorldPos.Y:F2},{anchorWorldPos.Z:F2})");
        return true;
    }

    /// <summary>Swap the anchor bone/position while the support is already active, without re-waking bodies.</summary>
    private bool UpdateStandingSupportCore(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName = FfxivHumanoidProfile.PelvisBoneName)
    {
        if (!standingActive || simulation == null) return false;

        foreach (var h in standingConstraints)
            try { simulation.Solver.Remove(h); } catch { }
        standingConstraints.Clear();
        standingAnchorHandle = null;

        BuildStandingConstraints(anchorWorldPos, uprightRot, anchorBoneName);
        ApplyStandingAnchorCorrection();
        return true;
    }

    private void BuildStandingConstraints(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName)
    {
        var sim = simulation;
        if (sim == null) return;

        var anchorHandle = FindBodyHandle(anchorBoneName);
        if (anchorHandle.HasValue)
        {
            standingAnchorHandle = anchorHandle.Value;
            standingAnchorTarget = anchorWorldPos;

            standingConstraints.Add(sim.Solver.Add(anchorHandle.Value,
                new OneBodyLinearServo
                {
                    LocalOffset    = Vector3.Zero,
                    Target         = anchorWorldPos,
                    ServoSettings  = new ServoSettings(8f, 1f, 3000f),
                    SpringSettings = new SpringSettings(120f, 1f),
                }));

            standingConstraints.Add(sim.Solver.Add(anchorHandle.Value,
                new OneBodyAngularServo
                {
                    TargetOrientation = uprightRot,
                    ServoSettings     = new ServoSettings(8f, 1f, 600f),
                    SpringSettings    = new SpringSettings(40f, 1f),
                }));
        }

        float spineForce = 350f;
        float spineFreq  = 25f;
        foreach (var name in StandingSpineBones)
        {
            var h = FindBodyHandle(name);
            if (h.HasValue)
            {
                standingConstraints.Add(sim.Solver.Add(h.Value,
                    new OneBodyAngularServo
                    {
                        TargetOrientation = uprightRot,
                        ServoSettings     = new ServoSettings(10f, 1f, spineForce),
                        SpringSettings    = new SpringSettings(spineFreq, 1f),
                    }));
            }
            spineForce *= 0.7f;
            spineFreq  *= 0.8f;
        }
    }

    private void ApplyStandingAnchorCorrection()
    {
        if (!standingActive || simulation == null || !standingAnchorHandle.HasValue)
            return;

        var anchor = simulation.Bodies.GetBodyReference(standingAnchorHandle.Value);
        var correction = standingAnchorTarget - anchor.Pose.Position;
        var anchorVelocity = anchor.Velocity.Linear;

        // Clothes still on the corpse come with it; the hold left them behind on the floor for the same
        // reason the grab did. Decide first, move second — see CollectCarriedExternalRigs.
        CollectCarriedExternalRigs();

        // Keep the held point fixed without disabling contact response. If collision pushes the
        // ragdoll as a whole, translate every body back by the same delta and remove the global
        // anchor velocity; relative limb motion from the contact is preserved.
        foreach (var rb in ragdollBones)
        {
            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            body.Pose.Position += correction;
            body.Velocity.Linear -= anchorVelocity;
            body.Awake = true;
        }

        TranslateCarriedExternalRigs(correction, anchorVelocity);
    }

    private void RemoveStandingSupportCore()
    {
        if (!standingActive || simulation == null) return;

        foreach (var h in standingConstraints)
        {
            try { simulation.Solver.Remove(h); } catch { }
        }
        standingConstraints.Clear();
        standingAnchorHandle = null;

        var normalThreshold = RagdollSleepThreshold;
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            try
            {
                var bodyRef = simulation.Bodies.GetBodyReference(ragdollBones[i].BodyHandle);
                bodyRef.Activity.SleepThreshold = normalThreshold;
                bodyRef.Awake = true;
            }
            catch { }
        }

        standingActive = false;
        BeginBiomechanicalSettle();
        log.Info("RagdollController: Standing support removed");
    }

    private bool CreateWristConstraintsCore(Vector3 leftTarget, Vector3 rightTarget,
        float force = 500f, float freq = 80f)
    {
        if (simulation == null || !isActive || grabSlots.Count > 0) return false;

        foreach (var h in wristConstraints)
            try { simulation.Solver.Remove(h); } catch { }
        wristConstraints.Clear();

        wristForce = force;
        wristFreq  = freq;
        BuildWristConstraint(Bones.Hand.Left, leftTarget);
        BuildWristConstraint(Bones.Hand.Right, rightTarget);
        BeginBiomechanicalSettle();
        return wristConstraints.Count > 0;
    }

    private bool UpdateWristConstraintsCore(Vector3 leftTarget, Vector3 rightTarget)
    {
        if (simulation == null || wristConstraints.Count == 0) return false;

        foreach (var h in wristConstraints)
            try { simulation.Solver.Remove(h); } catch { }
        wristConstraints.Clear();

        BuildWristConstraint(Bones.Hand.Left, leftTarget);
        BuildWristConstraint(Bones.Hand.Right, rightTarget);
        return true;
    }

    private void RemoveWristConstraintsCore()
    {
        if (simulation == null) return;
        foreach (var h in wristConstraints)
            try { simulation.Solver.Remove(h); } catch { }
        wristConstraints.Clear();
    }

    private void BuildWristConstraint(string boneName, Vector3 target)
    {
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        wristConstraints.Add(simulation!.Solver.Add(handle.Value,
            new OneBodyLinearServo
            {
                LocalOffset    = Vector3.Zero,
                Target         = target,
                ServoSettings  = new ServoSettings(8f, 1f, wristForce),
                SpringSettings = new SpringSettings(wristFreq, 1f),
            }));
    }
}
