// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Core;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private sealed class PartialBinding
    {
        public int Index, Offset = -1, Count, Connected = -1, Parent = -1;
        public nint Pose, Rig;
        public string[] Names = Array.Empty<string>();
    }
    private PartialBinding[] partialBindings = Array.Empty<PartialBinding>();
    private readonly Dictionary<string, int> surfaceBones = new(StringComparer.Ordinal);
    private string[] surfaceBoneNames = Array.Empty<string>();

    private bool PartialsMatch(SkeletonAccess ns)
    {
        var skeleton = ns.CharBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount != partialBindings.Length) return false;
        foreach (var binding in partialBindings)
        {
            var partial = &skeleton->PartialSkeletons[binding.Index];
            var pose = partial->GetHavokPose(0);
            if ((nint)pose != binding.Pose || (pose == null ? 0 : (nint)pose->Skeleton) != binding.Rig ||
                (pose == null ? 0 : pose->ModelPose.Length) != binding.Count ||
                partial->ConnectedBoneIndex != binding.Connected || partial->ConnectedParentBoneIndex != binding.Parent) return false;
        }
        return true;
    }

    private bool BuildPartialReferences(SkeletonAccess ns)
    {
        surfaceBones.Clear();
        var skeleton = ns.CharBase->Skeleton;
        int count = skeleton->PartialSkeletonCount;
        if (count < 1 || count > 32 || skeleton->PartialSkeletons == null) return false;
        partialBindings = new PartialBinding[count];
        var references = new Matrix4x4[MaxBones];
        var names = new List<string>();
        boneCount = 0;
        for (int pi = 0; pi < count; pi++)
        {
            var partial = &skeleton->PartialSkeletons[pi];
            var pose = partial->GetHavokPose(0);
            var binding = new PartialBinding { Index = pi, Pose = (nint)pose,
                Rig = pose == null ? 0 : (nint)pose->Skeleton, Count = pose == null ? 0 : pose->ModelPose.Length,
                Connected = partial->ConnectedBoneIndex, Parent = partial->ConnectedParentBoneIndex };
            partialBindings[pi] = binding;
            if (pose == null || pose->Skeleton == null) { if (pi == 0) return false; continue; }
            var rig = pose->Skeleton;
            int n = binding.Count;
            if (n < 1 || n > 512 || n + boneCount > MaxBones || rig->Bones.Length < n ||
                rig->ReferencePose.Length < n || rig->ParentIndices.Length < n) { if (pi == 0) return false; continue; }
            var rest = new Matrix4x4[n];
            binding.Names = new string[n];
            bool supported = true;
            for (int bi = 0; bi < n; bi++)
            {
                int parent = rig->ParentIndices[bi];
                if (parent < -1 || parent >= bi) { supported = false; break; }
                rest[bi] = ToMatrix(rig->ReferencePose.Data[bi]);
                if (parent >= 0) rest[bi] *= rest[parent];
                binding.Names[bi] = rig->Bones[bi].Name.String ?? string.Empty;
            }
            Matrix4x4 attachment = Matrix4x4.Identity;
            if (pi > 0)
            {
                // The connection must match the actual loaded base rig by name. No inferred offsets.
                var baseRig = partialBindings[0];
                int child = binding.Connected, parent = binding.Parent;
                if (child < 0 || child >= n || parent < 0 || parent >= baseRig.Count ||
                    binding.Names[child] != baseRig.Names[parent] || !Matrix4x4.Invert(rest[child], out var inverseConnection)) supported = false;
                else attachment = inverseConnection * references[parent];
            }
            if (!supported) { if (pi == 0) return false; continue; }
            int offset = boneCount;
            for (int bi = 0; bi < n; bi++)
            {
                references[offset + bi] = rest[bi] * attachment;
                if (!Matrix4x4.Invert(references[offset + bi], out inverseBind[offset + bi])) { supported = false; break; }
            }
            if (!supported) { if (pi == 0) return false; continue; }
            binding.Offset = offset; boneCount += n;
            for (int bi = 0; bi < n; bi++)
            {
                string name = binding.Names[bi]; names.Add(name);
                if (name.Length == 0) continue;
                if (!surfaceBones.TryGetValue(name, out int old)) surfaceBones[name] = offset + bi;
                // The explicitly connected root is the base bone, so it is a safe alias.
                else if (!(pi > 0 && bi == binding.Connected && old == binding.Parent)) surfaceBones[name] = -1;
            }
            Services.Log.Info($"Body fluid partial={pi} bones={n} offset={offset} connected={binding.Connected} parent={binding.Parent} modelSync={pose->ModelInSync}");
        }
        surfaceBoneNames = names.ToArray();
        return boneCount > 0;
    }

    private int ResolveSurfaceBone(string name) => surfaceBones.TryGetValue(name, out int index) ? index : -1;

    private bool CapturePartialTransforms(SkeletonAccess ns, Matrix4x4 root, bool continuous)
    {
        var skeleton = ns.CharBase->Skeleton;
        foreach (var binding in partialBindings)
        {
            if (binding.Offset < 0) continue;
            // Obtain pointers again from this frame's validated draw object. Do not dereference identities.
            var pose = skeleton->PartialSkeletons[binding.Index].GetHavokPose(0);
            if (pose == null || pose->ModelInSync == 0 || pose->ModelPose.Length != binding.Count)
            { CapturePoseFailureReason = $"Partial {binding.Index} missing, unsynchronized, or bone count changed"; return false; }
            Matrix4x4 attachment = Matrix4x4.Identity;
            if (binding.Index > 0)
            {
                var basePose = skeleton->PartialSkeletons[0].GetHavokPose(0);
                if (!Matrix4x4.Invert(ToMatrix(pose->ModelPose.Data[binding.Connected]), out var inverseConnection))
                { CapturePoseFailureReason = $"Partial {binding.Index} connected transform not invertible"; return false; }
                attachment = inverseConnection * ToMatrix(basePose->ModelPose.Data[binding.Parent]);
            }
            for (int bi = 0; bi < binding.Count; bi++)
            {
                int index = binding.Offset + bi;
                previousSkinWorld[index] = skinWorld[index];
                boneWorld[index] = ToMatrix(pose->ModelPose.Data[bi]) * attachment * root;
                skinWorld[index] = inverseBind[index] * boneWorld[index];
                if (!Finite(new Vector3(boneWorld[index].M41, boneWorld[index].M42, boneWorld[index].M43)))
                { CapturePoseFailureReason = $"Partial {binding.Index} bone {bi} has nonfinite world transform"; return false; }
                if (!continuous) previousSkinWorld[index] = skinWorld[index];
            }
        }
        return true;
    }
}
