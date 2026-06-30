using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using BepuSimulation = BepuPhysics.Simulation;

namespace CombatSimulator.Animation;

/// <summary>
/// Plugin-scoped weapon drop physics. Independent BepuPhysics2 simulation with its own
/// configurable parameters. Spawn is co-triggered with ragdoll using the matching activation
/// delay, so when ragdoll freezes the character's animation the weapon-bone writes survive
/// and the weapon mesh visibly tracks the falling rigid body.
/// </summary>
public unsafe class WeaponDropController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Single shared simulation across all dropped weapons
    private BufferPool? bufferPool;
    private BepuSimulation? simulation;

    // Snapshot of physics-relevant config used when sim was created.
    // If user changes any of these, we recreate the sim on next render frame
    // (existing weapon bodies are dropped — they'll respawn on next death).
    private float simGravity;
    private float simDamping;
    private float simAngularDamping;
    private float simBounce;
    private float simFriction;
    private int simSolverIterations;
    // Snapshot of the shape/inertia inputs too — the weapon capsule shape and inertia are
    // cached once at sim creation, so changing these must also trigger a rebuild or they go stale.
    private float simRadius;
    private float simHalfLength;
    private float simMass;

    private class Entry
    {
        public nint CharacterAddress;
        public BodyHandle? Main;
        public BodyHandle? Off;
        public int MainBoneIndex = -1;
        public int OffBoneIndex = -1;
        public TypedIndex? MainShape;
        public TypedIndex? OffShape;
        public StaticHandle? GroundTile;
        public TypedIndex? GroundShape;
        public List<BodyKinematicCollider> BodyColliders = new();
    }
    private readonly Dictionary<nint, Entry> entries = new();

    private class Pending
    {
        public nint CharacterAddress;
        public float Delay;
        public WeaponSpawnPose? MainPose;
        public WeaponSpawnPose? OffPose;
    }
    private readonly List<Pending> pending = new();

    private static readonly string[] WeaponMainHandBones = { "n_buki_r", "j_buki_r", "n_hte_r" };
    private static readonly string[] WeaponOffHandBones = { "n_buki_l", "j_buki_l", "n_hte_l" };
    private const int BodyCollisionMaxSegments = 18;
    private const float BodyCollisionMinSegmentLength = 0.08f;
    private const float BodyCollisionMinRadius = 0.035f;
    private const float BodyCollisionMaxRadius = 0.12f;
    private static readonly Vector3 BodyColliderParkPos = new(0, -9999, 0);

    private readonly struct WeaponSpawnPose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public WeaponSpawnPose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    private struct BodyKinematicCollider
    {
        public BodyHandle Handle;
        public TypedIndex Shape;
        public int BoneIndex;
        public int ParentBoneIndex;
        public float CenterFactor;
        public Vector3 PreviousPosition;
        public Quaternion PreviousOrientation;
        public bool HasPreviousPose;
    }

    public WeaponDropController(BoneTransformService boneService, Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.config = config;
        this.log = log;
        boneService.OnRenderFrame += OnRenderFrame;
    }

    public bool HasEntry(nint characterAddress) => entries.ContainsKey(characterAddress);

    /// <summary>
    /// Schedule a weapon drop for the given character. Bodies are created after <paramref name="delay"/>
    /// seconds (matching the ragdoll activation delay so weapon physics start exactly when ragdoll
    /// freezes animation). delay=0 spawns on the next render frame.
    /// </summary>
    public void SpawnFor(nint characterAddress, float delay = 0f)
    {
        if (characterAddress == nint.Zero) return;
        if (entries.ContainsKey(characterAddress)) return;
        if (pending.Exists(p => p.CharacterAddress == characterAddress)) return;

        var poses = CaptureWeaponSpawnPoses(characterAddress);
        pending.Add(new Pending
        {
            CharacterAddress = characterAddress,
            Delay = MathF.Max(0f, delay),
            MainPose = poses.Main,
            OffPose = poses.Off,
        });
    }

    /// <summary>Remove weapon bodies for a character so the weapon mesh returns to default hand parenting.</summary>
    public void RemoveFor(nint characterAddress)
    {
        pending.RemoveAll(p => p.CharacterAddress == characterAddress);
        if (!entries.TryGetValue(characterAddress, out var entry)) return;
        DisposeEntry(entry);
        entries.Remove(characterAddress);
        log.Info($"WeaponDropController: removed weapons for 0x{characterAddress:X}");
    }

    /// <summary>Remove all weapon bodies. Called on simulation reset / zone change.</summary>
    public void RemoveAll()
    {
        pending.Clear();
        if (entries.Count == 0) return;
        foreach (var entry in entries.Values) DisposeEntry(entry);
        entries.Clear();
        log.Info("WeaponDropController: removed all weapons");
    }

    private void DisposeEntry(Entry entry)
    {
        if (simulation == null) return;
        if (entry.Main.HasValue) simulation.Bodies.Remove(entry.Main.Value);
        if (entry.Off.HasValue) simulation.Bodies.Remove(entry.Off.Value);
        if (entry.GroundTile.HasValue) simulation.Statics.Remove(entry.GroundTile.Value);
        for (int i = 0; i < entry.BodyColliders.Count; i++)
        {
            simulation.Bodies.Remove(entry.BodyColliders[i].Handle);
            if (bufferPool != null)
                simulation.Shapes.RemoveAndDispose(entry.BodyColliders[i].Shape, bufferPool);
        }
        // RemoveAndDispose (not Remove): the per-drop terrain patch is a Mesh that owns a
        // pooled triangle buffer + acceleration tree. Plain Remove drops the shape but leaks
        // those buffers, and this weapon simulation lives for the whole session.
        if (entry.GroundShape.HasValue && bufferPool != null)
            simulation.Shapes.RemoveAndDispose(entry.GroundShape.Value, bufferPool);
        // Per-weapon box shapes are created per drop, so dispose them with the entry.
        if (entry.MainShape.HasValue && bufferPool != null)
            simulation.Shapes.RemoveAndDispose(entry.MainShape.Value, bufferPool);
        if (entry.OffShape.HasValue && bufferPool != null)
            simulation.Shapes.RemoveAndDispose(entry.OffShape.Value, bufferPool);
    }

    private void OnRenderFrame()
    {
        try
        {
            // Recreate sim if any physics-relevant config changed
            if (simulation != null && ConfigSnapshotChanged())
            {
                log.Info("WeaponDropController: config changed — clearing weapons and recreating simulation");
                RemoveAll();
                DestroySimulation();
                return;
            }

            // Tick pending spawns
            if (pending.Count > 0)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var p = pending[i];
                    p.Delay -= 1f / 60f;
                    if (p.Delay <= 0f)
                    {
                        pending.RemoveAt(i);
                        TrySpawn(p);
                    }
                }
            }

            if (entries.Count == 0 || simulation == null) return;

            foreach (var entry in entries.Values)
                UpdateBodyColliders(entry);

            simulation.Timestep(1f / 60f);

            foreach (var (addr, entry) in entries)
            {
                if (entry.Main.HasValue)
                {
                    var mainDraw = GetWeaponDrawObject(addr, DrawDataContainer.WeaponSlot.MainHand);
                    if (mainDraw != null) DriveWeapon(mainDraw, entry.Main.Value);
                }
                if (entry.Off.HasValue)
                {
                    var offDraw = GetWeaponDrawObject(addr, DrawDataContainer.WeaponSlot.OffHand);
                    if (offDraw != null) DriveWeapon(offDraw, entry.Off.Value);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "WeaponDropController: error in render frame");
        }
    }

    private (WeaponSpawnPose? Main, WeaponSpawnPose? Off) CaptureWeaponSpawnPoses(nint characterAddress)
    {
        var skelN = boneService.TryGetSkeleton(characterAddress);
        if (skelN == null) return (null, null);

        var skel = skelN.Value;
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return (null, null);

        var skelWorldPos = new Vector3(
            skeleton->Transform.Position.X,
            skeleton->Transform.Position.Y,
            skeleton->Transform.Position.Z);
        var skelWorldRot = new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W);

        WeaponSpawnPose? main = null;
        WeaponSpawnPose? off = null;
        if (GetWeaponDrawObject(characterAddress, DrawDataContainer.WeaponSlot.MainHand) != null &&
            TryResolveWeaponSpawnPose(skel, WeaponMainHandBones, skelWorldPos, skelWorldRot, out var mainPose))
        {
            main = mainPose;
        }

        if (GetWeaponDrawObject(characterAddress, DrawDataContainer.WeaponSlot.OffHand) != null &&
            TryResolveWeaponSpawnPose(skel, WeaponOffHandBones, skelWorldPos, skelWorldRot, out var offPose))
        {
            off = offPose;
        }

        return (main, off);
    }

    private bool TryResolveWeaponSpawnPose(SkeletonAccess skel, string[] boneCandidates,
        Vector3 skelWorldPos, Quaternion skelWorldRot, out WeaponSpawnPose pose)
    {
        pose = default;
        foreach (var name in boneCandidates)
        {
            var boneIndex = boneService.ResolveBoneIndex(skel, name);
            if (boneIndex < 0) continue;

            ref var mt = ref skel.Pose->ModelPose.Data[boneIndex];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            pose = new WeaponSpawnPose(
                skelWorldPos + Vector3.Transform(modelPos, skelWorldRot),
                Quaternion.Normalize(skelWorldRot * modelRot));
            return true;
        }

        return false;
    }

    private void TrySpawn(Pending pendingSpawn)
    {
        var characterAddress = pendingSpawn.CharacterAddress;
        if (entries.ContainsKey(characterAddress)) return;
        var skelN = boneService.TryGetSkeleton(characterAddress);
        if (skelN == null) { log.Warning($"WeaponDropController: skeleton unavailable for 0x{characterAddress:X}"); return; }
        var skel = skelN.Value;

        EnsureSimulation();
        if (simulation == null) return;

        var sk = skel.CharBase->Skeleton;
        if (sk == null) return;
        var skelWorldPos = new Vector3(sk->Transform.Position.X, sk->Transform.Position.Y, sk->Transform.Position.Z);
        var skelWorldRot = new Quaternion(sk->Transform.Rotation.X, sk->Transform.Rotation.Y, sk->Transform.Rotation.Z, sk->Transform.Rotation.W);

        // Only drop a weapon that is actually drawn (a separate DrawObject). An
        // unarmed slot has no DrawObject, so there is nothing to decouple/fall.
        var mainDraw = GetWeaponDrawObject(characterAddress, DrawDataContainer.WeaponSlot.MainHand);
        var offDraw = GetWeaponDrawObject(characterAddress, DrawDataContainer.WeaponSlot.OffHand);

        var entry = new Entry { CharacterAddress = characterAddress };
        if (mainDraw != null)
            entry.Main = TryCreateWeaponBody(skel, WeaponMainHandBones, skelWorldPos, skelWorldRot, mainDraw,
                pendingSpawn.MainPose, out entry.MainBoneIndex, out entry.MainShape);
        if (offDraw != null)
            entry.Off = TryCreateWeaponBody(skel, WeaponOffHandBones, skelWorldPos, skelWorldRot, offDraw,
                pendingSpawn.OffPose, out entry.OffBoneIndex, out entry.OffShape);

        if (!entry.Main.HasValue && !entry.Off.HasValue)
        {
            log.Info($"WeaponDropController: no drawn weapon for 0x{characterAddress:X}");
            return;
        }

        // Per-entity terrain patch under the spawn point. A flat box looks fine on level
        // ground but makes weapons visibly float or clip on slopes, so build a small mesh
        // from game collision raycasts.
        (entry.GroundTile, entry.GroundShape) = CreateTerrainPatch(skelWorldPos.X, skelWorldPos.Z, skelWorldPos.Y);
        entry.BodyColliders = CreateBodyColliders(skel, skelWorldPos, skelWorldRot);

        entries[characterAddress] = entry;
        var n = (entry.Main.HasValue ? 1 : 0) + (entry.Off.HasValue ? 1 : 0);
        log.Info($"WeaponDropController: spawned {n} weapon(s) for 0x{characterAddress:X} with {entry.BodyColliders.Count} body colliders");
    }

    private (StaticHandle StaticHandle, TypedIndex ShapeIndex) CreateTerrainPatch(float centerX, float centerZ, float defaultGroundY)
    {
        const float terrainRadius = 4.0f;
        const float terrainStep = 0.5f;
        var gridSize = (int)(terrainRadius * 2 / terrainStep) + 1;

        var patchGroundY = defaultGroundY;
        var anyHit = false;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(centerX, defaultGroundY + 5.0f, centerZ),
                new Vector3(0, -1, 0), out var centerHit, 80f))
        {
            patchGroundY = centerHit.Point.Y;
            anyHit = true;
        }

        var heights = new float[gridSize, gridSize];
        var ox = centerX - terrainRadius;
        var oz = centerZ - terrainRadius;
        for (int gz = 0; gz < gridSize; gz++)
        for (int gx = 0; gx < gridSize; gx++)
        {
            var wx = ox + gx * terrainStep;
            var wz = oz + gz * terrainStep;
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(wx, patchGroundY + 5.0f, wz),
                    new Vector3(0, -1, 0), out var gridHit, 80f))
            {
                heights[gx, gz] = gridHit.Point.Y;
                anyHit = true;
            }
            else
            {
                heights[gx, gz] = patchGroundY;
            }
        }

        if (!anyHit)
        {
            log.Warning($"WeaponDropController: terrain raycasts missed at ({centerX:F2},{centerZ:F2}); using flat fallback.");
            var fallbackShape = simulation!.Shapes.Add(new Box(8f, 0.1f, 8f));
            var fallbackStatic = simulation.Statics.Add(new StaticDescription(
                new Vector3(centerX, defaultGroundY - 0.05f, centerZ),
                Quaternion.Identity,
                fallbackShape));
            return (fallbackStatic, fallbackShape);
        }

        var triCount = (gridSize - 1) * (gridSize - 1) * 2;
        bufferPool!.Take<Triangle>(triCount, out var triangles);
        var ti = 0;
        for (int gz = 0; gz < gridSize - 1; gz++)
        for (int gx = 0; gx < gridSize - 1; gx++)
        {
            var x0 = ox + gx * terrainStep;
            var x1 = x0 + terrainStep;
            var z0 = oz + gz * terrainStep;
            var z1 = z0 + terrainStep;
            var v00 = new Vector3(x0, heights[gx, gz], z0);
            var v10 = new Vector3(x1, heights[gx + 1, gz], z0);
            var v01 = new Vector3(x0, heights[gx, gz + 1], z1);
            var v11 = new Vector3(x1, heights[gx + 1, gz + 1], z1);

            triangles[ti++] = new Triangle(v00, v10, v01);
            triangles[ti++] = new Triangle(v10, v11, v01);
        }

        var mesh = new BepuPhysics.Collidables.Mesh(triangles, Vector3.One, bufferPool);
        var shapeIndex = simulation!.Shapes.Add(mesh);
        var staticHandle = simulation.Statics.Add(new StaticDescription(
            Vector3.Zero,
            Quaternion.Identity,
            shapeIndex));
        return (staticHandle, shapeIndex);
    }

    private List<BodyKinematicCollider> CreateBodyColliders(SkeletonAccess skel, Vector3 skelWorldPos, Quaternion skelWorldRot)
    {
        var result = new List<BodyKinematicCollider>();
        if (simulation == null) return result;

        var candidates = new List<(float SegLen, int BoneIdx, int ParentIdx, float HalfLen, float Radius, float CenterFactor, Vector3 Center, Quaternion Rot)>();
        var boneCount = Math.Min(skel.BoneCount, skel.ParentCount);
        for (int i = 1; i < boneCount; i++)
        {
            var parentIdx = skel.HavokSkeleton->ParentIndices[i];
            if (parentIdx < 0 || parentIdx >= skel.BoneCount) continue;

            ref var parentMt = ref skel.Pose->ModelPose.Data[parentIdx];
            var parentWorldPos = ModelToWorld(
                new Vector3(parentMt.Translation.X, parentMt.Translation.Y, parentMt.Translation.Z),
                skelWorldPos, skelWorldRot);

            ref var childMt = ref skel.Pose->ModelPose.Data[i];
            var childWorldPos = ModelToWorld(
                new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z),
                skelWorldPos, skelWorldRot);

            var segment = childWorldPos - parentWorldPos;
            var segLen = segment.Length();
            if (segLen < BodyCollisionMinSegmentLength) continue;

            var radius = Math.Clamp(segLen * 0.12f, BodyCollisionMinRadius, BodyCollisionMaxRadius);
            var halfLen = MathF.Max(0.01f, (segLen * 0.5f) - radius);
            var centerFactor = 0.5f;
            var segDir = segment / segLen;
            candidates.Add((segLen, i, parentIdx, halfLen, radius, centerFactor,
                parentWorldPos + (segLen * centerFactor) * segDir,
                RotationFromYToDirection(segment)));
        }

        if (candidates.Count > BodyCollisionMaxSegments)
        {
            candidates.Sort((a, b) => b.SegLen.CompareTo(a.SegLen));
            candidates.RemoveRange(BodyCollisionMaxSegments, candidates.Count - BodyCollisionMaxSegments);
        }

        foreach (var c in candidates)
        {
            var shape = simulation.Shapes.Add(new Capsule(c.Radius, c.HalfLen * 2f));
            var handle = simulation.Bodies.Add(BodyDescription.CreateKinematic(
                new RigidPose(c.Center, c.Rot),
                default(BodyVelocity),
                new CollidableDescription(shape, 0.04f),
                new BodyActivityDescription(0.01f)));
            result.Add(new BodyKinematicCollider
            {
                Handle = handle,
                Shape = shape,
                BoneIndex = c.BoneIdx,
                ParentBoneIndex = c.ParentIdx,
                CenterFactor = c.CenterFactor,
                PreviousPosition = c.Center,
                PreviousOrientation = c.Rot,
                HasPreviousPose = true,
            });
        }

        return result;
    }

    private void UpdateBodyColliders(Entry entry)
    {
        if (simulation == null || entry.BodyColliders.Count == 0) return;

        var skelN = boneService.TryGetSkeleton(entry.CharacterAddress);
        if (skelN == null)
        {
            ParkBodyColliders(entry);
            return;
        }

        var skel = skelN.Value;
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null)
        {
            ParkBodyColliders(entry);
            return;
        }

        var skelWorldPos = new Vector3(
            skeleton->Transform.Position.X,
            skeleton->Transform.Position.Y,
            skeleton->Transform.Position.Z);
        var skelWorldRot = new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W);

        for (int i = 0; i < entry.BodyColliders.Count; i++)
        {
            var bc = entry.BodyColliders[i];
            var bodyRef = simulation.Bodies.GetBodyReference(bc.Handle);
            if (bc.BoneIndex < 0 || bc.BoneIndex >= skel.BoneCount ||
                bc.ParentBoneIndex < 0 || bc.ParentBoneIndex >= skel.BoneCount)
            {
                MoveKinematicCollider(ref bc, bodyRef, BodyColliderParkPos, Quaternion.Identity);
                entry.BodyColliders[i] = bc;
                continue;
            }

            ref var parentMt = ref skel.Pose->ModelPose.Data[bc.ParentBoneIndex];
            var parentWorldPos = ModelToWorld(
                new Vector3(parentMt.Translation.X, parentMt.Translation.Y, parentMt.Translation.Z),
                skelWorldPos, skelWorldRot);

            ref var childMt = ref skel.Pose->ModelPose.Data[bc.BoneIndex];
            var childWorldPos = ModelToWorld(
                new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z),
                skelWorldPos, skelWorldRot);

            var segment = childWorldPos - parentWorldPos;
            var segLen = segment.Length();
            Vector3 targetPosition;
            Quaternion targetOrientation;
            if (segLen > 0.01f)
            {
                var segDir = segment / segLen;
                targetPosition = parentWorldPos + (segLen * bc.CenterFactor) * segDir;
                targetOrientation = RotationFromYToDirection(segment);
            }
            else
            {
                targetPosition = parentWorldPos;
                targetOrientation = Quaternion.Identity;
            }

            MoveKinematicCollider(ref bc, bodyRef, targetPosition, targetOrientation);
            entry.BodyColliders[i] = bc;
        }
    }

    private void ParkBodyColliders(Entry entry)
    {
        if (simulation == null) return;
        for (int i = 0; i < entry.BodyColliders.Count; i++)
        {
            var bc = entry.BodyColliders[i];
            var bodyRef = simulation.Bodies.GetBodyReference(bc.Handle);
            MoveKinematicCollider(ref bc, bodyRef, BodyColliderParkPos, Quaternion.Identity);
            entry.BodyColliders[i] = bc;
        }
    }

    private static void MoveKinematicCollider(ref BodyKinematicCollider collider, BodyReference bodyRef,
        Vector3 targetPosition, Quaternion targetOrientation)
    {
        const float dt = 1f / 60f;
        targetOrientation = Quaternion.Normalize(targetOrientation);

        var linearVelocity = collider.HasPreviousPose
            ? (targetPosition - collider.PreviousPosition) / dt
            : Vector3.Zero;
        var angularVelocity = collider.HasPreviousPose
            ? EstimateAngularVelocity(collider.PreviousOrientation, targetOrientation, dt)
            : Vector3.Zero;

        bodyRef.Pose.Position = targetPosition;
        bodyRef.Pose.Orientation = targetOrientation;
        bodyRef.Velocity.Linear = linearVelocity;
        bodyRef.Velocity.Angular = angularVelocity;
        bodyRef.Awake = true;

        collider.PreviousPosition = targetPosition;
        collider.PreviousOrientation = targetOrientation;
        collider.HasPreviousPose = true;
    }

    private static Vector3 EstimateAngularVelocity(Quaternion previous, Quaternion current, float dt)
    {
        if (dt <= 0f) return Vector3.Zero;

        previous = Quaternion.Normalize(previous);
        current = Quaternion.Normalize(current);
        var delta = Quaternion.Normalize(current * Quaternion.Inverse(previous));
        if (delta.W < 0f)
            delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);

        var sinHalfAngle = MathF.Sqrt(MathF.Max(0f, 1f - delta.W * delta.W));
        if (sinHalfAngle < 1e-5f) return Vector3.Zero;

        var angle = 2f * MathF.Acos(Math.Clamp(delta.W, -1f, 1f));
        var axis = new Vector3(delta.X, delta.Y, delta.Z) / sinHalfAngle;
        return axis * (angle / dt);
    }

    private BodyHandle? TryCreateWeaponBody(SkeletonAccess skel, string[] boneCandidates,
        Vector3 skelWorldPos, Quaternion skelWorldRot, DrawObject* weaponDraw,
        WeaponSpawnPose? cachedSpawnPose, out int boneIndex, out TypedIndex? shapeIndex)
    {
        boneIndex = -1;
        shapeIndex = null;
        if (!cachedSpawnPose.HasValue)
        {
            foreach (var name in boneCandidates)
            {
                var idx = boneService.ResolveBoneIndex(skel, name);
                if (idx >= 0) { boneIndex = idx; break; }
            }
            if (boneIndex < 0) return null;
        }

        Vector3 worldPos;
        Quaternion worldRot;
        if (cachedSpawnPose.HasValue)
        {
            worldPos = cachedSpawnPose.Value.Position;
            worldRot = cachedSpawnPose.Value.Rotation;
        }
        else
        {
            ref var mt = ref skel.Pose->ModelPose.Data[boneIndex];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            worldPos = skelWorldPos + Vector3.Transform(modelPos, skelWorldRot);
            worldRot = Quaternion.Normalize(skelWorldRot * modelRot);
        }

        // Per-weapon flat box: a capsule rolls forever, a box settles on a face. Length is taken from
        // the weapon's own skeleton span where available (so a long weapon gets a long box, a small
        // one stays small), falling back to the configured length; thin across so it lies flat.
        var halfLength = MathF.Max(config.WeaponDropHalfLength, EstimateWeaponHalfLength(weaponDraw));
        var halfWidth = MathF.Max(0.01f, config.WeaponDropRadius);
        var halfThick = halfWidth * 0.4f;
        var box = new Box(halfWidth * 2f, halfLength * 2f, halfThick * 2f);
        var shape = simulation!.Shapes.Add(box);
        shapeIndex = shape;
        var inertia = box.ComputeInertia(config.WeaponDropMass);
        worldPos = LiftWeaponPoseAboveGround(worldPos, worldRot, halfWidth, halfLength, halfThick);

        // Zero initial velocity (no jitter, no inheritance) — user requirement.
        var handle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(worldPos, worldRot),
            default(BodyVelocity),
            inertia,
            new CollidableDescription(shape, 0.04f),
            new BodyActivityDescription(0.01f)));
        return handle;
    }

    private Vector3 LiftWeaponPoseAboveGround(Vector3 worldPos, Quaternion worldRot,
        float halfWidth, float halfLength, float halfThick)
    {
        const float clearance = 0.02f;
        var groundY = SampleGroundY(worldPos.X, worldPos.Z, worldPos.Y);
        if (!groundY.HasValue) return worldPos;

        var verticalHalfExtent = WorldVerticalHalfExtent(worldRot, halfWidth, halfLength, halfThick);
        var minY = worldPos.Y - verticalHalfExtent;
        var targetMinY = groundY.Value + clearance;
        if (minY < targetMinY)
            worldPos.Y += targetMinY - minY;
        return worldPos;
    }

    private static float WorldVerticalHalfExtent(Quaternion worldRot, float halfWidth, float halfLength, float halfThick)
    {
        var x = Vector3.Transform(Vector3.UnitX, worldRot);
        var y = Vector3.Transform(Vector3.UnitY, worldRot);
        var z = Vector3.Transform(Vector3.UnitZ, worldRot);
        return MathF.Abs(Vector3.Dot(x, Vector3.UnitY)) * halfWidth
             + MathF.Abs(Vector3.Dot(y, Vector3.UnitY)) * halfLength
             + MathF.Abs(Vector3.Dot(z, Vector3.UnitY)) * halfThick;
    }

    private float? SampleGroundY(float x, float z, float defaultY)
    {
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(x, defaultY + 5.0f, z),
                new Vector3(0, -1, 0), out var hit, 80f))
        {
            return hit.Point.Y;
        }

        return null;
    }

    /// <summary>Estimate half the weapon's longest dimension from its own skeleton's bone spread
    /// (model space × draw scale). Returns 0 when the weapon has no usable multi-bone skeleton.</summary>
    private float EstimateWeaponHalfLength(DrawObject* weaponDraw)
    {
        if (weaponDraw == null) return 0f;
        var wskelN = boneService.TryGetSkeletonFromCharBase((CharacterBase*)weaponDraw);
        if (wskelN == null) return 0f;
        var wskel = wskelN.Value;
        if (wskel.BoneCount < 2) return 0f;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < wskel.BoneCount; i++)
        {
            ref var b = ref wskel.Pose->ModelPose.Data[i];
            var p = new Vector3(b.Translation.X, b.Translation.Y, b.Translation.Z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var span = max - min;
        var scale = weaponDraw->Scale.X <= 0f ? 1f : weaponDraw->Scale.X;
        // Longest axis of the bone AABB; pad a little since bones rarely reach the mesh tip. Clamp to
        // sane weapon lengths so a stray bone can't produce an absurd box.
        var longest = MathF.Max(span.X, MathF.Max(span.Y, span.Z)) * scale;
        if (longest < 0.05f) return 0f;
        return Math.Clamp(longest * 0.6f, 0.05f, 0.8f);
    }

    /// <summary>Resolve the drawn weapon DrawObject for a slot, or null if unarmed/unloaded.</summary>
    private static DrawObject* GetWeaponDrawObject(nint characterAddress, DrawDataContainer.WeaponSlot slot)
    {
        if (characterAddress == nint.Zero) return null;
        var character = (Character*)characterAddress;
        return character->DrawData.Weapon(slot).DrawObject;
    }

    /// <summary>Drive a weapon DrawObject's world transform from its rigid body.</summary>
    private void DriveWeapon(DrawObject* weaponDraw, BodyHandle bodyHandle)
    {
        if (simulation == null) return;

        var cb = (CharacterBase*)weaponDraw;
        var bodyRef = simulation.Bodies.GetBodyReference(bodyHandle);
        var pos = bodyRef.Pose.Position;
        var rot = bodyRef.Pose.Orientation;

        // The weapon mesh is skinned to its OWN skeleton; the attach normally
        // writes that skeleton's root world Transform from the owner hand bone.
        // Overwrite it here (after the attach ran this frame) so the rendered
        // weapon follows the rigid body — the same way ragdoll overwrites body
        // bone ModelPose. DrawObject.Position alone does NOT move the mesh.
        var sk = cb->Skeleton;
        if (sk != null)
        {
            sk->Transform.Position.X = pos.X;
            sk->Transform.Position.Y = pos.Y;
            sk->Transform.Position.Z = pos.Z;
            sk->Transform.Rotation.X = rot.X;
            sk->Transform.Rotation.Y = rot.Y;
            sk->Transform.Rotation.Z = rot.Z;
            sk->Transform.Rotation.W = rot.W;
        }

        weaponDraw->Position = pos;
        weaponDraw->Rotation = rot;
    }

    private bool ConfigSnapshotChanged()
    {
        return simGravity != config.WeaponDropGravity
            || simDamping != config.WeaponDropDamping
            || simAngularDamping != config.WeaponDropAngularDamping
            || simBounce != config.WeaponDropBounce
            || simFriction != config.WeaponDropFriction
            || simSolverIterations != config.WeaponDropSolverIterations
            || simRadius != config.WeaponDropRadius
            || simHalfLength != config.WeaponDropHalfLength
            || simMass != config.WeaponDropMass;
    }

    private void EnsureSimulation()
    {
        if (simulation != null) return;

        simGravity = config.WeaponDropGravity;
        simDamping = config.WeaponDropDamping;
        simAngularDamping = config.WeaponDropAngularDamping;
        simBounce = config.WeaponDropBounce;
        simFriction = config.WeaponDropFriction;
        simSolverIterations = config.WeaponDropSolverIterations;
        simRadius = config.WeaponDropRadius;
        simHalfLength = config.WeaponDropHalfLength;
        simMass = config.WeaponDropMass;

        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new WeaponDropNarrowPhaseCallbacks { Friction = simFriction, MaxRecoveryVelocity = simBounce },
            new WeaponDropPoseIntegratorCallbacks(new Vector3(0, -simGravity, 0), simDamping, simAngularDamping),
            new SolveDescription(simSolverIterations, 1));

        // Weapon collision shapes are now built per weapon (per-drop boxes) in TryCreateWeaponBody.
        log.Info($"WeaponDropController: simulation created (gravity={simGravity:F2}, bounce={simBounce:F2}, friction={simFriction:F2}, iter={simSolverIterations})");
    }

    private static Vector3 ModelToWorld(Vector3 modelPos, Vector3 skelWorldPos, Quaternion skelWorldRot)
        => skelWorldPos + Vector3.Transform(modelPos, skelWorldRot);

    private static Quaternion RotationFromYToDirection(Vector3 dir)
    {
        var dirN = Vector3.Normalize(dir);
        var dot = Vector3.Dot(Vector3.UnitY, dirN);

        if (dot > 0.9999f) return Quaternion.Identity;
        if (dot < -0.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);

        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, dirN));
        var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }

    private void DestroySimulation()
    {
        simulation?.Dispose();
        simulation = null;
        bufferPool?.Clear();
        bufferPool = null;
    }

    public void Dispose()
    {
        boneService.OnRenderFrame -= OnRenderFrame;
        RemoveAll();
        DestroySimulation();
    }
}

// --- BEPU callbacks ---

struct WeaponDropNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public float Friction;
    public float MaxRecoveryVelocity;

    public void Initialize(BepuSimulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // Only dynamic weapons collide with ground statics and mirrored kinematic body colliders.
        // Weapon-vs-weapon stays disabled because mainhand/offhand can overlap on creation.
        var aDynamic = a.Mobility == CollidableMobility.Dynamic;
        var bDynamic = b.Mobility == CollidableMobility.Dynamic;
        var aPassive = a.Mobility is CollidableMobility.Static or CollidableMobility.Kinematic;
        var bPassive = b.Mobility is CollidableMobility.Static or CollidableMobility.Kinematic;
        return (aDynamic && bPassive) || (bDynamic && aPassive);
    }

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        => true;

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = Friction;
        pairMaterial.MaximumRecoveryVelocity = MaxRecoveryVelocity;
        pairMaterial.SpringSettings = new SpringSettings(30, 1);
        return true;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;

    public void Dispose() { }
}

struct WeaponDropPoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    private Vector3 gravity;
    private float linearDamping;
    private float angularDamping;
    private Vector3Wide gravityDt;
    private Vector<float> linearDampingDt;
    private Vector<float> angularDampingDt;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public WeaponDropPoseIntegratorCallbacks(Vector3 gravity, float linearDamping, float angularDamping)
    {
        this.gravity = gravity;
        this.linearDamping = linearDamping;
        this.angularDamping = angularDamping;
        this.gravityDt = default;
        this.linearDampingDt = default;
        this.angularDampingDt = default;
    }

    public void Initialize(BepuSimulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        gravityDt.X = new Vector<float>(gravity.X * dt);
        gravityDt.Y = new Vector<float>(gravity.Y * dt);
        gravityDt.Z = new Vector<float>(gravity.Z * dt);
        linearDampingDt  = new Vector<float>(MathF.Pow(linearDamping,  dt * 60f));
        angularDampingDt = new Vector<float>(MathF.Pow(angularDamping, dt * 60f));
    }

    public void IntegrateVelocity(
        Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex,
        Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear.X = (velocity.Linear.X + gravityDt.X) * linearDampingDt;
        velocity.Linear.Y = (velocity.Linear.Y + gravityDt.Y) * linearDampingDt;
        velocity.Linear.Z = (velocity.Linear.Z + gravityDt.Z) * linearDampingDt;
        velocity.Angular.X *= angularDampingDt;
        velocity.Angular.Y *= angularDampingDt;
        velocity.Angular.Z *= angularDampingDt;
    }
}
