// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using CombatSimulator.Rendering.WorldGeometry;

namespace CombatSimulator.Effects.BodyFluids;

public sealed partial class BodyFluidController
{
    private readonly WorldGeometryLayer?[] materialLayers = new WorldGeometryLayer?[9];
    private readonly FluidVertex[] groupedVertices = new FluidVertex[WorldGeometryRenderer.MaxVertices];
    private readonly int[] groupCounts = new int[9], groupOffsets = new int[9];

    private void InitializeMaterialLayers() => materialLayers[0] = renderer;

    private void SubmitMaterialFrames()
    {
        fluidGeometry.PartitionGroups(groupedVertices, groupCounts, groupOffsets);
        for (int group = 0; group < materialLayers.Length; group++)
        {
            if (groupCounts[group] == 0) { materialLayers[group]?.Clear(); continue; }
            var layer = materialLayers[group];
            if (layer == null)
            {
                layer = worldRenderer.CreateLayer($"Fluid site {group}");
                layer.SetEnabled(enabled);
                materialLayers[group] = layer;
            }
            var site = simulation.GetOutletSettings((BodyFluidOutletKind)group);
            var material = config.CreateBodyFluidMaterial(site) with { DiagnosticView = productionView };
            if (simulation.LargeVisibilityPreview) material = material with { ReflectionStrength = 1 };
            layer.SubmitFluidFrame(groupedVertices.AsSpan(groupOffsets[group], groupCounts[group]), material);
        }
    }

    private void ClearMaterialLayers()
    {
        foreach (var layer in materialLayers) layer?.Clear();
        Array.Clear(groupCounts);
    }

    private string DescribeMaterialLayers()
    {
        for (int i = 0; i < materialLayers.Length; i++)
            if (groupCounts[i] > 0 && materialLayers[i] != null) return materialLayers[i]!.Status;
        return renderer.Status;
    }

    private void SetMaterialLayersEnabled(bool value)
    {
        foreach (var layer in materialLayers) layer?.SetEnabled(value);
    }

    private void DisposeMaterialLayers()
    {
        // The primary diagnostic/production layer retains its existing controller lifetime.
        for (int group = 1; group < materialLayers.Length; group++)
        {
            materialLayers[group]?.Dispose();
            materialLayers[group] = null;
        }
    }
}
