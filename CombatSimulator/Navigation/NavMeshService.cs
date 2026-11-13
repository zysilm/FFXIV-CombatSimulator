// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CombatSimulator.Animation;
using CombatSimulator.Rendering.WorldGeometry;
using Dalamud.Plugin.Services;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace CombatSimulator.Navigation;

/// <summary>
/// The plugin's own navigation mesh: built with DotRecast from the game's loaded collision around the
/// player (see docs/AI_MOVEMENT_REWRITE.md). The collision is read on the framework thread, under the
/// game's lock; the mesh is built on a worker thread and swapped in when done. Queries run on the
/// framework thread only.
/// </summary>
public sealed class NavMeshService : IDisposable
{
    // The area built: this far from the player horizontally, and this far below and above.
    private const float BuildHalfExtent = 96f;
    private const float BuildBelow = 60f;
    private const float BuildAbove = 80f;
    private const int MaxTriangles = 4_000_000;

    // The debug view: polygons within this distance of the player, redrawn this often.
    private const float DebugRadius = 32f;
    private const float DebugRefreshSeconds = 0.5f;
    private const int DebugVertexCapacity = 18_000;

    private readonly IPluginLog log;
    private readonly WorldGeometryLayer debugLayer;
    private readonly WorldGeometryBuilder debugMesh = new(DebugVertexCapacity);

    private Task<DtNavMesh?>? building;
    private CancellationTokenSource? cancel;
    private uint buildingTerritory;
    private Vector3 buildingCenter;
    private int buildingTriangles;
    private Stopwatch buildClock = new();

    private DtNavMesh? mesh;
    private DtNavMeshQuery? query;
    private readonly DtQueryDefaultFilter filter = new();
    private float debugTimer;

    public NavMeshService(WorldGeometryRenderer renderer, IPluginLog log)
    {
        this.log = log;
        debugLayer = renderer.CreateLayer("Navigation mesh", DebugVertexCapacity);
    }

    /// <summary>The built mesh, or null.</summary>
    public DtNavMesh? Mesh => mesh;
    public uint Territory { get; private set; }
    public Vector3 Center { get; private set; }
    public bool IsBuilding => building != null;
    public string Status { get; private set; } = "no navigation mesh";
    public bool ShowDebug { get; set; }

    /// <summary>Start building the mesh around a point (the player). A build already running is cancelled.</summary>
    public void Build(Vector3 center, uint territory)
    {
        CancelBuild();

        var min = new Vector3(center.X - BuildHalfExtent, center.Y - BuildBelow, center.Z - BuildHalfExtent);
        var max = new Vector3(center.X + BuildHalfExtent, center.Y + BuildAbove, center.Z + BuildHalfExtent);
        var triangles = new List<Vector3>();
        var clock = Stopwatch.StartNew();
        if (!WorldCollisionMesh.TryCollectSolid(min, max, triangles, MaxTriangles) || triangles.Count < 3)
        {
            Status = "could not read the collision here";
            log.Warning($"NavMesh: collision collection failed or empty around {center} ({triangles.Count / 3} triangles).");
            return;
        }
        var collectMs = clock.ElapsedMilliseconds;

        buildingTerritory = territory;
        buildingCenter = center;
        buildingTriangles = triangles.Count / 3;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        buildClock = Stopwatch.StartNew();
        building = Task.Run(() => NavMeshBuild.Build(triangles, new NavMeshBuild.Settings(), token, threads), token);
        Status = $"building from {buildingTriangles:N0} collision triangles...";
        log.Info($"NavMesh: collected {buildingTriangles:N0} triangles in {collectMs} ms around {center}; building on {threads} threads.");
    }

    public void Clear()
    {
        CancelBuild();
        mesh = null;
        query = null;
        Territory = 0;
        Status = "no navigation mesh";
        debugLayer.Clear();
    }

    private void CancelBuild()
    {
        cancel?.Cancel();
        cancel = null;
        building = null;
    }

    /// <summary>Framework thread: take a finished build, and keep the debug view current.</summary>
    public void Tick(float deltaTime, Vector3? playerPosition)
    {
        if (building is { IsCompleted: true } done)
        {
            building = null;
            if (done.IsCompletedSuccessfully && done.Result is { } built)
            {
                mesh = built;
                query = new DtNavMeshQuery(built);
                Territory = buildingTerritory;
                Center = buildingCenter;
                var tiles = CountTiles(built, out var polys);
                Status = $"ready: {tiles} tiles, {polys:N0} polygons, built in {buildClock.ElapsedMilliseconds / 1000.0:F1} s";
                log.Info($"NavMesh: {Status} from {buildingTriangles:N0} triangles.");
                debugTimer = 0f;
            }
            else if (done.IsFaulted)
            {
                Status = "build failed";
                log.Error(done.Exception, "NavMesh: build failed.");
            }
            else if (!done.IsCanceled)
                Status = "nothing walkable was found here";
        }

        if (!ShowDebug || mesh == null || playerPosition is not { } player)
        {
            if (!ShowDebug) debugLayer.Clear();
            return;
        }

        debugTimer -= deltaTime;
        if (debugTimer > 0f)
            return;
        debugTimer = DebugRefreshSeconds;
        DrawDebug(player);
    }

    /// <summary>
    /// The walkable floor at a point: the nearest polygon within <paramref name="vertical"/> metres above
    /// or below it (never a floor further up, so never the roof over a room).
    /// </summary>
    public bool TryFloor(Vector3 point, float vertical, out Vector3 floor, out long polyRef)
    {
        floor = point;
        polyRef = 0;
        if (query == null)
            return false;
        var status = query.FindNearestPoly(NavMeshBuild.ToRc(point), new RcVec3f(1.5f, vertical, 1.5f), filter,
            out polyRef, out var nearest, out _);
        if (status.Failed() || polyRef == 0)
            return false;
        floor = NavMeshBuild.ToVector(nearest);
        return true;
    }

    private static int CountTiles(DtNavMesh nav, out int polys)
    {
        var tiles = 0;
        polys = 0;
        for (var i = 0; i < nav.GetMaxTiles(); i++)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            tiles++;
            polys += tile.data.header.polyCount;
        }
        return tiles;
    }

    // Each polygon tinted by its own colour, drawn from its detail triangles a little above the floor.
    private void DrawDebug(Vector3 player)
    {
        debugMesh.Reset();
        var nav = mesh!;
        var lift = new Vector3(0f, 0.06f, 0f);
        var radiusSq = DebugRadius * DebugRadius;
        for (var i = 0; i < nav.GetMaxTiles() && !debugMesh.Overflowed; i++)
        {
            var tile = nav.GetTile(i);
            var data = tile?.data;
            if (data?.header == null)
                continue;
            for (var p = 0; p < data.header.polyCount && !debugMesh.Overflowed; p++)
            {
                var poly = data.polys[p];
                var detail = data.detailMeshes[p];
                Vector3 Vertex(int index)
                {
                    if (index < poly.vertCount)
                    {
                        var v = poly.verts[index] * 3;
                        return new Vector3(data.verts[v], data.verts[v + 1], data.verts[v + 2]);
                    }
                    var d = (detail.vertBase + index - poly.vertCount) * 3;
                    return new Vector3(data.detailVerts[d], data.detailVerts[d + 1], data.detailVerts[d + 2]);
                }

                var first = Vertex(0);
                if (Vector2.DistanceSquared(new Vector2(first.X, first.Z), new Vector2(player.X, player.Z)) > radiusSq)
                    continue;

                var hash = (uint)(i * 7919 + p * 104729);
                var color = new Vector4(0.2f + (hash % 97) / 160f, 0.45f + (hash / 97 % 89) / 200f, 0.9f - (hash / 8633 % 83) / 200f, 0.38f);
                for (var t = 0; t < detail.triCount; t++)
                {
                    var tri = (detail.triBase + t) * 4;
                    debugMesh.AddTriangle(
                        new WorldVertex(Vertex(data.detailTris[tri]) + lift, color),
                        new WorldVertex(Vertex(data.detailTris[tri + 1]) + lift, color),
                        new WorldVertex(Vertex(data.detailTris[tri + 2]) + lift, color));
                }
            }
        }

        debugLayer.SetEnabled(true);
        debugLayer.SubmitFrame(debugMesh.Vertices, shaded: false);
    }

    public void Dispose()
    {
        CancelBuild();
        debugLayer.Dispose();
    }
}
