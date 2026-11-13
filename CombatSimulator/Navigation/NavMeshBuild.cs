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

    /// <summary>The mesh split into connected pieces: which piece each polygon is in, and each piece's polygons.</summary>
    public sealed class Pieces
    {
        public readonly Dictionary<long, int> PieceOf = new();
        public readonly List<List<long>> Members = new();
    }

    public static Pieces FindPieces(DtNavMesh nav)
    {
        var result = new Pieces();
        var open = new Queue<long>();
        for (var i = 0; i < nav.GetMaxTiles(); i++)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            var baseRef = nav.GetPolyRefBase(tile);
            for (var p = 0; p < tile.data.header.polyCount; p++)
            {
                var start = baseRef | (long)p;
                if (result.PieceOf.ContainsKey(start))
                    continue;
                var id = result.Members.Count;
                var members = new List<long>();
                result.Members.Add(members);
                result.PieceOf[start] = id;
                open.Enqueue(start);
                while (open.Count > 0)
                {
                    var current = open.Dequeue();
                    members.Add(current);
                    nav.GetTileAndPolyByRefUnsafe(current, out var ct, out var cp);
                    for (var link = cp.firstLink; link != DtDetour.DT_NULL_LINK; link = ct.links[link].next)
                    {
                        var next = ct.links[link].refs;
                        if (next != 0 && result.PieceOf.TryAdd(next, id))
                            open.Enqueue(next);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Make a piece walkable (queries and paths use it) or not.</summary>
    public static void SetPieceEnabled(DtNavMesh nav, List<long> members, bool enabled)
    {
        foreach (var polyRef in members)
            nav.SetPolyFlags(polyRef, enabled ? WalkableFlag : 0);
    }

    /// <summary>A query filter that accepts every polygon, switched on or not.</summary>
    public sealed class AnyPolygonFilter : IDtQueryFilter
    {
        private readonly DtQueryDefaultFilter costs = new();

        public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly) => true;

        public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly, long curRef,
            DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
            => costs.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);
    }

    /// <summary>How the mesh is linked: within tiles, across tile borders, and polygons with no link at all.</summary>
    public static string DescribeLinks(DtNavMesh nav)
    {
        int inner = 0, across = 0, polys = 0, lonely = 0;
        for (var i = 0; i < nav.GetMaxTiles(); i++)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            var baseRef = nav.GetPolyRefBase(tile);
            for (var p = 0; p < tile.data.header.polyCount; p++)
            {
                polys++;
                var poly = tile.data.polys[p];
                var any = false;
                for (var link = poly.firstLink; link != DtDetour.DT_NULL_LINK; link = tile.links[link].next)
                {
                    any = true;
                    var other = tile.links[link].refs;
                    nav.GetTileAndPolyByRefUnsafe(other, out var otherTile, out _);
                    if (ReferenceEquals(otherTile, tile)) inner++; else across++;
                }
                if (!any) lonely++;
            }
        }
        return $"{polys} polygons, {inner} links within tiles, {across} across tile borders, {lonely} polygons with no link";
    }

    /// <summary>Write triangles (three world positions each) and the build centre to a binary file.</summary>
    public static void SaveTriangles(string path, IReadOnlyList<Vector3> vertices, Vector3 center)
    {
        try
        {
            using var writer = new System.IO.BinaryWriter(System.IO.File.Create(path));
            writer.Write(center.X); writer.Write(center.Y); writer.Write(center.Z);
            writer.Write(vertices.Count);
            foreach (var v in vertices) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        }
        catch
        {
            // Study material only.
        }
    }

    public static List<Vector3> LoadTriangles(string path, out Vector3 center)
    {
        using var reader = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
        center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var count = reader.ReadInt32();
        var list = new List<Vector3>(count);
        for (var i = 0; i < count; i++) list.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        return list;
    }

    public static RcVec3f ToRc(Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector3 ToVector(RcVec3f v) => new(v.X, v.Y, v.Z);
}
