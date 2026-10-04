// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using HkTransform = FFXIVClientStructs.Havok.Common.Base.Math.QsTransform.hkQsTransformf;

namespace CombatSimulator.Effects.BodyFluids.Surface;

/// <summary>
/// One actor's read-only CPU approximation of its currently loaded, enabled LOD0 triangles.
/// Framework calls UpdateActor; the final pose callback calls CapturePose. No bone writes or native hooks.
/// Unsupported partial skeletons/shape replacements are omitted, never replaced with reference geometry.
/// </summary>
public sealed unsafe partial class CharacterFluidSurface
{
    private const int MaxBones = 2048;
    private const int MaxSlots = 32;
    private const int MaxSkinVertices = 4096;
    private const int MaxTriangleTests = 4096;
    private const int MaxBroadPhaseCandidates = 32768;
    private readonly BoneTransformService bones;
    private readonly nint[] resources = new nint[MaxSlots];
    private readonly nint[] resourceData = new nint[MaxSlots];
    private readonly uint[] attributeMasks = new uint[MaxSlots];
    private readonly uint[] shapeMasks = new uint[MaxSlots];
    private readonly Matrix4x4[] inverseBind = new Matrix4x4[MaxBones];
    private readonly Matrix4x4[] skinWorld = new Matrix4x4[MaxBones];
    private readonly Matrix4x4[] previousSkinWorld = new Matrix4x4[MaxBones];
    private readonly Matrix4x4[] boneWorld = new Matrix4x4[MaxBones];
    private Vertex[] vertices = Array.Empty<Vertex>();
    private Face[] faces = Array.Empty<Face>();
    private Cluster[] clusters = Array.Empty<Cluster>();
    private Vector3[] currentVertices = Array.Empty<Vector3>();
    private Vector3[] previousVertices = Array.Empty<Vector3>();
    private uint[] vertexFrames = Array.Empty<uint>();
    private TriangleSweepBounds[] triangleSweepBounds = Array.Empty<TriangleSweepBounds>();
    private nint actor, drawIdentity, skeletonIdentity;
    private ulong objectIdentity;
    private uint territoryIdentity, frame;
    private int boneCount, skeletonBoneCount, modelSlotCount, tests;
    private bool poseAvailable, previousPoseAvailable;
    private float poseDt;
    public CharacterFluidSurface(BoneTransformService bones) => this.bones = bones;
    public uint Generation { get; private set; } = 1;
    public uint PoseRevision => frame;
    public bool HasSurface => poseAvailable && faces.Length != 0;
    public string Status { get; private set; } = "No actor";
    public string CapturePoseFailureReason { get; private set; } = "No captured pose";
    public int SkinVerticesThisFrame { get; private set; }
    public int TriangleTestsThisFrame => tests;
    public int BroadPhaseCandidatesThisFrame { get; private set; }
    public int TriangleBoundsBuiltThisFrame { get; private set; }
    public string ContactPendingReason { get; private set; } = string.Empty;
    /// <summary>False query results after this flag must defer simulation, not be interpreted as an open edge or a miss.</summary>
    public bool BudgetExhausted { get; private set; }
    public int TriangleCount => faces.Length;
    /// <summary>Legacy settings compatibility only. A surface landmark is required; this offset is never applied.</summary>
    public Vector3 MouthOffset { get; set; }

    public void Clear()
    {
        CancelTopologyMetadata();
        actor = drawIdentity = skeletonIdentity = 0;
        objectIdentity = 0;
        poseAvailable = previousPoseAvailable = false;
        boneCount = skeletonBoneCount = 0;
        ClearLipAnchor(); partialBindings = Array.Empty<PartialBinding>(); surfaceBones.Clear();
        deformationContext = null; deformationContextFailure = "No actor context";
        nextDeformationPoll = 0; deformationNeedsRebuild = false;
        unchecked { deformationEpoch++; }
        surfaceBoneNames = Array.Empty<string>(); topologyDiagnostics.Clear(); diagnosticSeed = -1;
        diagnosticFaceTriangles = Array.Empty<int>();
        awaitedModelLoads.Clear(); topologyAwaitingModels = false;
        vertices = Array.Empty<Vertex>(); faces = Array.Empty<Face>(); clusters = Array.Empty<Cluster>();
        currentVertices = previousVertices = Array.Empty<Vector3>(); vertexFrames = Array.Empty<uint>();
        triangleSweepBounds = Array.Empty<TriangleSweepBounds>();
        Array.Clear(resources); Array.Clear(resourceData); Array.Clear(attributeMasks); Array.Clear(shapeMasks);
        unchecked { Generation++; }
        Status = "No actor";
        CapturePoseFailureReason = "No actor";
    }

    /// <summary>Call on Framework, only while enabled. Resource loading occurs only on identity/mask change.</summary>
    public void UpdateActor(nint address, ulong objectId, uint territory)
    {
        if (address == 0) { if (actor != 0) Clear(); return; }
        var access = bones.TryGetSkeleton(address);
        if (access == null) { poseAvailable = false; Status = "Pose unavailable"; return; }
        var ns = access.Value;
        bool deformationChanged = RefreshDeformationContext(ns, address,
            actor != address || drawIdentity != (nint)ns.CharBase || objectIdentity != objectId ||
            builtRaceDeformation != ApplyRaceDeformation);
        bool changed = actor != address || objectIdentity != objectId || territoryIdentity != territory ||
            drawIdentity != (nint)ns.CharBase || skeletonIdentity != (nint)ns.HavokSkeleton || skeletonBoneCount != ns.BoneCount || !PartialsMatch(ns) || builtFaceOnlyScope != FaceOnlyCapture ||
            builtRaceDeformation != ApplyRaceDeformation || deformationChanged || deformationNeedsRebuild;
        int slots = Math.Clamp(ns.CharBase->SlotCount, 0, MaxSlots);
        for (int i = 0; i < MaxSlots; i++)
        {
            var model = i < slots && ns.CharBase->Models != null ? ns.CharBase->Models[i] : null;
            var resource = model == null ? 0 : (nint)model->ModelResourceHandle;
            var data = resource == 0 ? 0 : (nint)model->ModelResourceHandle->ModelData;
            uint attributes = model == null ? 0 : model->EnabledAttributeIndexMask;
            uint shapes = model == null ? 0 : model->EnabledShapeKeyIndexMask;
            if (resources[i] != resource || resourceData[i] != data || attributeMasks[i] != attributes || shapeMasks[i] != shapes) changed = true;
            resources[i] = resource; resourceData[i] = data; attributeMasks[i] = attributes; shapeMasks[i] = shapes;
        }
        if (!changed)
        {
            PollTopologyMetadata();
            if (PendingModelsReady())
            {
                unchecked { Generation++; }
                poseAvailable = previousPoseAvailable = false;
                ClearLipAnchor(); BuildTopology(ns);
            }
            return;
        }
        CancelTopologyMetadata();
        deformationNeedsRebuild = false;
        actor = address; objectIdentity = objectId; territoryIdentity = territory;
        modelSlotCount = slots;
        drawIdentity = (nint)ns.CharBase; skeletonIdentity = (nint)ns.HavokSkeleton;
        poseAvailable = previousPoseAvailable = false;
        unchecked { Generation++; }
        boneCount = Math.Min(ns.BoneCount, MaxBones);
        skeletonBoneCount = ns.BoneCount;
        ClearLipAnchor();
        if (!BuildReference(ns))
        {
            vertices = Array.Empty<Vertex>(); faces = Array.Empty<Face>(); clusters = Array.Empty<Cluster>();
            currentVertices = previousVertices = Array.Empty<Vector3>(); vertexFrames = Array.Empty<uint>();
            triangleSweepBounds = Array.Empty<TriangleSweepBounds>();
            Status = "Reference skeleton unsupported; emission disabled";
            return;
        }
        BuildTopology(ns);
    }

    /// <summary>Must run after existing pose writers. Missing or redrawn actors invalidate rather than dereference cached native data.</summary>
    public bool CapturePose(float delta)
    {
        BudgetExhausted = false;
        BroadPhaseCandidatesThisFrame = TriangleBoundsBuiltThisFrame = 0;
        ContactPendingReason = string.Empty;
        poseAvailable = false;
        CapturePoseFailureReason = "Capture pending";
        if (TopologyMetadataPending || topologyAwaitingModels)
            return InvalidatePose("Managed topology/model batch pending; simulation must defer");
        // Cached addresses are never a validity proof. First-phase scope is the current LocalPlayer only.
        var localPlayer = Services.ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != actor || localPlayer.GameObjectId != objectIdentity ||
            Services.ClientState.TerritoryType != territoryIdentity) return InvalidatePose("LocalPlayer address/object/territory identity changed");
        if (actor == 0 || ((GameObject*)actor)->DrawObject == null ||
            (nint)((GameObject*)actor)->DrawObject != drawIdentity) return InvalidatePose("Actor draw object missing or changed");
        var access = bones.TryGetSkeleton(actor);
        if (access == null) return InvalidatePose("Base skeleton missing or model pose not synchronized");
        var ns = access.Value;
        if ((nint)ns.HavokSkeleton != skeletonIdentity || ns.Pose->ModelPose.Length < skeletonBoneCount || ns.BoneCount != skeletonBoneCount)
            return InvalidatePose("Base skeleton identity/bone count changed");
        if (!PartialsMatch(ns)) return InvalidatePose("Partial skeleton pose/rig/connection identity changed");
        int slots = Math.Clamp(ns.CharBase->SlotCount, 0, MaxSlots);
        if (slots != modelSlotCount) return InvalidatePose("Model slot count changed");
        for (int i = 0; i < slots; i++)
        {
            var model = ns.CharBase->Models == null ? null : ns.CharBase->Models[i];
            if ((model == null ? 0 : (nint)model->ModelResourceHandle) != resources[i] ||
                (model == null || model->ModelResourceHandle == null ? 0 : (nint)model->ModelResourceHandle->ModelData) != resourceData[i] ||
                (model == null ? 0u : model->EnabledAttributeIndexMask) != attributeMasks[i] ||
                (model == null ? 0u : model->EnabledShapeKeyIndexMask) != shapeMasks[i]) return InvalidatePose($"Model slot {i} resource/visibility/shape identity changed");
        }
        var transform = ns.CharBase->Skeleton->Transform;
        var root = Matrix4x4.CreateScale(new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z)) *
            Matrix4x4.CreateFromQuaternion(new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W)) *
            Matrix4x4.CreateTranslation(new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z));
        poseDt = Math.Clamp(delta, 0.001f, 0.1f);
        bool continuous = previousPoseAvailable && delta > 0 && delta < 0.15f;
        if (!CapturePartialTransforms(ns, root, continuous)) return InvalidatePose(CapturePoseFailureReason);
        unchecked { frame++; }
        if (frame == 0) { Array.Clear(vertexFrames); Array.Clear(triangleSweepBounds); frame = 1; }
        SkinVerticesThisFrame = tests = 0;
        foreach (var cluster in clusters) UpdateBounds(cluster);
        previousPoseAvailable = poseAvailable = true;
        CapturePoseFailureReason = string.Empty;
        TrySelectLipCandidate();
        return true;
    }

    private bool InvalidatePose(string reason)
    {
        poseAvailable = previousPoseAvailable = false;
        CapturePoseFailureReason = reason;
        return false;
    }

    public bool TryGetMouth(out FluidSurfaceSample sample)
    {
        sample = default;
        return TryGetMouthAnchor(out var anchor) && TryEvaluate(anchor, out sample);
    }

    public bool TryEvaluate(FluidSurfaceAnchor anchor, out FluidSurfaceSample sample)
    {
        sample = default;
        if (!poseAvailable || anchor.Generation != Generation || anchor.Triangle < 0 || anchor.Triangle >= faces.Length ||
            !Finite(anchor.Barycentric) || !Triangle(anchor.Triangle, out var a, out var b, out var c)) return false;
        var bary = anchor.Barycentric;
        if (bary.X < -0.002f || bary.Y < -0.002f || bary.Z < -0.002f || Math.Abs(bary.X + bary.Y + bary.Z - 1) > 0.005f) return false;
        var p = a * bary.X + b * bary.Y + c * bary.Z;
        var f = faces[anchor.Triangle];
        var old = previousVertices[f.A] * bary.X + previousVertices[f.B] * bary.Y + previousVertices[f.C] * bary.Z;
        sample = new(p, SafeNormal(Vector3.Cross(b - a, c - a)), (p - old) / poseDt);
        return Finite(sample.Position) && Finite(sample.Velocity);
    }

    private bool BuildReference(SkeletonAccess ns) => BuildPartialReferences(ns);
    private static Matrix4x4 ToMatrix(HkTransform t) =>
        Matrix4x4.CreateScale(t.Scale.X, t.Scale.Y, t.Scale.Z) *
        Matrix4x4.CreateFromQuaternion(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W)) *
        Matrix4x4.CreateTranslation(t.Translation.X, t.Translation.Y, t.Translation.Z);
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    // Triangle() accepts cross products down to squared magnitude 1e-14.
    // Accepted small skin faces must keep their real normal rather than become UnitY.
    private static Vector3 SafeNormal(Vector3 p) => p.LengthSquared() >= 1e-14f && Finite(p) ? Vector3.Normalize(p) : Vector3.UnitY;
    private struct Vertex { public Vector3 Position; public Vector4 Weights, ExtraWeights; public int B0, B1, B2, B3, B4, B5, B6, B7; }
    private struct Face { public int A, B, C, N0, N1, N2, Slot, Mesh, V0, V1, V2; public uint IndexEntry; }
    private struct TriangleSweepBounds { public uint Frame; public Vector3 Minimum, Maximum; }
    private sealed class Cluster
    {
        public int[] Triangles = Array.Empty<int>();
        public InfluenceBounds[] Influences = Array.Empty<InfluenceBounds>();
        public Cluster[] Children = Array.Empty<Cluster>();
        public uint BoundsFrame;
        public Vector3 Minimum, Maximum, PreviousMinimum, PreviousMaximum;
    }
    private struct InfluenceBounds { public int Bone; public Vector3 Minimum, Maximum; }
}
