// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Camera;

/// <summary>
/// The world ray under a screen position (the mouse), for clicking on a model.
///
/// The game's own projection (<see cref="IGameGui.WorldToScreen(Vector3, out Vector2, out bool)"/>) is the only
/// authority on where a world point lands on screen, so the ray is defined by it: two points, near and far, each
/// solved until the game projects it onto the mouse. The camera basis and lens from <see cref="GameCameraView"/> only
/// give the first guess, so a wrong sign or lens there costs iterations, not accuracy.
/// </summary>
public static class ScreenRay
{
    private const float NearDepth = 1f;
    private const float FarDepth = 40f;
    private const int Iterations = 8;

    /// <summary>
    /// The ray through <paramref name="screen"/>. <paramref name="residualPixels"/> is how far the game projects the
    /// solved points from the mouse (the larger of the two); a few pixels at most when the ray is right.
    /// </summary>
    public static bool TryGet(IGameGui gameGui, Vector2 screen, Vector2 displaySize,
        out Vector3 origin, out Vector3 direction, out float residualPixels)
    {
        origin = direction = Vector3.Zero;
        residualPixels = float.MaxValue;
        if (!GameCameraView.TryRead(out var view) || displaySize.X < 1f || displaySize.Y < 1f) return false;

        Camera.DynamicCameraSolver.BasisFromForward(view.Forward, out var right, out var up);
        var ndcX = screen.X / displaySize.X * 2f - 1f;
        var ndcY = 1f - screen.Y / displaySize.Y * 2f;
        var a0 = ndcX * view.TanHalfH;
        var b0 = ndcY * view.TanHalfV;

        if (!Solve(gameGui, screen, view.Position, view.Forward, right, up, NearDepth, a0, b0, out var near, out var nearError) ||
            !Solve(gameGui, screen, view.Position, view.Forward, right, up, FarDepth, a0, b0, out var far, out var farError))
            return false;

        var span = far - near;
        if (span.LengthSquared() < 1e-6f) return false;
        origin = near;
        direction = Vector3.Normalize(span);
        residualPixels = MathF.Max(nearError, farError);
        return true;
    }

    // Gauss–Newton on the point cam + depth·(forward + a·right + b·up): adjust (a, b) until the game's projection of
    // it is the mouse. The Jacobian is measured by finite differences through the same projection.
    private static bool Solve(IGameGui gameGui, Vector2 target, Vector3 cam, Vector3 forward, Vector3 right, Vector3 up,
        float depth, float a, float b, out Vector3 point, out float error)
    {
        Vector3 At(float pa, float pb) => cam + depth * (forward + pa * right + pb * up);
        point = At(a, b);
        error = float.MaxValue;
        const float h = 1e-3f;
        for (var i = 0; i < Iterations; i++)
        {
            if (!Project(gameGui, point, out var p)) return false;
            var r = p - target;
            error = r.Length();
            if (error < 0.25f) return true;
            if (!Project(gameGui, At(a + h, b), out var pa) || !Project(gameGui, At(a, b + h), out var pb)) return false;
            var ja = (pa - p) / h;
            var jb = (pb - p) / h;
            var det = ja.X * jb.Y - jb.X * ja.Y;
            if (MathF.Abs(det) < 1e-6f) return false;
            // Solve [ja jb]·(da, db) = -r.
            var da = (-r.X * jb.Y + jb.X * r.Y) / det;
            var db = (-ja.X * r.Y + r.X * ja.Y) / det;
            a += da;
            b += db;
            point = At(a, b);
        }
        if (Project(gameGui, point, out var last)) error = (last - target).Length();
        return float.IsFinite(error);
    }

    private static bool Project(IGameGui gameGui, Vector3 world, out Vector2 screen)
    {
        // The two-out overload still fills the position when the point is off screen; false only when it is behind.
        var inFront = gameGui.WorldToScreen(world, out screen, out _);
        return inFront && float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }
}
