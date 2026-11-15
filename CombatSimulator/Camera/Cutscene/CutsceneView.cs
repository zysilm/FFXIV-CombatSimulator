// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Camera.Cutscene;

/// <summary>
/// The lens and pose arithmetic between a shot and the game camera. The game builds its own view and projection
/// matrices: the shot hands it an eye and a look-at point inside its camera update, and a FoV through the coordinator.
/// Game-free.
/// </summary>
public static class CutsceneView
{
    /// <summary>The game ignores an eye closer to its look-at point than 0.05 m (it keeps the previous frame's pair);
    /// a pose closer than this is not handed over.</summary>
    public const float MinEyeToTarget = 0.1f;

    /// <summary>The vertical FOV of a perspective projection matrix (radians), from its M22 = 1/tan(fov/2).</summary>
    public static float VerticalFovOf(in Matrix4x4 projection) =>
        MathF.Abs(projection.M22) > 1e-6f ? 2f * MathF.Atan(1f / MathF.Abs(projection.M22)) : float.NaN;

    /// <summary>Narrowest and widest vertical FOV ever asked for (radians): 1° and 170°.</summary>
    public const float MinFov = MathF.PI / 180f, MaxFov = 170f * MathF.PI / 180f;

    /// <summary>A vertical FOV (radians) kept within [<see cref="MinFov"/>, <see cref="MaxFov"/>].</summary>
    public static float ClampFov(float verticalFov) => Math.Clamp(verticalFov, MinFov, MaxFov);

    /// <summary>tan(lens/2) ÷ tan(field/2) for the game camera: how its FoV field maps to the vertical FOV it renders
    /// (measured from the projection it rendered with). Null when either is unusable.</summary>
    public static float? FovRatio(float renderedVerticalFov, float gameFovField)
    {
        if (!float.IsFinite(renderedVerticalFov) || renderedVerticalFov <= 0f || !float.IsFinite(gameFovField) || gameFovField <= 0.01f)
            return null;
        var r = MathF.Tan(renderedVerticalFov / 2f) / MathF.Tan(gameFovField / 2f);
        return float.IsFinite(r) && r > 0f ? r : null;
    }

    /// <summary>The value for the game camera's FoV field that renders <paramref name="verticalFov"/> (clamped to
    /// [1°, 170°]), given the measured <see cref="FovRatio"/>; null when it would not be a usable field value.</summary>
    public static float? GameFovFor(float verticalFov, float ratio)
    {
        if (!float.IsFinite(verticalFov) || !float.IsFinite(ratio) || ratio <= 0f) return null;
        var fov = 2f * MathF.Atan(MathF.Tan(ClampFov(verticalFov) / 2f) / ratio);
        return float.IsFinite(fov) && fov > 0.01f ? fov : null;
    }

    /// <summary>Whether an eye / look-at pair can be handed to the game: finite and at least
    /// <see cref="MinEyeToTarget"/> apart.</summary>
    public static bool CanHandOver(Vector3 eye, Vector3 target) =>
        float.IsFinite(eye.X) && float.IsFinite(eye.Y) && float.IsFinite(eye.Z) &&
        float.IsFinite(target.X) && float.IsFinite(target.Y) && float.IsFinite(target.Z) &&
        Vector3.DistanceSquared(eye, target) >= MinEyeToTarget * MinEyeToTarget;
}
