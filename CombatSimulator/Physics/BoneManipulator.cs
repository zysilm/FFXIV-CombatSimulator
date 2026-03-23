using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;

namespace CombatSimulator.Physics;

/// <summary>
/// Low-level bone transform read/write. Hooks the render manager function
/// (same approach as CustomizePlus) so bone writes work in normal gameplay, not just GPose.
/// Applies rotation deltas to model-space transforms each frame before rendering.
/// </summary>
public unsafe class BoneManipulator : IDisposable
{
    private readonly IPluginLog log;

    // Render hook — fires every frame before rendering (same as CustomizePlus)
    // Signature: "E8 ?? ?? ?? ?? 48 81 C3 ?? ?? ?? ?? BF ?? ?? ?? ?? 33 ED"
    private delegate nint RenderDelegate(nint a1, nint a2, nint a3, int a4);
    private Hook<RenderDelegate>? renderHook;

    // Pending bone overrides to apply in the hook
    private readonly object overrideLock = new();
    private readonly Dictionary<nint, BoneOverrideSet> pendingOverrides = new();

    // Diagnostics
    private int renderHookCallCount;
    private int overridesAppliedCount;
    private int lastAppliedBoneCount;
    private string lastSkipReason = "";

    public bool IsHooked => renderHook != null;
    public int DiagRenderCalls => renderHookCallCount;
    public int DiagOverridesApplied => overridesAppliedCount;
    public int DiagLastBoneCount => lastAppliedBoneCount;
    public string DiagLastSkipReason => lastSkipReason;

    public BoneManipulator(IGameInteropProvider gameInterop, ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;

        try
        {
            var addr = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 48 81 C3 ?? ?? ?? ?? BF ?? ?? ?? ?? 33 ED");
            renderHook = gameInterop.HookFromAddress<RenderDelegate>(addr, RenderDetour);
            renderHook.Enable();
            log.Info($"BoneManipulator: Render hook established at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "BoneManipulator: Could not hook render function — ragdoll will be unavailable.");
        }
    }

    private nint RenderDetour(nint a1, nint a2, nint a3, int a4)
    {
        renderHookCallCount++;
        try
        {
            ApplyPendingOverrides();
        }
        catch (Exception ex)
        {
            log.Error(ex, "BoneManipulator: Error applying bone overrides in render hook.");
        }

        return renderHook!.Original(a1, a2, a3, a4);
    }

    /// <summary>
    /// Set bone rotation overrides for a character. Called from RagdollController each frame.
    /// Rotations are delta quaternions multiplied onto the current model-space rotation.
    /// </summary>
    public void SetBoneOverrides(nint characterAddress, BoneOverrideSet overrides)
    {
        lock (overrideLock)
        {
            pendingOverrides[characterAddress] = overrides;
        }
    }

    /// <summary>
    /// Remove all bone overrides for a character.
    /// </summary>
    public void ClearBoneOverrides(nint characterAddress)
    {
        lock (overrideLock)
        {
            pendingOverrides.Remove(characterAddress);
        }
    }

    /// <summary>
    /// Clear all overrides (e.g. on simulation stop).
    /// </summary>
    public void ClearAllOverrides()
    {
        lock (overrideLock)
        {
            pendingOverrides.Clear();
        }
    }

    private void ApplyPendingOverrides()
    {
        Dictionary<nint, BoneOverrideSet> snapshot;
        lock (overrideLock)
        {
            if (pendingOverrides.Count == 0) return;
            snapshot = new Dictionary<nint, BoneOverrideSet>(pendingOverrides);
        }

        foreach (var (charAddr, overrideSet) in snapshot)
        {
            try
            {
                ApplyToCharacter(charAddr, overrideSet);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"BoneManipulator: Failed to apply overrides to character 0x{charAddr:X}");
            }
        }
    }

    private void ApplyToCharacter(nint charAddr, BoneOverrideSet overrideSet)
    {
        var gameObj = (GameObject*)charAddr;
        if (gameObj->DrawObject == null) { lastSkipReason = "DrawObject null"; return; }

        var charBase = (CharacterBase*)gameObj->DrawObject;
        var skeleton = charBase->Skeleton;
        if (skeleton == null) { lastSkipReason = "Skeleton null"; return; }

        // Only operate on partial skeleton 0 (main body)
        if (skeleton->PartialSkeletonCount < 1) { lastSkipReason = "No partial skeletons"; return; }
        var partial = &skeleton->PartialSkeletons[0];
        var pose = partial->GetHavokPose(0);
        if (pose == null) { lastSkipReason = "Pose null"; return; }

        var havokSkel = pose->Skeleton;
        if (havokSkel == null) { lastSkipReason = "hkaSkeleton null"; return; }

        var boneCount = pose->ModelPose.Length;
        var parentCount = havokSkel->ParentIndices.Length;
        int applied = 0;

        // Model-space approach with hierarchy propagation:
        // For each bone with a delta, rotate that bone AND all descendants.
        // This ensures child bones move with their parents (no rubber gum).
        foreach (var (boneIdx, deltaRotation) in overrideSet.Rotations)
        {
            if (boneIdx < 0 || boneIdx >= boneCount) continue;

            ref var boneXform = ref pose->ModelPose.Data[boneIdx];

            // Pivot = this bone's model-space position
            var pivotX = boneXform.Translation.X;
            var pivotY = boneXform.Translation.Y;
            var pivotZ = boneXform.Translation.Z;

            // Apply delta to this bone's model-space rotation
            var curQuat = new Quaternion(
                boneXform.Rotation.X, boneXform.Rotation.Y,
                boneXform.Rotation.Z, boneXform.Rotation.W);
            var newQuat = Quaternion.Normalize(deltaRotation * curQuat);
            boneXform.Rotation.X = newQuat.X;
            boneXform.Rotation.Y = newQuat.Y;
            boneXform.Rotation.Z = newQuat.Z;
            boneXform.Rotation.W = newQuat.W;

            // Propagate to all descendants: rotate their position around pivot
            // and rotate their orientation by the same delta.
            for (int i = 0; i < boneCount; i++)
            {
                if (i == boneIdx) continue;
                if (!IsDescendantOf(i, boneIdx, havokSkel->ParentIndices.Data, parentCount)) continue;

                ref var descXform = ref pose->ModelPose.Data[i];

                // Rotate position around pivot
                var relX = descXform.Translation.X - pivotX;
                var relY = descXform.Translation.Y - pivotY;
                var relZ = descXform.Translation.Z - pivotZ;
                var relPos = new Vector3(relX, relY, relZ);
                var rotatedPos = Vector3.Transform(relPos, deltaRotation);
                descXform.Translation.X = pivotX + rotatedPos.X;
                descXform.Translation.Y = pivotY + rotatedPos.Y;
                descXform.Translation.Z = pivotZ + rotatedPos.Z;

                // Rotate orientation
                var descQuat = new Quaternion(
                    descXform.Rotation.X, descXform.Rotation.Y,
                    descXform.Rotation.Z, descXform.Rotation.W);
                var newDescQuat = Quaternion.Normalize(deltaRotation * descQuat);
                descXform.Rotation.X = newDescQuat.X;
                descXform.Rotation.Y = newDescQuat.Y;
                descXform.Rotation.Z = newDescQuat.Z;
                descXform.Rotation.W = newDescQuat.W;
            }

            applied++;
        }

        if (applied > 0)
        {
            overridesAppliedCount++;
            lastAppliedBoneCount = applied;
            lastSkipReason = "";
        }
    }

    /// <summary>
    /// Walk up the parent chain to check if boneIdx is a descendant of ancestorIdx.
    /// </summary>
    private static bool IsDescendantOf(int boneIdx, int ancestorIdx, short* parentIndices, int count)
    {
        int current = boneIdx;
        // Walk up parents (max depth ~20 for humanoid skeletons)
        for (int safety = 0; safety < 32; safety++)
        {
            if (current < 0 || current >= count) return false;
            var parent = parentIndices[current];
            if (parent == ancestorIdx) return true;
            if (parent < 0) return false;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// Read all bone local-space transforms for a character. Returns null if unavailable.
    /// </summary>
    public BoneSnapshot? CaptureSnapshot(nint characterAddress)
    {
        try
        {
            var gameObj = (GameObject*)characterAddress;
            if (gameObj->DrawObject == null) return null;

            var charBase = (CharacterBase*)gameObj->DrawObject;
            var skeleton = charBase->Skeleton;
            if (skeleton == null) return null;
            if (skeleton->PartialSkeletonCount < 1) return null;

            var partial = &skeleton->PartialSkeletons[0];
            var pose = partial->GetHavokPose(0);
            if (pose == null) return null;

            var havokSkel = pose->Skeleton;
            if (havokSkel == null) return null;

            var boneCount = pose->LocalPose.Length;
            var snapshot = new BoneSnapshot
            {
                BoneCount = boneCount,
                LocalRotations = new Quaternion[boneCount],
                LocalTranslations = new Vector3[boneCount],
                LocalScales = new Vector3[boneCount],
                ModelPositions = new Vector3[boneCount],
                ParentIndices = new short[boneCount],
                BoneNames = new string[boneCount],
            };

            // Read local transforms
            for (int i = 0; i < boneCount; i++)
            {
                ref var local = ref pose->LocalPose.Data[i];
                snapshot.LocalRotations[i] = new Quaternion(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W);
                snapshot.LocalTranslations[i] = new Vector3(local.Translation.X, local.Translation.Y, local.Translation.Z);
                snapshot.LocalScales[i] = new Vector3(local.Scale.X, local.Scale.Y, local.Scale.Z);
            }

            // Sync and read model-space positions (for floor detection)
            pose->SyncModelSpace();
            for (int i = 0; i < boneCount; i++)
            {
                ref var model = ref pose->ModelPose.Data[i];
                snapshot.ModelPositions[i] = new Vector3(model.Translation.X, model.Translation.Y, model.Translation.Z);
            }

            // Read hierarchy
            var parentCount = havokSkel->ParentIndices.Length;
            for (int i = 0; i < boneCount && i < parentCount; i++)
            {
                snapshot.ParentIndices[i] = havokSkel->ParentIndices[i];
            }

            // Read bone names
            var nameCount = havokSkel->Bones.Length;
            for (int i = 0; i < boneCount && i < nameCount; i++)
            {
                snapshot.BoneNames[i] = havokSkel->Bones[i].Name.String ?? $"bone_{i}";
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            log.Error(ex, "BoneManipulator: Failed to capture snapshot.");
            return null;
        }
    }

    /// <summary>
    /// Freeze the animation timeline on a character so the game stops advancing animations.
    /// </summary>
    public void FreezeAnimation(nint characterAddress)
    {
        try
        {
            var character = (Character*)characterAddress;
            character->Timeline.OverallSpeed = 0f;
        }
        catch (Exception ex)
        {
            log.Error(ex, "BoneManipulator: Failed to freeze animation.");
        }
    }

    /// <summary>
    /// Unfreeze animation timeline (restore normal playback).
    /// </summary>
    public void UnfreezeAnimation(nint characterAddress)
    {
        try
        {
            var character = (Character*)characterAddress;
            character->Timeline.OverallSpeed = 1f;
        }
        catch (Exception ex)
        {
            log.Error(ex, "BoneManipulator: Failed to unfreeze animation.");
        }
    }

    public void Dispose()
    {
        ClearAllOverrides();
        renderHook?.Dispose();
    }
}

/// <summary>
/// Set of bone rotation overrides to apply in the render hook.
/// Rotations are delta quaternions (deviation from rest pose) applied to model-space.
/// </summary>
public class BoneOverrideSet
{
    /// <summary>
    /// BoneIndex → delta Quaternion rotation (multiplied onto model-space rotation).
    /// </summary>
    public Dictionary<int, Quaternion> Rotations { get; set; } = new();
}

/// <summary>
/// Snapshot of all bone transforms at a point in time.
/// </summary>
public class BoneSnapshot
{
    public int BoneCount;
    public Quaternion[] LocalRotations = Array.Empty<Quaternion>();
    public Vector3[] LocalTranslations = Array.Empty<Vector3>();
    public Vector3[] LocalScales = Array.Empty<Vector3>();
    public Vector3[] ModelPositions = Array.Empty<Vector3>();
    public short[] ParentIndices = Array.Empty<short>();
    public string[] BoneNames = Array.Empty<string>();
}
