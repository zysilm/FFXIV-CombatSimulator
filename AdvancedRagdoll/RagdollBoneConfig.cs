// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace AdvancedRagdoll;

// Moved from the plugin's Configuration.cs in stage A4. Neither is saved in the user's configuration (no
// property of Configuration has these types), so they may change assembly.

/// <summary>One bone's ragdoll parameters as the bone-def builder reads them (<see cref="Ragdoll.AllBoneDefaults"/>,
/// <see cref="Ragdoll.BuildBoneDefsFromConfigs"/>).</summary>
[Serializable]
public class RagdollBoneConfig
{
    public string Name { get; set; } = "";
    public string? SkeletonParent { get; set; }  // real skeleton parent (for computing physics parent chain)
    public bool Enabled { get; set; } = true;     // whether this bone participates in physics
    public float CapsuleRadius { get; set; }
    public float CapsuleHalfLength { get; set; }
    public float Mass { get; set; }
    public float SwingLimit { get; set; }
    public float? SwingMinLimit { get; set; } // hinge-only lower bound; null means migrate/default
    public float? HingeRestAngle { get; set; } // hinge-only passive rest target; null disables/defaults
    public float? HingeRestSpringFreq { get; set; }
    public float? HingeRestMaxForce { get; set; }
    public int JointType { get; set; } // 0=Ball, 1=Hinge
    public float TwistMinAngle { get; set; }
    public float TwistMaxAngle { get; set; }
    public int AnatomicalRole { get; set; } // 0=Auto, see SkeletonBoneRole
    public int ColliderShape { get; set; } // 0=Capsule, 1=Box
    public float BoxHalfExtentX { get; set; }
    public float BoxHalfExtentY { get; set; }
    public float BoxHalfExtentZ { get; set; }
    public string? Description { get; set; }      // human-readable label for UI
    // Soft body spring settings (for breast/jiggle bones). Split: stiff translation (the socket
    // only carries flesh weight, ~mm of sag), soft rotation (the jiggle swings about the pivot).
    public bool SoftBody { get; set; }             // use soft springs + AngularServo instead of rigid + AngularMotor
    public float SoftSpringFreq { get; set; } = 10f;   // BallSocket spring frequency (Hz); static sag = g/(2πf)²
    public float SoftSpringDamp { get; set; } = 1f;    // BallSocket damping ratio, critical = no translational ring
    public float SoftServoFreq { get; set; } = 3f;     // AngularServo spring frequency (Hz), the jiggle rate
    public float SoftServoDamp { get; set; } = 0.2f;   // AngularServo damping ratio, lower = more swings before settling
}

/// <summary>The knee power-loss tuning of the guided collapse (fixed at these defaults).</summary>
[Serializable]
public class GuidedCollapseKneePowerLossSettings
{
    public bool EntryConditioningEnabled { get; set; } = true;
    public float EntryStanceThreshold { get; set; } = 0.28f;
    public float EntryReadyStance { get; set; } = 0.30f;
    public float EntryReadyKneeAngle { get; set; } = 10f;
    public float EntryMinDuration { get; set; } = 0.24f;
    public float EntryMaxDuration { get; set; } = 0.42f;
    public float EntryTargetStanceStart { get; set; } = 0.34f;
    public float EntryTargetStanceEnd { get; set; } = 0.50f;
    public float EntryPelvisDownStart { get; set; } = 0.32f;
    public float EntryPelvisDownEnd { get; set; } = 0.60f;
    public float KneeFlexDegrees { get; set; } = 34f;
    // Knee-flex torques act on the lower-leg inertia, which Tier D's anthropometric masses
    // cut to ~half (shin 3->1.8, calf 1->0.18 kg). Scaled down ~x0.55 so the knee buckles at
    // the same rate instead of over-driving on the now-lighter leg (was 82 / 42).
    public float KneeBuckleFlexForce { get; set; } = 46f;
    public float KneeTorsoFlexForce { get; set; } = 24f;
    // Foot supports are positional pins that anchor the WHOLE body's pivot over the planted
    // foot; body mass is unchanged (~70 kg), so these stay as-is (lowering them slips the foot).
    public float BuckleFootSupportForce { get; set; } = 1100f;
    public float TorsoFootSupportForce { get; set; } = 650f;
    public bool FootProxyEnabled { get; set; } = true;
    public float FootProxyForwardOffset { get; set; } = 0.10f;
    public float FootProxyDownOffset { get; set; } = 0.035f;
    public float FootProxyGroundClearance { get; set; } = 0.018f;
    // Pelvis-drive torques act on the trunk, which Tier D made HEAVIER (pelvis 8->9.9,
    // mid-spine 5->7.7 kg). Scaled up ~x1.24 so the torso still pitches forward in step with
    // the buckling legs instead of lagging (was 420 / 220).
    public float BucklePelvisForce { get; set; } = 520f;
    public float TorsoPelvisForce { get; set; } = 275f;
    public float ChestPitchDegrees { get; set; } = 41f;
    public float BuckleMinDuration { get; set; } = 0.24f;
    public float BuckleTimeout { get; set; } = 0.95f;
    public float BucklePelvisDropToTorso { get; set; } = 0.30f;
    public float BuckleKneeAngleToTorso { get; set; } = 22f;
    public float TorsoMinDuration { get; set; } = 0.55f;
    public float TorsoTimeout { get; set; } = 0.90f;
}
