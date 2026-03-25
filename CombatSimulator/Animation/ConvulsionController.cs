using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Animation;

public unsafe class ConvulsionController : IDisposable
{
    private readonly IPluginLog log;
    private readonly Configuration config;

    // Animation update hook — same function the ragdoll system used
    private delegate void AnimationUpdateDelegate(nint characterBase, nint a2, nint a3);
    private Hook<AnimationUpdateDelegate>? animationHook;

    // State
    private bool isActive;
    private nint targetDrawObject;
    private float elapsed;
    private float duration;
    private float intensity;

    // Bone index cache (resolved once per activation)
    private readonly Dictionary<string, int> boneIndices = new();
    private bool bonesResolved;

    // Per-bone convulsion parameters
    private readonly BoneConvulsionConfig[] boneConfigs;

    // Random phase offsets (set each activation for variety)
    private readonly Random rng = new();

    private struct BoneConvulsionConfig
    {
        public string BoneName;
        public float MaxAngleDeg;
        public float FreqHz;
        public float PhaseOffset;
        public float AxisX; // weight on X axis
        public float AxisY; // weight on Y axis
        public float AxisZ; // weight on Z axis
    }

    public ConvulsionController(IGameInteropProvider gameInterop, ISigScanner sigScanner, Configuration config, IPluginLog log)
    {
        this.log = log;
        this.config = config;

        // Initialize bone configs (frequencies/phases are randomized on Activate)
        boneConfigs = new BoneConvulsionConfig[]
        {
            // Pelvis — subtle roll/twist
            new() { BoneName = "j_kosi", MaxAngleDeg = 2.0f, FreqHz = 10.0f, AxisX = 1.0f, AxisY = 0.0f, AxisZ = 0.7f },

            // Left thigh — flex/extend + small adduction
            new() { BoneName = "j_asi_a_l", MaxAngleDeg = 3.5f, FreqHz = 8.0f, AxisX = 1.0f, AxisY = 0.0f, AxisZ = 0.3f },
            // Right thigh — slightly different frequency for asymmetry
            new() { BoneName = "j_asi_a_r", MaxAngleDeg = 3.5f, FreqHz = 8.7f, AxisX = 1.0f, AxisY = 0.0f, AxisZ = 0.3f },

            // Left shin — knee bend only
            new() { BoneName = "j_asi_b_l", MaxAngleDeg = 5.0f, FreqHz = 12.0f, AxisX = 1.0f, AxisY = 0.0f, AxisZ = 0.0f },
            // Right shin
            new() { BoneName = "j_asi_b_r", MaxAngleDeg = 5.0f, FreqHz = 12.8f, AxisX = 1.0f, AxisY = 0.0f, AxisZ = 0.0f },

            // Left foot — twitch/curl
            new() { BoneName = "j_asi_c_l", MaxAngleDeg = 4.0f, FreqHz = 14.0f, AxisX = 1.0f, AxisY = 0.5f, AxisZ = 0.0f },
            // Right foot
            new() { BoneName = "j_asi_c_r", MaxAngleDeg = 4.0f, FreqHz = 15.0f, AxisX = 1.0f, AxisY = 0.5f, AxisZ = 0.0f },
        };

        try
        {
            var addr = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4");
            animationHook = gameInterop.HookFromAddress<AnimationUpdateDelegate>(addr, AnimationUpdateDetour);
            animationHook.Enable();
            log.Info($"ConvulsionController: Animation hook at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "ConvulsionController: Failed to create animation hook.");
        }
    }

    public void Activate(nint characterDrawObject, float intensityScale, float durationSeconds)
    {
        targetDrawObject = characterDrawObject;
        intensity = intensityScale;
        duration = durationSeconds;
        elapsed = 0f;
        bonesResolved = false;
        boneIndices.Clear();

        // Randomize phase offsets for natural look
        for (int i = 0; i < boneConfigs.Length; i++)
        {
            boneConfigs[i].PhaseOffset = (float)(rng.NextDouble() * Math.PI * 2.0);
        }

        isActive = true;
        log.Info($"ConvulsionController: Activated (intensity={intensityScale:F2}, duration={durationSeconds:F1}s)");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;
        targetDrawObject = nint.Zero;
        bonesResolved = false;
        boneIndices.Clear();
        log.Info("ConvulsionController: Deactivated");
    }

    private void AnimationUpdateDetour(nint characterBase, nint a2, nint a3)
    {
        // Always call original first
        animationHook!.Original(characterBase, a2, a3);

        if (!isActive || characterBase == nint.Zero || characterBase != targetDrawObject)
            return;

        try
        {
            ApplyConvulsion((CharacterBase*)characterBase);
        }
        catch (Exception ex)
        {
            log.Error(ex, "ConvulsionController: Error in convulsion update");
            Deactivate();
        }
    }

    private void ApplyConvulsion(CharacterBase* charBase)
    {
        var skeleton = charBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0)
            return;

        var partialSkeleton = &skeleton->PartialSkeletons[0];
        var pose = partialSkeleton->GetHavokPose(0);
        if (pose == null || pose->Skeleton == null)
            return;

        // Resolve bone indices on first frame
        if (!bonesResolved)
        {
            ResolveBoneIndices(pose->Skeleton);
            bonesResolved = true;
        }

        // Update elapsed time (~60 fps assumed in hook; use a small fixed step)
        elapsed += 1.0f / 60.0f;

        // Check if duration expired
        if (elapsed >= duration)
        {
            Deactivate();
            return;
        }

        // Exponential decay: intensity * e^(-t * 3.0 / duration)
        float decay = (float)Math.Exp(-elapsed * 3.0 / duration);
        float currentIntensity = intensity * decay;

        // Apply rotation offsets to each bone
        var localPose = pose->LocalPose;
        for (int i = 0; i < boneConfigs.Length; i++)
        {
            ref var bc = ref boneConfigs[i];
            if (!boneIndices.TryGetValue(bc.BoneName, out int boneIdx))
                continue;

            if (boneIdx < 0 || boneIdx >= localPose.Length)
                continue;

            // Compute oscillation angle
            float t = elapsed;
            float angle = bc.MaxAngleDeg * currentIntensity *
                          MathF.Sin(2.0f * MathF.PI * bc.FreqHz * t + bc.PhaseOffset);
            float angleRad = angle * (MathF.PI / 180.0f);

            // Build rotation offset quaternion from axis weights
            var axisAngleX = angleRad * bc.AxisX;
            var axisAngleY = angleRad * bc.AxisY;
            var axisAngleZ = angleRad * bc.AxisZ;

            var offsetQuat = Quaternion.CreateFromYawPitchRoll(axisAngleY, axisAngleX, axisAngleZ);

            // Read current bone rotation
            ref var transform = ref localPose.Data[boneIdx];
            var currentQuat = new Quaternion(
                transform.Rotation.X,
                transform.Rotation.Y,
                transform.Rotation.Z,
                transform.Rotation.W);

            // Multiply: apply offset on top of current rotation
            var newQuat = Quaternion.Normalize(currentQuat * offsetQuat);

            // Write back
            transform.Rotation.X = newQuat.X;
            transform.Rotation.Y = newQuat.Y;
            transform.Rotation.Z = newQuat.Z;
            transform.Rotation.W = newQuat.W;
        }

        // Force model-space recalculation
        pose->ModelInSync = 0;
    }

    private void ResolveBoneIndices(FFXIVClientStructs.Havok.Animation.Rig.hkaSkeleton* skeleton)
    {
        boneIndices.Clear();

        var bones = skeleton->Bones;
        for (int i = 0; i < bones.Length; i++)
        {
            var name = bones[i].Name.String;
            if (name == null) continue;

            foreach (var bc in boneConfigs)
            {
                if (name == bc.BoneName && !boneIndices.ContainsKey(bc.BoneName))
                {
                    boneIndices[bc.BoneName] = i;
                    break;
                }
            }
        }

        log.Info($"ConvulsionController: Resolved {boneIndices.Count}/{boneConfigs.Length} bones");
    }

    public void Dispose()
    {
        Deactivate();
        animationHook?.Dispose();
    }
}
