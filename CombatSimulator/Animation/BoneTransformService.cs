using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Animation;

/// <summary>
/// Shared service for manipulating bone transforms on characters.
/// Owns the render hook and provides reusable APIs for applying rotation/translation
/// deltas to ModelPose with proper descendant propagation and partial skeleton handling.
/// </summary>
public unsafe class BoneTransformService : IDisposable
{
    private readonly IPluginLog log;

    private delegate nint RenderDelegate(nint a1, nint a2, nint a3, int a4);
    private Hook<RenderDelegate>? renderHook;

    /// <summary>Fired each frame during the render hook. Consumers subscribe here to apply bone modifications.</summary>
    public event Action? OnRenderFrame;

    public BoneTransformService(IGameInteropProvider gameInterop, ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;

        try
        {
            var addr = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 48 81 C3 ?? ?? ?? ?? BF ?? ?? ?? ?? 33 ED");
            renderHook = gameInterop.HookFromAddress<RenderDelegate>(addr, RenderDetour);
            renderHook.Enable();
            log.Info($"BoneTransformService: Render hook at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "BoneTransformService: Failed to create render hook.");
        }
    }

    private nint RenderDetour(nint a1, nint a2, nint a3, int a4)
    {
        try
        {
            OnRenderFrame?.Invoke();
        }
        catch (Exception ex)
        {
            log.Error(ex, "BoneTransformService: Error in render frame callback");
        }

        return renderHook!.Original(a1, a2, a3, a4);
    }

    /// <summary>
    /// Get the body skeleton pose for a character. Returns null if unavailable.
    /// </summary>
    public SkeletonAccess? TryGetSkeleton(nint characterAddress)
    {
        if (characterAddress == nint.Zero) return null;

        var gameObj = (GameObject*)characterAddress;
        if (gameObj->DrawObject == null) return null;

        var charBase = (CharacterBase*)gameObj->DrawObject;
        var skeleton = charBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return null;

        var partial = &skeleton->PartialSkeletons[0];
        var pose = partial->GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) return null;
        if (pose->ModelInSync == 0) return null;

        var havokSkel = pose->Skeleton;
        var boneCount = pose->LocalPose.Length;
        if (boneCount != pose->ModelPose.Length) return null;

        return new SkeletonAccess
        {
            CharBase = charBase,
            Pose = pose,
            HavokSkeleton = havokSkel,
            BoneCount = boneCount,
            ParentCount = havokSkel->ParentIndices.Length,
        };
    }

    /// <summary>
    /// Apply bone rotation deltas to ModelPose with descendant propagation.
    /// Preserves physics by never touching LocalPose.
    /// Returns modification result for further processing (head follow, partial skeleton propagation).
    /// </summary>
    public BoneModificationResult ApplyRotationDeltas(
        SkeletonAccess skel,
        Dictionary<int, Quaternion> deltas,
        HashSet<int>? skipBones = null)
    {
        var pose = skel.Pose;
        var havokSkel = skel.HavokSkeleton;
        var boneCount = skel.BoneCount;
        var parentCount = skel.ParentCount;

        var result = new BoneModificationResult(boneCount);

        // Save original ModelPose before any modifications
        for (int i = 0; i < boneCount; i++)
        {
            ref var m = ref pose->ModelPose.Data[i];
            result.OriginalPositions[i] = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            result.OriginalRotations[i] = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W);
        }

        for (int i = 0; i < boneCount && i < parentCount; i++)
        {
            if (skipBones != null && skipBones.Contains(i)) continue;

            var parentIdx = (i > 0) ? havokSkel->ParentIndices[i] : (short)-1;
            bool hasDirect = deltas.TryGetValue(i, out var directDelta);
            bool parentHasAcc = parentIdx >= 0 && parentIdx < boneCount && result.HasAccumulated[parentIdx];

            if (!hasDirect && !parentHasAcc) continue;

            var newRot = result.OriginalRotations[i];
            var newPos = result.OriginalPositions[i];

            // Propagate parent's accumulated delta (rotate around parent's ORIGINAL pivot)
            if (parentHasAcc)
            {
                var parentOrigPos = result.OriginalPositions[parentIdx];
                var pDelta = result.AccumulatedDeltas[parentIdx];

                var relPos = result.OriginalPositions[i] - parentOrigPos;
                relPos = Vector3.Transform(relPos, pDelta);
                newPos = parentOrigPos + relPos;

                // Add parent's actual displacement
                ref var parentModel = ref pose->ModelPose.Data[parentIdx];
                var parentNewPos = new Vector3(parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z);
                newPos += parentNewPos - parentOrigPos;

                newRot = Quaternion.Normalize(pDelta * newRot);
            }

            // Apply direct delta (local-space, right-multiply)
            if (hasDirect)
                newRot = Quaternion.Normalize(newRot * directDelta);

            result.AccumulatedDeltas[i] = Quaternion.Normalize(newRot * Quaternion.Inverse(result.OriginalRotations[i]));
            result.HasAccumulated[i] = true;

            // Write back to ModelPose
            ref var model = ref pose->ModelPose.Data[i];
            model.Translation.X = newPos.X;
            model.Translation.Y = newPos.Y;
            model.Translation.Z = newPos.Z;
            model.Rotation.X = newRot.X;
            model.Rotation.Y = newRot.Y;
            model.Rotation.Z = newRot.Z;
            model.Rotation.W = newRot.W;
        }

        return result;
    }

    /// <summary>
    /// Write a bone's ModelPose transform directly and update accumulated delta tracking.
    /// Use this for custom bone handling (e.g., head follow modes).
    /// </summary>
    public void WriteBoneTransform(
        SkeletonAccess skel,
        int boneIndex,
        Vector3 newPos,
        Quaternion newRot,
        BoneModificationResult result)
    {
        ref var model = ref skel.Pose->ModelPose.Data[boneIndex];
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
    /// Propagate a connection bone's changes to all partial skeletons whose root matches the given bone name.
    /// Required for bones like j_kao (head) that are connection points between body and face skeletons.
    /// </summary>
    public void PropagateToPartialSkeletons(
        SkeletonAccess skel,
        int boneIndex,
        string boneName,
        BoneModificationResult result)
    {
        if (!result.HasAccumulated[boneIndex]) return;

        var skeleton = skel.CharBase->Skeleton;
        var delta = result.AccumulatedDeltas[boneIndex];
        ref var boneModel = ref skel.Pose->ModelPose.Data[boneIndex];
        var displacement = new Vector3(boneModel.Translation.X, boneModel.Translation.Y, boneModel.Translation.Z)
                           - result.OriginalPositions[boneIndex];
        var origBonePos = result.OriginalPositions[boneIndex];

        for (int ps = 1; ps < skeleton->PartialSkeletonCount; ps++)
        {
            var otherPartial = &skeleton->PartialSkeletons[ps];
            var otherPose = otherPartial->GetHavokPose(0);
            if (otherPose == null || otherPose->Skeleton == null) continue;
            if (otherPose->ModelInSync == 0) continue;

            var otherBoneCount = otherPose->ModelPose.Length;
            if (otherBoneCount < 1) continue;

            var rootName = otherPose->Skeleton->Bones[0].Name.String;
            if (rootName != boneName) continue;

            var otherParentCount = otherPose->Skeleton->ParentIndices.Length;
            for (int b = 0; b < otherBoneCount && b < otherParentCount; b++)
            {
                ref var bm = ref otherPose->ModelPose.Data[b];
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

    /// <summary>Resolve a bone index by name. Returns -1 if not found.</summary>
    public int ResolveBoneIndex(SkeletonAccess skel, string boneName)
    {
        var bones = skel.HavokSkeleton->Bones;
        var count = Math.Min(skel.BoneCount, bones.Length);
        for (int i = 0; i < count; i++)
        {
            var name = bones[i].Name.String;
            if (name == boneName) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        OnRenderFrame = null;
        renderHook?.Dispose();
    }
}

/// <summary>Cached skeleton access pointers. Valid only within the current render frame.</summary>
public unsafe struct SkeletonAccess
{
    public CharacterBase* CharBase;
    public FFXIVClientStructs.Havok.Animation.Rig.hkaPose* Pose;
    public FFXIVClientStructs.Havok.Animation.Rig.hkaSkeleton* HavokSkeleton;
    public int BoneCount;
    public int ParentCount;
}

/// <summary>Result of applying bone modifications. Contains originals and accumulated deltas for further processing.</summary>
public class BoneModificationResult
{
    public readonly Vector3[] OriginalPositions;
    public readonly Quaternion[] OriginalRotations;
    public readonly Quaternion[] AccumulatedDeltas;
    public readonly bool[] HasAccumulated;

    public BoneModificationResult(int boneCount)
    {
        OriginalPositions = new Vector3[boneCount];
        OriginalRotations = new Quaternion[boneCount];
        AccumulatedDeltas = new Quaternion[boneCount];
        HasAccumulated = new bool[boneCount];
    }
}
