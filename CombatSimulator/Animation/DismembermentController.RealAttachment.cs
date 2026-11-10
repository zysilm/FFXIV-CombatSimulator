// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Animation.Attachment;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Animation;

public unsafe partial class DismembermentController
{
    // Visual only (enhanced) slide feel. The distance is the user's (KoStripTopSlideDistance /
    // KoStripLowerSlideDistance, read every frame so edits apply live); the top's travel is further capped
    // at 0.08 m and the lower garment's at 65% of thigh length (see UpdateRealAttachment).
    private const float GarmentSlideSpeedLimit = 0.6f;
    private const float GarmentSlideBodyFriction = 0.45f;
    private const float GarmentSlideDamping = 1.5f;

    private sealed class RealAttachmentState
    {
        public readonly WholeGarmentSlide Motion = new();
        public readonly WaistRetentionMotion Waist = new();
        public float[] WaistWeights = Array.Empty<float>();
        public int LeftHip = -1, LeftKnee = -1, RightHip = -1, RightKnee = -1;
        public int SkeletonSignature;
        public float Scale, TravelLimit;
        public Vector3 Offset;
        // Top only: which way the garment sits askew once it has slipped, and the bones that define it.
        public float TiltSign;
        public int TiltPivot = -1, LeftShoulder = -1, RightShoulder = -1;
    }

    // A slipped top does not sit square: it rolls a few degrees about the body's front/back axis.
    private const float TopTiltRadians = 4f * MathF.PI / 180f;

    private bool UpdateRealAttachment(SkeletonAccess target, Clone c)
    {
        if (objectTable.LocalPlayer is not { } player || player.Address != c.SourceAddress) return false;
        var sourceN = boneService.TryGetSkeleton(c.SourceAddress, synchronizePose: true);
        if (sourceN == null) return false;
        var source = sourceN.Value;
        var signature = ComputeSkeletonSignature(source);
        if (source.BoneCount != target.BoneCount || source.ParentCount != target.ParentCount ||
            signature != ComputeSkeletonSignature(target) ||
            !TryGetSkeletonWorldTransform(source, out var origin, out var rotation)) return false;
        if (!float.IsFinite(origin.LengthSquared()) || !float.IsFinite(rotation.LengthSquared())) return false;

        if (c.RealAttachment == null || c.RealAttachment.SkeletonSignature != signature)
        {
            var scale = Math.Clamp(c.SourceScale.X, .1f, 10f);
            var limit = .08f * scale;
            if (c.GearKeepModelSlot == 3)
            {
                limit = float.MaxValue;
                foreach (var side in new[] { "l", "r" })
                {
                    var hip = FindBoneIndexByName(source, $"j_asi_a_{side}");
                    var knee = FindBoneIndexByName(source, $"j_asi_b_{side}");
                    if (hip < 0 || knee < 0) continue;
                    ref var a = ref source.Pose->ModelPose.Data[hip];
                    ref var b = ref source.Pose->ModelPose.Data[knee];
                    limit = MathF.Min(limit, .65f * new Vector3(b.Translation.X-a.Translation.X,
                        b.Translation.Y-a.Translation.Y, b.Translation.Z-a.Translation.Z).Length());
                }
                if (!float.IsFinite(limit) || limit == float.MaxValue) limit = .15f * scale;
            }
            c.RealAttachment = new RealAttachmentState { SkeletonSignature = signature, Scale = scale, TravelLimit = limit };
            if (c.GearKeepModelSlot == 1)
            {
                c.RealAttachment.TiltSign = Random.Shared.Next(2) == 0 ? -1f : 1f;
                c.RealAttachment.TiltPivot = FindBoneIndexByName(source, "j_sebo_b");
                c.RealAttachment.LeftShoulder = FindBoneIndexByName(source, "j_ude_a_l");
                c.RealAttachment.RightShoulder = FindBoneIndexByName(source, "j_ude_a_r");
            }
            if (c.GearKeepModelSlot == 3)
            {
                var created = c.RealAttachment;
                created.LeftHip = FindBoneIndexByName(source, "j_asi_a_l");
                created.LeftKnee = FindBoneIndexByName(source, "j_asi_b_l");
                created.RightHip = FindBoneIndexByName(source, "j_asi_a_r");
                created.RightKnee = FindBoneIndexByName(source, "j_asi_b_r");
                created.WaistWeights = new float[source.BoneCount];
                for (var i = 0; i < created.WaistWeights.Length; i++)
                {
                    var parent = i < source.ParentCount ? source.HavokSkeleton->ParentIndices[i] : -1;
                    created.WaistWeights[i] = WaistRetentionMotion.BoneWeight(
                        source.HavokSkeleton->Bones[i].Name.String ?? "",
                        parent >= 0 && parent < i ? created.WaistWeights[parent] : 1);
                }
            }
        }
        var state = c.RealAttachment;
        var direction = ResolveGarmentSlipDirection(source, origin, rotation, c.GearKeepModelSlot);
        var localDirection = Vector3.Transform(direction, Quaternion.Inverse(rotation));
        var travelLimit = MathF.Min(
            Math.Clamp(c.GearKeepModelSlot == 3 ? config.KoStripLowerSlideDistance : config.KoStripTopSlideDistance, 0f, 3f)
                * state.Scale, state.TravelLimit);
        state.Motion.Advance(attachmentFrameDt, localDirection, WholeGarmentSlide.Drive(direction), travelLimit,
            GarmentSlideSpeedLimit * state.Scale, GarmentSlideBodyFriction, GarmentSlideDamping);
        state.Offset = Vector3.Transform(state.Motion.Offset, rotation);
        if (c.GearKeepModelSlot == 3)
            state.Waist.Advance(attachmentFrameDt, AttachmentThigh(source, state.LeftHip, state.LeftKnee),
                AttachmentThigh(source, state.RightHip, state.RightKnee));

        // Native posing remains authoritative. During wide stances, smoothly cancel part
        // of the root slide at the waist, fading down the garment. No collision correction.
        ((Character*)c.Chara)->Timeline.OverallSpeed = 0f;
        target.Pose->AccessSyncedPoseModelSpace();
        for (var i = 0; i < source.BoneCount; i++)
        {
            ref var dst = ref target.Pose->ModelPose.Data[i];
            dst = source.Pose->ModelPose.Data[i];
            if (i < state.WaistWeights.Length)
            {
                var correction = state.Waist.Correction(state.Motion.Offset, state.WaistWeights[i]);
                dst.Translation.X += correction.X; dst.Translation.Y += correction.Y; dst.Translation.Z += correction.Z;
            }
        }
        target.Pose->SyncLocalSpace();
        var srcSkeleton = source.CharBase->Skeleton;
        var dstSkeleton = target.CharBase->Skeleton;
        for (var part = 1; part < Math.Min(srcSkeleton->PartialSkeletonCount, dstSkeleton->PartialSkeletonCount); part++)
        {
            var src = srcSkeleton->PartialSkeletons[part].GetHavokPose(0);
            var dst = dstSkeleton->PartialSkeletons[part].GetHavokPose(0);
            if (src == null || dst == null || src->Skeleton == null || dst->Skeleton == null ||
                src->ModelPose.Length != dst->ModelPose.Length || src->ModelPose.Data == null || dst->ModelPose.Data == null ||
                src->Skeleton->Bones.Length < src->ModelPose.Length || dst->Skeleton->Bones.Length < dst->ModelPose.Length) continue;
            var compatible = true;
            for (var i = 0; i < src->ModelPose.Length; i++)
                if (src->Skeleton->Bones[i].Name.String != dst->Skeleton->Bones[i].Name.String) { compatible = false; break; }
            if (!compatible) continue;
            src->SyncModelSpace(); dst->AccessSyncedPoseModelSpace();
            for (var i = 0; i < src->ModelPose.Length; i++)
            {
                ref var bone = ref dst->ModelPose.Data[i];
                bone = src->ModelPose.Data[i];
                if (c.GearKeepModelSlot != 3) continue;
                var name = src->Skeleton->Bones[i].Name.String;
                var mainIndex = name == null ? -1 : FindBoneIndexByName(source, name);
                if (mainIndex >= 0 && mainIndex < state.WaistWeights.Length)
                    bone = target.Pose->ModelPose.Data[mainIndex];
                else
                {
                    var parent = i < src->Skeleton->ParentIndices.Length ? src->Skeleton->ParentIndices[i] : -1;
                    var correction = state.Waist.Correction(state.Motion.Offset, 1);
                    if (parent >= 0 && parent < i)
                    {
                        ref var a = ref src->ModelPose.Data[parent]; ref var b = ref dst->ModelPose.Data[parent];
                        correction = new Vector3(b.Translation.X-a.Translation.X, b.Translation.Y-a.Translation.Y, b.Translation.Z-a.Translation.Z);
                    }
                    if (name?.StartsWith("j_sk_", StringComparison.Ordinal) == true) correction *= .65f;
                    bone.Translation.X += correction.X; bone.Translation.Y += correction.Y; bone.Translation.Z += correction.Z;
                }
            }
            dst->SyncLocalSpace();
        }
        var rootPosition = origin + state.Offset;
        var rootRotation = rotation;
        if (state.TiltPivot >= 0 && state.LeftShoulder >= 0 && state.RightShoulder >= 0 && travelLimit > 1e-4f &&
            state.TiltPivot < source.BoneCount && state.LeftShoulder < source.BoneCount && state.RightShoulder < source.BoneCount)
        {
            ref var pivotBone = ref source.Pose->ModelPose.Data[state.TiltPivot];
            ref var left = ref source.Pose->ModelPose.Data[state.LeftShoulder];
            ref var right = ref source.Pose->ModelPose.Data[state.RightShoulder];
            var lateral = Vector3.Transform(new Vector3(right.Translation.X - left.Translation.X,
                right.Translation.Y - left.Translation.Y, right.Translation.Z - left.Translation.Z), rotation);
            var forward = Vector3.Cross(lateral, direction);
            if (forward.LengthSquared() > 1e-6f)
            {
                var amount = Math.Clamp(state.Motion.Offset.Length() / travelLimit, 0f, 1f);
                var tilt = Quaternion.CreateFromAxisAngle(Vector3.Normalize(forward), state.TiltSign * TopTiltRadians * amount);
                var pivot = origin + Vector3.Transform(
                    new Vector3(pivotBone.Translation.X, pivotBone.Translation.Y, pivotBone.Translation.Z), rotation);
                rootRotation = Quaternion.Normalize(tilt * rotation);
                rootPosition = pivot + Vector3.Transform(origin - pivot, tilt) + state.Offset;
            }
        }
        SetCloneBaseTransform(c, rootPosition, rootRotation);
        return true;
    }

    private static Vector3 AttachmentThigh(SkeletonAccess skeleton, int hip, int knee)
    {
        if (hip < 0 || knee < 0 || hip >= skeleton.BoneCount || knee >= skeleton.BoneCount) return Vector3.Zero;
        ref var a = ref skeleton.Pose->ModelPose.Data[hip]; ref var b = ref skeleton.Pose->ModelPose.Data[knee];
        return new Vector3(b.Translation.X-a.Translation.X, b.Translation.Y-a.Translation.Y, b.Translation.Z-a.Translation.Z);
    }
}
