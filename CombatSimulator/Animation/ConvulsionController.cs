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

    // Head follow: j_kao bone index (resolved but not in AllowedBones)
    private int kaoBoneIndex = -1;

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
        kaoBoneIndex = -1;
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
        if (pose->ModelInSync == 0) return;

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

        // Save original positions/rotations BEFORE any modifications (fixes propagation bug)
        var origPos = new Vector3[boneCount];
        var origRot = new Quaternion[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            ref var m = ref pose->ModelPose.Data[i];
            origPos[i] = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            origRot[i] = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W);
        }

        // Track accumulated model-space rotation delta per bone for propagation
        var accDelta = new Quaternion[boneCount];
        var hasAcc = new bool[boneCount];

        // In rotation mode, skip j_kao in the main loop — handle it separately
        bool kaoRotationMode = kaoBoneIndex >= 0 && config.ConvulsionHeadFollowMode == 1;

        for (int i = 0; i < boneCount && i < parentCount; i++)
        {
            if (kaoRotationMode && i == kaoBoneIndex) continue;

            var parentIdx = (i > 0) ? havokSkel->ParentIndices[i] : (short)-1;
            bool hasDirect = deltas.TryGetValue(i, out var directDelta);
            bool parentHasAcc = parentIdx >= 0 && parentIdx < boneCount && hasAcc[parentIdx];

            if (!hasDirect && !parentHasAcc)
                continue;

            var newRot = origRot[i];
            var newPos = origPos[i];

            // Propagate parent's accumulated delta (rotate around parent's ORIGINAL pivot)
            if (parentHasAcc)
            {
                var parentOrigPos = origPos[parentIdx];
                var pDelta = accDelta[parentIdx];

                var relPos = origPos[i] - parentOrigPos;
                relPos = Vector3.Transform(relPos, pDelta);
                newPos = parentOrigPos + relPos;

                // Parent also moved — add its displacement
                ref var parentModel = ref pose->ModelPose.Data[parentIdx];
                var parentNewPos = new Vector3(parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z);
                newPos += parentNewPos - parentOrigPos;

                newRot = Quaternion.Normalize(pDelta * newRot);
            }

            // Apply this bone's own direct delta (local-space, so right-multiply)
            if (hasDirect)
            {
                newRot = Quaternion.Normalize(newRot * directDelta);
            }

            // Compute accumulated delta for children: total change from original
            accDelta[i] = Quaternion.Normalize(newRot * Quaternion.Inverse(origRot[i]));
            hasAcc[i] = true;

            // Write back
            ref var model = ref pose->ModelPose.Data[i];
            model.Translation.X = newPos.X;
            model.Translation.Y = newPos.Y;
            model.Translation.Z = newPos.Z;
            model.Rotation.X = newRot.X;
            model.Rotation.Y = newRot.Y;
            model.Rotation.Z = newRot.Z;
            model.Rotation.W = newRot.W;
        }

        // Rotation mode: rotate j_kao around its ground contact point instead of translating.
        // The head rests on the ground at the point furthest from the neck connection.
        // Converting the neck displacement into a rotation makes the head rock/roll in place.
        if (kaoRotationMode && kaoBoneIndex < boneCount)
        {
            var kaoParentIdx = havokSkel->ParentIndices[kaoBoneIndex];
            if (kaoParentIdx >= 0 && kaoParentIdx < boneCount && hasAcc[kaoParentIdx])
            {
                var neckDelta = accDelta[kaoParentIdx];
                ref var neckModel = ref pose->ModelPose.Data[kaoParentIdx];
                var neckNewPos = new Vector3(neckModel.Translation.X, neckModel.Translation.Y, neckModel.Translation.Z);

                // Desired displacement (what translation mode would do)
                var relToNeck = origPos[kaoBoneIndex] - origPos[kaoParentIdx];
                var rotatedRel = Vector3.Transform(relToNeck, neckDelta);
                var desiredPos = origPos[kaoParentIdx] + rotatedRel + (neckNewPos - origPos[kaoParentIdx]);
                var displacement = desiredPos - origPos[kaoBoneIndex];

                // Estimate ground contact point: head extends from neck connection,
                // ground contact is at the far side (opposite from neck), ~headRadius away.
                const float headRadius = 0.12f;
                var headDir = Vector3.Normalize(origPos[kaoBoneIndex] - origPos[kaoParentIdx]);
                var groundContact = origPos[kaoBoneIndex] + headDir * headRadius;

                // Convert displacement to rotation around ground contact
                var leverArm = origPos[kaoBoneIndex] - groundContact; // from ground to j_kao
                var leverLen = leverArm.Length();

                Quaternion pivotRot;
                if (leverLen > 0.001f && displacement.Length() > 0.0001f)
                {
                    var rotAxis = Vector3.Cross(leverArm, displacement);
                    var axisLen = rotAxis.Length();
                    if (axisLen > 0.00001f)
                    {
                        rotAxis /= axisLen;
                        var rotAngle = displacement.Length() / leverLen;
                        pivotRot = Quaternion.CreateFromAxisAngle(rotAxis, rotAngle);
                    }
                    else
                    {
                        pivotRot = Quaternion.Identity;
                    }
                }
                else
                {
                    pivotRot = Quaternion.Identity;
                }

                // Apply rotation around ground contact point
                var kaoNewPos = groundContact + Vector3.Transform(leverArm, pivotRot);
                var kaoNewRot = Quaternion.Normalize(pivotRot * origRot[kaoBoneIndex]);

                ref var kaoModel = ref pose->ModelPose.Data[kaoBoneIndex];
                kaoModel.Translation.X = kaoNewPos.X;
                kaoModel.Translation.Y = kaoNewPos.Y;
                kaoModel.Translation.Z = kaoNewPos.Z;
                kaoModel.Rotation.X = kaoNewRot.X;
                kaoModel.Rotation.Y = kaoNewRot.Y;
                kaoModel.Rotation.Z = kaoNewRot.Z;
                kaoModel.Rotation.W = kaoNewRot.W;

                accDelta[kaoBoneIndex] = Quaternion.Normalize(kaoNewRot * Quaternion.Inverse(origRot[kaoBoneIndex]));
                hasAcc[kaoBoneIndex] = true;
            }
        }

        // Propagate j_kao changes to other partial skeletons (face, hair, etc.).
        // Each partial skeleton's root bone connects to a bone in partial 0.
        // The face skeleton (partial 1+) roots at j_kao — if we don't update those,
        // the game will overwrite j_kao with the unmodified face root position.
        if (kaoBoneIndex >= 0 && hasAcc[kaoBoneIndex])
        {
            var kaoDelta = accDelta[kaoBoneIndex];
            ref var kaoModel = ref pose->ModelPose.Data[kaoBoneIndex];
            var kaoNewPos = new Vector3(kaoModel.Translation.X, kaoModel.Translation.Y, kaoModel.Translation.Z);
            var kaoNewRot = new Quaternion(kaoModel.Rotation.X, kaoModel.Rotation.Y, kaoModel.Rotation.Z, kaoModel.Rotation.W);
            var kaoPosDisplacement = kaoNewPos - origPos[kaoBoneIndex];

            for (int ps = 1; ps < skeleton->PartialSkeletonCount; ps++)
            {
                var otherPartial = &skeleton->PartialSkeletons[ps];
                var otherPose = otherPartial->GetHavokPose(0);
                if (otherPose == null || otherPose->Skeleton == null) continue;
                if (otherPose->ModelInSync == 0) continue;

                var otherBoneCount = otherPose->ModelPose.Length;
                if (otherBoneCount < 1) continue;

                // Check if root bone (index 0) of this partial skeleton matches j_kao by name
                var rootName = otherPose->Skeleton->Bones[0].Name.String;
                if (rootName != "j_kao") continue;

                // Apply delta to root bone and propagate to all children
                var otherParentCount = otherPose->Skeleton->ParentIndices.Length;
                for (int b = 0; b < otherBoneCount && b < otherParentCount; b++)
                {
                    ref var boneModel = ref otherPose->ModelPose.Data[b];
                    var bOldPos = new Vector3(boneModel.Translation.X, boneModel.Translation.Y, boneModel.Translation.Z);
                    var bOldRot = new Quaternion(boneModel.Rotation.X, boneModel.Rotation.Y, boneModel.Rotation.Z, boneModel.Rotation.W);

                    // Rotate position around j_kao's original position, then add displacement
                    var relToKao = bOldPos - origPos[kaoBoneIndex];
                    relToKao = Vector3.Transform(relToKao, kaoDelta);
                    var bNewPos = origPos[kaoBoneIndex] + relToKao + kaoPosDisplacement;

                    var bNewRot = Quaternion.Normalize(kaoDelta * bOldRot);

                    boneModel.Translation.X = bNewPos.X;
                    boneModel.Translation.Y = bNewPos.Y;
                    boneModel.Translation.Z = bNewPos.Z;
                    boneModel.Rotation.X = bNewRot.X;
                    boneModel.Rotation.Y = bNewRot.Y;
                    boneModel.Rotation.Z = bNewRot.Z;
                    boneModel.Rotation.W = bNewRot.W;
                }
            }
        }
    }

    private void BuildRuntimeBones(FFXIVClientStructs.Havok.Animation.Rig.hkaSkeleton* skeleton, int boneCount)
    {
        runtimeBones.Clear();
        kaoBoneIndex = -1;

        var bones = skeleton->Bones;
        var nameCount = bones.Length;

        for (int i = 0; i < boneCount && i < nameCount; i++)
        {
            var name = bones[i].Name.String;
            if (name == null) continue;

            // Track j_kao for head follow (not convulsed, just follows neck)
            if (name == "j_kao")
            {
                kaoBoneIndex = i;
                log.Info($"ConvulsionController: Found j_kao at bone index {i}");
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
        renderHook?.Dispose();
    }
}
