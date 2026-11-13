// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace CombatSimulator.Navigation;

/// <summary>
/// Builds a tiled navigation mesh (DotRecast: Recast to voxelise, Detour to query) from world-space
/// collision triangles. Pure: no game state, so it runs on a worker thread and in offline tests.
/// </summary>
public static class NavMeshBuild
{
    /// <summary>Walkable polygons carry this flag; queries include it.</summary>
    public const int WalkableFlag = 1;

    /// <summary>
    /// Voxel and agent sizes, in metres. The agent is a humanoid: 2 m tall, 0.4 m radius, climbing
    /// 0.6 m steps and 55° slopes (the game's own walkable limit is close to that).
    /// </summary>
    public sealed record Settings
    {
        public float CellSize { get; init; } = 0.3f;
        public float CellHeight { get; init; } = 0.2f;
        public float AgentHeight { get; init; } = 2.0f;
        public float AgentRadius { get; init; } = 0.4f;
        public float AgentClimb { get; init; } = 0.6f;
        public float MaxSlopeDegrees { get; init; } = 55f;
        /// <summary>Tile edge in cells: 64 × 0.3 m ≈ 19 m.</summary>
        public int TileCells { get; init; } = 64;
    }

    /// <summary>
    /// Build the mesh over the triangles' bounds. <paramref name="vertices"/> holds three world
    /// positions per triangle. Null when nothing walkable came out of it.
    /// </summary>
    public static DtNavMesh? Build(IReadOnlyList<Vector3> vertices, Settings settings, CancellationToken cancellation = default,
        int threads = 1)
    {
        if (vertices.Count < 3)
            return null;

        var count = vertices.Count - vertices.Count % 3;
        var verts = new float[count * 3];
        var faces = new int[count];
        for (var i = 0; i < count; i++)
        {
            verts[i * 3] = vertices[i].X;
            verts[i * 3 + 1] = vertices[i].Y;
            verts[i * 3 + 2] = vertices[i].Z;
            faces[i] = i;
        }
        var geom = new RcSampleInputGeomProvider(verts, faces);

        var s = settings;
        var config = new RcConfig(
            useTiles: true, tileSizeX: s.TileCells, tileSizeZ: s.TileCells,
            borderSize: RcConfig.CalcBorder(s.AgentRadius, s.CellSize),
            partition: RcPartition.WATERSHED,
            cellSize: s.CellSize, cellHeight: s.CellHeight,
            agentMaxSlope: s.MaxSlopeDegrees, agentHeight: s.AgentHeight, agentRadius: s.AgentRadius, agentMaxClimb: s.AgentClimb,
            minRegionArea: 8, mergeRegionArea: 20,
            edgeMaxLen: 12f, edgeMaxError: 1.3f, vertsPerPoly: 6,
            detailSampleDist: 6f, detailSampleMaxError: 1f,
            filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            walkableAreaMod: new RcAreaModification(RcRecast.RC_WALKABLE_AREA), buildMeshDetail: true);

        var results = new RcBuilder().BuildTiles(geom, config, false, true, Math.Max(1, threads),
            Task.Factory, cancellation);

        var bmin = geom.GetMeshBoundsMin();
        var tileWidth = s.TileCells * s.CellSize;
        var tilesX = (int)MathF.Ceiling((geom.GetMeshBoundsMax().X - bmin.X) / tileWidth) + 1;
        var tilesZ = (int)MathF.Ceiling((geom.GetMeshBoundsMax().Z - bmin.Z) / tileWidth) + 1;
        var navParams = new DtNavMeshParams
        {
            orig = bmin,
            tileWidth = tileWidth,
            tileHeight = tileWidth,
            maxTiles = Math.Max(1, tilesX * tilesZ),
            maxPolys = 1 << 14,
        };
        var mesh = new DtNavMesh();
        mesh.Init(ref navParams, 6);

        var added = 0;
        foreach (var result in results)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = ToMeshData(result, s);
            if (data == null)
                continue;
            mesh.AddTile(data, 0, 0, out _);
            added++;
        }
        return added > 0 ? mesh : null;
    }

    /// <summary>
    /// Keep only what can be walked to from the seed polygon (where the player stands): every polygon
    /// not connected to it (railing tops, roofs, ledges, islands) loses its walkable flag, so no query or
    /// path ever lands there. Returns how many polygons were cut off.
    /// </summary>
    public static int MarkUnreachable(DtNavMesh nav, long seed)
    {
        var reached = new HashSet<long>();
        if (seed != 0 && nav.IsValidPolyRef(seed))
        {
            var open = new Queue<long>();
            reached.Add(seed);
            open.Enqueue(seed);
            while (open.Count > 0)
            {
                var current = open.Dequeue();
                nav.GetTileAndPolyByRefUnsafe(current, out var tile, out var poly);
                for (var link = poly.firstLink; link != DtDetour.DT_NULL_LINK; link = tile.links[link].next)
                {
                    var next = tile.links[link].refs;
                    if (next != 0 && reached.Add(next))
                        open.Enqueue(next);
                }
            }
        }

        var cut = 0;
        for (var i = 0; i < nav.GetMaxTiles(); i++)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            var baseRef = nav.GetPolyRefBase(tile);
            for (var p = 0; p < tile.data.header.polyCount; p++)
            {
                var polyRef = baseRef | (long)p;
                if (reached.Contains(polyRef))
                    continue;
                nav.SetPolyFlags(polyRef, 0);
                cut++;
            }
        }
        return cut;
    }

    private static DtMeshData? ToMeshData(RcBuilderResult result, Settings s)
    {
        var poly = result.Mesh;
        var detail = result.MeshDetail;
        if (poly == null || detail == null || poly.npolys == 0)
            return null;

        for (var i = 0; i < poly.npolys; i++)
            poly.flags[i] = poly.areas[i] == RcRecast.RC_WALKABLE_AREA ? WalkableFlag : 0;

        var option = new DtNavMeshCreateParams
        {
            verts = poly.verts,
            vertCount = poly.nverts,
            polys = poly.polys,
            polyAreas = poly.areas,
            polyFlags = poly.flags,
            polyCount = poly.npolys,
            nvp = poly.nvp,
            detailMeshes = detail.meshes,
            detailVerts = detail.verts,
            detailVertsCount = detail.nverts,
            detailTris = detail.tris,
            detailTriCount = detail.ntris,
            walkableHeight = s.AgentHeight,
            walkableRadius = s.AgentRadius,
            walkableClimb = s.AgentClimb,
            bmin = poly.bmin,
            bmax = poly.bmax,
            cs = s.CellSize,
            ch = s.CellHeight,
            buildBvTree = true,
            tileX = result.TileX,
            tileZ = result.TileZ,
        };
        return DtNavMeshBuilder.CreateNavMeshData(option);
    }

    public static RcVec3f ToRc(Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector3 ToVector(RcVec3f v) => new(v.X, v.Y, v.Z);
}
