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
/// constraints while the primary NPC continues to perform melee attacks.
/// Arms, head, and hands remain fully dynamic and react to NPC collisions.
/// </summary>
public unsafe class ExecutionModeController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly EmoteTimelinePlayer emotePlayer;
    private readonly RagdollController ragdollController;
    private readonly AnimationController animationController;
    private readonly IPluginLog log;

    private bool isActive;
    private SimulatedNpc? primaryNpc;
    private float attackTimer;

    // Configurable attack interval (seconds between melee swings).
    public float AttackInterval { get; set; } = 2.5f;

    public bool IsActive => isActive;

    public ExecutionModeController(
        BoneTransformService boneService,
        EmoteTimelinePlayer emotePlayer,
        RagdollController ragdollController,
        AnimationController animationController,
        IPluginLog log)
    {
        this.boneService = boneService;
        this.emotePlayer = emotePlayer;
        this.ragdollController = ragdollController;
        this.animationController = animationController;
        this.log = log;
    }

    /// <summary>
    /// Start the mode. Lifts the ragdoll to standing height at the anchor bone and
    /// begins driving the primary NPC to perform periodic melee attack animations.
    /// </summary>
    /// <param name="npcs">Active NPC list — first alive entry is used.</param>
    /// <param name="anchorBone">Ragdoll bone to pin (e.g. "j_kosi", "j_sebo_c").</param>
    /// <param name="standingHeight">Y offset above the character's death position for the anchor bone.</param>
    public bool TryStart(IReadOnlyList<SimulatedNpc> npcs, string anchorBone = "j_kosi", float standingHeight = 0.92f)
    {
        if (isActive) return false;
        if (!ragdollController.IsActive)
        {
            log.Warning("ExecutionMode: ragdoll not active");
            return false;
        }

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

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            log.Warning("ExecutionMode: no local player");
            return false;
        }

        var go = (GameObject*)player.Address;
        var anchorTarget = new Vector3(go->Position.X, go->Position.Y + standingHeight, go->Position.Z);

        if (!ragdollController.CreateStandingSupport(anchorTarget, Quaternion.Identity, anchorBone))
        {
            log.Warning("ExecutionMode: CreateStandingSupport failed");
            return false;
        }

        primaryNpc = candidate;

        // Put NPC into battle stance (weapon drawn, combat animation set).
        animationController.SetBattleStance(primaryNpc);

        // Fire first attack immediately on start.
        attackTimer = 0f;

        isActive = true;
        log.Info($"ExecutionMode: started — NPC '{primaryNpc.Name}', anchor={anchorBone}, height={standingHeight:F2}");
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

        // Periodically trigger melee auto-attack animation on the NPC.
        attackTimer -= deltaTime;
        if (attackTimer <= 0f)
        {
            var targetId = Core.Services.ObjectTable.LocalPlayer?.EntityId ?? 0;
            if (targetId != 0)
                animationController.PlayNpcAutoAttack(primaryNpc, targetId, 0);
            attackTimer = AttackInterval;
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
            emotePlayer.ResetEmote(character);
            animationController.ClearBattleStance(primaryNpc);
            character->SetMode(CharacterModes.Normal, 0);
        }

        log.Info($"ExecutionMode: stopped (NPC='{primaryNpc?.Name ?? "none"}')");
        primaryNpc = null;
        attackTimer = 0f;
        isActive = false;
    }

    public void Dispose()
    {
        if (isActive) StopInternal(restoreNpc: true);
    }
}
