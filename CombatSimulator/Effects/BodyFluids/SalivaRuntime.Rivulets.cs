// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;

namespace CombatSimulator.Effects.BodyFluids;

internal sealed partial class SalivaRuntime
{
    private readonly RivuletPath[] rivulets = new RivuletPath[BeadCapacity];
    public int RivuletsDrawn { get; private set; }

    // The mobile owner's existing volume is reconstructed as an attached strip.
    // These are material samples, not extra particles or an extra volume ledger.
    private sealed class RivuletPath
    {
        internal const int Rows = 16, Columns = 7;
        internal readonly FluidSurfaceAnchor[] Centers = new FluidSurfaceAnchor[Rows];
        internal readonly FluidSurfaceAnchor[] Left = new FluidSurfaceAnchor[Rows], Right = new FluidSurfaceAnchor[Rows];
        internal readonly FluidSurfaceAnchor[] Samples = new FluidSurfaceAnchor[Rows * Columns];
        internal readonly FluidSurfaceAnchor[] PendingSamples = new FluidSurfaceAnchor[Columns];
        internal readonly Vector3[] Base = new Vector3[Rows * Columns], Free = new Vector3[Rows * Columns], Normals = new Vector3[Rows * Columns];
        internal readonly Vector3[] Support = new Vector3[Rows * Columns];
        internal readonly Vector2[] UV = new Vector2[Rows * Columns];
        internal readonly float[] Profile = new float[Rows * Columns], CoatHeight = new float[Rows * Columns], Length = new float[Rows];
        internal int Count;
        internal int RetainedSlot = -1;
        internal void Reset() { Count = 0; RetainedSlot = -1; }
    }

    private void RecordRivulet(int index, FluidSurfaceAnchor anchor, FluidSurfaceSample sample)
    {
        var path = rivulets[index] ??= new RivuletPath();
        if (path.Count > 0 && path.Centers[0].Generation != anchor.Generation) path.Reset();
        if (path.Count > 0)
        {
            if (!surface.TryEvaluate(path.Centers[path.Count - 1], out var last)) return;
            float distance = Vector3.Distance(last.Position, sample.Position);
            if (distance < 1e-7f) return;
            if (distance > .025f) path.Reset();
            else if (path.Count == 1 && distance < .0003f) return;
        }
        const int lateralSamples = RivuletPath.Columns - 1;
        if (surfaceBudget < 2 || rivuletSampleBudget < lateralSamples || surface.BudgetExhausted) return;
        var downhill = Gravity - sample.Normal * Vector3.Dot(Gravity, sample.Normal);
        if (downhill.LengthSquared() < 1e-8f) return;
        var across = Vector3.Normalize(Vector3.Cross(sample.Normal, downhill));
        BeadDimensions(beads[index].Volume, out _, out _, out var footprint);
        float halfWidth = Math.Clamp((float)footprint * .65f, .00035f, .0025f);
        const int middle = RivuletPath.Columns / 2;
        surfaceBudget -= 2;
        rivuletSampleBudget -= lateralSamples;
        // Cache each material column once. Interpolating world-space endpoint
        // positions cuts through convex modded skin, even with a correct mesh.
        for (int col = 0; col < RivuletPath.Columns; col++)
        {
            var sampleAnchor = anchor;
            float x = (float)(col - middle) / middle;
            if (col != middle && (!surface.TryWalk(ref sampleAnchor, across * (halfWidth * x), out _) || surface.BudgetExhausted)) return;
            path.PendingSamples[col] = sampleAnchor;
        }
        var left = path.PendingSamples[0]; var right = path.PendingSamples[RivuletPath.Columns - 1];
        int at = path.Count;
        if (path.Count > 1 && surface.TryEvaluate(path.Centers[path.Count - 2], out var previous) &&
            Vector3.Distance(previous.Position, sample.Position) < .0013f) at--;
        if (at == RivuletPath.Rows)
        {
            // The full segment remains in its independent retained owner.
            // Start the next segment at the old tip instead of erasing its tail.
            path.Centers[0] = path.Centers[at - 1];
            path.Left[0] = path.Left[at - 1]; path.Right[0] = path.Right[at - 1];
            Array.Copy(path.Samples, (at - 1) * RivuletPath.Columns, path.Samples, 0, RivuletPath.Columns);
            path.RetainedSlot = -1;
            at = 1;
        }
        path.Centers[at] = anchor; path.Left[at] = left; path.Right[at] = right;
        Array.Copy(path.PendingSamples, 0, path.Samples, at * RivuletPath.Columns, RivuletPath.Columns);
        path.Count = at + 1;
        PreserveRivulet(index);
    }

    private bool AppendRivulet(int index, FluidGeometryBuilder builder)
    {
        var path = rivulets[index];
        if (path == null || path.Count < 2 || beads[index].Volume <= 0) return false;
        return AppendRivuletSurface(path, beads[index].Volume, builder);
    }

    private bool AppendRivuletSurface(RivuletPath path, double volume, FluidGeometryBuilder builder, int coatOutlet = -1)
    {
        if (path.Count < 2 || volume <= 0) return false;
        const int columns = RivuletPath.Columns;
        int count = path.Count;
        int required = (count - 1) * (columns - 1) * 6;
        if (builder.Count + required > WorldGeometryRenderer.MaxVertices) return false;
        Vector3 previous = default;
        for (int row = 0; row < count; row++)
        {
            if (!surface.TryEvaluate(path.Centers[row], out var center) ||
                !surface.TryEvaluate(path.Left[row], out var left) || !surface.TryEvaluate(path.Right[row], out var right)) return false;
            if (Vector3.Dot(center.Normal, left.Normal) < .5f || Vector3.Dot(center.Normal, right.Normal) < .5f) return false;
            path.Length[row] = row == 0 ? 0 : path.Length[row - 1] + Vector3.Distance(previous, center.Position);
            if (row > 0 && Vector3.Distance(previous, center.Position) > .004f) return false;
            previous = center.Position;
            for (int col = 0; col < columns; col++)
            {
                int vertex = row * columns + col;
                if (!surface.TryEvaluate(path.Samples[vertex], out var support)) return false;
                path.Base[vertex] = support.Position;
                path.Support[vertex] = support.Normal;
                surface.TryGetMaterialCoordinate(path.Samples[vertex], out path.UV[vertex]);
            }
        }
        float length = path.Length[count - 1];
        if (length < .0003f) return false;
        for (int row = 0; row < count; row++)
        {
            float t = path.Length[row] / length;
            // Narrow trailing attachment, elongated body and a rounded advancing
            // front. A continuous free surface replaces separate moving beads.
            float longitudinal = .05f + MathF.Pow(MathF.Max(0, MathF.Sin(t * MathF.PI)), .7f) * (.35f + .65f * t);
            for (int col = 0; col < columns; col++)
            {
                float x = (float)col / (columns - 1) * 2 - 1;
                path.Profile[row * columns + col] = longitudinal * MathF.Sqrt(MathF.Max(0, 1 - x * x));
            }
        }
        double integral = 0;
        for (int row = 0; row < count - 1; row++)
            for (int col = 0; col < columns - 1; col++)
            {
                int a = row * columns + col, b = a + 1, c = a + columns, d = c + 1;
                integral += Prism(a, b, c) + Prism(b, d, c);
            }
        if (integral <= 1e-12) return false;
        float height = (float)(volume / integral);
        if (!float.IsFinite(height) || height <= 0 || height > .01f) return false;
        int vertices = count * columns;
        for (int i = 0; i < vertices; i++)
        {
            // The retained trace sits on the owner's existing wet coat. Adding
            // that local thickness avoids letting a broad film bury the strip.
            path.CoatHeight[i] = coatOutlet < 0 ? 0 : WetCoatThickness(path.Samples[i], coatOutlet);
            path.Free[i] = path.Base[i] + path.Support[i] * (path.Profile[i] * height + path.CoatHeight[i] + .00008f);
        }
        Array.Clear(path.Normals, 0, vertices);
        for (int row = 0; row < count - 1; row++)
            for (int col = 0; col < columns - 1; col++)
            {
                int a = row * columns + col, b = a + 1, c = a + columns, d = c + 1;
                AccumulateNormal(a, b, c); AccumulateNormal(b, d, c);
            }
        for (int i = 0; i < vertices; i++)
        {
            if (path.Normals[i].LengthSquared() <= 1e-16f) return false;
            path.Normals[i] = Vector3.Normalize(path.Normals[i]);
        }
        for (int row = 0; row < count - 1; row++)
            for (int col = 0; col < columns - 1; col++)
            {
                int a = row * columns + col, b = a + 1, c = a + columns, d = c + 1;
                builder.AddTriangle(Vertex(a), Vertex(b), Vertex(c));
                builder.AddTriangle(Vertex(b), Vertex(d), Vertex(c));
            }
        return true;

        double Prism(int a, int b, int c) => Vector3.Cross(path.Base[b] - path.Base[a], path.Base[c] - path.Base[a]).Length() *
            (path.Profile[a] + path.Profile[b] + path.Profile[c]) / 6;
        void AccumulateNormal(int a, int b, int c)
        {
            var n = Vector3.Cross(path.Free[b] - path.Free[a], path.Free[c] - path.Free[a]);
            // Left/right samples can reverse the strip's intrinsic winding; orient
            // from its verified substrate, then let the common builder match it.
            var support = path.Support[a] + path.Support[b] + path.Support[c];
            if (Vector3.Dot(n, support) < 0) n = -n;
            path.Normals[a] += n; path.Normals[b] += n; path.Normals[c] += n;
        }
        FluidVertex Vertex(int i) => new(path.Free[i], path.Normals[i], path.UV[i], path.Profile[i] * height + path.CoatHeight[i],
            Math.Clamp(path.Profile[i] * height / .00002f, 0, 1));
    }

    private float WetCoatThickness(FluidSurfaceAnchor anchor, int outlet)
    {
        float thickness = 0;
        int first = outlet * FilmsPerOutlet, end = Math.Min(first + FilmsPerOutlet, films.Length);
        for (int film = first; film < end; film++)
            if (films[film].TryGetCell(anchor, out int cell))
                thickness = Math.Max(thickness, (float)films[film].GetCell(cell).Thickness);
        return thickness;
    }
}
