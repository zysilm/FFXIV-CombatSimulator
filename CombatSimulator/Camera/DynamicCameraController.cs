// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatSimulator.Camera;

/// <summary>
/// Dynamic Camera.
///
/// Two personalities:
///
///  • Combat — God of War-style over-the-shoulder framing. The character is pushed off
///    to one side (away from the enemy that has the player's attention) and held at a
///    constant on-screen size; a wide enemy arc pulls the camera back so a crowd does
///    not turn into the claustrophobic mess the real thing is criticised for.
///
///  • Death — the automatic death shot (<see cref="DeathShotDirector"/>, docs/DEATH_SHOTS.md).
///    This controller only decides when one is wanted (the player is down, the Directed
///    camera directs deaths, nothing has suspended it), with which profile and of which
///    body, and drives the director; the framing itself is the director's and the
///    solver's. Combat framing stops while the player is down.
///
/// The rule that shapes the combat framing: THE PLAYER NEVER LOSES THE CAMERA. We own the
/// orbit centre — but the yaw is always the player's, we never clear their input, and
/// their pitch and zoom nudges are read back as preferences rather than overwritten. The
/// framing re-solves around whatever they do. (During a death shot the player keeps the
/// left/right orbit and the wheel; see the director.)
/// </summary>
public sealed unsafe partial class DynamicCameraController : IDisposable
{
    public enum Phase
    {
        Off,
        Combat,
        /// <summary>The player is down and the automatic death shot is wanted.</summary>
        Death,
    }

    private readonly Configuration config;
    public Func<bool>? EnabledOverride { get; set; }
    public Func<bool>? ActiveCameraEnabledOverride { get; set; }
    public Func<bool>? ProfileFramingOverride { get; set; }
    private bool DeathFramingEnabled => (ProfileFramingOverride?.Invoke() ?? false) || config.DynCamDeathFraming;
    private bool CombatFramingEnabled => (ProfileFramingOverride?.Invoke() ?? false) || config.DynCamCombatFraming;
    private bool CameraEnabled => EnabledOverride?.Invoke() ?? config.EnableDynamicCamera;
    private bool ActiveCameraEnabled => ActiveCameraEnabledOverride?.Invoke() ?? config.EnableActiveCamera;
    private readonly CameraModeCoordinator coordinator;
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly BoneTransformService boneService;
    private readonly DeathShotDirector deathShots;
    private readonly IPluginLog log;

    private Phase phase = Phase.Off;

    // Submitted combat state, kept across frames so the spring-follow is continuous.
    private Vector3 curPivot;
    private float curDistance;
    private bool hasCurState;

    // Player-intent tracking. We write some axes, so "what the player did" is the
    // difference between what the game reports and what we last wrote.
    private float lastWrittenDistance;
    private bool wroteDistance;
    private float prevObservedDistance;

    private float zoomHold;

    /// <summary>Player's zoom preference in combat, as a multiple of the framing distance.
    /// Learned from their scroll wheel so we adapt to them instead of dragging them back.
    /// Seeded from (and persisted to) config so a new fight resumes at their zoom.</summary>
    private float combatZoomBias = 1f;
    private bool combatZoomDirty;
    /// <summary>Last combat framing distance BEFORE the zoom bias was applied. The bias has to
    /// be measured against this, not against the biased result, or it compounds every scroll.</summary>
    private float combatBaseDistance;
    private float lastShareSetting;

    /// <summary>Player's combat pitch (game DirV), remembered across a death the same way
    /// the zoom is. Restored once when combat framing (re)starts; tracked continuously while in
    /// combat. Persisted so it survives sessions.</summary>
    private float combatPitchMemory;
    private bool combatPitchMemoryValid;
    private bool combatPitchDirty;
    /// <summary>Set on entry into the Combat phase so the next TickCombat writes the remembered
    /// pitch once, then hands the axis back to the player.</summary>
    private bool combatPitchRestorePending;
    private Phase prevPhase = Phase.Off;

    /// <summary>Which side the character currently sits on (±1). Held across frames so the
    /// choice is stable and so it can be frozen while the player is orbiting.</summary>
    private float shoulderSide;
    private float smoothedShoulder;

    /// <summary>Currently applied COMBAT-only distance added on top of the user's zoom so the
    /// visible engaged enemies fit the lens. It owns a separate transition: entering/leaving
    /// enemy fitting eases this value instead of changing the camera target in one frame.
    /// Zoom adoption subtracts it so temporary framing never becomes the learned preference.</summary>
    private float lastEnemyFitExtra;

    // --- status (read by the GUI) ---
    public Phase CurrentPhase => phase;
    public string StatusText { get; private set; } = "off";

    /// <summary>Raw mouse wheel from the plugin's ImGui draw pass: during a death shot the wheel is the shot's
    /// size (the director holds the game's own zoom). Ignored everywhere else.</summary>
    public void NotifyMouseWheel(float wheel)
    {
        if (phase == Phase.Death)
            deathShots.NotifyMouseWheel(wheel);
    }

    /// <summary>Camera write authority, from the coordinator. Only used for the status readout —
    /// arbitration itself is the coordinator's job.</summary>
    public Func<CameraOwner>? GetCurrentOwner { get; set; }

    public DynamicCameraController(
        Configuration config,
        CameraModeCoordinator coordinator,
        CombatEngine combatEngine,
        NpcSelector npcSelector,
        BoneTransformService boneService,
        DeathShotDirector deathShots,
        ISigScanner sigScanner,
        IPluginLog log)
    {
        this.config = config;
        this.coordinator = coordinator;
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.boneService = boneService;
        this.deathShots = deathShots;
        this.log = log;

        lastShareSetting = config.DynCamSubjectScreenShare;
        combatZoomBias = Math.Clamp(config.DynCamCombatZoomMemory, 0.4f, 3.0f);
        combatPitchMemory = config.DynCamCombatPitchMemory;
        combatPitchMemoryValid = config.DynCamCombatPitchMemoryValid;
    }

    /// <summary>CombatEvents.BeforePlayerDeath: the player's death animation starts now, and with it the ragdoll
    /// is armed (when ragdolls are on). Until then a death shot that already runs (the player has been down since
    /// the killing blow) knows a ragdoll is still to come.</summary>
    public void HandlePlayerDeath() => deathAnimationStarted = true;

    private bool deathAnimationStarted;

    /// <summary>Drop the camera and undo everything we touched (a death shot ends at once). Hooked to sim reset,
    /// territory change, logout and dispose.</summary>
    public void Reset() => Reset(true);

    public void Reset(bool persistMemory)
    {
        phase = Phase.Off;
        hasCurState = false;
        wroteDistance = false;
        deathAnimationStarted = false;
        // Combat zoom and pitch survive resets by design: persist what was learned, then reseed
        // from the persisted values — a new fight resumes at the zoom and angle last settled on.
        if (persistMemory)
        {
            PersistCombatZoom();
            PersistCombatPitch();
        }
        combatZoomDirty = combatPitchDirty = false;
        combatZoomBias = Math.Clamp(config.DynCamCombatZoomMemory, 0.4f, 3.0f);
        combatPitchMemory = config.DynCamCombatPitchMemory;
        combatPitchMemoryValid = config.DynCamCombatPitchMemoryValid;
        combatPitchRestorePending = false;
        prevPhase = Phase.Off;
        smoothedShoulder = 0f;
        shoulderSide = 0f;
        lastEnemyFitExtra = 0f;
        StatusText = "off";
        coordinator.Release(CameraOwner.DynamicCam);
        deathShots.Stop("camera reset");
    }

    private bool externalControlSuspended;

    public void SuspendForExternalControl(bool suspended)
    {
        if (externalControlSuspended == suspended) return;
        externalControlSuspended = suspended;
        if (suspended)
        {
            // Keep the combat state; a death shot ends, and starts again from the camera it finds once the
            // scene gives the camera back.
            coordinator.Release(CameraOwner.DynamicCam);
            deathShots.Stop("another scene has the camera");
        }
        else
        {
            // A restored scene camera must not be mistaken for player scroll input.
            prevObservedDistance = 0f;
            wroteDistance = false;
        }
    }

    public void Tick(float dt)
    {
        deathShots.Advance(dt);
        if (externalControlSuspended) return;
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
        if (!CameraEnabled)
        {
            if (phase != Phase.Off) Reset();
            StatusText = "disabled";
            return;
        }

        // The user turning Active Camera on is an explicit choice about who drives; we step
        // aside entirely rather than leaking a pivot in through the coordinator's orbit cascade.
        if (ActiveCameraEnabled)
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

        // Revived: back to combat framing; the death shot eases back to the game's camera.
        if (alive)
        {
            deathAnimationStarted = false;
            if (phase == Phase.Death)
            {
                phase = Phase.Combat;
                deathShots.Stop("revived", blend: true);
            }
        }
        else
        {
            // Death shots switched off: release every Dynamic Camera request and leave the
            // game's current camera untouched instead of pointing combat framing at a body.
            if (!DeathFramingEnabled)
            {
                coordinator.Release(CameraOwner.DynamicCam);
                deathShots.Stop("death shots are off");
                phase = Phase.Off;
                hasCurState = false;
                StatusText = "death shot off";
                return;
            }

            // The player is down (from the killing blow on; the death animation follows after the engine's
            // delay): the death shot runs from here, also when the option was switched on mid-death.
            if (phase != Phase.Death)
            {
                phase = Phase.Death;
                hasCurState = false;
            }
        }

        if (alive && phase == Phase.Off)
            phase = Phase.Combat;

        // Combat pitch memory: arm a one-shot restore when we enter combat framing, and save
        // the last-held pitch when we leave it (a death, or the mode going Off) — the
        // same lifecycle the combat zoom already has.
        if (phase == Phase.Combat && prevPhase != Phase.Combat)
            combatPitchRestorePending = true;
        else if (phase != Phase.Combat && prevPhase == Phase.Combat)
            PersistCombatPitch();
        prevPhase = phase;

        switch (phase)
        {
            case Phase.Combat:
                if (!CombatFramingEnabled)
                {
                    TransitionEnemyFitExtra(0f, dt);
                    coordinator.Release(CameraOwner.DynamicCam);
                    StatusText = "combat framing off";
                    return;
                }
                TickCombat(player.Address, gameCam, in view, dt);
                break;

            case Phase.Death:
                // Enemy-volume fitting belongs exclusively to the combat personality. Let its
                // internal contribution return to the inactive state; the death shot never reads it.
                TransitionEnemyFitExtra(0f, dt);
                coordinator.Release(CameraOwner.DynamicCam);
                deathShots.Run(dt, DeathShotRequestFor(player.Address));
                StatusText = deathShots.Status;
                return;

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

        var hold = MathF.Max(0.1f, config.DynCamInputHold);

        // COMBAT-mode zoom adoption only. A death shot reads the player's hand itself
        // (DeathShotDirector) and holds the game's zoom where it was.
        var observedDistance = gameCam->Distance;
        var reference = wroteDistance ? lastWrittenDistance : prevObservedDistance;
        var zoomDelta = observedDistance - reference;
        if (phase == Phase.Combat && MathF.Abs(zoomDelta) > 0.02f && prevObservedDistance > 0f)
        {
            zoomHold = hold;

            // Adopt their zoom as a bias on the framing distance, so we never drag them
            // back to ours — the screen-share adaptation just rides on top of it. Measured
            // against the UNBIASED distance, otherwise each scroll multiplies the last.
            // Adaptive enemy fitting is subtracted from the observation first: it is OUR
            // temporary addition, and learning it as the player's preference would leave
            // the camera parked far out after the fight ends.
            var basis = MathF.Max(0.5f, combatBaseDistance);
            combatZoomBias = Math.Clamp((observedDistance - lastEnemyFitExtra) / basis, 0.4f, 3.0f);
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

        // Combat pitch memory. On the first frame of a combat session, restore the pitch the
        // player last held (unless we have never recorded one, in which case we adopt the
        // current pitch as the starting memory). After that the player owns the axis, and we
        // just remember where they leave it — a death shot leaves the game's own pitch to
        // whatever the mouse did meanwhile, so reviving puts it back.
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
        // gets the open half of the frame. Field-verified handedness (the cross-product right
        // vector points screen-LEFT in this engine): shifting the orbit centre along +right
        // slides the character RIGHT on screen, and vice versa — the auto path never noticed
        // because it uses `right` twice and the flips cancel. The offset is a SCREEN fraction:
        // the world offset scales with distance, so the composition holds steady across the
        // whole zoom range instead of shoving the character off-frame at close range.
        var focus = FindFocusEnemy(playerAddress);
        var targetShoulder = 0f;
        if (config.DynCamShoulderScreenFrac > 0.001f)
        {
            var side = config.DynCamShoulderSide switch
            {
                1 => -1f,  // character forced left  → pivot shifts -right
                2 => 1f,   // character forced right → pivot shifts +right
                // Auto follows the CURRENT view even while the player is orbiting. Freezing
                // here used to leave both subjects crammed onto one side after the camera
                // crossed the fight. AutoShoulderSide supplies the hysteresis; the shoulder
                // and pivot springs below supply the visible transition.
                _ => AutoShoulderSide(focus, playerAddress, anchor, right),
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
        MeasureEnemyFraming(view.Position, fwd, right, up, lens, gameCam->Distance,
            config.DynCamEnemyVolumeFitting,
            out var crowdingScale, out var minimumEnemyFitDistance);
        desired *= crowdingScale;
        combatBaseDistance = Math.Clamp(desired, 1.0f, 30f);

        // Enemy fitting is a COMBAT-only dynamic minimum distance, not part of the user's zoom
        // preference. Give this feature its own enter/leave transition. The general distance
        // spring then follows the already-smoothed target; losing engagement cannot create a cut.
        var biasedDistance = Math.Clamp(desired * combatZoomBias, 1.0f, 30f);
        var targetEnemyFitExtra = Math.Clamp(minimumEnemyFitDistance - biasedDistance,
            0f, 30f - biasedDistance);
        TransitionEnemyFitExtra(targetEnemyFitExtra, dt);
        desired = Math.Clamp(biasedDistance + lastEnemyFitExtra, 1.0f, 30f);

        if (!hasCurState)
        {
            curPivot = pivot;
            curDistance = desired;
            hasCurState = true;
        }
        else
        {
            // Anisotropic pivot tracking. The exponential smoothing has a time constant of
            // 1/PivotSmoothing seconds; at run speed (~6 y/s) the default 6.0 leaves the pivot
            // trailing the character by ~1 yalm. Trailing SIDEWAYS is the cinematic drift we
            // want. Trailing in DEPTH is a bug: running toward the camera parks the character
            // a yalm inside the framing distance, and at the closest zoom (1.6y) that fills
            // the frame with torso. So split the error against the horizontal view axis —
            // depth chases near-instantly (reads as a faint size breath, not as motion), while
            // the lateral and vertical parts (including run bob) keep the configured drift.
            var pk = 1f - MathF.Exp(-MathF.Max(0.1f, config.DynCamPivotSmoothing) * dt);
            var fwdFlat = new Vector3(fwd.X, 0f, fwd.Z);
            if (fwdFlat.LengthSquared() > 1e-4f)
            {
                fwdFlat = Vector3.Normalize(fwdFlat);
                var err = pivot - curPivot;
                var depth = fwdFlat * Vector3.Dot(err, fwdFlat);
                var drift = err - depth;
                var dkDepth = 1f - MathF.Exp(-MathF.Max(25f, config.DynCamPivotSmoothing) * dt);
                curPivot += drift * pk + depth * dkDepth;
            }
            else
            {
                // Looking straight up/down: no meaningful depth axis, plain smoothing.
                curPivot = Vector3.Lerp(curPivot, pivot, pk);
            }

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

        StatusText = zoomHold > 0f ? "combat framing (player zooming)" : "combat framing";

        coordinator.Submit(CameraOwner.DynamicCam, new CameraRequest
        {
            OrbitCenter = curPivot,
            Distance = distanceOut,
            MaxDistanceAtLeast = curDistance + 1f,
            // Yaw, pitch and lens all stay the player's in combat.
        });
    }

    /// <summary>Translate the combat-only enemy-fit contribution toward its active target or the
    /// inactive zero state. Activation is deliberate but responsive; release is slower so an enemy
    /// dying, disengaging, or leaving the frustum cannot flash the camera back inward.</summary>
    private void TransitionEnemyFitExtra(float target, float dt)
    {
        target = Math.Clamp(float.IsFinite(target) ? target : 0f, 0f, 30f);
        dt = Math.Clamp(float.IsFinite(dt) ? dt : 0f, 0f, 0.1f);
        var rate = target > lastEnemyFitExtra ? 4.0f : 2.2f;
        var t = 1f - MathF.Exp(-rate * dt);
        lastEnemyFitExtra += (target - lastEnemyFitExtra) * t;
        if (target <= 0f && lastEnemyFitExtra < 0.001f)
            lastEnemyFitExtra = 0f;
    }

    private float AutoShoulderSide(SimulatedNpc? focus, nint playerAddress, Vector3 anchor, Vector3 right)
    {
        // Use the committed target side for hysteresis, not the smoothed offset. During a
        // flip the offset necessarily crosses zero; treating that transient value as the
        // decision state can re-decide halfway through the translation. With no history at
        // all (fresh session, zero offset) default to -1: the character sits LEFT, the GoW
        // home composition, until an enemy gives us a reason to move them.
        var currentSide = shoulderSide != 0f
            ? shoulderSide
            : smoothedShoulder > 0f ? 1f : -1f;

        // Prefer the actual focus enemy. Before the first attack (notably with the optional
        // enemy-pack gate) nobody is engaged yet, so fall back to the player's facing direction:
        // orbiting from one side of the character to the other must still mirror the composition.
        var openDirection = focus is { BattleChara: not null }
            ? EnemyPosition(focus) - anchor
            : ReadObjectForward(playerAddress);
        openDirection.Y = 0f;
        if (openDirection.LengthSquared() < 0.01f)
            return currentSide;

        var lateral = Vector3.Dot(right, Vector3.Normalize(openDirection));

        // Hysteresis: only commit to the other side once the enemy is clearly over there,
        // otherwise an enemy circling through screen centre makes the frame twitch.
        const float flipThreshold = 0.15f;
        if (lateral > flipThreshold) return 1f;
        if (lateral < -flipThreshold) return -1f;
        return currentSide;
    }

    /// <summary>
    /// Measures crowd spread and the closest camera distance at which every visible engaged
    /// enemy fits. Each enemy is an upright cylinder using the game's already-available
    /// Height and HitboxRadius — no bones, raycasts, allocations, or screen-space sampling.
    ///
    /// Cylinder containment is solved directly against the four side planes of a slightly
    /// inset view frustum. Moving the camera backwards increases every plane distance
    /// linearly, so four dot products give the exact required pullback for that cylinder.
    /// All enemies share one traversal with crowding relief; cost is O(engaged enemies) with
    /// a very small constant and remains independent of model/bone complexity.
    /// </summary>
    private void MeasureEnemyFraming(Vector3 cam,
        in Vector3 fwd, in Vector3 right, in Vector3 up, in DynamicCameraSolver.Lens lens,
        float currentDistance, bool fitEnemyVolumes,
        out float crowdingScale, out float minimumFitDistance)
    {
        crowdingScale = 1f;
        minimumFitDistance = 0f;

        // Hard phase boundary: even if this helper is reused later, enemy-volume fitting must
        // never participate in a death shot. The death solver owns that composition.
        if (phase != Phase.Combat)
            return;

        // Leave a little breathing room so animation and distance smoothing do not graze the
        // exact screen edge. Invalid/tiny projection values disable fitting safely.
        const float safeFraction = 0.90f;
        var spanH = safeFraction * MathF.Abs(lens.TanHalfH);
        var spanV = safeFraction * MathF.Abs(lens.TanHalfV);
        if (spanH < 0.05f || spanV < 0.05f)
            return;

        var planeLeft = spanH * fwd + right;
        var planeRight = spanH * fwd - right;
        var planeBottom = spanV * fwd + up;
        var planeTop = spanV * fwd - up;

        static float HorizontalLength(in Vector3 n) => MathF.Sqrt(n.X * n.X + n.Z * n.Z);
        var leftHorizontal = HorizontalLength(planeLeft);
        var rightHorizontal = HorizontalLength(planeRight);
        var bottomHorizontal = HorizontalLength(planeBottom);
        var topHorizontal = HorizontalLength(planeTop);
        var forwardHorizontal = HorizontalLength(fwd);

        static float PlanePullback(in Vector3 to, float height, float radius,
            in Vector3 plane, float planeHorizontal, float retreatGain)
        {
            // Minimum support of an upright cylinder along the inward plane normal.
            var verticalMin = MathF.Min(0f, plane.Y * height);
            var cylinderMin = verticalMin - radius * planeHorizontal;
            return -(Vector3.Dot(plane, to) + cylinderMin) / retreatGain;
        }

        var maxAbsX = 0f;
        var requiredPullback = float.NegativeInfinity;
        var hasVisibleEnemy = false;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null || !npc.IsAlive || !npc.IsEngaged)
                continue;

            var obj = (GameObject*)npc.BattleChara;
            var root = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
            var to = root - cam;
            var depth = Vector3.Dot(to, fwd);
            if (depth <= 0.1f)
                continue; // do not try to fit the half of the fight behind the player
            // Preserve the existing crowd-spread preference without a second NPC traversal.
            var ndcX = Vector3.Dot(to, right) / (depth * lens.TanHalfH);
            if (float.IsFinite(ndcX))
                maxAbsX = MathF.Max(maxAbsX, MathF.Abs(ndcX));

            // Crowd spread is a separate combat option. When volume fitting is switched off,
            // retain that centre-point calculation but do not read or solve enemy dimensions.
            if (!fitEnemyVolumes)
                continue;
            hasVisibleEnemy = true;

            var radius = obj->HitboxRadius;
            if (!float.IsFinite(radius) || radius < 0.05f)
                radius = 0.5f;
            radius = Math.Clamp(radius, 0.1f, 12f);

            var objectHeight = obj->Height;
            if (!float.IsFinite(objectHeight) || objectHeight < 0.2f)
                objectHeight = MathF.Max(1.8f, radius * 2f);
            objectHeight = Math.Clamp(objectHeight, 0.2f, 30f);

            requiredPullback = MathF.Max(requiredPullback,
                PlanePullback(to, objectHeight, radius, planeLeft, leftHorizontal, spanH));
            requiredPullback = MathF.Max(requiredPullback,
                PlanePullback(to, objectHeight, radius, planeRight, rightHorizontal, spanH));
            requiredPullback = MathF.Max(requiredPullback,
                PlanePullback(to, objectHeight, radius, planeBottom, bottomHorizontal, spanV));
            requiredPullback = MathF.Max(requiredPullback,
                PlanePullback(to, objectHeight, radius, planeTop, topHorizontal, spanV));

            // Keep the complete cylinder in front of a small near-plane allowance too.
            var forwardVerticalMin = MathF.Min(0f, fwd.Y * objectHeight);
            var nearestDepth = depth + forwardVerticalMin - radius * forwardHorizontal;
            requiredPullback = MathF.Max(requiredPullback, 0.15f - nearestDepth);
        }

        const float comfortable = 0.75f;
        if (config.DynCamCrowdingRelief > 0.001f && maxAbsX > comfortable)
        {
            var over = MathF.Min(maxAbsX - comfortable, 0.75f) / 0.75f;
            crowdingScale = 1f + config.DynCamCrowdingRelief * over;
        }

        if (hasVisibleEnemy)
        {
            var observedDistance = float.IsFinite(currentDistance) ? MathF.Max(0.5f, currentDistance) : 0.5f;
            minimumFitDistance = MathF.Max(0f, observedDistance + requiredPullback);
        }
    }

    private SimulatedNpc? FindFocusEnemy(nint playerAddress)
    {
        var playerPos = ReadObjectPosition(playerAddress);
        SimulatedNpc? bestEngaged = null;
        SimulatedNpc? bestAlive = null;
        var bestEngagedDist = float.MaxValue;
        var bestAliveDist = float.MaxValue;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null || !npc.IsAlive)
                continue;
            var d = Vector3.DistanceSquared(EnemyPosition(npc), playerPos);
            if (d < bestAliveDist)
            {
                bestAliveDist = d;
                bestAlive = npc;
            }
            if (npc.IsEngaged && d < bestEngagedDist)
            {
                bestEngagedDist = d;
                bestEngaged = npc;
            }
        }
        return bestEngaged ?? bestAlive;
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

    private static Vector3 ReadObjectForward(nint address)
    {
        if (address == nint.Zero)
            return Vector3.UnitZ;
        var obj = (GameObject*)address;
        if (obj == null)
            return Vector3.UnitZ;
        return new Vector3(MathF.Sin(obj->Rotation), 0f, MathF.Cos(obj->Rotation));
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
        if (ProfileFramingOverride?.Invoke() == true) return;
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
        if (ProfileFramingOverride?.Invoke() == true) return;
        if (!combatPitchDirty)
            return;
        combatPitchDirty = false;
        config.DynCamCombatPitchMemory = combatPitchMemory;
        config.DynCamCombatPitchMemoryValid = combatPitchMemoryValid;
        config.Save();
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

    public void Dispose()
    {
        Reset();
    }
}
