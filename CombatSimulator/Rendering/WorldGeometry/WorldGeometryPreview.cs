// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>A finite API example using its own layer; independent of every effect's lifetime.</summary>
internal sealed class WorldGeometryPreview : IDisposable
{
    private readonly WorldGeometryLayer layer;
    private readonly WorldGeometryBuilder geometry = new(144);
    private float remaining;
    public string Status => layer.Status;

    public WorldGeometryPreview(WorldGeometryRenderer renderer) => layer = renderer.CreateLayer("API preview", 144);

    public void Show(Vector3 origin)
    {
        geometry.Reset();
        geometry.AddEllipsoid(origin + Vector3.UnitX * 0.2f, 0.025f, new Vector4(1, 0.15f, 0.15f, 0.9f));
        geometry.AddEllipsoid(origin, 0.025f, new Vector4(0.15f, 0.3f, 1, 0.9f));
        geometry.AddEllipsoid(origin - Vector3.UnitX * 0.2f, 0.025f, new Vector4(0.15f, 1, 0.2f, 0.9f));
        layer.SetEnabled(true);
        layer.SubmitFrame(geometry.Vertices, shaded: false);
        remaining = 8;
    }

    public void Tick(float dt)
    {
        if (remaining <= 0) return;
        remaining = MathF.Max(0, remaining - Math.Clamp(dt, 0, 0.1f));
        if (remaining == 0) Clear();
    }

    public void Clear()
    {
        remaining = 0;
        layer.Clear();
        layer.SetEnabled(false);
    }

    public void Dispose() => layer.Dispose();
}
