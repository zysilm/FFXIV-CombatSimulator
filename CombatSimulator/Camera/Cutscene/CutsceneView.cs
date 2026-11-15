// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace CombatSimulator.Camera.Cutscene;

/// <summary>Layout of a view matrix: row vectors (v·M, translation in row 4) or column vectors (M·v, translation in
/// column 4), right- or left-handed.</summary>
public enum ViewConvention { RowRightHanded, RowLeftHanded, ColumnRightHanded, ColumnLeftHanded }

/// <summary>
/// Builds a view matrix in the game's own layout. The layout is not assumed: it is detected each frame by building the
/// game camera's own pose (its eye and look-at point, no roll) in each candidate layout and keeping the one that matches
/// the game's freshly computed matrix (<see cref="Detect"/>). Game-free.
/// </summary>
public static class CutsceneView
{
    public static Matrix4x4 Build(ViewConvention c, Vector3 eye, Vector3 target, Vector3 up)
    {
        var m = c is ViewConvention.RowRightHanded or ViewConvention.ColumnRightHanded
            ? Matrix4x4.CreateLookAt(eye, target, up)
            : Matrix4x4.CreateLookAtLeftHanded(eye, target, up);
        return c is ViewConvention.ColumnRightHanded or ViewConvention.ColumnLeftHanded ? Matrix4x4.Transpose(m) : m;
    }

    /// <summary>The layout that reproduces <paramref name="native"/> from its eye and look-at point, and its error
    /// (sum of absolute element differences; the right layout gives ~0, the wrong ones several units).</summary>
    public static (ViewConvention Convention, float Error) Detect(in Matrix4x4 native, Vector3 eye, Vector3 target)
    {
        var best = ViewConvention.RowRightHanded;
        var bestErr = float.MaxValue;
        foreach (var c in Enum.GetValues<ViewConvention>())
        {
            var m = Build(c, eye, target, Vector3.UnitY);
            var err = Diff(m, native);
            if (err < bestErr) { bestErr = err; best = c; }
        }
        return (best, bestErr);
    }

    public static float Diff(in Matrix4x4 a, in Matrix4x4 b) =>
        MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13) + MathF.Abs(a.M14 - b.M14) +
        MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22) + MathF.Abs(a.M23 - b.M23) + MathF.Abs(a.M24 - b.M24) +
        MathF.Abs(a.M31 - b.M31) + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33) + MathF.Abs(a.M34 - b.M34) +
        MathF.Abs(a.M41 - b.M41) + MathF.Abs(a.M42 - b.M42) + MathF.Abs(a.M43 - b.M43) + MathF.Abs(a.M44 - b.M44);

    /// <summary>The vertical FOV of a perspective projection matrix (radians), from its M22 = 1/tan(fov/2).</summary>
    public static float VerticalFovOf(in Matrix4x4 projection) =>
        MathF.Abs(projection.M22) > 1e-6f ? 2f * MathF.Atan(1f / MathF.Abs(projection.M22)) : float.NaN;

    /// <summary>Narrowest and widest vertical FOV ever written (radians): 1° and 170°.</summary>
    public const float MinFov = MathF.PI / 180f, MaxFov = 170f * MathF.PI / 180f;

    /// <summary>A vertical FOV (radians) kept within [<see cref="MinFov"/>, <see cref="MaxFov"/>].</summary>
    public static float ClampFov(float verticalFov) => Math.Clamp(verticalFov, MinFov, MaxFov);

    /// <summary>Whether all 16 elements are finite.</summary>
    public static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    /// <summary>The projection with its vertical FOV replaced (aspect ratio and the depth terms kept); the FOV is
    /// clamped to [1°, 170°].</summary>
    public static Matrix4x4 WithVerticalFov(in Matrix4x4 projection, float verticalFov)
    {
        var m = projection;
        if (MathF.Abs(m.M22) < 1e-6f || !float.IsFinite(verticalFov) || verticalFov <= 0f) return m;
        verticalFov = ClampFov(verticalFov);
        var newM22 = 1f / MathF.Tan(verticalFov / 2f);
        var scale = newM22 / MathF.Abs(m.M22);
        m.M11 *= scale;
        m.M22 *= scale;
        return m;
    }
}
