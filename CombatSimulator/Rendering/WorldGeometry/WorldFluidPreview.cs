// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>Finite static optical validation using the production world render path.</summary>
public sealed class WorldFluidPreview : IDisposable
{
    private readonly WorldGeometryLayer layer;
    private readonly FluidGeometryBuilder geometry = new();
    private long deadline;
    public string Status => layer.Status;
    public WorldFluidPreview(WorldGeometryRenderer renderer) => layer = renderer.CreateLayer("Fluid material preview", 12288);
    public void Show(Vector3 origin, FluidDiagnosticView diagnosticView = FluidDiagnosticView.Composite, FluidMaterial? material = null)
    {
        geometry.Reset();
        geometry.AddEllipsoid(origin, new Vector3(0.035f, 0.045f, 0.035f));
        layer.SubmitFluidFrame(geometry.Vertices, (material ?? FluidMaterial.Default) with { DiagnosticView = diagnosticView });
        layer.SetEnabled(true); deadline = Environment.TickCount64 + 30000;
    }
    public void Tick(float dt)
    {
        if (deadline != 0 && Environment.TickCount64 >= deadline) Clear();
    }
    public void Clear() { deadline = 0; layer.Clear(); layer.SetEnabled(false); }
    public void Dispose() => layer.Dispose();
}
