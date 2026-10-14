// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation.MeshVolume;

/// <summary>An unweighted skin contribution in its owning physical bone's local frame.</summary>
public readonly record struct MeshSkinInfluence(int BoneIndex, float Weight, Vector3 BoneLocalPosition);

public static class MeshSkinDeformer
{
    /// <summary>Render-order bone rotation, actor scale and root rotation, in row-vector convention.</summary>
    public static Matrix4x4 BoneTransform(Vector3 origin, Quaternion worldBoneRotation,
        Quaternion rootRotation, Vector3 rootScale)
    {
        rootRotation = Quaternion.Normalize(rootRotation);
        var modelRotation = Quaternion.Normalize(Quaternion.Inverse(rootRotation) * worldBoneRotation);
        // Actor scale acts in root/model space. Scaling an already world-rotated
        // contribution would shear it around the wrong axes for anisotropic mods.
        return Matrix4x4.CreateFromQuaternion(modelRotation) * Matrix4x4.CreateScale(rootScale) *
               Matrix4x4.CreateFromQuaternion(rootRotation) * Matrix4x4.CreateTranslation(origin);
    }

    public static bool Evaluate(MeshSkinInfluence[][] sources, ReadOnlySpan<Matrix4x4> boneTransforms,
        Span<Vector3> worldPositions)
    {
        if (worldPositions.Length != sources.Length) return false;
        for (var i = 0; i < sources.Length; i++)
        {
            // Capture may retain vertices belonging only to explicitly omitted faces.
            // The partitioner never references these unowned entries.
            if (sources[i].Length == 0) { worldPositions[i] = default; continue; }
            var point = Vector3.Zero;
            var sum = 0f;
            foreach (var influence in sources[i])
            {
                if ((uint)influence.BoneIndex >= boneTransforms.Length ||
                    !float.IsFinite(influence.Weight) || influence.Weight < 0f) return false;
                point += Vector3.Transform(influence.BoneLocalPosition, boneTransforms[influence.BoneIndex]) * influence.Weight;
                sum += influence.Weight;
            }
            if (!(sum > 0f)) return false;
            point /= sum;
            if (!Finite(point)) return false;
            worldPositions[i] = point;
        }
        return true;
    }

    public static bool Interpolate(ReadOnlySpan<Vector3> sourceWorld, ReadOnlySpan<MeshSourceBinding> bindings,
        Vector3 bodyPosition, Quaternion bodyOrientation, Span<Vector3> bodyLocal)
    {
        if (bindings.Length != bodyLocal.Length) return false;
        var inverse = Quaternion.Inverse(bodyOrientation);
        for (var i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            if ((uint)binding.A >= sourceWorld.Length || (uint)binding.B >= sourceWorld.Length ||
                (uint)binding.C >= sourceWorld.Length) return false;
            var bary = binding.Barycentric;
            var world = sourceWorld[binding.A] * bary.X + sourceWorld[binding.B] * bary.Y + sourceWorld[binding.C] * bary.Z;
            var local = Vector3.Transform(world - bodyPosition, inverse);
            if (!Finite(local)) return false;
            bodyLocal[i] = local;
        }
        return true;
    }

    private static bool Finite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
