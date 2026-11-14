// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Collections.Generic;

namespace AdvancedRagdoll.Hair;

/// <summary>Managed, visible-LOD0 weighted samples for mesh-informed hair guides.
/// No native references are retained; this is not a vertex-deformation backend. The host decodes them from its
/// models (the plugin: CombatSimulator.Animation.Hair.HairMeshDecoder).</summary>
internal sealed class HairMeshBinding
{
    public readonly Dictionary<string, HairMeshSample[]> Samples;
    public readonly int VisibleVertices;
    public HairMeshBinding(Dictionary<string, HairMeshSample[]> samples, int vertices) { Samples = samples; VisibleVertices = vertices; }
}
