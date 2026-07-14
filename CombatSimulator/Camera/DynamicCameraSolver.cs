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
/// The controller closes the loop between χ and whatever the game's DirV turns out to mean.
///
/// The death shot uses a "prone photographer" parameterisation: the camera is PINNED near
/// the ground at a human-scale height and a small angle, close to the fallen body, and the
/// only solved variable is how far it backs away from the corpse until the killer fits in
/// frame too. Composition (body low and large, killer towering) emerges from the depth
/// difference between the subjects instead of being imposed as screen-line constraints —
/// the previous 3-plane closed-form solve was algebraically sound and cinematically
/// arbitrary, which is how it ended up underground.
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
    /// <paramref name="corpseAnchor"/> along the view yaw; orientation is fixed by
    /// (yaw, χ). The single unknown is the horizontal standoff t from the corpse.
    /// Starting at <paramref name="minStandoff"/> (the close-up the shot WANTS), t grows
    /// only as far as needed for every required point to sit inside the safe frame.
    ///
    /// The fit region in t need not be a half-line — at strong angles distant points
    /// drift toward the horizon line, which may itself sit outside the safe frame — so
    /// this scans geometrically for the first fitting t and then bisects the boundary
    /// below it, rather than assuming monotonicity.
    /// </summary>
    public static GroundedFitResult GroundedFit(
        IReadOnlyList<Vector3> required,
        Vector3 corpseAnchor,
        float yaw, float chi, in Lens lens,
        float safeX, float safeY,
        float cameraHeight,
        float minStandoff, float maxStandoff)
    {
        if (required.Count == 0 || maxStandoff <= minStandoff || !lens.IsValid)
            return default;

        var localLens = lens; // local copy: `in` params cannot be captured by local functions
        BasisFromAngles(yaw, chi, out var fwd, out var right, out var up);

        // Horizontal direction from the corpse toward the camera — the horizontal part of
        // the pivot→camera direction for this yaw.
        var back = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

        // Lateral slide along the view's right axis. Backing away is the fit's main variable,
        // but on its own it wastes frame: the camera retreats along the ray through the
        // corpse anchor, so a body sprawled to one side binds against one frame edge while
        // the other stays empty (testing showed the body pinned at x≈0.94 with everything
        // left of centre vacant). Sliding sideways to centre the horizontal span lets the
        // same subjects fit at a smaller standoff — strictly bigger on screen. The view
        // DIRECTION never changes, so the player's yaw is untouched.
        var lateral = 0f;

        Vector3 CamAt(float t) => new(
            corpseAnchor.X + back.X * t + right.X * lateral,
            cameraHeight,
            corpseAnchor.Z + back.Z * t + right.Z * lateral);

        (bool fits, float maxAbsX, float maxAbsY, float minX, float maxX, float depthMinX, float depthMaxX) Measure(float t)
        {
            var cam = CamAt(t);
            var maxAbsX = 0f;
            var maxAbsY = 0f;
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            var depthMinX = 1f;
            var depthMaxX = 1f;
            var ok = true;
            foreach (var p in required)
            {
                var ndc = Project(p, cam, fwd, right, up, localLens);
                if (ndc.Z <= 0.2f)
                {
                    ok = false;
                    continue;
                }
                maxAbsX = MathF.Max(maxAbsX, MathF.Abs(ndc.X));
                maxAbsY = MathF.Max(maxAbsY, MathF.Abs(ndc.Y));
                if (ndc.X < minX) { minX = ndc.X; depthMinX = ndc.Z; }
                if (ndc.X > maxX) { maxX = ndc.X; depthMaxX = ndc.Z; }
            }
            ok = ok && maxAbsX <= safeX && maxAbsY <= safeY;
            return (ok, maxAbsX, maxAbsY, minX, maxX, depthMinX, depthMaxX);
        }

        float FindStandoff()
        {
            var prev = minStandoff;
            if (Measure(minStandoff).fits)
                return minStandoff;

            var found = -1f;
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
            if (found < 0f)
                return -1f;

            // Bisect the non-fit/fit boundary so the shot stays as close as it may.
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
            return hi;
        }

        var standoff = FindStandoff();
        if (standoff < 0f)
            return default;

        // Centre the horizontal span, then let the standoff shrink into the freed room.
        // Two passes settle it; the slide solve accounts for the two extremes' depths
        // (a shift changes each anchor's x by −δ/(depth·tanHalfH), signs included).
        for (var pass = 0; pass < 2; pass++)
        {
            var m = Measure(standoff);
            if (!m.fits || m.maxX < m.minX)
                break;

            var centre = (m.minX + m.maxX) * 0.5f;
            if (MathF.Abs(centre) < 0.03f)
                break;

            var k = 0.5f * (1f / (m.depthMinX * localLens.TanHalfH) + 1f / (m.depthMaxX * localLens.TanHalfH));
            if (MathF.Abs(k) < 1e-5f)
                break;

            lateral += centre / k;
            var refit = FindStandoff();
            if (refit < 0f)
            {
                // Centering somehow broke the fit (degenerate geometry) — undo and stop.
                lateral -= centre / k;
                break;
            }
            standoff = refit;
        }

        var final = Measure(standoff);
        return new GroundedFitResult(true, CamAt(standoff), standoff, final.maxAbsX, final.maxAbsY);
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
