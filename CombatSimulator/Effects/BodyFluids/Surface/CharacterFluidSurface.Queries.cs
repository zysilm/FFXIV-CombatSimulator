// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private bool Skin(int index)
    {
        if (vertexFrames[index] == frame) return true;
        if (SkinVerticesThisFrame >= MaxSkinVertices) { BudgetExhausted = true; return false; }
        var v = vertices[index];
        currentVertices[index] = Transform(v, skinWorld);
        previousVertices[index] = Transform(v, previousSkinWorld);
        if (!Finite(currentVertices[index]) || !Finite(previousVertices[index])) return false;
        vertexFrames[index] = frame; SkinVerticesThisFrame++;
        return true;
    }
    private static Vector3 Transform(Vertex v, Matrix4x4[] matrices) =>
        Vector3.Transform(v.Position, matrices[v.B0]) * v.Weights.X +
        Vector3.Transform(v.Position, matrices[v.B1]) * v.Weights.Y +
        Vector3.Transform(v.Position, matrices[v.B2]) * v.Weights.Z +
        Vector3.Transform(v.Position, matrices[v.B3]) * v.Weights.W +
        Vector3.Transform(v.Position, matrices[v.B4]) * v.ExtraWeights.X +
        Vector3.Transform(v.Position, matrices[v.B5]) * v.ExtraWeights.Y +
        Vector3.Transform(v.Position, matrices[v.B6]) * v.ExtraWeights.Z +
        Vector3.Transform(v.Position, matrices[v.B7]) * v.ExtraWeights.W;
    private bool Triangle(int fi, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        a = b = c = default;
        var face = faces[fi];
        if (!Skin(face.A) || !Skin(face.B) || !Skin(face.C)) return false;
        a = currentVertices[face.A]; b = currentVertices[face.B]; c = currentVertices[face.C];
        return Vector3.Cross(b - a, c - a).LengthSquared() > 1e-14f;
    }

    private bool ContactPending(string reason)
    {
        BudgetExhausted = true;
        ContactPendingReason = reason;
        return false;
    }

    private bool CacheTriangleSweepBounds(int fi)
    {
        ref var bounds = ref triangleSweepBounds[fi];
        if (bounds.Frame == frame) return true;
        var face = faces[fi];
        if (!Skin(face.A) || !Skin(face.B) || !Skin(face.C))
            return ContactPending(BudgetExhausted ? "Skin vertex budget exhausted" : "Triangle pose unavailable/nonfinite");
        var a = currentVertices[face.A]; var b = currentVertices[face.B]; var c = currentVertices[face.C];
        var pa = previousVertices[face.A]; var pb = previousVertices[face.B]; var pc = previousVertices[face.C];
        bounds.Minimum = Vector3.Min(Vector3.Min(Vector3.Min(a, b), c), Vector3.Min(Vector3.Min(pa, pb), pc));
        bounds.Maximum = Vector3.Max(Vector3.Max(Vector3.Max(a, b), c), Vector3.Max(Vector3.Max(pa, pb), pc));
        // All three temporal slice triangles lie in the six-endpoint box. PlaneContact additionally
        // shifts each slice's from-point by centroidDelta/3 and permits a 1mm interior start.
        // The barycentric test also permits two negative coordinates of up to 0.0001 each.
        // Cover these terms before rejecting; this is broad phase for that same three-slice approximation.
        var relativeMotion = Vector3.Abs(((a - pa) + (b - pb) + (c - pc)) / 9f);
        var padding = relativeMotion + new Vector3(0.001f) + (bounds.Maximum - bounds.Minimum) * 0.0002f;
        bounds.Minimum -= padding; bounds.Maximum += padding;
        if (!Finite(bounds.Minimum) || !Finite(bounds.Maximum)) return ContactPending("Triangle sweep bounds nonfinite");
        bounds.Frame = frame; TriangleBoundsBuiltThisFrame++;
        return true;
    }

    private static bool BoundsOverlap(Vector3 segmentMinimum, Vector3 segmentMaximum, Vector3 minimum, Vector3 maximum) =>
        segmentMaximum.X >= minimum.X && segmentMinimum.X <= maximum.X &&
        segmentMaximum.Y >= minimum.Y && segmentMinimum.Y <= maximum.Y &&
        segmentMaximum.Z >= minimum.Z && segmentMinimum.Z <= maximum.Z;

    /// <summary>
    /// Local material-space walk over native shared edges and verified duplicate seam edges.
    /// Returns false at a real/unverified boundary; anchor/sample are left at the edge.
    /// Eight edge transitions maximum. Budget exhaustion stops travel rather than teleporting to another part.
    /// </summary>
    public bool TryWalk(ref FluidSurfaceAnchor anchor, Vector3 worldDisplacement, out FluidSurfaceSample sample)
    {
        sample = default;
        WalkStatus = "Walking";
        if (!Finite(worldDisplacement)) { WalkStatus = "Invalid displacement"; return false; }
        if (!TryEvaluate(anchor, out sample)) { WalkStatus = "Anchor pose unavailable"; return ContactPending(WalkStatus); }
        var remaining = worldDisplacement;
        for (int step = 0; step < 8; step++)
        {
            if (!Triangle(anchor.Triangle, out var a, out var b, out var c))
            { WalkStatus = "Walk triangle pose unavailable"; return ContactPending(WalkStatus); }
            var normal = SafeNormal(Vector3.Cross(b - a, c - a));
            remaining -= normal * Vector3.Dot(normal, remaining);
            var point = a * anchor.Barycentric.X + b * anchor.Barycentric.Y + c * anchor.Barycentric.Z;
            if (!Barycentric(point + remaining, a, b, c, out var target))
            { WalkStatus = "Walk material projection unavailable"; return ContactPending(WalkStatus); }
            if (target.X >= -1e-6f && target.Y >= -1e-6f && target.Z >= -1e-6f)
            {
                anchor.Barycentric = NormalizeBarycentric(target);
                bool evaluated = TryEvaluate(anchor, out sample);
                WalkStatus = evaluated ? "Complete" : "Walk destination pose unavailable";
                return evaluated || ContactPending(WalkStatus);
            }
            float fraction = 1;
            int edge = -1;
            for (int e = 0; e < 3; e++)
            {
                float oldValue = Component(anchor.Barycentric, e), newValue = Component(target, e);
                if (newValue >= 0 || oldValue - newValue <= 0) continue;
                float crossing = Math.Clamp(oldValue / (oldValue - newValue), 0, 1);
                if (crossing <= fraction) { fraction = crossing; edge = e; }
            }
            if (edge < 0) { WalkStatus = "No resolvable crossed edge"; return ContactPending(WalkStatus); }
            var edgePoint = point + remaining * fraction;
            anchor.Barycentric = NormalizeBarycentric(Vector3.Lerp(anchor.Barycentric, target, fraction));
            if (!TryEvaluate(anchor, out sample)) { WalkStatus = "Edge pose unavailable"; return ContactPending(WalkStatus); }
            if (!TryResolvedNeighbour(anchor.Triangle, edge, out int next))
            { WalkStatus = BudgetExhausted ? ContactPendingReason : "Open/unverified seam boundary"; return false; }
            if (!Triangle(next, out var na, out var nb, out var nc))
            { WalkStatus = "Neighbour pose unavailable"; return ContactPending(WalkStatus); }
            var nextNormal = SafeNormal(Vector3.Cross(nb - na, nc - na));
            if (Vector3.Dot(normal, nextNormal) < -0.25f) { WalkStatus = "Sharp folded edge"; return false; }
            if (!Barycentric(edgePoint, na, nb, nc, out var nextBary) || nextBary.X < -0.002f || nextBary.Y < -0.002f || nextBary.Z < -0.002f)
            { WalkStatus = "Neighbour material point unavailable"; return ContactPending(WalkStatus); }
            EdgeVertices(faces[anchor.Triangle], edge, out int first, out int second);
            var axis = currentVertices[second] - currentVertices[first];
            if (axis.LengthSquared() <= 1e-14f) { WalkStatus = "Degenerate crossed edge"; return ContactPending(WalkStatus); }
            axis = Vector3.Normalize(axis);
            float turn = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(normal, nextNormal)), Vector3.Dot(normal, nextNormal));
            anchor.Triangle = next; anchor.Barycentric = NormalizeBarycentric(nextBary);
            remaining *= 1 - fraction;
            // Parallel transport around the true shared edge preserves tangential travel length.
            // Repeated projection alone damps movement at every curved triangle transition.
            remaining = Vector3.Transform(remaining, Quaternion.CreateFromAxisAngle(axis, turn));
            remaining -= nextNormal * Vector3.Dot(nextNormal, remaining);
        }
        WalkStatus = "Eight-edge transition budget exhausted";
        TryEvaluate(anchor, out sample);
        return ContactPending(WalkStatus);
    }

    /// <summary>
    /// Earliest local contact against the visible cached model triangles. Three temporal slices approximate
    /// moving triangles; each uses relative centroid motion. This is not exact rotating-triangle CCD.
    /// Radius is a plane offset; edge/corner sphere contacts are conservatively omitted in this first version.
    /// </summary>
    public bool TryContact(Vector3 start, Vector3 end, float radius,
        out FluidSurfaceAnchor anchor, out FluidSurfaceSample sample, out float fraction)
    {
        anchor = default; sample = default; fraction = 1;
        if (BudgetExhausted) return ContactPending(ContactPendingReason.Length == 0 ? "Earlier pose query exhausted its budget" : ContactPendingReason);
        if (!HasSurface) return ContactPending("Surface pose/topology unavailable");
        if (!Finite(start) || !Finite(end) || !float.IsFinite(radius)) return ContactPending("Contact sweep input nonfinite");
        radius = Math.Clamp(radius, 0.0001f, 0.03f);
        var segmentMinimum = Vector3.Min(start, end) - new Vector3(radius);
        var segmentMaximum = Vector3.Max(start, end) + new Vector3(radius);
        int found = -1;
        Vector3 foundBary = default;
        foreach (var cluster in clusters)
        {
            var minimum = Vector3.Min(cluster.Minimum, cluster.PreviousMinimum);
            var maximum = Vector3.Max(cluster.Maximum, cluster.PreviousMaximum);
            if (!BoundsOverlap(segmentMinimum, segmentMaximum, minimum, maximum)) continue;
            foreach (var leaf in cluster.Children)
            {
                if (leaf.BoundsFrame != frame) UpdateBounds(leaf);
                var leafMinimum = Vector3.Min(leaf.Minimum, leaf.PreviousMinimum);
                var leafMaximum = Vector3.Max(leaf.Maximum, leaf.PreviousMaximum);
                if (!BoundsOverlap(segmentMinimum, segmentMaximum, leafMinimum, leafMaximum)) continue;
                foreach (int fi in leaf.Triangles)
                {
                if (BroadPhaseCandidatesThisFrame >= MaxBroadPhaseCandidates)
                { ContactPending("Broad-phase candidate budget exhausted"); goto Finished; }
                BroadPhaseCandidatesThisFrame++;
                if (!CacheTriangleSweepBounds(fi)) goto Finished;
                ref var bounds = ref triangleSweepBounds[fi];
                if (!BoundsOverlap(segmentMinimum, segmentMaximum, bounds.Minimum, bounds.Maximum)) continue;
                if (tests >= MaxTriangleTests) { ContactPending("Narrow-phase triangle budget exhausted"); goto Finished; }
                tests++;
                var face = faces[fi];
                var a = currentVertices[face.A]; var b = currentVertices[face.B]; var c = currentVertices[face.C];
                var pa = previousVertices[face.A]; var pb = previousVertices[face.B]; var pc = previousVertices[face.C];
                for (int slice = 0; slice < 3; slice++)
                {
                    float t0 = slice / 3f, t1 = (slice + 1) / 3f;
                    if (t0 >= fraction) break;
                    var va = Vector3.Lerp(pa, a, t1); var vb = Vector3.Lerp(pb, b, t1); var vc = Vector3.Lerp(pc, c, t1);
                    var motion = ((a - pa) + (b - pb) + (c - pc)) * ((t1 - t0) / 3f);
                    var from = Vector3.Lerp(start, end, t0) + motion;
                    var to = Vector3.Lerp(start, end, t1);
                    if (!PlaneContact(from, to, radius, va, vb, vc, out float localFraction, out var bary)) continue;
                    float candidate = t0 + (t1 - t0) * localFraction;
                    if (candidate >= fraction) continue;
                    fraction = candidate; found = fi; foundBary = bary;
                }
                }
            }
        }
        Finished:
        if (found < 0 || BudgetExhausted) return false;
        anchor = new(Generation, found, foundBary);
        if (TryEvaluate(anchor, out sample)) return true;
        return ContactPending("Contact found but current triangle sample unavailable");
    }

    private static bool PlaneContact(Vector3 from, Vector3 to, float radius, Vector3 a, Vector3 b, Vector3 c,
        out float fraction, out Vector3 barycentric)
    {
        fraction = 1; barycentric = default;
        var cross = Vector3.Cross(b - a, c - a);
        if (cross.LengthSquared() < 1e-14f) return false;
        var normal = Vector3.Normalize(cross);
        float startDistance = Vector3.Dot(from - a, normal), endDistance = Vector3.Dot(to - a, normal);
        // Back-facing/interior starts cannot suck droplets through a body to the far-side skin.
        if (startDistance < -0.001f || endDistance >= startDistance || endDistance > radius) return false;
        fraction = startDistance <= radius ? 0 : Math.Clamp((startDistance - radius) / (startDistance - endDistance), 0, 1);
        var center = Vector3.Lerp(from, to, fraction);
        var point = center - normal * Vector3.Dot(center - a, normal);
        if (!Barycentric(point, a, b, c, out barycentric) || barycentric.X < -0.0001f || barycentric.Y < -0.0001f || barycentric.Z < -0.0001f) return false;
        barycentric = NormalizeBarycentric(barycentric);
        return true;
    }
    private static bool Barycentric(Vector3 point, Vector3 a, Vector3 b, Vector3 c, out Vector3 barycentric)
    {
        var v0 = b - a; var v1 = c - a; var v2 = point - a;
        static double Dot(Vector3 x, Vector3 y) => (double)x.X * y.X + (double)x.Y * y.Y + (double)x.Z * y.Z;
        double d00 = Dot(v0, v0), d01 = Dot(v0, v1), d11 = Dot(v1, v1);
        double denominator = d00 * d11 - d01 * d01;
        barycentric = default;
        if (!(denominator > 1e-16f)) return false;
        double d20 = Dot(v2, v0), d21 = Dot(v2, v1);
        float y = (float)((d11 * d20 - d01 * d21) / denominator), z = (float)((d00 * d21 - d01 * d20) / denominator);
        barycentric = new(1 - y - z, y, z);
        return Finite(barycentric);
    }
    private static Vector3 NormalizeBarycentric(Vector3 value)
    {
        value = Vector3.Max(Vector3.Zero, value);
        float sum = value.X + value.Y + value.Z;
        return sum > 0 ? value / sum : Vector3.UnitX;
    }
    private static float Component(Vector3 v, int index) => index == 0 ? v.X : index == 1 ? v.Y : v.Z;
}
