using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using CombatSimulator.Animation;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace CombatSimulator.Dev;

public unsafe class VictorySequenceController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly EmoteTimelinePlayer emotePlayer;
    private readonly MovementBlockHook movementBlockHook;
    private readonly RagdollController ragdollController;
    private readonly VNavmeshIpc vnavmeshIpc;
    private readonly IClientState clientState;
    private readonly ITargetManager targetManager;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Runtime state
    private bool isActive;
    private float elapsed;
    private float stageElapsed;
    private SimulatedNpc? cinematicNpc;
    private Vector3 npcOriginalPos;
    private int currentStageIndex = -1;
    private bool stageAnimPlayed;
    private uint lastPlayedEmoteId;
    private uint lastPlayedActionTimelineId;
    private bool lastPlayedUseEmote;
    private string lastGrabNpcBone = "";
    private string lastGrabPlayerBone = "";
    private Vector3 playerDeathPos;
    private float playerFacingAngle;
    private ulong playerObjId;

    // Track last targeted NPC during combat (before death)
    private ulong lastTargetedNpcId;

    // Other NPCs (non-cinematic) — separate animation sequence with per-NPC time offset
    private struct OtherNpcState
    {
        public SimulatedNpc Npc;
        public float TimeOffset;      // random stagger so they don't all transition at once
        public int StageIndex;
        public float StageElapsed;
        public bool AnimPlayed;
        public uint LastEmoteId;
        public uint LastActionTimelineId;
        public bool LastUseEmote;
    }
    private readonly List<OtherNpcState> otherNpcStates = new();

    // Grab bone indices (resolved per stage)
    private bool grabActive;
    private int npcHandBoneIdx = -1;
    private int playerNeckBoneIdx = -1;

    // Manual grabber control (hidden): drive the grabber's movement + walk anim.
    private readonly ActorVisualState grabberVisualState = new();
    private bool grabberMoving;
    private float grabberControlYaw;

    // Automatic cinematic approach before a stage starts. The timer for the
    // behavior stage does not advance while this is moving.
    private bool stageApproachComplete;
    private int approachStageIndex = -1;
    private readonly ApproachPathState approachPath = new();
    private const float ApproachStopDistance = 0.25f;
    private const float ApproachRepathInterval = 0.75f;
    private const float ApproachRepathDistance = 1.0f;
    private const float ApproachWaypointReachDistance = 0.5f;
    private const float ApproachFloorRayStartOffset = 6.0f;
    private const float ApproachFloorRayDistance = 24.0f;
    private const float ApproachMaxNavmeshFloorDelta = 2.5f;

    public bool IsActive => isActive;
    public nint CinematicNpcAddress => cinematicNpc?.Address ?? nint.Zero;
    public int CurrentStageIndex => currentStageIndex;

    public bool ControlsNpc(nint address)
    {
        if (!isActive || address == nint.Zero)
            return false;

        if (cinematicNpc?.Address == address)
            return true;

        foreach (var state in otherNpcStates)
            if (state.Npc.Address == address)
                return true;

        return false;
    }

    public VictorySequenceController(
        BoneTransformService boneService,
        EmoteTimelinePlayer emotePlayer,
        MovementBlockHook movementBlockHook,
        RagdollController ragdollController,
        VNavmeshIpc vnavmeshIpc,
        IClientState clientState,
        ITargetManager targetManager,
        Configuration config,
        IPluginLog log)
    {
        this.boneService = boneService;
        this.emotePlayer = emotePlayer;
        this.movementBlockHook = movementBlockHook;
        this.ragdollController = ragdollController;
        this.vnavmeshIpc = vnavmeshIpc;
        this.clientState = clientState;
        this.targetManager = targetManager;
        this.config = config;
        this.log = log;
    }

    /// <summary>
    /// Call each frame during active combat to track the player's current target.
    /// The last targeted NPC is used as the cinematic NPC when the player dies.
    /// </summary>
    public void TrackTarget(IReadOnlyList<SimulatedNpc> npcs)
    {
        var target = targetManager.Target;
        if (target == null) return;
        var targetId = target.EntityId;
        foreach (var npc in npcs)
        {
            if (npc.SimulatedEntityId == targetId && npc.State.IsAlive && npc.BattleChara != null)
            {
                lastTargetedNpcId = targetId;
                return;
            }
        }
    }

    /// <summary>
    /// Try to start a cinematic victory sequence. Returns (started, cinematicNpc).
    /// If started, the caller should play normal victory emotes on ALL OTHER NPCs.
    /// </summary>
    public (bool Started, SimulatedNpc? CinematicNpc) TryStart(IReadOnlyList<SimulatedNpc> npcs)
    {
        if (!config.EnableVictorySequence || config.VictorySequenceStages.Count == 0)
            return (false, null);

        SimulatedNpc? candidate = null;

        // Custom primary: prefer an alive NPC whose name matches the filter text.
        // If several match, pick one at random; if none match, fall through to the
        // default last-targeted selection below.
        if (config.GrabCustomPrimary && !string.IsNullOrWhiteSpace(config.GrabCustomPrimaryName))
        {
            var matches = new List<SimulatedNpc>();
            foreach (var npc in npcs)
            {
                if (npc.State.IsAlive && npc.BattleChara != null &&
                    npc.Name.Contains(config.GrabCustomPrimaryName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(npc);
            }
            if (matches.Count > 0)
                candidate = matches[Random.Shared.Next(matches.Count)];
        }

        // Find the last targeted NPC (must be alive with valid BattleChara)
        if (candidate == null)
        {
            foreach (var npc in npcs)
            {
                if (npc.SimulatedEntityId == lastTargetedNpcId && npc.State.IsAlive && npc.BattleChara != null)
                {
                    candidate = npc;
                    break;
                }
            }
        }
        // Fallback: first alive NPC if last target is gone
        if (candidate == null)
        {
            foreach (var npc in npcs)
            {
                if (npc.State.IsAlive && npc.BattleChara != null)
                {
                    candidate = npc;
                    break;
                }
            }
        }
        if (candidate == null)
            return (false, null);

        cinematicNpc = candidate;

        // Capture player state at death
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return (false, null);

        playerDeathPos = player.Position;
        var playerObj = (GameObject*)player.Address;
        playerFacingAngle = playerObj->Rotation;
        playerObjId = playerObj->GetGameObjectId().Id;

        // Save NPC's true original position (before any plugin manipulation)
        npcOriginalPos = cinematicNpc.SpawnPosition;

        // Register NPC for approach movement (bypass server overrides)
        movementBlockHook.AddApproachNpc(cinematicNpc.Address);

        // Subscribe to render frame for bone constraints
        boneService.OnRenderFrame += OnRenderFrame;

        // Collect other alive NPCs with random time offsets for stagger
        otherNpcStates.Clear();
        foreach (var npc in npcs)
        {
            if (npc != cinematicNpc && npc.State.IsAlive && npc.BattleChara != null)
            {
                otherNpcStates.Add(new OtherNpcState
                {
                    Npc = npc,
                    TimeOffset = Random.Shared.NextSingle() * 0.25f, // 0-0.25s random delay
                    StageIndex = -1,
                });
                movementBlockHook.AddApproachNpc(npc.Address);
            }
        }

        isActive = true;
        elapsed = 0;
        stageElapsed = 0;
        currentStageIndex = -1;
        stageAnimPlayed = false;
        grabActive = false;
        grabberMoving = false;
        grabberVisualState.Kind = ActorVisualStateKind.None;
        grabberControlYaw = 0f;
        stageApproachComplete = false;
        approachStageIndex = -1;
        ResetApproachPath();

        var otherCount = config.VictorySequenceOtherStages.Count;
        log.Info($"VictorySequence: Started with NPC '{cinematicNpc.Name}' (lastTarget={lastTargetedNpcId:X}), {config.VictorySequenceStages.Count} stages, {otherNpcStates.Count} other NPCs with {otherCount} stages");
        return (true, cinematicNpc);
    }

    public void Tick(float deltaTime)
    {
        if (!isActive || cinematicNpc == null) return;

        // Safety: NPC might have been cleaned up
        if (cinematicNpc.BattleChara == null)
        {
            Stop();
            return;
        }

        elapsed += deltaTime;
        var stages = config.VictorySequenceStages;
        if (currentStageIndex < 0)
            EnterStage(0);

        if (currentStageIndex < 0 || currentStageIndex >= stages.Count)
            return;

        var stage = stages[currentStageIndex];
        var gameObj = (GameObject*)cinematicNpc.BattleChara;

        if (TickStageApproach(stage, gameObj, deltaTime))
        {
            TickOtherNpcs(deltaTime);
            return;
        }

        stageElapsed += deltaTime;
        var duration = GetStageDuration(stage);
        if (duration >= 0f && stageElapsed > duration)
        {
            var nextStage = currentStageIndex + 1;
            if (nextStage >= stages.Count)
            {
                log.Info("VictorySequence: All stages complete");
                Stop();
                return;
            }

            EnterStage(nextStage);
            TickOtherNpcs(deltaTime);
            return;
        }

        // When manual grabber control is on for a grab stage, the player drives the
        // grabber's movement (walk anim + floor-snapped) instead of the scripted
        // path. The grab constraint keeps tracking the moving hand bone, so the
        // carried body comes along.
        bool control = config.ControlGrabber && stage.GrabEnabled;
        if (control)
        {
            TickGrabberControl(stage, gameObj, deltaTime);
        }
        else
        {
            var npcPos = new Vector3(gameObj->Position.X, gameObj->Position.Y, gameObj->Position.Z);
            movementBlockHook.SetApproachPosition(gameObj, npcPos.X, npcPos.Y, npcPos.Z);

            if (stage.LockFacing)
            {
                var player = Core.Services.ObjectTable.LocalPlayer;
                var headPos = player != null ? GetBoneWorldPos(player.Address, "j_kao") : null;
                var faceTarget = headPos ?? playerDeathPos;
                var toHead = faceTarget - npcPos;
                if (toHead.LengthSquared() > 0.001f)
                    movementBlockHook.SetApproachRotation(gameObj, MathF.Atan2(toHead.X, toHead.Z));
            }
        }

        // Play animation on stage enter, or re-play if user changed config live.
        // Skip while actively walking under manual control (the walk anim owns the
        // timeline then; the stage's carry pose is restored once the player stops).
        bool configChanged = stageAnimPlayed && (
            stage.UseEmote != lastPlayedUseEmote ||
            stage.EmoteId != lastPlayedEmoteId ||
            stage.ActionTimelineId != lastPlayedActionTimelineId);
        if ((!stageAnimPlayed || configChanged) && !(control && grabberMoving))
        {
            if (configChanged)
            {
                stage.ResolvedIntroTimeline = 0;
                stage.ResolvedLoopTimeline = 0;
            }
            var character = (Character*)cinematicNpc.BattleChara;

            // Reset current emote before playing new one (BaseOverride persists otherwise)
            if (configChanged)
                emotePlayer.ResetEmote(character);

            if (stage.UseEmote && stage.EmoteId > 0)
            {
                // Emote mode: resolve loop from emote sheet, use height-adjusted
                // timeline as intro (kneel transition), base emote as loop (action).
                try
                {
                    var emoteSheet = Core.Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
                    if (emoteSheet != null)
                    {
                        var emote = emoteSheet.GetRow(stage.EmoteId);
                        stage.ResolvedLoopTimeline = (ushort)emote.ActionTimeline[0].RowId;
                        stage.ResolvedIntroTimeline = (ushort)emote.ActionTimeline[1].RowId;
                    }
                }
                catch { }

                if (stage.ResolvedIntroTimeline != 0 || stage.ResolvedLoopTimeline != 0)
                {
                    emotePlayer.PlayLoopedEmote(character, stage.ResolvedLoopTimeline, stage.ResolvedIntroTimeline, playerObjId);
                    log.Info($"VictorySequence: Emote {stage.EmoteId} (intro={stage.ResolvedIntroTimeline}, loop={stage.ResolvedLoopTimeline})");
                }
            }
            else if (!stage.UseEmote && stage.ActionTimelineId > 0)
            {
                character->Timeline.BaseOverride = (ushort)stage.ActionTimelineId;
                log.Info($"VictorySequence: ActionTimeline {stage.ActionTimelineId} via BaseOverride");
            }

            stageAnimPlayed = true;
            lastPlayedUseEmote = stage.UseEmote;
            lastPlayedEmoteId = stage.EmoteId;
            lastPlayedActionTimelineId = stage.ActionTimelineId;
        }

        // Update grab state — use BEPU2 OneBodyLinearServo via ragdoll
        // Also detect live bone name changes for debugging
        bool grabBonesChanged = grabActive && (
            stage.NpcBoneName != lastGrabNpcBone ||
            stage.PlayerBoneName != lastGrabPlayerBone);
        if (grabBonesChanged)
        {
            ragdollController.RemoveGrabConstraint();
            grabActive = false;
            log.Info("VictorySequence: Grab bones changed, re-creating constraint");
        }

        var grabOffset = new Vector3(stage.GrabOffsetX, stage.GrabOffsetY, stage.GrabOffsetZ);
        if (stage.GrabEnabled && !grabActive)
        {
            // Activate grab: create physics constraint on player's ragdoll bone
            ResolveBoneIndices(stage);
            if (npcHandBoneIdx >= 0 && ragdollController.IsActive)
            {
                var npcHandWorld = GetBoneWorldPos(cinematicNpc!.Address, stage.NpcBoneName, grabOffset);
                if (npcHandWorld != null)
                {
                    grabActive = ragdollController.CreateGrabConstraint(
                        stage.PlayerBoneName, npcHandWorld.Value, cinematicNpc!.Address,
                        stage.GrabForce, stage.GrabSpeed, stage.GrabSpringFreq);
                    if (grabActive)
                    {
                        lastGrabNpcBone = stage.NpcBoneName;
                        lastGrabPlayerBone = stage.PlayerBoneName;
                        log.Info("VictorySequence: BEPU2 grab constraint activated");
                    }
                }
            }
        }
        else if (!stage.GrabEnabled && grabActive)
        {
            // Deactivate grab (NPC collision restored internally by RagdollController)
            ragdollController.RemoveGrabConstraint();
            grabActive = false;
            log.Info("VictorySequence: Grab constraint deactivated");
        }

        // --- Tick other NPCs' animation sequence ---
        TickOtherNpcs(deltaTime);
    }

    private void EnterStage(int stageIndex)
    {
        currentStageIndex = stageIndex;
        stageElapsed = 0f;
        stageAnimPlayed = false;
        stageApproachComplete = false;
        approachStageIndex = -1;
        ResetApproachPath();

        var stages = config.VictorySequenceStages;
        if (stageIndex >= 0 && stageIndex < stages.Count)
            SyncLegacyTiming(stages[stageIndex]);

        log.Info($"VictorySequence: Entering stage {stageIndex}");
    }

    private static float GetStageDuration(VictorySequenceStage stage)
    {
        if (stage.Duration.HasValue)
            return stage.Duration.Value;

        if (stage.EndTime < 0f)
            return -1f;

        if (stage.EndTime > stage.StartTime)
            return MathF.Max(0.1f, stage.EndTime - stage.StartTime);

        return 3.0f;
    }

    private static void SetStageDuration(VictorySequenceStage stage, float duration)
    {
        stage.Duration = duration;
        stage.StartTime = 0f;
        stage.EndTime = duration < 0f ? -1f : duration;
    }

    private static void SyncLegacyTiming(VictorySequenceStage stage)
        => SetStageDuration(stage, GetStageDuration(stage));

    private bool TickStageApproach(VictorySequenceStage stage, GameObject* gameObj, float dt)
    {
        if (!stage.ApproachBeforeStage || stageApproachComplete)
            return false;

        var character = (Character*)gameObj;
        if (approachStageIndex != currentStageIndex)
        {
            approachStageIndex = currentStageIndex;
            ResetApproachPath();
            emotePlayer.ResetEmote(character);
            log.Debug($"VictorySeq approach init: charMode={character->Mode} stageIdx={currentStageIndex}");
            if (character->Mode != CharacterModes.Dead)
                character->SetMode(CharacterModes.Normal, 0);
            grabberVisualState.Kind = ActorVisualStateKind.None;
        }

        var target = CalculateApproachTarget(stage);
        var current = new Vector3(gameObj->Position.X, gameObj->Position.Y, gameObj->Position.Z);
        if (FlatDistance(current, target) <= ApproachStopDistance)
        {
            var snappedTarget = SnapApproachToFloor(target, playerDeathPos.Y);
            movementBlockHook.SetApproachPosition(gameObj, snappedTarget.X, snappedTarget.Y, snappedTarget.Z);
            FacePlayer(gameObj, snappedTarget);
            ActorVisualStateController.ClearMovement(character, grabberVisualState);
            emotePlayer.ResetEmote(character);
            grabberMoving = false;
            stageAnimPlayed = false;
            stageApproachComplete = true;
            ResetApproachPath();
            return false;
        }

        var moveTarget = GetApproachMoveTarget(current, target, dt);
        var dir = moveTarget - current;
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f)
            dir = target - current;
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f)
            return true;

        dir = Vector3.Normalize(dir);
        var speed = config.GrabberControlSpeed > 0f ? config.GrabberControlSpeed : 2.5f;
        var remaining = FlatDistance(current, moveTarget);
        var moveDist = speed * dt;
        var next = remaining <= moveDist
            ? moveTarget
            : current + dir * moveDist;
        next = SnapApproachToFloor(next, current.Y);

        movementBlockHook.SetApproachPosition(gameObj, next.X, next.Y, next.Z);
        movementBlockHook.SetApproachRotation(gameObj, MathF.Atan2(dir.X, dir.Z));
        ActorVisualStateController.ApplyMoving(character, grabberVisualState, dt);
        grabberMoving = true;
        return true;
    }

    private Vector3 CalculateApproachTarget(VictorySequenceStage stage)
    {
        var distance = Math.Clamp(stage.ApproachDistance, 0.2f, 30f);
        var playerForward = new Vector3(MathF.Sin(playerFacingAngle), 0f, MathF.Cos(playerFacingAngle));
        var target = playerDeathPos + playerForward * distance;
        return SnapApproachToFloor(target, playerDeathPos.Y);
    }

    private Vector3 GetApproachMoveTarget(Vector3 current, Vector3 target, float dt)
    {
        if (vnavmeshIpc == null)
            return target;

        vnavmeshIpc.RefreshStatus();
        if (!vnavmeshIpc.CanPathfind)
            return target;

        approachPath.RepathTimer = Math.Max(0f, approachPath.RepathTimer - dt);
        if (approachPath.PendingPath != null && approachPath.PendingPath.IsCompleted)
        {
            try
            {
                approachPath.Waypoints = approachPath.PendingPath.GetAwaiter().GetResult();
                approachPath.RequestedTarget = approachPath.PendingTarget;
                approachPath.WaypointIndex = 0;
            }
            catch (Exception ex)
            {
                log.Verbose($"VictorySequence approach path failed: {ex.Message}");
                approachPath.Waypoints.Clear();
            }
            approachPath.PendingPath = null;
        }

        var pathExhausted = approachPath.WaypointIndex >= approachPath.Waypoints.Count;
        var shouldRepath = approachPath.PendingPath == null &&
            approachPath.RepathTimer <= 0f &&
            (approachPath.Waypoints.Count == 0 ||
             Vector3.Distance(approachPath.RequestedTarget, target) > ApproachRepathDistance ||
             (pathExhausted && FlatDistance(current, target) > 2f));

        if (shouldRepath)
        {
            approachPath.RepathTimer = ApproachRepathInterval;
            var from = SnapToNavmeshNearHeight(current, current.Y) ?? current;
            var to = SnapToNavmeshNearHeight(target, playerDeathPos.Y) ?? target;
            approachPath.PendingTarget = to;
            try
            {
                approachPath.PendingPath = vnavmeshIpc.Pathfind(from, to, 0.75f);
            }
            catch (Exception ex)
            {
                log.Verbose($"VictorySequence approach path request failed: {ex.Message}");
                approachPath.PendingPath = null;
            }
        }

        while (approachPath.WaypointIndex < approachPath.Waypoints.Count &&
               FlatDistance(current, approachPath.Waypoints[approachPath.WaypointIndex]) < ApproachWaypointReachDistance)
            approachPath.WaypointIndex++;

        return approachPath.WaypointIndex < approachPath.Waypoints.Count
            ? SnapApproachToFloor(approachPath.Waypoints[approachPath.WaypointIndex], current.Y)
            : target;
    }

    private Vector3? SnapToNavmeshNearHeight(Vector3 point, float referenceY)
    {
        try
        {
            var snapped = vnavmeshIpc.NearestPointReachable(point)
                          ?? vnavmeshIpc.PointOnFloor(point + new Vector3(0, 10f, 0));
            if (snapped.HasValue && MathF.Abs(snapped.Value.Y - referenceY) <= ApproachMaxNavmeshFloorDelta)
                return snapped;
        }
        catch (Exception ex)
        {
            log.Verbose($"VictorySequence vnavmesh snap failed: {ex.Message}");
        }

        return null;
    }

    private Vector3 SnapApproachToFloor(Vector3 pos, float referenceY)
    {
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(pos.X, referenceY + ApproachFloorRayStartOffset, pos.Z),
                new Vector3(0, -1, 0),
                out var hit,
                ApproachFloorRayDistance))
            return new Vector3(pos.X, hit.Point.Y, pos.Z);

        if (vnavmeshIpc != null)
        {
            vnavmeshIpc.RefreshStatus();
            if (vnavmeshIpc.CanPathfind)
            {
                var floor = SnapToNavmeshNearHeight(pos, referenceY);
                if (floor.HasValue)
                    return new Vector3(pos.X, floor.Value.Y, pos.Z);
            }
        }

        return pos;
    }

    private void FacePlayer(GameObject* gameObj, Vector3 npcPos)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        var headPos = player != null ? GetBoneWorldPos(player.Address, "j_kao") : null;
        var faceTarget = headPos ?? playerDeathPos;
        var toHead = faceTarget - npcPos;
        if (toHead.LengthSquared() > 0.001f)
            movementBlockHook.SetApproachRotation(gameObj, MathF.Atan2(toHead.X, toHead.Z));
    }

    private void ResetApproachPath()
    {
        approachPath.Waypoints.Clear();
        approachPath.WaypointIndex = 0;
        approachPath.RequestedTarget = default;
        approachPath.PendingTarget = default;
        approachPath.PendingPath = null;
        approachPath.RepathTimer = 0f;
    }

    private void TickOtherNpcs(float deltaTime)
    {
        var otherStages = config.VictorySequenceOtherStages;
        if (otherStages.Count == 0 || otherNpcStates.Count == 0) return;

        // Resolve emote timelines once (shared across all NPCs)
        foreach (var os in otherStages)
        {
            if (os.UseEmote && os.EmoteId > 0 && os.ResolvedIntroTimeline == 0 && os.ResolvedLoopTimeline == 0)
            {
                try
                {
                    var emoteSheet = Core.Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
                    if (emoteSheet != null)
                    {
                        var emote = emoteSheet.GetRow(os.EmoteId);
                        os.ResolvedLoopTimeline = (ushort)emote.ActionTimeline[0].RowId;
                        os.ResolvedIntroTimeline = (ushort)emote.ActionTimeline[1].RowId;
                    }
                }
                catch { }
            }
        }

        // Head bone for lock facing (shared)
        Vector3? headPos = null;
        bool headResolved = false;

        // Tick each NPC independently with its own time offset
        for (int ni = 0; ni < otherNpcStates.Count; ni++)
        {
            var state = otherNpcStates[ni];
            if (state.Npc.BattleChara == null) continue;

            if (elapsed < state.TimeOffset) continue; // not started yet

            if (state.StageIndex < 0)
            {
                state.StageIndex = 0;
                state.StageElapsed = 0f;
                state.AnimPlayed = false;
            }

            if (state.StageIndex < 0 || state.StageIndex >= otherStages.Count)
            {
                otherNpcStates[ni] = state;
                continue;
            }

            var os = otherStages[state.StageIndex];
            state.StageElapsed += deltaTime;
            var osDuration = GetStageDuration(os);
            if (osDuration >= 0f && state.StageElapsed > osDuration)
            {
                state.StageIndex++;
                state.StageElapsed = 0f;
                state.AnimPlayed = false;
                if (state.StageIndex >= otherStages.Count)
                {
                    otherNpcStates[ni] = state;
                    continue;
                }
                os = otherStages[state.StageIndex];
            }

            // Play animation on stage enter or config change
            bool changed = state.AnimPlayed && (
                os.UseEmote != state.LastUseEmote ||
                os.EmoteId != state.LastEmoteId ||
                os.ActionTimelineId != state.LastActionTimelineId);
            if (!state.AnimPlayed || changed)
            {
                var character = (Character*)state.Npc.BattleChara;
                if (changed) emotePlayer.ResetEmote(character);

                if (os.UseEmote && os.EmoteId > 0)
                {
                    if (os.ResolvedIntroTimeline != 0 || os.ResolvedLoopTimeline != 0)
                        emotePlayer.PlayLoopedEmote(character, os.ResolvedLoopTimeline, os.ResolvedIntroTimeline, playerObjId);
                }
                else if (!os.UseEmote && os.ActionTimelineId > 0)
                {
                    character->Timeline.BaseOverride = (ushort)os.ActionTimelineId;
                }

                state.AnimPlayed = true;
                state.LastUseEmote = os.UseEmote;
                state.LastEmoteId = os.EmoteId;
                state.LastActionTimelineId = os.ActionTimelineId;
            }

            // Lock facing: track player's head bone
            if (os.LockFacing)
            {
                if (!headResolved)
                {
                    var player = Core.Services.ObjectTable.LocalPlayer;
                    headPos = player != null ? GetBoneWorldPos(player.Address, "j_kao") : null;
                    headResolved = true;
                }
                var faceTarget = headPos ?? playerDeathPos;
                var gameObj = (GameObject*)state.Npc.BattleChara;
                var npcPos = new Vector3(gameObj->Position.X, gameObj->Position.Y, gameObj->Position.Z);
                movementBlockHook.SetApproachPosition(gameObj, npcPos.X, npcPos.Y, npcPos.Z);
                var toHead = faceTarget - npcPos;
                if (toHead.LengthSquared() > 0.001f)
                    movementBlockHook.SetApproachRotation(gameObj, MathF.Atan2(toHead.X, toHead.Z));
            }

            otherNpcStates[ni] = state;
        }
    }

    /// <summary>
    /// Player-driven movement for the grabber: move in the camera-relative input
    /// direction, snap to the floor, face the movement direction, and play the walk
    /// animation. When stopped, hold position and restore the stage's carry pose.
    /// </summary>
    private void TickGrabberControl(VictorySequenceStage stage, GameObject* gameObj, float dt)
    {
        var character = (Character*)gameObj;
        var curPos = new Vector3(gameObj->Position.X, gameObj->Position.Y, gameObj->Position.Z);
        var moveAxis = ReadMoveInputAxis();

        if (moveAxis != Vector2.Zero)
        {
            if (!grabberMoving)
            {
                emotePlayer.ResetEmote(character);
                if (character->Mode != CharacterModes.Dead)
                    character->SetMode(CharacterModes.Normal, 0);
                grabberVisualState.Kind = ActorVisualStateKind.None;
                grabberControlYaw = GetCameraYaw();
            }

            var moveDir = AxisToWorldDir(moveAxis, grabberControlYaw);
            var speed = config.GrabberControlSpeed > 0f ? config.GrabberControlSpeed : 2.5f;
            var newPos = curPos + moveDir * speed * dt;
            newPos = SnapGrabberToFloor(newPos);
            movementBlockHook.SetApproachPosition(gameObj, newPos.X, newPos.Y, newPos.Z);
            movementBlockHook.SetApproachRotation(gameObj, MathF.Atan2(moveDir.X, moveDir.Z));
            ActorVisualStateController.ApplyMoving(character, grabberVisualState, dt);
            grabberMoving = true;
        }
        else
        {
            movementBlockHook.SetApproachPosition(gameObj, curPos.X, curPos.Y, curPos.Z);
            if (grabberMoving)
            {
                // Just stopped — drop the walk anim and let the stage carry pose replay.
                ActorVisualStateController.ClearMovement(character, grabberVisualState);
                emotePlayer.ResetEmote(character);
                stageAnimPlayed = false;
                grabberMoving = false;
            }
        }
    }

    /// <summary>Camera-relative planar move direction from movement input (0 if idle).</summary>
    private Vector2 ReadMoveInputAxis()
    {
        var input = ReadKeyboardMoveAxis();

        if (input == Vector2.Zero) return Vector2.Zero;
        if (input.LengthSquared() > 1f)
            input = Vector2.Normalize(input);

        return input;
    }

    private static float GetCameraYaw()
    {
        float yaw = 0f;
        var camMgr = GameCameraManager.Instance();
        if (camMgr != null && camMgr->Camera != null)
            yaw = camMgr->Camera->DirH;
        return yaw;
    }

    private static Vector3 AxisToWorldDir(Vector2 input, float yaw)
    {
        var camFwd = new Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
        var camRight = new Vector3(-camFwd.Z, 0f, camFwd.X);
        var dir = camFwd * input.Y + camRight * input.X;
        return dir.LengthSquared() < 1e-6f ? Vector3.Zero : Vector3.Normalize(dir);
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0f;
        return delta.Length();
    }

    private static Vector2 ReadKeyboardMoveAxis()
    {
        var fw = GameFramework.Instance();
        if (fw == null)
            return Vector2.Zero;

        var keys = fw->KeyboardInputs;
        float fwd = (IsKeyDown(keys, SeVirtualKey.W) ? 1f : 0f)
                  - (IsKeyDown(keys, SeVirtualKey.S) ? 1f : 0f);
        float strafe = (IsKeyDown(keys, SeVirtualKey.D) ? 1f : 0f)
                     - (IsKeyDown(keys, SeVirtualKey.A) ? 1f : 0f);
        return new Vector2(strafe, fwd);
    }

    private static bool IsKeyDown(KeyboardInputData keys, SeVirtualKey key)
        => keys.KeyState[(int)key].HasFlag(KeyStateFlags.Down);

    /// <summary>Floor-snap the grabber's destination via vnavmesh, falling back to a raycast.</summary>
    private Vector3 SnapGrabberToFloor(Vector3 pos)
    {
        if (vnavmeshIpc != null)
        {
            vnavmeshIpc.RefreshStatus();
            if (vnavmeshIpc.CanPathfind)
            {
                var f = vnavmeshIpc.PointOnFloor(pos, false, 5f);
                if (f != null)
                    return new Vector3(pos.X, f.Value.Y, pos.Z);
            }
        }

        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(pos.X, pos.Y + 2f, pos.Z),
                new Vector3(0, -1, 0), out var hit, 50f))
            return new Vector3(pos.X, hit.Point.Y, pos.Z);

        return pos;
    }

    private void ResolveBoneIndices(VictorySequenceStage stage)
    {
        npcHandBoneIdx = -1;
        playerNeckBoneIdx = -1;

        if (cinematicNpc?.BattleChara == null) return;
        var npcSkel = boneService.TryGetSkeleton(cinematicNpc.Address);
        if (npcSkel != null)
            npcHandBoneIdx = boneService.ResolveBoneIndex(npcSkel.Value, stage.NpcBoneName);

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null)
        {
            var playerSkel = boneService.TryGetSkeleton(player.Address);
            if (playerSkel != null)
                playerNeckBoneIdx = boneService.ResolveBoneIndex(playerSkel.Value, stage.PlayerBoneName);
        }

        log.Info($"VictorySequence: Grab bones resolved — NPC '{stage.NpcBoneName}'={npcHandBoneIdx}, Player '{stage.PlayerBoneName}'={playerNeckBoneIdx}");
    }

    /// <summary>
    /// Read any character's bone world position from its current skeleton.
    /// </summary>
    private Vector3? GetBoneWorldPos(nint characterAddress, string boneName, Vector3 localOffset = default)
    {
        if (characterAddress == nint.Zero) return null;

        var skel = boneService.TryGetSkeleton(characterAddress);
        if (skel == null) return null;
        var ns = skel.Value;

        var idx = boneService.ResolveBoneIndex(ns, boneName);
        if (idx < 0 || idx >= ns.BoneCount) return null;

        var npcSkeleton = ns.CharBase->Skeleton;
        if (npcSkeleton == null) return null;

        var nSkelPos = new Vector3(
            npcSkeleton->Transform.Position.X,
            npcSkeleton->Transform.Position.Y,
            npcSkeleton->Transform.Position.Z);
        var nSkelRot = new Quaternion(
            npcSkeleton->Transform.Rotation.X,
            npcSkeleton->Transform.Rotation.Y,
            npcSkeleton->Transform.Rotation.Z,
            npcSkeleton->Transform.Rotation.W);

        ref var mt = ref ns.Pose->ModelPose.Data[idx];
        var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
        var worldPos = nSkelPos + Vector3.Transform(modelPos, nSkelRot);

        // Apply caller-supplied offset in the bone's local axes (e.g., shift the
        // grab attach from wrist toward fingertips). Bone's world rotation is
        // the skeleton's world rotation composed with its model-space rotation.
        if (localOffset != Vector3.Zero)
        {
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            var boneWorldRot = nSkelRot * modelRot;
            worldPos += Vector3.Transform(localOffset, boneWorldRot);
        }
        return worldPos;
    }

    private void OnRenderFrame()
    {
        if (!isActive || cinematicNpc?.BattleChara == null) return;

        try
        {
            var stages = config.VictorySequenceStages;
            if (currentStageIndex < 0 || currentStageIndex >= stages.Count) return;
            var stage = stages[currentStageIndex];

            // Apply shoulder rotation FIRST. The rotation writes propagate to
            // hand/finger bones in ModelPose, so when we read the hand world
            // position below it reflects the new rotated location. Without
            // this ordering, BEPU gets the animation-default hand position,
            // pulls the victim's neck there, and then our rotation swings the
            // hand off into empty air — producing the "grabbing on air" bug.
            if (stage.ShoulderRotationEnabled && stage.GrabEnabled)
            {
                ApplyShoulderRotation(stage);
            }

            // Update BEPU2 grab target each render frame. This now sees the
            // post-rotation hand position (when shoulder override is on).
            if (grabActive)
            {
                var grabOffset = new Vector3(stage.GrabOffsetX, stage.GrabOffsetY, stage.GrabOffsetZ);
                var npcHandWorld = GetBoneWorldPos(cinematicNpc!.Address, stage.NpcBoneName, grabOffset);
                if (npcHandWorld != null)
                    ragdollController.UpdateGrabTarget(npcHandWorld.Value);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "VictorySequence: Error in render frame");
        }
    }

    /// <summary>
    /// Apply a manual delta rotation to the grabber's shoulder/upper-arm bone
    /// in ModelPose. BoneTransformService.ApplyRotationDeltas handles the
    /// descendant propagation so elbow/forearm/wrist/hand/fingers rotate with
    /// the shoulder — same approach Customize+ uses.
    /// </summary>
    private void ApplyShoulderRotation(VictorySequenceStage stage)
    {
        if (cinematicNpc?.BattleChara == null) return;
        if (stage.ShoulderPitch == 0f && stage.ShoulderYaw == 0f && stage.ShoulderRoll == 0f)
            return;

        var skel = boneService.TryGetSkeleton(cinematicNpc.Address);
        if (skel == null) return;

        var shoulderIdx = boneService.ResolveBoneIndex(skel.Value, stage.ShoulderBoneName);
        if (shoulderIdx < 0) return;

        const float Deg2Rad = MathF.PI / 180f;
        var delta = Quaternion.CreateFromYawPitchRoll(
            stage.ShoulderYaw * Deg2Rad,
            stage.ShoulderPitch * Deg2Rad,
            stage.ShoulderRoll * Deg2Rad);

        var deltas = new Dictionary<int, Quaternion> { [shoulderIdx] = delta };
        boneService.ApplyRotationDeltas(skel.Value, deltas);
    }

    public void Stop()
    {
        if (!isActive) return;

        // Restore player Y position
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var playerObj = (GameObject*)player.Address;
                playerObj->Position.Y = playerDeathPos.Y;
            }
        }
        catch { }

        boneService.OnRenderFrame -= OnRenderFrame;

        // Remove BEPU2 grab constraint (NPC collision restored internally by RagdollController)
        if (grabActive)
        {
            ragdollController.RemoveGrabConstraint();
            grabActive = false;
        }

        grabberMoving = false;
        grabberVisualState.Kind = ActorVisualStateKind.None;

        if (cinematicNpc?.BattleChara != null)
        {
            // Restore NPC to original position
            var npcObj = (GameObject*)cinematicNpc.BattleChara;
            movementBlockHook.SetApproachPosition(npcObj, npcOriginalPos.X, npcOriginalPos.Y, npcOriginalPos.Z);
            movementBlockHook.RemoveApproachNpc(cinematicNpc.Address);
            ActorVisualStateController.ClearMovement((Character*)cinematicNpc.BattleChara, grabberVisualState);
            emotePlayer.ResetEmote((Character*)cinematicNpc.BattleChara);
        }

        // Reset emotes and remove approach registration for other NPCs
        foreach (var state in otherNpcStates)
        {
            if (state.Npc.BattleChara != null)
            {
                emotePlayer.ResetEmote((Character*)state.Npc.BattleChara);
                movementBlockHook.RemoveApproachNpc(state.Npc.Address);
            }
        }
        otherNpcStates.Clear();

        isActive = false;
        cinematicNpc = null;
        currentStageIndex = -1;
        npcHandBoneIdx = -1;
        playerNeckBoneIdx = -1;

        log.Info("VictorySequence: Stopped");
    }

    public void Dispose()
    {
        Stop();
    }

    private class ApproachPathState
    {
        public List<Vector3> Waypoints { get; set; } = new();
        public int WaypointIndex { get; set; }
        public Vector3 RequestedTarget { get; set; }
        public Vector3 PendingTarget { get; set; }
        public Task<List<Vector3>>? PendingPath { get; set; }
        public float RepathTimer { get; set; }
    }
}
