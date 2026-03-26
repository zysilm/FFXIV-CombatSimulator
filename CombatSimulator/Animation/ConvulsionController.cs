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

    // Only these bones participate in convulsion
    private static readonly HashSet<string> AllowedBones = new()
    {
        "j_kosi",       // waist/pelvis
        "j_sebo_a",     // spine A
        "j_ude_a_l",    // upper arm L
        "j_ude_a_r",    // upper arm R
        "j_ude_b_l",    // forearm L
        "j_ude_b_r",    // forearm R
        "j_te_l",       // hand L
        "j_te_r",       // hand R
        "j_asi_a_l",    // thigh L
        "j_asi_a_r",    // thigh R
        "j_asi_b_l",    // shin L
        "j_asi_b_r",    // shin R
        "j_asi_c_l",    // foot L
        "j_asi_c_r",    // foot R
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

        // CustomizePlus approach: modify ModelPose directly, never touch LocalPose.
        // This preserves physics (breast, stomach, etc.) since physics writes to ModelPose.
        //
        // For each bone with a direct delta: right-multiply its ModelPose rotation.
        // For descendants of modified bones: propagate position/rotation changes
        // so the skeleton stays connected without recomputing from LocalPose.

        // Track accumulated model-space rotation delta per bone for propagation
        var accDelta = new Quaternion[boneCount];
        var hasAcc = new bool[boneCount];

        for (int i = 0; i < boneCount && i < parentCount; i++)
        {
            var parentIdx = (i > 0) ? havokSkel->ParentIndices[i] : (short)-1;
            bool hasDirect = deltas.TryGetValue(i, out var directDelta);
            bool parentHasAcc = parentIdx >= 0 && parentIdx < boneCount && hasAcc[parentIdx];

            if (!hasDirect && !parentHasAcc)
                continue;

            ref var model = ref pose->ModelPose.Data[i];

            // Read current model-space transform
            var oldRot = new Quaternion(model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W);
            var oldPos = new Vector3(model.Translation.X, model.Translation.Y, model.Translation.Z);

            var newRot = oldRot;
            var newPos = oldPos;

            // Propagate parent's accumulated delta (rotate around parent pivot)
            if (parentHasAcc)
            {
                ref var parentModel = ref pose->ModelPose.Data[parentIdx];
                var parentPos = new Vector3(parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z);
                var pDelta = accDelta[parentIdx];

                var relPos = newPos - parentPos;
                relPos = Vector3.Transform(relPos, pDelta);
                newPos = parentPos + relPos;

                newRot = Quaternion.Normalize(pDelta * newRot);
            }

            // Apply this bone's own direct delta (local-space, so right-multiply)
            if (hasDirect)
            {
                newRot = Quaternion.Normalize(newRot * directDelta);
            }

            // Compute accumulated delta for children: total change from original
            // accDelta = newRot * inverse(oldRot)
            accDelta[i] = Quaternion.Normalize(newRot * Quaternion.Inverse(oldRot));
            hasAcc[i] = true;

            // Write back
            model.Translation.X = newPos.X;
            model.Translation.Y = newPos.Y;
            model.Translation.Z = newPos.Z;
            model.Rotation.X = newRot.X;
            model.Rotation.Y = newRot.Y;
            model.Rotation.Z = newRot.Z;
            model.Rotation.W = newRot.W;
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
            if (!AllowedBones.Contains(name)) continue;

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
                var baseFreq = 8.0f + (float)(rng.NextDouble() * 6.0);
                bc = new RuntimeBoneConfig
                {
                    BoneIndex = i,
                    MaxAngleDeg = 3.0f,
                    FreqHz = baseFreq * config.ConvulsionFrequencyRatio,
                    PhaseOffset = (float)(rng.NextDouble() * Math.PI * 2.0),
                    AxisX = 0.6f + (float)(rng.NextDouble() * 0.4),
                    AxisY = (float)(rng.NextDouble() * 0.3),
                    AxisZ = (float)(rng.NextDouble() * 0.3),
                    Intensity = config.ConvulsionIntensity,
                };
            }

            runtimeBones.Add(bc);
        }

        log.Info($"ConvulsionController: Built {runtimeBones.Count} bone configs from {AllowedBones.Count} allowed");
    }

    public void Dispose()
    {
        Deactivate();
        renderHook?.Dispose();
    }
}
