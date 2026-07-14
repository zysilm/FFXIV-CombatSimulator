using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Camera;

/// <summary>
/// Framing math for the Dynamic Camera. Pure functions over the game's orbit model
/// (pivot + DirH + DirV + Distance + FoV) — no game pointers, no state.
///
/// The death shot uses a "prone photographer" parameterisation: the camera is PINNED
/// near the ground at a human-scale height and a small pitch, close to the fallen
/// body, and the only solved variable is how far it backs away from the corpse until
/// the killer fits in frame too. Composition (body low in frame, killer towering)
/// then emerges from the depth difference between the two subjects instead of being
/// imposed as screen-line constraints. The previous approach — solving the full 3D
/// camera position from three screen-plane equations — was algebraically sound and
/// cinematically arbitrary: nothing tied the camera to the ground plane or to a
/// sensible height, which is how it ended up underground.
/// </summary>
public static class DynamicCameraSolver
{
    /// <summary>
    /// Camera basis for the game's orbit angles.
    ///
    /// Sign convention (verified against the game's own asymmetric pitch limits,
    /// DirVMin ≈ −1.48 / DirVMax ≈ +0.79, and the in-game behaviour that you can look
    /// down on your character almost vertically but up only ~45°):
    /// NEGATIVE DirV puts the camera ABOVE the pivot looking down;
    /// positive DirV sinks it below, looking up.
    /// </summary>
    public static void Basis(float dirH, float dirV, out Vector3 fwd, out Vector3 right, out Vector3 up)
    {
        var cv = MathF.Cos(dirV);
        var sv = MathF.Sin(dirV);
        var sh = MathF.Sin(dirH);
        var ch = MathF.Cos(dirH);

        // Vector from pivot to camera, per the game's orbit convention.
        var toCamera = new Vector3(sh * cv, -sv, ch * cv);
        fwd = -toCamera;

        var r = Vector3.Cross(Vector3.UnitY, fwd);
        if (r.LengthSquared() < 1e-6f)
        {
            // Looking straight up/down: the horizontal cross product degenerates.
            r = new Vector3(-ch, 0f, sh);
        }
        right = Vector3.Normalize(r);
        up = Vector3.Cross(fwd, right);
    }

    public static float TanHalfV(float fov) => MathF.Tan(Math.Clamp(fov, 0.05f, 2.8f) * 0.5f);

    public static float TanHalfH(float fov, float aspect) => TanHalfV(fov) * MathF.Max(0.1f, aspect);

    /// <summary>
    /// Normalised device coords of a world point: x,y ∈ [-1,1] inside the frame.
    /// z is view depth (negative = behind the camera).
    /// </summary>
    public static Vector3 Project(Vector3 x, Vector3 cam, in Vector3 fwd, in Vector3 right, in Vector3 up,
        float fov, float aspect)
    {
        var v = x - cam;
        var depth = Vector3.Dot(fwd, v);
        if (MathF.Abs(depth) < 1e-4f)
            depth = depth < 0f ? -1e-4f : 1e-4f;

        var ndcX = Vector3.Dot(right, v) / (depth * TanHalfH(fov, aspect));
        var ndcY = Vector3.Dot(up, v) / (depth * TanHalfV(fov));
        return new Vector3(ndcX, ndcY, depth);
    }

    public readonly record struct GroundedFitResult(
        bool Ok,
        Vector3 Camera,
        float Standoff,
        float MaxAbsX,
        float MaxAbsY);

    /// <summary>
    /// The prone-photographer fit.
    ///
    /// The camera sits at absolute height <paramref name="cameraHeight"/> (the caller
    /// anchors this to the terrain) on the horizontal ray that backs away from
    /// <paramref name="corpseAnchor"/> in the direction the orbit angles dictate; view
    /// orientation is fixed by (dirH, dirV). The single unknown is the horizontal
    /// standoff t from the corpse. Starting at <paramref name="minStandoff"/> (the
    /// close-up the shot WANTS), t grows only as far as needed for every required
    /// point to sit inside the safe frame.
    ///
    /// The fit region in t need not be a half-line — at strong pitches distant points
    /// drift toward the horizon line, which may itself sit outside the safe frame —
    /// so this scans geometrically for the first fitting t and then bisects the
    /// boundary below it, rather than assuming monotonicity.
    /// </summary>
    public static GroundedFitResult GroundedFit(
        IReadOnlyList<Vector3> required,
        Vector3 corpseAnchor,
        float dirH, float dirV, float fov, float aspect,
        float safeX, float safeY,
        float cameraHeight,
        float minStandoff, float maxStandoff)
    {
        if (required.Count == 0 || maxStandoff <= minStandoff)
            return default;

        Basis(dirH, dirV, out var fwd, out var right, out var up);

        // Horizontal direction from the corpse toward the camera. This is exactly the
        // horizontal part of the game's pivot→camera vector for dirH, so the pivot we
        // hand back reconstructs this same camera.
        var back = new Vector3(MathF.Sin(dirH), 0f, MathF.Cos(dirH));

        Vector3 CamAt(float t) => new(
            corpseAnchor.X + back.X * t,
            cameraHeight,
            corpseAnchor.Z + back.Z * t);

        (bool fits, float maxAbsX, float maxAbsY) Measure(float t)
        {
            var cam = CamAt(t);
            var maxX = 0f;
            var maxY = 0f;
            var ok = true;
            foreach (var p in required)
            {
                var ndc = Project(p, cam, fwd, right, up, fov, aspect);
                if (ndc.Z <= 0.2f)
                {
                    ok = false;
                    continue;
                }
                maxX = MathF.Max(maxX, MathF.Abs(ndc.X));
                maxY = MathF.Max(maxY, MathF.Abs(ndc.Y));
            }
            ok = ok && maxX <= safeX && maxY <= safeY;
            return (ok, maxX, maxY);
        }

        // Geometric scan out from the close-up distance for the first t that fits.
        var prev = minStandoff;
        var found = -1f;
        var prevFit = Measure(minStandoff);
        if (prevFit.fits)
        {
            found = minStandoff;
        }
        else
        {
            var t = minStandoff;
            for (var i = 0; i < 14 && t < maxStandoff; i++)
            {
                prev = t;
                t = MathF.Min(t * 1.35f + 0.15f, maxStandoff);
                if (Measure(t).fits)
                {
                    found = t;
                    break;
                }
                if (t >= maxStandoff)
                    break;
            }
        }

        if (found < 0f)
            return default;

        // Bisect the non-fit/fit boundary so the shot stays as close as it may.
        if (found > minStandoff)
        {
            var lo = prev;    // known not to fit
            var hi = found;   // known to fit
            for (var i = 0; i < 10; i++)
            {
                var mid = (lo + hi) * 0.5f;
                if (Measure(mid).fits)
                    hi = mid;
                else
                    lo = mid;
            }
            found = hi;
        }

        var final = Measure(found);
        return new GroundedFitResult(true, CamAt(found), found, final.maxAbsX, final.maxAbsY);
    }

    /// <summary>
    /// Distance at which a subject of height h fills <paramref name="share"/> of the
    /// screen height. Inverse of the vertical projection — used by combat framing to
    /// hold the character at a constant on-screen size.
    /// </summary>
    public static float DistanceForScreenShare(float height, float share, float fov)
    {
        var s = Math.Clamp(share, 0.05f, 0.95f);
        var half = MathF.Tan(Math.Clamp(fov, 0.05f, 2.8f) * 0.5f) * s;
        if (half < 1e-4f)
            return 10f;
        return MathF.Max(0.5f, height * 0.5f / half);
    }
}
