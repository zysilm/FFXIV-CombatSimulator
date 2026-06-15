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
/// constraints while the primary NPC plays an attack animation.
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
    /// Start execution mode. Lifts the ragdoll to standing height and optionally
    /// overrides the primary NPC's animation to a looping attack timeline.
    /// </summary>
    /// <param name="npcs">Active NPC list — first alive entry is used.</param>
    /// <param name="standingHeight">World-space Y offset above the character's death
    /// position to place the pelvis. ~0.92 matches a standing FFXIV character.</param>
    /// <param name="attackTimelineId">If non-zero, set on the NPC via BaseOverride.</param>
    public bool TryStart(IReadOnlyList<SimulatedNpc> npcs, float standingHeight = 0.92f, ushort attackTimelineId = 0)
    {
        if (isActive) return false;
        if (!ragdollController.IsActive)
        {
            log.Warning("ExecutionMode: ragdoll not active");
            return false;
        }

        // Find first alive NPC.
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

        // Target pelvis at death-spot XZ + caller-specified standing height.
        // We deliberately do NOT read the current ragdoll skeleton position here —
        // after a death fall the pelvis is at ground level, using it as the target
        // would just pin the character to the floor rather than lifting them up.
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            log.Warning("ExecutionMode: no local player");
            return false;
        }
        var go = (GameObject*)player.Address;
        var pelvisTarget = new Vector3(go->Position.X, go->Position.Y + standingHeight, go->Position.Z);

        // Identity upright: Y-up, facing the death yaw (no yaw correction for now).
        var uprightRot = Quaternion.Identity;

        if (!ragdollController.CreateStandingSupport(pelvisTarget, uprightRot))
        {
            log.Warning("ExecutionMode: CreateStandingSupport failed");
            return false;
        }

        primaryNpc = candidate;

        // Override NPC animation to a specific attack loop if requested.
        if (attackTimelineId > 0 && primaryNpc.BattleChara != null)
        {
            var character = (Character*)primaryNpc.BattleChara;
            savedTimelineOverride = character->Timeline.BaseOverride;
            character->Timeline.BaseOverride = attackTimelineId;
            log.Info($"ExecutionMode: NPC '{primaryNpc.Name}' timeline → {attackTimelineId}");
        }
        else
        {
            savedTimelineOverride = 0;
        }

        isActive = true;
        log.Info($"ExecutionMode: started — NPC '{primaryNpc.Name}', pelvisTarget=({pelvisTarget.X:F2},{pelvisTarget.Y:F2},{pelvisTarget.Z:F2}), timeline={attackTimelineId}");
        return true;
    }

    public void Tick(float deltaTime)
    {
        if (!isActive) return;

        if (!ragdollController.IsActive)
        {
            StopInternal(restoreNpc: true);
            return;
        }

        if (primaryNpc?.BattleChara == null)
        {
            StopInternal(restoreNpc: false);
            return;
        }
    }

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

            if (savedTimelineOverride > 0)
                character->Timeline.BaseOverride = savedTimelineOverride;
            else
                emotePlayer.ResetEmote(character);

            character->SetMode(CharacterModes.Normal, 0);
        }

        log.Info($"ExecutionMode: stopped (NPC='{primaryNpc?.Name ?? "none"}')");
        primaryNpc = null;
        savedTimelineOverride = 0;
        isActive = false;
    }

    public void Dispose()
    {
        if (isActive) StopInternal(restoreNpc: true);
    }
}
