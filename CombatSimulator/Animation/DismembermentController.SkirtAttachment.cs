using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.Attachment;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Animation;

public unsafe partial class DismembermentController
{
    private static bool IsPartialSkirtBone(FFXIVClientStructs.Havok.Animation.Rig.hkaSkeleton* skeleton, int index)
    {
        for (var depth = 0; depth < 128 && index >= 0 && index < skeleton->Bones.Length && index < skeleton->ParentIndices.Length; depth++)
        {
            if (skeleton->Bones[index].Name.String?.StartsWith("j_sk_", StringComparison.Ordinal) == true) return true;
            index = skeleton->ParentIndices[index];
        }
        return false;
    }

    private sealed class SkirtBoneBinding
    {
        public int Index;
        public Vector3 Position, Scale;
        public Quaternion Rotation;
    }
    private sealed class SkirtPanelBinding
    {
        public int Anchor;
        public Vector3 Pivot;
        public RetainedSkirtPanel Solver = null!;
        public readonly List<SkirtBoneBinding> Bones = new();
        public readonly List<RetainedSkirtPanel.Capsule> Contacts = new();
        public Vector3 WorldPivot, WorldTip;
    }

    private void BuildRetainedSkirtPanels(SkeletonAccess source, RealAttachmentState state)
    {
        var count = Math.Min(source.BoneCount, Math.Min(source.ParentCount, source.Pose->ModelPose.Length));
        if (source.HavokSkeleton->ReferencePose.Data == null || source.HavokSkeleton->ReferencePose.Length < count)
        {
            log.Warning("Attachment: skirt reference pose unavailable; no procedural skirt physics created.");
            return;
        }
        var reference = new Matrix4x4[count];
        var owners = new int[count]; Array.Fill(owners, -1);
        for (var i = 0; i < count; i++)
        {
            ref var pose = ref source.HavokSkeleton->ReferencePose.Data[i];
            var local = Matrix4x4.CreateScale(pose.Scale.X, pose.Scale.Y, pose.Scale.Z) *
                Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W))) *
                Matrix4x4.CreateTranslation(pose.Translation.X, pose.Translation.Y, pose.Translation.Z);
            var parent = source.HavokSkeleton->ParentIndices[i];
            reference[i] = parent >= 0 && parent < i ? local * reference[parent] : local;
            var name = source.HavokSkeleton->Bones[i].Name.String;
            if (parent >= 0 && parent < i && owners[parent] >= 0) owners[i] = owners[parent];
            else if (name?.StartsWith("j_sk_", StringComparison.Ordinal) == true && parent >= 0 && parent < i &&
                Matrix4x4.Invert(reference[parent], out var inverse))
            {
                var rootLocal = reference[i] * inverse;
                owners[i] = state.SkirtPanels.Count;
                state.SkirtPanels.Add(new SkirtPanelBinding { Anchor = parent, Pivot = rootLocal.Translation });
            }
            if (owners[i] < 0) continue;
            var panel = state.SkirtPanels[owners[i]];
            if (!Matrix4x4.Invert(reference[panel.Anchor], out var anchorInverse) ||
                !Matrix4x4.Decompose(reference[i] * anchorInverse, out var scale, out var rotation, out var position)) continue;
            panel.Bones.Add(new SkirtBoneBinding { Index = i, Position = position, Rotation = rotation, Scale = scale });
        }
        var waist = FindBoneIndexByName(source, "j_kosi");
        var spine = FindBoneIndexByName(source, "j_sebo_a");
        var up = waist >= 0 && spine >= 0 && waist < count && spine < count
            ? AttachmentSimulation.SafeNormal(reference[spine].Translation - reference[waist].Translation, Vector3.UnitY)
            : Vector3.UnitY;
        for (var i = state.SkirtPanels.Count - 1; i >= 0; i--)
        {
            var panel = state.SkirtPanels[i];
            if (panel.Bones.Count == 0 || !Matrix4x4.Invert(reference[panel.Anchor], out var inverse))
            { state.SkirtPanels.RemoveAt(i); continue; }
            var localUp = AttachmentSimulation.SafeNormal(Vector3.TransformNormal(up, inverse), Vector3.UnitY);
            var radial = panel.Pivot - localUp * Vector3.Dot(panel.Pivot, localUp);
            if (radial.LengthSquared() < 1e-6f)
            {
                // A central/non-radial skirt chain has no defined outward hinge. Keep its rest
                // pose instead of inventing a changing axis from its noisy live cloth rotation.
                radial = Vector3.UnitZ - localUp * Vector3.Dot(Vector3.UnitZ, localUp);
            }
            var axis = AttachmentSimulation.SafeNormal(Vector3.Cross(-localUp, radial), Vector3.UnitX);
            var segments = new List<RetainedSkirtPanel.Segment>();
            foreach (var bone in panel.Bones)
            {
                var parent = source.HavokSkeleton->ParentIndices[bone.Index];
                var parentBone = panel.Bones.Find(b => b.Index == parent);
                if (parentBone == null) continue;
                segments.Add(new(parentBone.Position - panel.Pivot, bone.Position - panel.Pivot));
            }
            if (segments.Count == 0)
                segments.Add(new(Vector3.Zero, -localUp * .15f)); // Single-bone proxy tip; not a mesh-derived hem.
            panel.Solver = new RetainedSkirtPanel(segments.ToArray(), axis, .025f);
        }
    }

    private static readonly (string Bone, string End, float Radius)[] RetainedSkirtCapsules =
    {
        ("j_kosi", "j_sebo_a", .105f),
        ("j_asi_a_l", "j_asi_b_l", .075f), ("j_asi_a_r", "j_asi_b_r", .075f),
        ("j_asi_b_l", "j_asi_d_l", .055f), ("j_asi_b_r", "j_asi_d_r", .055f),
    };

    private void DriveRetainedSkirt(SkeletonAccess source, SkeletonAccess target, RealAttachmentState state,
        Vector3 origin, Quaternion rootRotation)
    {
        var worldCapsules = state.SkirtCapsules;
        worldCapsules.Clear();
        foreach (var (name, end, fallbackRadius) in RetainedSkirtCapsules)
        {
            if (PlayerRagdollController?.TryGetBoneCapsule(name, out var capsule) == true)
            {
                var axis = Vector3.Transform(Vector3.UnitY, capsule.Orientation) * capsule.HalfLength;
                worldCapsules.Add(new(capsule.Center - axis, capsule.Center + axis, capsule.Radius));
            }
            else
            {
                var a = FindBoneIndexByName(source, name); var b = FindBoneIndexByName(source, end);
                if (a < 0 || b < 0) continue;
                ref var pa = ref source.Pose->ModelPose.Data[a]; ref var pb = ref source.Pose->ModelPose.Data[b];
                worldCapsules.Add(new(origin + Vector3.Transform(new Vector3(pa.Translation.X, pa.Translation.Y, pa.Translation.Z), rootRotation),
                    origin + Vector3.Transform(new Vector3(pb.Translation.X, pb.Translation.Y, pb.Translation.Z), rootRotation), fallbackRadius * state.Scale));
            }
        }
        state.SkirtFloorAge += attachmentFrameDt;
        if (state.SkirtFloorAge >= .2f || Vector3.DistanceSquared(origin, state.SkirtFloorSample) > .01f)
        {
            state.SkirtFloorAge = 0; state.SkirtFloorSample = origin;
            state.SkirtHasFloor = BGCollisionModule.RaycastMaterialFilter(origin + Vector3.UnitY,
                -Vector3.UnitY, out var hit, 8f);
            if (state.SkirtHasFloor && (!FiniteAttachmentVector(state.SkirtFloorPoint) ||
                MathF.Abs(hit.Point.Y - state.SkirtFloorPoint.Y) > .00025f)) state.SkirtFloorPoint = hit.Point;
        }
        foreach (var panel in state.SkirtPanels)
        {
            // This parent is outside the skirt subtree and was copied from the current body pose.
            ref var anchor = ref target.Pose->ModelPose.Data[panel.Anchor];
            var position = new Vector3(anchor.Translation.X, anchor.Translation.Y, anchor.Translation.Z);
            var rotation = Quaternion.Normalize(new Quaternion(anchor.Rotation.X, anchor.Rotation.Y, anchor.Rotation.Z, anchor.Rotation.W));
            var anchorScale = new Vector3(anchor.Scale.X, anchor.Scale.Y, anchor.Scale.Z);
            var worldRotation = Quaternion.Normalize(rootRotation * rotation);
            var inverse = Quaternion.Inverse(worldRotation);
            panel.WorldPivot = origin + Vector3.Transform(position + Vector3.Transform(panel.Pivot * anchorScale, rotation), rootRotation);
            // Uniform local collision units; nonuniform scales conservatively use the smallest axis.
            var unit = MathF.Max(.1f, MathF.Min(MathF.Abs(anchorScale.X), MathF.Min(MathF.Abs(anchorScale.Y), MathF.Abs(anchorScale.Z))));
            panel.Contacts.Clear();
            foreach (var cap in worldCapsules)
                panel.Contacts.Add(new(Vector3.Transform(cap.A - panel.WorldPivot, inverse) / unit,
                    Vector3.Transform(cap.B - panel.WorldPivot, inverse) / unit, cap.Radius / unit));
            if (state.SkirtHasFloor)
            {
                var point = Vector3.Transform(state.SkirtFloorPoint - panel.WorldPivot, inverse) / unit;
                var normal = Vector3.Transform(Vector3.UnitY, inverse);
                panel.Solver.Ground = new Plane(normal, -Vector3.Dot(normal, point));
            }
            else panel.Solver.Ground = null;
            panel.Solver.Advance(attachmentFrameDt, Vector3.Transform(-Vector3.UnitY * 9.81f, inverse), panel.Contacts,
                state.Settings.SkirtSwingDegrees * MathF.PI / 180, state.Settings.Thickness * state.Scale / unit,
                state.Settings.SkirtStiffness, state.Settings.SkirtDamping);
            var swing = panel.Solver.Rotation;
            foreach (var bone in panel.Bones)
            {
                ref var dst = ref target.Pose->ModelPose.Data[bone.Index];
                var local = panel.Pivot + Vector3.Transform(bone.Position - panel.Pivot, swing);
                var point = position + Vector3.Transform(local * anchorScale, rotation);
                var orientation = Quaternion.Normalize(rotation * swing * bone.Rotation);
                dst.Translation.X = point.X; dst.Translation.Y = point.Y; dst.Translation.Z = point.Z;
                dst.Rotation.X = orientation.X; dst.Rotation.Y = orientation.Y; dst.Rotation.Z = orientation.Z; dst.Rotation.W = orientation.W;
                var scale = bone.Scale * anchorScale;
                dst.Scale.X = scale.X; dst.Scale.Y = scale.Y; dst.Scale.Z = scale.Z;
                panel.WorldTip = origin + Vector3.Transform(point, rootRotation);
            }
        }
    }
}
