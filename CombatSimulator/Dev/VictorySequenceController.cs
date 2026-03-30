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
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Runtime state
    private bool isActive;
    private float elapsed;
    private SimulatedNpc? cinematicNpc;
    private int currentStageIndex = -1;
    private bool stageAnimPlayed;
    private uint lastPlayedEmoteId;
    private uint lastPlayedActionTimelineId;
    private bool lastPlayedUseEmote;
    private Vector3 playerDeathPos;
    private float playerFacingAngle;
    private ulong playerObjId;

    // Grab bone indices (resolved per stage)
    private bool grabActive;
    private int npcHandBoneIdx = -1;
    private int playerNeckBoneIdx = -1;

    public bool IsActive => isActive;

    public VictorySequenceController(
        BoneTransformService boneService,
        EmoteTimelinePlayer emotePlayer,
        MovementBlockHook movementBlockHook,
        RagdollController ragdollController,
        IClientState clientState,
        Configuration config,
        IPluginLog log)
    {
        this.boneService = boneService;
        this.emotePlayer = emotePlayer;
        this.movementBlockHook = movementBlockHook;
        this.ragdollController = ragdollController;
        this.clientState = clientState;
        this.config = config;
        this.log = log;
    }

    /// <summary>
    /// Try to start a cinematic victory sequence. Returns (started, cinematicNpc).
    /// If started, the caller should play normal victory emotes on ALL OTHER NPCs.
    /// </summary>
    public (bool Started, SimulatedNpc? CinematicNpc) TryStart(IReadOnlyList<SimulatedNpc> npcs)
    {
        if (!config.EnableVictorySequence || config.VictorySequenceStages.Count == 0)
            return (false, null);

        // Collect surviving NPCs
        var survivors = new List<SimulatedNpc>();
        foreach (var npc in npcs)
        {
            if (npc.State.IsAlive && npc.BattleChara != null)
                survivors.Add(npc);
        }
        if (survivors.Count == 0)
            return (false, null);

        // Pick random NPC for the cinematic
        cinematicNpc = survivors[Random.Shared.Next(survivors.Count)];

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

        // Register NPC for approach movement (bypass server overrides)
        movementBlockHook.AddApproachNpc(cinematicNpc.Address);

        // Subscribe to render frame for bone constraints
        boneService.OnRenderFrame += OnRenderFrame;

        isActive = true;
        elapsed = 0;
        currentStageIndex = -1;
        stageAnimPlayed = false;
        grabActive = false;

        log.Info($"VictorySequence: Started with NPC '{cinematicNpc.Name}', {config.VictorySequenceStages.Count} stages");
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

        // Stage transition
        if (newStageIdx != currentStageIndex && newStageIdx >= 0)
        {
            currentStageIndex = newStageIdx;
            stageAnimPlayed = false;
            log.Info($"VictorySequence: Entering stage {newStageIdx} (emote={stages[newStageIdx].EmoteId})");
        }

        if (currentStageIndex < 0 || currentStageIndex >= stages.Count)
            return;

        var stage = stages[currentStageIndex];

        // Calculate NPC distance
        float dist;
        if (stage.InfiniteWalk)
        {
            // Constant speed walk: distance decreases over time
            var timeInStage = elapsed - stage.StartTime;
            dist = stage.StartDistance - stage.WalkSpeed * timeInStage;
        }
        else
        {
            var duration = stage.EndTime - stage.StartTime;
            var progress = duration > 0.001f
                ? Math.Clamp((elapsed - stage.StartTime) / duration, 0f, 1f)
                : 1f;
            dist = stage.StartDistance + (stage.EndDistance - stage.StartDistance) * progress;
        }

        // Position along player's facing direction
        var facingDir = new Vector3(MathF.Sin(playerFacingAngle), 0, MathF.Cos(playerFacingAngle));
        var targetPos = playerDeathPos + facingDir * dist;
        targetPos.Y = playerDeathPos.Y + stage.HeightOffset;

        // Move NPC via approach bypass
        var gameObj = (GameObject*)cinematicNpc.BattleChara;
        movementBlockHook.SetApproachPosition(gameObj, targetPos.X, targetPos.Y, targetPos.Z);

        // Rotate NPC to face the player's head bone (j_kao) for realistic eye contact.
        // Lock Facing only prevents the 180° flip — still tracks the head.
        float rotAngle;
        var player = clientState.LocalPlayer;
        var headPos = player != null ? GetBoneWorldPos(player.Address, "j_kao") : null;
        var faceTarget = headPos ?? playerDeathPos;
        var toHead = faceTarget - targetPos;
        rotAngle = MathF.Atan2(toHead.X, toHead.Z);

        if (stage.LockFacing || stage.InfiniteWalk)
        {
            // Prevent 180° flip: if NPC would face away from approach direction, clamp
            var approachAngle = MathF.Atan2(-facingDir.X, -facingDir.Z);
            var diff = rotAngle - approachAngle;
            while (diff > MathF.PI) diff -= 2 * MathF.PI;
            while (diff < -MathF.PI) diff += 2 * MathF.PI;
            // Allow up to 90° deviation from approach direction for head tracking
            if (MathF.Abs(diff) > MathF.PI / 2f)
                rotAngle = approachAngle; // snap back to approach direction
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
        if (stage.GrabEnabled && !grabActive)
        {
            // Activate grab: create physics constraint on player's ragdoll bone
            ResolveBoneIndices(stage);
            if (npcHandBoneIdx >= 0 && ragdollController.IsActive)
            {
                var npcHandWorld = GetBoneWorldPos(cinematicNpc!.Address,stage.NpcBoneName);
                if (npcHandWorld != null)
                {
                    grabActive = ragdollController.CreateGrabConstraint(
                        stage.PlayerBoneName, npcHandWorld.Value,
                        stage.GrabForce, stage.GrabSpeed, stage.GrabSpringFreq);
                    if (grabActive)
                        log.Info("VictorySequence: BEPU2 grab constraint activated");
                }
            }
        }
        else if (!stage.GrabEnabled && grabActive)
        {
            // Deactivate grab
            ragdollController.RemoveGrabConstraint();
            grabActive = false;
            log.Info("VictorySequence: Grab constraint deactivated");
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
        // Update BEPU2 grab target each render frame with NPC hand world position
        if (!isActive || !grabActive || cinematicNpc?.BattleChara == null) return;

        try
        {
            var stages = config.VictorySequenceStages;
            if (currentStageIndex < 0 || currentStageIndex >= stages.Count) return;

            var npcHandWorld = GetBoneWorldPos(cinematicNpc!.Address,stages[currentStageIndex].NpcBoneName);
            if (npcHandWorld != null)
                ragdollController.UpdateGrabTarget(npcHandWorld.Value);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "VictorySequence: Error updating grab target");
        }
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

        // Remove BEPU2 grab constraint
        if (grabActive)
        {
            ragdollController.RemoveGrabConstraint();
            grabActive = false;
        }

        if (cinematicNpc?.BattleChara != null)
        {
            movementBlockHook.RemoveApproachNpc(cinematicNpc.Address);
            emotePlayer.ResetEmote((Character*)cinematicNpc.BattleChara);
        }

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
