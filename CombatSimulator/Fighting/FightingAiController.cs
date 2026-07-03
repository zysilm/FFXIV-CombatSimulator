using System;
using System.Linq;
using CombatSimulator.ActionCombat;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Fighting;

/// <summary>
/// 1v1 fighting-game AI for the engaged enemy, independent of NpcAiController (which
/// skips this NPC while engaged). Works entirely in lane coordinates: keeps a spacing
/// band, approaches/retreats, commits to telegraphed attacks through the shared
/// CombatModeRouter (always telegraphed while engaged, so the player's guard works
/// against every attack), and takes hitstun pushback from weapon-contact hits.
/// FightingModeController remains the single position writer — it reads DesiredAlong.
/// </summary>
public sealed unsafe class FightingAiController
{
    private enum AiState
    {
        Neutral,
        Approach,
        Retreat,
        Committing,
        Recover,
        Hitstun,
    }

    private readonly Configuration config;
    private readonly CombatModeRouter router;
    private readonly TelegraphSystem telegraphs;
    private readonly AnimationController animationController;
    private readonly IPluginLog log;
    private readonly Random random = new();

    private SimulatedNpc? npc;
    private AiState state = AiState.Neutral;
    private float attackCooldown;
    private float retreatTimer;
    private float recoverTimer;
    private float hitstunTimer;
    private float pushSide;
    private float desiredAlong;
    private bool moving;

    public bool IsActive => npc != null;
    public float DesiredAlong => desiredAlong;
    public bool IsMoving => moving;

    public FightingAiController(
        Configuration config,
        CombatModeRouter router,
        TelegraphSystem telegraphs,
        AnimationController animationController,
        IPluginLog log)
    {
        this.config = config;
        this.router = router;
        this.telegraphs = telegraphs;
        this.animationController = animationController;
        this.log = log;
    }

    public void Begin(SimulatedNpc target)
    {
        if (npc == target)
            return;

        npc = target;
        state = AiState.Neutral;
        attackCooldown = NextCooldown() * 0.5f; // first attack comes a bit sooner
        moving = false;
        // NpcAiController may have left a cast mid-flight; it is skipped from here on,
        // so that cast would never complete — clear it.
        target.State.IsCasting = false;
        target.CurrentCastSkill = null;
        animationController.SetBattleStance(target);
        log.Debug($"FightingAi: driving '{target.Name}'.");
    }

    public void End()
    {
        if (npc == null)
            return;

        try
        {
            if (npc.BattleChara != null)
                ActorVisualStateController.ClearMovement((Character*)npc.BattleChara, npc.VisualState);
        }
        catch { }

        npc = null;
        state = AiState.Neutral;
        moving = false;
    }

    /// <summary>Weapon-contact hit landed on the enemy: hitstun + pushback, unless it is
    /// mid-windup (super-armor, consistent with the engine's flinch suppression).</summary>
    public void NotifyPlayerHitLanded()
    {
        if (npc == null)
            return;
        if (telegraphs.IsWindingUp(npc.SimulatedEntityId))
            return;

        state = AiState.Hitstun;
        hitstunTimer = MathF.Max(0.05f, config.FightingAiHitstunDuration);
    }

    public void Tick(float dt, float npcAlong, float playerAlong)
    {
        if (npc == null)
            return;
        if (!npc.IsAlive)
        {
            moving = false;
            return;
        }

        desiredAlong = npcAlong;
        moving = false;

        // NpcAiController normally owns these decrements but skips the engaged fighter.
        npc.State.AnimationLock = MathF.Max(0f, npc.State.AnimationLock - dt);
        foreach (var skill in npc.Behavior.Skills)
            if (skill.CooldownRemaining > 0)
                skill.CooldownRemaining = MathF.Max(0f, skill.CooldownRemaining - dt);

        if (attackCooldown > 0f)
            attackCooldown -= dt;

        var dist = MathF.Abs(npcAlong - playerAlong);
        // Which side of the player the enemy stands on: retreat/pushback go this way.
        pushSide = MathF.Sign(npcAlong - playerAlong);
        if (pushSide == 0f)
            pushSide = 1f;

        var rangeMin = MathF.Max(0.5f, config.FightingAiRangeMin);
        var rangeMax = MathF.Max(rangeMin + 0.2f, config.FightingAiRangeMax);
        var speed = MathF.Max(0.1f, config.FightingAiMoveSpeed);

        switch (state)
        {
            case AiState.Neutral:
                if (dist > rangeMax)
                    state = AiState.Approach;
                else if (dist < rangeMin)
                {
                    state = AiState.Retreat;
                    retreatTimer = MathF.Max(0.1f, config.FightingAiRetreatDuration);
                }
                else if (attackCooldown <= 0f)
                    CommitAttack();
                break;

            case AiState.Approach:
                desiredAlong = npcAlong - pushSide * speed * dt;
                moving = true;
                if (dist <= (rangeMin + rangeMax) * 0.5f)
                    state = AiState.Neutral;
                break;

            case AiState.Retreat:
                desiredAlong = npcAlong + pushSide * speed * dt;
                moving = true;
                retreatTimer -= dt;
                if (retreatTimer <= 0f || dist >= rangeMax * 0.9f)
                    state = AiState.Neutral;
                break;

            case AiState.Committing:
                // Locked into the telegraphed swing until windup + animation lock resolve.
                if (!telegraphs.IsWindingUp(npc.SimulatedEntityId) && npc.State.AnimationLock <= 0f)
                {
                    state = AiState.Recover;
                    recoverTimer = MathF.Max(0.05f, config.FightingAiRecoverTime);
                }
                break;

            case AiState.Recover:
                recoverTimer -= dt;
                if (recoverTimer <= 0f)
                {
                    attackCooldown = NextCooldown();
                    if (random.NextSingle() < Math.Clamp(config.FightingAiRetreatChance, 0f, 1f))
                    {
                        state = AiState.Retreat;
                        retreatTimer = MathF.Max(0.1f, config.FightingAiRetreatDuration);
                    }
                    else
                        state = AiState.Neutral;
                }
                break;

            case AiState.Hitstun:
                hitstunTimer -= dt;
                var pushSpeed = config.FightingAiHitstunPushback / MathF.Max(0.05f, config.FightingAiHitstunDuration);
                desiredAlong = npcAlong + pushSide * pushSpeed * dt;
                if (hitstunTimer <= 0f)
                    state = AiState.Neutral;
                break;
        }

        UpdateVisualState(dt);
    }

    private void CommitAttack()
    {
        if (npc == null)
            return;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return;
        var targetId = (ulong)player.EntityId;

        // Prefer an off-cooldown skill (priority order, HP threshold respected), else auto.
        var hpPercent = npc.State.MaxHp > 0 ? (float)npc.State.CurrentHp / npc.State.MaxHp : 1f;
        var skill = npc.Behavior.Skills
            .OrderByDescending(s => s.Priority)
            .FirstOrDefault(s => s.CooldownRemaining <= 0 && hpPercent <= s.HpThreshold);

        bool ok;
        if (skill != null)
        {
            ok = router.AttackExecutor.Execute(npc, new NpcAttackRequest(
                skill.ActionId, targetId, skill.Potency, skill.AttackStyle, skill.Radius, skill.CastTime));
            if (ok)
                skill.CooldownRemaining = skill.Cooldown;
        }
        else
        {
            ok = router.AttackExecutor.Execute(npc, new NpcAttackRequest(
                npc.Behavior.AutoAttackActionId, targetId, npc.Behavior.AutoAttackPotency,
                npc.Behavior.AutoAttackStyle, 0f, 0f, IsAutoAttack: true));
        }

        if (ok)
            state = AiState.Committing;
        else
            attackCooldown = 0.5f; // brief re-try backoff instead of spamming
    }

    private float NextCooldown()
        => MathF.Max(0.2f, config.FightingAiAttackCooldown)
           + random.NextSingle() * MathF.Max(0f, config.FightingAiAttackCooldownJitter);

    private void UpdateVisualState(float dt)
    {
        if (npc?.BattleChara == null)
            return;

        try
        {
            var character = (Character*)npc.BattleChara;
            if (npc.State.AnimationLock > 0f || state == AiState.Committing)
                ActorVisualStateController.ApplyActionLocked(character, npc.VisualState);
            else if (moving)
                ActorVisualStateController.ApplyMoving(character, npc.VisualState, dt);
            else
                ActorVisualStateController.ApplyCombatIdle(character, npc.VisualState);
        }
        catch { }
    }
}
