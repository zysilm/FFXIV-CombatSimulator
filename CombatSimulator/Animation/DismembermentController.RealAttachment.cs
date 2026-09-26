// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.Attachment;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Animation;

public unsafe partial class DismembermentController
{
    private readonly List<(long Owner, nint Source, AttachmentSimulation.ContactPoint Point)> attachmentLayers = new();

    private void CaptureRealAttachmentLayers()
    {
        // Both garments consume the same previous-frame sample, independent of clone update order.
        attachmentLayers.Clear();
        foreach (var c in clones)
            if (c.RealAttachment is { } state)
                foreach (var p in state.Simulation.Particles)
                    attachmentLayers.Add((c.CreatedSeq, c.SourceAddress,
                        new AttachmentSimulation.ContactPoint(p.Position, p.Velocity, state.Settings.Thickness * state.Scale)));
    }
    private sealed class AttachmentBone
    {
        public string Name = "";
        public int Index, Toward = -1, Ring;
        public Vector3 SourceCenter, FloorSample;
        public Quaternion SourceFrame;
        public readonly AttachmentFrame Frame = new();
        public float FloorAge = 1f;
    }

    private sealed class RealAttachmentState
    {
        public AttachmentSimulation Simulation = null!;
        public AttachmentSettings Settings = null!;
        public readonly List<AttachmentBone> Bones = new();
        public readonly Dictionary<string, Vector3> PreviousCapsuleCenters = new();
        public int[] DrivenAncestor = Array.Empty<int>();
        public int SkeletonSignature;
        public float Scale;
        public Vector3[] RenderCenters = Array.Empty<Vector3>();
        public Quaternion[] RenderDeltas = Array.Empty<Quaternion>();
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

    public void CollectAttachmentDebug(List<(Vector3 A, Vector3 B)> edges, List<AttachmentDebugPoint> points)
    {
        edges.Clear(); points.Clear();
        foreach (var c in clones)
        {
            if (c.RealAttachment is not { } state) continue;
            edges.AddRange(state.Simulation.DebugEdges());
            foreach (var p in state.Simulation.Particles)
                points.Add(new AttachmentDebugPoint(p.Position, p.Target, p.Range >= 0,
                    p.ContactNormal.LengthSquared() > 0, p.Tension));
        }
    }

    public string GetAttachmentStatus()
    {
        var cages = 0; var nodes = 0; var contacts = 0; var asleep = 0; var recoveries = 0;
        foreach (var c in clones)
        {
            if (c.RealAttachment is not { } state) continue;
            cages++; nodes += state.Simulation.Particles.Count;
            contacts += state.Simulation.ContactCount;
            if (state.Simulation.Sleeping) asleep++;
            recoveries += state.Simulation.RecoveryCount;
        }
        return $"Attached: {cages} | Nodes: {nodes} | Contacts: {contacts} | Sleeping: {asleep} | Recoveries: {recoveries}";
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
            state.Simulation.SetTarget(bone.Ring, bone.SourceCenter, bone.SourceFrame);
        }
        UpdateAttachmentCollisions(source, c, state, origin, rotation);
        foreach (var bone in state.Bones)
        {
            var center = state.Simulation.Center(bone.Ring);
            bone.FloorAge += attachmentFrameDt;
            if (bone.FloorAge < 0.12f && Vector3.DistanceSquared(center, bone.FloorSample) < 0.0025f) continue;
            bone.FloorAge = 0; bone.FloorSample = center;
            var valid = SampleAttachmentFloor(center, out var floor, out var normal);
            state.Simulation.SetFloor(bone.Ring, valid, floor, normal);
        }
        state.Simulation.Advance(attachmentFrameDt);
        DriveRealAttachment(source, target, c, state, origin, rotation);
        return true;
    }

    private RealAttachmentState? CreateRealAttachment(SkeletonAccess source, Clone c,
        Vector3 origin, Quaternion rotation, int signature)
    {
        var settings = ResolveAttachmentSettings(c);
        var scale = Math.Clamp(c.SourceScale.X, 0.1f, 10f);
        var state = new RealAttachmentState { Settings = settings, SkeletonSignature = signature, Scale = scale,
            Simulation = new AttachmentSimulation(settings, scale) };
        var rings = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string name, string toward, float radius, float attachment, string? parent = null)
        {
            if (rings.ContainsKey(name) || state.Bones.Count >= 48) return;
            var index = FindBoneIndexByName(source, name);
            if (index < 0) return;
            var next = FindBoneIndexByName(source, toward);
            var binding = new AttachmentBone { Name = name, Index = index, Toward = next };
            ResolveAttachmentFrame(source, index, next, origin, rotation, binding.Frame, out var center, out var frame);
            if (PlayerRagdollController?.TryGetBoneCapsule(name, out var cap) == true)
                radius = MathF.Max(radius * scale, cap.Radius) / scale;
            if (settings.Anchors.TryGetValue(name, out var custom)) attachment = custom;
            var r = state.Simulation.AddRing(name, center, frame,
                radius * scale * settings.OpeningScale + settings.Thickness * scale, attachment);
            rings.Add(name, r);
            binding.Ring = r; binding.SourceCenter = center; binding.SourceFrame = frame;
            state.Bones.Add(binding);
            if (parent != null && rings.TryGetValue(parent, out var p)) state.Simulation.Connect(p, r);
        }

        if (c.GearKeepModelSlot == 1)
        {
            Add("j_kosi", "j_sebo_a", 0.12f, -1);
            Add("j_sebo_a", "j_sebo_b", 0.115f, -1, "j_kosi");
            Add("j_sebo_b", "j_sebo_c", 0.13f, -1, "j_sebo_a");
            Add("j_sebo_c", "j_kubi", 0.13f, -1, "j_sebo_b");
            Add("j_kubi", "j_kao", 0.065f, 0.7f, "j_sebo_c");
            foreach (var side in new[] { "l", "r" })
            {
                Add($"j_sako_{side}", $"j_ude_a_{side}", 0.065f, 1f, "j_sebo_c");
                Add($"j_ude_a_{side}", $"j_ude_b_{side}", 0.055f, -1, $"j_sako_{side}");
                Add($"j_ude_b_{side}", $"j_te_{side}", 0.043f, -1, $"j_ude_a_{side}");
                Add($"j_te_{side}", $"j_oya_a_{side}", 0.038f, 0.45f, $"j_ude_b_{side}");
            }
        }
        else
        {
            Add("j_kosi", "j_sebo_a", 0.13f, 0.65f);
            if (settings.Template != GarmentTemplate.Skirt)
                foreach (var side in new[] { "l", "r" })
                {
                    Add($"j_asi_a_{side}", $"j_asi_b_{side}", 0.085f, -1, "j_kosi");
                    Add($"j_asi_b_{side}", $"j_asi_d_{side}", 0.062f, -1, $"j_asi_a_{side}");
                    Add($"j_asi_d_{side}", $"j_asi_e_{side}", 0.048f, 0.5f, $"j_asi_b_{side}");
                }
        }
        if (settings.Template is GarmentTemplate.Coat or GarmentTemplate.Dress or GarmentTemplate.Skirt)
        {
            // Authored skirt columns, including intermediate bones. Their actual parent topology
            // supplies the cloth links; it does not assume a particular number of skirt columns.
            var count = Math.Min(source.BoneCount, source.ParentCount);
            for (var i = 0; i < count; i++)
            {
                var name = source.HavokSkeleton->Bones[i].Name.String;
                if (name == null || !name.StartsWith("j_sk_", StringComparison.Ordinal)) continue;
                var parentIndex = source.HavokSkeleton->ParentIndices[i];
                var parent = parentIndex >= 0 ? source.HavokSkeleton->Bones[parentIndex].Name.String : null;
                Add(name, parent ?? "j_kosi", 0.024f, -1, rings.ContainsKey(parent ?? "") ? parent : "j_kosi");
            }
        }
        if (settings.Template == GarmentTemplate.Rigid)
        {
            // Dense cross braces retain the cage shape. Connections remain flexible at the body.
            for (var i = 0; i < state.Bones.Count; i++)
                for (var j = i + 2; j < state.Bones.Count; j++) state.Simulation.Connect(i, j);
        }
        if (state.Bones.Count == 0) return null;
        var anchored = state.Simulation.Rings.Exists(r => r.AnchorRange >= 0);
        if (!anchored) return null;
        state.RenderCenters = new Vector3[state.Bones.Count];
        state.RenderDeltas = new Quaternion[state.Bones.Count];
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
        UpdateAttachmentCollisions(source, c, state, origin, rotation);
        state.Simulation.FitToBody();
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

    private static readonly (string Bone, string End, float Radius)[] AttachmentBodyCapsules =
    {
        ("j_kosi", "j_sebo_a", 0.105f), ("j_sebo_a", "j_sebo_b", 0.1f),
        ("j_sebo_b", "j_sebo_c", 0.115f), ("j_sebo_c", "j_kubi", 0.1f),
        ("j_kubi", "j_kao", 0.06f), ("j_kao", "j_kao", 0.095f),
        ("j_sako_l", "j_ude_a_l", 0.06f), ("j_sako_r", "j_ude_a_r", 0.06f),
        ("j_ude_a_l", "j_ude_b_l", 0.05f), ("j_ude_a_r", "j_ude_b_r", 0.05f),
        ("j_ude_b_l", "j_te_l", 0.038f), ("j_ude_b_r", "j_te_r", 0.038f),
        ("j_asi_a_l", "j_asi_b_l", 0.075f), ("j_asi_a_r", "j_asi_b_r", 0.075f),
        ("j_asi_b_l", "j_asi_d_l", 0.055f), ("j_asi_b_r", "j_asi_d_r", 0.055f),
        ("j_asi_d_l", "j_asi_e_l", 0.05f), ("j_asi_d_r", "j_asi_e_r", 0.05f),
    };

    private void UpdateAttachmentCollisions(SkeletonAccess source, Clone clone, RealAttachmentState state,
        Vector3 origin, Quaternion rotation)
    {
        var simulation = state.Simulation;
        simulation.Capsules.Clear();
        foreach (var def in AttachmentBodyCapsules)
        {
            Vector3 a, b; float radius;
            if (PlayerRagdollController?.TryGetBoneCapsule(def.Bone, out var capsule) == true)
            {
                var axis = Vector3.Transform(Vector3.UnitY, capsule.Orientation) * capsule.HalfLength;
                a = capsule.Center - axis; b = capsule.Center + axis; radius = capsule.Radius;
            }
            else
            {
                var i = FindBoneIndexByName(source, def.Bone); var j = FindBoneIndexByName(source, def.End);
                if (i < 0 || j < 0) continue;
                ref var pa = ref source.Pose->ModelPose.Data[i]; ref var pb = ref source.Pose->ModelPose.Data[j];
                a = origin + Vector3.Transform(new Vector3(pa.Translation.X, pa.Translation.Y, pa.Translation.Z), rotation);
                b = origin + Vector3.Transform(new Vector3(pb.Translation.X, pb.Translation.Y, pb.Translation.Z), rotation);
                radius = def.Radius * state.Scale;
            }
            var center = (a + b) * 0.5f;
            var velocity = state.PreviousCapsuleCenters.TryGetValue(def.Bone, out var previous)
                ? ClampVectorLength((center - previous) / MathF.Max(attachmentFrameDt, 0.001f), 15f) : Vector3.Zero;
            state.PreviousCapsuleCenters[def.Bone] = center;
            simulation.Capsules.Add(new AttachmentSimulation.Capsule(a, b, radius, velocity));
        }
        simulation.OtherGarments.Clear();
        foreach (var sample in attachmentLayers)
        {
            if (sample.Owner == clone.CreatedSeq || sample.Source != clone.SourceAddress) continue;
            simulation.OtherGarments.Add(sample.Point);
        }
    }

    private static bool SampleAttachmentFloor(Vector3 position, out Vector3 floor, out Vector3 normal)
    {
        normal = Vector3.UnitY; floor = default;
        // Short upward allowance avoids selecting a ceiling above the garment.
        if (!BGCollisionModule.RaycastMaterialFilter(position + Vector3.UnitY * 0.35f,
                -Vector3.UnitY, out var hit, 4f)) return false;
        floor = hit.Point;
        const float offset = 0.06f;
        if (BGCollisionModule.RaycastMaterialFilter(position + new Vector3(offset, 0.35f, 0), -Vector3.UnitY, out var hx, 4f) &&
            BGCollisionModule.RaycastMaterialFilter(position + new Vector3(0, 0.35f, offset), -Vector3.UnitY, out var hz, 4f) &&
            MathF.Abs(hx.Point.Y - floor.Y) < 0.15f && MathF.Abs(hz.Point.Y - floor.Y) < 0.15f)
            normal = AttachmentSimulation.SafeNormal(Vector3.Cross(hz.Point - floor, hx.Point - floor), Vector3.UnitY);
        return true;
    }

    private void DriveRealAttachment(SkeletonAccess source, SkeletonAccess target, Clone clone,
        RealAttachmentState state, Vector3 origin, Quaternion rotation)
    {
        var inverse = Quaternion.Inverse(rotation);
        var centers = state.RenderCenters;
        var deltas = state.RenderDeltas;
        for (var i = 0; i < state.Bones.Count; i++)
        {
            var bone = state.Bones[i];
            centers[i] = state.Simulation.DrivenCenter(bone.Ring);
            deltas[i] = Quaternion.Normalize(state.Simulation.Rotation(bone.Ring) * Quaternion.Inverse(bone.SourceFrame));
        }
        for (var i = 0; i < state.DrivenAncestor.Length; i++)
        {
            ref var src = ref source.Pose->ModelPose.Data[i];
            ref var dst = ref target.Pose->ModelPose.Data[i];
            dst = src;
            var driven = state.DrivenAncestor[i];
            if (driven < 0) continue;
            var bone = state.Bones[driven];
            var world = origin + Vector3.Transform(new Vector3(src.Translation.X, src.Translation.Y, src.Translation.Z), rotation);
            var position = Vector3.Transform(centers[driven] + Vector3.Transform(world - bone.SourceCenter, deltas[driven]) - origin, inverse);
            var q = Quaternion.Normalize(inverse * deltas[driven] * rotation *
                new Quaternion(src.Rotation.X, src.Rotation.Y, src.Rotation.Z, src.Rotation.W));
            dst.Translation.X = position.X; dst.Translation.Y = position.Y; dst.Translation.Z = position.Z;
            dst.Rotation.X = q.X; dst.Rotation.Y = q.Y; dst.Rotation.Z = q.Z; dst.Rotation.W = q.W;
        }
        SetCloneBaseTransform(clone, origin, rotation);
    }

    private static bool FiniteAttachmentVector(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
