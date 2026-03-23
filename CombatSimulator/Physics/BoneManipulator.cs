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
/// Low-level bone transform read/write. Hooks UpdateBonePhysics so our writes
/// survive the game's animation pipeline.
/// </summary>
public unsafe class BoneManipulator : IDisposable
{
    private readonly IPluginLog log;

    // Hook: fires after the game's bone physics pass completes
    private delegate nint UpdateBonePhysicsDelegate(nint a1);
    private Hook<UpdateBonePhysicsDelegate>? updateBonePhysicsHook;

    // Pending bone overrides to apply in the hook (written by RagdollController, consumed in hook)
    private readonly object overrideLock = new();
    private readonly Dictionary<nint, BoneOverrideSet> pendingOverrides = new();

    public bool IsHooked => updateBonePhysicsHook != null;

    public BoneManipulator(IGameInteropProvider gameInterop, ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;

        try
        {
            var addr = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4");
            updateBonePhysicsHook = gameInterop.HookFromAddress<UpdateBonePhysicsDelegate>(addr, UpdateBonePhysicsDetour);
            updateBonePhysicsHook.Enable();
            log.Info($"BoneManipulator: UpdateBonePhysics hooked at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "BoneManipulator: Could not hook UpdateBonePhysics — ragdoll will be unavailable.");
        }
    }

    private nint UpdateBonePhysicsDetour(nint a1)
    {
        var result = updateBonePhysicsHook!.Original(a1);

        try
        {
            ApplyPendingOverrides();
        }
        catch (Exception ex)
        {
            log.Error(ex, "BoneManipulator: Error applying bone overrides in hook.");
        }

        return result;
    }

    /// <summary>
    /// Set bone rotation overrides for a character. Called from RagdollController each frame.
    /// The overrides will be applied in the next UpdateBonePhysics hook call.
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
        if (gameObj->DrawObject == null) return;

        var charBase = (CharacterBase*)gameObj->DrawObject;
        var skeleton = charBase->Skeleton;
        if (skeleton == null) return;

        // Only operate on partial skeleton 0 (main body)
        if (skeleton->PartialSkeletonCount < 1) return;
        var partial = &skeleton->PartialSkeletons[0];
        var pose = partial->GetHavokPose(0);
        if (pose == null) return;

        var boneCount = pose->LocalPose.Length;

        foreach (var (boneIdx, rotation) in overrideSet.Rotations)
        {
            if (boneIdx < 0 || boneIdx >= boneCount) continue;

            // Write rotation to local-space pose directly
            var localTransform = &pose->LocalPose.Data[boneIdx];
            localTransform->Rotation.X = rotation.X;
            localTransform->Rotation.Y = rotation.Y;
            localTransform->Rotation.Z = rotation.Z;
            localTransform->Rotation.W = rotation.W;
        }

        // Mark model space as needing sync
        pose->ModelInSync = 0;
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
        updateBonePhysicsHook?.Dispose();
    }
}

/// <summary>
/// Set of bone rotation overrides to apply in the hook.
/// </summary>
public class BoneOverrideSet
{
    /// <summary>
    /// BoneIndex → Quaternion rotation (local space).
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
