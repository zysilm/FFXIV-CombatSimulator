// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private FluidSurfaceAnchor lipAnchor;
    private bool lipBound;
    private int diagnosticSeed = -1;
    private int[] diagnosticFaceTriangles = Array.Empty<int>();
    private readonly List<string> topologyDiagnostics = new();
    public string LipStatus { get; private set; } = "No verified lip landmark/profile; emission disabled";

    public void ClearLipAnchor()
    {
        lipBound = false; lipAnchor = default;
        LipAnchorIsValidated = false; lipSelectionAttempted = false;
        LipStatus = "No current anatomical lip anchor; emission disabled";
    }

    public bool TryGetMouthAnchor(out FluidSurfaceAnchor anchor)
    {
        anchor = lipAnchor;
        return lipBound && poseAvailable && anchor.Generation == Generation && anchor.Triangle >= 0 && anchor.Triangle < faces.Length;
    }

    /// <summary>Developer proof binding to the actual resolved mesh entry. Never a jaw/head offset.</summary>
    public bool TryBindLipAnchor(int slot, int mesh, uint meshIndexEntry, Vector3 barycentric, out string reason)
    {
        reason = "Resolved visible triangle unavailable";
        if (!poseAvailable) { reason = "Final pose unavailable"; return false; }
        for (int i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            if (face.Slot != slot || face.Mesh != mesh || face.IndexEntry != meshIndexEntry) continue;
            var anchor = new FluidSurfaceAnchor(Generation, i, barycentric);
            if (!TryEvaluate(anchor, out _)) { reason = "Invalid barycentric coordinates or skin budget"; return false; }
            lipAnchor = anchor; lipBound = true;
            LipAnchorIsValidated = false; lipSelectionAttempted = true;
            LipStatus = reason = $"Developer surface proof: generation={Generation} slot={slot} mesh={mesh} indexEntry={meshIndexEntry} vertices={face.V0},{face.V1},{face.V2}";
            return true;
        }
        return false;
    }

    public bool TryGetDiagnosticTriangle(FluidSurfaceAnchor anchor, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 normal)
        => TryGetDiagnosticTriangle(anchor.Generation, anchor.Triangle, out a, out b, out c, out normal);

    public bool TryGetDiagnosticTriangle(uint generation, int triangle, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 normal)
    {
        a = b = c = normal = default;
        if (!poseAvailable || generation != Generation || triangle < 0 || triangle >= faces.Length || !Triangle(triangle, out a, out b, out c)) return false;
        normal = SafeNormal(Vector3.Cross(b - a, c - a));
        return true;
    }

    /// <summary>Resolved indices in this surface generation's unified vertex array, including shape replacements.
    /// The same index denotes the same material vertex; split UV vertices retain distinct identities.</summary>
    public bool TryGetTriangleVertexIds(uint generation, int triangle, out int a, out int b, out int c)
    {
        a = b = c = -1;
        if (generation != Generation || triangle < 0 || triangle >= faces.Length) return false;
        var face = faces[triangle];
        a = face.A; b = face.B; c = face.C;
        return true;
    }

    /// <summary>Edge is opposite barycentric coordinate 0/1/2. Native shared edges and pose-verified
    /// duplicate seams are linked. Real gaps/nonmanifold edges remain boundaries.</summary>
    public bool TryGetAdjacentTriangle(FluidSurfaceAnchor anchor, int edge, out FluidSurfaceAnchor neighbor, out float edgeLength)
    {
        neighbor = default; edgeLength = 0;
        if (!poseAvailable || anchor.Generation != Generation || anchor.Triangle < 0 || anchor.Triangle >= faces.Length || edge < 0 || edge > 2) return false;
        var face = faces[anchor.Triangle];
        if (!TryResolvedNeighbour(anchor.Triangle, edge, out int next)) return false;
        int first = edge == 0 ? face.B : edge == 1 ? face.C : face.A;
        int second = edge == 0 ? face.C : edge == 1 ? face.A : face.B;
        if (!Skin(first) || !Skin(second)) return ContactPending("Adjacent edge skin budget/pose unavailable");
        edgeLength = Vector3.Distance(currentVertices[first], currentVertices[second]);
        if (!float.IsFinite(edgeLength) || edgeLength <= 1e-7f) { edgeLength = 0; return false; }
        neighbor = new(Generation, next, new Vector3(1f / 3f));
        return true;
    }

    public FluidSurfaceAnchor[] GetAdjacentTriangles(FluidSurfaceAnchor anchor, int maximum)
    {
        maximum = Math.Clamp(maximum, 0, 512);
        if (anchor.Generation != Generation || anchor.Triangle < 0 || anchor.Triangle >= faces.Length || maximum == 0) return Array.Empty<FluidSurfaceAnchor>();
        var result = new List<FluidSurfaceAnchor>(maximum);
        var seen = new HashSet<int> { anchor.Triangle };
        var queue = new Queue<int>(); queue.Enqueue(anchor.Triangle);
        while (queue.Count > 0 && result.Count < maximum)
        {
            int index = queue.Dequeue();
            result.Add(new(Generation, index, new Vector3(1f / 3f)));
            var face = faces[index];
            for (int e = 0; e < 3; e++)
            {
                if (!TryResolvedNeighbour(index, e, out int next) || seen.Count >= maximum || !seen.Add(next)) continue;
                queue.Enqueue(next);
            }
        }
        return result.ToArray();
    }

    public FluidSurfaceAnchor[] GetDiagnosticTriangles(int maximum)
    {
        maximum = Math.Clamp(maximum, 0, 512);
        if (maximum == 0) return Array.Empty<FluidSurfaceAnchor>();
        if (TryGetMouthAnchor(out var anchor)) return GetAdjacentTriangles(anchor, maximum);
        // Select a face-partial mesh once from metadata. Pose queries skin only the bounded returned set.
        int count = Math.Min(maximum, diagnosticFaceTriangles.Length);
        var result = new FluidSurfaceAnchor[count];
        for (int i = 0; i < count; i++)
            result[i] = new(Generation, diagnosticFaceTriangles[i * diagnosticFaceTriangles.Length / count], new Vector3(1f / 3f));
        return result;
    }

    /// <summary>Metadata-only samples across supported actor slots. Does not skin the entire body.</summary>
    public FluidSurfaceAnchor[] GetBodyDiagnosticTriangles(int maximum)
    {
        maximum = Math.Clamp(maximum, 0, 1024);
        if (maximum == 0 || faces.Length == 0) return Array.Empty<FluidSurfaceAnchor>();
        var slots = new List<int>?[MaxSlots];
        for (int triangle = 0; triangle < faces.Length; triangle++)
        {
            int slot = faces[triangle].Slot;
            if (slot >= 0 && slot < slots.Length) (slots[slot] ??= new List<int>()).Add(triangle);
        }
        int active = 0;
        foreach (var slot in slots) if (slot is { Count: > 0 }) active++;
        if (active == 0) return Array.Empty<FluidSurfaceAnchor>();
        var result = new List<FluidSurfaceAnchor>(maximum);
        int order = 0;
        foreach (var slot in slots)
        {
            if (slot is not { Count: > 0 }) continue;
            int count = Math.Min(slot.Count, maximum / active + (order++ < maximum % active ? 1 : 0));
            for (int index = 0; index < count; index++)
                result.Add(new(Generation, slot[index * slot.Count / count], new Vector3(1f / 3f)));
        }
        return result.ToArray();
    }

    private bool IsFacialBone(int index) => index >= 0 && index < surfaceBoneNames.Length &&
        (surfaceBoneNames[index].StartsWith("j_f_", StringComparison.Ordinal) || surfaceBoneNames[index].Contains("lip", StringComparison.OrdinalIgnoreCase));

    public string DescribeFacialBones()
    {
        var output = new StringBuilder();
        foreach (var partial in partialBindings)
        {
            output.AppendLine($"partial={partial.Index} supported={partial.Offset >= 0} bones={partial.Count} connected={partial.Connected} parent={partial.Parent}");
            for (int i = 0; i < partial.Names.Length; i++)
            {
                string name = partial.Names[i];
                if (partial.Index == 0 && !name.StartsWith("j_f_", StringComparison.Ordinal) && name != "j_kao" && name != "j_ago" && !name.Contains("lip", StringComparison.OrdinalIgnoreCase)) continue;
                int index = partial.Offset + i;
                var p = partial.Offset >= 0 && poseAvailable ? new Vector3(boneWorld[index].M41, boneWorld[index].M42, boneWorld[index].M43) : default;
                output.AppendLine($"  bone={i} name={name} world={p} finalPose={poseAvailable && partial.Offset >= 0}");
            }
        }
        return output.ToString();
    }

    public string DescribeTopology()
    {
        var output = new StringBuilder($"generation={Generation} status={Status} scope={(FaceOnlyCapture ? "face slot 11" : "full actor")} modelLoad={ModelLoadStatus} " +
            $"modelSnapshot={LastModelSnapshotMilliseconds:0.000}ms backgroundDecode={LastModelDecodeMilliseconds:0.000}ms cachedModels={modelLoads.Count} " +
            $"topologyBuild='{TopologyBuildStatus}' topologySnapshot={LastTopologySnapshotMilliseconds:0.000}ms " +
            $"topologyWorker={LastTopologyBuildMilliseconds:0.000}ms topologyInstall={LastTopologyInstallMilliseconds:0.000}ms " +
            $"poseAvailable={poseAvailable} poseFailure='{CapturePoseFailureReason}' triangles={faces.Length} vertices={vertices.Length} " +
            $"verifiedSeamPairs={VerifiedSeamPairs} walk='{WalkStatus}' lip={LipStatus}\n");
        foreach (string item in topologyDiagnostics) output.AppendLine(item);
        output.Append(DescribeFacialBones());
        return output.ToString();
    }
}
