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
        IClientState clientState,
        Configuration config,
        IPluginLog log)
    {
        this.boneService = boneService;
        this.emotePlayer = emotePlayer;
        this.movementBlockHook = movementBlockHook;
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
            if (elapsed >= stages[i].StartTime && elapsed < stages[i].EndTime)
            {
                newStageIdx = i;
                break;
            }
        }

        // Past all stages → stop
        if (newStageIdx == -1 && stages.Count > 0 && elapsed >= stages[^1].EndTime)
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

        // Update grab state
        grabActive = stage.GrabEnabled;
        if (grabActive && (npcHandBoneIdx < 0 || playerNeckBoneIdx < 0))
            ResolveBoneIndices(stage);
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

    private int grabFrameCount;

    private void OnRenderFrame()
    {
        if (!isActive || !grabActive || cinematicNpc?.BattleChara == null) return;
        if (npcHandBoneIdx < 0 || playerNeckBoneIdx < 0) return;

        try
        {
            var player = clientState.LocalPlayer;
            if (player == null) return;

            // Read NPC hand bone world position (from current animation frame)
            var npcSkel = boneService.TryGetSkeleton(cinematicNpc.Address);
            if (npcSkel == null) return;
            var ns = npcSkel.Value;
            if (npcHandBoneIdx >= ns.BoneCount) return;

            var npcSkeleton = ns.CharBase->Skeleton;
            if (npcSkeleton == null) return;
            var nSkelPos = new Vector3(
                npcSkeleton->Transform.Position.X,
                npcSkeleton->Transform.Position.Y,
                npcSkeleton->Transform.Position.Z);
            var nSkelRot = new Quaternion(
                npcSkeleton->Transform.Rotation.X,
                npcSkeleton->Transform.Rotation.Y,
                npcSkeleton->Transform.Rotation.Z,
                npcSkeleton->Transform.Rotation.W);

            ref var npcHandMt = ref ns.Pose->ModelPose.Data[npcHandBoneIdx];
            var npcHandModel = new Vector3(npcHandMt.Translation.X, npcHandMt.Translation.Y, npcHandMt.Translation.Z);
            var npcHandWorld = nSkelPos + Vector3.Transform(npcHandModel, nSkelRot);

            // Read player skeleton
            var playerSkel = boneService.TryGetSkeleton(player.Address);
            if (playerSkel == null) return;
            var ps = playerSkel.Value;
            if (playerNeckBoneIdx >= ps.BoneCount) return;

            var playerSkeleton = ps.CharBase->Skeleton;
            if (playerSkeleton == null) return;
            var pSkelPos = new Vector3(
                playerSkeleton->Transform.Position.X,
                playerSkeleton->Transform.Position.Y,
                playerSkeleton->Transform.Position.Z);
            var pSkelRot = new Quaternion(
                playerSkeleton->Transform.Rotation.X,
                playerSkeleton->Transform.Rotation.Y,
                playerSkeleton->Transform.Rotation.Z,
                playerSkeleton->Transform.Rotation.W);
            var pSkelRotInv = Quaternion.Inverse(pSkelRot);

            // Read player bone current position and rotation in model space
            ref var playerBoneMt = ref ps.Pose->ModelPose.Data[playerNeckBoneIdx];
            var boneModelPos = new Vector3(playerBoneMt.Translation.X, playerBoneMt.Translation.Y, playerBoneMt.Translation.Z);
            var boneModelRot = new Quaternion(playerBoneMt.Rotation.X, playerBoneMt.Rotation.Y, playerBoneMt.Rotation.Z, playerBoneMt.Rotation.W);

            // Player bone world position
            var boneWorldPos = pSkelPos + Vector3.Transform(boneModelPos, pSkelRot);

            // Direction from player bone to NPC hand (in model space)
            var dirToHandWorld = npcHandWorld - boneWorldPos;
            var dirLen = dirToHandWorld.Length();
            if (dirLen < 0.001f) return;
            dirToHandWorld /= dirLen;

            // Convert direction to model space
            var dirToHandModel = Vector3.Transform(dirToHandWorld, pSkelRotInv);

            // Current bone "up" direction in model space (Y axis of bone rotation)
            var boneUp = Vector3.Transform(Vector3.UnitY, boneModelRot);

            // Compute rotation that tilts bone toward the NPC hand
            var dot = Vector3.Dot(boneUp, dirToHandModel);
            if (MathF.Abs(dot) > 0.999f) return; // already aligned or opposite

            var axis = Vector3.Cross(boneUp, dirToHandModel);
            var axisLen = axis.Length();
            if (axisLen < 0.0001f) return;
            axis /= axisLen;

            var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            // Clamp rotation to prevent extreme twisting (max ~45 degrees)
            angle = MathF.Min(angle, MathF.PI / 4f);

            var rotDelta = Quaternion.CreateFromAxisAngle(axis, angle);
            var newRot = Quaternion.Normalize(rotDelta * boneModelRot);

            // Write ROTATION ONLY — no position change, no mesh deformation
            playerBoneMt.Rotation.X = newRot.X;
            playerBoneMt.Rotation.Y = newRot.Y;
            playerBoneMt.Rotation.Z = newRot.Z;
            playerBoneMt.Rotation.W = newRot.W;

            // Propagate to descendants (face, hair follow neck rotation)
            // Apply same rotation delta to all child bones in this partial skeleton
            var parentCount = Math.Min(ps.BoneCount, ps.ParentCount);
            for (int i = 0; i < parentCount; i++)
            {
                if (i == playerNeckBoneIdx) continue;
                var parentIdx = ps.HavokSkeleton->ParentIndices[i];
                if (parentIdx != playerNeckBoneIdx) continue;

                ref var childMt = ref ps.Pose->ModelPose.Data[i];
                // Rotate child position around parent bone
                var childPos = new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z);
                var relPos = childPos - boneModelPos;
                var newChildPos = boneModelPos + Vector3.Transform(relPos, rotDelta);
                childMt.Translation.X = newChildPos.X;
                childMt.Translation.Y = newChildPos.Y;
                childMt.Translation.Z = newChildPos.Z;
                // Rotate child orientation
                var childRot = new Quaternion(childMt.Rotation.X, childMt.Rotation.Y, childMt.Rotation.Z, childMt.Rotation.W);
                var newChildRot = Quaternion.Normalize(rotDelta * childRot);
                childMt.Rotation.X = newChildRot.X;
                childMt.Rotation.Y = newChildRot.Y;
                childMt.Rotation.Z = newChildRot.Z;
                childMt.Rotation.W = newChildRot.W;
            }

            grabFrameCount++;
            if (grabFrameCount <= 3 || grabFrameCount % 60 == 0)
            {
                log.Info($"[Grab F{grabFrameCount}] NPC hand=({npcHandWorld.X:F3},{npcHandWorld.Y:F3},{npcHandWorld.Z:F3}) " +
                         $"bone angle={angle * 180 / MathF.PI:F1}°");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "VictorySequence: Error in grab render frame");
        }
    }

    public void Stop()
    {
        if (!isActive) return;

        boneService.OnRenderFrame -= OnRenderFrame;

        if (cinematicNpc?.BattleChara != null)
        {
            movementBlockHook.RemoveApproachNpc(cinematicNpc.Address);
            emotePlayer.ResetEmote((Character*)cinematicNpc.BattleChara);
        }

        isActive = false;
        cinematicNpc = null;
        currentStageIndex = -1;
        grabActive = false;
        grabFrameCount = 0;
        npcHandBoneIdx = -1;
        playerNeckBoneIdx = -1;

        log.Info("VictorySequence: Stopped");
    }

    public void Dispose()
    {
        Stop();
    }
}
