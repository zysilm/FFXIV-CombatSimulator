// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private Vector3 ModelToWorld(Vector3 modelPos)
        => skelWorldPos + Vector3.Transform(modelPos * skelWorldScale, skelWorldRot);

    private Vector3 WorldToModel(Vector3 worldPos)
        => DivideComponents(Vector3.Transform(worldPos - skelWorldPos, skelWorldRotInv), skelWorldScale);

    private Quaternion ModelRotToWorld(Quaternion modelRot)
        => Quaternion.Normalize(skelWorldRot * modelRot);

    private Quaternion WorldRotToModel(Quaternion worldRot)
        => Quaternion.Normalize(skelWorldRotInv * worldRot);

    // Static overloads for NPC skeletons (each NPC has its own transform)
    private static Vector3 NpcModelToWorld(Vector3 modelPos, Vector3 skelPos, Quaternion skelRot, Vector3 skelScale)
        => skelPos + Vector3.Transform(modelPos * skelScale, skelRot);

    private static Vector3 DivideComponents(Vector3 value, Vector3 divisor)
        => new(value.X / divisor.X, value.Y / divisor.Y, value.Z / divisor.Z);

    private static Quaternion NpcModelRotToWorld(Quaternion modelRot, Quaternion skelRot)
        => Quaternion.Normalize(skelRot * modelRot);

    internal static Vector3 GetSkeletonScale(ISkeletonPose ns)
    {
        if (!ns.HasRoot)
            return Vector3.One;

        var scale = ns.Root.Scale;
        if (!IsFinite(scale) || MathF.Abs(scale.X) < 1e-6f || MathF.Abs(scale.Y) < 1e-6f || MathF.Abs(scale.Z) < 1e-6f)
            return Vector3.One;

        return scale;
    }

    /// <summary>
    /// Read the scale that is actually applied to the rendered character. The NPC Scale effect
    /// writes DrawObject.Scale directly, while ordinary actors normally expose the same
    /// value through Skeleton.Transform.Scale. Prefer the draw object so collision follows live
    /// live scale changes, and retain the skeleton value as a safe fallback.
    /// </summary>
    internal Vector3 GetCharacterScale(nint address, ISkeletonPose ns)
    {
        if (TryGetDrawObjectScale(address, out var drawScale))
            return drawScale;

        return GetSkeletonScale(ns);
    }

    private bool TryGetDrawObjectScale(nint address, out Vector3 scale)
    {
        scale = Vector3.One;
        if (address != nint.Zero)
        {
            if (actors.TryGetDrawScale(address, out scale))
            {
                if (IsFinite(scale) && MathF.Abs(scale.X) >= 1e-6f && MathF.Abs(scale.Y) >= 1e-6f && MathF.Abs(scale.Z) >= 1e-6f)
                    return true;
            }
        }

        scale = Vector3.One;
        return false;
    }

    // Capsules cannot represent non-uniform scale. Use the largest component so their volume
    // encloses the visual bone instead of leaving a thin axis that can pass through a corpse.
    private static float CollisionScale(Vector3 scale)
        => MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));

    internal static Matrix4x4 QsToMatrix(BoneQs transform)
    {
        var scale = new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z);
        var rotation = Quaternion.Normalize(new Quaternion(
            transform.Rotation.X,
            transform.Rotation.Y,
            transform.Rotation.Z,
            transform.Rotation.W));
        var translation = new Vector3(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);
        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(translation);
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
