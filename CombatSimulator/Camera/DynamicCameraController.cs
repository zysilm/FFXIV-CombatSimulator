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

    /// <summary>Bounds on the death-shot camera angle χ (positive = raised slightly,
    /// looking down; negative = flat on the ground, looking up at the killer).</summary>
    private const float DeathAngleMin = -0.35f;
    private const float DeathAngleMax = 0.45f;

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
    private float curPitch;
    private float curFov;
    private bool hasCurState;

    // Translate start snapshot.
    private Vector3 startCam;
    private Vector3 startPivot;
    private float startDistance;
    private float startPitch;
    private float startFov;
    private float translateElapsed;

    // Death subjects.
    private nint killerAddress;
    private uint killerEntityId;
    private KillerFit killerFit = KillerFit.FullBody;
    private int ladderLevel;
    private float ladderDwell;

    // Player-intent tracking. We write some axes, so "what the player did" is the
    // difference between what the game reports and what we last wrote.
    private float lastWrittenDistance;
    private float lastWrittenPitch;
    private bool wroteDistance;
    private bool wrotePitch;
    private float prevObservedDistance;

    private float zoomHold;
    private float rotateHold;

    /// <summary>Player's zoom preference in combat, as a multiple of the framing distance.
    /// Learned from their scroll wheel so we adapt to them instead of dragging them back.</summary>
    private float combatZoomBias = 1f;
    /// <summary>Last combat framing distance BEFORE the zoom bias was applied. The bias has to
    /// be measured against this, not against the biased result, or it compounds every scroll.</summary>
    private float combatBaseDistance;
    private float lastShareSetting;

    /// <summary>Death-shot camera angle χ (positive = looking down), seeded from the slider
    /// and then nudged by the player's vertical drags. χ lives in "photographer space";
    /// the game's DirV is its negation (negative DirV = camera above, looking down).</summary>
    private float deathAnglePref;
    private float deathZoomMul = 1f;
    private float smoothedShoulder;
    /// <summary>Which side the character currently sits on (±1). Held across frames so the
    /// choice is stable and so it can be frozen while the player is orbiting.</summary>
    private float shoulderSide;

    // Ground height under the shot, from a BGCollision raycast, smoothed so a ledge or a
    // stair edge under the camera path does not step the frame.
    private float smoothedGroundY;
    private bool hasGroundSample;

    private readonly List<Vector3> requiredPoints = new();

    // --- debug surface (read by DynamicCameraDebugOverlay / the GUI) ---
    public Phase CurrentPhase => phase;
    public IReadOnlyList<Vector3> RequiredPoints => requiredPoints;
    public KillerFit CurrentKillerFit => killerFit;
    public int LadderLevel => ladderLevel;
    public bool LastSolveOk { get; private set; }
    public float SolvedDistance { get; private set; }
    public float SolvedPitch { get; private set; }
    public float SolvedFov { get; private set; }
    public float SolvedYaw { get; private set; }
    public float Aspect { get; private set; } = 16f / 9f;
    public string StatusText { get; private set; } = "off";
    public float DebugGroundY { get; private set; }
    public float DebugStandoff { get; private set; }

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
        deathZoomMul = 1f;
        hasGroundSample = false;
        deathAnglePref = Math.Clamp(config.DynCamDeathAngle, DeathAngleMin, DeathAngleMax);

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
            startPitch = cam->DirV;
            startFov = cam->FoV;

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
            curPitch = startPitch;
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
        wrotePitch = false;
        killerAddress = nint.Zero;
        killerEntityId = 0;
        killerFit = KillerFit.FullBody;
        ladderLevel = 0;
        combatZoomBias = 1f;
        deathZoomMul = 1f;
        smoothedShoulder = 0f;
        shoulderSide = 0f;
        hasGroundSample = false;
        requiredPoints.Clear();
        StatusText = "off";
        coordinator.Release(CameraOwner.DynamicCam);
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

        Aspect = ReadAspect();
        ReadPlayerIntent(gameCam, dt);

        var alive = combatEngine.State.PlayerState.IsAlive;

        // Revived / reset: fall back to combat framing.
        if (alive && phase is Phase.DeathTranslate or Phase.DeathHold)
        {
            phase = Phase.Combat;
            DisableCollisionPatch();
        }

        if (!alive)
        {
            // Death framing switched off: hand the corpse back to whoever else wants it (the
            // classic Death Cam, which we are no longer suppressing) instead of pointing the
            // combat framing at a body.
            if (!config.DynCamDeathFraming)
            {
                coordinator.Release(CameraOwner.DynamicCam);
                DisableCollisionPatch();
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

        switch (phase)
        {
            case Phase.Combat:
                if (!config.DynCamCombatFraming)
                {
                    coordinator.Release(CameraOwner.DynamicCam);
                    StatusText = "combat framing off";
                    return;
                }
                TickCombat(player.Address, gameCam, dt);
                break;

            case Phase.DeathTranslate:
            case Phase.DeathHold:
                if (!config.DynCamDeathFraming)
                {
                    coordinator.Release(CameraOwner.DynamicCam);
                    StatusText = "death framing off";
                    return;
                }
                TickDeath(player.Address, gameCam, dt);
                break;

            default:
                coordinator.Release(CameraOwner.DynamicCam);
                break;
        }

        var owner = GetCurrentOwner?.Invoke() ?? CameraOwner.None;
        if (owner > CameraOwner.DynamicCam)
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

        var observedDistance = gameCam->Distance;
        var reference = wroteDistance ? lastWrittenDistance : prevObservedDistance;
        var zoomDelta = observedDistance - reference;
        if (MathF.Abs(zoomDelta) > 0.02f && prevObservedDistance > 0f)
        {
            zoomHold = hold;

            if (phase == Phase.Combat)
            {
                // Adopt their zoom as a bias on the framing distance, so we never drag them
                // back to ours — the screen-share adaptation just rides on top of it. Measured
                // against the UNBIASED distance, otherwise each scroll multiplies the last.
                var basis = MathF.Max(0.5f, combatBaseDistance);
                combatZoomBias = Math.Clamp(observedDistance / basis, 0.4f, 3.0f);
            }
            else
            {
                var basis = MathF.Max(0.5f, SolvedDistance);
                deathZoomMul = Math.Clamp(observedDistance / basis, 1.0f, MathF.Max(1.0f, config.DynCamDeathZoomMax));
            }
        }
        prevObservedDistance = observedDistance;

        if (wrotePitch && phase is Phase.DeathTranslate or Phase.DeathHold)
        {
            var pitchDelta = gameCam->DirV - lastWrittenPitch;
            if (MathF.Abs(pitchDelta) > 0.0005f)
            {
                // Game DirV and our χ run in opposite directions (negative DirV = camera
                // above looking down = positive χ), so a drag's delta enters negated.
                deathAnglePref = Math.Clamp(deathAnglePref - pitchDelta, DeathAngleMin, DeathAngleMax);
            }
        }

        // The slider is the user speaking directly — it overrides anything we learned.
        if (MathF.Abs(config.DynCamSubjectScreenShare - lastShareSetting) > 0.0001f)
        {
            lastShareSetting = config.DynCamSubjectScreenShare;
            combatZoomBias = 1f;
        }

        wroteDistance = false;
        wrotePitch = false;
    }

    private static float ReadAspect()
    {
        try
        {
            var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            if (device != null && device->Height > 0)
            {
                var a = (float)device->Width / device->Height;
                if (a > 0.5f && a < 5f)
                    return a;
            }
        }
        catch { }
        return 16f / 9f;
    }

    // ------------------------------------------------------------------
    // Combat framing
    // ------------------------------------------------------------------

    private void TickCombat(nint playerAddress, FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam, float dt)
    {
        var dirH = gameCam->DirH;
        var dirV = gameCam->DirV;
        var fov = gameCam->FoV;
        DynamicCameraSolver.Basis(dirH, dirV, out var fwd, out var right, out var up);

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
                config.DynCamShoulderScreenFrac * refDistance * DynamicCameraSolver.TanHalfH(fov, Aspect),
                0f, 1.5f);
            targetShoulder = side * offWorld;
        }

        // Ease side flips; snapping the frame across the character reads as a cut.
        var flipK = 1f - MathF.Exp(-2.0f * dt);
        smoothedShoulder += (targetShoulder - smoothedShoulder) * flipK;
        var pivot = anchor + right * smoothedShoulder;

        // Hold the body at a constant on-screen size.
        var height = MeasurePlayerHeight(playerAddress);
        var desired = DynamicCameraSolver.DistanceForScreenShare(height, config.DynCamSubjectScreenShare, fov);
        desired *= CrowdingRelief(gameCam, fwd, right, up, fov);
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
        SolvedPitch = dirV;
        SolvedFov = fov;
        SolvedYaw = dirH;
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
    private float CrowdingRelief(FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam,
        in Vector3 fwd, in Vector3 right, in Vector3 up, float fov)
    {
        if (config.DynCamCrowdingRelief <= 0.001f)
            return 1f;

        var scenePos = gameCam->CameraBase.SceneCamera.Position;
        var cam = new Vector3(scenePos.X, scenePos.Y, scenePos.Z);

        var maxAbsX = 0f;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null || !npc.IsAlive || !npc.IsEngaged)
                continue;
            var ndc = DynamicCameraSolver.Project(EnemyPosition(npc), cam, fwd, right, up, fov, Aspect);
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

    private void TickDeath(nint playerAddress, FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCam, float dt)
    {
        var wantCollisionOff = config.DynCamDeathDisableCollision;
        if (wantCollisionOff && !collisionPatchActive) EnableCollisionPatch();
        else if (!wantCollisionOff && collisionPatchActive) DisableCollisionPatch();

        BuildDeathAnchors(playerAddress, killerFit);
        if (requiredPoints.Count < 2)
        {
            StatusText = "death framing — no anchors";
            return;
        }

        // Yaw is the player's, always. We only ever add a small bias on top, and give it
        // straight back the moment they touch the stick.
        var playerYaw = gameCam->DirH;

        // χ is the photographer's angle (positive = raised a little, looking down; negative
        // = flat on the ground looking up). The game's DirV is its negation. Clamp through
        // the game's own DirV band too: the game silently clamping our write would read back
        // as a phantom player drag and walk the preference away.
        var chi = Math.Clamp(deathAnglePref, DeathAngleMin, DeathAngleMax);
        chi = -ClampToGamePitch(gameCam, -chi);
        deathAnglePref = chi;

        var solved = SolveDeathFraming(playerAddress, playerYaw, chi, dt,
            out var solvedCam, out var distance, out var fov, out var yaw);

        var pitch = -chi; // game-space DirV target for the shot

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
            SolvedPitch = pitch;
            SolvedFov = fov;
            SolvedYaw = yaw;
        }

        var targetPitch = solved ? pitch : curPitch;
        var targetFov = solved ? fov : curFov;
        var targetDistance = solved ? distance : curDistance;
        var targetCam = solved ? solvedCam : curCam;

        // The player's zoom pulls the camera straight back along the view axis. Backing the
        // camera off by d·(μ−1) while asking for a distance of d·μ leaves the pivot exactly
        // where it was — anchored on the body — so zooming out never breaks the composition,
        // it only makes it smaller.
        if (solved && deathZoomMul > 1.0001f)
        {
            DynamicCameraSolver.Basis(yaw, pitch, out var zf, out _, out _);
            targetCam -= zf * (distance * (deathZoomMul - 1f));
            targetDistance = distance * deathZoomMul;
        }

        if (phase == Phase.DeathTranslate)
        {
            translateElapsed += dt;
            var duration = MathF.Max(0.05f, config.DynCamDeathTranslateDuration);
            var t = Math.Clamp(translateElapsed / duration, 0f, 1f);
            var s = t * t * (3f - 2f * t);

            curCam = Vector3.Lerp(startCam, targetCam, s);
            curDistance = Lerp(startDistance, targetDistance, s);
            curPitch = Lerp(startPitch, targetPitch, s);
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
            curPitch += (targetPitch - curPitch) * dk;
            curFov += (targetFov - curFov) * sk;

            StatusText = killerFit switch
            {
                KillerFit.FullBody => "death framing — corpse + killer (full body)",
                KillerFit.HeadTorso => "death framing — corpse + killer (head/torso)",
                _ => "death framing — corpse only (killer out of reach)",
            };
        }

        // The floor, enforced on the value we actually submit rather than on the solve. Nothing
        // downstream can put the camera underground: not a bad solve, not a spring overshoot,
        // not the translate blending through a hillside.
        if (hasGroundSample)
            curCam.Y = MathF.Max(curCam.Y, smoothedGroundY + 0.15f);

        curDistance = ClampToGameDistance(gameCam, curDistance);
        curPitch = ClampToGamePitch(gameCam, curPitch);

        // Hand the game an orbit centre that reconstructs the camera we want: it rebuilds the
        // camera at pivot − fwd·Distance, so pivot = camera + fwd·Distance lands it on curCam.
        DynamicCameraSolver.Basis(gameCam->DirH, curPitch, out var fwd, out _, out _);
        curPivot = curCam + fwd * curDistance;

        hasCurState = true;
        lastWrittenDistance = curDistance;
        lastWrittenPitch = curPitch;
        wroteDistance = true;
        wrotePitch = true;

        coordinator.Submit(CameraOwner.DynamicCam, new CameraRequest
        {
            OrbitCenter = curPivot,
            DirV = curPitch,
            Distance = curDistance,
            Fov = curFov,
            MaxDistanceAtLeast = curDistance + 1f,
            // No DirH, no ClearInput: the player keeps the yaw and the camera keeps working.
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
    private bool SolveDeathFraming(nint playerAddress, float playerYaw, float chi, float dt,
        out Vector3 cam, out float distance, out float fov, out float yaw)
    {
        cam = default;
        distance = 0f;
        fov = curFov;
        yaw = playerYaw;

        var safeY = 1f - Math.Clamp(config.DynCamDeathSafeMargin, 0.01f, 0.4f);
        var safeX = safeY;
        var fovMin = Math.Clamp(config.DynCamDeathFovMin, 0.2f, 2.0f);
        var fovMax = MathF.Max(fovMin + 0.05f, Math.Clamp(config.DynCamDeathFovMax, 0.2f, 2.5f));
        var maxDist = MathF.Max(5f, config.DynCamDeathMaxDistance);
        var baseFov = Math.Clamp(startFov, fovMin, fovMax);
        var minStandoff = Math.Clamp(config.DynCamDeathCloseUpDistance, 1.2f, 6f);

        // The shot is anchored on the corpse's chest: standoff is measured from it, and the
        // zoom the player is handed works against it.
        var corpseMain = CorpsePoint(playerAddress, "j_sebo_c")
                         ?? CorpsePoint(playerAddress, "j_kosi")
                         ?? ReadObjectPosition(playerAddress);

        // Terrain-anchored camera height. This is the hard guarantee the first version lacked:
        // whatever the fit does, the camera can no longer end up underground.
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
        var cameraHeight = CameraHeightAbove(smoothedGroundY);
        DebugGroundY = smoothedGroundY;

        // Find the cheapest rung that fits.
        var needed = -1;
        for (var level = 0; level <= 3; level++)
        {
            if (TrySolveAtLevel(playerAddress, level, corpseMain, playerYaw, chi,
                    baseFov, fovMax, safeX, safeY, cameraHeight, minStandoff, maxDist,
                    out _, out _))
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
        if (!TrySolveAtLevel(playerAddress, ladderLevel, corpseMain, playerYaw, chi,
                baseFov, fovMax, safeX, safeY, cameraHeight, minStandoff, maxDist,
                out var final, out var chosenFov))
            return false;

        killerFit = LevelToFit(ladderLevel);

        // The terrain under the solved camera spot can differ from the terrain under the corpse
        // (slopes, ledges, stairs). Re-probe there and redo the fit once at the corrected
        // height; one correction is enough for anything that is not a cliff.
        var groundAtCam = SampleGroundHeight(final.Camera.X, final.Camera.Z, corpseMain.Y);
        if (groundAtCam.HasValue && MathF.Abs(groundAtCam.Value - smoothedGroundY) > 0.15f)
        {
            var corrected = DynamicCameraSolver.GroundedFit(
                requiredPoints, corpseMain, playerYaw, -chi, chosenFov, Aspect,
                safeX, safeY, CameraHeightAbove(groundAtCam.Value), minStandoff, maxDist);
            if (corrected.Ok)
                final = corrected;
        }

        DebugStandoff = final.Standoff;

        // Hand back the camera position itself; the caller springs it and derives the orbit
        // centre from the sprung value. The distance is measured to the body so the player's
        // scroll wheel works against the subject of the shot.
        cam = final.Camera;
        distance = MathF.Max(0.5f, Vector3.Distance(final.Camera, corpseMain));
        fov = chosenFov;
        yaw = playerYaw;
        return true;
    }

    private float CameraHeightAbove(float ground)
        => ground + MathF.Max(0.15f, Math.Clamp(config.DynCamDeathCamHeight, 0.15f, 3f));

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
        nint playerAddress, int level, Vector3 corpseMain, float playerYaw, float chi,
        float baseFov, float fovMax, float safeX, float safeY,
        float cameraHeight, float minStandoff, float maxDist,
        out DynamicCameraSolver.GroundedFitResult result, out float usedFov)
    {
        result = default;
        usedFov = level >= 1 ? fovMax : baseFov;

        BuildDeathAnchors(playerAddress, LevelToFit(level));
        if (requiredPoints.Count == 0)
            return false;

        result = DynamicCameraSolver.GroundedFit(
            requiredPoints, corpseMain, playerYaw, -chi, usedFov, Aspect,
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

        // Corpse: a prefix of the head→feet chain, sized by the visibility slider.
        var levels = Math.Clamp(
            (int)MathF.Ceiling(Math.Clamp(config.DynCamDeathBodyVisibility, 0.25f, 1f) * CorpseChain.Length),
            2, CorpseChain.Length);

        for (var i = 0; i < levels; i++)
        {
            foreach (var bone in CorpseChain[i])
            {
                var p = CorpsePoint(playerAddress, bone);
                if (p.HasValue)
                    requiredPoints.Add(p.Value);
            }
        }

        if (requiredPoints.Count == 0)
            requiredPoints.Add(fallback + new Vector3(0f, 0.5f, 0f));

        if (fit == KillerFit.Dropped)
            return;

        if (!TryGetKillerExtent(out var feet, out var head))
            return;

        if (fit == KillerFit.FullBody)
        {
            requiredPoints.Add(feet);
            requiredPoints.Add(head);
        }
        else
        {
            requiredPoints.Add(head);
            requiredPoints.Add(Vector3.Lerp(feet, head, 0.45f));
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
