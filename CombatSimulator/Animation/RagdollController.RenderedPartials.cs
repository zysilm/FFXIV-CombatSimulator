// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private readonly record struct RenderedPartialStamp(int Index, nint Pose, nint Havok, nint Resource, short Connected, short Parent, bool Readable);
    private readonly List<RenderedPartialStamp> renderedPartialStamps = new();
    private int renderedPartialCount;

    private bool RenderedPartialSourcesMatch(SkeletonAccess skel)
    {
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton->PartialSkeletonCount != renderedPartialCount || skeleton->PartialSkeletons == null) return false;
        foreach (var stamp in renderedPartialStamps)
        {
            var partial = &skeleton->PartialSkeletons[stamp.Index];
            var pose = partial->GetHavokPose(0);
            var readable = pose != null && pose->Skeleton != null && pose->ModelInSync != 0 &&
                           pose->ModelPose.Data != null && pose->Skeleton->ReferencePose.Data != null;
            if ((nint)pose != stamp.Pose || (pose == null ? 0 : (nint)pose->Skeleton) != stamp.Havok ||
                (nint)partial->SkeletonResourceHandle != stamp.Resource || partial->ConnectedBoneIndex != stamp.Connected ||
                partial->ConnectedParentBoneIndex != stamp.Parent || (!stamp.Readable && readable)) return false;
        }
        return true;
    }

    private void BuildRenderedSkinPalette(SkeletonAccess skel, List<string> diagnostics, ref Matrix4x4[] skin,
        out string[] names, out int[] parents, out int[] owners, out Dictionary<string, int> byName)
    {
        var nameList = new List<string>(); var parentList = new List<int>(); var ownerList = new List<int>();
        var matrices = new List<Matrix4x4>(skin);
        byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < skin.Length; i++)
        {
            string name = skel.HavokSkeleton->Bones[i].Name.String ?? string.Empty;
            nameList.Add(name); parentList.Add(skel.HavokSkeleton->ParentIndices[i]); ownerList.Add(i);
            if (name.Length != 0) byName.TryAdd(name, i);
        }
        int primaryCount = skin.Length;
        var skeleton = skel.CharBase->Skeleton;
        renderedPartialCount = skeleton->PartialSkeletonCount;
        if (TryBuildReferenceModelTransforms(skel, out var primaryReference) && skeleton->PartialSkeletons != null)
        {
            for (int ps = 1; ps < Math.Min(32, (int)skeleton->PartialSkeletonCount); ps++)
            {
                var partial = &skeleton->PartialSkeletons[ps]; var pose = partial->GetHavokPose(0);
                bool readable = pose != null && pose->Skeleton != null && pose->ModelInSync != 0 &&
                                pose->ModelPose.Data != null && pose->Skeleton->ReferencePose.Data != null;
                renderedPartialStamps.Add(new(ps, (nint)pose, pose == null ? 0 : (nint)pose->Skeleton,
                    (nint)partial->SkeletonResourceHandle, partial->ConnectedBoneIndex, partial->ConnectedParentBoneIndex, readable));
                int connected = partial->ConnectedBoneIndex, anchor = partial->ConnectedParentBoneIndex;
                if (!readable || anchor < 0 || anchor >= primaryReference.Length || anchor >= primaryCount)
                { diagnostics.Add($"partial={ps}: unreadable pose or unsupported primary connection; weighted faces omitted."); continue; }
                var havok = pose->Skeleton;
                int count = Math.Min(havok->ReferencePose.Length, Math.Min(pose->ModelPose.Length, Math.Min(havok->ParentIndices.Length, havok->Bones.Length)));
                if (count is < 1 or > 4096 || connected < 0 || connected >= count || matrices.Count + count > 8192)
                { diagnostics.Add($"partial={ps}: invalid or over-budget skeleton; weighted faces omitted."); continue; }
                string connectionName = havok->Bones[connected].Name.String ?? string.Empty;
                if (connectionName.Length == 0 || connectionName != nameList[anchor])
                { diagnostics.Add($"partial={ps}: connection name '{connectionName}' does not match primary '{nameList[anchor]}'; weighted faces omitted."); continue; }
                var reference = new Matrix4x4[count]; var current = new Matrix4x4[count]; var localParents = new int[count];
                bool valid = true;
                for (int i = 0; i < count; i++)
                {
                    int parent = havok->ParentIndices[i]; localParents[i] = parent;
                    // Native Havok reference arrays normally use parent-before-child order.
                    // Decline an unsupported hierarchy rather than silently misaligning it.
                    if (parent >= i || parent < -1) { valid = false; break; }
                    reference[i] = QsToMatrix(havok->ReferencePose.Data[i]);
                    if (parent >= 0) reference[i] *= reference[parent];
                    current[i] = QsToMatrix(pose->ModelPose.Data[i]);
                }
                if (!valid || !RenderedPartialGeometry.TrySkinMatrices(reference, current, connected, primaryReference[anchor],
                    QsToMatrix(skel.Pose->ModelPose.Data[anchor]), out var partialSkin))
                { diagnostics.Add($"partial={ps}: invalid reference/current connection transforms; weighted faces omitted."); continue; }
                int start = matrices.Count;
                for (int i = 0; i < count; i++)
                {
                    string name = havok->Bones[i].Name.String ?? string.Empty;
                    int owner = -1, ancestor = i;
                    while (ancestor >= 0 && ancestor < count)
                    {
                        string candidate = havok->Bones[ancestor].Name.String ?? string.Empty;
                        if (byName.TryGetValue(candidate, out int primary) && primary >= 0 && primary < primaryCount) { owner = primary; break; }
                        ancestor = localParents[ancestor];
                    }
                    if (owner < 0) owner = anchor;
                    nameList.Add(name); parentList.Add(localParents[i] < 0 ? anchor : start + localParents[i]);
                    ownerList.Add(owner); matrices.Add(partialSkin[i]);
                    if (name.Length == 0) continue;
                    if (!byName.TryAdd(name, start + i) && byName[name] >= primaryCount)
                        byName[name] = -1; // Ambiguous names across partials require a native palette binding.
                }
                diagnostics.Add($"partial={ps}: captured {count} connection-aligned bones; ownership anchored to primary bone {anchor}, partial pose preserved in vertex contributions.");
            }
        }
        names = nameList.ToArray(); parents = parentList.ToArray(); owners = ownerList.ToArray(); skin = matrices.ToArray();
    }
}
