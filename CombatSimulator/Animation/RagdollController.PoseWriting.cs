// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    // Collapse the given limb's entire bone subtree to ~0 scale so the limb vanishes from the body.
    private void HideLimbSubtree(ISkeletonPose skel, string rootName)
    {
        if (string.IsNullOrEmpty(rootName)) return;

        var rootIdx = ResolveBoneIndex(skel, rootName);
        if (rootIdx < 0)
        {
            foreach (var rb in ragdollBones)
                if (rb.Name == rootName) { rootIdx = rb.BoneIndex; break; }
        }
        if (rootIdx < 0) return;

        var pose = skel.ModelPose;
        // Severance point = the root bone's model-space position (its joint). Collapse every subtree
        // bone to BOTH ~0 scale AND that single point, so the limb has no length and no leftover
        // sliver — it fully vanishes into the joint instead of compressing into a thin model.
        ref var rootM = ref pose[rootIdx];
        var rx = rootM.Translation.X;
        var ry = rootM.Translation.Y;
        var rz = rootM.Translation.Z;

        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        for (int i = 0; i < n; i++)
        {
            if (!IsDescendantOrSelf(skel, i, rootIdx)) continue;
            ref var m = ref pose[i];
            m.Translation.X = rx;
            m.Translation.Y = ry;
            m.Translation.Z = rz;
            m.Scale.X = 0.0001f;
            m.Scale.Y = 0.0001f;
            m.Scale.Z = 0.0001f;
        }

        // Face/hair (and similar) live on SEPARATE partial skeletons attached to a body bone, so they
        // are not in the main parent chain above — collapse those too or the head stays visible.
        HideSubtreePartialSkeletons(skel, rootIdx, new Vector3(rx, ry, rz));
    }

    private void HideSubtreePartialSkeletons(ISkeletonPose skel, int rootIdx, Vector3 collapsePoint)
    {
        if (!skel.HasRoot) return;

        for (int ps = 1; ps < skel.PartCount; ps++)
        {
            if (!skel.PartAvailable(ps) || !skel.PartInSync(ps)) continue;

            var pose = skel.PartModelPose(ps);
            var cnt = pose.Length;
            if (cnt < 1) continue;

            // This partial attaches to its root bone (e.g. the head); collapse it only if that bone is
            // inside the hidden subtree.
            var rootName = skel.PartBoneName(ps, 0);
            if (string.IsNullOrEmpty(rootName)) continue;
            var mainIdx = ResolveBoneIndex(skel, rootName);
            if (mainIdx < 0 || !IsDescendantOrSelf(skel, mainIdx, rootIdx)) continue;

            for (int b = 0; b < cnt; b++)
            {
                ref var m = ref pose[b];
                m.Translation.X = collapsePoint.X;
                m.Translation.Y = collapsePoint.Y;
                m.Translation.Z = collapsePoint.Z;
                m.Scale.X = 0.0001f;
                m.Scale.Y = 0.0001f;
                m.Scale.Z = 0.0001f;
            }
        }
    }

    private static bool IsDescendantOrSelf(ISkeletonPose skel, int bone, int root)
    {
        var guard = 0;
        while (bone >= 0 && guard++ < 256)
        {
            if (bone == root) return true;
            bone = skel.Parent(bone);
        }
        return false;
    }

    /// <summary>Index of a body bone by name, or -1 (what BoneTransformService.ResolveBoneIndex does).</summary>
    internal static int ResolveBoneIndex(ISkeletonPose skel, string boneName)
    {
        var count = Math.Min(skel.BoneCount, skel.BoneNameCount);
        for (int i = 0; i < count; i++)
        {
            var name = skel.BoneName(i);
            if (name == boneName) return i;
        }
        return -1;
    }

    /// <summary>
    /// Writes a bone's model-space transform and records the delta (what BoneTransformService.WriteBoneTransform
    /// does to the game's memory, here on any skeleton).
    /// </summary>
    private static void WriteBoneTransform(
        ISkeletonPose skel,
        int boneIndex,
        Vector3 newPos,
        Quaternion newRot,
        BoneModificationResult result)
    {
        if (boneIndex < 0 ||
            boneIndex >= skel.BoneCount ||
            boneIndex >= result.OriginalRotations.Length ||
            boneIndex >= result.AccumulatedDeltas.Length ||
            boneIndex >= result.HasAccumulated.Length)
            return;

        ref var model = ref skel.ModelPose[boneIndex];
        model.Translation.X = newPos.X;
        model.Translation.Y = newPos.Y;
        model.Translation.Z = newPos.Z;
        model.Rotation.X = newRot.X;
        model.Rotation.Y = newRot.Y;
        model.Rotation.Z = newRot.Z;
        model.Rotation.W = newRot.W;

        result.AccumulatedDeltas[boneIndex] = Quaternion.Normalize(
            newRot * Quaternion.Inverse(result.OriginalRotations[boneIndex]));
        result.HasAccumulated[boneIndex] = true;
    }

    /// <summary>
    /// Carries a connection bone's change over to every partial skeleton rooted at that bone (what
    /// BoneTransformService.PropagateToPartialSkeletons does to the game's memory, here on any skeleton).
    /// </summary>
    private static void PropagateToPartialSkeletons(
        ISkeletonPose skel,
        int boneIndex,
        string boneName,
        BoneModificationResult result)
    {
        if (!result.HasAccumulated[boneIndex]) return;

        var delta = result.AccumulatedDeltas[boneIndex];
        ref var boneModel = ref skel.ModelPose[boneIndex];
        var displacement = new Vector3(boneModel.Translation.X, boneModel.Translation.Y, boneModel.Translation.Z)
                           - result.OriginalPositions[boneIndex];
        var origBonePos = result.OriginalPositions[boneIndex];

        for (int ps = 1; ps < skel.PartCount; ps++)
        {
            if (!skel.PartAvailable(ps)) continue;
            if (!skel.PartInSync(ps)) continue;

            var otherPose = skel.PartModelPose(ps);
            var otherBoneCount = otherPose.Length;
            if (otherBoneCount < 1) continue;

            var rootName = skel.PartBoneName(ps, 0);
            if (rootName != boneName) continue;

            var otherParentCount = skel.PartParentCount(ps);
            for (int b = 0; b < otherBoneCount && b < otherParentCount; b++)
            {
                ref var bm = ref otherPose[b];
                var bOldPos = new Vector3(bm.Translation.X, bm.Translation.Y, bm.Translation.Z);
                var bOldRot = new Quaternion(bm.Rotation.X, bm.Rotation.Y, bm.Rotation.Z, bm.Rotation.W);

                var relToRoot = bOldPos - origBonePos;
                relToRoot = Vector3.Transform(relToRoot, delta);
                var bNewPos = origBonePos + relToRoot + displacement;
                var bNewRot = Quaternion.Normalize(delta * bOldRot);

                bm.Translation.X = bNewPos.X;
                bm.Translation.Y = bNewPos.Y;
                bm.Translation.Z = bNewPos.Z;
                bm.Rotation.X = bNewRot.X;
                bm.Rotation.Y = bNewRot.Y;
                bm.Rotation.Z = bNewRot.Z;
                bm.Rotation.W = bNewRot.W;
            }
        }
    }
}
