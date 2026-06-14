using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Simulation;

public readonly struct TerrainHeightBounds
{
    public TerrainHeightBounds(float minX, float maxX, float minZ, float maxZ, float maxY)
    {
        MinX = minX;
        MaxX = maxX;
        MinZ = minZ;
        MaxZ = maxZ;
        MaxY = maxY;
    }

    public float MinX { get; }
    public float MaxX { get; }
    public float MinZ { get; }
    public float MaxZ { get; }
    public float MaxY { get; }

    public TerrainHeightBounds Expand(float pad) => new(MinX - pad, MaxX + pad, MinZ - pad, MaxZ + pad, MaxY);
}

public sealed class TerrainHeightCache
{
    public float OriginX { get; init; }
    public float OriginZ { get; init; }
    public float Step { get; init; }
    public int Width { get; init; }
    public int Depth { get; init; }
    public float[,] Heights { get; init; } = new float[0, 0];
    public bool[,] Valid { get; init; } = new bool[0, 0];

    public float MaxX => OriginX + (Width - 1) * Step;
    public float MaxZ => OriginZ + (Depth - 1) * Step;

    public bool Covers(TerrainHeightBounds bounds)
        => Width > 0 &&
           Depth > 0 &&
           bounds.MinX >= OriginX &&
           bounds.MaxX <= MaxX &&
           bounds.MinZ >= OriginZ &&
           bounds.MaxZ <= MaxZ;

    public bool TrySample(float x, float z, out float y)
    {
        y = 0;
        if (Width <= 0 || Depth <= 0 || Step <= 0)
            return false;

        var gx = (x - OriginX) / Step;
        var gz = (z - OriginZ) / Step;
        var ix = (int)MathF.Floor(gx);
        var iz = (int)MathF.Floor(gz);

        if (ix < 0 || iz < 0 || ix >= Width || iz >= Depth)
            return false;

        if (ix < Width - 1 && iz < Depth - 1)
        {
            var tx = gx - ix;
            var tz = gz - iz;
            if (Valid[ix, iz] && Valid[ix + 1, iz] && Valid[ix, iz + 1] && Valid[ix + 1, iz + 1])
            {
                var y00 = Heights[ix, iz];
                var y10 = Heights[ix + 1, iz];
                var y01 = Heights[ix, iz + 1];
                var y11 = Heights[ix + 1, iz + 1];
                y = Lerp(Lerp(y00, y10, tx), Lerp(y01, y11, tx), tz);
                return true;
            }
        }

        var bestDist = float.MaxValue;
        var bestY = 0f;
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var sx = ix + dx;
            var sz = iz + dz;
            if (sx < 0 || sz < 0 || sx >= Width || sz >= Depth || !Valid[sx, sz])
                continue;

            var wx = OriginX + sx * Step;
            var wz = OriginZ + sz * Step;
            var dist = (wx - x) * (wx - x) + (wz - z) * (wz - z);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestY = Heights[sx, sz];
            }
        }

        if (bestDist < float.MaxValue)
        {
            y = bestY;
            return true;
        }

        return false;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

public sealed class TerrainHeightService
{
    private const float GridStep = 0.5f;
    private const int GridMaxSize = 33;
    private const float CachePadding = 6f;
    private const float RebuildInterval = 1.5f;
    private const float RaycastStartYOffset = 10f;
    private const float RaycastDistance = 80f;

    private TerrainHeightCache? cache;
    private float builtAt = float.NegativeInfinity;

    public TerrainHeightCache? EnsureCoverage(TerrainHeightBounds bounds, float now)
    {
        if (cache != null && cache.Covers(bounds) && now - builtAt < RebuildInterval)
            return cache;

        cache = Build(bounds.Expand(CachePadding));
        builtAt = now;
        return cache;
    }

    public void Clear()
    {
        cache = null;
        builtAt = float.NegativeInfinity;
    }

    private static TerrainHeightCache Build(TerrainHeightBounds bounds)
    {
        var minX = bounds.MinX;
        var maxX = bounds.MaxX;
        var minZ = bounds.MinZ;
        var maxZ = bounds.MaxZ;

        var widthWorld = MathF.Max(maxX - minX, GridStep);
        var depthWorld = MathF.Max(maxZ - minZ, GridStep);
        var step = MathF.Max(GridStep,
            MathF.Max(widthWorld, depthWorld) / MathF.Max(1, GridMaxSize - 1));
        var width = Math.Clamp((int)MathF.Ceiling(widthWorld / step) + 1, 2, GridMaxSize);
        var depth = Math.Clamp((int)MathF.Ceiling(depthWorld / step) + 1, 2, GridMaxSize);

        var heights = new float[width, depth];
        var valid = new bool[width, depth];
        var originY = bounds.MaxY + RaycastStartYOffset;

        for (var z = 0; z < depth; z++)
        for (var x = 0; x < width; x++)
        {
            var wx = minX + x * step;
            var wz = minZ + z * step;
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(wx, originY, wz),
                    new Vector3(0, -1, 0),
                    out var hit,
                    RaycastDistance))
            {
                heights[x, z] = hit.Point.Y;
                valid[x, z] = true;
            }
        }

        return new TerrainHeightCache
        {
            OriginX = minX,
            OriginZ = minZ,
            Step = step,
            Width = width,
            Depth = depth,
            Heights = heights,
            Valid = valid,
        };
    }
}
