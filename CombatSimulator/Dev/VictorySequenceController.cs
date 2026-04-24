using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Dev;

public unsafe class VictorySequenceController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly EmoteTimelinePlayer emotePlayer;
    private readonly MovementBlockHook movementBlockHook;
    private readonly RagdollController ragdollController;
    private readonly IClientState clientState;
    private readonly ITargetManager targetManager;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Runtime state
    private bool isActive;
    private float elapsed;
    private SimulatedNpc? cinematicNpc;
    private Vector3 npcOriginalPos;
    private Vector3 stageStartPos;           // NPC's position at the start of current stage
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

    public bool IsActive => isActive;
    public nint CinematicNpcAddress => cinematicNpc?.Address ?? nint.Zero;
    public int CurrentStageIndex => currentStageIndex;

    public VictorySequenceController(
        BoneTransformService boneService,
        EmoteTimelinePlayer emotePlayer,
        MovementBlockHook movementBlockHook,
        RagdollController ragdollController,
        IClientState clientState,
        ITargetManager targetManager,
        Configuration config,
        IPluginLog log)
    {
        this.boneService = boneService;
        this.emotePlayer = emotePlayer;
        this.movementBlockHook = movementBlockHook;
        this.ragdollController = ragdollController;
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

        // Find the last targeted NPC (must be alive with valid BattleChara)
        SimulatedNpc? candidate = null;
        foreach (var npc in npcs)
        {
            if (npc.SimulatedEntityId == lastTargetedNpcId && npc.State.IsAlive && npc.BattleChara != null)
            {
                candidate = npc;
                break;
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
        var player = clientState.LocalPlayer;
        if (player == null)
            return (false, null);

        playerDeathPos = player.Position;
        var playerObj = (GameObject*)player.Address;
        playerFacingAngle = playerObj->Rotation;
        playerObjId = playerObj->GetGameObjectId().Id;

        // Lower the player's game position to ground level so the game's emote
        // height adjustment system sees the target as on the ground (L variant).
        // The player is dead — ragdoll controls the visual, not GameObject.Position.
        // Direct struct write bypasses MovementBlockHook.IsBlocking.
        playerObj->Position.Y -= 1.5f; // ~character standing height → ground level
        log.Info($"VictorySequence: Lowered player Y from {playerDeathPos.Y:F2} to {playerObj->Position.Y:F2}");

        // Save NPC's true original position (before any plugin manipulation)
        npcOriginalPos = cinematicNpc.SpawnPosition;

        // Capture NPC's current world position as the initial stage start point
        var npcObj = (GameObject*)cinematicNpc.BattleChara;
        stageStartPos = new Vector3(npcObj->Position.X, npcObj->Position.Y, npcObj->Position.Z);

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
        currentStageIndex = -1;
        stageAnimPlayed = false;
        grabActive = false;

        var otherCount = config.VictorySequenceOtherStages.Count;
        log.Info($"VictorySequence: Started with NPC '{cinematicNpc.Name}' (lastTarget={lastTargetedNpcId:X}), start={stageStartPos}, {config.VictorySequenceStages.Count} stages, {otherNpcStates.Count} other NPCs with {otherCount} stages");
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

        // Find current stage
        int newStageIdx = -1;
        for (int i = 0; i < stages.Count; i++)
        {
            var isInfinite = stages[i].EndTime < 0;
            if (elapsed >= stages[i].StartTime && (isInfinite || elapsed < stages[i].EndTime))
            {
                newStageIdx = i;
                break;
            }
        }

        // Past all stages → stop (but not if last stage is infinite)
        var lastEnd = stages.Count > 0 ? stages[^1].EndTime : 0;
        if (newStageIdx == -1 && stages.Count > 0 && lastEnd >= 0 && elapsed >= lastEnd)
        {
            log.Info("VictorySequence: All stages complete");
            Stop();
            return;
        }

        // Stage transition — snapshot NPC's current position as the new stage start
        if (newStageIdx != currentStageIndex && newStageIdx >= 0)
        {
            if (currentStageIndex >= 0 && cinematicNpc.BattleChara != null)
            {
                var obj = (GameObject*)cinematicNpc.BattleChara;
                stageStartPos = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
            }
            currentStageIndex = newStageIdx;
            stageAnimPlayed = false;
            log.Info($"VictorySequence: Entering stage {newStageIdx}, stageStart={stageStartPos}");
        }

        if (currentStageIndex < 0 || currentStageIndex >= stages.Count)
            return;

        var stage = stages[currentStageIndex];

        // Calculate target endpoint: EndDistance along player's facing direction
        var facingDir = new Vector3(MathF.Sin(playerFacingAngle), 0, MathF.Cos(playerFacingAngle));
        var endPos = playerDeathPos + facingDir * stage.EndDistance;
        endPos.Y = playerDeathPos.Y + stage.HeightOffset;

        // Lerp NPC from stage start position to the endpoint
        Vector3 targetPos;
        if (stage.KeepPosition)
        {
            targetPos = stageStartPos;
        }
        else if (stage.InfiniteWalk)
        {
            // Constant speed walk from stage start toward endpoint
            var timeInStage = elapsed - stage.StartTime;
            var toEnd = endPos - stageStartPos;
            var totalDist = toEnd.Length();
            if (totalDist > 0.001f)
            {
                var dir = toEnd / totalDist;
                var traveled = stage.WalkSpeed * timeInStage;
                targetPos = stageStartPos + dir * traveled;
            }
            else
            {
                targetPos = endPos;
            }
        }
        else
        {
            var duration = stage.EndTime - stage.StartTime;
            var progress = duration > 0.001f
                ? Math.Clamp((elapsed - stage.StartTime) / duration, 0f, 1f)
                : 1f;
            targetPos = Vector3.Lerp(stageStartPos, endPos, progress);
        }

        // Move NPC via approach bypass
        var gameObj = (GameObject*)cinematicNpc.BattleChara;
        movementBlockHook.SetApproachPosition(gameObj, targetPos.X, targetPos.Y, targetPos.Z);

        // Rotate NPC facing direction
        float rotAngle;
        if (stage.LockFacing)
        {
            // Track player's head bone (j_kao) in real time
            var player = clientState.LocalPlayer;
            var headPos = player != null ? GetBoneWorldPos(player.Address, "j_kao") : null;
            var faceTarget = headPos ?? playerDeathPos;
            var toHead = faceTarget - targetPos;
            rotAngle = MathF.Atan2(toHead.X, toHead.Z);
        }
        else if (stage.InfiniteWalk)
        {
            // Lock to initial approach direction (stage start → endpoint)
            var walkDir = endPos - stageStartPos;
            rotAngle = MathF.Atan2(walkDir.X, walkDir.Z);
        }
        else
        {
            // Recalculate each frame from NPC→player position (flips at negative distance)
            var toPlayer = playerDeathPos - targetPos;
            rotAngle = MathF.Atan2(toPlayer.X, toPlayer.Z);
        }
        movementBlockHook.SetApproachRotation(gameObj, rotAngle);

        // Play animation on stage enter, or re-play if user changed config live
        bool configChanged = stageAnimPlayed && (
            stage.UseEmote != lastPlayedUseEmote ||
            stage.EmoteId != lastPlayedEmoteId ||
            stage.ActionTimelineId != lastPlayedActionTimelineId);
        if (!stageAnimPlayed || configChanged)
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

        if (stage.GrabEnabled && !grabActive)
        {
            // Activate grab: create physics constraint on player's ragdoll bone
            ResolveBoneIndices(stage);
            if (npcHandBoneIdx >= 0 && ragdollController.IsActive)
            {
                var npcHandWorld = GetBoneWorldPos(cinematicNpc!.Address, stage.NpcBoneName);
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
        TickOtherNpcs();
    }

    private void TickOtherNpcs()
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

            var npcElapsed = elapsed - state.TimeOffset;
            if (npcElapsed < 0) continue; // not started yet

            // Find stage for this NPC's shifted time
            int newIdx = -1;
            for (int i = 0; i < otherStages.Count; i++)
            {
                var isInfinite = otherStages[i].EndTime < 0;
                var stageStart = otherStages[i].StartTime;
                var stageEnd = otherStages[i].EndTime;
                if (npcElapsed >= stageStart && (isInfinite || npcElapsed < stageEnd))
                {
                    newIdx = i;
                    break;
                }
            }

            if (newIdx != state.StageIndex && newIdx >= 0)
            {
                state.StageIndex = newIdx;
                state.AnimPlayed = false;
            }

            if (state.StageIndex < 0 || state.StageIndex >= otherStages.Count)
            {
                otherNpcStates[ni] = state;
                continue;
            }

            var os = otherStages[state.StageIndex];

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
                    var player = clientState.LocalPlayer;
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

    private void ResolveBoneIndices(VictorySequenceStage stage)
    {
        npcHandBoneIdx = -1;
        playerNeckBoneIdx = -1;

        if (cinematicNpc?.BattleChara == null) return;
        var npcSkel = boneService.TryGetSkeleton(cinematicNpc.Address);
        if (npcSkel != null)
            npcHandBoneIdx = boneService.ResolveBoneIndex(npcSkel.Value, stage.NpcBoneName);

        var player = clientState.LocalPlayer;
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
    private Vector3? GetBoneWorldPos(nint characterAddress, string boneName)
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
        return nSkelPos + Vector3.Transform(modelPos, nSkelRot);
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
                var npcHandWorld = GetBoneWorldPos(cinematicNpc!.Address, stage.NpcBoneName);
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
            var player = clientState.LocalPlayer;
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

        if (cinematicNpc?.BattleChara != null)
        {
            // Restore NPC to original position
            var npcObj = (GameObject*)cinematicNpc.BattleChara;
            movementBlockHook.SetApproachPosition(npcObj, npcOriginalPos.X, npcOriginalPos.Y, npcOriginalPos.Z);
            movementBlockHook.RemoveApproachNpc(cinematicNpc.Address);
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
}
