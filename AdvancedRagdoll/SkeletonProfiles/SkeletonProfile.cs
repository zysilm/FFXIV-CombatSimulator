// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;

namespace CombatSimulator.Animation.SkeletonProfiles;

/// <summary>
/// Everything the ragdoll knows about one kind of skeleton: which bone plays which anatomical part,
/// the bodies and joints to build for it, its ranges of motion and mass budget, the bones it is
/// recognised by, and the tuning of its class. Immutable. The ragdoll code asks the profile for
/// "the pelvis", "the hand bones", "the left thigh" instead of naming bones itself
/// (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §5).
///
/// A host provides a profile for the skeletons it knows (the plugin's is <c>FfxivHumanoidProfile</c>).
/// <see cref="Auto"/> stands for the rig generated from a skeleton's own topology, used when the
/// profile's <see cref="SignatureBones"/> are not all present.
/// </summary>
public sealed class SkeletonProfile
{
    public required string Name { get; init; }

    /// <summary>True for the generated rig (<see cref="Auto"/>): bodies come from the skeleton's topology, not from <see cref="BoneDefinitions"/>.</summary>
    public bool IsAuto { get; init; }

    /// <summary>The character's forward axis in skeleton space.</summary>
    public required Vector3 Forward { get; init; }

    /// <summary>Bones every skeleton of this kind has. All present: this profile fits; otherwise the generated rig is used.</summary>
    public required ImmutableArray<string> SignatureBones { get; init; }

    public required SkeletonLandmarks Landmarks { get; init; }
    public required SkeletonBoneClasses Classes { get; init; }

    /// <summary>
    /// Suffixes that mark a bone's side: the left one first, then the right one. Stripped (in this
    /// order) to look a bone up in the side-free tables.
    /// </summary>
    public required ImmutableArray<string> SideSuffixes { get; init; }

    /// <summary>Anatomical role by bone name: the first matching rule wins.</summary>
    public required ImmutableArray<BoneRule<SkeletonBoneRole>> RoleRules { get; init; }

    /// <summary>The bodies, in build order, with their default shapes, masses and joints.</summary>
    public required ImmutableArray<SkeletonBoneDefinition> BoneDefinitions { get; init; }

    /// <summary>Range of motion per side-stripped bone name.</summary>
    public required ImmutableDictionary<string, JointRom> RangeOfMotion { get; init; }

    public required SkeletonMassBudget MassBudget { get; init; }

    /// <summary>Hinges whose axis is taken from the reference pose.</summary>
    public required ImmutableArray<HingeFrameTarget> HingeFrameTargets { get; init; }

    /// <summary>Segments whose collision volume is fitted to the body mesh.</summary>
    internal ImmutableArray<StructuralMeshFitter.Target> FitTargets { get; init; } = ImmutableArray<StructuralMeshFitter.Target>.Empty;

    /// <summary>Left/right pairs of fitted segments that must agree.</summary>
    public ImmutableArray<(string Left, string Right)> FitPairs { get; init; } = ImmutableArray<(string Left, string Right)>.Empty;

    /// <summary>Density relative to water by bone name: the first matching rule wins; 1 otherwise.</summary>
    public required ImmutableArray<BoneRule<float>> BuoyancyDensities { get; init; }

    /// <summary>Places walkers aim for on a lying body.</summary>
    public required ImmutableArray<StompLandmark> StompLandmarks { get; init; }

    /// <summary>Bones that arrive when the body lands (the rest follows through).</summary>
    public required ImmutableArray<string> ImpactTorsoBones { get; init; }

    /// <summary>Bones whose joints are released on landing so the limbs follow through.</summary>
    public required ImmutableArray<string> ImpactFollowThroughBones { get; init; }

    /// <summary>The spine bones a standing support holds upright.</summary>
    public required ImmutableArray<string> StandingSupportSpine { get; init; }

    /// <summary>Which bone of another character's skeleton is the one it attacks with (light collision).</summary>
    public required BoneScoreTable LightCollisionAttackScores { get; init; }

    /// <summary>Which bone of another character's skeleton its light collision body follows.</summary>
    public required BoneScoreTable LightCollisionFollowScores { get; init; }

    public required FaceLandmarkVocabulary FaceLandmarks { get; init; }
    public required HairBoneNaming HairBones { get; init; }
    public required DigitNaming Digits { get; init; }

    public required SkeletonClassConstants ClassConstants { get; init; }

    /// <summary>Parameters of the generated rig; set on <see cref="Auto"/> only.</summary>
    public GenericRigParameters? GenericRig { get; init; }

    /// <summary>The anatomical role a bone's name gives it; false when no rule matches.</summary>
    public bool TryGetRole(string name, out SkeletonBoneRole role) => BoneRules.TryFirst(RoleRules, name, out role);

    /// <summary>The name without its side suffix, for the side-free tables.</summary>
    public string StripSide(string name)
    {
        foreach (var suffix in SideSuffixes)
            if (name.Length > suffix.Length && name.EndsWith(suffix))
                return name.Substring(0, name.Length - suffix.Length);
        return name;
    }

    public bool TryGetRangeOfMotion(string boneName, out JointRom rom)
        => RangeOfMotion.TryGetValue(StripSide(boneName), out rom);

    /// <summary>Density of a body part relative to water.</summary>
    public float BuoyancyDensity(string boneName)
    {
        if (string.IsNullOrEmpty(boneName)) return 1f;
        return BoneRules.TryFirst(BuoyancyDensities, boneName, out var density) ? density : 1.0f;
    }

    /// <summary>
    /// The generated rig: no named bones, the bodies come from the skeleton's own topology
    /// (<c>RagdollController.BuildGenericSkeletonDefs</c>) with these parameters, and its class tuning.
    /// </summary>
    public static SkeletonProfile Auto() => AutoProfile;

    private static readonly SkeletonProfile AutoProfile = new()
    {
        Name = "auto",
        IsAuto = true,
        Forward = Vector3.UnitZ,
        SignatureBones = ImmutableArray<string>.Empty,
        Landmarks = new SkeletonLandmarks
        {
            Pelvis = "", BellyFollow = "", LowerSpine = "", MidSpine = "", Chest = "", Neck = "", Head = "",
            Breast = default, Clavicle = default, UpperArm = default, Forearm = default, Hand = default,
            ArmpitHelper = default, Thigh = default, Shin = default, Calf = default, Foot = default, Toes = default,
        },
        Classes = new SkeletonBoneClasses
        {
            Cloth = BoneNameClass.None, SoftTissue = BoneNameClass.None, Weapon = BoneNameClass.None,
            Clavicle = BoneNameClass.None, UpperArm = BoneNameClass.None, Forearm = BoneNameClass.None,
            Hand = BoneNameClass.None, Thigh = BoneNameClass.None, Shin = BoneNameClass.None,
            Calf = BoneNameClass.None, Foot = BoneNameClass.None, Toe = BoneNameClass.None,
            LongLimbRoots = BoneNameClass.None, HairContact = BoneNameClass.None,
        },
        SideSuffixes = ImmutableArray<string>.Empty,
        RoleRules = ImmutableArray<BoneRule<SkeletonBoneRole>>.Empty,
        BoneDefinitions = ImmutableArray<SkeletonBoneDefinition>.Empty,
        RangeOfMotion = ImmutableDictionary<string, JointRom>.Empty,
        MassBudget = new SkeletonMassBudget
        {
            Fractions = ImmutableDictionary<string, float>.Empty,
            ProximalFingers = ImmutableArray<SidedBone>.Empty,
            HandGroupFraction = 0f, PalmShare = 0f, FingersShare = 0f,
            FootGroupFraction = 0f, FootShare = 0f, ToesShare = 0f,
        },
        HingeFrameTargets = ImmutableArray<HingeFrameTarget>.Empty,
        BuoyancyDensities = ImmutableArray<BoneRule<float>>.Empty,
        StompLandmarks = ImmutableArray<StompLandmark>.Empty,
        ImpactTorsoBones = ImmutableArray<string>.Empty,
        ImpactFollowThroughBones = ImmutableArray<string>.Empty,
        StandingSupportSpine = ImmutableArray<string>.Empty,
        LightCollisionAttackScores = BoneScoreTable.Empty,
        LightCollisionFollowScores = BoneScoreTable.Empty,
        FaceLandmarks = FaceLandmarkVocabulary.Empty,
        HairBones = HairBoneNaming.None,
        Digits = DigitNaming.None,
        // Generic rigs build a stiffer, less-conditioned constraint network: more solver iterations,
        // and a tighter velocity clamp per frame to stop energy blow-up (tiny bodies spin up fastest).
        ClassConstants = new SkeletonClassConstants(MaxLinearVelocity: 12f, MaxAngularVelocity: 16f, MinSolverIterations: 16),
        // The min-segment threshold is adaptive to skeleton size: large rigs (toads) use the cap so
        // a dense cluster of tiny bodies is not simulated; small rigs (bats) use the floor so their
        // small bones (wings/limbs) still get bodies and the ragdoll articulates.
        GenericRig = new GenericRigParameters
        {
            MinSegmentCap = 0.08f,
            MinSegmentFloor = 0.02f,
            MinSegmentFactor = 0.06f,
            MaxBodies = 40,
            SwingLimit = 0.6f,
            TwistLimit = 0.35f,
        },
    };

    /// <summary>
    /// The fit target for a segment, its kind (foot, hand, upper knee, thigh) taken from the
    /// profile's bone classes.
    /// </summary>
    internal static StructuralMeshFitter.Target FitTarget(SkeletonBoneClasses classes, string bone, string child,
        bool useBox, int minimumSamples, int hullSlices = 0)
        => new(bone, child, useBox, minimumSamples, hullSlices,
            classes.Foot.Matches(bone) ? StructuralMeshFitter.SegmentKind.Foot
            : classes.Hand.Matches(bone) ? StructuralMeshFitter.SegmentKind.Hand
            : classes.Shin.Matches(bone) ? StructuralMeshFitter.SegmentKind.UpperKnee
            : classes.Thigh.Matches(bone) ? StructuralMeshFitter.SegmentKind.Thigh
            : StructuralMeshFitter.SegmentKind.Other);
}
