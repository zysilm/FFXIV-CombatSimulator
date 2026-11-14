// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using CombatSimulator.Animation.SkeletonProfiles;

namespace CombatSimulator.Animation;

/// <summary>
/// The skeleton profile of FFXIV's humanoid skeleton (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §5): every
/// FFXIV bone name the ragdoll uses, its bone table, ranges of motion, mass budget and the rest, as
/// code. The values were moved here from the ragdoll code unchanged (same expressions, same order), so
/// the ragdoll behaves bit for bit as before. Pure data: nothing here touches the game, so the offline
/// tests compile this file directly.
/// </summary>
public static class FfxivHumanoidProfile
{
    /// <summary>
    /// The pelvis. A constant so it can stay the default value of public parameters
    /// (<c>RagdollController.CreateStandingSupport</c>'s anchor bone).
    /// </summary>
    public const string PelvisBoneName = "j_kosi";

    public static SkeletonProfile Profile { get; } = Build();

    private static float D2R(float degrees) => degrees * (MathF.PI / 180f);

    private static SidedBone Sided(string root) => new(root, root + "_l", root + "_r");

    private static BoneRule<T> Rule<T>(BoneNameClass match, T value) => new(match, value);

    private static SkeletonProfile Build()
    {
        var bones = new SkeletonLandmarks
        {
            Pelvis = PelvisBoneName,
            // n_hara must follow j_kosi, or the mesh tears.
            BellyFollow = "n_hara",
            LowerSpine = "j_sebo_a",
            MidSpine = "j_sebo_b",
            Chest = "j_sebo_c",
            Neck = "j_kubi",
            Head = "j_kao",
            Breast = Sided("j_mune"),
            Clavicle = Sided("j_sako"),
            UpperArm = Sided("j_ude_a"),
            Forearm = Sided("j_ude_b"),
            Hand = Sided("j_te"),
            ArmpitHelper = Sided("n_hkata"),
            Thigh = Sided("j_asi_a"),
            // j_asi_b and j_asi_c are BOTH real knee-region hinges (see LogLegBoneStructureDiagnostics):
            // j_asi_b is a ~6cm proximal stub and j_asi_c carries the other ~86% of the knee-to-ankle span.
            Shin = Sided("j_asi_b"),
            Calf = Sided("j_asi_c"),
            Foot = Sided("j_asi_d"),
            Toes = Sided("j_asi_e"),
        };

        var classes = new SkeletonBoneClasses
        {
            Cloth = BoneNameClass.Prefix("j_sk_"),
            SoftTissue = BoneNameClass.Prefix("j_mune"),
            Weapon = BoneNameClass.Prefix("j_buki"),
            Clavicle = BoneNameClass.Prefix("j_sako_"),
            UpperArm = BoneNameClass.Prefix("j_ude_a_"),
            Forearm = BoneNameClass.Prefix("j_ude_b_"),
            Hand = BoneNameClass.Prefix("j_te_"),
            Thigh = BoneNameClass.Prefix("j_asi_a_"),
            Shin = BoneNameClass.Prefix("j_asi_b_"),
            Calf = BoneNameClass.Prefix("j_asi_c_"),
            Foot = BoneNameClass.Prefix("j_asi_d_"),
            Toe = BoneNameClass.Prefix("j_asi_e_"),
            LongLimbRoots = BoneNameClass.Exact("j_ude_a", "j_ude_b", "j_asi_a", "j_asi_b"),
            HairContact = BoneNameClass.Exact("j_kao", "j_kubi", "j_kosi").Or(BoneNameClass.Prefix("j_sebo_", "j_ude_")),
        };

        return new SkeletonProfile
        {
            Name = "ffxiv-humanoid",
            // FFXIV characters face +Z in skeleton space.
            Forward = Vector3.UnitZ,

            // Structural bones every humanoid skeleton has. If all resolve, the hand-tuned human
            // profile fits and the existing passes run unchanged. If any is missing (bats, birds,
            // dragons, quadrupeds, voidsent) the skeleton is treated as non-humanoid and gets a
            // generated ragdoll instead.
            SignatureBones = ImmutableArray.Create(
                "j_kosi", "j_sebo_a", "j_kubi",
                "j_ude_a_l", "j_ude_a_r",
                "j_asi_a_l", "j_asi_a_r"),

            Landmarks = bones,
            Classes = classes,
            SideSuffixes = ImmutableArray.Create("_l", "_r"),

            RoleRules = ImmutableArray.Create(
                Rule(BoneNameClass.Prefix("j_mune_"), SkeletonBoneRole.SoftBody),
                Rule(classes.Cloth, SkeletonBoneRole.Cloth),
                Rule(classes.Weapon, SkeletonBoneRole.Weapon),
                Rule(BoneNameClass.Exact("j_kosi"), SkeletonBoneRole.Pelvis),
                Rule(BoneNameClass.Prefix("j_sebo_").Or(BoneNameClass.Exact("j_kubi")), SkeletonBoneRole.Spine),
                Rule(BoneNameClass.Exact("j_kao"), SkeletonBoneRole.Head),
                Rule(classes.Clavicle.Or(classes.UpperArm), SkeletonBoneRole.Shoulder),
                Rule(classes.Forearm, SkeletonBoneRole.Elbow),
                Rule(classes.Hand, SkeletonBoneRole.Hand),
                Rule(classes.Thigh, SkeletonBoneRole.Hip),
                // j_asi_b and j_asi_c are BOTH real knee-region hinges (see LogLegBoneStructureDiagnostics):
                // j_asi_b is a ~6cm proximal stub and j_asi_c carries the other ~86% of the knee-to-ankle
                // span, and in a real death pose j_asi_c bends MORE than j_asi_b does. Treating j_asi_c as
                // "the ankle" (its old inference) is what let it get folded away as an inert helper.
                Rule(classes.Shin, SkeletonBoneRole.Knee),
                Rule(classes.Calf, SkeletonBoneRole.Knee),
                Rule(classes.Foot.Or(classes.Toe), SkeletonBoneRole.Foot)),

            BoneDefinitions = BoneDefinitions(),
            RangeOfMotion = RangeOfMotion(),
            MassBudget = MassBudget(),

            HingeFrameTargets = ImmutableArray.Create(
                new HingeFrameTarget("j_asi_b_l", "j_asi_a_l", "j_asi_c_l"), // upper knee: thigh -> shin -> calf
                new HingeFrameTarget("j_asi_b_r", "j_asi_a_r", "j_asi_c_r"),
                new HingeFrameTarget("j_asi_c_l", "j_asi_b_l", "j_asi_d_l"), // lower knee: shin -> calf -> ankle
                new HingeFrameTarget("j_asi_c_r", "j_asi_b_r", "j_asi_d_r")),

            // Only the torso and the thighs are fitted: they are what a lying body rests on, and where a
            // fixed volume around the bone is furthest from the surface (the spine runs along the back of
            // the torso, not through its middle). Arms, shins, hands and feet keep their built-in volumes.
            FitTargets = ImmutableArray.Create(
                SkeletonProfile.FitTarget(classes, "j_kosi",    "j_sebo_a", true,  30, hullSlices: 3),
                SkeletonProfile.FitTarget(classes, "j_sebo_a",  "j_sebo_b", true,  30, hullSlices: 3),
                SkeletonProfile.FitTarget(classes, "j_sebo_b",  "j_sebo_c", true,  30, hullSlices: 3),
                SkeletonProfile.FitTarget(classes, "j_sebo_c",  "j_kubi",   true,  30, hullSlices: 3),
                SkeletonProfile.FitTarget(classes, "j_asi_a_l", "j_asi_b_l", false, 24, hullSlices: 4),
                SkeletonProfile.FitTarget(classes, "j_asi_a_r", "j_asi_b_r", false, 24, hullSlices: 4)),
            // Left/right pairs that must agree; a one-sided or lopsided result is a broken mesh, not anatomy.
            FitPairs = ImmutableArray.Create(("j_asi_a_l", "j_asi_a_r")),

            // The proportions are anatomical (the chest holds the lungs and is lightest, shins and feet
            // are heaviest), so a body floats chest up with the legs trailing.
            BuoyancyDensities = ImmutableArray.Create(
                Rule(BoneNameClass.Prefix("j_sebo", "j_mune"), 0.78f),
                Rule(BoneNameClass.Exact("j_kosi"), 0.96f),
                Rule(BoneNameClass.Prefix("j_kubi", "j_kao"), 1.02f),
                Rule(BoneNameClass.Prefix("j_asi_a"), 1.03f),
                Rule(BoneNameClass.Prefix("j_asi"), 1.09f),
                Rule(BoneNameClass.Prefix("j_sako", "j_ude"), 1.04f),
                Rule(BoneNameClass.Prefix("j_te"), 1.08f),
                Rule(BoneNameClass.Prefix("j_sk_"), 1.0f)),

            // Navigation landmarks are deliberately semantic rather than a second copy of the collider
            // list. A nearest-collider search tends to collapse onto the large skirt/torso volumes and
            // gives the swarm no recognizable pose to follow. These bones describe stable, readable
            // parts of a humanoid pose. Mod-specific soft bones may be used as X/Z landmarks, but they
            // are never promoted to traversal surfaces by IsTraversalSurfaceBone.
            StompLandmarks = ImmutableArray.Create<StompLandmark>(
                // Chest and abdomen dominate automatic stomp selection.
                new("j_mune_l", "chest L", 13f, 0.10f), new("j_mune_r", "chest R", 13f, 0.10f),
                new("iv_c_mune_l", "chest L", 13f, 0.09f), new("iv_c_mune_r", "chest R", 13f, 0.09f),
                new("n_hara", "abdomen", 16f, 0.13f),
                new("iv_fukubu_phys_r", "abdomen", 15f, 0.10f), new("ya_fukubu_phys", "abdomen", 15f, 0.10f),
                // Pelvis, buttocks and groin are the other high-priority load-bearing landmarks.
                new("j_kosi", "pelvis", 15f, 0.12f),
                new("iv_shiri_l", "buttock L", 13f, 0.10f), new("iv_shiri_r", "buttock R", 13f, 0.10f),
                new("iv_inshin_l", "groin L", 12f, 0.07f), new("iv_inshin_r", "groin R", 12f, 0.07f),
                new("iv_kougan_l", "groin L", 11f, 0.06f), new("iv_kougan_r", "groin R", 11f, 0.06f),
                new("iv_omanko", "groin", 12f, 0.06f),
                // Armpit/shoulder landmarks have medium priority.
                new("j_sako_l", "armpit L", 7f, 0.075f), new("j_sako_r", "armpit R", 7f, 0.075f),
                new("n_hkata_l", "armpit L", 6f, 0.07f), new("n_hkata_r", "armpit R", 6f, 0.07f),
                // Extremities are valid but intentionally much less likely than the torso.
                new("j_te_l", "hand L", 1.6f, 0.055f), new("j_te_r", "hand R", 1.6f, 0.055f),
                new("j_asi_e_l", "foot L", 1.8f, 0.06f), new("j_asi_e_r", "foot R", 1.8f, 0.06f)),

            // The bodies that actually arrive when a body falls; everything else is along for the ride.
            ImpactTorsoBones = ImmutableArray.Create(
                bones.Pelvis, bones.BellyFollow, bones.LowerSpine, bones.MidSpine, bones.Chest),
            // The joints released on impact so the limbs can follow through.
            ImpactFollowThroughBones = ImmutableArray.Create(
                bones.Clavicle.Left, bones.Clavicle.Right,
                bones.UpperArm.Left, bones.UpperArm.Right,
                bones.Forearm.Left, bones.Forearm.Right,
                bones.Hand.Left, bones.Hand.Right,
                bones.Neck, bones.Head),
            StandingSupportSpine = ImmutableArray.Create(bones.LowerSpine, bones.MidSpine, bones.Chest),

            LightCollisionAttackScores = new BoneScoreTable(ImmutableArray.Create(
                Rule(BoneNameClass.Contains("j_te_", "hand", "claw", "talon"), 6),
                Rule(BoneNameClass.Contains("j_kao", "head", "mouth", "jaw"), 5),
                Rule(BoneNameClass.Contains("j_asi_e", "foot", "hoof"), 4),
                Rule(BoneNameClass.Contains("j_kosi", "n_hara", "j_sebo", "body"), 3))),
            LightCollisionFollowScores = new BoneScoreTable(ImmutableArray.Create(
                Rule(BoneNameClass.Contains("j_kosi", "n_hara", "pelvis", "body"), 6),
                Rule(BoneNameClass.Contains("j_sebo", "spine", "chest", "torso"), 5),
                Rule(BoneNameClass.Contains("j_asi_a", "j_asi_b", "thigh", "leg"), 3))),

            FaceLandmarks = new FaceLandmarkVocabulary
            {
                Excluded = ImmutableArray.Create("ear", "mimi"),
                Eye = ImmutableArray.Create("lash", "matuge", "mabuta", "eyelid"),
                LeftSuffix = "_l",
                RightSuffix = "_r",
                LeftWord = "left",
                RightWord = "right",
                Nose = ImmutableArray.Create("nose", "hana"),
                Mouth = ImmutableArray.Create("mouth", "lip", "kuchi", "kuti"),
                Specificity = ImmutableArray.Create(
                    Rule(BoneNameClass.Contains("lash", "matuge"), 4),
                    Rule(BoneNameClass.Contains("mabuta", "eyelid"), 3),
                    Rule(BoneNameClass.Contains("nose", "hana"), 3),
                    Rule(BoneNameClass.Contains("mouth", "kuchi", "kuti"), 3),
                    Rule(BoneNameClass.Contains("lip"), 2)),
            },

            HairBones = new HairBoneNaming("j_kami_", "j_ex_h", "_ke_"),

            // Finger name roots (thumb/index/middle/ring/pinky) as their own '_'-delimited segment —
            // e.g. j_oya_a_l, or on a mod skeleton's individual toes iv_asi_hito_a_l. Matched as a
            // whole segment (not a substring) so this never catches the pelvis. Toes: the vanilla
            // combined toe cluster (j_asi_e_l/r) and a mod skeleton's individual toe joints
            // (iv_asi_oya_a_l, and so on), which mirror the finger names with "asi_" inserted.
            Digits = new DigitNaming
            {
                DigitTokens = ImmutableHashSet.Create(StringComparer.Ordinal, "oya", "hito", "naka", "kusu", "ko"),
                FootToken = "asi",
                ToeClusterToken = "e",
            },

            // Human/player rigs are hand-tuned and normally stable, but their constraint solver can
            // still occasionally diverge. Keep the ceiling above normal ragdoll motion so it only
            // catches runaway energy, not ordinary falls or cinematic drags. No solver-iteration floor.
            ClassConstants = new SkeletonClassConstants(MaxLinearVelocity: 18f, MaxAngularVelocity: 14f, MinSolverIterations: 0),
        };
    }

    // Bone catalog values author generic ball joints and fallback collision volumes. The core
    // knee/elbow/wrist/ankle/toe topology and signed ranges are anatomical runtime invariants.
    /// <summary>
    /// Complete core bone catalog with skeleton parents, default enabled state, and physics
    /// parameters. The core body plus each finger's proximal joint are enabled by default;
    /// additional skeleton-specific bones are discovered by the Advanced UI and default disabled.
    ///
    /// Skeleton hierarchy (from Ktisis/xivmodding):
    ///   j_kosi (pelvis, root)
    ///     j_sebo_a (lumbar) → j_sebo_b (thoracic) → j_sebo_c (chest)
    ///       j_kubi (neck) → j_kao (head)
    ///       j_sako_l/r (clavicle) → j_ude_a (upper arm) → j_ude_b (forearm) → j_te (hand)
    ///       j_mune_l/r (breast, child of j_sebo_b)
    ///     j_asi_a (thigh) → j_asi_b (upper knee) → j_asi_c (lower knee/calf) → j_asi_d (foot) → j_asi_e (toes)
    /// </summary>
    private static ImmutableArray<SkeletonBoneDefinition> BoneDefinitions() => ImmutableArray.Create(new[]
    {
        // === SPINE CHAIN === (all enabled by default — core of ragdoll)
        new SkeletonBoneDefinition { Name = "j_kosi",    SkeletonParent = null,       Enabled = true,  CapsuleRadius = 0.105f, CapsuleHalfLength = 0.06f, Mass = 8.0f,  SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Pelvis" },
        new SkeletonBoneDefinition { Name = "j_sebo_a",  SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.10f,  CapsuleHalfLength = 0.05f, Mass = 10.0f, SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Lower Spine" },
        new SkeletonBoneDefinition { Name = "j_sebo_b",  SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.09f,  CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Mid Spine" },
        new SkeletonBoneDefinition { Name = "j_sebo_c",  SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.09f,  CapsuleHalfLength = 0.05f, Mass = 6.0f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Chest" },
        new SkeletonBoneDefinition { Name = "j_kubi",    SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.04f,  CapsuleHalfLength = 0.03f, Mass = 2.0f,  SwingLimit = 0.5f,                JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Neck" },
        new SkeletonBoneDefinition { Name = "j_kao",     SkeletonParent = "j_kubi",   Enabled = true,  CapsuleRadius = 0.08f,  CapsuleHalfLength = 0.04f, Mass = 3.5f,  SwingLimit = 0.35f,               JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Head" },

        // === CLOTH/SKIRT BONES === (chained per radial slot: A→j_sebo_a, B→matching A, C→matching B; b=back, f=front, s=side)
        new SkeletonBoneDefinition { Name = "j_sk_b_a_l", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back A L" },
        new SkeletonBoneDefinition { Name = "j_sk_b_a_r", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back A R" },
        new SkeletonBoneDefinition { Name = "j_sk_f_a_l", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front A L" },
        new SkeletonBoneDefinition { Name = "j_sk_f_a_r", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front A R" },
        new SkeletonBoneDefinition { Name = "j_sk_s_a_l", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side A L" },
        new SkeletonBoneDefinition { Name = "j_sk_s_a_r", SkeletonParent = "j_sebo_a",  Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side A R" },
        new SkeletonBoneDefinition { Name = "j_sk_b_b_l", SkeletonParent = "j_sk_b_a_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back B L" },
        new SkeletonBoneDefinition { Name = "j_sk_b_b_r", SkeletonParent = "j_sk_b_a_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back B R" },
        new SkeletonBoneDefinition { Name = "j_sk_f_b_l", SkeletonParent = "j_sk_f_a_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front B L" },
        new SkeletonBoneDefinition { Name = "j_sk_f_b_r", SkeletonParent = "j_sk_f_a_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front B R" },
        new SkeletonBoneDefinition { Name = "j_sk_s_b_l", SkeletonParent = "j_sk_s_a_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side B L" },
        new SkeletonBoneDefinition { Name = "j_sk_s_b_r", SkeletonParent = "j_sk_s_a_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side B R" },
        new SkeletonBoneDefinition { Name = "j_sk_b_c_l", SkeletonParent = "j_sk_b_b_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back C L" },
        new SkeletonBoneDefinition { Name = "j_sk_b_c_r", SkeletonParent = "j_sk_b_b_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back C R" },
        new SkeletonBoneDefinition { Name = "j_sk_f_c_l", SkeletonParent = "j_sk_f_b_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front C L" },
        new SkeletonBoneDefinition { Name = "j_sk_f_c_r", SkeletonParent = "j_sk_f_b_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front C R" },
        new SkeletonBoneDefinition { Name = "j_sk_s_c_l", SkeletonParent = "j_sk_s_b_l", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side C L" },
        new SkeletonBoneDefinition { Name = "j_sk_s_c_r", SkeletonParent = "j_sk_s_b_r", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side C R" },

        // === WEAPON HOLSTER/SHEATHE === (disabled by default — enable for sheathed weapon physics)
        new SkeletonBoneDefinition { Name = "j_buki_kosi_l",  SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Hip Sheathe" },
        new SkeletonBoneDefinition { Name = "j_buki_kosi_r",  SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Hip Sheathe" },
        new SkeletonBoneDefinition { Name = "j_buki2_kosi_l", SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Hip Holster" },
        new SkeletonBoneDefinition { Name = "j_buki2_kosi_r", SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Hip Holster" },
        new SkeletonBoneDefinition { Name = "j_buki_sebo_l",  SkeletonParent = "j_sebo_c", Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Back Scabbard" },
        new SkeletonBoneDefinition { Name = "j_buki_sebo_r",  SkeletonParent = "j_sebo_c", Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Back Scabbard" },

        // === BREAST === (child of j_sebo_b — soft tissue)
        // Soft split: the BallSocket is stiff (10 Hz critical — static gravity sag g/ω² ≈ 2.5mm,
        // just a flesh-weight offset) and the jiggle lives in the ROTATIONAL dof (3 Hz underdamped
        // servo swinging the mass about the bone-origin pivot), matching how the game's own phyb
        // bones move. The old 1 Hz/0.05 socket sagged a quarter meter and rang for seconds.
        // HalfLength pushes the capsule's mass away from the pivot so gravity/inertia have a lever.
        new SkeletonBoneDefinition { Name = "j_mune_l",  SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.06f,  CapsuleHalfLength = 0.045f, Mass = 0.1f,  SwingLimit = 0.25f,               JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Left Breast",  SoftBody = true, SoftSpringFreq = 10f, SoftSpringDamp = 1f, SoftServoFreq = 3f, SoftServoDamp = 0.2f },
        new SkeletonBoneDefinition { Name = "j_mune_r",  SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.06f,  CapsuleHalfLength = 0.045f, Mass = 0.1f,  SwingLimit = 0.25f,               JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Right Breast",  SoftBody = true, SoftSpringFreq = 10f, SoftSpringDamp = 1f, SoftServoFreq = 3f, SoftServoDamp = 0.2f },

        // === CLAVICLE === (child of j_sebo_c, inserts between chest and arms)
        new SkeletonBoneDefinition { Name = "j_sako_l",  SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.04f, Mass = 0.5f,  SwingLimit = 0.35f,               JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Left Clavicle" },
        new SkeletonBoneDefinition { Name = "j_sako_r",  SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.04f, Mass = 0.5f,  SwingLimit = 0.35f,               JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Right Clavicle" },

        // === ARM CHAIN === (all enabled — skeleton: j_sako → j_ude_a → j_ude_b → j_te)
        new SkeletonBoneDefinition { Name = "j_ude_a_l", SkeletonParent = "j_sako_l", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.35f,               JointType = 0, TwistMinAngle = -0.65f, TwistMaxAngle = 0.65f, Description = "Left Upper Arm" },
        new SkeletonBoneDefinition { Name = "j_ude_a_r", SkeletonParent = "j_sako_r", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.35f,               JointType = 0, TwistMinAngle = -0.65f, TwistMaxAngle = 0.65f, Description = "Right Upper Arm" },
        new SkeletonBoneDefinition { Name = "j_ude_b_l", SkeletonParent = "j_ude_a_l",Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.07f, Mass = 1.2f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -1.25f, TwistMaxAngle = 1.25f, Description = "Left Forearm" },
        new SkeletonBoneDefinition { Name = "j_ude_b_r", SkeletonParent = "j_ude_a_r",Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.07f, Mass = 1.2f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -1.25f, TwistMaxAngle = 1.25f, Description = "Right Forearm" },
        new SkeletonBoneDefinition { Name = "j_te_l",    SkeletonParent = "j_ude_b_l",Enabled = true,  CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Left Hand" },
        new SkeletonBoneDefinition { Name = "j_te_r",    SkeletonParent = "j_ude_b_r",Enabled = true,  CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Right Hand" },

        // === FINGERS === (proximal joint only; distal/helper digits inherit through propagation)
        new SkeletonBoneDefinition { Name = "j_oya_a_l",  SkeletonParent = "j_te_l", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Left Thumb A" },
        new SkeletonBoneDefinition { Name = "j_oya_a_r",  SkeletonParent = "j_te_r", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Right Thumb A" },
        new SkeletonBoneDefinition { Name = "j_hito_a_l", SkeletonParent = "j_te_l", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Left Index A" },
        new SkeletonBoneDefinition { Name = "j_hito_a_r", SkeletonParent = "j_te_r", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Right Index A" },
        new SkeletonBoneDefinition { Name = "j_naka_a_l", SkeletonParent = "j_te_l", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Left Middle A" },
        new SkeletonBoneDefinition { Name = "j_naka_a_r", SkeletonParent = "j_te_r", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Right Middle A" },
        new SkeletonBoneDefinition { Name = "j_kusu_a_l", SkeletonParent = "j_te_l", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Left Ring A" },
        new SkeletonBoneDefinition { Name = "j_kusu_a_r", SkeletonParent = "j_te_r", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Right Ring A" },
        new SkeletonBoneDefinition { Name = "j_ko_a_l",   SkeletonParent = "j_te_l", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Left Little A" },
        new SkeletonBoneDefinition { Name = "j_ko_a_r",   SkeletonParent = "j_te_r", Enabled = true, CapsuleRadius = 0.01f, CapsuleHalfLength = 0.03f, Mass = 1.0f, SwingLimit = 0.3f, JointType = 0, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f, Description = "Right Little A" },

        // === LEG CHAIN === (j_asi_e is enabled by mesh-aware mode)
        // j_asi_b and j_asi_c are BOTH real knee-region hinges: bind pose measures j_asi_b as a
        // ~6cm proximal stub (14% of the knee-to-ankle span) and j_asi_c as the other ~86%, and a
        // real death pose bends j_asi_c (~74°) MORE than j_asi_b (~47°) — see
        // LogLegBoneStructureDiagnostics. j_asi_c is not a skinning helper; it is where most of
        // the visible knee bend actually happens.
        new SkeletonBoneDefinition { Name = "j_asi_a_l", SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.045f, CapsuleHalfLength = 0.12f, Mass = 10.0f, SwingLimit = 1.3f,                JointType = 0, TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f,  Description = "Left Thigh" },
        new SkeletonBoneDefinition { Name = "j_asi_a_r", SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.045f, CapsuleHalfLength = 0.12f, Mass = 10.0f, SwingLimit = 1.3f,                JointType = 0, TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f,  Description = "Right Thigh" },
        new SkeletonBoneDefinition { Name = "j_asi_b_l", SkeletonParent = "j_asi_a_l",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.03f, Mass = 1.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Shin (Upper Knee)" },
        new SkeletonBoneDefinition { Name = "j_asi_b_r", SkeletonParent = "j_asi_a_r",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.03f, Mass = 1.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Shin (Upper Knee)" },
        new SkeletonBoneDefinition { Name = "j_asi_c_l", SkeletonParent = "j_asi_b_l",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.19f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Calf (Lower Knee)" },
        new SkeletonBoneDefinition { Name = "j_asi_c_r", SkeletonParent = "j_asi_b_r",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.19f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Calf (Lower Knee)" },
        new SkeletonBoneDefinition { Name = "j_asi_d_l", SkeletonParent = "j_asi_c_l",Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.0f,  Mass = 1.0f,  SwingLimit = 0.29f,               JointType = 0, TwistMinAngle = -0.64f, TwistMaxAngle = 0.65f, Description = "Left Foot (Ankle)" },
        new SkeletonBoneDefinition { Name = "j_asi_d_r", SkeletonParent = "j_asi_c_r",Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.0f,  Mass = 1.0f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.65f, TwistMaxAngle = 0.65f, Description = "Right Foot (Ankle)" },
        new SkeletonBoneDefinition { Name = "j_asi_e_l", SkeletonParent = "j_asi_d_l",Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.02f, Mass = 0.2f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Toes" },
        new SkeletonBoneDefinition { Name = "j_asi_e_r", SkeletonParent = "j_asi_d_r",Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.02f, Mass = 0.2f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Toes" },
    });

    // Tier D — Anthropometric segment masses as fractions of total body mass.
    // Winter, Biomechanics and Motor Control of Human Movement, Table 3.1.
    // Keyed by the side-stripped bone name (trailing _l/_r removed). Bones not in
    // this table (cloth j_sk_*, weapon j_buki*, breast j_mune_*) keep their literal
    // Mass and are excluded from anthropometric scaling.
    //
    // The table uses Winter's values as per-side segment values and adds to 1.0 for the core
    // humanoid. The extra calf and clavicle bodies are subdivisions of
    // Winter segments, not additional mass. Hand mass is distributed dynamically because a
    // profile may enable any subset of proximal finger bodies.
    private static SkeletonMassBudget MassBudget() => new()
    {
        Fractions = new Dictionary<string, float>
        {
            // Lower limb, each side. Winter's shank fraction (.0465) is split between the two real
            // knee-region bodies by their measured bind-pose length share (b=14%, c=86% — see
            // LogLegBoneStructureDiagnostics), not folded into one.
            { "j_asi_a", 0.100f },
            { "j_asi_b", 0.0065f },
            { "j_asi_c", 0.0400f },
            { "j_asi_d", 0.0145f },
            // Upper limb, each side. j_te + enabled proximal fingers share .006 below.
            { "j_ude_a", 0.028f },
            { "j_ude_b", 0.016f },
            // Head and neck.
            { "j_kao", 0.036f },
            { "j_kubi", 0.045f },
            // Trunk. Two clavicles receive .001 each from the thorax/chest allocation.
            { "j_kosi", 0.142f },
            { "j_sebo_a", 0.139f },
            { "j_sebo_b", 0.110f },
            { "j_sebo_c", 0.104f },
            { "j_sako", 0.001f },
        }.ToImmutableDictionary(),
        ProximalFingers = ImmutableArray.Create(
            Sided("j_oya_a"), Sided("j_hito_a"), Sided("j_naka_a"), Sided("j_kusu_a"), Sided("j_ko_a")),
        // Winter's .006 hand value includes the digits. Keep that group total invariant whether a
        // profile uses no finger bodies or all five. With digits present, 75% stays in the palm and
        // 25% is divided between the enabled proximal bodies.
        HandGroupFraction = 0.006f,
        PalmShare = 0.75f,
        FingersShare = 0.25f,
        // j_asi_d + j_asi_e are one anatomical foot segment. Mesh-derived thickness turns the
        // terminal toe cluster into a body, so the existing group mass is redistributed.
        FootGroupFraction = 0.0145f,
        FootShare = 0.85f,
        ToesShare = 0.15f,
    };

    // === Tier C — Anatomical range-of-motion (ROM) table ============================
    // Solver-AGNOSTIC, clinical/ISB-derived per-DOF ranges of motion, stored in RADIANS.
    // Keyed by the side-stripped bone name (so left/right share one entry, matching the mass
    // table). Bones absent from this table fall back to their hand-set boneDef twist values
    // (and the fold-stop).
    //
    // Source: standard healthy-adult goniometric / clinical ROM (AAOS averages) expressed
    // in the ISB Grood-Suntay joint coordinate system; rounded to typical means:
    //   Knee     flexion 0-140 deg, hyperextension 5,        axial (tibial) +/-10
    //   Elbow    flexion 0-145 deg, hyperextension 5,        axial (pron/sup) +/-80
    //   Hip      flexion 120, extension 20, abduction 45, adduction 25, axial +/-40
    //   Shoulder flexion 170, extension 50, abduction 170, adduction 40, axial +/-90
    //   Ankle    dorsiflexion 20, plantarflexion 45,         inv/eversion +/-20
    //   Neck     flexion/extension +/-45, lateral +/-40,     axial +/-70
    //   Spine    (per segment) flex/ext +/-15, lateral +/-15, axial +/-10
    //
    // Wired: the AXIAL twist range of every joint and the knee/elbow FLEXION + HYPEREXTENSION
    // bounds about the hinge axis. Only active while RagdollAnatomicalRom is on; off by
    // default (the twist governors, hemisphere locks, and profile-table limits stay active
    // regardless).
    private static ImmutableDictionary<string, JointRom> RangeOfMotion() => new Dictionary<string, JointRom>
    {
        // Hinge joints
        // Knee flexion: relaxed passive max is ~135-145° (thigh↔shin interior angle
        // 35-45°). Connected pairs don't collide, so there is no soft-tissue backstop —
        // use the conservative end or the shin folds visibly INTO the thigh.
        // The knee's ~135-145° total flexion is now split across two real hinges (j_asi_b then
        // j_asi_c); a measured death pose bent j_asi_c (lower) more than j_asi_b (upper) — see
        // LogLegBoneStructureDiagnostics — so the budget below is weighted the same way rather
        // than split evenly or given to each in full (which would let the pair fold past 180°).
        { "j_asi_b", new JointRom(D2R(60f),  D2R(5f),   0f,        0f,        D2R(-10f), D2R(10f)) }, // Knee (upper, shin)
        { "j_asi_c", new JointRom(D2R(100f), D2R(5f),   0f,        0f,        D2R(-10f), D2R(10f)) }, // Knee (lower, calf)
        { "j_ude_b", new JointRom(D2R(145f), D2R(5f),   0f,        0f,        D2R(-80f), D2R(80f)) }, // Elbow (forearm)
        // Ball joints (axial wired now; swing fields deferred to Tier A)
        // Hip flexion: 120° is the CLINICAL value measured with a bent knee. With the knee
        // extended the two-joint hamstrings cap passive flexion at ~80–90°; death poses mostly
        // have near-straight legs, so 95° is the relaxed-body compromise (no cross-joint
        // hamstring constraint is modelled). This is what stops the seated corpse folding
        // its chest flat onto its legs.
        { "j_asi_a", new JointRom(D2R(95f),  D2R(20f),  D2R(45f),  D2R(25f),  D2R(-40f), D2R(40f)) }, // Hip (thigh)
        { "j_ude_a", new JointRom(D2R(170f), D2R(50f),  D2R(170f), D2R(40f),  D2R(-90f), D2R(90f)) }, // Shoulder (upper arm)
        { "j_sako",  new JointRom(D2R(170f), D2R(50f),  D2R(170f), D2R(40f),  D2R(-90f), D2R(90f)) }, // Shoulder (clavicle)
        { "j_asi_d", new JointRom(D2R(20f),  D2R(45f),  0f,        0f,        D2R(-20f), D2R(20f)) }, // Ankle (foot): dorsi/plantar + inv/ev
        { "j_kubi",  new JointRom(D2R(45f),  D2R(45f),  D2R(40f),  D2R(40f),  D2R(-70f), D2R(70f)) }, // Neck
        { "j_sebo_a",new JointRom(D2R(15f),  D2R(15f),  D2R(15f),  D2R(15f),  D2R(-10f), D2R(10f)) }, // Lower spine
        { "j_sebo_b",new JointRom(D2R(15f),  D2R(15f),  D2R(15f),  D2R(15f),  D2R(-10f), D2R(10f)) }, // Mid spine
        { "j_sebo_c",new JointRom(D2R(15f),  D2R(15f),  D2R(15f),  D2R(15f),  D2R(-10f), D2R(10f)) }, // Chest
    }.ToImmutableDictionary();
}
