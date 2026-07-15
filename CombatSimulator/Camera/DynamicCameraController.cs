using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using CombatSimulator.Targeting;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatSimulator.Camera;

/// <summary>
/// Dynamic Camera.
///
/// Two personalities behind one <see cref="CameraOwner"/>:
///
///  • Combat — God of War-style over-the-shoulder framing. The character is pushed off
///    to one side (away from the enemy that has the player's attention) and held at a
///    constant on-screen size; a wide enemy arc pulls the camera back so a crowd does
///    not turn into the claustrophobic mess the real thing is criticised for.
///
///  • Death — a "prone photographer" shot. The camera drops to a configurable height
///    above the GROUND, close to the fallen body, at a small up/down angle — as if a
///    photographer were lying next to the corpse — and backs away only as far as it
///    must for the enemy that landed the killing blow to fit head to toe. The fallen-
///    hero composition (body large and low in frame, killer towering beyond it) comes
///    from the depth difference between the two subjects, not from screen-line
///    constraints. The camera translates into the shot over a configurable beat.
///
/// The rule that shapes the whole design: THE PLAYER NEVER LOSES THE CAMERA. We own the
/// orbit centre (and, when framing the death shot, the pitch/zoom/FoV) — but the yaw is
/// always the player's, we never clear their input, and their pitch and zoom nudges are
/// read back as preferences rather than overwritten. The framing re-solves around
/// whatever they do.
/// </summary>
public sealed unsafe class DynamicCameraController : IDisposable
{
    public enum Phase
    {
        Off,
        Combat,
        DeathTranslate,
        DeathHold,
    }

    /// <summary>How much of the killer we still insist on framing. Degrades when they are
    /// too far away to fit alongside the corpse.</summary>
    public enum KillerFit
    {
        FullBody,
        HeadTorso,
        Dropped,
    }

    /// <summary>Corpse anchors, head first. The visibility slider takes a prefix of these,
    /// so 0.25 gets you head+chest, 0.5 roughly half the body, 1.0 head to feet.</summary>
    private static readonly string[][] CorpseChain =
    {
        new[] { "j_kao" },
        new[] { "j_sebo_c" },
        new[] { "j_kosi" },
        new[] { "j_asi_b_l", "j_asi_b_r" },
        new[] { "j_asi_d_l", "j_asi_d_r" },
    };

    /// <summary>Bounds on the death-shot camera angle χ (positive = raised, looking down;
    /// negative = flat/below, looking up at the killer). The up end stops at −0.42: steeper
    /// look-up pushes the camera far and low where framing gets unstable, and it is past the
    /// point of diminishing dramatic return anyway.</summary>
    private const float DeathAngleMin = -0.42f;
    private const float DeathAngleMax = 0.80f;

    // Internal solver bounds for the death shot. These were sliders once; nothing a player
    // wants is expressed through them that the remaining sliders do not cover, and a mis-set
    // lens range proved able to quietly wreck the composition (see Configuration partial).
    /// <summary>Frame-edge clearance as a fraction of half-height. Public: the overlay draws it.</summary>
    public const float DeathSafeMargin = 0.08f;
    private const float DeathFovMin = 0.60f;
    private const float DeathFovMax = 1.20f;
    private const float DeathMaxDistance = 25f;
    private const float DeathZoomMax = 2.5f;

    private readonly Configuration config;
    private readonly CameraModeCoordinator coordinator;
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly PlayerTargetController playerTargetController;
    private readonly BoneTransformService boneService;
    private readonly RagdollController playerRagdoll;
    private readonly IPluginLog log;

    // --- camera collision patch (same Cammy-derived approach the other camera modes use) ---
    private readonly nint collisionPatchAddress;
    private readonly byte[]? collisionOriginalBytes;
    private static readonly byte[] CollisionPatchBytes = { 0x30, 0xC0, 0x90, 0x90, 0x90 };
    private bool collisionPatchActive;

    private Phase phase = Phase.Off;

    // Submitted state, kept across frames so translate and spring-follow are continuous.
    //
    // The death shot tracks the CAMERA POSITION rather than the orbit centre, and derives the
    // pivot from it before submitting. Springing the pivot and the distance separately (as the
    // combat path can afford to) would let the reconstructed camera — pivot − fwd·distance —
    // drift off the solved point whenever the two springs are out of step, and "off the solved
    // point" includes "under the floor". Owning the camera position means the ground clearance
    // is enforced by construction, on every frame, mid-transition included.
    private Vector3 curCam;
    private Vector3 curPivot;
    private float curDistance;
    private float curFov;
    private bool hasCurState;

    // Translate start snapshot.
    private Vector3 startCam;
    private Vector3 startPivot;
    private float startDistance;
    private float startFov;
    private float translateElapsed;
    /// <summary>DirV runs opposite to χ: the struct documents "positive is looking up",
    /// and χ is down-positive. Fixed by documentation, not probed — the probe-and-flip
    /// servo this replaces was itself a source of oscillation.</summary>
    private const float DirVSignForChi = -1f;

    // Death-shot pitch is OPEN LOOP: DirV slews toward the slider's value at a fixed rate
    // and nothing measures-and-corrects it. Every closed-loop variant tried — read-modify-
    // write feedback, absolute-write feedback with reduced gain, composition fitted at the
    // measured pitch — oscillated in testing, and the last one made it worse: the measured
    // pitch carries the game's distance-dependent tilt, so routing it back into the
    // composition (whose solve moves the distance) closed a positive feedback loop. The
    // measured pitch is still USED, but only forward: a heavily smoothed copy tells the fit
    // what the lens actually sees. Nothing downstream of that smoothed value writes DirV.
    private float dirVOwned;
    private bool hasDirVOwned;
    // Slow estimate of the game's tilt offset — the gap between the pitch we COMMAND (−DirV)
    // and the pitch the camera actually renders at (the game adds a distance/target-dependent
    // tilt, ~0.24 rad in testing). The fit needs the real pitch, but reading it back fast
    // closes a loop through the solve that moves the geometry the tilt depends on (the shake).
    // Learned at τ≈2s instead: fast wobble never reaches the fit, slow drift still tracks.
    private float tiltBias;
    private bool hasTiltBias;
    // Standoff fed into the camera-HEIGHT derivation, smoothed and clamped. Raw last-frame
    // standoff was a second feedback path (height→standoff→height) that ran away when a
    // grabbed corpse pushed the standoff out to 7y+.
    private float xRefSmoothed;
    private bool hasXRef;

    // Orbit-placement feedback. The game rebuilds the camera at pivot + orbitDir·Distance,
    // and its orbit direction is NOT the reverse of the view direction — the view carries an
    // aim offset (measured ~0.23 rad at close range), so decomposing the pivot with the view
    // forward landed the actual camera ~0.3y below the solved spot, straight through the
    // ground floor we thought we had enforced. Instead of modelling the offset, measure the
    // orbit direction directly: we know the pivot we submitted and where the camera actually
    // ended up; their difference IS the game's orbit direction for the current angles.
    private Vector3 measuredOrbitDir;
    private bool haveMeasuredOrbitDir;
    private Vector3 lastSubmittedPivot;
    private bool haveLastSubmittedPivot;

    // Death subjects.
    private nint killerAddress;
    private uint killerEntityId;
    private KillerFit killerFit = KillerFit.FullBody;
    private int ladderLevel;
    private float ladderDwell;

    // Player-intent tracking. We write some axes, so "what the player did" is the
    // difference between what the game reports and what we last wrote.
    private float lastWrittenDistance;
    private bool wroteDistance;
    private float prevObservedDistance;

    private float zoomHold;
    private float rotateHold;

    /// <summary>Player's zoom preference in combat, as a multiple of the framing distance.
    /// Learned from their scroll wheel so we adapt to them instead of dragging them back.
    /// Seeded from (and persisted to) config so a new fight resumes at their zoom.</summary>
    private float combatZoomBias = 1f;
    private bool combatZoomDirty;
    /// <summary>Last combat framing distance BEFORE the zoom bias was applied. The bias has to
    /// be measured against this, not against the biased result, or it compounds every scroll.</summary>
    private float combatBaseDistance;
    private float lastShareSetting;

    /// <summary>Player's combat pitch (game DirV), remembered across the death shot the same way
    /// the zoom is. Restored once when combat framing (re)starts; tracked continuously while in
    /// combat. Persisted so it survives sessions.</summary>
    private float combatPitchMemory;
    private bool combatPitchMemoryValid;
    private bool combatPitchDirty;
    /// <summary>Set on entry into the Combat phase so the next TickCombat writes the remembered
    /// pitch once, then hands the axis back to the player.</summary>
    private bool combatPitchRestorePending;
    private Phase prevPhase = Phase.Off;

    // Death-shot preferences. The ANGLE is slider-only by decision: after three attempts at
    // live pitch adjustment (each a differently-shaped feedback loop, each one shaking), the
    // vertical axis is simply locked during the shot — ClearInputV eats the input, the slider
    // is the sole authority. The wheel still drives coverage/zoom-out below.
    /// <summary>Runtime body coverage the wheel drives (the coverage slider seeds it).
    /// Zooming in tightens the shot toward head-and-chest; zooming out first restores full
    /// coverage, then backs the camera off (deathZoomOut) up to DeathZoomMax.</summary>
    private float deathCoveragePref = 1f;
    private float deathZoomOut = 1f;
    private float lastDeathCoverageSetting;
    private float smoothedShoulder;

    // During the death shot the game's zoom is PINNED (MinDistance = MaxDistance = ours):
    // there is no pending-input field for the wheel to intercept the way pitch has
    // InputDeltaV, and testing proved that merely rewriting Distance every frame loses to
    // the game's own wheel handling (it applies input after our write — the wheel stayed
    // fully functional and dragged the camera off the solved pose, corpse anchors out of
    // frame). With the pin, the wheel's intent is read from ImGui's raw wheel instead
    // (forwarded by the plugin's draw handler) and spent on coverage / zoom-out headroom.
    private float savedMinDistance;
    private float savedMaxDistance;
    private bool distanceLimitsOverridden;
    private int distanceLimitSaveDelay;
    private float pendingWheel;

    // Pre-camera-update enforcement (via DeathCamController's update hook, wired by the
    // plugin). Framework-time writes proved insufficient on BOTH locked axes: the game
    // re-applies controller/keyboard pitch and wheel zoom DURING its camera update, i.e.
    // after everything we wrote at framework time — the residual "micro-shake when nudging
    // pitch" was our value and the input's value alternating frame by frame. Forcing the
    // values immediately before the update makes ours the ones it integrates.
    private volatile bool enforceCameraLock;
    private float enforceDirV;
    private float enforcePinDistance;
    /// <summary>Which side the character currently sits on (±1). Held across frames so the
    /// choice is stable and so it can be frozen while the player is orbiting.</summary>
    private float shoulderSide;

    // Ground height under the shot, from BGCollision raycasts, smoothed so a ledge or a
    // stair edge under the camera path does not step the frame. Two probes: under the
    // corpse, and under the CAMERA's own spot — both smoothed and combined by max. The
    // camera probe used to be a binary "refit at the other height when they differ" branch,
    // which flip-flopped every other frame at ledges (the reported "camera jumps up and
    // down by itself at particular spots").
    private float smoothedGroundY;
    private bool hasGroundSample;
    private float smoothedCamGroundY;
    private bool hasCamGroundSample;

    private readonly List<Vector3> requiredPoints = new();

    // --- debug surface (read by DynamicCameraDebugOverlay / the GUI) ---
    public Phase CurrentPhase => phase;
    public IReadOnlyList<Vector3> RequiredPoints => requiredPoints;
    public KillerFit CurrentKillerFit => killerFit;
    public int LadderLevel => ladderLevel;
    public bool LastSolveOk { get; private set; }
    public float SolvedDistance { get; private set; }
    /// <summary>Solver-space χ (positive = looking down), not the game's DirV.</summary>
    public float SolvedChi { get; private set; }
    public float SolvedFov { get; private set; }
    public float SolvedYaw { get; private set; }
    public string StatusText { get; private set; } = "off";
    public float DebugGroundY { get; private set; }
    public float DebugStandoff { get; private set; }
    /// <summary>Raw mouse wheel from the plugin's ImGui draw pass — the only reliable place
    /// to read it, since the camera struct exposes no pending zoom input to intercept.
    /// Consumed by the death shot; ignored (and discarded) everywhere else.</summary>
    public void NotifyMouseWheel(float wheel)
    {
        if (wheel != 0f && phase is Phase.DeathTranslate or Phase.DeathHold)
            pendingWheel += wheel;
    }

    /// <summary>Runs immediately before the game's camera update (hosted by
    /// DeathCamController's hook, wired in the plugin). Forces the death shot's locked
    /// axes so the update integrates OUR values — the last word, after every other
    /// framework-time writer and before the game's own input application.</summary>
    public unsafe void OnPreCameraUpdate(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCamera)
    {
        if (!enforceCameraLock || gameCamera == null)
            return;

        gameCamera->DirV = enforceDirV;
        gameCamera->InputDeltaV = 0f;
        gameCamera->InputDeltaVAdjusted = 0f;

        // (TiltOffset turned out NOT to be the DirV→pitch discrepancy — measured at only
        // −0.08 while the discrepancy was ~0.43. The gap is the game aiming at the character's
        // upper body: at close range that look-at offset adds a large downward pitch. Read it
        // for diagnostics but leave it alone.)
        DebugTiltOffset = gameCamera->TiltOffset;

        gameCamera->MinDistance = enforcePinDistance;
        gameCamera->MaxDistance = enforcePinDistance;
        gameCamera->Distance = enforcePinDistance;
        gameCamera->InterpDistance = enforcePinDistance;
    }

    /// <summary>The game's vertical tilt offset seen just before we zeroed it (diagnostics).</summary>
    public float DebugTiltOffset { get; private set; }
    /// <summary>Runtime wheel-driven coverage / zoom-out (death shot), for the overlay.</summary>
    public float DeathCoverage => deathCoveragePref;
    public float DeathZoomOut => deathZoomOut;
    /// <summary>Wheel input received but not yet consumed — nonzero here across frames means
    /// the ImGui wheel arrives but the death tick never spends it.</summary>
    public float DebugPendingWheel => pendingWheel;
    /// <summary>Whether the zoom pin (MinDistance == MaxDistance == ours) is engaged.</summary>
    public bool DebugZoomPinned => distanceLimitsOverridden;

    /// <summary>Camera write authority, from the coordinator. Only used for the status readout —
    /// arbitration itself is the coordinator's job.</summary>
    public Func<CameraOwner>? GetCurrentOwner { get; set; }

    /// <summary>The dynamic death shot replaces the old Death Cam outright; running both
    /// would mean two interpolators fighting over the same frame.</summary>
    public bool ShouldSuppressDeathCamera =>
        config.EnableDynamicCamera && config.DynCamDeathFraming && !config.EnableActiveCamera;

    public DynamicCameraController(
        Configuration config,
        CameraModeCoordinator coordinator,
        CombatEngine combatEngine,
        NpcSelector npcSelector,
        PlayerTargetController playerTargetController,
        BoneTransformService boneService,
        RagdollController playerRagdoll,
        ISigScanner sigScanner,
        IPluginLog log)
    {
        this.config = config;
        this.coordinator = coordinator;
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.playerTargetController = playerTargetController;
        this.boneService = boneService;
        this.playerRagdoll = playerRagdoll;
        this.log = log;

        lastShareSetting = config.DynCamSubjectScreenShare;
        combatZoomBias = Math.Clamp(config.DynCamCombatZoomMemory, 0.4f, 3.0f);
        combatPitchMemory = config.DynCamCombatPitchMemory;
        combatPitchMemoryValid = config.DynCamCombatPitchMemoryValid;

        try
        {
            collisionPatchAddress = sigScanner.ScanModule("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7 01");
            if (collisionPatchAddress != nint.Zero)
            {
                collisionOriginalBytes = new byte[CollisionPatchBytes.Length];
                Marshal.Copy(collisionPatchAddress, collisionOriginalBytes, 0, collisionOriginalBytes.Length);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "DynamicCam: camera collision signature not found — low death shots may clip terrain.");
            collisionPatchAddress = nint.Zero;
        }
    }

    /// <summary>Called from CombatEngine.BeforePlayerDeath, by which point the killing blow
    /// has long since been recorded (deaths are resolved on a delay).</summary>
    public void HandlePlayerDeath(nint killer, uint killerId)
    {
        if (!config.EnableDynamicCamera || !config.DynCamDeathFraming || config.EnableActiveCamera)
            return;

        killerAddress = killer;
        killerEntityId = killerId;
        killerFit = KillerFit.FullBody;
        ladderLevel = 0;
        ladderDwell = 0f;
        deathZoomOut = 1f;
        pendingWheel = 0f;
        distanceLimitSaveDelay = 0;
        hasGroundSample = false;
        hasCamGroundSample = false;
        haveMeasuredOrbitDir = false;
        haveLastSubmittedPivot = false;
        hasDirVOwned = false;
        hasTiltBias = false;
        hasXRef = false;
        deathCoveragePref = Math.Clamp(config.DynCamDeathBodyVisibility, 0.25f, 1f);
        lastDeathCoverageSetting = config.DynCamDeathBodyVisibility;

        CaptureTranslateStart();
        phase = Phase.DeathTranslate;
        translateElapsed = 0f;

        log.Info($"DynamicCam: death framing armed (killer=0x{killer:X} id={killerId}).");
    }

    private void CaptureTranslateStart()
    {
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return;

            var cam = camMgr->Camera;
            var lookAt = cam->CameraBase.SceneCamera.LookAtVector;
            var scenePos = cam->CameraBase.SceneCamera.Position;

            startPivot = new Vector3(lookAt.X, lookAt.Y, lookAt.Z);
            startCam = new Vector3(scenePos.X, scenePos.Y, scenePos.Z);
            startDistance = cam->Distance;
            startFov = cam->FoV;

            // The starting position is MEASURED where possible.
            if (GameCameraView.TryRead(out var v))
                startCam = v.Position;

            // If combat framing was already running, start from what it was showing rather
            // than from the game's raw look-at (they can differ by the shoulder offset).
            if (hasCurState)
            {
                startPivot = curPivot;
                startDistance = curDistance;
            }

            curCam = startCam;
            curPivot = startPivot;
            curDistance = startDistance;
            curFov = startFov;
            hasCurState = true;
        }
        catch { }
    }

    /// <summary>Drop the camera and undo everything we touched. Hooked to sim reset,
    /// territory change, logout and dispose.</summary>
    public void Reset()
    {
        phase = Phase.Off;
        hasCurState = false;
        curCam = default;
        wroteDistance = false;
        killerAddress = nint.Zero;
        killerEntityId = 0;
        killerFit = KillerFit.FullBody;
        ladderLevel = 0;
        // Combat zoom and pitch survive resets by design: persist what was learned, then reseed
        // from the persisted values — a new fight resumes at the zoom and angle last settled on.
        PersistCombatZoom();
        PersistCombatPitch();
        combatZoomBias = Math.Clamp(config.DynCamCombatZoomMemory, 0.4f, 3.0f);
        combatPitchMemory = config.DynCamCombatPitchMemory;
        combatPitchMemoryValid = config.DynCamCombatPitchMemoryValid;
        combatPitchRestorePending = false;
        prevPhase = Phase.Off;
        deathZoomOut = 1f;
        smoothedShoulder = 0f;
        shoulderSide = 0f;
        hasGroundSample = false;
        hasCamGroundSample = false;
        haveMeasuredOrbitDir = false;
        haveLastSubmittedPivot = false;
        hasDirVOwned = false;
        hasTiltBias = false;
        hasXRef = false;
        RestoreMinDistance();
        requiredPoints.Clear();
        StatusText = "off";
        coordinator.Release(CameraOwner.DynamicCam);
        coordinator.Release(CameraOwner.DynamicDeath);
        DisableCollisionPatch();
    }

    public void Tick(float dt)
    {
        try
        {
            TickCore(dt);
        }
        catch (Exception ex)
        {
            log.Error(ex, "DynamicCam: tick failed — releasing the camera.");
            Reset();
        }
    }

    private void TickCore(float dt)
    {
        if (!config.EnableDynamicCamera)
        {
            if (phase != Phase.Off) Reset();
            StatusText = "disabled";
            return;
        }

        // The user turning Active Camera on is an explicit choice about who drives; we step
        // aside entirely rather than leaking a pivot in through the coordinator's orbit cascade.
        if (config.EnableActiveCamera)
        {
            if (phase != Phase.Off) Reset();
            StatusText = "yielding — Active Camera is on";
            return;
        }

        if (!combatEngine.IsActive)
        {
            if (phase != Phase.Off) Reset();
            StatusText = "idle — simulation not running";
            return;
        }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
        {
            if (phase != Phase.Off) Reset();
            StatusText = "no player";
            return;
        }

        var camMgr = GameCameraManager.Instance();
        if (camMgr == null || camMgr->Camera == null)
            return;
        var gameCam = camMgr->Camera;

        // Everything downstream reasons about the view the renderer is ACTUALLY using —
        // matrices, not orbit angles. No view, no framing.
        if (!GameCameraView.TryRead(out var view))
        {
            StatusText = "no render camera";
            return;
        }

        ReadPlayerIntent(gameCam, dt);

        var alive = combatEngine.State.PlayerState.IsAlive;

        // Revived / reset: fall back to combat framing.
        if (alive && phase is Phase.DeathTranslate or Phase.DeathHold)
        {
            phase = Phase.Combat;
            DisableCollisionPatch();
            RestoreMinDistance();
        }

        if (!alive)
        {
            // Death framing switched off: hand the corpse back to whoever else wants it (the
            // classic Death Cam, which we are no longer suppressing) instead of pointing the
            // combat framing at a body.
            if (!config.DynCamDeathFraming)
            {
                coordinator.Release(CameraOwner.DynamicCam);
                coordinator.Release(CameraOwner.DynamicDeath);
                DisableCollisionPatch();
                RestoreMinDistance();
                phase = Phase.Off;
                hasCurState = false;
                StatusText = "death framing off — Death Cam has it";
                return;
            }

            // Arm death framing if it was not armed through BeforePlayerDeath (e.g. the option
            // was toggled on mid-death), using whatever the engine recorded as the killing blow.
            if (phase is Phase.Combat or Phase.Off)
                HandlePlayerDeath(combatEngine.LastPlayerKillerAddress, combatEngine.LastPlayerKillerEntityId);
        }

        if (alive && phase == Phase.Off)
            phase = Phase.Combat;

        // Combat pitch memory: arm a one-shot restore when we enter combat framing, and save
        // the last-held pitch when we leave it (the death shot, or the mode going Off) — the
        // same lifecycle the combat zoom already has.
        if (phase == Phase.Combat && prevPhase != Phase.Combat)
            combatPitchRestorePending = true;
        else if (phase != Phase.Combat && prevPhase == Phase.Combat)
            PersistCombatPitch();
        prevPhase = phase;

        switch (phase)
        {
            case Phase.Combat:
                if (!config.DynCamCombatFraming)
                {
                    coordinator.Release(CameraOwner.DynamicCam);
                    StatusText = "combat framing off";
                    return;
                }
                TickCombat(player.Address, gameCam, in view, dt);
                break;

            case Phase.DeathTranslate:
            case Phase.DeathHold:
                if (!config.DynCamDeathFraming)
                {
                    coordinator.Release(CameraOwner.DynamicDeath);
                    StatusText = "death framing off";
                    return;
                }
                TickDeath(player.Address, gameCam, in view, dt);
                break;

            default:
                coordinator.Release(CameraOwner.DynamicCam);
                coordinator.Release(CameraOwner.DynamicDeath);
                break;
        }

        var owner = GetCurrentOwner?.Invoke() ?? CameraOwner.None;
        var ourRank = phase is Phase.DeathTranslate or Phase.DeathHold
            ? CameraOwner.DynamicDeath
            : CameraOwner.DynamicCam;
        if (owner > ourRank)
            StatusText = $"yielding — {owner} owns the camera";
    }

    // ------------------------------------------------------------------
    // Player intent
    // ------------------------------------------------------------------

    /// <summary>
    /// Work out what the player just asked for. We write pitch/zoom on some frames, so
    /// their intent is the delta between what the game now reports and what we last wrote —
    /// anything else is the game (and the player) moving the camera, which we respect.
    /// </summary>
    private void ReadPlayerIntent(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam, float dt)
    {
        zoomHold = MathF.Max(0f, zoomHold - dt);
        rotateHold = MathF.Max(0f, rotateHold - dt);

        var hold = MathF.Max(0.1f, config.DynCamInputHold);

        if (MathF.Abs(gameCam->InputDeltaH) > 0.0001f || MathF.Abs(gameCam->InputDeltaHAdjusted) > 0.0001f ||
            MathF.Abs(gameCam->InputDeltaV) > 0.0001f || MathF.Abs(gameCam->InputDeltaVAdjusted) > 0.0001f)
            rotateHold = hold;

        // COMBAT-mode zoom adoption only. Death mode does not read intent back from the
        // camera at all any more — its inputs are intercepted at the source in TickDeath,
        // which is what ended the two-writers fighting (shake, ratcheting zoom).
        var observedDistance = gameCam->Distance;
        var reference = wroteDistance ? lastWrittenDistance : prevObservedDistance;
        var zoomDelta = observedDistance - reference;
        if (phase == Phase.Combat && MathF.Abs(zoomDelta) > 0.02f && prevObservedDistance > 0f)
        {
            zoomHold = hold;

            // Adopt their zoom as a bias on the framing distance, so we never drag them
            // back to ours — the screen-share adaptation just rides on top of it. Measured
            // against the UNBIASED distance, otherwise each scroll multiplies the last.
            var basis = MathF.Max(0.5f, combatBaseDistance);
            combatZoomBias = Math.Clamp(observedDistance / basis, 0.4f, 3.0f);
            combatZoomDirty = true;
        }
        prevObservedDistance = observedDistance;

        // Persist the learned zoom once the gesture settles (one config write per gesture,
        // not per wheel notch).
        if (combatZoomDirty && zoomHold <= 0f)
            PersistCombatZoom();

        // The slider is the user speaking directly — it overrides anything we learned.
        if (MathF.Abs(config.DynCamSubjectScreenShare - lastShareSetting) > 0.0001f)
        {
            lastShareSetting = config.DynCamSubjectScreenShare;
            combatZoomBias = 1f;
            PersistCombatZoom(force: true);
        }

        wroteDistance = false;
    }

    // ------------------------------------------------------------------
    // Combat framing
    // ------------------------------------------------------------------

    private void TickCombat(nint playerAddress, FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam,
        in GameCameraView view, float dt)
    {
        var lens = new DynamicCameraSolver.Lens(view.TanHalfH, view.TanHalfV);
        var fwd = view.Forward;
        DynamicCameraSolver.BasisFromForward(fwd, out var right, out var up);
        DynamicCameraSolver.MeasureAngles(fwd, out var yawReal, out var chiReal);

        // Combat pitch memory. On the first frame of a combat session, restore the pitch the
        // player last held (unless we have never recorded one, in which case we adopt the
        // current pitch as the starting memory). After that the player owns the axis, and we
        // just remember where they leave it — this is what survives the death shot's forced
        // angle so reviving does not dump them at the death camera's tilt.
        if (combatPitchRestorePending)
        {
            combatPitchRestorePending = false;
            if (combatPitchMemoryValid)
            {
                gameCam->DirV = ClampToGamePitch(gameCam, combatPitchMemory);
                gameCam->InputDeltaV = 0f;
                gameCam->InputDeltaVAdjusted = 0f;
            }
            else
            {
                combatPitchMemory = gameCam->DirV;
                combatPitchMemoryValid = true;
            }
        }
        else if (MathF.Abs(gameCam->DirV - combatPitchMemory) > 0.0005f)
        {
            combatPitchMemory = gameCam->DirV;
            combatPitchMemoryValid = true;
            combatPitchDirty = true;
        }

        var anchor = boneService.GetBoneWorldPos(playerAddress, config.DynCamPivotBoneName)
                     ?? ReadObjectPosition(playerAddress) + new Vector3(0f, 1.2f, 0f);
        anchor.Y += config.DynCamHeightOffset;

        // Zooming in raises the aim toward the head — the behaviour the game's own camera has
        // and our wholesale pivot override removed. Without it a close zoom centres the waist
        // and pushes the head off the top of the frame.
        var refDistance = hasCurState ? curDistance : gameCam->Distance;
        var eye = boneService.GetBoneWorldPos(playerAddress, "j_kao");
        if (eye.HasValue)
        {
            var closeness = Math.Clamp((4.5f - refDistance) / (4.5f - 1.6f), 0f, 1f);
            anchor.Y = Lerp(anchor.Y, eye.Value.Y + 0.08f, closeness);
        }

        // Put the character on the side AWAY from whatever has their attention, so the enemy
        // gets the open half of the frame. Offsetting the orbit centre along +right slides the
        // character left on screen, and vice versa. The offset is a SCREEN fraction: the world
        // offset scales with distance, so the composition holds steady across the whole zoom
        // range instead of shoving the character off-frame at close range.
        var focus = FindFocusEnemy(playerAddress);
        var targetShoulder = 0f;
        if (config.DynCamShoulderScreenFrac > 0.001f)
        {
            var side = config.DynCamShoulderSide switch
            {
                1 => 1f,   // character forced left  → pivot shifts +right
                2 => -1f,  // character forced right → pivot shifts -right
                // While the player is orbiting, the enemy sweeps across the frame; re-deciding
                // the side mid-orbit would swing the character past them. Hold what we have.
                _ when rotateHold > 0f && shoulderSide != 0f => shoulderSide,
                _ => AutoShoulderSide(focus, anchor, right),
            };
            shoulderSide = side;

            var offWorld = Math.Clamp(
                config.DynCamShoulderScreenFrac * refDistance * MathF.Abs(lens.TanHalfH),
                0f, 1.5f);
            targetShoulder = side * offWorld;
        }

        // Ease side flips; snapping the frame across the character reads as a cut.
        var flipK = 1f - MathF.Exp(-2.0f * dt);
        smoothedShoulder += (targetShoulder - smoothedShoulder) * flipK;
        var pivot = anchor + right * smoothedShoulder;

        // Hold the body at a constant on-screen size.
        var height = MeasurePlayerHeight(playerAddress);
        var desired = DynamicCameraSolver.DistanceForScreenShare(height, config.DynCamSubjectScreenShare, lens.TanHalfV);
        desired *= CrowdingRelief(view.Position, fwd, right, up, lens);
        combatBaseDistance = Math.Clamp(desired, 1.0f, 30f);

        desired = Math.Clamp(desired * combatZoomBias, 1.0f, 30f);
        SolvedDistance = desired;

        if (!hasCurState)
        {
            curPivot = pivot;
            curDistance = desired;
            hasCurState = true;
        }
        else
        {
            var pk = 1f - MathF.Exp(-MathF.Max(0.1f, config.DynCamPivotSmoothing) * dt);
            curPivot = Vector3.Lerp(curPivot, pivot, pk);

            var dk = 1f - MathF.Exp(-MathF.Max(0.1f, config.DynCamDistanceSmoothing) * dt);
            curDistance += (desired - curDistance) * dk;
        }

        // Never ask for a distance the game will clamp: we recognise the player's zoom by
        // comparing what the game reports against what we last wrote, so a silently clamped
        // write would read back as phantom scrolling and lock us out of our own distance.
        curDistance = ClampToGameDistance(gameCam, curDistance);

        // While the player is working the scroll wheel, stop writing Distance entirely —
        // their input is the one that counts. We resume from wherever they left it.
        float? distanceOut = null;
        if (zoomHold <= 0f)
        {
            distanceOut = curDistance;
            lastWrittenDistance = curDistance;
            wroteDistance = true;
        }
        else
        {
            curDistance = gameCam->Distance;
        }

        LastSolveOk = true;
        SolvedChi = chiReal;
        SolvedFov = view.GameFov;
        SolvedYaw = yawReal;
        StatusText = zoomHold > 0f ? "combat framing (player zooming)" : "combat framing";

        coordinator.Submit(CameraOwner.DynamicCam, new CameraRequest
        {
            OrbitCenter = curPivot,
            Distance = distanceOut,
            MaxDistanceAtLeast = curDistance + 1f,
            // Yaw, pitch and lens all stay the player's in combat.
        });
    }

    private float AutoShoulderSide(SimulatedNpc? focus, Vector3 anchor, Vector3 right)
    {
        if (focus == null || focus.BattleChara == null)
            return smoothedShoulder >= 0f ? 1f : -1f; // hold the last side rather than snapping to centre

        var toEnemy = EnemyPosition(focus) - anchor;
        toEnemy.Y = 0f;
        if (toEnemy.LengthSquared() < 0.01f)
            return smoothedShoulder >= 0f ? 1f : -1f;

        var lateral = Vector3.Dot(right, Vector3.Normalize(toEnemy));

        // Hysteresis: only commit to the other side once the enemy is clearly over there,
        // otherwise an enemy circling through screen centre makes the frame twitch.
        const float flipThreshold = 0.25f;
        if (smoothedShoulder > 0.01f && lateral > -flipThreshold) return 1f;
        if (smoothedShoulder < -0.01f && lateral < flipThreshold) return -1f;
        return lateral >= 0f ? 1f : -1f;
    }

    /// <summary>
    /// Pull back when the engaged enemies span more of an arc than the frame can hold. This
    /// is the one place we deliberately depart from the game we're imitating: its tight
    /// framing is exactly what players complain about once a crowd shows up.
    /// </summary>
    private float CrowdingRelief(Vector3 cam,
        in Vector3 fwd, in Vector3 right, in Vector3 up, in DynamicCameraSolver.Lens lens)
    {
        if (config.DynCamCrowdingRelief <= 0.001f)
            return 1f;

        var maxAbsX = 0f;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null || !npc.IsAlive || !npc.IsEngaged)
                continue;
            var ndc = DynamicCameraSolver.Project(EnemyPosition(npc), cam, fwd, right, up, lens);
            if (ndc.Z <= 0.1f)
                continue;
            maxAbsX = MathF.Max(maxAbsX, MathF.Abs(ndc.X));
        }

        const float comfortable = 0.75f;
        if (maxAbsX <= comfortable)
            return 1f;

        var over = MathF.Min(maxAbsX - comfortable, 0.75f) / 0.75f;
        return 1f + config.DynCamCrowdingRelief * over;
    }

    private SimulatedNpc? FindFocusEnemy(nint playerAddress)
    {
        var locked = playerTargetController.LockedTargetEntityId;
        if (locked != 0)
        {
            var target = npcSelector.GetSelectedNpc(locked);
            if (target is { BattleChara: not null } && target.IsAlive)
                return target;
        }

        var playerPos = ReadObjectPosition(playerAddress);
        SimulatedNpc? best = null;
        var bestDist = float.MaxValue;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null || !npc.IsAlive || !npc.IsEngaged)
                continue;
            var d = Vector3.DistanceSquared(EnemyPosition(npc), playerPos);
            if (d < bestDist)
            {
                bestDist = d;
                best = npc;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------
    // Death framing
    // ------------------------------------------------------------------

    private void TickDeath(nint playerAddress, FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam,
        in GameCameraView view, float dt)
    {
        // Soft yield to anything that still outranks the death shot (fighting cameras, the
        // user's Active Cam): keep declaring presence so we resume the instant they release,
        // but take none of the direct actions — pinning the zoom or consuming inputs for a
        // camera someone else is driving would corrupt THEIR frame.
        var currentOwner = GetCurrentOwner?.Invoke() ?? CameraOwner.None;
        if (currentOwner > CameraOwner.DynamicDeath)
        {
            RestoreMinDistance();
            coordinator.Submit(CameraOwner.DynamicDeath, default);
            StatusText = $"yielding — {currentOwner} owns the camera";
            return;
        }

        var wantCollisionOff = config.DynCamDeathDisableCollision;
        if (wantCollisionOff && !collisionPatchActive) EnableCollisionPatch();
        else if (!wantCollisionOff && collisionPatchActive) DisableCollisionPatch();

        // Save the game's distance band once — but not on the very first death tick. Combat
        // framing raises MaxDistance through the coordinator, and the coordinator gives that
        // raise back during the FIRST Apply after we stop requesting it (this frame). Saving
        // then would capture the raised value and ratchet it back in on restore; one tick
        // later the band is the game's own again.
        if (!distanceLimitsOverridden && ++distanceLimitSaveDelay >= 2)
        {
            savedMinDistance = gameCam->MinDistance;
            savedMaxDistance = gameCam->MaxDistance;
            distanceLimitsOverridden = true;
        }

        // Vertical camera input is fully locked during the death shot (the request below
        // clears it every frame). The angle is the slider's alone — three attempts at live
        // pitch adjustment each shook, and the call was made to lock it rather than keep
        // fighting the game's pitch pipeline.

        // The wheel's intent, read raw (ImGui) because the game's zoom is pinned shut below.
        // One notch ≈ ±1. Zooming in spends zoom-out headroom first, then tightens coverage
        // toward the head; zooming out restores coverage first, then backs the camera off.
        var wheel = pendingWheel;
        pendingWheel = 0f;
        if (MathF.Abs(wheel) > 0.001f)
        {
            if (wheel > 0f)
            {
                // Wheel up = zoom in (FFXIV convention).
                if (deathZoomOut > 1.001f)
                    deathZoomOut = MathF.Max(1f, deathZoomOut / (1f + wheel * 0.12f));
                else
                    deathCoveragePref = MathF.Max(0.25f, deathCoveragePref - wheel * 0.08f);
            }
            else
            {
                var w = -wheel;
                if (deathCoveragePref < 1f)
                    deathCoveragePref = MathF.Min(1f, deathCoveragePref + w * 0.08f);
                else
                    deathZoomOut = MathF.Min(DeathZoomMax, deathZoomOut * (1f + w * 0.12f));
            }
        }

        // The coverage slider is the user speaking directly — reseed the wheel-driven pref.
        if (MathF.Abs(config.DynCamDeathBodyVisibility - lastDeathCoverageSetting) > 0.0001f)
        {
            lastDeathCoverageSetting = config.DynCamDeathBodyVisibility;
            deathCoveragePref = Math.Clamp(config.DynCamDeathBodyVisibility, 0.25f, 1f);
        }

        BuildDeathAnchors(playerAddress, killerFit);
        if (requiredPoints.Count < 2)
        {
            StatusText = "death framing — no anchors";
            return;
        }

        // Where the camera ACTUALLY is and points, measured. Yaw is the player's, always —
        // we take whatever it currently is and solve around it.
        DynamicCameraSolver.MeasureAngles(view.Forward, out var yawReal, out var chiReal);

        // Measure the game's orbit direction: where it put the camera relative to the pivot
        // we handed it last frame. This is the exact mapping the pivot decomposition needs.
        if (haveLastSubmittedPivot)
        {
            var orbit = view.Position - lastSubmittedPivot;
            var len = orbit.Length();
            if (len > 0.25f)
            {
                measuredOrbitDir = orbit / len;
                haveMeasuredOrbitDir = true;
            }
        }

        // χ is the photographer's angle (positive = raised a little, looking down; negative
        // = flat on the ground looking up at the killer). Slider-only, by decision.
        var chiTarget = Math.Clamp(config.DynCamDeathAngle, DeathAngleMin, DeathAngleMax);

        // Slew OUR DirV toward the slider value (open loop; nothing measured writes it). Done
        // up here so the fit can use the pitch we are actually commanding.
        if (!hasDirVOwned)
        {
            dirVOwned = gameCam->DirV;
            hasDirVOwned = true;
        }
        var dirVDesired = ClampToGamePitch(gameCam, DirVSignForChi * chiTarget);
        var slew = 0.8f * dt;
        dirVOwned = ClampToGamePitch(gameCam, dirVOwned + Math.Clamp(dirVDesired - dirVOwned, -slew, slew));

        // The fit composes at the actual measured pitch, tracked through a slow filter so the
        // composition matches what the lens really sees. This IS a feedback loop (measured
        // pitch → fit → geometry → game aim → measured pitch); hands-off it converges, held
        // stable by decoupling the camera height from it (below). But a PLAYER PITCH INPUT
        // kicks it past its margin and it ratchets the camera to the sky and never returns —
        // that was the whole "adjusting pitch triggers looking-at-sky, holding does not"
        // report. So freeze the tracking whenever the player is touching the camera: their
        // input can no longer feed the loop, and dirVOwned/height are open-loop and unaffected.
        // Vertical input is cleared anyway (slider-only angle), so nothing is lost.
        var measuredTilt = chiReal - (-dirVOwned);
        if (!hasTiltBias)
        {
            tiltBias = measuredTilt;
            hasTiltBias = true;
        }
        else if (rotateHold <= 0f)
        {
            tiltBias += (measuredTilt - tiltBias) * (1f - MathF.Exp(-1.0f * dt));
        }
        // Bound it to a physically sane tilt so no path (a leaked input, a bad frame) can walk
        // it out to a sky-pointing fit.
        tiltBias = Math.Clamp(tiltBias, -0.6f, 0.6f);
        var chiForFit = (-dirVOwned) + tiltBias;
        var chiCommanded = -dirVOwned; // open-loop; drives the camera height

        var solved = SolveDeathFraming(playerAddress, yawReal, chiForFit, chiCommanded, in view, dt,
            out var solvedCam, out var distance, out var fov);

        LastSolveOk = solved;
        if (!solved)
        {
            StatusText = "death framing — no solution (holding last)";
            if (!hasCurState)
                return;
        }
        else
        {
            SolvedDistance = distance;
            SolvedChi = chiTarget;
            SolvedFov = fov;
            SolvedYaw = yawReal;
        }

        var targetFov = solved ? fov : curFov;
        var targetDistance = solved ? distance : curDistance;
        var targetCam = solved ? solvedCam : curCam;

        // The wheel's zoom-out headroom pulls the camera back along the ORBIT ray (what the
        // game itself does with distance). Backing the camera off by d·(μ−1) while asking for
        // a distance of d·μ leaves the pivot exactly where it was — anchored on the body — so
        // zooming out never breaks the composition, it only makes it smaller.
        if (solved && deathZoomOut > 1.0001f)
        {
            Vector3 away;
            if (haveMeasuredOrbitDir)
            {
                away = measuredOrbitDir;
            }
            else
            {
                DynamicCameraSolver.BasisFromAngles(yawReal, chiForFit, out var zf, out _, out _);
                away = -zf;
            }
            targetCam += away * (distance * (deathZoomOut - 1f));
            targetDistance = distance * deathZoomOut;
        }

        if (phase == Phase.DeathTranslate)
        {
            translateElapsed += dt;
            var duration = MathF.Max(0.05f, config.DynCamDeathTranslateDuration);
            var t = Math.Clamp(translateElapsed / duration, 0f, 1f);
            var s = t * t * (3f - 2f * t);

            curCam = Vector3.Lerp(startCam, targetCam, s);
            curDistance = Lerp(startDistance, targetDistance, s);
            curFov = Lerp(startFov, targetFov, s);

            if (t >= 1f)
            {
                phase = Phase.DeathHold;
                log.Info("DynamicCam: death translate complete — holding the composition.");
            }
            StatusText = $"death translate {t * 100f:F0}%";
        }
        else
        {
            // The corpse keeps sliding and the killer keeps moving, so the composition is
            // re-solved every frame; springs keep the camera from twitching along with it.
            var pk = 1f - MathF.Exp(-6f * dt);
            var dk = 1f - MathF.Exp(-4f * dt);
            var sk = 1f - MathF.Exp(-2f * dt);

            curCam = Vector3.Lerp(curCam, targetCam, pk);
            curDistance += (targetDistance - curDistance) * dk;
            curFov += (targetFov - curFov) * sk;

            StatusText = killerFit switch
            {
                KillerFit.FullBody => "death framing — corpse + killer (full body)",
                KillerFit.HeadTorso => "death framing — corpse + killer (head/torso)",
                _ => "death framing — corpse only (killer out of reach)",
            };
        }

        // Terrain safety, applied to the value we actually submit — never to the solve, so it
        // cannot feed back into the fit (that self-loop is exactly what we removed above). One
        // fresh probe under the final camera spot; smoothed on its own state so a stray raycast
        // does not jump the camera. This is a hard floor only: it lifts the camera off terrain
        // it would otherwise clip, and never lowers it.
        if (hasGroundSample)
        {
            var camGround = SampleGroundHeight(curCam.X, curCam.Z, curCam.Y + 1f);
            if (camGround.HasValue)
            {
                if (!hasCamGroundSample)
                {
                    smoothedCamGroundY = camGround.Value;
                    hasCamGroundSample = true;
                }
                else
                {
                    var ck = 1f - MathF.Exp(-5f * dt);
                    smoothedCamGroundY += (camGround.Value - smoothedCamGroundY) * ck;
                }
            }
            var safeFloor = hasCamGroundSample ? MathF.Max(smoothedGroundY, smoothedCamGroundY) : smoothedGroundY;
            curCam.Y = MathF.Max(curCam.Y, safeFloor + 0.15f);
        }

        curDistance = MathF.Max(0.3f, curDistance);

        // Pin the game's zoom shut on exactly our distance: with min == max == ours, the
        // wheel handler the game runs AFTER our write has nowhere to move the camera. This
        // is what actually disables the game zoom — rewriting Distance alone demonstrably
        // did not (the game re-applied the wheel on top every frame). Waits for the limit
        // save above so the original band can be given back cleanly.
        if (distanceLimitsOverridden)
        {
            gameCam->MinDistance = curDistance;
            gameCam->MaxDistance = curDistance;
        }

        // dirVOwned was slewed toward the slider up front (open loop; no measurement writes
        // it). This is what we submit and enforce.
        var dirVOut = dirVOwned;

        // Hand the game an orbit centre that reconstructs the camera we want: it rebuilds the
        // camera at pivot + orbitDir·Distance, so pivot = curCam − orbitDir·Distance lands it
        // on curCam. The orbit direction is the MEASURED one — the view forward is not its
        // reverse (the game aims above the pivot), and using it here is what used to sink the
        // real camera ~0.3y below the solved spot, through the ground guarantee.
        Vector3 orbitDir;
        if (haveMeasuredOrbitDir)
        {
            orbitDir = measuredOrbitDir;
        }
        else
        {
            // First frame: approximate with the reversed view direction; the measurement
            // takes over next frame and the springs absorb the correction.
            DynamicCameraSolver.BasisFromAngles(yawReal, chiForFit, out var fwd0, out _, out _);
            orbitDir = -fwd0;
        }
        curPivot = curCam - orbitDir * curDistance;
        lastSubmittedPivot = curPivot;
        haveLastSubmittedPivot = true;

        hasCurState = true;
        lastWrittenDistance = curDistance;
        wroteDistance = true;

        // Arm the pre-camera-update enforcement: these are the values the game's update will
        // be handed at the last moment, immune to everything written between now and then
        // (other controllers' framework writes, and the input the update itself applies).
        // Only once the original distance band is safely saved — enforcing before that would
        // leave nothing to restore.
        enforceDirV = dirVOut;
        enforcePinDistance = curDistance;
        enforceCameraLock = distanceLimitsOverridden;

        coordinator.Submit(CameraOwner.DynamicDeath, new CameraRequest
        {
            OrbitCenter = curPivot,
            DirV = dirVOut,
            Distance = curDistance,
            Fov = curFov,
            // No MaxDistanceAtLeast: the coordinator's raise machinery would undo the zoom
            // pin above. Vertical input is locked (slider-only angle); clearing it keeps
            // the game from applying it to DirV. Yaw stays untouched — the player keeps
            // horizontal control for real.
            ClearInputV = true,
        });
    }

    /// <summary>
    /// The prone-photographer solve. The camera's HEIGHT is pinned to the terrain (ground +
    /// the height slider), its angle is the photographer's χ, and the only fitted variable is
    /// how far it backs away from the corpse — starting at the close-up distance the shot
    /// wants and growing just until the killer fits too. When they will not both fit, a
    /// relaxation ladder escalates: widen the lens, then give up on the killer's legs, then on
    /// the killer entirely. Escalation is immediate (the alternative is a broken shot) but
    /// de-escalation waits out a dwell so the frame does not pump as the killer wanders in and
    /// out of reach.
    ///
    /// Note there is no "borrow some yaw" rung. The first version had one, and it was a lie:
    /// it fitted at a biased yaw but never wrote DirH (the player owns the yaw), so the game
    /// rendered at the unbiased angle and the fit was simply wrong. Backing away shrinks
    /// horizontal spread anyway, so the rung was never needed — the standoff subsumes it.
    /// </summary>
    private bool SolveDeathFraming(nint playerAddress, float yawReal, float chi, float chiForHeight, in GameCameraView view, float dt,
        out Vector3 cam, out float distance, out float fov)
    {
        cam = default;
        distance = 0f;
        fov = curFov;

        var safeY = 1f - DeathSafeMargin;
        var safeX = safeY;
        const float fovMin = DeathFovMin;
        const float fovMax = DeathFovMax;
        const float maxDist = DeathMaxDistance;
        var baseFov = Math.Clamp(startFov, fovMin, fovMax);

        // Maximize mode: no distance floor at all — the fit returns the SMALLEST standoff at
        // which every required point sits in frame, so the body presses right up against the
        // safe margins (a body lying across the view spans the frame edge to edge) and the
        // camera tracks the killer in and out at the constraint boundary. Otherwise the
        // close-up slider is the floor.
        var minStandoff = config.DynCamDeathMaximizeBody
            ? 0.25f
            : Math.Clamp(config.DynCamDeathCloseUpDistance, 0.1f, 6f);

        // The lens each rung will fit with: the measured projection, rescaled for the FoV
        // value the rung plans to write. The rescale is anchored on the measured
        // (game FoV value → real lens) pair, so whatever the FoV field actually means
        // optically is carried through instead of assumed.
        var lensNow = new DynamicCameraSolver.Lens(view.TanHalfH, view.TanHalfV);
        var lensBase = lensNow.ScaledToFov(baseFov, view.GameFov);
        var lensWide = lensNow.ScaledToFov(fovMax, view.GameFov);

        // The shot is anchored on the corpse's chest: standoff is measured from it, and the
        // zoom the player is handed works against it.
        var corpseMain = CorpsePoint(playerAddress, "j_sebo_c")
                         ?? CorpsePoint(playerAddress, "j_kosi")
                         ?? ReadObjectPosition(playerAddress);

        // Terrain-anchored camera height. This is the hard guarantee the first version lacked:
        // whatever the fit does, the camera can no longer end up underground. Two probes,
        // both smoothed: under the corpse (floor for the body) and under where the camera
        // actually ended up last frame (floor for the camera on a slope). Combining by MAX
        // and smoothing replaces the old binary "refit at the other height when they differ"
        // branch that flip-flopped every frame at ledges.
        var ground = SampleGroundHeight(corpseMain.X, corpseMain.Z, corpseMain.Y) ?? (corpseMain.Y - 0.25f);
        if (!hasGroundSample)
        {
            smoothedGroundY = ground;
            hasGroundSample = true;
        }
        else
        {
            var gk = 1f - MathF.Exp(-5f * dt);
            smoothedGroundY += (ground - smoothedGroundY) * gk;
        }

        // The floor for the HEIGHT DERIVATION is the corpse-ground only. It deliberately does
        // NOT probe the ground under the camera: the camera position is what we are solving,
        // so a camera-ground probe feeds curCam → ground → height → fit → curCam — a self-loop
        // that runs away exactly in the angle ranges that push the camera far and low over
        // uneven terrain (the "steep angle drifts to sky, even holding still" case). Corpse
        // position does not depend on curCam, so this floor cannot feed back. Camera-vs-terrain
        // safety is handled by a clamp at submit time, which does not re-enter the fit.
        var floorY = smoothedGroundY;

        // Camera height is DERIVED, not dialled: from where the body should sit on screen.
        // The chest's angle above the view axis must be atan(band·tanHalfV); subtract the
        // view angle χ and the height difference over the standoff follows. This is what
        // produces the fallen-hero look — body low in frame — for ANY angle the player
        // picks, instead of asking them to hand-tune a height that happens to work.
        //
        // The band only makes sense while the body is ON THE GROUND. When a monster grabs and
        // lifts the corpse, "body low in frame" is a request the geometry can only satisfy by
        // retreating (a body at camera height needs distance to fall to the lower frame — the
        // "grab pulls the camera far" report, where the fit correctly went to 7y). Fade the
        // band toward centre as the body leaves the ground, so a lifted body is framed
        // centred-and-close instead of low-and-far.
        var lift = corpseMain.Y - floorY;
        var grounded = Math.Clamp((0.85f - lift) / 0.6f, 0f, 1f); // 1 at lift≤0.25, 0 by lift≥0.85
        var band = Math.Clamp(config.DynCamDeathBodyBand, -0.85f, 0.3f) * grounded;

        // Standoff reference for the height, smoothed and CLAMPED. Raw last-frame standoff fed
        // a second runaway loop (height→standoff→height); capping it at 4y keeps a large solved
        // standoff from dragging the height derivation out with it.
        var xRawRef = Math.Clamp(DebugStandoff > 0.1f ? DebugStandoff : minStandoff, minStandoff, 4f);
        if (!hasXRef)
        {
            xRefSmoothed = xRawRef;
            hasXRef = true;
        }
        else
        {
            xRefSmoothed += (xRawRef - xRefSmoothed) * (1f - MathF.Exp(-3f * dt));
        }
        // Height uses the COMMANDED pitch, not the fitted/measured one. The game derives its
        // actual aim (and hence the rendered pitch) from where the camera sits, so letting the
        // measured pitch set the height closes a high-gain loop that rang (steep angles) or
        // ran away (froze looking at the sky). The commanded pitch is open-loop and constant
        // once the slider settles, so the height — the loop's strongest lever — stops feeding
        // back. The fit still PROJECTS at the measured pitch, so composition stays honest.
        var drop = MathF.Tan(MathF.Atan(band * MathF.Abs(lensBase.TanHalfV)) - chiForHeight);
        var desiredCamY = corpseMain.Y - xRefSmoothed * drop;
        var cameraHeight = Math.Clamp(desiredCamY, floorY + 0.15f, floorY + 3.5f);
        DebugGroundY = floorY;

        // Find the cheapest rung that fits.
        var needed = -1;
        for (var level = 0; level <= 3; level++)
        {
            if (TrySolveAtLevel(playerAddress, level, corpseMain, yawReal, chi,
                    baseFov, fovMax, in lensBase, in lensWide, safeX, safeY,
                    cameraHeight, minStandoff, maxDist, out _, out _))
            {
                needed = level;
                break;
            }
        }

        if (needed < 0)
            return false;

        // Ladder hysteresis: climb the moment we must, come back down only once the cheaper
        // rung has been safe for a while.
        if (needed > ladderLevel)
        {
            ladderLevel = needed;
            ladderDwell = 0f;
        }
        else if (needed < ladderLevel)
        {
            ladderDwell += dt;
            if (ladderDwell > 1.0f)
            {
                ladderLevel--;
                ladderDwell = 0f;
            }
        }
        else
        {
            ladderDwell = 0f;
        }

        // Commit to the rung the hysteresis settled on — NOT the one we just found; solving at
        // `needed` would make the dwell decorative. ladderLevel ≥ needed, so this fits.
        if (!TrySolveAtLevel(playerAddress, ladderLevel, corpseMain, yawReal, chi,
                baseFov, fovMax, in lensBase, in lensWide, safeX, safeY,
                cameraHeight, minStandoff, maxDist, out var final, out var chosenFov))
            return false;

        killerFit = LevelToFit(ladderLevel);

        // (Terrain under the camera is handled up front by the smoothed camGround probe that
        // feeds floorY — no per-frame refit branch here, which is what used to flip-flop the
        // height at ledges.)

        DebugStandoff = final.Standoff;

        // Hand back the camera position itself; the caller springs it and derives the orbit
        // centre from the sprung value. The distance is measured to the body so the player's
        // scroll wheel works against the subject of the shot.
        cam = final.Camera;
        distance = MathF.Max(0.5f, Vector3.Distance(final.Camera, corpseMain));
        fov = chosenFov;
        return true;
    }

    private static KillerFit LevelToFit(int level) => level switch
    {
        >= 3 => KillerFit.Dropped,
        2 => KillerFit.HeadTorso,
        _ => KillerFit.FullBody,
    };

    /// <summary>
    /// Solve one rung of the relaxation ladder. Leaves <see cref="requiredPoints"/> holding
    /// that rung's anchors so the caller can re-fit against them.
    ///
    /// 0 — everything, at the lens we started with
    /// 1 — widen the lens (buys the killer's height without giving up the close-up)
    /// 2 — stop insisting on the killer's legs
    /// 3 — drop the killer; a close-up of the body alone
    /// </summary>
    private bool TrySolveAtLevel(
        nint playerAddress, int level, Vector3 corpseMain, float yawReal, float chi,
        float baseFov, float fovMax, in DynamicCameraSolver.Lens lensBase, in DynamicCameraSolver.Lens lensWide,
        float safeX, float safeY, float cameraHeight, float minStandoff, float maxDist,
        out DynamicCameraSolver.GroundedFitResult result, out float usedFov)
    {
        result = default;
        usedFov = level >= 1 ? fovMax : baseFov;
        var lens = level >= 1 ? lensWide : lensBase;

        BuildDeathAnchors(playerAddress, LevelToFit(level));
        if (requiredPoints.Count == 0)
            return false;

        result = DynamicCameraSolver.GroundedFit(
            requiredPoints, corpseMain, yawReal, chi, in lens,
            safeX, safeY, cameraHeight, minStandoff, maxDist);
        return result.Ok;
    }

    /// <summary>Ground height straight below a point, from the game's collision mesh (the
    /// same raycast TerrainHeightService uses for NPC footing). Null when nothing is hit.</summary>
    private static float? SampleGroundHeight(float x, float z, float refY)
    {
        try
        {
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(x, refY + 10f, z),
                    new Vector3(0f, -1f, 0f),
                    out var hit,
                    80f))
            {
                return hit.Point.Y;
            }
        }
        catch
        {
            // Collision module unavailable (zone transition); caller falls back to corpse height.
        }
        return null;
    }

    private void BuildDeathAnchors(nint playerAddress, KillerFit fit)
    {
        requiredPoints.Clear();
        if (playerAddress == nint.Zero)
            return;

        var fallback = ReadObjectPosition(playerAddress);

        // Corpse: a prefix of the head→feet chain, sized by the runtime coverage (slider-
        // seeded, wheel-driven). Coverage counts FROM THE HEAD: 0.5 must mean head-to-waist,
        // so the head can never be the part that goes missing.
        var levels = Math.Clamp(
            (int)MathF.Ceiling(Math.Clamp(deathCoveragePref, 0.25f, 1f) * CorpseChain.Length),
            2, CorpseChain.Length);

        // The head needs care the rest of the chain does not. The j_kao bone sits at the
        // skull BASE, so guaranteeing it on-screen still lets the top half of the head clip
        // the frame edge (reported in testing as "head not visible at 50% coverage") — pad
        // outward along the body axis to the head's far side. And if the head bone cannot be
        // read at all, synthesize it from the chest/hips axis rather than silently dropping
        // the one anchor the coverage semantics are counted from.
        var chest = CorpsePoint(playerAddress, "j_sebo_c");
        var hips = CorpsePoint(playerAddress, "j_kosi");
        var head = CorpsePoint(playerAddress, "j_kao");
        if (!head.HasValue && chest.HasValue && hips.HasValue)
            head = chest.Value + (chest.Value - hips.Value) * 0.9f;

        if (head.HasValue)
        {
            requiredPoints.Add(head.Value);
            var axisRef = chest ?? hips;
            if (axisRef.HasValue)
            {
                var axis = head.Value - axisRef.Value;
                if (axis.LengthSquared() > 1e-4f)
                    requiredPoints.Add(head.Value + Vector3.Normalize(axis) * 0.18f);
            }
        }

        for (var i = 1; i < levels; i++)
        {
            foreach (var bone in CorpseChain[i])
            {
                var p = CorpsePoint(playerAddress, bone);
                if (p.HasValue)
                    requiredPoints.Add(p.Value);
            }
        }

        // The ragdoll lets extremities settle INTO the floor (a foot 0.1y under ground in
        // testing). Requiring a below-ground point in frame drags the whole shot back for a
        // spot nobody can see — clamp the corpse anchors to the visible side of the ground.
        if (hasGroundSample)
        {
            var floor = smoothedGroundY + 0.03f;
            for (var i = 0; i < requiredPoints.Count; i++)
            {
                if (requiredPoints[i].Y < floor)
                    requiredPoints[i] = new Vector3(requiredPoints[i].X, floor, requiredPoints[i].Z);
            }
        }

        if (requiredPoints.Count == 0)
            requiredPoints.Add(fallback + new Vector3(0f, 0.5f, 0f));

        if (fit == KillerFit.Dropped)
            return;

        if (!TryGetKillerExtent(out var killerFeet, out var killerHead))
            return;

        if (fit == KillerFit.FullBody)
        {
            requiredPoints.Add(killerFeet);
            requiredPoints.Add(killerHead);
        }
        else
        {
            requiredPoints.Add(killerHead);
            requiredPoints.Add(Vector3.Lerp(killerFeet, killerHead, 0.45f));
        }
    }

    /// <summary>Head and foot of the enemy that killed the player. Head comes from the skull
    /// bone where we can read it, so tall or non-humanoid killers frame correctly.</summary>
    private bool TryGetKillerExtent(out Vector3 feet, out Vector3 head)
    {
        feet = default;
        head = default;

        var address = killerAddress;
        if (address == nint.Zero)
            return false;

        // Prefer re-resolving through the selector: the raw address can go stale.
        var npc = killerEntityId != 0 ? npcSelector.GetSelectedNpc(killerEntityId) : null;
        if (npc is { BattleChara: not null })
            address = npc.Address;

        var obj = (GameObject*)address;
        if (obj == null)
            return false;

        feet = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
        if (!float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
            return false;

        var skull = boneService.GetBoneWorldPos(address, "j_kao");
        head = skull ?? feet + new Vector3(0f, 2.0f, 0f);
        head.Y += 0.15f; // clear the top of the head, not its bone origin

        if (head.Y < feet.Y + 0.3f)
            head = feet + new Vector3(0f, 2.0f, 0f);

        return true;
    }

    /// <summary>
    /// The corpse is wherever physics left it. The skeleton pose stops updating at the moment
    /// of death, so the ragdoll body is the only thing that tracks a corpse that got kicked
    /// across the floor.
    /// </summary>
    private Vector3? CorpsePoint(nint playerAddress, string bone)
    {
        var name = bone == "n_hara" ? "j_kosi" : bone;
        var p = playerRagdoll.IsActive ? playerRagdoll.GetBodyWorldPosition(name) : null;
        p ??= boneService.GetBoneWorldPos(playerAddress, name);
        return p;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private float MeasurePlayerHeight(nint playerAddress)
    {
        var head = boneService.GetBoneWorldPos(playerAddress, "j_kao");
        if (head.HasValue)
        {
            var feet = ReadObjectPosition(playerAddress);
            var h = head.Value.Y - feet.Y + 0.15f;
            if (h > 0.5f && h < 6f)
                return h;
        }
        return 1.8f;
    }

    private static Vector3 ReadObjectPosition(nint address)
    {
        if (address == nint.Zero)
            return Vector3.Zero;
        var obj = (GameObject*)address;
        if (obj == null)
            return Vector3.Zero;
        return new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
    }

    private static Vector3 EnemyPosition(SimulatedNpc npc)
    {
        if (npc.BattleChara == null)
            return Vector3.Zero;
        var obj = (GameObject*)npc.BattleChara;
        return new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
    }

    /// <summary>Write the learned combat zoom through to config — throttled to gesture ends
    /// and resets so the wheel does not turn into disk IO.</summary>
    private void PersistCombatZoom(bool force = false)
    {
        if (!combatZoomDirty && !force)
            return;
        combatZoomDirty = false;
        if (MathF.Abs(config.DynCamCombatZoomMemory - combatZoomBias) < 0.001f)
            return;
        config.DynCamCombatZoomMemory = combatZoomBias;
        config.Save();
    }

    /// <summary>Write the remembered combat pitch through to config — on leaving combat and on
    /// reset, so it is not a per-frame disk write.</summary>
    private void PersistCombatPitch()
    {
        if (!combatPitchDirty)
            return;
        combatPitchDirty = false;
        config.DynCamCombatPitchMemory = combatPitchMemory;
        config.DynCamCombatPitchMemoryValid = combatPitchMemoryValid;
        config.Save();
    }

    /// <summary>Unpin the game's zoom once the death shot ends: give back the original
    /// distance band so the wheel works normally again, and disarm the pre-update lock.</summary>
    private void RestoreMinDistance()
    {
        enforceCameraLock = false;
        if (!distanceLimitsOverridden)
            return;
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr != null && camMgr->Camera != null)
            {
                camMgr->Camera->MinDistance = savedMinDistance;
                camMgr->Camera->MaxDistance = savedMaxDistance;
            }
        }
        catch { }
        distanceLimitsOverridden = false;
        distanceLimitSaveDelay = 0;
        pendingWheel = 0f;
    }

    /// <summary>
    /// Hold a value inside the limits the game will enforce anyway.
    ///
    /// This matters more than it looks: player intent is measured as the gap between what the
    /// game reports and what we last wrote. Write something the game clamps, and next frame that
    /// clamp is indistinguishable from the player having moved the camera — so we would hand
    /// them control they never asked for, permanently.
    ///
    /// We raise MaxDistance ourselves (via MaxDistanceAtLeast), so only the lower bound binds.
    /// </summary>
    private static float ClampToGameDistance(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam, float distance)
    {
        var min = gameCam->MinDistance;
        if (min > 0f && distance < min)
            return min;
        return distance;
    }

    private static float ClampToGamePitch(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam, float pitch)
    {
        var min = gameCam->DirVMin;
        var max = gameCam->DirVMax;
        if (max <= min)
            return pitch;
        return Math.Clamp(pitch, min, max);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ------------------------------------------------------------------
    // Collision patch
    // ------------------------------------------------------------------

    private void EnableCollisionPatch()
    {
        if (collisionPatchActive || collisionPatchAddress == nint.Zero)
            return;
        WriteMemory(collisionPatchAddress, CollisionPatchBytes);
        collisionPatchActive = true;
    }

    private void DisableCollisionPatch()
    {
        if (!collisionPatchActive || collisionPatchAddress == nint.Zero || collisionOriginalBytes == null)
            return;
        WriteMemory(collisionPatchAddress, collisionOriginalBytes);
        collisionPatchActive = false;
    }

    private static void WriteMemory(nint address, byte[] bytes)
    {
        VirtualProtect(address, (nuint)bytes.Length, 0x40, out var oldProtect);
        Marshal.Copy(bytes, 0, address, bytes.Length);
        VirtualProtect(address, (nuint)bytes.Length, oldProtect, out _);
    }

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    public void Dispose()
    {
        Reset();
    }
}
