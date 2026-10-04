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
    private const int MaxBones = 512;
    private const int MaxSlots = 32;
    private const int MaxSkinVertices = 4096;
    private const int MaxTriangleTests = 4096;
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
    private nint actor, drawIdentity, skeletonIdentity;
    private ulong objectIdentity;
    private uint territoryIdentity, frame;
    private int boneCount, skeletonBoneCount, modelSlotCount, mouthBone = -1, tests;
    private bool jawSource, poseAvailable, previousPoseAvailable;
    private float poseDt;
    private Vector3 mouth, previousMouth, mouthNormal;
    public CharacterFluidSurface(BoneTransformService bones) => this.bones = bones;
    public uint Generation { get; private set; } = 1;
    public bool HasSurface => poseAvailable && faces.Length != 0;
    public string Status { get; private set; } = "No actor";
    public int SkinVerticesThisFrame { get; private set; }
    public int TriangleTestsThisFrame => tests;
    /// <summary>False query results after this flag must defer simulation, not be interpreted as an open edge or a miss.</summary>
    public bool BudgetExhausted { get; private set; }
    public int TriangleCount => faces.Length;
    /// <summary>Additional model-space jaw/head offset; subject to character scale.</summary>
    public Vector3 MouthOffset { get; set; }

    public void Clear()
    {
        actor = drawIdentity = skeletonIdentity = 0;
        objectIdentity = 0;
        poseAvailable = previousPoseAvailable = false;
        boneCount = skeletonBoneCount = 0; mouthBone = -1;
        vertices = Array.Empty<Vertex>(); faces = Array.Empty<Face>(); clusters = Array.Empty<Cluster>();
        currentVertices = previousVertices = Array.Empty<Vector3>(); vertexFrames = Array.Empty<uint>();
        Array.Clear(resources); Array.Clear(resourceData); Array.Clear(attributeMasks); Array.Clear(shapeMasks);
        unchecked { Generation++; }
        Status = "No actor";
    }

    /// <summary>Call on Framework, only while enabled. Resource loading occurs only on identity/mask change.</summary>
    public void UpdateActor(nint address, ulong objectId, uint territory)
    {
        if (address == 0) { if (actor != 0) Clear(); return; }
        var access = bones.TryGetSkeleton(address);
        if (access == null) { poseAvailable = false; Status = "Pose unavailable"; return; }
        var ns = access.Value;
        bool changed = actor != address || objectIdentity != objectId || territoryIdentity != territory ||
            drawIdentity != (nint)ns.CharBase || skeletonIdentity != (nint)ns.HavokSkeleton || skeletonBoneCount != ns.BoneCount;
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
        if (!changed) return;
        actor = address; objectIdentity = objectId; territoryIdentity = territory;
        modelSlotCount = slots;
        drawIdentity = (nint)ns.CharBase; skeletonIdentity = (nint)ns.HavokSkeleton;
        poseAvailable = previousPoseAvailable = false;
        unchecked { Generation++; }
        boneCount = Math.Min(ns.BoneCount, MaxBones);
        skeletonBoneCount = ns.BoneCount;
        mouthBone = bones.ResolveBoneIndex(ns, "j_ago"); jawSource = mouthBone >= 0;
        if (mouthBone < 0) mouthBone = bones.ResolveBoneIndex(ns, "j_kao");
        if (!BuildReference(ns))
        {
            vertices = Array.Empty<Vertex>(); faces = Array.Empty<Face>(); clusters = Array.Empty<Cluster>();
            currentVertices = previousVertices = Array.Empty<Vector3>(); vertexFrames = Array.Empty<uint>();
            Status = "Reference skeleton unsupported; mouth source only";
            return;
        }
        BuildTopology(ns);
    }

    /// <summary>Must run after existing pose writers. Missing or redrawn actors invalidate rather than dereference cached native data.</summary>
    public bool CapturePose(float delta)
    {
        BudgetExhausted = false;
        poseAvailable = false;
        // Cached addresses are never a validity proof. First-phase scope is the current LocalPlayer only.
        var localPlayer = Services.ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != actor || localPlayer.GameObjectId != objectIdentity ||
            Services.ClientState.TerritoryType != territoryIdentity) return false;
        if (actor == 0 || ((GameObject*)actor)->DrawObject == null ||
            (nint)((GameObject*)actor)->DrawObject != drawIdentity) return false;
        var access = bones.TryGetSkeleton(actor);
        if (access == null) return false;
        var ns = access.Value;
        if ((nint)ns.HavokSkeleton != skeletonIdentity || ns.Pose->ModelPose.Length < boneCount || ns.BoneCount != skeletonBoneCount) return false;
        int slots = Math.Clamp(ns.CharBase->SlotCount, 0, MaxSlots);
        if (slots != modelSlotCount) return false;
        for (int i = 0; i < slots; i++)
        {
            var model = ns.CharBase->Models == null ? null : ns.CharBase->Models[i];
            if ((model == null ? 0 : (nint)model->ModelResourceHandle) != resources[i] ||
                (model == null || model->ModelResourceHandle == null ? 0 : (nint)model->ModelResourceHandle->ModelData) != resourceData[i] ||
                (model == null ? 0u : model->EnabledAttributeIndexMask) != attributeMasks[i] ||
                (model == null ? 0u : model->EnabledShapeKeyIndexMask) != shapeMasks[i]) return false;
        }
        var transform = ns.CharBase->Skeleton->Transform;
        var root = Matrix4x4.CreateScale(new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z)) *
            Matrix4x4.CreateFromQuaternion(new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W)) *
            Matrix4x4.CreateTranslation(new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z));
        poseDt = Math.Clamp(delta, 0.001f, 0.1f);
        bool continuous = previousPoseAvailable && delta > 0 && delta < 0.15f;
        previousMouth = mouth;
        for (int i = 0; i < boneCount; i++)
        {
            previousSkinWorld[i] = skinWorld[i];
            boneWorld[i] = ToMatrix(ns.Pose->ModelPose.Data[i]) * root;
            skinWorld[i] = inverseBind[i] * boneWorld[i];
            if (!continuous) previousSkinWorld[i] = skinWorld[i];
        }
        if (mouthBone >= 0 && mouthBone < boneCount)
        {
            // Jaw reference is near the hinge; head fallback is an explicitly approximate source.
            var offset = (jawSource ? new Vector3(0, 0.004f, 0.025f) : new Vector3(0, -0.052f, 0.075f)) + MouthOffset;
            mouth = Vector3.Transform(offset, boneWorld[mouthBone]);
            mouthNormal = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, boneWorld[mouthBone]));
            if (!continuous || Vector3.DistanceSquared(mouth, previousMouth) > 4) previousMouth = mouth;
        }
        unchecked { frame++; }
        if (frame == 0) { Array.Clear(vertexFrames); frame = 1; }
        SkinVerticesThisFrame = tests = 0;
        foreach (var cluster in clusters) UpdateBounds(cluster);
        previousPoseAvailable = poseAvailable = true;
        return true;
    }

    public bool TryGetMouth(out FluidSurfaceSample sample)
    {
        sample = default;
        if (!poseAvailable || mouthBone < 0 || mouthBone >= boneCount || !Finite(mouth)) return false;
        sample = new(mouth, mouthNormal, (mouth - previousMouth) / poseDt);
        return true;
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

    private bool BuildReference(SkeletonAccess ns)
    {
        if (ns.HavokSkeleton->ReferencePose.Length < boneCount || ns.ParentCount < boneCount || ns.BoneCount > MaxBones) return false;
        var reference = new Matrix4x4[boneCount]; // Once per skeleton generation.
        for (int i = 0; i < boneCount; i++)
        {
            int parent = ns.HavokSkeleton->ParentIndices[i];
            if (parent >= i) return false;
            reference[i] = ToMatrix(ns.HavokSkeleton->ReferencePose.Data[i]);
            if (parent >= 0) reference[i] *= reference[parent];
            if (!Matrix4x4.Invert(reference[i], out inverseBind[i])) return false;
        }
        return true;
    }
    private static Matrix4x4 ToMatrix(HkTransform t) =>
        Matrix4x4.CreateScale(t.Scale.X, t.Scale.Y, t.Scale.Z) *
        Matrix4x4.CreateFromQuaternion(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W)) *
        Matrix4x4.CreateTranslation(t.Translation.X, t.Translation.Y, t.Translation.Z);
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static Vector3 SafeNormal(Vector3 p) => p.LengthSquared() > 1e-12f && Finite(p) ? Vector3.Normalize(p) : Vector3.UnitY;
    private struct Vertex { public Vector3 Position; public Vector4 Weights; public int B0, B1, B2, B3; }
    private struct Face { public int A, B, C, N0, N1, N2; }
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
