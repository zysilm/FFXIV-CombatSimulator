using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Fighting;
using CombatSimulator.Integration;
using CombatSimulator.Safety;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Npcs;

/// <summary>
/// Hidden dev mode: "EnemyControl". On player death, spawns a controllable creature (default Bat,
/// a BNpc model) with no HP. The player flies it around with the keyboard; the camera follows
/// the creature (active-camera-style free orbit centered on it); and the attack input (keyboard
/// or gamepad — routed via the UseAction hook) toggles a grab (see config.EnemyControlGrabPairs).
///
/// Controls:
///   W / S — forward / back along facing,  A / D — turn,  Q / E — ascend / descend (ground-clamped)
///   Attack action (your real hotkey/gamepad button) — toggle grab
///
/// Position/rotation are driven via MovementBlockHook (so the game's own movement doesn't fight
/// our writes). No AI, not targetable.
/// </summary>
public unsafe class EnemyControlController : IDisposable
{
    private readonly IKeyState keyState;
    private readonly IGamepadState gamepad;
    private readonly IFramework framework;
    private readonly RagdollController playerRagdoll;
    private readonly AnimationController animation;
    private readonly BoneTransformService boneService;
    private readonly MovementBlockHook movementBlock;
    private readonly ActiveCameraController activeCamera;
    private readonly VNavmeshIpc vnavmesh;
    private readonly Npcs.NpcSelector npcSelector;
    private readonly Func<IReadOnlyList<RagdollController>> corpseRagdollProvider;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Puts the grab on the body's surface instead of inside it: aims the pull at the hand's grip
    // volume, and curls the digits onto the grabbed bone. Created in the ctor because it has to be
    // subscribed to the render pass to write bones at all.
    private readonly EnemyControlGrabConformSolver grabConform;

    // Bookkeeping for whichever grab pairs (see config.EnemyControlGrabPairs) are currently held. Two
    // independent id spaces per pair: the ragdoll's own constraint slot, and the conform solver's —
    // they're allocated by different owners, so there's no reason to force them to match.
    private readonly struct ActiveGrabPair
    {
        public readonly int RagdollSlotId;
        public readonly int ConformSlotId;
        public readonly string NpcBone;
        public ActiveGrabPair(int ragdollSlotId, int conformSlotId, string npcBone)
        {
            RagdollSlotId = ragdollSlotId;
            ConformSlotId = conformSlotId;
            NpcBone = npcBone;
        }
    }
    private readonly List<ActiveGrabPair> activeGrabPairs = new();

    private int EnemyControlIndex = -1;       // ClientObjectManager index (only when we spawned the object)
    private uint EnemyControlEntityId;
    private uint nextSpawnedEnemyControlEntityId = 0xF4000001;
    private nint controlledAddress;
    private bool ownsObject;              // true = we spawned it (delete on despawn); false = controlling an existing enemy
    private Npcs.SimulatedNpc? controlledNpc; // the killer we took over (so we can release its AI)
    private bool corpseTraversalSupportActive;
    private bool pendingDraw;
    private int framesWaited;
    private const int MaxPendingFrames = 60;

    private float yaw;
    private float posX, posY, posZ;
    private bool prevAttackKeyDown;
    private bool colliderRegistered;
    private bool lastMovementActive;
    private readonly ActorVisualState visualState = new();
    public IFightingModeLaneConstraint? FightingLane { get; set; }

    private const string GrabAttackLoopKey = "normal/aettouch_loop";
    private ushort grabAttackLoopTimeline;
    // Which config.EnemyControlGrabEmoteId the cached timeline above was resolved for; re-resolve
    // whenever the picked animation changes. uint.MaxValue never matches a real (or the 0/default)
    // emote id, so the first ApplyGrabAttackTimeline call always resolves.
    private uint grabAttackResolvedEmoteId = uint.MaxValue;
    private bool grabAttackLoopResolveWarned;
    private bool grabAttackActive;
    private bool grabAttackRestorePending;
    private bool grabAttackTimelineCaptured;
    private ushort grabAttackSavedBaseOverride;
    private float grabAttackRestoreTimer;

    private sealed class SwarmFollowerState
    {
        public required Npcs.SimulatedNpc Npc;
        public float Angle;
        public float RadiusFactor;
        public float WanderPhase;
        public float WanderRate;
        public float FloorRefreshTimer;
        public float FloorY;
        public bool HasFloor;
        public bool ColliderEnsured;
        public int CorpseSlotId = -1;
        public int PreviousCorpseSlotId = -1;
        public float CorpseSlotTimer;
        public Vector2 CorpseSlotOffset;
        public int CorpseSlotEpoch;
        public RagdollController? ColliderRagdoll;
        public bool OnCorpseSupport;
        public bool TraversalGhosted;
        public bool TraversalProxyActive;
    }

    private readonly Dictionary<nint, SwarmFollowerState> swarmFollowers = new();
    private readonly HashSet<nint> desiredSwarmAddresses = new();
    private readonly List<nint> staleSwarmAddresses = new();
    private readonly List<RagdollController.CorpseTraversalSlot> swarmCorpseSlots = new();
    private readonly Dictionary<int, nint> swarmCorpseReservations = new();
    private readonly List<nint> attackSources = new();
    private Vector3 swarmCorpseCenter;
    private float swarmCorpseExclusionRadius;
    private float swarmElapsed;
    private float swarmClimbDelayRemaining;
    private float swarmStompMassHoldRemaining;
    private RagdollController? swarmCorpseRagdoll;
    private float enemyControlAttackCooldownRemaining;
    private const float EnemyControlAttackWindow = 0.75f;
    private const float EnemyControlAttackCooldown = 0.35f;

    public bool IsActive => controlledAddress != nint.Zero;
    public int SwarmFollowerCount => swarmFollowers.Count;
    public int SwarmCorpseOccupantCount => swarmFollowers.Values.Count(f => f.CorpseSlotId >= 0);

    /// <summary>True while we're controlling an existing enemy at this address (suppresses its AI).</summary>
    public bool ControlsNpc(nint address)
        => (controlledNpc != null && controlledNpc.Address == address) || swarmFollowers.ContainsKey(address);

    public EnemyControlController(IKeyState keyState, IGamepadState gamepad, IFramework framework,
        RagdollController playerRagdoll, AnimationController animation, BoneTransformService boneService,
        MovementBlockHook movementBlock, ActiveCameraController activeCamera,
        VNavmeshIpc vnavmesh, Npcs.NpcSelector npcSelector,
        Func<IReadOnlyList<RagdollController>> corpseRagdollProvider,
        Configuration config, IPluginLog log)
    {
        this.keyState = keyState;
        this.gamepad = gamepad;
        this.framework = framework;
        this.playerRagdoll = playerRagdoll;
        this.animation = animation;
        this.boneService = boneService;
        this.movementBlock = movementBlock;
        this.activeCamera = activeCamera;
        this.vnavmesh = vnavmesh;
        this.npcSelector = npcSelector;
        this.corpseRagdollProvider = corpseRagdollProvider;
        this.config = config;
        this.log = log;
        grabConform = new EnemyControlGrabConformSolver(boneService, playerRagdoll, config, log);
        if (config.EnemyControlSwarmEnabled && config.EnemyControlSwarmClimbCorpses)
            swarmClimbDelayRemaining = Math.Clamp(config.EnemyControlSwarmClimbDelay, 0f, 5f);
        framework.Update += OnUpdate;
        boneService.OnRenderFrame += ApplyArmPoseOverrides;
    }

    // Center the camera on the creature's body, not its origin (feet). Try common center
    // bones in order; fall back to the object position raised a little.
    private static readonly string[] CameraCenterBones = { "j_kosi", "n_hara", "j_sebo_a", "j_sebo_b", "j_kao" };

    /// <summary>Orbit center for the active camera while the EnemyControl is alive (a body bone).</summary>
    private Vector3? CameraCenter()
    {
        if (!IsActive) return null;
        foreach (var b in CameraCenterBones)
        {
            var p = boneService.GetBoneWorldPos(controlledAddress, b);
            if (p.HasValue) return p.Value;
        }
        return new Vector3(posX, posY + 0.5f, posZ);
    }

    /// <summary>True when the active camera is orbiting the creature; false = orbiting the player.</summary>
    public bool CameraFollowsControlledTarget => config.EnemyControlCameraFollowsControlledTarget;

    /// <summary>Creature body-center for external framing (Fighting Mode's KO camera):
    /// non-null only while active with camera-follow on.</summary>
    public Vector3? FollowCenter => IsActive && config.EnemyControlCameraFollowsControlledTarget ? CameraCenter() : null;

    /// <summary>Toggle the camera between following the EnemyControl and the player (character cam).
    /// The choice is remembered (config) — spawn/despawn/reset never auto-switch it.
    /// The follow itself is a per-frame coordinator submission (see OnUpdate).</summary>
    public void ToggleCamera()
    {
        if (!IsActive) return;
        config.EnemyControlCameraFollowsControlledTarget = !config.EnemyControlCameraFollowsControlledTarget;
        config.Save();
        if (!config.EnemyControlCameraFollowsControlledTarget)
            cameraCoordinator?.Release(CameraOwner.EnemyControlFollow);
    }

    /// <summary>Camera arbitration seam — set once by the plugin via IDevExperimental.</summary>
    public void SetCameraCoordinator(CameraModeCoordinator coordinator) => cameraCoordinator = coordinator;

    private CameraModeCoordinator? cameraCoordinator;

    // The creature-follow center used the active camera's height/side offsets when it
    // rode that controller's hook directly; keep applying them so the framing is unchanged.
    private Vector3 ApplyActiveCamOffsets(Vector3 pos)
    {
        pos.Y += config.ActiveCameraHeightOffset;
        if (config.ActiveCameraSideOffset != 0)
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr != null && camMgr->Camera != null)
            {
                var a = camMgr->Camera->DirH - MathF.PI / 2f;
                pos.X += -config.ActiveCameraSideOffset * MathF.Sin(a);
                pos.Z += -config.ActiveCameraSideOffset * MathF.Cos(a);
            }
        }
        return pos;
    }

    public void Spawn()
    {
        if (IsActive || pendingDraw) { log.Info("EnemyControlMode: already active — despawn first"); return; }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) { log.Warning("EnemyControlMode: no local player"); return; }

        var mgr = ClientObjectManager.Instance();
        if (mgr == null) { log.Warning("EnemyControlMode: ClientObjectManager null"); return; }

        var hint = Core.ClientActorSlotAllocator.FindFreeAscending(mgr, 100);
        if (hint == uint.MaxValue) { log.Warning("EnemyControlMode: no free shared client actor slot"); return; }
        var createResult = mgr->CreateBattleCharacter(hint);
        if (createResult == 0xFFFFFFFF) { log.Warning("EnemyControlMode: CreateBattleCharacter failed (no slot)"); return; }

        var index = (int)createResult;
        if (index < 0 || index >= Core.ClientActorSlotAllocator.SharedSlotLimit)
        {
            if (index >= 0 && index < Core.ClientActorSlotAllocator.TotalSlotCount)
                mgr->DeleteObjectByIndex((ushort)index, 0);
            log.Warning($"EnemyControlMode: CreateBattleCharacter returned unsafe index {index}");
            return;
        }
        var obj = mgr->GetObjectByIndex((ushort)index);
        if (obj == null)
        {
            mgr->DeleteObjectByIndex((ushort)index, 0);
            log.Warning($"EnemyControlMode: object null at index {index}");
            return;
        }

        var chara = (BattleChara*)obj;
        var character = (Character*)chara;

        obj->ObjectKind = ObjectKind.BattleNpc;
        obj->SubKind = (byte)BattleNpcSubKind.Combatant;
        obj->TargetableStatus = 0;
        obj->RenderFlags = VisibilityFlags.None;

        var spawnPos = player.Position;
        yaw = player.Rotation;
        posX = spawnPos.X; posY = spawnPos.Y; posZ = spawnPos.Z;
        obj->Position = spawnPos;
        obj->Rotation = yaw;

        var nameBytes = Encoding.UTF8.GetBytes("EnemyControl");
        for (int j = 0; j < 64; j++)
            obj->Name[j] = j < nameBytes.Length && j < 63 ? nameBytes[j] : (byte)0;

        character->CharacterSetup.SetupBNpc(config.EnemyControlModelId, config.EnemyControlModelNameId);
        character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);

        obj->ObjectKind = ObjectKind.BattleNpc;
        obj->SubKind = (byte)BattleNpcSubKind.Combatant;
        character->SetMode(CharacterModes.Normal, 0);

        // A valid EntityId lets it be the caster of a fabricated attack ActionEffect
        // (so the swing animation + sound play).
        // Keep standalone EnemyControl actors out of the virtual-enemy (0xF0), companion (0xF1),
        // dismemberment (0xF2), and spectator (0xF3) synthetic identity ranges.
        EnemyControlEntityId = nextSpawnedEnemyControlEntityId++;
        obj->EntityId = EnemyControlEntityId;

        EnemyControlIndex = index;
        controlledAddress = (nint)obj;
        ownsObject = true;
        pendingDraw = true;
        framesWaited = 0;
        BeginControl(spawnPos, yaw);

        log.Info($"EnemyControlMode: spawned model={config.EnemyControlModelId} at index {index} (0x{controlledAddress:X})");
    }

    /// <summary>
    /// Take control of an existing enemy (the one that just defeated the player) instead of
    /// spawning a creature. Same controls; we don't own the object (no delete on release).
    /// </summary>
    public void ControlKiller(Npcs.SimulatedNpc killer)
    {
        if (IsActive) { log.Info("EnemyControlMode: already active"); return; }
        if (killer.BattleChara == null || killer.Address == nint.Zero) { log.Warning("EnemyControlMode: killer invalid"); return; }

        var obj = (GameObject*)killer.Address;
        if (obj->DrawObject == null) { log.Warning("EnemyControlMode: killer not drawn"); return; }
        var character = (Character*)killer.Address;

        controlledNpc = killer;
        killer.IsClientControlled = true; // suppress its AI behaviours
        EnemyControlIndex = -1;
        ownsObject = false;
        controlledAddress = killer.Address;
        EnemyControlEntityId = obj->EntityId;
        pendingDraw = false; // already drawn
        var pos = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
        posX = pos.X; posY = pos.Y; posZ = pos.Z;
        yaw = obj->Rotation;
        character->SetMode(CharacterModes.Normal, 0);
        BeginControl(pos, yaw);

        log.Info($"EnemyControlMode: controlling killer '{killer.Name}' (0x{controlledAddress:X})");
    }

    private void BeginControl(Vector3 pos, float rot)
    {
        posX = pos.X; posY = pos.Y; posZ = pos.Z; yaw = rot;
        corpseTraversalSupportActive = false;
        // Block the game from moving the actor so our writes win.
        movementBlock.AddApproachNpc(controlledAddress);
        // Camera follow is submitted per-frame in OnUpdate per the remembered preference
        // (config.EnemyControlCameraFollowsControlledTarget) — nothing to toggle here.
    }

    private void OnUpdate(IFramework fw)
    {
        try
        {
            if (pendingDraw)
            {
                if (controlledAddress == nint.Zero) { pendingDraw = false; return; }
                var chara = (BattleChara*)controlledAddress;
                framesWaited++;
                if (chara->IsReadyToDraw() || framesWaited >= MaxPendingFrames)
                {
                    chara->EnableDraw();
                    pendingDraw = false;
                    log.Info($"EnemyControlMode: draw enabled after {framesWaited} frames — controls live");
                }
                return;
            }

            if (!IsActive || controlledAddress == nint.Zero) return;

            // The controlled killer is a real map enemy the game can despawn at any time; its
            // address then dangles (still non-zero) and the next SetPosition write crashes the
            // game in native GameObject.SetPosition. Bail out and release cleanly if it's gone.
            if (!EnemyControlObjectAlive())
            {
                log.Info("EnemyControlMode: controlled object no longer live (despawned) — releasing");
                Despawn();
                return;
            }

            // Creature-follow camera: one coordinator request per frame. Priority
            // arbitration replaces the old SetActive/GetOrbitCenterOverride toggling.
            if (config.EnemyControlCameraFollowsControlledTarget)
            {
                var center = CameraCenter();
                if (center.HasValue)
                    cameraCoordinator?.Submit(CameraOwner.EnemyControlFollow, new CameraRequest
                    {
                        OrbitCenter = ApplyActiveCamOffsets(center.Value),
                    });
            }

            // Register the creature as a live collider so it physically pushes the ragdoll when it
            // walks into it (not just on attack). Under soft contact the registration is simply left
            // alone through a grab, which is what gives the body a surface to rest against instead of
            // sinking into the hand — see EnemyControlGrabSoftContact for why removing and re-adding it
            // was the thing injecting explosive impulses, not the collider itself.
            if (ShouldSuppressLiveCollider())
                RemoveLiveColliderIfRegistered();
            else if (!colliderRegistered && playerRagdoll.AddLiveCollider(controlledAddress))
                colliderRegistered = true;

            if (ImGui.GetIO().WantTextInput) return;

            var dt = (float)fw.UpdateDelta.TotalSeconds;
            if (dt <= 0f || dt > 0.25f) dt = 1f / 60f;

            TickMovement(dt);
            TickSwarm(dt);
            enemyControlAttackCooldownRemaining = MathF.Max(0f, enemyControlAttackCooldownRemaining - dt);
            TickAttackKey();
            TickGrabAttack(dt);
        }
        catch (Exception ex)
        {
            log.Error(ex, "EnemyControlMode: error in update");
        }
    }

    private static Vector2 BuildCorpseSlotOffset(
        nint address,
        RagdollController.CorpseTraversalSlot slot,
        int epoch)
    {
        if (slot.WanderRadius <= 0.0001f)
            return Vector2.Zero;
        var angleNoise = StableSlotNoise(address, slot.Id ^ unchecked(epoch * 0x45d9f3b), epoch * 0.37f);
        var radiusNoise = StableSlotNoise(address, slot.Id ^ unchecked(epoch * 0x27d4eb2d), epoch * 0.71f);
        var angle = angleNoise * MathF.Tau;
        var radius = MathF.Sqrt(Math.Clamp(radiusNoise, 0f, 1f)) * slot.WanderRadius;
        return new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
    }

    private void TickMovement(float dt)
    {
        var obj = (GameObject*)controlledAddress;
        var character = (Character*)controlledAddress;
        movementBlock.AddApproachNpc(controlledAddress);

        // ── Horizontal input: keyboard WASD + gamepad left stick (camera-relative) ──
        // Screen axes: +fwd = forward (W reversed earlier), +strafe = right.
        var fwd = 0f; var strafe = 0f;
        if (Down(VirtualKey.W)) fwd += 1f;
        if (Down(VirtualKey.S)) fwd -= 1f;
        if (Down(VirtualKey.D)) strafe += 1f;
        if (Down(VirtualKey.A)) strafe -= 1f;
        // Gamepad left stick via Dalamud's live LeftStick (raw -99..99; the game's axes are
        // sign-inverted: +X = left, +Y = up). Reading our own GamepadInputAddress gave a stale
        // constant, so use Dalamud's accessor. Deadzone kills any rest drift.
        var ls = gamepad.LeftStick;
        var sx = ls.X / 99f; // right positive
        var sy = ls.Y / 99f; // forward/up positive
        const float deadzone = 0.15f;
        if (MathF.Abs(sx) > deadzone) strafe += sx;
        if (MathF.Abs(sy) > deadzone) fwd += sy;

        var dirH = yaw;
        var camMgr = GameCameraManager.Instance();
        if (camMgr != null && camMgr->Camera != null) dirH = camMgr->Camera->DirH;

        // W goes into the screen (forward), so forward = -(sin,cos).
        var forward = new Vector3(-MathF.Sin(dirH), 0f, -MathF.Cos(dirH));
        var right = new Vector3(MathF.Cos(dirH), 0f, -MathF.Sin(dirH));
        var move = forward * fwd + right * strafe;
        if (FightingLane is { IsLaneActive: true })
        {
            var scalar = Vector3.Dot(move, FightingLane.LaneAxis);
            if (MathF.Abs(scalar) < 0.001f && (MathF.Abs(strafe) > 0.001f || MathF.Abs(fwd) > 0.001f))
                scalar = MathF.Abs(strafe) > 0.001f ? strafe : fwd;
            move = FightingLane.LaneAxis * scalar;
        }

        var inputMoving = move.LengthSquared() > 0.0001f;
        var oldPos = new Vector3(posX, posY, posZ);
        if (inputMoving)
        {
            if (move.LengthSquared() > 1f) move = Vector3.Normalize(move);
            posX += move.X * config.EnemyControlMoveSpeed * dt;
            posZ += move.Z * config.EnemyControlMoveSpeed * dt;
        }

        if (config.EnemyControlGroundWalk)
        {
            // Map collision/navmesh cannot see the separate BEPU ragdoll simulation. Resolve the
            // terrain first, then explicitly treat the player's structural ragdoll bodies as a
            // walkable surface. Kinematic NPC collision only pushes the corpse; it can never lift
            // this manually positioned actor by itself.
            var terrainY = SnapToFloor(posX, posY, posZ);
            var visualScale = GetVisualScale(obj);
            var maxClimb = 0.65f * MathF.Max(1f, visualScale);
            var desiredY = terrainY;
            var hasCorpseSupport = config.NpcCollisionActive && config.RagdollNpcCorpseTraversal &&
                playerRagdoll.TryGetNpcTraversalRootHeight(
                    controlledAddress, new Vector3(posX, posY, posZ), terrainY, maxClimb, out desiredY);

            if (hasCorpseSupport)
            {
                // The ragdoll query returns a unilateral support plane: while the same physical
                // foot/body contact remains under the creature it does not feed the corpse's
                // compression/rebound back into root height. The corpse still receives the real
                // kinematic collision and visibly yields; only the controller feedback is removed.
                var fallScale = MathF.Max(1f, visualScale);
                var maxFall = MathF.Max(0.02f * fallScale, 1.5f * fallScale * dt);
                // Initial acquisition must clear the corpse immediately. A genuine handoff to a
                // higher volume rises at a bounded rate; downward travel is reserved for walk-off.
                var maxRise = MathF.Max(0.03f * fallScale, 4.5f * fallScale * dt);
                posY = desiredY > posY
                    ? corpseTraversalSupportActive ? MathF.Min(desiredY, posY + maxRise) : desiredY
                    : MathF.Max(desiredY, posY - maxFall);
                corpseTraversalSupportActive = true;
            }
            else if (corpseTraversalSupportActive && posY > terrainY + 0.001f)
            {
                var maxFall = MathF.Max(0.04f, 4.5f * dt);
                posY = MathF.Max(terrainY, posY - maxFall);
                corpseTraversalSupportActive = posY > terrainY + 0.001f;
            }
            else
            {
                posY = terrainY;
                corpseTraversalSupportActive = false;
            }
        }
        else
        {
            corpseTraversalSupportActive = false;
            // ── Vertical: keyboard Q/E + gamepad D-pad up/down. Any height, but not below ground. ──
            var up = 0f;
            if (Down(VirtualKey.Q)) up += 1f;
            if (Down(VirtualKey.E)) up -= 1f;
            if (gamepad.Raw(GamepadButtons.R2) != 0) up += 1f; // R2 ascend
            if (gamepad.Raw(GamepadButtons.L2) != 0) up -= 1f; // L2 descend
            posY += up * config.EnemyControlVerticalSpeed * dt;
            // No ground clamp — the creature may sink below the floor (unlimited), by request.
        }

        if (FightingLane is { IsLaneActive: true })
        {
            var constrained = FightingLane.ConstrainToLane(new Vector3(posX, posY, posZ));
            posX = constrained.X;
            posY = constrained.Y;
            posZ = constrained.Z;
        }

        var actualMove = new Vector3(posX - oldPos.X, 0f, posZ - oldPos.Z);
        var inputIntentMoving = MathF.Abs(strafe) > 0.001f || MathF.Abs(fwd) > 0.001f;
        var moving = inputIntentMoving || actualMove.LengthSquared() > 0.000001f;
        lastMovementActive = moving;
        if (actualMove.LengthSquared() > 0.000001f)
            yaw = MathF.Atan2(actualMove.X, actualMove.Z);
        if (moving)
        {
            character->SetMode(CharacterModes.Normal, 0);
            ActorVisualStateController.ApplyMoving(character, visualState, dt);
        }
        else ActorVisualStateController.ClearMovement(character, visualState);

        // Write through MovementBlockHook so the game's own movement doesn't override us.
        movementBlock.SetApproachPosition(obj, posX, posY, posZ);
        movementBlock.SetApproachRotation(obj, yaw);
    }

    /// <summary>Drive selected living enemies into stable randomized slots around the controlled
    /// EnemyControl. Slots are persistent (no per-frame dice jitter); a small slow wander supplies the
    /// requested organic variation, and Compactness damps both wander and slot error.</summary>
    private void TickSwarm(float dt)
    {
        if (!config.EnemyControlSwarmEnabled)
        {
            ReleaseSwarmFollowers();
            return;
        }

        swarmElapsed += dt;
        desiredSwarmAddresses.Clear();
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            var address = npc.Address;
            if (address == nint.Zero || address == controlledAddress || npc.BattleChara == null ||
                !npc.IsAlive || !IsObjectAddressLive(address))
                continue;

            desiredSwarmAddresses.Add(address);
            if (swarmFollowers.ContainsKey(address))
                continue;

            var go = (GameObject*)address;
            swarmFollowers[address] = new SwarmFollowerState
            {
                Npc = npc,
                // Golden-angle spacing prevents accidental piles while the small random phase keeps
                // the ring from reading as a military formation.
                Angle = swarmFollowers.Count * 2.39996323f + (Random.Shared.NextSingle() - 0.5f) * 0.7f,
                RadiusFactor = Random.Shared.NextSingle(),
                WanderPhase = Random.Shared.NextSingle() * MathF.Tau,
                WanderRate = 0.35f + Random.Shared.NextSingle() * 0.45f,
                FloorRefreshTimer = Random.Shared.NextSingle() * 0.2f,
                FloorY = go->Position.Y,
                HasFloor = false,
            };
            log.Info($"EnemyControlMode: swarm follower joined '{npc.Name}' (0x{address:X}).");
        }

        staleSwarmAddresses.Clear();
        foreach (var (address, _) in swarmFollowers)
            if (!desiredSwarmAddresses.Contains(address) || !IsObjectAddressLive(address))
                staleSwarmAddresses.Add(address);
        foreach (var address in staleSwarmAddresses)
            ReleaseSwarmFollower(address);

        var compactness = Math.Clamp(config.EnemyControlSwarmCompactness, 0f, 1f);
        var followDistance = Math.Clamp(config.EnemyControlSwarmFollowDistance, 0.35f, 12f);
        var leaderPosition = new Vector3(posX, posY, posZ);
        SetSwarmCorpseRagdoll(config.EnemyControlSwarmClimbCorpses
            ? FindNearestSwarmCorpse()
            : null);
        var waitingToClimb = config.EnemyControlSwarmClimbCorpses && swarmClimbDelayRemaining > 0f;
        if (waitingToClimb)
        {
            swarmClimbDelayRemaining = MathF.Max(0f, swarmClimbDelayRemaining - dt);
            ClearAllSwarmCorpseSlots();
        }
        var corpseOccupationActive = !waitingToClimb && swarmCorpseRagdoll != null &&
                                     PrepareSwarmCorpseSlots(swarmCorpseRagdoll, dt);
        var enhancedStompTracking = corpseOccupationActive && config.EnemyControlSwarmEnhancedStompTracking;
        var corpseFace = Vector3.Zero;
        var hasCorpseFace = corpseOccupationActive && TryGetCorpseFacePoint(out corpseFace);
        foreach (var (address, follower) in swarmFollowers)
        {
            var go = (GameObject*)address;
            var character = (Character*)address;
            movementBlock.AddApproachNpc(address);

            var collisionRagdoll = swarmCorpseRagdoll ?? playerRagdoll;
            EnsureSwarmFollowerCollider(address, follower, collisionRagdoll);
            SetSwarmFollowerTraversalGhost(address, follower, corpseOccupationActive);
            follower.OnCorpseSupport = false;

            var wanderAmount = (1f - compactness) * 0.28f;
            var angle = yaw + follower.Angle +
                        MathF.Sin(swarmElapsed * follower.WanderRate + follower.WanderPhase) * wanderAmount;
            var radiusVariation = (follower.RadiusFactor - 0.5f) * (1f - compactness) * 0.7f;
            var radius = followDistance * MathF.Max(0.35f, 1f + radiusVariation);
            var target = leaderPosition + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
            var current = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);

            RagdollController.CorpseTraversalSlot? corpseSlot = null;
            if (corpseOccupationActive && TryReserveCorpseSlot(
                    address, follower, current, out var reservedSlot))
            {
                corpseSlot = reservedSlot;
                target.X = reservedSlot.Position.X + follower.CorpseSlotOffset.X;
                target.Z = reservedSlot.Position.Z + follower.CorpseSlotOffset.Y;
            }
            else if (corpseOccupationActive)
            {
                // No free surface lane yet: circulate around the boundary instead of freezing at
                // one formation point. The follower retries reservations every frame and can enter
                // as soon as a lane moves/releases, while the exclusion radius still prevents the
                // old all-at-once corpse shove.
                var orbitDirection = (address.ToInt64() & 1) == 0 ? 1f : -1f;
                var stagingAngle = angle + orbitDirection * swarmElapsed *
                    (0.16f + follower.WanderRate * 0.12f);
                var stagingRadius = swarmCorpseExclusionRadius + 0.10f +
                                    follower.RadiusFactor * 0.22f;
                target.X = swarmCorpseCenter.X + MathF.Sin(stagingAngle) * stagingRadius;
                target.Z = swarmCorpseCenter.Z + MathF.Cos(stagingAngle) * stagingRadius;
            }

            follower.FloorRefreshTimer -= dt;
            if (config.EnemyControlGroundWalk)
            {
                if (!follower.HasFloor || follower.FloorRefreshTimer <= 0f)
                {
                    follower.FloorY = SnapToFloor(target.X, go->Position.Y, target.Z);
                    follower.HasFloor = true;
                    follower.FloorRefreshTimer = enhancedStompTracking && corpseSlot.HasValue
                        ? 0.045f + follower.RadiusFactor * 0.03f
                        : 0.18f + follower.RadiusFactor * 0.14f;
                }
                target.Y = follower.FloorY;
            }
            else
            {
                target.Y = posY + MathF.Sin(follower.WanderPhase) * followDistance * 0.08f;
            }

            if (FightingLane is { IsLaneActive: true })
                target = FightingLane.ConstrainToLane(target);

            var horizontal = new Vector3(target.X - current.X, 0f, target.Z - current.Z);
            var horizontalDistance = horizontal.Length();
            // Normal formation following intentionally has a broad dead zone.  Reusing that dead
            // zone for a moving corpse landmark makes sub-half-metre bone motion invisible and was
            // the main reason occupied followers still looked like rigid ring followers.
            var stopDistance = corpseSlot.HasValue
                ? Math.Clamp(corpseSlot.Value.Clearance * 0.42f, 0.035f, 0.11f)
                : 0.55f + (0.10f - 0.55f) * compactness;
            var next = current;
            var moving = horizontalDistance > stopDistance;
            var followerYaw = yaw;
            var resolvedMoveSpeed = config.EnemyControlMoveSpeed;
            if (moving && horizontalDistance > 1e-5f)
            {
                var catchup = corpseSlot.HasValue
                    ? MathF.Max(1.15f, 0.75f + compactness * 0.75f)
                    : 0.75f + compactness * 0.75f;
                if (horizontalDistance > followDistance * 1.75f)
                    catchup *= 1.65f;
                resolvedMoveSpeed *= catchup;
                var step = MathF.Min(horizontalDistance, resolvedMoveSpeed * dt);
                var direction = horizontal / horizontalDistance;
                next += direction * step;
                followerYaw = MathF.Atan2(direction.X, direction.Z);
                character->SetMode(CharacterModes.Normal, 0);
                ActorVisualStateController.ApplyMoving(character, follower.Npc.VisualState, dt);
            }
            else
            {
                ActorVisualStateController.ClearMovement(character, follower.Npc.VisualState);
            }

            // Corpse occupation is staged as a stomp/stand interaction rather than a formation
            // march. Every assigned climber therefore watches the current ragdoll face while it
            // approaches and after it reaches its body landmark.
            if (corpseSlot.HasValue && hasCorpseFace)
            {
                var faceDirection = new Vector2(corpseFace.X - next.X, corpseFace.Z - next.Z);
                if (faceDirection.LengthSquared() > 0.0001f)
                    followerYaw = MathF.Atan2(faceDirection.X, faceDirection.Y);
            }
            movementBlock.SetApproachRotation(go, followerYaw);

            var dynamicTraversal = false;
            if (corpseSlot.HasValue && swarmCorpseRagdoll != null && config.EnemyControlGroundWalk &&
                config.NpcCollisionActive && config.RagdollNpcCorpseTraversal)
            {
                var visualScale = GetVisualScale(go);
                // Let ordinary navigation cover long distances. Once the follower reaches the
                // corpse neighbourhood, one finite-mass foot carrier becomes authoritative for
                // all three axes; no sampled height is ever written directly to the actor root.
                var proxyEngageDistance = MathF.Max(
                    0.9f, visualScale * 1.15f + corpseSlot.Value.Clearance * 2f);
                dynamicTraversal = follower.TraversalProxyActive ||
                                   horizontalDistance <= proxyEngageDistance;
                if (dynamicTraversal)
                {
                    var maxClimb = 0.65f * MathF.Max(1f, visualScale);
                    swarmCorpseRagdoll.DriveNpcTraversalProxy(
                        address,
                        current,
                        new Vector3(target.X, follower.FloorY, target.Z),
                        follower.FloorY,
                        visualScale,
                        resolvedMoveSpeed,
                        maxClimb);
                    follower.TraversalProxyActive = true;
                    if (swarmCorpseRagdoll.TryGetNpcTraversalProxyRoot(
                            address, out var proxyRoot, out var proxyOnCorpse))
                    {
                        next = proxyRoot;
                        follower.OnCorpseSupport = proxyOnCorpse;
                    }
                }
            }

            if (!dynamicTraversal)
            {
                if (follower.TraversalProxyActive)
                    swarmCorpseRagdoll?.ReleaseNpcTraversalProxy(address);
                follower.TraversalProxyActive = false;
                var desiredRootY = target.Y;
                var verticalSpeed = config.EnemyControlGroundWalk
                    ? 6f
                    : MathF.Max(1f, config.EnemyControlVerticalSpeed);
                next.Y = MoveTowards(current.Y, desiredRootY, verticalSpeed * dt);
            }
            movementBlock.SetApproachPosition(go, next.X, next.Y, next.Z);
        }

        var anyStompSupport = corpseOccupationActive &&
                              swarmFollowers.Values.Any(f => f.OnCorpseSupport);
        swarmStompMassHoldRemaining = anyStompSupport
            ? 0.45f
            : MathF.Max(0f, swarmStompMassHoldRemaining - dt);
        swarmCorpseRagdoll?.SetTraversalMassMultiplier(swarmStompMassHoldRemaining > 0f
            ? Math.Clamp(config.EnemyControlSwarmStompCorpseMassMultiplier, 1f, 12f)
            : 1f);
    }

    private RagdollController? FindNearestSwarmCorpse()
    {
        if (swarmFollowers.Count == 0)
            return null;

        var reference = Vector3.Zero;
        var count = 0;
        foreach (var address in swarmFollowers.Keys)
        {
            if (!IsObjectAddressLive(address))
                continue;
            var go = (GameObject*)address;
            reference += new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
            count++;
        }
        if (count == 0)
            return null;
        reference /= count;

        RagdollController? best = null;
        var bestDistance = float.MaxValue;
        if (swarmCorpseRagdoll is { IsActive: true })
        {
            var retainedDistance = swarmCorpseRagdoll.NearestBodyDistance(reference);
            if (retainedDistance.HasValue)
            {
                best = swarmCorpseRagdoll;
                bestDistance = retainedDistance.Value;
            }
        }

        IReadOnlyList<RagdollController> candidates;
        try { candidates = corpseRagdollProvider(); }
        catch { return null; }
        foreach (var candidate in candidates)
        {
            if (!candidate.IsActive)
                continue;
            var distance = candidate.NearestBodyDistance(reference);
            if (!distance.HasValue || distance.Value >= bestDistance)
                continue;

            // Two nearby moving ragdolls can exchange the exact "nearest" result every frame.
            // Migrating every follower's colliders and clearing all support anchors on each flip is
            // visible as a whole-pack flash. Keep the current corpse until another is meaningfully
            // closer; inactive/despawned targets still switch immediately.
            if (best != null && ReferenceEquals(best, swarmCorpseRagdoll) &&
                !ReferenceEquals(candidate, swarmCorpseRagdoll))
            {
                var switchMargin = MathF.Max(0.35f, bestDistance * 0.12f);
                if (distance.Value + switchMargin >= bestDistance)
                    continue;
            }
            best = candidate;
            bestDistance = distance.Value;
        }
        return best;
    }

    private void SetSwarmCorpseRagdoll(RagdollController? next)
    {
        if (ReferenceEquals(swarmCorpseRagdoll, next))
            return;

        var previous = swarmCorpseRagdoll;
        previous?.SetTraversalMassMultiplier(1f);
        ClearAllSwarmCorpseSlots();
        foreach (var (address, follower) in swarmFollowers)
        {
            previous?.ReleaseNpcTraversalProxy(address);
            follower.TraversalProxyActive = false;
            if (follower.ColliderRagdoll == null || ReferenceEquals(follower.ColliderRagdoll, next))
                continue;
            follower.ColliderRagdoll.SetLiveColliderLightweight(address, false);
            follower.ColliderRagdoll.SetLiveColliderTraversalGhost(address, false);
            follower.ColliderRagdoll.RemoveLiveCollider(address);
            follower.ColliderRagdoll = null;
            follower.ColliderEnsured = false;
            follower.TraversalGhosted = false;
        }

        swarmCorpseRagdoll = next;
        swarmStompMassHoldRemaining = 0f;
        if (next != null)
            log.Info($"EnemyControlMode: swarm selected nearest corpse 0x{next.TargetCharacterAddress:X}.");
    }

    private static void EnsureSwarmFollowerCollider(
        nint address,
        SwarmFollowerState follower,
        RagdollController owner)
    {
        if (!ReferenceEquals(follower.ColliderRagdoll, owner))
        {
            if (follower.ColliderRagdoll != null)
            {
                follower.ColliderRagdoll.ReleaseNpcTraversalProxy(address);
                follower.ColliderRagdoll.SetLiveColliderTraversalGhost(address, false);
                follower.ColliderRagdoll.SetLiveColliderLightweight(address, false);
                follower.ColliderRagdoll.RemoveLiveCollider(address);
            }
            follower.ColliderRagdoll = owner;
            follower.ColliderEnsured = false;
            follower.TraversalGhosted = false;
            follower.TraversalProxyActive = false;
        }

        owner.SetLiveColliderLightweight(address, true);
        if (!follower.ColliderEnsured)
        {
            follower.ColliderEnsured = owner.AddLiveCollider(address);
            if (follower.ColliderEnsured)
                follower.TraversalGhosted = false;
        }
    }

    private static void SetSwarmFollowerTraversalGhost(
        nint address,
        SwarmFollowerState follower,
        bool ghost)
    {
        if (follower.TraversalGhosted == ghost)
            return;
        follower.ColliderRagdoll?.SetLiveColliderTraversalGhost(address, ghost);
        follower.TraversalGhosted = ghost;
    }

    private bool PrepareSwarmCorpseSlots(RagdollController corpseRagdoll, float dt)
    {
        swarmCorpseReservations.Clear();
        swarmCorpseSlots.Clear();
        if (!config.EnemyControlSwarmClimbCorpses || !config.EnemyControlGroundWalk ||
            !config.NpcCollisionActive || !config.RagdollNpcCorpseTraversal ||
            corpseRagdoll.CollectCorpseTraversalSlots(swarmCorpseSlots) == 0)
        {
            ClearAllSwarmCorpseSlots();
            return false;
        }

        swarmCorpseCenter = Vector3.Zero;
        foreach (var slot in swarmCorpseSlots)
            swarmCorpseCenter += slot.Position;
        swarmCorpseCenter /= swarmCorpseSlots.Count;
        swarmCorpseExclusionRadius = 0.25f;
        foreach (var slot in swarmCorpseSlots)
        {
            var radial = new Vector2(slot.Position.X - swarmCorpseCenter.X,
                slot.Position.Z - swarmCorpseCenter.Z).Length();
            swarmCorpseExclusionRadius = MathF.Max(
                swarmCorpseExclusionRadius, radial + MathF.Max(0.12f, slot.Clearance * 1.8f));
        }

        var retainedFaceOccupants = 0;

        foreach (var (address, follower) in swarmFollowers)
        {
            if (config.EnemyControlSwarmRepositionOnCorpse)
                follower.CorpseSlotTimer -= dt;

            var timerExpired = config.EnemyControlSwarmRepositionOnCorpse && follower.CorpseSlotTimer <= 0f;
            if (follower.CorpseSlotId < 0 ||
                !TryFindCorpseSlot(follower.CorpseSlotId, out var slot))
            {
                ClearFollowerCorpseSlot(address, follower);
                continue;
            }

            var release = timerExpired;
            if (!release)
            {
                release = slot.IsFace && retainedFaceOccupants >= 2 ||
                          !swarmCorpseReservations.TryAdd(slot.Id, address);
                if (!release && slot.IsFace)
                    retainedFaceOccupants++;
            }

            if (release)
            {
                ClearFollowerCorpseSlot(address, follower);
            }
        }

        return true;
    }

    private bool TryGetCorpseFacePoint(out Vector3 facePoint)
    {
        facePoint = Vector3.Zero;
        var count = 0;
        foreach (var slot in swarmCorpseSlots)
        {
            if (!slot.IsFace)
                continue;
            facePoint += slot.Position;
            count++;
        }

        if (count == 0)
            return false;
        facePoint /= count;
        return true;
    }

    private bool TryReserveCorpseSlot(
        nint address,
        SwarmFollowerState follower,
        Vector3 current,
        out RagdollController.CorpseTraversalSlot slot)
    {
        if (follower.CorpseSlotId >= 0 && TryFindCorpseSlot(follower.CorpseSlotId, out slot))
            return true;

        var bestScore = float.MaxValue;
        var found = false;
        var best = default(RagdollController.CorpseTraversalSlot);
        foreach (var candidate in swarmCorpseSlots)
        {
            if (swarmCorpseReservations.ContainsKey(candidate.Id))
                continue;
            if (candidate.IsFace && ReservedFaceOccupantCount() >= 2)
                continue;

            var distanceFromFollower = Vector2.Distance(
                new Vector2(current.X, current.Z), new Vector2(candidate.Position.X, candidate.Position.Z));

            var randomBias = StableSlotNoise(address, candidate.Id, follower.WanderPhase) *
                             MathF.Max(0.12f, candidate.Clearance * 1.5f);
            var previousPenalty = candidate.Id == follower.PreviousCorpseSlotId ? 0.55f : 0f;
            // A logarithmic bonus strongly prefers torso/groin landmarks without making hands,
            // feet or the precisely sampled face impossible once the central slots are occupied.
            var priorityBonus = MathF.Log2(MathF.Max(1f, candidate.SelectionWeight)) * 0.32f;
            var score = distanceFromFollower + randomBias + previousPenalty - priorityBonus;
            if (score >= bestScore)
                continue;
            bestScore = score;
            best = candidate;
            found = true;
        }

        if (!found)
        {
            slot = default;
            return false;
        }

        follower.CorpseSlotId = best.Id;
        follower.CorpseSlotEpoch++;
        follower.CorpseSlotOffset = BuildCorpseSlotOffset(address, best, follower.CorpseSlotEpoch);
        var landmarkChangeFrequency = Math.Clamp(config.EnemyControlSwarmLandmarkChangeFrequency, 0.25f, 4f);
        follower.CorpseSlotTimer = config.EnemyControlSwarmRepositionOnCorpse
            ? (5f + StableSlotNoise(address, best.Id ^ 0x5f3759df,
                follower.WanderPhase + follower.CorpseSlotEpoch * 0.73f) * 7f) /
              landmarkChangeFrequency
            : 0f;
        follower.FloorRefreshTimer = 0f;
        swarmCorpseReservations[best.Id] = address;
        slot = best;
        return true;
    }

    private int ReservedFaceOccupantCount()
    {
        var count = 0;
        foreach (var slotId in swarmCorpseReservations.Keys)
            if (TryFindCorpseSlot(slotId, out var slot) && slot.IsFace)
                count++;
        return count;
    }

    private bool TryFindCorpseSlot(int id, out RagdollController.CorpseTraversalSlot slot)
    {
        foreach (var candidate in swarmCorpseSlots)
        {
            if (candidate.Id != id)
                continue;
            slot = candidate;
            return true;
        }
        slot = default;
        return false;
    }

    private void ClearFollowerCorpseSlot(nint address, SwarmFollowerState follower)
    {
        if (follower.CorpseSlotId >= 0)
            follower.PreviousCorpseSlotId = follower.CorpseSlotId;
        follower.CorpseSlotId = -1;
        follower.CorpseSlotTimer = 0f;
        follower.CorpseSlotOffset = Vector2.Zero;
        follower.FloorRefreshTimer = 0f;
        follower.OnCorpseSupport = false;
        follower.ColliderRagdoll?.ClearNpcTraversalSupport(address);
    }

    private void ClearAllSwarmCorpseSlots()
    {
        foreach (var (address, follower) in swarmFollowers)
            ClearFollowerCorpseSlot(address, follower);
        swarmCorpseReservations.Clear();
        swarmCorpseSlots.Clear();
        swarmCorpseExclusionRadius = 0f;
    }

    private static float StableSlotNoise(nint address, int slotId, float phase)
    {
        var seed = unchecked((uint)address.ToInt64()) ^ unchecked((uint)slotId * 0x9e3779b9u);
        seed ^= seed >> 16;
        seed *= 0x7feb352du;
        seed ^= seed >> 15;
        var normalized = (seed & 0x00ffffffu) / 16777215f;
        return (normalized + MathF.Abs(MathF.Sin(phase + slotId * 0.017f))) * 0.5f;
    }

    public void OnSwarmSettingChanged(bool enabled)
    {
        if (!enabled)
        {
            swarmClimbDelayRemaining = 0f;
            ReleaseSwarmFollowers();
        }
        else if (config.EnemyControlSwarmClimbCorpses)
        {
            swarmClimbDelayRemaining = Math.Clamp(config.EnemyControlSwarmClimbDelay, 0f, 5f);
        }
    }

    public void ResetSwarm()
    {
        swarmCorpseRagdoll?.SetTraversalMassMultiplier(1f);
        swarmStompMassHoldRemaining = 0f;
        ClearAllSwarmCorpseSlots();
        swarmElapsed = 0f;
        swarmClimbDelayRemaining = config.EnemyControlSwarmClimbCorpses
            ? Math.Clamp(config.EnemyControlSwarmClimbDelay, 0f, 5f)
            : 0f;
        var followDistance = Math.Clamp(config.EnemyControlSwarmFollowDistance, 0.35f, 12f);
        var formationCenter = new Vector3(posX, posY, posZ);
        var index = 0;
        foreach (var (address, follower) in swarmFollowers)
        {
            follower.ColliderRagdoll?.ReleaseNpcTraversalProxy(address);
            follower.TraversalProxyActive = false;
            SetSwarmFollowerTraversalGhost(address, follower, false);
            follower.Angle = index++ * 2.39996323f +
                             (Random.Shared.NextSingle() - 0.5f) * 0.7f;
            follower.RadiusFactor = Random.Shared.NextSingle();
            follower.WanderPhase = Random.Shared.NextSingle() * MathF.Tau;
            follower.WanderRate = 0.35f + Random.Shared.NextSingle() * 0.45f;
            follower.FloorRefreshTimer = 0f;
            follower.HasFloor = false;
            follower.PreviousCorpseSlotId = -1;
            follower.CorpseSlotEpoch++;
            follower.ColliderRagdoll?.ClearNpcTraversalSupport(address);
            if (!IsObjectAddressLive(address))
                continue;

            // Reset is an explicit recovery operation, so make it immediately observable: move
            // every follower out of stale corpse/contact state and distribute it around the leader
            // in one frame. The normal smooth controller resumes from these fresh slots next tick.
            var go = (GameObject*)address;
            var angle = yaw + follower.Angle;
            var radius = followDistance * (0.78f + follower.RadiusFactor * 0.38f);
            var resetPosition = formationCenter +
                                new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
            if (config.EnemyControlGroundWalk)
                resetPosition.Y = SnapToFloor(resetPosition.X, formationCenter.Y, resetPosition.Z);
            if (FightingLane is { IsLaneActive: true })
                resetPosition = FightingLane.ConstrainToLane(resetPosition);

            follower.FloorY = resetPosition.Y;
            follower.HasFloor = config.EnemyControlGroundWalk;
            movementBlock.AddApproachNpc(address);
            movementBlock.SetApproachPosition(go, resetPosition.X, resetPosition.Y, resetPosition.Z);
            movementBlock.SetApproachRotation(go, yaw);
            ActorVisualStateController.ClearMovement((Character*)address, follower.Npc.VisualState);
        }
        log.Info($"EnemyControlMode: swarm hard reset ({swarmFollowers.Count} follower(s)).");
    }

    public void OnSwarmRepositionSettingChanged(bool enabled)
    {
        if (!enabled)
            return;
        foreach (var follower in swarmFollowers.Values)
            follower.CorpseSlotTimer = 0f;
    }

    public void OnSwarmLandmarkChangeFrequencyChanged(float previous, float current)
    {
        var oldFrequency = Math.Clamp(previous, 0.25f, 4f);
        var newFrequency = Math.Clamp(current, 0.25f, 4f);
        var remainingScale = oldFrequency / newFrequency;
        foreach (var follower in swarmFollowers.Values)
            if (follower.CorpseSlotTimer > 0f)
                follower.CorpseSlotTimer *= remainingScale;
    }

    public void OnSwarmClimbSettingChanged(bool enabled)
    {
        if (!enabled)
        {
            swarmClimbDelayRemaining = 0f;
            SetSwarmCorpseRagdoll(null);
        }
        else
        {
            swarmClimbDelayRemaining = Math.Clamp(config.EnemyControlSwarmClimbDelay, 0f, 5f);
        }
    }

    public void OnAttackModeChanged(bool enabled)
    {
        if (enabled)
            CancelGrabAttack();
        else
        {
            playerRagdoll.CancelAttackStrike();
            if (swarmCorpseRagdoll != null && !ReferenceEquals(swarmCorpseRagdoll, playerRagdoll))
                swarmCorpseRagdoll.CancelAttackStrike();
        }
    }

    private void ReleaseSwarmFollowers()
    {
        if (swarmFollowers.Count == 0)
        {
            SetSwarmCorpseRagdoll(null);
            return;
        }
        staleSwarmAddresses.Clear();
        foreach (var address in swarmFollowers.Keys)
            staleSwarmAddresses.Add(address);
        foreach (var address in staleSwarmAddresses)
            ReleaseSwarmFollower(address);
        desiredSwarmAddresses.Clear();
        SetSwarmCorpseRagdoll(null);
    }

    private void ReleaseSwarmFollower(nint address)
    {
        if (!swarmFollowers.Remove(address, out var follower)) return;
        ClearFollowerCorpseSlot(address, follower);
        if (follower.ColliderRagdoll != null)
        {
            follower.ColliderRagdoll.ReleaseNpcTraversalProxy(address);
            follower.ColliderRagdoll.SetLiveColliderTraversalGhost(address, false);
            follower.ColliderRagdoll.SetLiveColliderLightweight(address, false);
            follower.ColliderRagdoll.RemoveLiveCollider(address);
            follower.ColliderRagdoll = null;
            follower.TraversalGhosted = false;
        }
        movementBlock.RemoveApproachNpc(address);
        if (Core.Services.ObjectTable.LocalPlayer != null && IsObjectAddressLive(address))
            ActorVisualStateController.ClearMovement((Character*)address, follower.Npc.VisualState);
        log.Info($"EnemyControlMode: swarm follower released '{follower.Npc.Name}' (0x{address:X}).");
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        var delta = target - current;
        if (MathF.Abs(delta) <= maxDelta) return target;
        return current + MathF.CopySign(maxDelta, delta);
    }

    private void TickAttackKey()
    {
        // Keyboard attack key (configurable) + gamepad Cross/South button.
        var down = Down((VirtualKey)config.EnemyControlAttackKey) || gamepad.Raw(GamepadButtons.South) != 0;
        if (down && !prevAttackKeyDown) OnAttackInput(); // edge trigger
        prevAttackKeyDown = down;
    }

    /// <summary>Attack input performs a melee swing when Attack mode is enabled; otherwise it
    /// starts/releases the configured grab pairs.</summary>
    public void OnAttackInput()
    {
        if (!IsActive) return;

        if (config.EnemyControlAttackEnabled)
            PerformEnemyControlAttack();
        else
            ToggleGrabAttack();
    }

    private void PerformEnemyControlAttack()
    {
        if (enemyControlAttackCooldownRemaining > 0f || !EnemyControlObjectAlive())
            return;

        if (grabAttackActive || grabAttackRestorePending || activeGrabPairs.Count > 0)
            CancelGrabAttack();

        enemyControlAttackCooldownRemaining = EnemyControlAttackCooldown;
        attackSources.Clear();
        var attackRagdoll = config.EnemyControlSwarmEnabled && swarmCorpseRagdoll != null
            ? swarmCorpseRagdoll
            : playerRagdoll;
        PlayPhysicalAttack(controlledAddress, controlledNpc, attackRagdoll);

        if (config.EnemyControlSwarmEnabled)
        {
            foreach (var follower in swarmFollowers.Values)
                PlayPhysicalAttack(follower.Npc.Address, follower.Npc, attackRagdoll);
        }

        if (attackSources.Count > 0)
        {
            // RagdollController shares one per-body hit budget across this whole group, so many
            // simultaneous light attacks cannot stack into a launch.
            var strength = Math.Clamp(config.EnemyControlAttackStrength, 0f, 0.25f);
            attackRagdoll.BeginAttackStrike(attackSources, EnemyControlAttackWindow, strength);
        }
    }

    private void PlayPhysicalAttack(
        nint address,
        Npcs.SimulatedNpc? knownNpc,
        RagdollController attackRagdoll)
    {
        if (address == nint.Zero || !IsObjectAddressLive(address))
            return;

        attackRagdoll.AddLiveCollider(address);
        var go = (GameObject*)address;
        var corpseCenter = attackRagdoll.GetBodyWorldPosition("j_kosi") ??
                           attackRagdoll.GetBodyWorldPosition("j_sebo_b");
        if (corpseCenter.HasValue)
        {
            var toward = corpseCenter.Value - new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
            toward.Y = 0f;
            if (toward.LengthSquared() > 0.0025f && toward.LengthSquared() < 8f * 8f)
                movementBlock.SetApproachRotation(go, MathF.Atan2(toward.X, toward.Z));
        }
        var npc = knownNpc ?? new Npcs.SimulatedNpc
        {
            BattleChara = (BattleChara*)address,
            SimulatedEntityId = go->EntityId,
            Name = "EnemyControl",
        };
        animation.PlayNpcMeleeAnimationOnly(npc);
        attackSources.Add(address);
    }

    public void CancelGrabAttack()
    {
        foreach (var pair in activeGrabPairs)
            grabConform.End(pair.ConformSlotId);

        if (activeGrabPairs.Count == 0 &&
            !grabAttackActive && !grabAttackRestorePending && !grabAttackTimelineCaptured)
            return;

        foreach (var pair in activeGrabPairs)
        {
            try { playerRagdoll.RemoveGrabConstraint(pair.RagdollSlotId); }
            catch (Exception ex) { log.Warning(ex, "EnemyControlMode: grab cancel failed"); }
        }
        activeGrabPairs.Clear();

        grabAttackActive = false;
        grabAttackRestorePending = false;
        RestoreGrabAttackTimeline();
    }

    private void ToggleGrabAttack()
    {
        if (grabAttackActive)
        {
            ReleaseGrabAttack();
            return;
        }

        StartGrabAttack();
    }

    /// <summary>
    /// Arm every configured pair (see config.EnemyControlGrabPairs). Arbitrarily many can hold at once —
    /// one hand on the neck, the other on the pelvis, is a carry/lift pose rather than a single hold.
    /// A pair that fails to resolve (bad bone name, no body under it) is skipped with a warning
    /// rather than aborting the whole attack; only an empty result set aborts.
    /// </summary>
    private void StartGrabAttack()
    {
        var pairs = config.EnemyControlGrabPairs;
        if (pairs == null || pairs.Count == 0)
        {
            log.Warning("EnemyControlMode: no grab pairs configured.");
            return;
        }

        CaptureGrabAttackTimeline();
        grabAttackRestorePending = false;
        grabAttackRestoreTimer = 0f;

        if (ShouldSuppressLiveCollider())
            RemoveLiveColliderIfRegistered();

        // Handing the creature's address to the grab tells the ragdoll to SUSPEND its collision for the
        // duration — parking its proxies out of the world.
        //
        // Suspending used to be necessary because the grab moved the WHOLE rig onto the hand: the body
        // was forced inside the creature, contact heaved it out, the projection put it back, and the
        // limbs were kneaded between them. A grab now holds only the bone it caught and leaves the body
        // to the joints, so contact is free to do its job again — the corpse hangs against the creature
        // and rests on it, instead of hanging through it. Shared across every pair below: they all
        // belong to the same creature, so there is only ever one address to suspend.
        var suspendAddress = config.EnemyControlGrabSoftContact ? nint.Zero : controlledAddress;

        // Set before any constraint is built: that is what makes the grab land on this frame instead
        // of winching the body up off the ground.
        playerRagdoll.GrabRigid = config.EnemyControlGrabRigid;

        activeGrabPairs.Clear();
        var resolvedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateResolvedPairs = 0;

        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (string.IsNullOrWhiteSpace(pair.NpcBone) || string.IsNullOrWhiteSpace(pair.PlayerBone))
                continue;

            // What this particular creature actually grabs WITH — a hand if it has one, and something
            // it does have if it does not. See ResolveGrabAttackBone.
            var npcBone = ResolveGrabAttackBone(pair.NpcBone);
            var playerBone = pair.PlayerBone.Trim();
            // Several configured hands can legitimately collapse to the same fallback bone on a
            // handless creature. Attaching identical constraints does not improve the hold; it only
            // multiplies solver load (the supplied log created twenty j_kao->j_kubi constraints).
            if (!resolvedPairs.Add($"{npcBone}\u001f{playerBone}"))
            {
                duplicateResolvedPairs++;
                continue;
            }
            var conformSlotId = i;

            // Arm the conform first: it measures the grip volume from the pose the animation is
            // holding right now, and that measurement is what the pull aims at from here on. A
            // creature with no digits under that bone simply gets no conform — see EnemyControlGrabConformSolver.
            grabConform.Begin(conformSlotId, controlledAddress, npcBone, playerBone);

            var target = grabConform.GripAnchor(conformSlotId) ?? ResolveGrabAttackTarget(npcBone);
            if (!target.HasValue)
            {
                log.Warning($"EnemyControlMode: grab pair {i} ('{npcBone}'->'{playerBone}') could not resolve source bone.");
                grabConform.End(conformSlotId);
                continue;
            }

            var ragdollSlotId = playerRagdoll.CreateGrabConstraint(playerBone, target.Value, suspendAddress,
                maxForce: config.EnemyControlGrabServoForce,
                maxSpeed: config.EnemyControlGrabServoSpeed,
                springFreq: config.EnemyControlGrabServoFrequency);
            if (ragdollSlotId == null)
            {
                grabConform.End(conformSlotId);
                log.Warning($"EnemyControlMode: grab pair {i} ('{npcBone}'->'{playerBone}') failed to attach.");
                continue;
            }

            activeGrabPairs.Add(new ActiveGrabPair(ragdollSlotId.Value, conformSlotId, npcBone));
            log.Info($"EnemyControlMode: grab pair {i} started ({npcBone}->{playerBone}).");
        }

        if (duplicateResolvedPairs > 0)
            log.Warning($"EnemyControlMode: ignored {duplicateResolvedPairs} duplicate resolved grab pair(s).");

        if (activeGrabPairs.Count == 0)
        {
            log.Warning("EnemyControlMode: grab attack could not resolve any pair.");
            RestoreGrabAttackTimeline();
            return;
        }

        grabAttackActive = true;
        ApplyGrabAttackTimeline();
    }

    private void ReleaseGrabAttack()
    {
        // Let go of the digits at the same moment as the body — otherwise the hand keeps clutching a
        // throat that is no longer in it.
        foreach (var pair in activeGrabPairs)
        {
            grabConform.End(pair.ConformSlotId);
            try { playerRagdoll.RemoveGrabConstraint(pair.RagdollSlotId); }
            catch (Exception ex) { log.Warning(ex, "EnemyControlMode: grab release failed"); }
        }
        activeGrabPairs.Clear();

        grabAttackActive = false;
        grabAttackRestorePending = true;
        grabAttackRestoreTimer = 1.0f;
    }

    private void TickGrabAttack(float dt)
    {
        if (grabAttackActive)
        {
            // Lets the conform re-measure the grip once the grasp animation has reached the hand.
            grabConform.Tick(dt);

            // Re-apply the tuning each frame so the sliders move a live grab instead of only the next
            // one; UpdateGrabTarget rewrites the constraint from these settings anyway. Shared across
            // every pair, not per-pair — see config.EnemyControlGrabServo*.
            playerRagdoll.GrabRigid = config.EnemyControlGrabRigid;

            foreach (var pair in activeGrabPairs)
            {
                playerRagdoll.SetGrabTuning(pair.RagdollSlotId,
                    config.EnemyControlGrabServoForce, config.EnemyControlGrabServoSpeed, config.EnemyControlGrabServoFrequency);

                var target = grabConform.GripAnchor(pair.ConformSlotId) ?? ResolveGrabAttackTarget(pair.NpcBone);
                if (target.HasValue)
                    playerRagdoll.UpdateGrabTarget(pair.RagdollSlotId, target.Value);
            }

            if (!lastMovementActive)
                ApplyGrabAttackTimeline();
        }

        if (!grabAttackRestorePending)
            return;

        grabAttackRestoreTimer -= dt;
        if (grabAttackRestoreTimer <= 0f)
        {
            grabAttackRestorePending = false;
            RestoreGrabAttackTimeline();
        }
    }

    private bool ShouldSuppressLiveCollider()
        => !config.EnemyControlGrabSoftContact && (grabAttackActive || grabAttackRestorePending);

    private void RemoveLiveColliderIfRegistered()
    {
        if (!colliderRegistered || controlledAddress == nint.Zero)
            return;

        playerRagdoll.RemoveLiveCollider(controlledAddress);
        colliderRegistered = false;
    }

    /// <summary>
    /// Which bone on THIS creature does the grabbing.
    ///
    /// The configured one is a hand, and plenty of things that kill you do not have hands. A bat, a
    /// wisp, a cactuar carry no j_te_* at all, and the grab used to quietly fall back to a point floating
    /// above the creature's origin — nothing was holding anything.
    ///
    /// So when the hand is missing, take the creature at its word and ask its own skeleton. The other
    /// hand first, then anything hand-shaped, then whatever it leads with: a head or a jaw, which is what
    /// a beast grabs with anyway. Humanoids resolve on the first line and behave exactly as before.
    /// </summary>
    private static readonly string[] GrabBoneFallbacks =
    {
        "j_te_l", "j_te_r",   // the other hand
        "j_kao",              // head / face — a beast bites
        "j_kubi",             // neck
        "j_sebo_c",           // chest: the last thing that is definitely there
    };

    private string ResolveGrabAttackBone(string configuredBone)
    {
        var configured = string.IsNullOrWhiteSpace(configuredBone) ? "j_te_r" : configuredBone;
        if (controlledAddress == nint.Zero) return configured;

        var bones = boneService.GetBoneNames(controlledAddress);
        if (bones.Count == 0) return configured;
        if (bones.Contains(configured)) return configured;

        foreach (var fallback in GrabBoneFallbacks)
        {
            if (!bones.Contains(fallback)) continue;
            log.Info($"EnemyControlMode: this creature has no '{configured}' — grabbing with '{fallback}' instead");
            return fallback;
        }

        // Nothing recognisable. Take the bone furthest down the skeleton: on a creature built out of
        // limbs or tentacles that is the end of one, which is the part that would do the reaching.
        var deepest = bones[bones.Count - 1];
        log.Info($"EnemyControlMode: this creature has no '{configured}' and nothing familiar — grabbing with '{deepest}'");
        return deepest;
    }

    /// <summary>Every bone on the live creature, for the grabbing-bone picker. Empty when none is out —
    /// there is no skeleton to read until there is a creature.</summary>
    public IReadOnlyList<string> GetGrabbingBoneCandidates()
        => IsActive ? boneService.GetBoneNames(controlledAddress) : Array.Empty<string>();

    private Vector3? ResolveGrabAttackTarget(string npcBone)
    {
        if (!IsActive) return null;
        var pos = boneService.GetBoneWorldPos(controlledAddress, npcBone);
        if (pos.HasValue) return pos.Value;
        return new Vector3(posX, posY + 0.8f, posZ);
    }

    // Upper-arm / forearm bone pairs the pose sliders drive, left then right — see
    // ApplyArmPoseOverrides. Standard FFXIV skeleton naming (j_ude_a = upper arm, j_ude_b = forearm),
    // already used elsewhere in this file (HoldGrabPlayerBones) and in MainWindow.DevToolbars.cs.
    private static readonly (string Upper, string Lower)[] ArmPoseBones =
    {
        ("j_ude_a_l", "j_ude_b_l"),
        ("j_ude_a_r", "j_ude_b_r"),
    };

    /// <summary>
    /// Manual arm-pose override (see config.EnemyControlArmPoseEnabled and the twelve slider fields):
    /// bends the creature's own upper arm / forearm bones independent of whatever the grab animation
    /// is doing, so the reach can be lined up with wherever a grab pair actually targets. Runs in the
    /// render pass, same as EnemyControlGrabConformSolver, so it isn't immediately overwritten by that frame's
    /// animation re-drive.
    ///
    /// Only applied while a grab is held — the creature walks around on its own animation otherwise.
    /// The release window counts as held: the grab loop keeps playing until RestoreGrabAttackTimeline
    /// fires (see TickGrabAttack), so dropping the pose at release instead would snap the arms back
    /// into the canned grab pose for a second before that pose itself is released — two pops where
    /// there should be one.
    /// </summary>
    private void ApplyArmPoseOverrides()
    {
        if (!IsActive || !config.EnemyControlArmPoseEnabled) return;
        if (!grabAttackActive && !grabAttackRestorePending) return;

        var skelN = boneService.TryGetSkeleton(controlledAddress);
        if (skelN == null) return;
        var skel = skelN.Value;

        var deltas = new Dictionary<int, Quaternion>();
        AddArmPoseDelta(skel, deltas, ArmPoseBones[0].Upper,
            config.EnemyControlLeftArmUpperPitch, config.EnemyControlLeftArmUpperYaw, config.EnemyControlLeftArmUpperRoll);
        AddArmPoseDelta(skel, deltas, ArmPoseBones[0].Lower,
            config.EnemyControlLeftArmLowerPitch, config.EnemyControlLeftArmLowerYaw, config.EnemyControlLeftArmLowerRoll);
        AddArmPoseDelta(skel, deltas, ArmPoseBones[1].Upper,
            config.EnemyControlRightArmUpperPitch, config.EnemyControlRightArmUpperYaw, config.EnemyControlRightArmUpperRoll);
        AddArmPoseDelta(skel, deltas, ArmPoseBones[1].Lower,
            config.EnemyControlRightArmLowerPitch, config.EnemyControlRightArmLowerYaw, config.EnemyControlRightArmLowerRoll);

        if (deltas.Count > 0)
            boneService.ApplyRotationDeltas(skel, deltas);
    }

    private void AddArmPoseDelta(SkeletonAccess skel, Dictionary<int, Quaternion> deltas, string bone,
        float pitchDeg, float yawDeg, float rollDeg)
    {
        if (pitchDeg == 0f && yawDeg == 0f && rollDeg == 0f) return;

        var index = boneService.ResolveBoneIndex(skel, bone);
        if (index < 0) return;

        deltas[index] = Quaternion.CreateFromYawPitchRoll(
            float.DegreesToRadians(yawDeg), float.DegreesToRadians(pitchDeg), float.DegreesToRadians(rollDeg));
    }

    private void CaptureGrabAttackTimeline()
    {
        if (grabAttackTimelineCaptured || controlledAddress == nint.Zero)
            return;

        var character = (Character*)controlledAddress;
        grabAttackSavedBaseOverride = character->Timeline.BaseOverride;
        grabAttackTimelineCaptured = true;
    }

    private void ApplyGrabAttackTimeline()
    {
        if (controlledAddress == nint.Zero)
            return;

        if (grabAttackLoopTimeline == 0 || grabAttackResolvedEmoteId != config.EnemyControlGrabEmoteId)
        {
            grabAttackLoopTimeline = ResolveGrabAnimationTimeline(config.EnemyControlGrabEmoteId);
            grabAttackResolvedEmoteId = config.EnemyControlGrabEmoteId;
            if (grabAttackLoopTimeline == 0)
            {
                if (!grabAttackLoopResolveWarned)
                {
                    grabAttackLoopResolveWarned = true;
                    log.Warning($"EnemyControlMode: could not resolve grab animation (emote {config.EnemyControlGrabEmoteId}).");
                }
                return;
            }
            grabAttackLoopResolveWarned = false;
        }

        var character = (Character*)controlledAddress;
        if (character->Timeline.BaseOverride != grabAttackLoopTimeline)
            character->Timeline.BaseOverride = grabAttackLoopTimeline;
    }

    /// <summary>
    /// 0 (the built-in default) resolves the hardcoded grab-and-hold pose by its own ActionTimeline
    /// key, same as always. Anything else is a real Emote sheet row — see MainWindow's
    /// DrawEnemyControlGrabAnimationPicker for how it's picked, and VictorySequenceGui.ResolveEmoteTimelines
    /// for the matching pattern — and its own loop ActionTimeline drives the hold instead.
    /// </summary>
    private ushort ResolveGrabAnimationTimeline(uint emoteId)
    {
        if (emoteId == 0)
            return animation.ResolveActionTimelineKey(GrabAttackLoopKey);

        try
        {
            var row = Core.Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRow(emoteId);
            if (row.HasValue)
                return (ushort)row.Value.ActionTimeline[0].RowId;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"EnemyControlMode: failed to resolve emote {emoteId} for grab animation");
        }
        return 0;
    }

    private void RestoreGrabAttackTimeline()
    {
        if (!grabAttackTimelineCaptured)
            return;

        if (controlledAddress != nint.Zero)
        {
            var character = (Character*)controlledAddress;
            character->Timeline.BaseOverride = grabAttackSavedBaseOverride;
        }

        grabAttackTimelineCaptured = false;
        grabAttackSavedBaseOverride = 0;
    }

    /// <summary>Floor-clamp the walking creature: raycast down, then fall back to vnavmesh.</summary>
    private float SnapToFloor(float x, float refY, float z)
    {
        const float rayStart = 2f;
        const float rayDist = 50f;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(x, refY + rayStart, z), new Vector3(0, -1f, 0), out var hit, rayDist))
            return hit.Point.Y;

        if (vnavmesh != null)
        {
            vnavmesh.RefreshStatus();
            if (vnavmesh.CanPathfind)
            {
                try
                {
                    var floor = vnavmesh.PointOnFloor(new Vector3(x, refY + 10f, z), false, 5f)
                                ?? vnavmesh.NearestPointReachable(new Vector3(x, refY, z));
                    if (floor.HasValue) return floor.Value.Y;
                }
                catch (Exception ex) { log.Verbose($"EnemyControlMode: floor snap failed ({ex.Message})"); }
            }
        }
        return refY;
    }

    private static float GetVisualScale(GameObject* gameObject)
    {
        if (gameObject == null || gameObject->DrawObject == null)
            return 1f;

        var scale = gameObject->DrawObject->Scale;
        var max = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
        return float.IsFinite(max) && max > 0f ? max : 1f;
    }

    private bool Down(VirtualKey k) => keyState[k];

    // True while controlledAddress still refers to a live game object. The controlled killer is a
    // real enemy the game owns and can free at any moment, leaving a dangling pointer; the object
    // table only enumerates live objects, so a match there means it is safe to dereference. The
    // EntityId check guards against the slot being reused by a different object.
    private bool EnemyControlObjectAlive()
    {
        if (controlledAddress == nint.Zero) return false;
        foreach (var o in Core.Services.ObjectTable)
        {
            if (o.Address != controlledAddress) continue;
            return EnemyControlEntityId == 0 || o.EntityId == EnemyControlEntityId;
        }
        return false;
    }

    private static bool IsObjectAddressLive(nint address)
    {
        if (address == nint.Zero) return false;
        foreach (var o in Core.Services.ObjectTable)
            if (o.Address == address) return true;
        return false;
    }

    public void Despawn()
    {
        if (controlledAddress == nint.Zero) { pendingDraw = false; return; }

        CancelGrabAttack();
        playerRagdoll.CancelAttackStrike();
        ReleaseSwarmFollowers();

        // Stop the ragdoll referencing this address BEFORE the object is deleted.
        if (colliderRegistered)
        {
            playerRagdoll.RemoveLiveCollider(controlledAddress);
            colliderRegistered = false;
        }
        movementBlock.RemoveApproachNpc(controlledAddress);
        cameraCoordinator?.Release(CameraOwner.EnemyControlFollow);

        // Controlled killer: it's a real enemy we don't own — release its AI, never delete it.
        if (controlledNpc != null)
        {
            controlledNpc.IsClientControlled = false;
            controlledNpc = null;
            // Reset our move-anim override only while the object is still live — this path is also
            // reached when the killer already despawned, where its memory is freed and touching it
            // (or on session close) would crash.
            if (Core.Services.ObjectTable.LocalPlayer != null && EnemyControlObjectAlive())
                ActorVisualStateController.ClearMovement((Character*)controlledAddress, visualState);
        }
        // Spawned creature: delete the object. Only touch game memory while the session is alive —
        // during game shutdown the game has already freed it (DisableDraw/Delete would crash).
        else if (ownsObject && Core.Services.ObjectTable.LocalPlayer != null)
        {
            // Tear down the draw object/skeleton BEFORE deleting the slot (matches NpcSpawner order),
            // but only if the object still exists — skip the deref if the game already freed it.
            if (EnemyControlObjectAlive())
                ((GameObject*)controlledAddress)->DisableDraw();
            var mgr = ClientObjectManager.Instance();
            if (mgr != null && EnemyControlIndex >= 0) mgr->DeleteObjectByIndex((ushort)EnemyControlIndex, 0);
        }

        log.Info($"EnemyControlMode: despawned (owned={ownsObject}, idx={EnemyControlIndex})");
        EnemyControlIndex = -1;
        controlledAddress = nint.Zero;
        ownsObject = false;
        pendingDraw = false;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        boneService.OnRenderFrame -= ApplyArmPoseOverrides;
        grabConform.Dispose();
        Despawn();
    }
}
