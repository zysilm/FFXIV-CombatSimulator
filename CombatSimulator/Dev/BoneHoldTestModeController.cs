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
/// constraints while NPCs continue to perform melee attacks.
/// Arms, head, and hands remain fully dynamic and react to NPC collisions.
/// </summary>
public unsafe class BoneHoldTestModeController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly EmoteTimelinePlayer emotePlayer;
    private readonly RagdollController ragdollController;
    private readonly AnimationController animationController;
    private readonly IPluginLog log;

    private bool isActive;
    private bool attackEnabled;
    private bool attackAllNpcs;
    private float attackDistance;
    private SimulatedNpc? primaryNpc;
    private float attackTimer;

    public float AttackInterval { get; set; } = 2.5f;
    public bool IsActive => isActive;

    public BoneHoldTestModeController(
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

    public bool TryStart(IReadOnlyList<SimulatedNpc> npcs, string anchorBone = "j_kosi",
        float standingHeight = 0.92f, bool enableAttack = true,
        bool allNpcs = false, float atkDistance = 8.0f)
    {
        if (isActive) return false;
        if (!ragdollController.IsActive)
        {
            log.Warning("BoneHoldTestMode: ragdoll not active");
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
            log.Warning("BoneHoldTestMode: no alive NPC found");
            return false;
        }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            log.Warning("BoneHoldTestMode: no local player");
            return false;
        }

        var go = (GameObject*)player.Address;
        var anchorTarget = new Vector3(go->Position.X, go->Position.Y + standingHeight, go->Position.Z);

        if (!ragdollController.CreateStandingSupport(anchorTarget, Quaternion.Identity, anchorBone))
        {
            log.Warning("BoneHoldTestMode: CreateStandingSupport failed");
            return false;
        }

        primaryNpc     = candidate;
        attackEnabled  = enableAttack;
        attackAllNpcs  = allNpcs;
        attackDistance = atkDistance;

        if (attackEnabled)
        {
            animationController.SetBattleStance(primaryNpc);
            attackTimer = 0f;
        }

        isActive = true;
        log.Info($"BoneHoldTestMode: started — NPC '{primaryNpc.Name}', anchor={anchorBone}, height={standingHeight:F2}");
        return true;
    }

    /// <summary>Adjust anchor bone and height while the mode is already running.</summary>
    public void UpdateHold(string anchorBone, float standingHeight)
    {
        if (!isActive) return;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;

        var go = (GameObject*)player.Address;
        var anchorTarget = new Vector3(go->Position.X, go->Position.Y + standingHeight, go->Position.Z);
        ragdollController.UpdateStandingSupport(anchorTarget, Quaternion.Identity, anchorBone);
    }

    public void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> allNpcs)
    {
        if (!isActive) return;

        if (!ragdollController.IsActive)
        {
            StopInternal(restoreNpc: true, allNpcs);
            return;
        }

        if (primaryNpc?.BattleChara == null)
        {
            StopInternal(restoreNpc: false, allNpcs);
            return;
        }

        if (!attackEnabled) return;

        attackTimer -= deltaTime;
        if (attackTimer > 0f) return;
        attackTimer = AttackInterval;

        var playerPos = Core.Services.ObjectTable.LocalPlayer is { } lp
            ? new Vector3(((GameObject*)lp.Address)->Position.X,
                          ((GameObject*)lp.Address)->Position.Y,
                          ((GameObject*)lp.Address)->Position.Z)
            : Vector3.Zero;

        var targetId = Core.Services.ObjectTable.LocalPlayer?.EntityId ?? 0;
        if (targetId == 0) return;

        if (!attackAllNpcs)
        {
            if (InRange(primaryNpc, playerPos))
                animationController.PlayNpcAutoAttack(primaryNpc, targetId, 0);
            return;
        }

        foreach (var npc in allNpcs)
        {
            if (!npc.State.IsAlive || npc.BattleChara == null) continue;
            if (!InRange(npc, playerPos)) continue;
            animationController.PlayNpcAutoAttack(npc, targetId, 0);
        }
    }

    private bool InRange(SimulatedNpc npc, Vector3 playerPos)
    {
        if (attackDistance <= 0f) return true;
        var go = (GameObject*)npc.BattleChara;
        var npcPos = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
        return Vector3.Distance(npcPos, playerPos) <= attackDistance;
    }

    public void Stop() => Stop(Array.Empty<SimulatedNpc>());

    public void Stop(IReadOnlyList<SimulatedNpc> allNpcs)
    {
        if (!isActive) return;
        StopInternal(restoreNpc: true, allNpcs);
    }

    private void StopInternal(bool restoreNpc, IReadOnlyList<SimulatedNpc> allNpcs)
    {
        ragdollController.RemoveStandingSupport();

        if (restoreNpc)
        {
            var npcsToClean = attackAllNpcs ? allNpcs : (IEnumerable<SimulatedNpc>)(primaryNpc != null ? new[] { primaryNpc } : Array.Empty<SimulatedNpc>());
            foreach (var npc in npcsToClean)
            {
                if (npc.BattleChara == null) continue;
                var character = (Character*)npc.BattleChara;
                emotePlayer.ResetEmote(character);
                if (attackEnabled) animationController.ClearBattleStance(npc);
                character->SetMode(CharacterModes.Normal, 0);
            }
        }

        log.Info($"BoneHoldTestMode: stopped (NPC='{primaryNpc?.Name ?? "none"}')");
        primaryNpc    = null;
        attackEnabled = false;
        attackTimer   = 0f;
        isActive      = false;
    }

    public void Dispose()
    {
        if (isActive) StopInternal(restoreNpc: true, Array.Empty<SimulatedNpc>());
    }
}
