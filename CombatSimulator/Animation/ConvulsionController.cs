using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Animation;

public unsafe class ConvulsionController : IDisposable
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly BoneTransformService boneService;

    // State
    private bool isActive;
    private nint targetCharacterAddress;
    private float elapsed;
    private float duration;

    public bool IsActive => isActive;

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

    // Head follow
    private int kaoBoneIndex = -1;
    private int kaoParentIndex = -1;

    private struct RuntimeBoneConfig
    {
        public int BoneIndex;
        public float MaxAngleDeg;
        public float FreqHz;
        public float PhaseOffset;
        public float AxisX;
        public float AxisY;
        public float AxisZ;
        public float Intensity;
    }

    public ConvulsionController(BoneTransformService boneService, Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.config = config;
        this.log = log;

        boneService.OnRenderFrame += OnRenderFrame;
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
        kaoBoneIndex = -1;
        kaoParentIndex = -1;
        runtimeBones.Clear();
        log.Info("ConvulsionController: Deactivated");
    }

    private void OnRenderFrame()
    {
        if (!isActive) return;

        try
        {
            ApplyConvulsion();
        }
        catch (Exception ex)
        {
            log.Error(ex, "ConvulsionController: Error in render frame");
            Deactivate();
        }
    }

    private void ApplyConvulsion()
    {
        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return;
        var skel = skelNullable.Value;

        // Resolve bones on first frame
        if (!bonesResolved)
        {
            BuildRuntimeBones(skel);
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

        // Compute per-bone rotation deltas
        var deltas = new Dictionary<int, Quaternion>();
        for (int i = 0; i < runtimeBones.Count; i++)
        {
            var bc = runtimeBones[i];
            if (bc.BoneIndex < 0 || bc.BoneIndex >= skel.BoneCount) continue;

            float currentIntensity = bc.Intensity * decay;
            float angle = bc.MaxAngleDeg * currentIntensity *
                          MathF.Sin(2.0f * MathF.PI * bc.FreqHz * elapsed + bc.PhaseOffset);
            float angleRad = angle * (MathF.PI / 180.0f);

            var offsetQuat = Quaternion.CreateFromYawPitchRoll(
                angleRad * bc.AxisY, angleRad * bc.AxisX, angleRad * bc.AxisZ);

            deltas[bc.BoneIndex] = offsetQuat;
        }

        if (deltas.Count == 0) return;

        // In rotation head mode, skip j_kao in the main loop
        HashSet<int>? skipBones = null;
        bool kaoRotationMode = kaoBoneIndex >= 0 && config.ConvulsionHeadFollowMode == 1;
        if (kaoRotationMode)
        {
            skipBones = new HashSet<int> { kaoBoneIndex };
        }

        // Apply deltas via service (handles propagation)
        var result = boneService.ApplyRotationDeltas(skel, deltas, skipBones);

        // Head rotation mode: rotate j_kao around ground contact instead of translating
        if (kaoRotationMode && kaoBoneIndex < skel.BoneCount &&
            kaoParentIndex >= 0 && kaoParentIndex < skel.BoneCount &&
            result.HasAccumulated[kaoParentIndex])
        {
            var neckDelta = result.AccumulatedDeltas[kaoParentIndex];
            ref var neckModel = ref skel.Pose->ModelPose.Data[kaoParentIndex];
            var neckNewPos = new Vector3(neckModel.Translation.X, neckModel.Translation.Y, neckModel.Translation.Z);
            var neckOrigPos = result.OriginalPositions[kaoParentIndex];
            var kaoOrigPos = result.OriginalPositions[kaoBoneIndex];
            var kaoOrigRot = result.OriginalRotations[kaoBoneIndex];

            // Desired displacement (what translation mode would produce)
            var relToNeck = kaoOrigPos - neckOrigPos;
            var rotatedRel = Vector3.Transform(relToNeck, neckDelta);
            var desiredPos = neckOrigPos + rotatedRel + (neckNewPos - neckOrigPos);
            var displacement = desiredPos - kaoOrigPos;

            // Ground contact: furthest point on head from neck, ~headRadius away
            const float headRadius = 0.12f;
            var neckToHead = kaoOrigPos - neckOrigPos;
            var headDirLen = neckToHead.Length();
            var headDir = headDirLen > 0.001f ? neckToHead / headDirLen : Vector3.UnitY;
            var groundContact = kaoOrigPos + headDir * headRadius;

            // Convert displacement to rotation around ground contact
            var leverArm = kaoOrigPos - groundContact;
            var leverLen = leverArm.Length();

            Quaternion pivotRot = Quaternion.Identity;
            if (leverLen > 0.001f && displacement.Length() > 0.0001f)
            {
                var rotAxis = Vector3.Cross(leverArm, displacement);
                var axisLen = rotAxis.Length();
                if (axisLen > 0.00001f)
                {
                    rotAxis /= axisLen;
                    pivotRot = Quaternion.CreateFromAxisAngle(rotAxis, displacement.Length() / leverLen);
                }
            }

            var kaoNewPos = groundContact + Vector3.Transform(leverArm, pivotRot);
            var kaoNewRot = Quaternion.Normalize(pivotRot * kaoOrigRot);

            boneService.WriteBoneTransform(skel, kaoBoneIndex, kaoNewPos, kaoNewRot, result);
        }

        // Propagate j_kao changes to face/hair partial skeletons
        if (kaoBoneIndex >= 0 && result.HasAccumulated[kaoBoneIndex])
        {
            boneService.PropagateToPartialSkeletons(skel, kaoBoneIndex, "j_kao", result);
        }
    }

    private void BuildRuntimeBones(SkeletonAccess skel)
    {
        runtimeBones.Clear();
        kaoBoneIndex = -1;
        kaoParentIndex = -1;

        var bones = skel.HavokSkeleton->Bones;
        var nameCount = bones.Length;
        var boneCount = skel.BoneCount;

        for (int i = 0; i < boneCount && i < nameCount; i++)
        {
            var name = bones[i].Name.String;
            if (name == null) continue;

            if (name == "j_kao")
            {
                kaoBoneIndex = i;
                kaoParentIndex = (i > 0) ? skel.HavokSkeleton->ParentIndices[i] : -1;
                log.Info($"ConvulsionController: Found j_kao at bone index {i}, parent={kaoParentIndex}");
                continue;
            }

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
        boneService.OnRenderFrame -= OnRenderFrame;
    }
}
