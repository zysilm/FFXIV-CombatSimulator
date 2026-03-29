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

        // Interpolate NPC distance
        var duration = stage.EndTime - stage.StartTime;
        var progress = duration > 0.001f
            ? Math.Clamp((elapsed - stage.StartTime) / duration, 0f, 1f)
            : 1f;
        var dist = stage.StartDistance + (stage.EndDistance - stage.StartDistance) * progress;

        // Position along player's facing direction
        var facingDir = new Vector3(MathF.Sin(playerFacingAngle), 0, MathF.Cos(playerFacingAngle));
        var targetPos = playerDeathPos + facingDir * dist;
        targetPos.Y = playerDeathPos.Y + stage.HeightOffset;

        // Move NPC via approach bypass
        var gameObj = (GameObject*)cinematicNpc.BattleChara;
        movementBlockHook.SetApproachPosition(gameObj, targetPos.X, targetPos.Y, targetPos.Z);

        // Rotate NPC to face player
        var toPlayer = playerDeathPos - targetPos;
        var rotAngle = MathF.Atan2(toPlayer.X, toPlayer.Z);
        movementBlockHook.SetApproachRotation(gameObj, rotAngle);

        // Play animation on stage enter
        if (!stageAnimPlayed && stage.EmoteId > 0)
        {
            // Resolve timeline IDs from emote if not already set
            if (stage.AnimationTimelineId == 0 && stage.LoopTimelineId == 0)
            {
                try
                {
                    var emoteSheet = Core.Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
                    if (emoteSheet != null)
                    {
                        var emote = emoteSheet.GetRow(stage.EmoteId);
                        stage.LoopTimelineId = (ushort)emote.ActionTimeline[0].RowId;
                        stage.AnimationTimelineId = (ushort)emote.ActionTimeline[1].RowId;
                    }
                }
                catch { }
            }

            if (stage.AnimationTimelineId != 0 || stage.LoopTimelineId != 0)
            {
                var character = (Character*)cinematicNpc.BattleChara;
                if (stage.LoopTimelineId != 0)
                    emotePlayer.PlayLoopedEmote(character, stage.LoopTimelineId, stage.AnimationTimelineId, playerObjId);
                else
                    emotePlayer.PlayOneShot(character, stage.AnimationTimelineId);
                log.Info($"VictorySequence: Playing emote {stage.EmoteId} (intro={stage.AnimationTimelineId}, loop={stage.LoopTimelineId})");
            }
            stageAnimPlayed = true;
        }

        // Update grab state — use BEPU2 OneBodyLinearServo via ragdoll
        if (stage.GrabEnabled && !grabActive)
        {
            // Activate grab: create physics constraint on player's ragdoll bone
            ResolveBoneIndices(stage);
            if (npcHandBoneIdx >= 0 && ragdollController.IsActive)
            {
                var npcHandWorld = GetNpcBoneWorldPos(stage.NpcBoneName);
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
    /// Read an NPC bone's world position from its current animation frame.
    /// </summary>
    private Vector3? GetNpcBoneWorldPos(string boneName)
    {
        if (cinematicNpc?.BattleChara == null) return null;

        var npcSkel = boneService.TryGetSkeleton(cinematicNpc.Address);
        if (npcSkel == null) return null;
        var ns = npcSkel.Value;

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

            var npcHandWorld = GetNpcBoneWorldPos(stages[currentStageIndex].NpcBoneName);
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
