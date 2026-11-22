// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using CombatSimulator.Camera;

namespace DeathShots;

/// <summary>
/// The fit of the Directed camera's old death framing (the "prone photographer": DynamicCameraSolver.GroundedFit
/// until the automatic death shots replaced that framing in the plugin, 2026-11-22). Moved here unchanged: the
/// "Directed" baseline of the metric run (<see cref="Runners.Directed"/>) reproduces the old framing with it, and
/// nothing in the plugin uses it any more.
/// </summary>
public static class OldDirectedFraming
{
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
        float yaw, float chi, in DynamicCameraSolver.Lens lens,
        float safeX, float safeY,
        float cameraHeight,
        float minStandoff, float maxStandoff)
    {
        if (required.Count == 0 || maxStandoff <= minStandoff || !lens.IsValid)
            return default;

        var localLens = lens; // local copy: `in` params cannot be captured by local functions
        DynamicCameraSolver.BasisFromAngles(yaw, chi, out var fwd, out var right, out var up);

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
                var ndc = DynamicCameraSolver.Project(p, cam, fwd, right, up, localLens);
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
}
