// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace AdvancedRagdoll;

// The visible models of a character as drawn, as the mesh source hands them over (IMeshSource.CaptureRenderedGeometry).
// The host decodes them from its model format (the plugin: CombatSimulator.Animation.RenderedGeometryDecoder).
internal readonly record struct RenderedGeometryInfluence(int BoneIndex, float Weight, Vector3 WorldContribution = default);
internal readonly record struct RenderedGeometryVertex(Vector3 WorldPosition, RenderedGeometryInfluence[] Influences, int SourceSlot);
internal sealed class RenderedGeometrySnapshot
{
    public RenderedGeometryVertex[] Vertices = Array.Empty<RenderedGeometryVertex>();
    public int[] TriangleIndices = Array.Empty<int>();
    public string[] Diagnostics = Array.Empty<string>();
}
