// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>
/// Where the face points, from the head bone's rotation. The torso's front (<see cref="BodyFrame.Front"/>) says
/// nothing about a head that is turned or thrown back, and bone positions alone cannot (the head is one point): the
/// death shots' side preference needs the head itself.
///
/// No axis of the bone is assumed: the rotation asked for is the head bone's world rotation relative to its bind
/// pose (skeleton rotation × model rotation × inverse of the bone's model rotation in the reference pose). In the
/// reference pose the character faces +Z of model space, so that rotation applied to +Z is the face (the way the
/// head look-at does it, Animation/HeadLookAtMath). Game-free: the caller reads the rotations (the plugin from the
/// live skeleton, whose pose is the animated one before the ragdoll and the one the ragdoll wrote back after; the
/// offline check from the recorded poses and the recorded reference pose).
/// </summary>
public static class DeathShotFace
{
    /// <summary>Unit vector out of the face for the head bone's world rotation relative to its bind pose; zero when
    /// the rotation is unusable.</summary>
    public static Vector3 Direction(Quaternion headWorldRotation)
    {
        if (!float.IsFinite(headWorldRotation.X) || !float.IsFinite(headWorldRotation.Y) ||
            !float.IsFinite(headWorldRotation.Z) || !float.IsFinite(headWorldRotation.W) ||
            headWorldRotation.LengthSquared() < 1e-6f)
            return Vector3.Zero;
        var v = Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(headWorldRotation));
        return v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : Vector3.Zero;
    }
}
