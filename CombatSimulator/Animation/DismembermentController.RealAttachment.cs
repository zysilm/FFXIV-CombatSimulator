// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.Attachment;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Animation;

public unsafe partial class DismembermentController
{
    private sealed class RealAttachmentState
    {
        public readonly WholeGarmentSlide Motion = new();
        public readonly WaistRetentionMotion Waist = new();
        public float[] WaistWeights = Array.Empty<float>();
        public int LeftHip = -1, LeftKnee = -1, RightHip = -1, RightKnee = -1;
        public AttachmentSettings Settings = null!;
        public int SkeletonSignature;
        public float Scale, TravelLimit;
        public Vector3 Origin, Offset;
    }

    public readonly record struct AttachmentModel(string Key, string Label, int Slot);
    public readonly record struct AttachmentDebugPoint(Vector3 Position, Vector3 Target, bool Anchored,
        bool Contact, float Tension);

    public List<AttachmentModel> GetAttachmentModels()
    {
        var result = new List<AttachmentModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? signature, int slot)
        {
            if (string.IsNullOrEmpty(signature)) return;
            var key = AttachmentKey(slot, signature);
            if (!seen.Add(key)) return;
            var path = signature.Split('|')[0];
            result.Add(new AttachmentModel(key, $"{(slot == 1 ? "Body" : "Legs")}: {path}", slot));
        }
        foreach (var c in clones)
            if (c.GearRealAttachmentRequested) Add(c.GearExpectedAppearanceSignature, c.GearKeepModelSlot);
        var player = objectTable.LocalPlayer;
        if (player != null && player.Address != nint.Zero)
        {
            var draw = ((GameObject*)player.Address)->DrawObject;
            if (draw != null)
            {
                var cb = (CharacterBase*)draw;
                if (cb->Models != null)
                    foreach (var slot in new[] { 1, 3 })
                        if (slot < cb->SlotCount) Add(GetModelAppearanceSignature(cb->Models[slot]), slot);
            }
        }
        return result;
    }

    private static string AttachmentKey(int slot, string signature) => $"{slot}:{signature}";

    public void RebindRealAttachments()
    {
        // Settings are snapshots: explicit rebind makes structural edits predictable and recoverable.
        foreach (var c in clones) if (c.GearRealAttachmentRequested) c.RealAttachment = null;
    }

    public void RefreshAttachmentLiveSettings()
    {
        foreach (var c in clones)
        {
            if (c.RealAttachment is not { } state) continue;
            var saved = ResolveAttachmentSettings(c);
            state.Settings.SlipDistance = saved.SlipDistance;
            state.Settings.SpeedLimit = saved.SpeedLimit;
            state.Settings.BodyFriction = saved.BodyFriction;
            state.Settings.Damping = saved.Damping;
        }
    }

    public void CollectAttachmentDebug(List<(Vector3 A, Vector3 B)> edges, List<AttachmentDebugPoint> points)
    {
        edges.Clear(); points.Clear();
        foreach (var c in clones)
            if (c.RealAttachment is { } state)
            {
                edges.Add((state.Origin, state.Origin + state.Offset));
                points.Add(new(state.Origin + state.Offset, state.Origin, true, false, 0));
            }
    }

    public string GetAttachmentStatus()
        => $"Attached: {clones.FindAll(c => c.RealAttachment != null).Count}";

    private AttachmentSettings ResolveAttachmentSettings(Clone c)
    {
        var defaults = c.GearKeepModelSlot == 1 ? config.KoStripAttachmentBody : config.KoStripAttachmentLegs;
        if (c.GearExpectedAppearanceSignature is { } signature && config.KoStripAttachmentOverrides != null &&
            config.KoStripAttachmentOverrides.TryGetValue(AttachmentKey(c.GearKeepModelSlot, signature), out var saved))
            defaults = saved;
        var result = (defaults ?? new AttachmentSettings()).Validated();
        result.Template = AttachmentSettings.TemplateForSlot(result.Template, c.GearKeepModelSlot);
        return result;
    }

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
            c.RealAttachment = new RealAttachmentState { Settings = ResolveAttachmentSettings(c),
                SkeletonSignature = signature, Scale = scale, TravelLimit = limit };
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
        state.Motion.Advance(attachmentFrameDt, localDirection, MathF.Max(0, -direction.Y),
            MathF.Min(state.Settings.SlipDistance * state.Scale, state.TravelLimit),
            state.Settings.SpeedLimit * state.Scale, state.Settings.BodyFriction, state.Settings.Damping);
        state.Origin = origin;
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
        SetCloneBaseTransform(c, origin + state.Offset, rotation);
        return true;
    }

    private static Vector3 AttachmentThigh(SkeletonAccess skeleton, int hip, int knee)
    {
        if (hip < 0 || knee < 0 || hip >= skeleton.BoneCount || knee >= skeleton.BoneCount) return Vector3.Zero;
        ref var a = ref skeleton.Pose->ModelPose.Data[hip]; ref var b = ref skeleton.Pose->ModelPose.Data[knee];
        return new Vector3(b.Translation.X-a.Translation.X, b.Translation.Y-a.Translation.Y, b.Translation.Z-a.Translation.Z);
    }
}
