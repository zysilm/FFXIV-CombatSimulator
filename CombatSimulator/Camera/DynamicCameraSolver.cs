// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Camera;

/// <summary>
/// Framing math for the Dynamic Camera. Pure functions — no game pointers, no state.
///
/// Everything here runs in the solver's OWN angle convention: yaw ψ is the azimuth of the
/// pivot→camera direction ((sinψ, cosψ) in xz), and χ is the photographer's angle, positive
/// tipping the lens down (camera above, looking down). Game conventions never enter: the
/// current view is measured (<see cref="GameCameraView"/>), planned views are built with
/// <see cref="BasisFromAngles"/>, and both are projected through a measured <see cref="Lens"/>.
///
/// Shared by the combat framing, the automatic death shots (Camera/DeathShots, which use the
/// basis and the projection) and Tests/DeathShots. The fit of the old death framing (the
/// "prone photographer") left with that framing; the offline baseline that reproduces it keeps
/// its own copy (Tests/DeathShots/OldDirectedFraming.cs).
/// </summary>
public static class DynamicCameraSolver
{
    /// <summary>Projection scales: tan of the half field of view per axis. Read off the
    /// game's projection matrix for the current view; scaled for planned views.</summary>
    public readonly record struct Lens(float TanHalfH, float TanHalfV)
    {
        public bool IsValid => MathF.Abs(TanHalfH) > 1e-4f && MathF.Abs(TanHalfV) > 1e-4f;

        /// <summary>The lens a planned FoV value would produce, assuming the game maps its
        /// FoV field to the projection by a fixed scale — measured from the current pair
        /// (<paramref name="currentGameFov"/> → this lens), which absorbs whether the field
        /// is a vertical angle, a horizontal angle, or anything else linear in tan-space.</summary>
        public Lens ScaledToFov(float plannedGameFov, float currentGameFov)
        {
            var tanNow = MathF.Tan(Math.Clamp(currentGameFov, 0.05f, 2.8f) * 0.5f);
            var tanPlan = MathF.Tan(Math.Clamp(plannedGameFov, 0.05f, 2.8f) * 0.5f);
            if (tanNow < 1e-4f)
                return this;
            var k = tanPlan / tanNow;
            return new Lens(TanHalfH * k, TanHalfV * k);
        }
    }

    /// <summary>Basis for a PLANNED camera at the solver's angles (χ positive = looking
    /// down). Self-consistent with <see cref="MeasureAngles"/> and <see cref="Project"/>.</summary>
    public static void BasisFromAngles(float yaw, float chi, out Vector3 fwd, out Vector3 right, out Vector3 up)
    {
        var cc = MathF.Cos(chi);
        var sc = MathF.Sin(chi);
        var sy = MathF.Sin(yaw);
        var cy = MathF.Cos(yaw);

        // Pivot→camera = (sinψ·cosχ, sinχ, cosψ·cosχ); the camera looks back along it.
        fwd = new Vector3(-sy * cc, -sc, -cy * cc);
        FinishBasis(fwd, yaw, out right, out up);
    }

    /// <summary>Basis for the MEASURED view direction (no-roll assumption; the dynamic
    /// camera never writes roll).</summary>
    public static void BasisFromForward(Vector3 fwd, out Vector3 right, out Vector3 up)
    {
        FinishBasis(fwd, MeasureYaw(fwd), out right, out up);
    }

    private static void FinishBasis(Vector3 fwd, float yawForDegenerate, out Vector3 right, out Vector3 up)
    {
        var r = Vector3.Cross(Vector3.UnitY, fwd);
        if (r.LengthSquared() < 1e-6f)
        {
            // Looking straight up/down: the horizontal cross product degenerates.
            r = new Vector3(-MathF.Cos(yawForDegenerate), 0f, MathF.Sin(yawForDegenerate));
        }
        right = Vector3.Normalize(r);
        up = Vector3.Cross(fwd, right);
    }

    /// <summary>Solver angles of a measured view direction. Inverse of
    /// <see cref="BasisFromAngles"/>.</summary>
    public static void MeasureAngles(Vector3 fwd, out float yaw, out float chi)
    {
        yaw = MeasureYaw(fwd);
        chi = -MathF.Asin(Math.Clamp(fwd.Y, -1f, 1f));
    }

    private static float MeasureYaw(Vector3 fwd) => MathF.Atan2(-fwd.X, -fwd.Z);

    /// <summary>
    /// Normalised device coords of a world point: x,y ∈ [-1,1] inside the frame.
    /// z is view depth (negative = behind the camera).
    /// </summary>
    public static Vector3 Project(Vector3 x, Vector3 cam, in Vector3 fwd, in Vector3 right, in Vector3 up,
        in Lens lens)
    {
        var v = x - cam;
        var depth = Vector3.Dot(fwd, v);
        if (MathF.Abs(depth) < 1e-4f)
            depth = depth < 0f ? -1e-4f : 1e-4f;

        var ndcX = Vector3.Dot(right, v) / (depth * lens.TanHalfH);
        var ndcY = Vector3.Dot(up, v) / (depth * lens.TanHalfV);
        return new Vector3(ndcX, ndcY, depth);
    }

    /// <summary>
    /// Distance at which a subject of height h fills <paramref name="share"/> of the
    /// screen height, for a lens with the given vertical scale. Inverse of the vertical
    /// projection — used by combat framing to hold the character at a constant on-screen
    /// size.
    /// </summary>
    public static float DistanceForScreenShare(float height, float share, float tanHalfV)
    {
        var s = Math.Clamp(share, 0.05f, 0.95f);
        var half = MathF.Abs(tanHalfV) * s;
        if (half < 1e-4f)
            return 10f;
        return MathF.Max(0.5f, height * 0.5f / half);
    }
}
