using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Animation;

public unsafe class ConvulsionController : IDisposable
{
    private readonly IPluginLog log;
    private readonly Configuration config;

    private delegate nint RenderDelegate(nint a1, nint a2, nint a3, int a4);
    private Hook<RenderDelegate>? renderHook;

    // State
    private bool isActive;
    private nint targetCharacterAddress;
    private float elapsed;
    private float duration;

    // Bones excluded from convulsion
    private static readonly HashSet<string> ExcludedBones = new()
    {
        "j_kao",
        "j_kubi",
        "j_sebo_c",
        "j_sebo_b",
    };

    // Dynamically built per-activation
    private readonly List<RuntimeBoneConfig> runtimeBones = new();
    private bool bonesResolved;
    private readonly Random rng = new();

    private struct RuntimeBoneConfig
    {
        public int BoneIndex;
        public float MaxAngleDeg;
        public float FreqHz;
        public float PhaseOffset;
        public float AxisX;
        public float AxisY;
        public float AxisZ;
        public float Intensity; // per-bone intensity multiplier
    }

    public ConvulsionController(IGameInteropProvider gameInterop, ISigScanner sigScanner, Configuration config, IPluginLog log)
    {
        this.log = log;
        this.config = config;

        try
        {
            var addr = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 48 81 C3 ?? ?? ?? ?? BF ?? ?? ?? ?? 33 ED");
            renderHook = gameInterop.HookFromAddress<RenderDelegate>(addr, RenderDetour);
            renderHook.Enable();
            log.Info($"ConvulsionController: Render hook at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "ConvulsionController: Failed to create render hook.");
        }
    }

    public void Activate(nint characterAddress, float intensityScale, float durationSeconds)
    {
        targetCharacterAddress = characterAddress;
        duration = durationSeconds;
        elapsed = 0f;
        bonesResolved = false;
        runtimeBones.Clear();

        isActive = true;
        log.Info($"ConvulsionController: Activated (intensity={intensityScale:F2}, duration={durationSeconds:F1}s)");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;
        targetCharacterAddress = nint.Zero;
        bonesResolved = false;
        runtimeBones.Clear();
        log.Info("ConvulsionController: Deactivated");
    }

    private nint RenderDetour(nint a1, nint a2, nint a3, int a4)
    {
        if (isActive)
        {
            try
            {
                ApplyConvulsion();
            }
            catch (Exception ex)
            {
                log.Error(ex, "ConvulsionController: Error in render hook");
                Deactivate();
            }
        }

        return renderHook!.Original(a1, a2, a3, a4);
    }

    private void ApplyConvulsion()
    {
        if (targetCharacterAddress == nint.Zero) return;

        var gameObj = (GameObject*)targetCharacterAddress;
        if (gameObj->DrawObject == null) return;

        var charBase = (CharacterBase*)gameObj->DrawObject;
        var skeleton = charBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return;

        var partial = &skeleton->PartialSkeletons[0];
        var pose = partial->GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) return;

        var havokSkel = pose->Skeleton;
        var boneCount = pose->LocalPose.Length;
        var modelCount = pose->ModelPose.Length;
        var parentCount = havokSkel->ParentIndices.Length;
        if (boneCount != modelCount) return;

        // Resolve bones dynamically on first frame
        if (!bonesResolved)
        {
            BuildRuntimeBones(havokSkel, boneCount);
            bonesResolved = true;
        }

        elapsed += 1.0f / 60.0f;

        if (elapsed >= duration)
        {
            Deactivate();
            return;
        }

        // Exponential decay
        float decay = (float)Math.Exp(-elapsed * 3.0 / duration);

        // Build deltas
        var deltas = new Dictionary<int, Quaternion>();
        for (int i = 0; i < runtimeBones.Count; i++)
        {
            var bc = runtimeBones[i];
            if (bc.BoneIndex < 0 || bc.BoneIndex >= boneCount) continue;

            float currentIntensity = bc.Intensity * decay;
            float angle = bc.MaxAngleDeg * currentIntensity *
                          MathF.Sin(2.0f * MathF.PI * bc.FreqHz * elapsed + bc.PhaseOffset);
            float angleRad = angle * (MathF.PI / 180.0f);

            var offsetQuat = Quaternion.CreateFromYawPitchRoll(
                angleRad * bc.AxisY, angleRad * bc.AxisX, angleRad * bc.AxisZ);

            deltas[bc.BoneIndex] = offsetQuat;
        }

        if (deltas.Count == 0) return;

        // Full hierarchy recomputation
        {
            ref var local = ref pose->LocalPose.Data[0];
            ref var model = ref pose->ModelPose.Data[0];

            var localRot = new Quaternion(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W);
            if (deltas.TryGetValue(0, out var delta0))
                localRot = Quaternion.Normalize(localRot * delta0);

            model.Translation = local.Translation;
            model.Rotation.X = localRot.X;
            model.Rotation.Y = localRot.Y;
            model.Rotation.Z = localRot.Z;
            model.Rotation.W = localRot.W;
            model.Scale = local.Scale;
        }

        for (int i = 1; i < boneCount && i < parentCount; i++)
        {
            var parentIdx = havokSkel->ParentIndices[i];
            if (parentIdx < 0 || parentIdx >= boneCount) continue;

            ref var local = ref pose->LocalPose.Data[i];
            ref var model = ref pose->ModelPose.Data[i];
            ref var parentModel = ref pose->ModelPose.Data[parentIdx];

            var localRot = new Quaternion(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W);
            if (deltas.TryGetValue(i, out var delta))
                localRot = Quaternion.Normalize(localRot * delta);

            var parentRot = new Quaternion(
                parentModel.Rotation.X, parentModel.Rotation.Y,
                parentModel.Rotation.Z, parentModel.Rotation.W);
            var parentPos = new Vector3(
                parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z);
            var parentScale = new Vector3(
                parentModel.Scale.X, parentModel.Scale.Y, parentModel.Scale.Z);

            var modelRot = Quaternion.Normalize(parentRot * localRot);

            var localTrans = new Vector3(local.Translation.X, local.Translation.Y, local.Translation.Z);
            var scaledTrans = localTrans * parentScale;
            var rotatedTrans = Vector3.Transform(scaledTrans, parentRot);
            var modelPos = parentPos + rotatedTrans;

            var localScale = new Vector3(local.Scale.X, local.Scale.Y, local.Scale.Z);
            var modelScale = parentScale * localScale;

            model.Translation.X = modelPos.X;
            model.Translation.Y = modelPos.Y;
            model.Translation.Z = modelPos.Z;
            model.Rotation.X = modelRot.X;
            model.Rotation.Y = modelRot.Y;
            model.Rotation.Z = modelRot.Z;
            model.Rotation.W = modelRot.W;
            model.Scale.X = modelScale.X;
            model.Scale.Y = modelScale.Y;
            model.Scale.Z = modelScale.Z;
        }
    }

    private void BuildRuntimeBones(FFXIVClientStructs.Havok.Animation.Rig.hkaSkeleton* skeleton, int boneCount)
    {
        runtimeBones.Clear();

        var bones = skeleton->Bones;
        var nameCount = bones.Length;

        for (int i = 0; i < boneCount && i < nameCount; i++)
        {
            var name = bones[i].Name.String;
            if (name == null) continue;
            if (ExcludedBones.Contains(name)) continue;

            RuntimeBoneConfig bc;

            if (name == "j_kosi")
            {
                bc = new RuntimeBoneConfig
                {
                    BoneIndex = i,
                    MaxAngleDeg = 3.0f,
                    FreqHz = config.ConvulsionKosiFrequency,
                    PhaseOffset = (float)(rng.NextDouble() * Math.PI * 2.0),
                    AxisX = 1.0f,
                    AxisY = 0.0f,
                    AxisZ = 0.7f,
                    Intensity = config.ConvulsionKosiIntensity,
                };
            }
            else if (name == "j_sebo_a")
            {
                bc = new RuntimeBoneConfig
                {
                    BoneIndex = i,
                    MaxAngleDeg = 2.0f,
                    FreqHz = config.ConvulsionSeboAFrequency,
                    PhaseOffset = (float)(rng.NextDouble() * Math.PI * 2.0),
                    AxisX = 1.0f,
                    AxisY = 0.0f,
                    AxisZ = 0.5f,
                    Intensity = config.ConvulsionSeboAIntensity,
                };
            }
            else
            {
                // All other non-excluded bones: use global intensity, randomized frequency
                var freq = 8.0f + (float)(rng.NextDouble() * 6.0); // 8–14 Hz
                bc = new RuntimeBoneConfig
                {
                    BoneIndex = i,
                    MaxAngleDeg = 3.0f,
                    FreqHz = freq,
                    PhaseOffset = (float)(rng.NextDouble() * Math.PI * 2.0),
                    AxisX = 0.6f + (float)(rng.NextDouble() * 0.4),
                    AxisY = (float)(rng.NextDouble() * 0.3),
                    AxisZ = (float)(rng.NextDouble() * 0.3),
                    Intensity = config.ConvulsionIntensity,
                };
            }

            runtimeBones.Add(bc);
        }

        log.Info($"ConvulsionController: Built {runtimeBones.Count} bone configs (excluded {ExcludedBones.Count})");
    }

    public void Dispose()
    {
        Deactivate();
        renderHook?.Dispose();
    }
}
