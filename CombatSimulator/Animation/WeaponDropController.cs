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
    private TypedIndex weaponShapeIndex;
    private BodyInertia weaponInertia;

    // Snapshot of physics-relevant config used when sim was created.
    // If user changes any of these, we recreate the sim on next render frame
    // (existing weapon bodies are dropped — they'll respawn on next death).
    private float simGravity;
    private float simDamping;
    private float simBounce;
    private float simFriction;
    private int simSolverIterations;

    private class Entry
    {
        public nint CharacterAddress;
        public BodyHandle? Main;
        public BodyHandle? Off;
        public int MainBoneIndex = -1;
        public int OffBoneIndex = -1;
        public StaticHandle? GroundTile;
        public TypedIndex? GroundShape;
    }
    private readonly Dictionary<nint, Entry> entries = new();

    private class Pending
    {
        public nint CharacterAddress;
        public float Delay;
    }
    private readonly List<Pending> pending = new();

    private static readonly string[] WeaponMainHandBones = { "n_buki_r", "j_buki_r", "n_hte_r" };
    private static readonly string[] WeaponOffHandBones = { "n_buki_l", "j_buki_l", "n_hte_l" };

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

        pending.Add(new Pending { CharacterAddress = characterAddress, Delay = MathF.Max(0f, delay) });
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
        if (entry.GroundShape.HasValue) simulation.Shapes.Remove(entry.GroundShape.Value);
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
                        TrySpawn(p.CharacterAddress);
                    }
                }
            }

            if (entries.Count == 0 || simulation == null) return;

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

    private void TrySpawn(nint characterAddress)
    {
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
            entry.Main = TryCreateWeaponBody(skel, WeaponMainHandBones, skelWorldPos, skelWorldRot, out entry.MainBoneIndex);
        if (offDraw != null)
            entry.Off = TryCreateWeaponBody(skel, WeaponOffHandBones, skelWorldPos, skelWorldRot, out entry.OffBoneIndex);

        if (!entry.Main.HasValue && !entry.Off.HasValue)
        {
            log.Info($"WeaponDropController: no drawn weapon for 0x{characterAddress:X}");
            return;
        }

        // Per-entity terrain patch under the spawn point. A flat box looks fine on level
        // ground but makes weapons visibly float or clip on slopes, so build a small mesh
        // from game collision raycasts.
        (entry.GroundTile, entry.GroundShape) = CreateTerrainPatch(skelWorldPos.X, skelWorldPos.Z, skelWorldPos.Y);

        entries[characterAddress] = entry;
        var n = (entry.Main.HasValue ? 1 : 0) + (entry.Off.HasValue ? 1 : 0);
        log.Info($"WeaponDropController: spawned {n} weapon(s) for 0x{characterAddress:X}");
    }

    private (StaticHandle StaticHandle, TypedIndex ShapeIndex) CreateTerrainPatch(float centerX, float centerZ, float defaultGroundY)
    {
        const float terrainRadius = 4.0f;
        const float terrainStep = 0.5f;
        var gridSize = (int)(terrainRadius * 2 / terrainStep) + 1;

        var patchGroundY = defaultGroundY;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(centerX, defaultGroundY + 5.0f, centerZ),
                new Vector3(0, -1, 0), out var centerHit, 80f))
            patchGroundY = centerHit.Point.Y;

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
                heights[gx, gz] = gridHit.Point.Y;
            else
                heights[gx, gz] = patchGroundY;
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

    private BodyHandle? TryCreateWeaponBody(SkeletonAccess skel, string[] boneCandidates,
        Vector3 skelWorldPos, Quaternion skelWorldRot, out int boneIndex)
    {
        boneIndex = -1;
        foreach (var name in boneCandidates)
        {
            var idx = boneService.ResolveBoneIndex(skel, name);
            if (idx >= 0) { boneIndex = idx; break; }
        }
        if (boneIndex < 0) return null;

        ref var mt = ref skel.Pose->ModelPose.Data[boneIndex];
        var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
        var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
        var worldPos = skelWorldPos + Vector3.Transform(modelPos, skelWorldRot);
        var worldRot = Quaternion.Normalize(skelWorldRot * modelRot);

        // Zero initial velocity (no jitter, no inheritance) — user requirement.
        var handle = simulation!.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(worldPos, worldRot),
            default(BodyVelocity),
            weaponInertia,
            new CollidableDescription(weaponShapeIndex, 0.04f),
            new BodyActivityDescription(0.01f)));
        return handle;
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
            || simBounce != config.WeaponDropBounce
            || simFriction != config.WeaponDropFriction
            || simSolverIterations != config.WeaponDropSolverIterations;
    }

    private void EnsureSimulation()
    {
        if (simulation != null) return;

        simGravity = config.WeaponDropGravity;
        simDamping = config.WeaponDropDamping;
        simBounce = config.WeaponDropBounce;
        simFriction = config.WeaponDropFriction;
        simSolverIterations = config.WeaponDropSolverIterations;

        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new WeaponDropNarrowPhaseCallbacks { Friction = simFriction, MaxRecoveryVelocity = simBounce },
            new WeaponDropPoseIntegratorCallbacks(new Vector3(0, -simGravity, 0), simDamping),
            new SolveDescription(simSolverIterations, 1));

        var weaponShape = new Capsule(config.WeaponDropRadius, config.WeaponDropHalfLength * 2f);
        weaponShapeIndex = simulation.Shapes.Add(weaponShape);
        weaponInertia = weaponShape.ComputeInertia(config.WeaponDropMass);

        log.Info($"WeaponDropController: simulation created (gravity={simGravity:F2}, bounce={simBounce:F2}, friction={simFriction:F2}, iter={simSolverIterations})");
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
        // Only weapon-vs-static (ground tiles). Weapon-vs-weapon disabled — mainhand and offhand
        // spawn near each other on the body, overlap on creation, and produce erratic launches.
        return a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static;
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
    private Vector3Wide gravityDt;
    private Vector<float> dampingDt;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public WeaponDropPoseIntegratorCallbacks(Vector3 gravity, float linearDamping)
    {
        this.gravity = gravity;
        this.linearDamping = linearDamping;
        this.gravityDt = default;
        this.dampingDt = default;
    }

    public void Initialize(BepuSimulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        gravityDt.X = new Vector<float>(gravity.X * dt);
        gravityDt.Y = new Vector<float>(gravity.Y * dt);
        gravityDt.Z = new Vector<float>(gravity.Z * dt);
        dampingDt = new Vector<float>(MathF.Pow(linearDamping, dt * 60f));
    }

    public void IntegrateVelocity(
        Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex,
        Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear.X = (velocity.Linear.X + gravityDt.X) * dampingDt;
        velocity.Linear.Y = (velocity.Linear.Y + gravityDt.Y) * dampingDt;
        velocity.Linear.Z = (velocity.Linear.Z + gravityDt.Z) * dampingDt;
        velocity.Angular.X *= dampingDt;
        velocity.Angular.Y *= dampingDt;
        velocity.Angular.Z *= dampingDt;
    }
}
