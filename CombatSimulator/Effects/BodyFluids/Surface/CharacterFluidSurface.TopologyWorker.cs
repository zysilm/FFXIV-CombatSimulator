// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    // Native identity snapshots belong to Framework alone. The task receives only private managed buffers.
    private sealed class TopologyMetadataRequest
    {
        public uint Generation;
        public nint Actor, Draw, Skeleton;
        public ulong Object;
        public uint Territory;
        public bool FaceOnly;
        public bool RaceDeformation;
        public ulong DeformationEpoch;
        public nint[] Resources = Array.Empty<nint>(), ResourceData = Array.Empty<nint>();
        public uint[] Attributes = Array.Empty<uint>(), Shapes = Array.Empty<uint>();
        public int Loaded, Omitted;
        public CancellationTokenSource Cancellation = null!;
        public Task<TopologyMetadataResult> Work = null!;
    }

    private sealed record TopologyMetadataResult(Vertex[] Vertices, Face[] Faces, Cluster[] Clusters,
        Vector3[] Current, Vector3[] Previous, uint[] VertexFrames, TriangleSweepBounds[] TriangleBounds,
        double BuildMilliseconds, int SeamPairs);

    private TopologyMetadataRequest? topologyMetadataRequest;
    public bool TopologyMetadataPending => topologyMetadataRequest != null;
    public double LastTopologyBuildMilliseconds { get; private set; }
    public double LastTopologySnapshotMilliseconds { get; private set; }
    public double LastTopologyInstallMilliseconds { get; private set; }
    public string TopologyBuildStatus { get; private set; } = "No topology request";

    private void CancelTopologyMetadata()
    {
        var request = topologyMetadataRequest;
        topologyMetadataRequest = null;
        // Workers poll the token between bounded batches. Cancel never joins or touches live buffers.
        if (request == null) return;
        request.Cancellation.Cancel();
        var cancellation = request.Cancellation;
        // Observe obsolete faulted jobs and dispose after completion, without joining on Framework.
        _ = request.Work.ContinueWith(completed =>
        {
            _ = completed.Exception;
            cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        TopologyBuildStatus = "Superseded topology request discarded";
    }

    private void QueueTopologyMetadata(Vertex[] points, Face[] triangles, int loaded, int omitted)
    {
        CancelTopologyMetadata();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var request = new TopologyMetadataRequest
        {
            Generation = Generation, Actor = actor, Object = objectIdentity, Draw = drawIdentity,
            Territory = territoryIdentity,
            Skeleton = skeletonIdentity, FaceOnly = FaceOnlyCapture,
            RaceDeformation = ApplyRaceDeformation, DeformationEpoch = deformationEpoch,
            Resources = (nint[])resources.Clone(), ResourceData = (nint[])resourceData.Clone(),
            Attributes = (uint[])attributeMasks.Clone(), Shapes = (uint[])shapeMasks.Clone(),
            Loaded = loaded, Omitted = omitted, Cancellation = cancellation,
            // This closure captures only points, triangles and token. It never captures the surface instance.
            Work = Task.Run(() => BuildTopologyMetadata(points, triangles, token)),
        };
        topologyMetadataRequest = request;
        poseAvailable = previousPoseAvailable = false;
        vertices = Array.Empty<Vertex>(); faces = Array.Empty<Face>(); clusters = Array.Empty<Cluster>();
        currentVertices = previousVertices = Array.Empty<Vector3>(); vertexFrames = Array.Empty<uint>();
        triangleSweepBounds = Array.Empty<TriangleSweepBounds>();
        diagnosticFaceTriangles = lipCandidateTriangles = Array.Empty<int>();
        Status = TopologyBuildStatus = "Managed adjacency/clusters pending; emission paused";
    }

    private static TopologyMetadataResult BuildTopologyMetadata(Vertex[] points, Face[] triangles, CancellationToken cancellation)
    {
        var timer = Stopwatch.StartNew();
        cancellation.ThrowIfCancellationRequested();
        BuildAdjacency(triangles, cancellation);
        int seamPairs = BuildVerifiedSeamAdjacency(points, triangles, cancellation);
        var builtClusters = BuildClusters(points, triangles, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return new(points, triangles, builtClusters, new Vector3[points.Length], new Vector3[points.Length],
            new uint[points.Length], new TriangleSweepBounds[triangles.Length], timer.Elapsed.TotalMilliseconds, seamPairs);
    }

    // Called only after UpdateActor has validated the current native identities and partial bindings.
    private void PollTopologyMetadata()
    {
        var request = topologyMetadataRequest;
        if (request == null || !request.Work.IsCompleted) return;
        bool matches = request.Generation == Generation && request.Actor == actor && request.Object == objectIdentity &&
            request.Territory == territoryIdentity &&
            request.Draw == drawIdentity && request.Skeleton == skeletonIdentity && request.FaceOnly == FaceOnlyCapture &&
            request.RaceDeformation == ApplyRaceDeformation && request.DeformationEpoch == deformationEpoch;
        for (int i = 0; matches && i < MaxSlots; i++)
            matches = request.Resources[i] == resources[i] && request.ResourceData[i] == resourceData[i] &&
                request.Attributes[i] == attributeMasks[i] && request.Shapes[i] == shapeMasks[i];
        if (!matches) { CancelTopologyMetadata(); return; }
        if (!ValidateDeformationInstall()) { CancelTopologyMetadata(); return; }
        topologyMetadataRequest = null;
        var timer = Stopwatch.StartNew();
        try
        {
            // Completed task only: no waiting, native worker access, or shared-array mutation.
            var result = request.Work.GetAwaiter().GetResult();
            vertices = result.Vertices; faces = result.Faces; clusters = result.Clusters;
            currentVertices = result.Current; previousVertices = result.Previous; vertexFrames = result.VertexFrames;
            triangleSweepBounds = result.TriangleBounds;
            LastTopologyBuildMilliseconds = result.BuildMilliseconds;
            VerifiedSeamPairs = result.SeamPairs;
            poseAvailable = previousPoseAvailable = false;
            FinishTopologyMetadata(request.Loaded, request.Omitted);
            TopologyBuildStatus = "Managed adjacency/clusters installed atomically";
        }
        catch (Exception ex)
        {
            poseAvailable = previousPoseAvailable = false;
            vertices = Array.Empty<Vertex>(); faces = Array.Empty<Face>(); clusters = Array.Empty<Cluster>();
            currentVertices = previousVertices = Array.Empty<Vector3>(); vertexFrames = Array.Empty<uint>();
            triangleSweepBounds = Array.Empty<TriangleSweepBounds>();
            Status = TopologyBuildStatus = $"Topology metadata failed: {ex.GetType().Name}; emission paused";
        }
        finally
        {
            request.Cancellation.Dispose();
            LastTopologyInstallMilliseconds = timer.Elapsed.TotalMilliseconds;
        }
    }
}
