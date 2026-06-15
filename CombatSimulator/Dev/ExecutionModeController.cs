using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Dev;

/// <summary>
/// Hidden mode: ragdoll stays active but is held upright via spine/pelvis physics
/// constraints while the primary NPC continues to play an attack animation.
/// Arms, head, and hands remain fully dynamic and react to NPC collisions.
/// </summary>
public unsafe class ExecutionModeController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly EmoteTimelinePlayer emotePlayer;
    private readonly RagdollController ragdollController;
    private readonly IPluginLog log;

    private bool isActive;
    private SimulatedNpc? primaryNpc;
    private ushort savedTimelineOverride;

    public bool IsActive => isActive;

    public ExecutionModeController(
        BoneTransformService boneService,
        EmoteTimelinePlayer emotePlayer,
        RagdollController ragdollController,
        IPluginLog log)
    {
        this.boneService = boneService;
        this.emotePlayer = emotePlayer;
        this.ragdollController = ragdollController;
        this.log = log;
    }

    /// <summary>
    /// Attempt to start execution mode. Requires an active ragdoll and at least one alive
    /// NPC in the selector list. The NPC's current animation is preserved unless
    /// <paramref name="attackTimelineId"/> is non-zero, in which case it is looped.
    /// </summary>
    public bool TryStart(IReadOnlyList<SimulatedNpc> npcs, ushort attackTimelineId = 0)
    {
        if (isActive) return false;
        if (!ragdollController.IsActive)
        {
            log.Warning("ExecutionMode: ragdoll not active, cannot start");
            return false;
        }

        // Find first alive NPC with a valid BattleChara pointer.
        SimulatedNpc? candidate = null;
        foreach (var npc in npcs)
        {
            if (npc.State.IsAlive && npc.BattleChara != null)
            {
                candidate = npc;
                break;
            }
        }
        if (candidate == null)
        {
            log.Warning("ExecutionMode: no alive NPC found");
            return false;
        }

        // Resolve player pelvis world position from their skeleton.
        // Falls back to character root + standing hip height if skeleton unavailable.
        var pelvisPos = ResolvePelvisWorldPos();
        if (pelvisPos == null)
        {
            log.Warning("ExecutionMode: could not resolve player pelvis position");
            return false;
        }

        // Upright orientation: world identity (Y-up, no yaw bias).
        // Using identity keeps the character facing their original death direction.
        var uprightRot = Quaternion.Identity;

        if (!ragdollController.CreateStandingSupport(pelvisPos.Value, uprightRot))
        {
            log.Warning("ExecutionMode: CreateStandingSupport failed");
            return false;
        }

        primaryNpc = candidate;

        // Optionally override the NPC's animation to a specific attack loop.
        if (attackTimelineId > 0 && primaryNpc.BattleChara != null)
        {
            var character = (Character*)primaryNpc.BattleChara;
            savedTimelineOverride = character->Timeline.BaseOverride;
            character->Timeline.BaseOverride = attackTimelineId;
            log.Info($"ExecutionMode: NPC '{primaryNpc.Name}' attack timeline set to {attackTimelineId}");
        }
        else
        {
            savedTimelineOverride = 0;
        }

        isActive = true;
        log.Info($"ExecutionMode: started — NPC '{primaryNpc.Name}', pelvis=({pelvisPos.Value.X:F2},{pelvisPos.Value.Y:F2},{pelvisPos.Value.Z:F2})");
        return true;
    }

    public void Tick(float deltaTime)
    {
        if (!isActive) return;

        // Auto-stop if ragdoll deactivated externally.
        if (!ragdollController.IsActive)
        {
            StopInternal(restoreNpc: true);
            return;
        }

        // Auto-stop if the NPC pointer becomes invalid.
        if (primaryNpc?.BattleChara == null)
        {
            StopInternal(restoreNpc: false);
            return;
        }
    }

    /// <summary>Stop execution mode and let the ragdoll fall naturally.</summary>
    public void Stop()
    {
        if (!isActive) return;
        StopInternal(restoreNpc: true);
    }

    private void StopInternal(bool restoreNpc)
    {
        ragdollController.RemoveStandingSupport();

        if (restoreNpc && primaryNpc?.BattleChara != null)
        {
            var character = (Character*)primaryNpc.BattleChara;

            // Restore animation state.
            if (savedTimelineOverride > 0)
                character->Timeline.BaseOverride = savedTimelineOverride;
            else
                emotePlayer.ResetEmote(character);

            // Return NPC to normal combat stance.
            character->SetMode(CharacterModes.Normal, 0);
        }

        log.Info($"ExecutionMode: stopped (NPC='{primaryNpc?.Name ?? "none"}')");
        primaryNpc = null;
        savedTimelineOverride = 0;
        isActive = false;
    }

    /// <summary>
    /// Read the player's j_kosi (pelvis) world position from their current skeleton.
    /// Falls back to character root position + approximate hip height.
    /// </summary>
    private Vector3? ResolvePelvisWorldPos()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return null;

        var skel = boneService.TryGetSkeleton(player.Address);
        if (skel != null)
        {
            var ns = skel.Value;
            var pelvisIdx = boneService.ResolveBoneIndex(ns, "j_kosi");
            if (pelvisIdx >= 0 && pelvisIdx < ns.BoneCount && ns.CharBase->Skeleton != null)
            {
                var sk = ns.CharBase->Skeleton;
                var skelPos = new Vector3(sk->Transform.Position.X, sk->Transform.Position.Y, sk->Transform.Position.Z);
                var skelRot = new Quaternion(sk->Transform.Rotation.X, sk->Transform.Rotation.Y, sk->Transform.Rotation.Z, sk->Transform.Rotation.W);
                ref var mt = ref ns.Pose->ModelPose.Data[pelvisIdx];
                var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
                return skelPos + Vector3.Transform(modelPos, skelRot);
            }
        }

        // Fallback: character root + standing hip height.
        var go = (GameObject*)player.Address;
        return new Vector3(go->Position.X, go->Position.Y + 0.9f, go->Position.Z);
    }

    public void Dispose()
    {
        if (isActive) StopInternal(restoreNpc: true);
    }
}
