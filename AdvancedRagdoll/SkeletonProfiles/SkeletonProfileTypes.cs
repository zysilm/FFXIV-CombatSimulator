// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Immutable;

namespace CombatSimulator.Animation.SkeletonProfiles;

/// <summary>
/// The anatomical role of a bone. Same members, in the same order (so the same values), as
/// <c>RagdollController.AnatomicalRole</c>, which converts to and from it by value.
/// </summary>
public enum SkeletonBoneRole { Auto, Generic, Pelvis, Spine, Head, Shoulder, Elbow, Hand, Hip, Knee, Ankle, Foot, Cloth, SoftBody, Weapon }

/// <summary>A bone that exists once per side; <see cref="Root"/> is its name without the side.</summary>
public readonly record struct SidedBone(string Root, string Left, string Right)
{
    public string For(bool left) => left ? Left : Right;
}

/// <summary>Named anatomical landmarks: which bone of the skeleton plays which part.</summary>
public sealed record SkeletonLandmarks
{
    public required string Pelvis { get; init; }
    /// <summary>A helper bone at the belly that must follow the pelvis (otherwise the mesh tears).</summary>
    public required string BellyFollow { get; init; }
    public required string LowerSpine { get; init; }
    public required string MidSpine { get; init; }
    public required string Chest { get; init; }
    public required string Neck { get; init; }
    public required string Head { get; init; }
    public required SidedBone Breast { get; init; }
    public required SidedBone Clavicle { get; init; }
    public required SidedBone UpperArm { get; init; }
    public required SidedBone Forearm { get; init; }
    public required SidedBone Hand { get; init; }
    /// <summary>A helper bone at the armpit.</summary>
    public required SidedBone ArmpitHelper { get; init; }
    public required SidedBone Thigh { get; init; }
    /// <summary>The upper knee-region bone (a short stub below the knee).</summary>
    public required SidedBone Shin { get; init; }
    /// <summary>The lower knee-region bone (most of the knee-to-ankle span).</summary>
    public required SidedBone Calf { get; init; }
    public required SidedBone Foot { get; init; }
    public required SidedBone Toes { get; init; }
}

/// <summary>Named groups of bone names, each a <see cref="BoneNameClass"/>.</summary>
public sealed record SkeletonBoneClasses
{
    public required BoneNameClass Cloth { get; init; }
    /// <summary>Soft-tissue bones excluded from walking surfaces and some collision (no trailing separator).</summary>
    public required BoneNameClass SoftTissue { get; init; }
    public required BoneNameClass Weapon { get; init; }
    public required BoneNameClass Clavicle { get; init; }
    public required BoneNameClass UpperArm { get; init; }
    public required BoneNameClass Forearm { get; init; }
    public required BoneNameClass Hand { get; init; }
    public required BoneNameClass Thigh { get; init; }
    public required BoneNameClass Shin { get; init; }
    public required BoneNameClass Calf { get; init; }
    public required BoneNameClass Foot { get; init; }
    public required BoneNameClass Toe { get; init; }
    /// <summary>Side-stripped names of the long limb segments whose inertia is corrected about the long axis.</summary>
    public required BoneNameClass LongLimbRoots { get; init; }
    /// <summary>Bones the live hair collides with.</summary>
    public required BoneNameClass HairContact { get; init; }
}

/// <summary>
/// A bone definition as the profile authors it. The same fields, with the same defaults, as the
/// plugin's per-bone configuration record, which is built from it field by field.
/// </summary>
public sealed record SkeletonBoneDefinition
{
    public string Name { get; init; } = "";
    public string? SkeletonParent { get; init; }
    public bool Enabled { get; init; } = true;
    public float CapsuleRadius { get; init; }
    public float CapsuleHalfLength { get; init; }
    public float Mass { get; init; }
    public float SwingLimit { get; init; }
    public float? SwingMinLimit { get; init; }
    public float? HingeRestAngle { get; init; }
    public float? HingeRestSpringFreq { get; init; }
    public float? HingeRestMaxForce { get; init; }
    public int JointType { get; init; }
    public float TwistMinAngle { get; init; }
    public float TwistMaxAngle { get; init; }
    public int AnatomicalRole { get; init; }
    public int ColliderShape { get; init; }
    public float BoxHalfExtentX { get; init; }
    public float BoxHalfExtentY { get; init; }
    public float BoxHalfExtentZ { get; init; }
    public string? Description { get; init; }
    public bool SoftBody { get; init; }
    public float SoftSpringFreq { get; init; } = 10f;
    public float SoftSpringDamp { get; init; } = 1f;
    public float SoftServoFreq { get; init; } = 3f;
    public float SoftServoDamp { get; init; } = 0.2f;
}

/// <summary>Per-degree-of-freedom range of motion of one joint, in radians.</summary>
public readonly struct JointRom
{
    public readonly float FlexionMax;    // forward bend / dorsiflexion (rad, >=0)  [swing]
    public readonly float ExtensionMax;  // backward bend / plantarflex / hyperext (rad, >=0)
    public readonly float AbductionMax;  // (rad) [swing]
    public readonly float AdductionMax;  // (rad) [swing]
    public readonly float AxialMin;      // axial twist lower bound (rad, signed)
    public readonly float AxialMax;      // axial twist upper bound (rad, signed)

    public JointRom(float flexionMax, float extensionMax, float abductionMax,
                    float adductionMax, float axialMin, float axialMax)
    {
        FlexionMax = flexionMax;
        ExtensionMax = extensionMax;
        AbductionMax = abductionMax;
        AdductionMax = adductionMax;
        AxialMin = axialMin;
        AxialMax = axialMax;
    }
}

/// <summary>Anthropometric mass budget: shares of the total body mass.</summary>
public sealed record SkeletonMassBudget
{
    /// <summary>Share of the body mass per side-stripped bone name (per side for sided bones).</summary>
    public required ImmutableDictionary<string, float> Fractions { get; init; }
    /// <summary>The finger bodies that share the hand's mass, in the order they are given it.</summary>
    public required ImmutableArray<SidedBone> ProximalFingers { get; init; }
    /// <summary>Share of the body mass of one hand including its digits.</summary>
    public required float HandGroupFraction { get; init; }
    /// <summary>Share of the hand group kept by the palm when finger bodies are present.</summary>
    public required float PalmShare { get; init; }
    /// <summary>Share of the hand group divided between the finger bodies.</summary>
    public required float FingersShare { get; init; }
    /// <summary>Share of the body mass of one foot including its toes.</summary>
    public required float FootGroupFraction { get; init; }
    public required float FootShare { get; init; }
    public required float ToesShare { get; init; }
}

/// <summary>A hinge whose frame is taken from the reference pose: the bone, the segment above and below.</summary>
public readonly record struct HingeFrameTarget(string Bone, string Parent, string Child);

/// <summary>A place on a lying body that walkers aim for, with its label, selection weight and wander radius.</summary>
public readonly record struct StompLandmark(string Bone, string Label, float Weight, float WanderRadius);

/// <summary>
/// Scores bone names by ordered tiers of name fragments: the name is lower-cased, and the first tier
/// with a fragment in it gives the score; 0 when none does.
/// </summary>
public sealed class BoneScoreTable
{
    public BoneScoreTable(ImmutableArray<BoneRule<int>> tiers) => Tiers = tiers;

    public ImmutableArray<BoneRule<int>> Tiers { get; }

    public static BoneScoreTable Empty { get; } = new(ImmutableArray<BoneRule<int>>.Empty);

    public int Score(string boneName)
    {
        var name = boneName.ToLowerInvariant();
        return BoneRules.TryFirst(Tiers, name, out var score) ? score : 0;
    }
}

/// <summary>The words face bones are named with, to classify them as eye, nose or mouth landmarks.</summary>
public sealed record FaceLandmarkVocabulary
{
    /// <summary>Bones never used (ears).</summary>
    public required ImmutableArray<string> Excluded { get; init; }
    /// <summary>Eyelid and eyelash bones; they need a side.</summary>
    public required ImmutableArray<string> Eye { get; init; }
    public required string LeftSuffix { get; init; }
    public required string RightSuffix { get; init; }
    public required string LeftWord { get; init; }
    public required string RightWord { get; init; }
    public required ImmutableArray<string> Nose { get; init; }
    public required ImmutableArray<string> Mouth { get; init; }
    /// <summary>How specific a face bone is: ordered tiers, the first containing tier wins; 1 when none does.</summary>
    public required ImmutableArray<BoneRule<int>> Specificity { get; init; }

    public static FaceLandmarkVocabulary Empty { get; } = new()
    {
        Excluded = ImmutableArray<string>.Empty,
        Eye = ImmutableArray<string>.Empty,
        LeftSuffix = "\0",
        RightSuffix = "\0",
        LeftWord = "\0",
        RightWord = "\0",
        Nose = ImmutableArray<string>.Empty,
        Mouth = ImmutableArray<string>.Empty,
        Specificity = ImmutableArray<BoneRule<int>>.Empty,
    };

    /// <summary>0 left eye, 1 right eye, 2 nose, 3 mouth, -1 none.</summary>
    public int Category(string boneName)
    {
        var name = boneName.ToLowerInvariant();
        if (ContainsAny(name, Excluded))
            return -1;

        if (ContainsAny(name, Eye))
        {
            if (name.EndsWith(LeftSuffix, StringComparison.Ordinal) || name.Contains(LeftWord)) return 0;
            if (name.EndsWith(RightSuffix, StringComparison.Ordinal) || name.Contains(RightWord)) return 1;
        }
        if (ContainsAny(name, Nose)) return 2;
        if (ContainsAny(name, Mouth)) return 3;
        return -1;
    }

    public int SpecificityOf(string boneName)
    {
        var name = boneName.ToLowerInvariant();
        return BoneRules.TryFirst(Specificity, name, out var score) ? score : 1;
    }

    private static bool ContainsAny(string name, ImmutableArray<string> parts)
    {
        foreach (var part in parts)
            if (name.Contains(part))
                return true;
        return false;
    }
}

/// <summary>How hair bones are named: a plain prefix, or a numbered prefix (a digit or a marker after it).</summary>
public sealed record HairBoneNaming(string Prefix, string NumberedPrefix, string NumberedMarker)
{
    public static HairBoneNaming None { get; } = new("\0", "\0", "\0");

    public bool IsHairBone(string? name) => name != null &&
        (name.StartsWith(Prefix, StringComparison.Ordinal) ||
         (name.StartsWith(NumberedPrefix, StringComparison.Ordinal) && name.Length > NumberedPrefix.Length &&
          (char.IsDigit(name[NumberedPrefix.Length]) || name.Contains(NumberedMarker, StringComparison.Ordinal))));
}

/// <summary>
/// How finger and toe bones are named: '_'-separated segments, one of which is a digit token, or a
/// limb token followed by a toe-cluster token or a digit token.
/// </summary>
public sealed record DigitNaming
{
    public required ImmutableHashSet<string> DigitTokens { get; init; }
    public required string FootToken { get; init; }
    public required string ToeClusterToken { get; init; }

    public static DigitNaming None { get; } = new()
    {
        DigitTokens = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
        FootToken = "\0",
        ToeClusterToken = "\0",
    };

    public bool IsDigitBone(string name)
    {
        var parts = name.Split('_');

        foreach (var part in parts)
            if (DigitTokens.Contains(part)) return true;

        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i] == FootToken && (parts[i + 1] == ToeClusterToken || DigitTokens.Contains(parts[i + 1])))
                return true;

        return false;
    }
}

/// <summary>Tuning that differs between classes of skeleton (hand-tuned profile or generated rig).</summary>
public sealed record SkeletonClassConstants(float MaxLinearVelocity, float MaxAngularVelocity, int MinSolverIterations);

/// <summary>Parameters of the generic rig generated from a skeleton's own topology.</summary>
public sealed record GenericRigParameters
{
    /// <summary>Upper bound of the minimum segment length for a body (big skeletons).</summary>
    public required float MinSegmentCap { get; init; }
    /// <summary>Lower bound of the minimum segment length (small skeletons).</summary>
    public required float MinSegmentFloor { get; init; }
    /// <summary>Minimum segment length as a share of the skeleton's largest segment.</summary>
    public required float MinSegmentFactor { get; init; }
    public required int MaxBodies { get; init; }
    /// <summary>Ball cone half-angle (rad).</summary>
    public required float SwingLimit { get; init; }
    /// <summary>Ball axial twist (rad).</summary>
    public required float TwistLimit { get; init; }
}
