using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
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
/// Enemy Control: on the player's death it takes over ONE enemy (the killer, or under Warring States the
/// one that carries off the lord's body). The player walks it with the keyboard; the camera may follow
/// it (active-camera-style free orbit centered on it); and the attack input (keyboard or gamepad —
/// routed via the UseAction hook) toggles a grab (see config.EnemyControlGrabPairs).
///
/// Scope: only the possessed enemy. The enemy AI
/// skips whatever <see cref="ControlsNpc"/> names (its behaviour layer is "controlled") and treats it as
/// absent for everyone else's decisions; this class never writes the enemy's own state (its AI state,
/// patrol, identity flags), so on release it picks up where it left off.
///
/// Controls:
///   W / S — forward / back along facing,  A / D — turn,  Q / E — ascend / descend (ground-clamped)
///   Attack action (your real hotkey/gamepad button) — toggle grab
///
/// Position/rotation are driven via MovementBlockHook (so the game's own movement doesn't fight
/// our writes). No AI, not targetable.
/// </summary>
public unsafe partial class EnemyControlController : IDisposable
{
    private readonly IKeyState keyState;
    private readonly IGamepadState gamepad;
    private readonly IFramework framework;
    private readonly RagdollController defaultPlayerRagdoll;
    private RagdollController playerRagdoll { get { var body = defaultPlayerRagdoll; ResolveExternalBody(ref body); return body; } }
    private readonly AnimationController animation;
    private readonly BoneTransformService boneService;
    private readonly MovementBlockHook movementBlock;
    private readonly ActiveCameraController activeCamera;
    private readonly VNavmeshIpc vnavmesh;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Puts the grab on the body's surface instead of inside it: aims the pull at the hand's grip
    // volume, and curls the digits onto the grabbed bone. Created in the ctor because it has to be
    // subscribed to the render pass to write bones at all.
    private readonly EnemyControlGrabConformSolver defaultGrabConform;
    private EnemyControlGrabConformSolver grabConform { get { var solver = defaultGrabConform; ResolveExternalConform(ref solver); return solver; } }

    // Bookkeeping for whichever grab pairs (see config.EnemyControlGrabPairs) are currently held. Two
    // independent id spaces per pair: the ragdoll's own constraint slot, and the conform solver's —
    // they're allocated by different owners, so there's no reason to force them to match.
    // NpcAnchor/PlayerAnchor are the picked points this hold uses (null = the bone, as before anchors; an enemy anchor
    // whose bone this creature lacks is already dropped to null here). PlayerBone/Index are for the log only.
    private readonly struct ActiveGrabPair
    {
        public readonly int RagdollSlotId;
        public readonly int ConformSlotId;
        public readonly string NpcBone;
        public readonly EnemyControlGrabAnchor? NpcAnchor;
        public readonly EnemyControlGrabAnchor? PlayerAnchor;
        public readonly string PlayerBone;
        public readonly int Index;
        public readonly float StartGap;
        public ActiveGrabPair(int ragdollSlotId, int conformSlotId, string npcBone, EnemyControlGrabAnchor? npcAnchor = null,
            EnemyControlGrabAnchor? playerAnchor = null, string playerBone = "", int index = 0, float startGap = 0f)
        {
            RagdollSlotId = ragdollSlotId;
            ConformSlotId = conformSlotId;
            NpcBone = npcBone;
            NpcAnchor = npcAnchor;
            PlayerAnchor = playerAnchor;
            PlayerBone = playerBone;
            Index = index;
            StartGap = startGap;
        }
    }
    private readonly List<ActiveGrabPair> activeGrabPairs = new();
    // GrabAnchor: one follow-up log line this long after an anchored grab starts (how close the points got).
    private const float GrabAnchorReportSeconds = 1f;
    private float grabAnchorReportTimer = -1f;
    private readonly EnemyControlGrabAnchorPicker anchorPicker;

    private uint EnemyControlEntityId;
    private nint controlledAddress;
    private Npcs.SimulatedNpc? controlledNpc; // the enemy taken over (its AI skips it while held)
    private bool corpseTraversalSupportActive;

    private float yaw;
    private float posX, posY, posZ;
    private bool prevAttackKeyDown;
    private bool colliderRegistered;
    private bool lastMovementActive;
    private readonly ActorVisualState visualState = new();
    // Same per-leg solver NpcAiController uses. When it has bones resolved for the controlled
    // creature, it owns bending the affected leg(s) onto a corpse and root height below skips its
    // own corpse query entirely — otherwise both would react to the same contact and the whole
    // body would still lift on top of the leg bend. Null (or unresolved bones, e.g. a non-humanoid
    // creature) falls back to the existing capsule-based root handling unchanged.
    public NpcCorpseFootIkSolver? FootIkSolver { get; set; }
    private bool lastLoggedLegIkHandlesCorpse;

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

    public bool IsActive => controlledAddress != nint.Zero;

    /// <summary>True while we're controlling an existing enemy at this address (suppresses its AI).</summary>
    public bool ControlsNpc(nint address)
        => controlledNpc != null && controlledNpc.Address == address;

    public EnemyControlController(IKeyState keyState, IGamepadState gamepad, IFramework framework,
        RagdollController playerRagdoll, AnimationController animation, BoneTransformService boneService,
        MovementBlockHook movementBlock, ActiveCameraController activeCamera,
        VNavmeshIpc vnavmesh,
        Configuration config, IPluginLog log)
    {
        this.keyState = keyState;
        this.gamepad = gamepad;
        this.framework = framework;
        defaultPlayerRagdoll = playerRagdoll;
        this.animation = animation;
        this.boneService = boneService;
        this.movementBlock = movementBlock;
        this.activeCamera = activeCamera;
        this.vnavmesh = vnavmesh;
        this.config = config;
        this.log = log;
        defaultGrabConform = new EnemyControlGrabConformSolver(boneService, playerRagdoll, config, log);
        anchorPicker = new EnemyControlGrabAnchorPicker(boneService, log);
        framework.Update += OnUpdate;
        boneService.OnPosePrepared += UpdatePreparedGrabTargets;
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

    /// <summary>
    /// Take control of one existing enemy (the one that just defeated the player, or the one carrying
    /// off the lord's body). We don't own the object (no delete on release), and we don't write its
    /// state: the enemy AI leaves it alone while <see cref="ControlsNpc"/> names it.
    /// </summary>
    public void ControlKiller(Npcs.SimulatedNpc killer)
    {
        if (IsActive) { log.Info("EnemyControlMode: already active"); return; }
        if (killer.BattleChara == null || killer.Address == nint.Zero) { log.Warning("EnemyControlMode: killer invalid"); return; }
        // A despawned enemy's address dangles (reading its DrawObject would crash) and may already belong to
        // another object: only an object the table still lists, with this enemy's entity id.
        if (!IsLiveObject(killer.Address, killer.SimulatedEntityId)) { log.Warning($"EnemyControlMode: '{killer.Name}' is no longer in the object table (despawned or reused)"); return; }

        var obj = (GameObject*)killer.Address;
        if (obj->DrawObject == null) { log.Warning("EnemyControlMode: killer not drawn"); return; }
        var character = (Character*)killer.Address;

        controlledNpc = killer;
        controlledAddress = killer.Address;
        EnemyControlEntityId = obj->EntityId;
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

            if ((!ExternalControlActive || ExternalManualControlActive) && ImGui.GetIO().WantTextInput) return;

            var dt = (float)fw.UpdateDelta.TotalSeconds;
            if (dt <= 0f || dt > 0.25f) dt = 1f / 60f;

            TickMovement(dt);
            if (!ExternalControlActive || ExternalManualControlActive) TickAttackKey();
            TickGrabAttack(dt);
        }
        catch (Exception ex)
        {
            log.Error(ex, "EnemyControlMode: error in update");
        }
    }

    private void TickMovement(float dt)
    {
        var obj = (GameObject*)controlledAddress;
        var character = (Character*)controlledAddress;
        movementBlock.AddApproachNpc(controlledAddress);

        // Grab and corpse traversal must never own vertical placement at the same time. The grab
        // target follows a bone on this creature; if foot IK then raises the creature onto the body
        // being pulled, the target rises too, the corpse follows, and the next IK sample raises the
        // creature again (an unbounded staircase/spiral). Keep the grab physically colliding and
        // horizontally movable, but terrain-ground its carrier until the constraint is released.
        var allowCorpseTraversal = !ExternalControlActive && !grabAttackActive &&
                                   config.NpcCollisionActive;
        if (allowCorpseTraversal)
            FootIkSolver?.Track(controlledAddress, dt);

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

        OverrideExternalMovement(ref move, ref fwd, ref strafe);
        var inputMoving = move.LengthSquared() > 0.0001f;
        var oldPos = new Vector3(posX, posY, posZ);
        if (inputMoving)
        {
            if (move.LengthSquared() > 1f) move = Vector3.Normalize(move);
            var speed = config.EnemyControlMoveSpeed;
            OverrideExternalSpeed(dt, ref speed);
            posX += move.X * speed * dt;
            posZ += move.Z * speed * dt;
        }

        if ((ExternalControlActive && !ExternalManualControlActive) || config.EnemyControlGroundWalk)
        {
            // Map collision/navmesh cannot see the separate BEPU ragdoll simulation. Resolve the
            // terrain first, then explicitly treat the player's structural ragdoll bodies as a
            // walkable surface. Kinematic NPC collision only pushes the corpse; it can never lift
            // this manually positioned actor by itself.
            var terrainY = SnapToFloor(posX, posY, posZ);
            var visualScale = GetVisualScale(obj);
            var maxClimb = 0.65f * MathF.Max(1f, visualScale);
            var desiredY = terrainY;
            // A humanoid creature's legs already bend onto the corpse via FootIkSolver — root only
            // takes the shared part of that correction (see GetPelvisOffset) instead of separately
            // deciding whether/how much to rise, so the two don't double-react to the same contact.
            // Non-humanoid creatures (no resolvable leg bones) still rely on the capsule-based query
            // entirely, unchanged.
            var legIkHandlesCorpse = allowCorpseTraversal &&
                                     FootIkSolver?.IsSupported(controlledAddress) == true;
            if (legIkHandlesCorpse != lastLoggedLegIkHandlesCorpse)
            {
                log.Info($"EnemyControlMode: leg-IK corpse handling for 0x{controlledAddress:X} = {legIkHandlesCorpse}");
                lastLoggedLegIkHandlesCorpse = legIkHandlesCorpse;
            }

            bool hasCorpseSupport;
            if (legIkHandlesCorpse)
            {
                var pelvisOffset = FootIkSolver!.GetPelvisOffset(controlledAddress);
                desiredY = terrainY + pelvisOffset;
                hasCorpseSupport = pelvisOffset > 0.0001f;
            }
            else
            {
                hasCorpseSupport = allowCorpseTraversal &&
                    playerRagdoll.TryGetNpcTraversalRootHeight(
                        controlledAddress, new Vector3(posX, posY, posZ), terrainY, maxClimb, out desiredY);
            }

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
            // The feet are measured on the root height decided here; the solver needs that lift to
            // report a stance height that does not shrink by what the root just rose (see ReportRootLift).
            FootIkSolver?.ReportRootLift(controlledAddress, posY - terrainY);
        }
        else
        {
            corpseTraversalSupportActive = false;
            FootIkSolver?.ReportRootLift(controlledAddress, 0f);
            // ── Vertical: keyboard Q/E + gamepad D-pad up/down. Any height, but not below ground. ──
            var up = 0f;
            if (Down(VirtualKey.Q)) up += 1f;
            if (Down(VirtualKey.E)) up -= 1f;
            if (gamepad.Raw(GamepadButtons.R2) != 0) up += 1f; // R2 ascend
            if (gamepad.Raw(GamepadButtons.L2) != 0) up -= 1f; // L2 descend
            posY += up * config.EnemyControlVerticalSpeed * dt;
            // No ground clamp — the creature may sink below the floor (unlimited), by request.
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
        else if (!ExternalControlActive || visualState.Kind == ActorVisualStateKind.Moving)
        {
            ActorVisualStateController.ClearMovement(character, visualState);
            if (ExternalControlActive) visualState.Kind = ActorVisualStateKind.None;
        }

        // Write through MovementBlockHook so the game's own movement doesn't override us.
        movementBlock.SetApproachPosition(obj, posX, posY, posZ);
        movementBlock.SetApproachRotation(obj, yaw);
    }

    private void TickAttackKey()
    {
        // Keyboard attack key (configurable) + gamepad Cross/South button.
        var down = Down((VirtualKey)config.EnemyControlAttackKey) || gamepad.Raw(GamepadButtons.South) != 0;
        if (down && !prevAttackKeyDown) OnAttackInput(); // edge trigger
        prevAttackKeyDown = down;
    }

    /// <summary>Whether the controlled enemy's grab holds the body now.</summary>
    public bool IsHoldingGrab => grabAttackActive;

    /// <summary>Attack input starts/releases the configured grab pairs.</summary>
    public void OnAttackInput()
    {
        if (ExternalControlActive && !ExternalManualControlActive) return;
        if (!IsActive) return;

        // A story that hands over manual control still owns the grab's lifecycle (it ends the grab on
        // its own stage). Toggling here released the body mid-scene while the story carried on as if
        // the hold were intact, so the attack input only re-grabs when the hold was lost.
        if (ExternalManualControlActive)
        {
            if (!grabAttackActive)
                StartGrabAttack();
        }
        else
            ToggleGrabAttack();
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
        ResolveExternalGripPairs(ref pairs);
        if (pairs == null || pairs.Count == 0)
        {
            log.Warning("EnemyControlMode: no grab pairs configured.");
            return;
        }

        CaptureGrabAttackTimeline();
        // Young and elder bodies lack most emote files: swap in the adult male file before the pose
        // is first set (ApplyGrabAttackTimeline may wait while locomotion owns the timeline).
        if (controlledAddress != nint.Zero)
            BodyTypeAnimationFallback.EnsureEmoteFiles((Character*)controlledAddress, ResolveGrabAnimationTimeline(config.EnemyControlGrabEmoteId));
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
        grabAnchorReportTimer = -1f;
        var resolvedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateResolvedPairs = 0;

        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (string.IsNullOrWhiteSpace(pair.NpcBone) || string.IsNullOrWhiteSpace(pair.PlayerBone))
                continue;

            // The picked points, if any (docs/ENEMY_CONTROL_GRAB.md §3). An enemy anchor names a bone of the creature
            // it was picked on; another creature may not have it, and then the grab falls back to the hand chain below
            // and the point is dropped (user decision 2026-11-15).
            var npcAnchor = pair.NpcAnchor;
            if (npcAnchor != null && !CreatureHasBone(npcAnchor.Bone))
            {
                log.Info($"GrabAnchor: pair {i}: this creature has no '{npcAnchor.Bone}' — the enemy point is dropped, grabbing with the fallback bone");
                npcAnchor = null;
            }
            var playerAnchor = pair.PlayerAnchor;
            var bodyAddress = GrabbedBodyAddress();
            if (playerAnchor != null &&
                GrabAnchorSkeleton.IsUnderAny(boneService, bodyAddress, playerAnchor.Bone, config.DismemberPocBones))
            {
                log.Info($"GrabAnchor: pair {i}: '{playerAnchor.Bone}' is on a severed part — pair skipped");
                continue;
            }

            // What this particular creature actually grabs WITH — a hand if it has one, and something
            // it does have if it does not. See ResolveGrabAttackBone.
            var npcBone = npcAnchor?.Bone ?? ResolveGrabAttackBone(pair.NpcBone);
            var playerBone = (playerAnchor?.Bone ?? pair.PlayerBone).Trim();
            // Several configured hands can legitimately collapse to the same fallback bone on a
            // handless creature. Attaching identical constraints does not improve the hold; it only
            // multiplies solver load (the supplied log created twenty j_kao->j_kubi constraints).
            if (!resolvedPairs.Add(EnemyControlGrabPairList.Key(npcBone, playerBone, npcAnchor, playerAnchor)))
            {
                duplicateResolvedPairs++;
                continue;
            }
            var conformSlotId = i;

            // The player's end: with a picked point, the exact point on the body (re-snapped onto its surface as it is
            // now), held by the body that carries the picked bone; without one, the bone itself, as before.
            Vector3? gripPoint = null;
            var surfaceBone = playerBone;
            var snapMm = 0f;
            if (playerAnchor != null)
            {
                if (TryResolvePlayerGripPoint(playerAnchor, bodyAddress, out var point, out surfaceBone, out snapMm))
                    gripPoint = point;
                else
                {
                    log.Info($"GrabAnchor: pair {i}: the body's point on '{playerAnchor.Bone}' could not be read — grabbing the bone itself");
                    playerAnchor = null;
                }
            }

            // Arm the conform first: it measures the grip volume from the pose the animation is
            // holding right now, and that measurement is what the pull aims at from here on. A
            // creature with no digits under that bone simply gets no conform — see EnemyControlGrabConformSolver.
            // An enemy point only curls digits when the picked bone is itself a hand (a point on a forearm must not
            // bend the hand), and the digits conform to the body that carries the picked player bone.
            var conform = npcAnchor == null || grabConform.HasDigitHand(controlledAddress, npcBone);
            if (conform)
                grabConform.Begin(conformSlotId, controlledAddress, npcBone, surfaceBone);

            var target = npcAnchor != null
                ? ResolveEnemyAnchorTarget(npcAnchor)
                : grabConform.GripAnchor(conformSlotId) ?? ResolveGrabAttackTarget(npcBone);
            if (!target.HasValue)
            {
                log.Warning($"EnemyControlMode: grab pair {i} ('{npcBone}'->'{playerBone}') could not resolve source bone.");
                grabConform.End(conformSlotId);
                continue;
            }

            var ragdollSlotId = gripPoint.HasValue
                ? playerRagdoll.CreateGrabConstraintAt(playerBone, gripPoint.Value, target.Value, suspendAddress,
                    maxForce: config.EnemyControlGrabServoForce,
                    maxSpeed: config.EnemyControlGrabServoSpeed,
                    springFreq: config.EnemyControlGrabServoFrequency)
                : playerRagdoll.CreateGrabConstraint(playerBone, target.Value, suspendAddress,
                    maxForce: config.EnemyControlGrabServoForce,
                    maxSpeed: config.EnemyControlGrabServoSpeed,
                    springFreq: config.EnemyControlGrabServoFrequency);
            if (ragdollSlotId == null)
            {
                grabConform.End(conformSlotId);
                log.Warning($"EnemyControlMode: grab pair {i} ('{npcBone}'->'{playerBone}') failed to attach.");
                continue;
            }

            var startGap = 0f;
            if (npcAnchor != null || playerAnchor != null)
            {
                var held = gripPoint ?? boneService.GetBoneWorldPos(bodyAddress, playerBone) ?? target.Value;
                startGap = Vector3.Distance(held, target.Value);
                LogEnemyAnchorScale(i, npcBone);
                log.Info($"GrabAnchor: pair {i} start — enemy {(npcAnchor != null ? $"point on '{npcBone}'" : $"bone '{npcBone}'")}, " +
                         $"body {(playerAnchor != null ? $"point on '{playerBone}' (carried by '{surfaceBone}', re-snapped {snapMm:F1} mm)" : $"bone '{playerBone}'")}, " +
                         $"finger conform {(conform ? "on" : "off")}, start distance {startGap * 1000f:F0} mm");
                grabAnchorReportTimer = 0f;
            }

            activeGrabPairs.Add(new ActiveGrabPair(ragdollSlotId.Value, conformSlotId, npcBone, npcAnchor, playerAnchor,
                playerBone, i, startGap));
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

    private void UpdatePreparedGrabTargets()
    {
        if (!grabAttackActive) return;
        foreach (var pair in activeGrabPairs)
        {
            var target = pair.NpcAnchor != null
                ? ResolveEnemyAnchorTarget(pair.NpcAnchor)
                : grabConform.GripAnchor(pair.ConformSlotId) ?? ResolveGrabAttackTarget(pair.NpcBone);
            if (target.HasValue) playerRagdoll.UpdateGrabTarget(pair.RagdollSlotId, target.Value);
        }
    }

    // ── Anchored grab (docs/ENEMY_CONTROL_GRAB.md §3) ──

    /// <summary>Turns clicks on the models into anchors (the Enemy Control window's Pick buttons).</summary>
    public EnemyControlGrabAnchorPicker AnchorPicker => anchorPicker;

    /// <summary>The controlled creature's address while it is live, for picking and markers; zero otherwise.</summary>
    public nint ControlledActor => IsActive && EnemyControlObjectAlive() ? controlledAddress : nint.Zero;

    /// <summary>Where an anchor is on a live character now (for the window's markers).</summary>
    public bool TryResolveAnchor(nint actor, EnemyControlGrabAnchor anchor, out Vector3 point)
        => GrabAnchorSkeleton.TryResolve(boneService, actor, anchor, out point, out _);

    /// <summary>The body the grab holds: the ragdoll's own character, else the local player.</summary>
    private nint GrabbedBodyAddress()
    {
        var body = playerRagdoll.TargetCharacterAddress;
        return body != nint.Zero ? body : Core.Services.ObjectTable.LocalPlayer?.Address ?? nint.Zero;
    }

    private bool CreatureHasBone(string bone)
    {
        if (controlledAddress == nint.Zero || string.IsNullOrWhiteSpace(bone)) return false;
        foreach (var name in boneService.GetBoneNames(controlledAddress))
            if (name == bone) return true;
        return false;
    }

    /// <summary>The enemy's picked point now, moved out along its normal by the Skin gap.</summary>
    private Vector3? ResolveEnemyAnchorTarget(EnemyControlGrabAnchor anchor)
    {
        if (!IsActive || !GrabAnchorSkeleton.TryResolve(boneService, controlledAddress, anchor, out var point, out var normal))
            return null;
        return point + normal * config.EnemyControlGrabFingerClearance;
    }

    // The re-snap may move the point this far along its normal (times the body's scale); beyond that the surface
    // found is not the patch that was picked, and the stored depth is kept.
    private const float MaxGripSnap = 0.05f;

    /// <summary>
    /// The body's picked point at grab start: the stored point on its bone as posed now, then moved along its normal
    /// onto the body's surface as it is now (gear taken off, another race), so the hand closes on skin rather than on
    /// where the coat was. <paramref name="surfaceBone"/> is the bone whose body carries the point (the surface query
    /// and the finger conform use it).
    /// </summary>
    private bool TryResolvePlayerGripPoint(EnemyControlGrabAnchor anchor, nint body, out Vector3 point,
        out string surfaceBone, out float snapMm)
    {
        surfaceBone = anchor.Bone;
        snapMm = 0f;
        if (!GrabAnchorSkeleton.TryResolve(boneService, body, anchor, out point, out var normal)) return false;
        var ragdoll = playerRagdoll;
        surfaceBone = GrabAnchorSkeleton.CarryingBone(boneService, body, anchor.Bone, ragdoll.IsBoneSimulated);
        if (ragdoll.TryGetCharacterSurface(surfaceBone, point, CharacterSurfaceUsage.Grab, out var surface))
        {
            var scale = GrabAnchorSkeleton.TryScaledBoneOrigin(boneService, body, anchor.Bone, out _, out _, out var s) ? s.X : 1f;
            var raw = point;
            var move = EnemyControlGrabAnchorMath.SnapAlongNormal(ref point, normal, surface.Point, MaxGripSnap * MathF.Max(1f, scale));
            snapMm = move * 1000f;
            if (move == 0f && Vector3.Distance(raw, surface.Point) > 0.001f)
                log.Info($"GrabAnchor: re-snap on '{surfaceBone}' kept the stored depth (surface {Vector3.Dot(surface.Point - raw, normal) * 1000f:F0} mm away along the normal)");
        }
        return true;
    }

    /// <summary>The enemy's draw scale and how far the scaled bone origin (what anchors use, like the rendered mesh)
    /// is from the unscaled reading the bone grab uses — verifies the anchor math on scaled creatures.</summary>
    private void LogEnemyAnchorScale(int index, string npcBone)
    {
        if (GrabAnchorSkeleton.TryScaledBoneOrigin(boneService, controlledAddress, npcBone, out var scaled, out var unscaled, out var scale))
            log.Info($"GrabAnchor: pair {index} enemy scale ({scale.X:F2},{scale.Y:F2},{scale.Z:F2}), " +
                     $"scaled vs unscaled '{npcBone}' origin {Vector3.Distance(scaled, unscaled) * 1000f:F0} mm");
    }

    /// <summary>The GrabAnchor follow-up line: how far each anchored pair's two points still are apart.</summary>
    private void ReportAnchoredGrab()
    {
        var body = GrabbedBodyAddress();
        foreach (var pair in activeGrabPairs)
        {
            if (pair.NpcAnchor == null && pair.PlayerAnchor == null) continue;
            var target = pair.NpcAnchor != null
                ? ResolveEnemyAnchorTarget(pair.NpcAnchor)
                : grabConform.GripAnchor(pair.ConformSlotId) ?? ResolveGrabAttackTarget(pair.NpcBone);
            Vector3? held = null;
            if (pair.PlayerAnchor != null && GrabAnchorSkeleton.TryResolve(boneService, body, pair.PlayerAnchor, out var p, out _))
                held = p;
            held ??= boneService.GetBoneWorldPos(body, pair.PlayerBone);
            if (!target.HasValue || !held.HasValue) continue;
            log.Info($"GrabAnchor: pair {pair.Index} after {GrabAnchorReportSeconds:F1} s — gap {Vector3.Distance(held.Value, target.Value) * 1000f:F0} mm " +
                     $"(start {pair.StartGap * 1000f:F0} mm)");
        }
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

            }

            if (!lastMovementActive)
                ApplyGrabAttackTimeline();

            if (grabAnchorReportTimer >= 0f)
            {
                grabAnchorReportTimer += dt;
                if (grabAnchorReportTimer >= GrabAnchorReportSeconds)
                {
                    grabAnchorReportTimer = -1f;
                    ReportAnchoredGrab();
                }
            }
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
        if (ExternalControlActive && lastMovementActive) return; // Locomotion owns the timeline while carrying.
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
            // Young and elder bodies lack most emote files: swap in the adult male file before the pose plays.
            BodyTypeAnimationFallback.EnsureEmoteFiles((Character*)controlledAddress, grabAttackLoopTimeline);
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

        if (Core.Services.ObjectTable.LocalPlayer != null && EnemyControlObjectAlive())
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

    /// <summary>An object the table still lists at this address, with this entity id (safe to dereference, and the same object).</summary>
    public static bool IsLiveObject(nint address, uint entityId)
    {
        if (address == nint.Zero) return false;
        foreach (var o in Core.Services.ObjectTable)
            if (o.Address == address) return o.EntityId == entityId;
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
        ReleaseExternalControl();
        if (controlledAddress == nint.Zero) return;

        CancelGrabAttack();
        playerRagdoll.CancelAttackStrike();

        // Stop the ragdoll referencing this address BEFORE the object is deleted.
        if (colliderRegistered)
        {
            playerRagdoll.RemoveLiveCollider(controlledAddress);
            colliderRegistered = false;
        }
        movementBlock.RemoveApproachNpc(controlledAddress);
        cameraCoordinator?.Release(CameraOwner.EnemyControlFollow);

        // The enemy is the game's (or the spawner's), never ours to delete: hand it back to its AI.
        // Reset our move-anim override only while the object is still live — this path is also
        // reached when the enemy already despawned, where its memory is freed and touching it
        // (or on session close) would crash.
        var releasedName = controlledNpc?.Name ?? "?";
        controlledNpc = null;
        if (Core.Services.ObjectTable.LocalPlayer != null && EnemyControlObjectAlive())
            ActorVisualStateController.ClearMovement((Character*)controlledAddress, visualState);

        log.Info($"EnemyControlMode: despawned — '{releasedName}' is back to its AI");
        controlledAddress = nint.Zero;
        EnemyControlEntityId = 0;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        boneService.OnPosePrepared -= UpdatePreparedGrabTargets;
        Despawn();
        defaultGrabConform.Dispose();
    }
    partial void ResolveExternalBody(ref RagdollController body);
    partial void ResolveExternalConform(ref EnemyControlGrabConformSolver solver);
    partial void GetExternalControlState(ref bool active);
    partial void GetExternalManualControlState(ref bool active);
    partial void OverrideExternalMovement(ref Vector3 move, ref float forward, ref float strafe);
    partial void OverrideExternalSpeed(float deltaTime, ref float speed);
    partial void ReleaseExternalControl();
    partial void ResolveExternalGripPairs(ref List<EnemyControlGrabPairConfig> pairs);
    // Declared for the Dev/Experimental partial, which still calls it before a story takes over the
    // enemy. There is nothing to release any more (Enemy Control controls one enemy only), so it has no
    // body and the compiler removes the calls.
    partial void ReleaseSwarmFollowers();
    private bool ExternalControlActive
    {
        get { var active = false; GetExternalControlState(ref active); return active; }
    }
    private bool ExternalManualControlActive
    {
        get { var active = false; GetExternalManualControlState(ref active); return active; }
    }
}
