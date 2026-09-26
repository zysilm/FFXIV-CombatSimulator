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
    private sealed class AttachmentBone
    {
        public string Name = "";
        public int Index, Toward = -1;
        public float Multiplier;
        public bool Skirt;
        public Vector3 SourceCenter, Offset;
        public Quaternion SourceFrame;
        public readonly AttachmentFrame Frame = new();
    }

    private sealed class RealAttachmentState
    {
        public readonly AttachedGarmentMotion Motion = new();
        public AttachmentSettings Settings = null!;
        public readonly List<AttachmentBone> Bones = new();
        public int[] DrivenAncestor = Array.Empty<int>();
        public int SkeletonSignature;
        public float Scale;
        public readonly List<SkirtPanelBinding> SkirtPanels = new();
        public readonly List<RetainedSkirtPanel.Capsule> SkirtCapsules = new();
        public float SkirtFloorAge = 1;
        public Vector3 SkirtFloorSample, SkirtFloorPoint;
        public bool SkirtHasFloor;
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
            state.Settings.SkirtSwingDegrees = saved.SkirtSwingDegrees;
            state.Settings.SkirtStiffness = saved.SkirtStiffness;
            state.Settings.SkirtDamping = saved.SkirtDamping;
            state.Settings.Thickness = saved.Thickness;
        }
    }

    public void CollectAttachmentDebug(List<(Vector3 A, Vector3 B)> edges, List<AttachmentDebugPoint> points)
    {
        edges.Clear(); points.Clear();
        foreach (var c in clones)
        {
            if (c.RealAttachment is not { } state) continue;
            foreach (var bone in state.Bones)
            {
                var position = bone.SourceCenter + bone.Offset;
                edges.Add((bone.SourceCenter, position));
                points.Add(new AttachmentDebugPoint(position, bone.SourceCenter, true, false,
                    Math.Clamp(bone.Offset.Length() / MathF.Max(.001f, state.Settings.SlipDistance * state.Scale), 0, 1)));
            }
            foreach (var panel in state.SkirtPanels)
            {
                edges.Add((panel.WorldPivot, panel.WorldTip));
                points.Add(new AttachmentDebugPoint(panel.WorldTip, panel.WorldPivot, false,
                    panel.Solver.InContact, panel.Solver.ResidualPenetration > .001f ? 1 : 0));
            }
        }
    }

    public string GetAttachmentStatus()
    {
        var garments = 0; var bones = 0; var panels = 0; var unresolved = 0;
        var maximumAngle = 0f;
        foreach (var c in clones)
            if (c.RealAttachment is { } state)
            {
                garments++; bones += state.Bones.Count; panels += state.SkirtPanels.Count;
                foreach (var panel in state.SkirtPanels)
                {
                    if (panel.Solver.ResidualPenetration > .001f) unresolved++;
                    maximumAngle = MathF.Max(maximumAngle, panel.Solver.Angle * 180 / MathF.PI);
                }
            }
        if (!config.KoStripAttachmentDebugDraw) return $"Attached: {garments}";
        return $"Attached: {garments} | Live bone followers: {bones} | Skirt panels: {panels} | Blocked panels: {unresolved} | Actual max angle: {maximumAngle:F1} deg";
    }

    private AttachmentSettings ResolveAttachmentSettings(Clone c)
    {
        var defaults = c.GearKeepModelSlot == 1 ? config.KoStripAttachmentBody : config.KoStripAttachmentLegs;
        if (c.GearExpectedAppearanceSignature is { } signature && config.KoStripAttachmentOverrides != null &&
            config.KoStripAttachmentOverrides.TryGetValue(AttachmentKey(c.GearKeepModelSlot, signature), out var saved))
            defaults = saved;
        var result = (defaults ?? new AttachmentSettings()).Validated();
        // Old/corrupt JSON must not remove all permanent connections.
        if (c.GearKeepModelSlot == 3 && result.Template is GarmentTemplate.Top or GarmentTemplate.Coat or GarmentTemplate.Dress)
            result.Template = GarmentTemplate.Trousers;
        if (c.GearKeepModelSlot == 1 && result.Template is GarmentTemplate.Trousers or GarmentTemplate.Skirt)
            result.Template = GarmentTemplate.Top;
        return result;
    }

    private bool UpdateRealAttachment(SkeletonAccess target, Clone c)
    {
        // This feature is player-only. Don't keep dereferencing an actor slot after logout/reuse.
        if (objectTable.LocalPlayer is not { } player || player.Address != c.SourceAddress) return false;
        var sourceN = boneService.TryGetSkeleton(c.SourceAddress);
        if (sourceN == null) return false;
        var source = sourceN.Value;
        var signature = ComputeSkeletonSignature(source);
        if (source.BoneCount != target.BoneCount || source.ParentCount != target.ParentCount ||
            signature != ComputeSkeletonSignature(target) ||
            !TryGetSkeletonWorldTransform(source, out var origin, out var rotation)) return false;
        if (!FiniteAttachmentVector(origin) || !float.IsFinite(rotation.LengthSquared())) return false;

        if (c.RealAttachment == null || c.RealAttachment.SkeletonSignature != signature)
        {
            c.RealAttachment = CreateRealAttachment(source, c, origin, rotation, signature);
            if (c.RealAttachment == null)
            {
                log.Warning("Real attachment: no compatible garment bones; retiring the visual clone.");
                return false;
            }
        }
        var state = c.RealAttachment;
        foreach (var bone in state.Bones)
        {
            ResolveAttachmentFrame(source, bone.Index, bone.Toward, origin, rotation, bone.Frame,
                out bone.SourceCenter, out bone.SourceFrame);
            if (!FiniteAttachmentVector(bone.SourceCenter) || !float.IsFinite(bone.SourceFrame.LengthSquared())) return false;
        }
        // The first node is the live waist/torso frame. Retain only body-relative sliding;
        // every render pose is rebuilt from the source, so no world-space cage drift survives.
        var bodyFrame = state.Bones[0].SourceFrame;
        var upperBody = c.GearKeepModelSlot == 1;
        if (upperBody) state.Motion.Advance(attachmentFrameDt, bodyFrame, state.Settings, upperBody: true);
        else state.Motion.AdvanceTrousers(attachmentFrameDt, bodyFrame, state.Settings);
        foreach (var bone in state.Bones)
        {
            bone.Offset = state.Motion.Offset(bodyFrame, state.Scale, bone.Multiplier, bone.Skirt, state.Settings, upperBody);
            if (state.Settings.AnchorOffsets.TryGetValue(bone.Name, out var authored))
                bone.Offset += Vector3.Transform(authored.ToVector() * state.Scale, bone.SourceFrame);
            if (upperBody)
            {
                var weight = AttachedGarmentMotion.UpperBodyWeight(bone.Name);
                // Even legacy authored offsets cannot unpin the upper opening. Lower offsets
                // remain bounded; the same rule applies to coats/dresses in the Body slot.
                bone.Offset = ClampVectorLength(bone.Offset,
                    MathF.Min(state.Settings.SlipDistance, .08f) * state.Scale) * weight;
            }
            else
            {
                var direction = Vector3.Zero;
                var pathLength = 0f;
                if (bone.Toward >= 0)
                {
                    ref var end = ref source.Pose->ModelPose.Data[bone.Toward];
                    direction = origin + Vector3.Transform(new Vector3(end.Translation.X, end.Translation.Y, end.Translation.Z), rotation) - bone.SourceCenter;
                }
                pathLength = direction.Length();
                if (bone.Name == "j_kosi")
                {
                    // Follow the mean of the two live thighs, preserving the authored waist
                    // clearance. Never use pelvis-to-spine (which points toward the head).
                    direction = Vector3.Zero; pathLength = float.MaxValue; var legs = 0;
                    foreach (var side in new[] { "l", "r" })
                    {
                        var hip = FindBoneIndexByName(source, $"j_asi_a_{side}");
                        var knee = FindBoneIndexByName(source, $"j_asi_b_{side}");
                        if (hip < 0 || knee < 0) continue;
                        ref var a = ref source.Pose->ModelPose.Data[hip]; ref var b = ref source.Pose->ModelPose.Data[knee];
                        var segment = Vector3.Transform(new Vector3(b.Translation.X - a.Translation.X,
                            b.Translation.Y - a.Translation.Y, b.Translation.Z - a.Translation.Z), rotation);
                        direction += segment; pathLength = MathF.Min(pathLength, segment.Length()); legs++;
                    }
                    if (legs == 0) pathLength = 0;
                    else pathLength = MathF.Min(pathLength, direction.Length() / legs);
                }
                bone.Offset = state.Settings.Template == GarmentTemplate.Skirt ? Vector3.Zero :
                    state.Motion.TrouserOffset(bone.Name, direction, pathLength, state.Scale, bone.Multiplier, state.Settings);
            }
        }
        DriveRealAttachment(source, target, c, state, origin, rotation);
        return true;
    }

    private RealAttachmentState? CreateRealAttachment(SkeletonAccess source, Clone c,
        Vector3 origin, Quaternion rotation, int signature)
    {
        var settings = ResolveAttachmentSettings(c);
        var scale = Math.Clamp(c.SourceScale.X, 0.1f, 10f);
        var state = new RealAttachmentState { Settings = settings, SkeletonSignature = signature, Scale = scale };
        var rings = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string name, string toward, float attachment, string? parent = null)
        {
            if (rings.ContainsKey(name) || state.Bones.Count >= 48) return;
            var index = FindBoneIndexByName(source, name);
            if (index < 0) return;
            var next = FindBoneIndexByName(source, toward);
            var binding = new AttachmentBone { Name = name, Index = index, Toward = next };
            ResolveAttachmentFrame(source, index, next, origin, rotation, binding.Frame, out var center, out var frame);
            if (settings.Anchors.TryGetValue(name, out var custom)) attachment = custom;
            var multiplier = attachment >= 0 ? attachment :
                parent != null && rings.TryGetValue(parent, out var p) ? state.Bones[p].Multiplier :
                c.GearKeepModelSlot == 1 ? .7f : .65f;
            binding.Multiplier = multiplier;
            binding.Skirt = name.StartsWith("j_sk_", StringComparison.Ordinal);
            binding.SourceCenter = center; binding.SourceFrame = frame;
            rings.Add(name, state.Bones.Count);
            state.Bones.Add(binding);
        }

        if (c.GearKeepModelSlot == 1)
        {
            Add("j_kosi", "j_sebo_a", -1);
            Add("j_sebo_a", "j_sebo_b", -1, "j_kosi");
            Add("j_sebo_b", "j_sebo_c", -1, "j_sebo_a");
            Add("j_sebo_c", "j_kubi", -1, "j_sebo_b");
            Add("j_kubi", "j_kao", 0.7f, "j_sebo_c");
            foreach (var side in new[] { "l", "r" })
            {
                Add($"j_sako_{side}", $"j_ude_a_{side}", 1f, "j_sebo_c");
                Add($"j_ude_a_{side}", $"j_ude_b_{side}", -1, $"j_sako_{side}");
                Add($"j_ude_b_{side}", $"j_te_{side}", -1, $"j_ude_a_{side}");
                Add($"j_te_{side}", $"j_oya_a_{side}", 0.45f, $"j_ude_b_{side}");
            }
        }
        else
        {
            Add("j_kosi", "j_sebo_a", 0.65f);
            if (settings.Template != GarmentTemplate.Skirt)
                foreach (var side in new[] { "l", "r" })
                {
                    Add($"j_asi_a_{side}", $"j_asi_b_{side}", -1, "j_kosi");
                    Add($"j_asi_b_{side}", $"j_asi_d_{side}", -1, $"j_asi_a_{side}");
                    Add($"j_asi_d_{side}", $"j_asi_e_{side}", 0.5f, $"j_asi_b_{side}");
                }
        }
        if (state.Bones.Count == 0) return null;
        state.DrivenAncestor = new int[Math.Min(source.BoneCount, source.ParentCount)];
        var byIndex = new Dictionary<int, int>();
        for (var i = 0; i < state.Bones.Count; i++) byIndex[state.Bones[i].Index] = i;
        for (var i = 0; i < state.DrivenAncestor.Length; i++)
        {
            var current = i; var driven = -1;
            for (var depth = 0; current >= 0 && current < state.DrivenAncestor.Length && depth < 128; depth++)
            {
                if (byIndex.TryGetValue(current, out driven)) break;
                driven = -1;
                current = source.HavokSkeleton->ParentIndices[current];
            }
            state.DrivenAncestor[i] = driven;
        }
        BuildRetainedSkirtPanels(source, state);
        return state;
    }

    private static void ResolveAttachmentFrame(SkeletonAccess skel, int index, int toward,
        Vector3 origin, Quaternion rootRotation, AttachmentFrame transport, out Vector3 center, out Quaternion frame)
    {
        ref var bone = ref skel.Pose->ModelPose.Data[index];
        center = origin + Vector3.Transform(new Vector3(bone.Translation.X, bone.Translation.Y, bone.Translation.Z), rootRotation);
        var boneRotation = Quaternion.Normalize(rootRotation * new Quaternion(bone.Rotation.X, bone.Rotation.Y,
            bone.Rotation.Z, bone.Rotation.W));
        var y = Vector3.Transform(Vector3.UnitY, boneRotation);
        if (toward >= 0)
        {
            ref var other = ref skel.Pose->ModelPose.Data[toward];
            var endpoint = origin + Vector3.Transform(new Vector3(other.Translation.X, other.Translation.Y, other.Translation.Z), rootRotation);
            y = AttachmentSimulation.SafeNormal(endpoint - center, y);
        }
        frame = transport.Update(boneRotation, y);
    }

    private void DriveRealAttachment(SkeletonAccess source, SkeletonAccess target, Clone clone,
        RealAttachmentState state, Vector3 origin, Quaternion rotation)
    {
        ((Character*)clone.Chara)->Timeline.OverallSpeed = 0f;
        var inverse = Quaternion.Inverse(rotation);
        for (var i = 0; i < state.DrivenAncestor.Length; i++)
        {
            ref var src = ref source.Pose->ModelPose.Data[i];
            ref var dst = ref target.Pose->ModelPose.Data[i];
            dst = src;
            var driven = state.DrivenAncestor[i];
            if (driven < 0) continue;
            var bone = state.Bones[driven];
            var offset = Vector3.Transform(bone.Offset, inverse);
            // Preserve the live bone's rotation and scale exactly. Only a bounded translation
            // is added, so the skirt keeps its authored shape and cannot flip from cage collapse.
            dst.Translation.X += offset.X; dst.Translation.Y += offset.Y; dst.Translation.Z += offset.Z;
        }
        DriveRetainedSkirt(source, target, state, origin, rotation);
        // Equipment can carry its own partial skeleton. Mirror compatible partials too rather
        // than leaving the clone's separate animation/cloth pose running under the live main pose.
        var sourceSkeleton = source.CharBase->Skeleton;
        var targetSkeleton = target.CharBase->Skeleton;
        if (sourceSkeleton != null && targetSkeleton != null)
            for (var part = 1; part < Math.Min(sourceSkeleton->PartialSkeletonCount, targetSkeleton->PartialSkeletonCount); part++)
            {
                var srcPose = sourceSkeleton->PartialSkeletons[part].GetHavokPose(0);
                var dstPose = targetSkeleton->PartialSkeletons[part].GetHavokPose(0);
                if (srcPose == null || dstPose == null || srcPose->Skeleton == null || dstPose->Skeleton == null ||
                    srcPose->ModelInSync == 0 || dstPose->ModelInSync == 0 ||
                    srcPose->ModelPose.Length != dstPose->ModelPose.Length ||
                    srcPose->Skeleton->Bones.Length < srcPose->ModelPose.Length ||
                    dstPose->Skeleton->Bones.Length < dstPose->ModelPose.Length) continue;
                var count = srcPose->ModelPose.Length;
                var compatible = true;
                for (var i = 0; i < count; i++)
                    if (srcPose->Skeleton->Bones[i].Name.String != dstPose->Skeleton->Bones[i].Name.String)
                    { compatible = false; break; }
                if (!compatible) continue;
                for (var i = 0; i < count; i++)
                {
                    var name = srcPose->Skeleton->Bones[i].Name.String;
                    var index = name == null ? -1 : FindBoneIndexByName(source, name);
                    var driven = index >= 0 && index < state.DrivenAncestor.Length ? state.DrivenAncestor[index] : -1;
                    // Partial-only bones inherit their already mapped parent; roots use the waist.
                    var parent = i < srcPose->Skeleton->ParentIndices.Length ? srcPose->Skeleton->ParentIndices[i] : -1;
                    var offset = Vector3.Transform(state.Bones[driven >= 0 ? driven : 0].Offset, inverse);
                    if (driven < 0 && parent >= 0 && parent < i)
                    {
                        ref var parentSource = ref srcPose->ModelPose.Data[parent];
                        ref var parentTarget = ref dstPose->ModelPose.Data[parent];
                        offset = new Vector3(parentTarget.Translation.X - parentSource.Translation.X,
                            parentTarget.Translation.Y - parentSource.Translation.Y,
                            parentTarget.Translation.Z - parentSource.Translation.Z);
                    }
                    ref var dst = ref dstPose->ModelPose.Data[i];
                    if (index >= 0 && index < state.DrivenAncestor.Length)
                        dst = target.Pose->ModelPose.Data[index];
                    else if (parent >= 0 && parent < i && srcPose->Skeleton->ReferencePose.Data != null && srcPose->Skeleton->ReferencePose.Length > i &&
                        IsPartialSkirtBone(srcPose->Skeleton, i))
                    {
                        // A partial-only skirt chain has no main-panel binding. Reconstruct its
                        // reference local pose on the copied parent, rather than importing native
                        // cloth jitter. This is a stable fallback, not another simulated panel.
                        ref var rest = ref srcPose->Skeleton->ReferencePose.Data[i];
                        ref var parentPose = ref dstPose->ModelPose.Data[parent];
                        var pr = new Quaternion(parentPose.Rotation.X, parentPose.Rotation.Y, parentPose.Rotation.Z, parentPose.Rotation.W);
                        var ps = new Vector3(parentPose.Scale.X, parentPose.Scale.Y, parentPose.Scale.Z);
                        var pt = new Vector3(parentPose.Translation.X, parentPose.Translation.Y, parentPose.Translation.Z);
                        var position = pt + Vector3.Transform(new Vector3(rest.Translation.X, rest.Translation.Y, rest.Translation.Z) * ps, pr);
                        var orientation = Quaternion.Normalize(pr * new Quaternion(rest.Rotation.X, rest.Rotation.Y, rest.Rotation.Z, rest.Rotation.W));
                        dst = rest;
                        dst.Translation.X = position.X; dst.Translation.Y = position.Y; dst.Translation.Z = position.Z;
                        dst.Rotation.X = orientation.X; dst.Rotation.Y = orientation.Y; dst.Rotation.Z = orientation.Z; dst.Rotation.W = orientation.W;
                        dst.Scale.X *= ps.X; dst.Scale.Y *= ps.Y; dst.Scale.Z *= ps.Z;
                    }
                    else
                    {
                        dst = srcPose->ModelPose.Data[i];
                        dst.Translation.X += offset.X; dst.Translation.Y += offset.Y; dst.Translation.Z += offset.Z;
                    }
                }
            }
        SetCloneBaseTransform(clone, origin, rotation);
    }

    private static bool FiniteAttachmentVector(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
