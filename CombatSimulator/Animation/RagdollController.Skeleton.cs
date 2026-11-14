// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.SkeletonProfiles;
using System.Collections.Immutable;
using BepuPhysics;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    /// <summary>
    /// Bone definitions for the mesh-aware humanoid rig: the built-in profile, filtered to enabled
    /// bones with physics parents computed. Collider dimensions come from the body mesh fit.
    /// </summary>
    private static RagdollBoneDef[] GetBoneDefs() => BuildBoneDefsFromConfigs(AllBoneDefaults);

    // Structural bones every humanoid skeleton has (the profile's signature bones). If all resolve,
    // the hand-tuned human profile fits and the existing passes run unchanged. If any is missing
    // (bats, birds, dragons, quadrupeds, voidsent) the skeleton is treated as non-humanoid and gets
    // a generated ragdoll instead (SkeletonProfile.Auto).
    private static ImmutableArray<string> HumanoidSignatureBones => HumanoidSkeleton.SignatureBones;

    private bool IsHumanoidSkeleton(Dictionary<string, int> humanNameToIndex)
    {
        foreach (var bone in HumanoidSignatureBones)
            if (!humanNameToIndex.ContainsKey(bone))
                return false;
        return true;
    }

    private bool IsNpcHumanoidSkeleton(ISkeletonPose skel)
    {
        foreach (var bone in HumanoidSignatureBones)
            if (ResolveBoneIndex(skel, bone) < 0)
                return false;
        return true;
    }

    /// <summary>
    /// Build a ragdoll bone definition set from a skeleton's real topology, for
    /// non-humanoid characters where the human profile does not fit. Capsule size
    /// and mass scale with actual bone lengths (so small rigs like bats do not get
    /// oversized human capsules that explode), parents follow the real skeleton
    /// hierarchy (no human-name mapping, so no orphaning), and all joints are loose
    /// ball joints. The returned defs feed the same Pass 1/2/3 body/constraint build
    /// and StepAndApply write-back as the human path.
    /// </summary>
    private (RagdollBoneDef[] Defs, Dictionary<string, int> NameToIndex) BuildGenericSkeletonDefs(ISkeletonPose skel)
    {
        var genericRig = SkeletonProfile.Auto().GenericRig!;
        var pose = skel.ModelPose;
        int n = skel.BoneCount;
        int pc = skel.ParentCount;

        // World position of every bone in the current (death) pose.
        var wpos = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            ref var mt = ref pose[i];
            wpos[i] = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
        }

        int ParentOf(int i) => (i >= 0 && i < pc) ? skel.Parent(i) : -1;

        // Distance to direct parent, and the longest child segment a bone owns.
        var distToParent = new float[n];
        var longestChildLen = new float[n];
        for (int i = 0; i < n; i++)
        {
            int p = ParentOf(i);
            if (p < 0 || p >= n) continue;
            var d = Vector3.Distance(wpos[i], wpos[p]);
            distToParent[i] = d;
            if (d > longestChildLen[p]) longestChildLen[p] = d;
        }

        // Adaptive min-segment threshold scaled to the skeleton's own size: a fraction of
        // its largest segment, clamped to [floor, cap]. Big rigs (toad, longest segment
        // ~1.7m) land at the cap and stay sparse; small rigs (bat, ~0.2m) drop to the floor
        // so their wing/limb bones are simulated instead of being skipped as "twigs".
        float maxSegment = 0f;
        for (int i = 0; i < n; i++)
            if (longestChildLen[i] > maxSegment) maxSegment = longestChildLen[i];
        float minSegment = Math.Clamp(maxSegment * genericRig.MinSegmentFactor, genericRig.MinSegmentFloor, genericRig.MinSegmentCap);
        log.Info($"RagdollController: generic min-segment threshold {minSegment:F3}m (skeleton largest segment {maxSegment:F3}m)");

        // A bone gets a body if it owns a real forward segment. Coincident/twig bones
        // (fingers, tips) are skipped and follow via StepAndApply propagation.
        var significant = new bool[n];
        int significantCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (longestChildLen[i] >= minSegment)
            {
                significant[i] = true;
                significantCount++;
            }
        }

        // Cap body count on large rigs: keep the longest segments, but force-keep the
        // significant ancestors of kept bones so the constraint tree stays connected
        // (a kept bone whose ancestors were dropped would become a free-floating root).
        if (significantCount > genericRig.MaxBodies)
        {
            var ranked = new List<int>();
            for (int i = 0; i < n; i++)
                if (significant[i]) ranked.Add(i);
            ranked.Sort((a, b) => longestChildLen[b].CompareTo(longestChildLen[a]));

            var keep = new bool[n];
            for (int k = 0; k < genericRig.MaxBodies && k < ranked.Count; k++)
            {
                keep[ranked[k]] = true;
                int p = ParentOf(ranked[k]);
                while (p >= 0)
                {
                    if (significant[p]) keep[p] = true;
                    p = ParentOf(p);
                }
            }
            for (int i = 0; i < n; i++)
                significant[i] = significant[i] && keep[i];
        }

        int SignificantAncestor(int i)
        {
            int p = ParentOf(i);
            while (p >= 0)
            {
                if (significant[p]) return p;
                p = ParentOf(p);
            }
            return -1;
        }

        string Name(int i) => "gen_" + i.ToString();

        var simIndices = new List<int>();
        for (int i = 0; i < n; i++)
            if (significant[i]) simIndices.Add(i);

        // Emit roots first, then longest-from-parent first, so boneToFirstChild picks
        // each bone's longest child as its capsule axis (best rotational stability).
        simIndices.Sort((a, b) =>
        {
            bool aRoot = SignificantAncestor(a) < 0;
            bool bRoot = SignificantAncestor(b) < 0;
            if (aRoot != bRoot) return aRoot ? -1 : 1;
            return distToParent[b].CompareTo(distToParent[a]);
        });

        var nameToIndex = new Dictionary<string, int>();
        foreach (var i in simIndices)
            nameToIndex[Name(i)] = i;

        var defs = new List<RagdollBoneDef>(simIndices.Count);
        foreach (var i in simIndices)
        {
            float segLen = MathF.Max(longestChildLen[i], minSegment);
            int anc = SignificantAncestor(i);
            defs.Add(new RagdollBoneDef
            {
                Name = Name(i),
                ParentName = anc >= 0 ? Name(anc) : null,
                CapsuleRadius = Math.Clamp(segLen * 0.28f, 0.02f, 0.18f),
                CapsuleHalfLength = segLen * 0.45f,
                Mass = Math.Clamp(segLen * 20f, 0.5f, 12f),
                SwingLimit = genericRig.SwingLimit,
                Joint = JointType.Ball,
                TwistMinAngle = -genericRig.TwistLimit,
                TwistMaxAngle = genericRig.TwistLimit,
                AnatomicalRole = AnatomicalRole.Generic,
                ColliderShape = RagdollColliderShape.Capsule,
                BoxHalfExtents = new Vector3(Math.Clamp(segLen * 0.28f, 0.02f, 0.18f), segLen * 0.45f, Math.Clamp(segLen * 0.28f, 0.02f, 0.18f)),
            });
        }

        return (defs.ToArray(), nameToIndex);
    }

    // Cap on discovered soft-tissue bodies. Standard (mod bones) needs ~59 on a full IVCS
    // skeleton; All bones adds every remaining vanilla bone (~60 more on a human body
    // skeleton), so 128 leaves headroom without letting an exotic rig double past it.
    private const int MaxModSoftTissueBones = 128;

    /// <summary>
    /// Discover extra bones and append them as SoftBody jiggle defs. Coverage Standard takes
    /// mod-skeleton bones (iv_/ya_ prefixes — Rue/YAS/IVCS lineage) except fingers and toes;
    /// coverage All takes EVERY skeleton bone that is not already a rig body, vanilla included.
    /// Body meshes are
    /// weighted to these bones, so driving them restores the secondary motion the game's own
    /// phyb simulation provides in life (our pose overwrite suppresses it during ragdoll).
    /// Anchoring: each discovered bone attaches to its nearest ancestor that is (or has just
    /// become) a rig def, so open chains hang link by link while isolated bones anchor
    /// straight to the torso bone that carries them.
    /// </summary>
    private RagdollBoneDef[] AppendModSoftTissueDefs(
        ISkeletonPose skel,
        RagdollBoneDef[] defs,
        Dictionary<string, int> nameToIndex,
        Dictionary<string, RagdollBoneDef> defByName)
    {
        // Coverage: Standard = mod-skeleton bones only (prefix match), excluding digits;
        // All = every bone; All-except-digits = every bone but the fingers and toes.
        // Digits should normally inherit the hand/foot pose rather than use the generic,
        // deliberately loose soft-tissue constraints.
        var scope = settings.RagdollSoftTissueScope;
        var allBones = scope == 1 || scope == 2;
        var excludeDigits = scope != 1;

        var prefixes = new List<string>();
        foreach (var part in settings.RagdollSoftTissueBonePrefixes.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) prefixes.Add(trimmed);
        }
        if (!allBones && prefixes.Count == 0) return defs;

        int n = Math.Min(skel.BoneCount, skel.BoneNameCount);
        int pc = skel.ParentCount;

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in defs) known.Add(d.Name);

        // Fit the dynamically discovered bodies to the vertices that the rendered body mesh
        // actually skins to them.  The old estimator measured all the way to the nearest active
        // ragdoll ancestor; helper chains often skip several skeleton nodes, so that distance had
        // almost no relationship to the flesh region and routinely produced the maximum capsule.
        var fitCandidates = new HashSet<int>();
        for (int i = 0; i < n && i < pc; i++)
        {
            var candidateName = skel.BoneName(i);
            if (candidateName == null || known.Contains(candidateName))
                continue;

            if (!allBones)
            {
                var matched = false;
                foreach (var prefix in prefixes)
                    if (candidateName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                if (!matched)
                    continue;
            }

            if (!excludeDigits || !IsDigitBone(candidateName))
                fitCandidates.Add(i);
        }

        var meshFits = fitCandidates.Count == 0
            ? new Dictionary<int, SoftTissueMeshFit>()
            : meshes.SoftTissueFits(skel.Actor, skel, fitCandidates, settings.RagdollVerboseLog);

        List<RagdollBoneDef>? added = null;
        // Havok skeletons are topologically ordered (parent before child), so chains resolve
        // in one forward pass: by the time a child is visited its discovered parent is known.
        for (int i = 0; i < n && i < pc; i++)
        {
            var name = skel.BoneName(i);
            if (name == null || known.Contains(name)) continue;

            if (!allBones)
            {
                var matched = false;
                foreach (var prefix in prefixes)
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
                if (!matched) continue;
            }
            if (excludeDigits && IsDigitBone(name))
            {
                continue;
            }

            // Nearest ancestor that is a rig def (original or discovered) — the joint anchor.
            // A match with no rig ancestor is skipped: it would orphan into a free body.
            string? anchorName = null;
            int p = skel.Parent(i);
            while (p >= 0 && p < n)
            {
                var pName = skel.BoneName(p);
                if (pName != null && known.Contains(pName)) { anchorName = pName; break; }
                p = p < pc ? skel.Parent(p) : -1;
            }
            if (anchorName == null || !nameToIndex.ContainsKey(anchorName))
                continue;

            // A missing mesh cluster is deliberately a tiny inertial proxy.  It is safer to
            // under-represent a helper bone than to fabricate a large torso-sized collider from
            // hierarchy distance.  With a usable cluster, dimensions and center come entirely
            // from the weighted model vertices.
            var hasMeshFit = meshFits.TryGetValue(i, out var meshFit);
            var radius = hasMeshFit ? meshFit.Radius : SoftTissueFallbackRadius;
            var halfLength = hasMeshFit ? meshFit.HalfLength : SoftTissueFallbackHalfLength;

            var def = new RagdollBoneDef
            {
                Name = name,
                ParentName = anchorName,
                CapsuleRadius = radius,
                CapsuleHalfLength = halfLength,
                Mass = 0.1f,
                SwingLimit = 0.35f,
                Joint = JointType.Ball,
                AnatomicalRole = AnatomicalRole.SoftBody,
                ColliderShape = RagdollColliderShape.Capsule,
                SoftBody = true,
                SoftSpringFreq = 10f,
                SoftSpringDamp = 1f,
                SoftServoFreq = 3f,
                SoftServoDamp = 0.2f,
                HasMeshFit = hasMeshFit,
                MeshFitCenterModelOffset = hasMeshFit ? meshFit.CenterModelOffset : Vector3.Zero,
                MeshFitAxisModel = hasMeshFit ? meshFit.AxisModel : Vector3.UnitY,
            };

            (added ??= new List<RagdollBoneDef>()).Add(def);
            known.Add(name);
            nameToIndex[name] = i;
            defByName[name] = def;
            if (added.Count >= MaxModSoftTissueBones) break;
        }

        if (added == null) return defs;

        var scopeLabel = allBones
            ? excludeDigits ? "all bones except digits" : "all bones"
            : "mod bones except digits";
        var fittedCount = added.Count(d => d.HasMeshFit);
        log.Info($"SoftTissue: discovered {added.Count} soft bone(s) ({scopeLabel}, " +
                 $"mesh-fitted={fittedCount}, conservative-fallback={added.Count - fittedCount}): " +
                 string.Join(", ", added.ConvertAll(d => d.Name)));

        var merged = new RagdollBoneDef[defs.Length + added.Count];
        defs.CopyTo(merged, 0);
        added.CopyTo(merged, defs.Length);
        return merged;
    }

    /// <summary>
    /// True for a finger bone (any skeleton) or a toe bone (the combined toe cluster, or a mod
    /// skeleton's individual toe joints), by the profile's digit naming (SkeletonProfile.Digits). Used by
    /// Standard and "All bones except digits" coverage to keep the (larger, cheaper-per-bone) capsules
    /// away from digits, where a wobbling fingertip or toe reads as wrong rather than fleshy.
    /// </summary>
    private static bool IsDigitBone(string name) => HumanoidSkeleton.Digits.IsDigitBone(name);
    // Catalog hint for generic authored joints. Core anatomical joints override it by bone:
    // knees are signed one-coordinate hinges; elbows are two-coordinate swivel hinges; distal
    // joints use dedicated frames. Every rigid joint adds only finite angular friction.
    public enum JointType { Ball, Hinge }
    public enum AnatomicalRole { Auto, Generic, Pelvis, Spine, Head, Shoulder, Elbow, Hand, Hip, Knee, Ankle, Foot, Cloth, SoftBody, Weapon }
    public enum RagdollColliderShape { Capsule, Box, ProfileHull, Mesh }

    private void SetDismemberedBonesCore(IEnumerable<string> bones)
    {
        localDismemberBones.Clear();
        foreach (var bone in bones)
            if (!string.IsNullOrWhiteSpace(bone) && !localDismemberBones.Contains(bone))
                localDismemberBones.Add(bone);
        useLocalDismemberBones = true;
    }

    private RagdollBoneDef[] BuildActivePhysicsDefsForDismemberment(
        ISkeletonPose skel,
        RagdollBoneDef[] defs,
        Dictionary<string, int> activeNameToIndex)
    {
        var severedBones = useLocalDismemberBones ? localDismemberBones : settings.DismemberPocBones;
        if (severedBones == null || severedBones.Count == 0 || defs.Length == 0)
            return defs;

        var severedRoots = new List<int>();
        foreach (var boneName in severedBones)
        {
            if (string.IsNullOrWhiteSpace(boneName)) continue;
            var idx = ResolveBoneIndex(skel, boneName);
            if (idx >= 0 && !severedRoots.Contains(idx))
                severedRoots.Add(idx);
        }
        if (severedRoots.Count == 0)
            return defs;

        var physicsRoots = new List<int>();
        foreach (var def in defs)
            if (def.ParentName == null && activeNameToIndex.TryGetValue(def.Name, out var idx))
                physicsRoots.Add(idx);

        var removableRoots = new List<int>(severedRoots.Count);
        foreach (var root in severedRoots)
        {
            var containsPhysicsRoot = false;
            foreach (var physicsRoot in physicsRoots)
            {
                if (IsDescendantOrSelf(skel, physicsRoot, root))
                {
                    containsPhysicsRoot = true;
                    break;
                }
            }

            if (!containsPhysicsRoot)
                removableRoots.Add(root);
        }

        if (removableRoots.Count == 0)
            return defs;

        var kept = new List<RagdollBoneDef>(defs.Length);
        var removedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in defs)
        {
            if (!activeNameToIndex.TryGetValue(def.Name, out var idx))
                continue;

            var severed = false;
            foreach (var root in removableRoots)
            {
                if (IsDescendantOrSelf(skel, idx, root))
                {
                    severed = true;
                    break;
                }
            }

            if (severed)
                removedNames.Add(def.Name);
            else
                kept.Add(def);
        }

        if (removedNames.Count == 0)
            return defs;
        if (kept.Count < 2)
        {
            log.Warning($"RagdollController: active dismember physics would leave {kept.Count} bodies; keeping full rig.");
            return defs;
        }

        foreach (var name in removedNames)
            activeNameToIndex.Remove(name);

        log.Info($"RagdollController: active dismember physics removed {removedNames.Count}/{defs.Length} bodies.");
        return kept.ToArray();
    }

    // Ragdoll bone definition
    public struct RagdollBoneDef
    {
        public string Name;
        public string? ParentName;
        public float CapsuleRadius;
        public float CapsuleHalfLength;
        public float Mass;
        public float SwingLimit;
        public float SwingMinLimit;
        public float HingeRestAngle;
        public float HingeRestSpringFreq;
        public float HingeRestMaxForce;
        public JointType Joint;
        public float TwistMinAngle;
        public float TwistMaxAngle;
        public AnatomicalRole AnatomicalRole;
        public RagdollColliderShape ColliderShape;
        public Vector3 BoxHalfExtents;
        public bool SoftBody;          // use soft springs + AngularServo (for breast/jiggle)
        public float SoftSpringFreq;   // BallSocket frequency (Hz)
        public float SoftSpringDamp;   // BallSocket damping ratio
        public float SoftServoFreq;    // AngularServo frequency (Hz)
        public float SoftServoDamp;    // AngularServo damping ratio
        // Dynamic Soft Tissue bodies can be fitted to the model's actual weighted vertices.
        // These values are in unscaled skeleton model space and are intentionally runtime-only;
        // hand-authored Ragdoll Advanced profiles continue to use their explicit shapes.
        public bool HasMeshFit;
        public Vector3 MeshFitCenterModelOffset;
        public Vector3 MeshFitAxisModel;
    }

    // Runtime bone with physics body
    private struct RagdollBone
    {
        public int BoneIndex;       // index in FFXIV skeleton
        public int ParentBoneIndex; // parent bone index (-1 if root)
        public BodyHandle BodyHandle;
        public string Name;
        // Rotation offset: physicsBodyRot * CapsuleToBoneOffset = boneWorldRot
        // Capsule Y axis is oriented along the bone segment, which differs from the bone's rotation.
        public Quaternion CapsuleToBoneOffset;
        // Half the distance from this bone to its child bone. Used to convert
        // the capsule center (at segment midpoint) back to the bone origin position.
        // 0 for leaf bones and degenerate (zero-length) segments.
        public float SegmentHalfLength;
        // The collision volume this body was actually given. Capsule fields provide a common
        // bounding representation; box metadata lets traversal query the real oriented surface.
        public float CapsuleRadius;
        public float CapsuleHalfLength;
        public RagdollColliderShape ColliderShape;
        public Vector3 BoxHalfExtents;
        // BEPU recenters convex hull vertices around their center of mass. This offset maps the
        // body's COM pose back to the anatomical bone origin used by skeleton write-back. Collision
        // and surface queries stay at the body's real center. Zero for bone-centered shapes.
        public Vector3 ShapeCenterOffset;
        // Set when the body collides with a fitted hull rather than with its box or capsule
        // envelope: the hull's corners, and its edges as pairs of points, in the body's frame.
        public Vector3[]? HullCorners;
        public Vector3[]? HullEdges;
        public BodyMeshGeometry? MeshGeometry;
        public MeshCollisionLodResult? MeshLod;
        // The mass this body was actually built with, so the joint above it can size its friction
        // against the weight it is holding rather than against a constant.
        public float Mass;
    }

    /// <summary>A ragdoll bone's collision volume in world space. The capsule axis is local Y.</summary>
    public readonly record struct BoneCapsule(
        Vector3 Center,
        Quaternion Orientation,
        float Radius,
        float HalfLength);

    // Field by field: the configuration record has the same fields, with the same defaults.
    private static RagdollBoneConfig[] ToBoneConfigs(ImmutableArray<SkeletonBoneDefinition> definitions)
    {
        var result = new RagdollBoneConfig[definitions.Length];
        for (var i = 0; i < definitions.Length; i++)
        {
            var d = definitions[i];
            result[i] = new RagdollBoneConfig
            {
                Name = d.Name,
                SkeletonParent = d.SkeletonParent,
                Enabled = d.Enabled,
                CapsuleRadius = d.CapsuleRadius,
                CapsuleHalfLength = d.CapsuleHalfLength,
                Mass = d.Mass,
                SwingLimit = d.SwingLimit,
                SwingMinLimit = d.SwingMinLimit,
                HingeRestAngle = d.HingeRestAngle,
                HingeRestSpringFreq = d.HingeRestSpringFreq,
                HingeRestMaxForce = d.HingeRestMaxForce,
                JointType = d.JointType,
                TwistMinAngle = d.TwistMinAngle,
                TwistMaxAngle = d.TwistMaxAngle,
                AnatomicalRole = d.AnatomicalRole,
                ColliderShape = d.ColliderShape,
                BoxHalfExtentX = d.BoxHalfExtentX,
                BoxHalfExtentY = d.BoxHalfExtentY,
                BoxHalfExtentZ = d.BoxHalfExtentZ,
                Description = d.Description,
                SoftBody = d.SoftBody,
                SoftSpringFreq = d.SoftSpringFreq,
                SoftSpringDamp = d.SoftSpringDamp,
                SoftServoFreq = d.SoftServoFreq,
                SoftServoDamp = d.SoftServoDamp,
            };
        }
        return result;
    }

    // Tier D — Anthropometric segment masses as fractions of total body mass (Winter, Biomechanics
    // and Motor Control of Human Movement, Table 3.1): profile data (SkeletonProfile.MassBudget),
    // keyed by the side-stripped bone name. Bones not in it (cloth, weapon, breast) keep their
    // literal Mass and are excluded from anthropometric scaling.

    // Strip a trailing side suffix for the side-free table lookups.
    private static string SideStrippedBoneName(string name) => HumanoidSkeleton.StripSide(name);

    // Resolve a bone's effective mass. With anthropometric mass on, bones present in
    // the profile's mass budget use (fraction x bodyMass); all others fall back to the
    // hand-picked def.Mass.
    private static float ResolveBoneMass(
        RagdollBoneDef def,
        IReadOnlyDictionary<string, float>? massByBone = null)
    {
        if (massByBone != null && massByBone.TryGetValue(def.Name, out var resolvedMass))
            return resolvedMass;
        return def.Mass;
    }

    // === Tier C — Anatomical range-of-motion (ROM) table ============================
    // Per-DOF ranges of motion in radians, keyed by side-stripped bone name: profile data
    // (SkeletonProfile.RangeOfMotion). Bones absent from the table fall back to their hand-set
    // boneDef twist values (and the fold-stop).
    private static float D2R(float degrees) => DegreesToRadians(degrees);

    private static bool TryGetAnatomicalRom(string boneName, out JointRom rom)
        => HumanoidSkeleton.TryGetRangeOfMotion(boneName, out rom);

    private static Dictionary<string, float> BuildMassBudget(
        RagdollBoneDef[] defs,
        IReadOnlyDictionary<string, int> presentBones,
        float bodyMass)
    {
        var budget = HumanoidSkeleton.MassBudget;
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var def in defs)
        {
            if (!presentBones.ContainsKey(def.Name))
                continue;
            if (budget.Fractions.TryGetValue(SideStrippedBoneName(def.Name), out var frac))
                result[def.Name] = frac * bodyMass;
        }

        // Winter's .006 hand value includes the digits. Keep that group total invariant whether a
        // profile uses no finger bodies or all five. With digits present, 75% stays in the palm and
        // 25% is divided between the enabled proximal bodies. This removes the former 5 kg per hand
        // without introducing mass when a profile adds or removes a finger.
        foreach (var left in new[] { true, false })
        {
            var handName = Bones.Hand.For(left);
            if (!presentBones.ContainsKey(handName))
                continue;

            var fingers = new List<string>(budget.ProximalFingers.Length);
            foreach (var finger in budget.ProximalFingers)
            {
                var name = finger.For(left);
                if (presentBones.ContainsKey(name) && defs.Any(d => d.Name == name))
                    fingers.Add(name);
            }

            var handGroupMass = budget.HandGroupFraction * bodyMass;
            if (fingers.Count == 0)
            {
                result[handName] = handGroupMass;
                continue;
            }

            result[handName] = handGroupMass * budget.PalmShare;
            var fingerMass = handGroupMass * budget.FingersShare / fingers.Count;
            foreach (var finger in fingers)
                result[finger] = fingerMass;
        }

        // The foot and the toe cluster are one anatomical foot segment. Mesh-derived thickness turns the
        // terminal toe cluster into a body, so redistribute the existing group mass instead of
        // quietly adding two more literal masses to the rig.
        foreach (var left in new[] { true, false })
        {
            var footName = Bones.Foot.For(left);
            var toeName = Bones.Toes.For(left);
            if (!presentBones.ContainsKey(footName) || !presentBones.ContainsKey(toeName) ||
                !defs.Any(d => d.Name == toeName))
                continue;

            var footGroupMass = budget.FootGroupFraction * bodyMass;
            result[footName] = footGroupMass * budget.FootShare;
            result[toeName] = footGroupMass * budget.ToesShare;
        }

        return result;
    }

    public static void FillProfileDefaults(RagdollBoneConfig bone)
    {
        if (bone.AnatomicalRole == (int)AnatomicalRole.Auto)
            bone.AnatomicalRole = (int)InferAnatomicalRole(bone.Name, bone.Description, bone.SoftBody);

        bone.SwingMinLimit ??= DefaultSwingMinLimit((AnatomicalRole)bone.AnatomicalRole, bone.Name);
        bone.HingeRestAngle ??= DefaultHingeRestAngle((AnatomicalRole)bone.AnatomicalRole, bone.Name);
        // 0 counts as "unset" for the rest spring, not "disabled": the built-in profiles store 0
        // for every bone, so treating it as null lets knee/elbow inherit the firm default (the
        // master toggle, not a per-bone 0, is what turns the feature off). Non-hinge bones keep 0
        // because their default is 0.
        if ((bone.HingeRestSpringFreq ?? 0f) <= 0f)
            bone.HingeRestSpringFreq = DefaultHingeRestSpringFreq((AnatomicalRole)bone.AnatomicalRole, bone.Name);
        if ((bone.HingeRestMaxForce ?? 0f) <= 0f)
            bone.HingeRestMaxForce = DefaultHingeRestMaxForce((AnatomicalRole)bone.AnatomicalRole, bone.Name);

        var hasBoxMetadata = bone.BoxHalfExtentX > 0 || bone.BoxHalfExtentY > 0 || bone.BoxHalfExtentZ > 0;
        if (bone.ColliderShape == 0 && !hasBoxMetadata &&
            (IsHandBone(bone.Name) || IsFootBone(bone.Name) || IsShinBone(bone.Name) || IsCalfBone(bone.Name) ||
             IsForearmBone(bone.Name) || IsUpperArmBone(bone.Name)))
            bone.ColliderShape = (int)RagdollColliderShape.Box;

        if (bone.BoxHalfExtentX <= 0 || bone.BoxHalfExtentY <= 0 || bone.BoxHalfExtentZ <= 0)
        {
            var extents = DefaultBoxHalfExtents(bone);
            if (bone.BoxHalfExtentX <= 0) bone.BoxHalfExtentX = extents.X;
            if (bone.BoxHalfExtentY <= 0) bone.BoxHalfExtentY = extents.Y;
            if (bone.BoxHalfExtentZ <= 0) bone.BoxHalfExtentZ = extents.Z;
        }
    }

    private static AnatomicalRole InferAnatomicalRole(string name, string? description, bool softBody)
    {
        if (softBody) return AnatomicalRole.SoftBody;
        // The skeleton profile names the role of its bones (SkeletonProfile.RoleRules); the values
        // of SkeletonBoneRole are those of AnatomicalRole.
        if (HumanoidSkeleton.TryGetRole(name, out var profileRole)) return (AnatomicalRole)(int)profileRole;
        if (!string.IsNullOrEmpty(description) && description.Contains("knee", StringComparison.OrdinalIgnoreCase)) return AnatomicalRole.Knee;
        if (!string.IsNullOrEmpty(description) && description.Contains("elbow", StringComparison.OrdinalIgnoreCase)) return AnatomicalRole.Elbow;
        return AnatomicalRole.Generic;
    }

    private static bool IsHandBone(string name) => BoneClasses.Hand.Matches(name);
    private static bool IsFootBone(string name) => BoneClasses.Foot.Matches(name);
    private static bool IsToeBone(string name) => BoneClasses.Toe.Matches(name);
    // The upper knee-region hinge, a short stub below the knee.
    private static bool IsShinBone(string name) => BoneClasses.Shin.Matches(name);
    // The lower knee-region hinge (see LogLegBoneStructureDiagnostics): carries most of the
    // knee-to-ankle span and, in a real death pose, bends more than the shin stub does. A real
    // dynamic body and joint, not a mesh-sampling helper.
    private static bool IsCalfBone(string name) => BoneClasses.Calf.Matches(name);
    private static bool IsForearmBone(string name) => BoneClasses.Forearm.Matches(name);
    private static bool IsUpperArmBone(string name) => BoneClasses.UpperArm.Matches(name);
    private static bool IsClavicleBone(string name) => BoneClasses.Clavicle.Matches(name);

    private static Vector3 DefaultBoxHalfExtents(RagdollBoneConfig bone)
    {
        if (IsUpperArmBone(bone.Name))
            return new Vector3(MathF.Max(0.032f, bone.CapsuleRadius * 1.15f), MathF.Max(0.075f, bone.CapsuleHalfLength), MathF.Max(0.024f, bone.CapsuleRadius * 0.85f));
        if (IsShinBone(bone.Name))
            return new Vector3(MathF.Max(0.038f, bone.CapsuleRadius * 1.1f), MathF.Max(0.03f, bone.CapsuleHalfLength), MathF.Max(0.028f, bone.CapsuleRadius * 0.85f));
        if (IsCalfBone(bone.Name))
            return new Vector3(MathF.Max(0.042f, bone.CapsuleRadius * 1.15f), MathF.Max(0.15f, bone.CapsuleHalfLength), MathF.Max(0.030f, bone.CapsuleRadius * 0.85f));
        if (IsForearmBone(bone.Name))
            return new Vector3(MathF.Max(0.030f, bone.CapsuleRadius * 1.10f), MathF.Max(0.060f, bone.CapsuleHalfLength), MathF.Max(0.022f, bone.CapsuleRadius * 0.85f));
        if (IsFootBone(bone.Name))
            return new Vector3(0.035f, MathF.Max(0.045f, bone.CapsuleHalfLength), 0.018f);
        if (IsHandBone(bone.Name))
            return new Vector3(0.025f, MathF.Max(0.035f, bone.CapsuleHalfLength), 0.014f);

        var r = MathF.Max(0.01f, bone.CapsuleRadius);
        var h = MathF.Max(0.01f, bone.CapsuleHalfLength);
        return new Vector3(r, h, r);
    }

    private static float DefaultSwingMinLimit(AnatomicalRole role, string name)
    {
        if (role == AnatomicalRole.Knee || IsShinBone(name))
            return 0.75f;
        if (role == AnatomicalRole.Elbow || IsForearmBone(name))
            return 0.45f;

        return 0f;
    }

    private static float DefaultHingeRestAngle(AnatomicalRole role, string name)
    {
        return 0f;
    }

    // Knee/elbow passive-rest defaults sit well below the slider maxima on purpose. The spring
    // competes against joint friction and ground contact, so it wants to be firm — but the maxima
    // (30 Hz / 500 N) are a divergence hazard: a spring that stiff fighting the grab servo over a
    // knee blew the solver up to NaN in testing. 10 Hz / 200 N is far stronger than the old
    // 3.5 / 50 (enough to straighten a supine knee) with headroom before instability. Only active
    // while RagdollAnatomicalHingeRestBias is on.
    private static float DefaultHingeRestSpringFreq(AnatomicalRole role, string name)
    {
        if (role == AnatomicalRole.Knee || IsShinBone(name))
            return 10.0f;
        if (role == AnatomicalRole.Elbow || IsForearmBone(name))
            return 10.0f;
        return 0f;
    }

    private static float DefaultHingeRestMaxForce(AnatomicalRole role, string name)
    {
        if (role == AnatomicalRole.Knee || IsShinBone(name))
            return 200.0f;
        if (role == AnatomicalRole.Elbow || IsForearmBone(name))
            return 200.0f;
        return 0f;
    }

    private static bool HasPassiveHingeRest(AnatomicalRole role, string name)
    {
        return role == AnatomicalRole.Knee || role == AnatomicalRole.Elbow ||
               IsShinBone(name) ||
               IsForearmBone(name);
    }

    private static RagdollBoneDef[] BuildDefaultBoneDefs()
    {
        return BuildBoneDefsFromConfigs(AllBoneDefaults);
    }

    /// <summary>
    /// Convert config list to RagdollBoneDef[], filtering to enabled bones and
    /// computing physics parents (nearest enabled ancestor in skeleton tree).
    /// </summary>
    public static RagdollBoneDef[] BuildBoneDefsFromConfigs(
        RagdollBoneConfig[] configs,
        IReadOnlySet<string>? forceEnabled = null)
    {
        // Build lookup of known skeleton parents from AllBoneDefaults (fallback)
        var defaultParents = new Dictionary<string, string?>();
        foreach (var d in AllBoneDefaults)
            defaultParents[d.Name] = d.SkeletonParent;

        var enabledNames = new HashSet<string>();
        foreach (var c in configs)
            if (!IsToeBone(c.Name) &&
                (c.Enabled || forceEnabled?.Contains(c.Name) == true))
                enabledNames.Add(c.Name);

        var configByName = new Dictionary<string, RagdollBoneConfig>();
        foreach (var c in configs)
            configByName[c.Name] = c;

        var result = new List<RagdollBoneDef>();
        foreach (var c in configs)
        {
            // j_asi_e is never a ragdoll body — not from a saved per-bone profile's Enabled
            // flag, not from mesh-aware mode. Neither GUI path may bring it back.
            if (IsToeBone(c.Name)) continue;
            if (!c.Enabled && forceEnabled?.Contains(c.Name) != true) continue;
            FillProfileDefaults(c);

            // Walk up skeleton tree to find nearest enabled parent.
            // Use SkeletonParent from config, fall back to AllBoneDefaults if null.
            string? physicsParent = null;
            var skelParent = c.SkeletonParent;
            if (skelParent == null) defaultParents.TryGetValue(c.Name, out skelParent);
            var current = skelParent;
            while (current != null)
            {
                if (enabledNames.Contains(current))
                {
                    physicsParent = current;
                    break;
                }
                // Walk further up: check config, then default
                string? nextParent = null;
                if (configByName.TryGetValue(current, out var parentConfig))
                    nextParent = parentConfig.SkeletonParent;
                if (nextParent == null)
                    defaultParents.TryGetValue(current, out nextParent);
                current = nextParent;
            }

            result.Add(new RagdollBoneDef
            {
                Name = c.Name,
                ParentName = physicsParent,
                CapsuleRadius = c.CapsuleRadius,
                CapsuleHalfLength = c.CapsuleHalfLength,
                Mass = c.Mass,
                SwingLimit = c.SwingLimit,
                SwingMinLimit = c.SwingMinLimit ?? 0f,
                HingeRestAngle = c.HingeRestAngle ?? 0f,
                HingeRestSpringFreq = c.HingeRestSpringFreq ?? 0f,
                HingeRestMaxForce = c.HingeRestMaxForce ?? 0f,
                Joint = IsShinBone(c.Name) || IsCalfBone(c.Name) || IsForearmBone(c.Name) || IsToeBone(c.Name)
                    ? JointType.Hinge
                    : (JointType)c.JointType,
                TwistMinAngle = c.TwistMinAngle,
                TwistMaxAngle = c.TwistMaxAngle,
                AnatomicalRole = (AnatomicalRole)c.AnatomicalRole,
                ColliderShape = (RagdollColliderShape)c.ColliderShape,
                BoxHalfExtents = new Vector3(c.BoxHalfExtentX, c.BoxHalfExtentY, c.BoxHalfExtentZ),
                SoftBody = c.SoftBody,
                SoftSpringFreq = c.SoftSpringFreq,
                SoftSpringDamp = c.SoftSpringDamp,
                SoftServoFreq = c.SoftServoFreq,
                SoftServoDamp = c.SoftServoDamp,
            });
        }
        return result.ToArray();
    }
}
