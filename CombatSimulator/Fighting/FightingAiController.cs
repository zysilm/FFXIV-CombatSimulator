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
/// skips this NPC while engaged). Works in lane coordinates: keeps a spacing band,
/// approaches (sometimes dashing), retreats, commits to telegraphed attacks — singly
/// or in combo strings — hops over the player to switch sides, and takes hitstun
/// pushback from weapon-contact hits. Attacks route through the shared
/// CombatModeRouter (always telegraphed while engaged, so guard works against every
/// attack). FightingModeController remains the single position writer — it reads
/// DesiredAlong / DesiredY.
/// </summary>
public sealed unsafe class FightingAiController
{
    private enum AiState
    {
        Neutral,
        Approach,
        Dash,
        Retreat,
        Committing,
        ComboGap,
        JumpOver,
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

    // Combo string
    private int comboRemaining;
    private float comboGapTimer;

    // Jump-over arc
    private float jumpElapsed;
    private float jumpFrom;
    private float jumpTo;
    private float groundY;
    private bool groundYInitialized;
    private bool airborne;

    public bool IsActive => npc != null;
    public float DesiredAlong => desiredAlong;
    /// <summary>Enemy Y the lane writer should use (ground, or the jump arc). Null until
    /// the first tick seeds the ground height.</summary>
    public float? DesiredY { get; private set; }
    public bool IsAirborne => airborne;
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
        airborne = false;
        groundYInitialized = false;
        DesiredY = null;
        comboRemaining = 0;
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
        airborne = false;
        DesiredY = null;
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
        comboRemaining = 0;
        airborne = false; // knocked out of a hop
    }

    public void Tick(float dt, float npcAlong, float playerAlong, float npcY)
    {
        if (npc == null)
            return;
        if (!npc.IsAlive)
        {
            moving = false;
            airborne = false;
            return;
        }

        if (!groundYInitialized)
        {
            groundY = npcY;
            groundYInitialized = true;
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
                    state = random.NextSingle() < Math.Clamp(config.FightingAiDashChance, 0f, 1f)
                        ? AiState.Dash
                        : AiState.Approach;
                else if (dist < rangeMin)
                {
                    state = AiState.Retreat;
                    retreatTimer = MathF.Max(0.1f, config.FightingAiRetreatDuration);
                }
                else if (attackCooldown <= 0f)
                    DecideOffense(npcAlong, playerAlong, rangeMin);
                break;

            case AiState.Approach:
                desiredAlong = npcAlong - pushSide * speed * dt;
                moving = true;
                if (dist <= (rangeMin + rangeMax) * 0.5f)
                    state = AiState.Neutral;
                break;

            case AiState.Dash:
                // Burst approach — closes distance fast, reads as a fighting-game dash-in.
                desiredAlong = npcAlong - pushSide * speed
                    * MathF.Max(1.2f, config.FightingAiDashSpeedScale) * dt;
                moving = true;
                if (dist <= (rangeMin + rangeMax) * 0.5f)
                {
                    state = AiState.Neutral;
                    attackCooldown = 0f; // dash-in wants an immediate follow-up
                }
                break;

            case AiState.Retreat:
                // Slower than approach — a backpedal the player can run down.
                desiredAlong = npcAlong + pushSide * speed
                    * Math.Clamp(config.FightingAiRetreatSpeedScale, 0.1f, 1f) * dt;
                moving = true;
                retreatTimer -= dt;
                if (retreatTimer <= 0f || dist >= rangeMax * 0.9f)
                    state = AiState.Neutral;
                break;

            case AiState.Committing:
                // Locked into the telegraphed swing until windup + animation lock resolve.
                if (!telegraphs.IsWindingUp(npc.SimulatedEntityId) && npc.State.AnimationLock <= 0f)
                {
                    if (comboRemaining > 0 && dist <= rangeMax * 1.25f)
                    {
                        comboGapTimer = MathF.Max(0.05f, config.FightingAiComboGap);
                        state = AiState.ComboGap;
                    }
                    else
                    {
                        comboRemaining = 0;
                        state = AiState.Recover;
                        recoverTimer = MathF.Max(0.05f, config.FightingAiRecoverTime);
                    }
                }
                break;

            case AiState.ComboGap:
                // Short breath between combo hits; drop the string if the player escaped.
                comboGapTimer -= dt;
                if (dist > rangeMax * 1.25f)
                {
                    comboRemaining = 0;
                    state = AiState.Recover;
                    recoverTimer = MathF.Max(0.05f, config.FightingAiRecoverTime);
                }
                else if (comboGapTimer <= 0f)
                {
                    comboRemaining--;
                    CommitAttack();
                }
                break;

            case AiState.JumpOver:
                TickJumpOver(dt, playerAlong, rangeMin);
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

        DesiredY = airborne ? DesiredY : groundY;
        UpdateVisualState(dt);
    }

    /// <summary>In-band, off-cooldown offense: hop over the player, open a combo
    /// string, or throw a single attack.</summary>
    private void DecideOffense(float npcAlong, float playerAlong, float rangeMin)
    {
        var roll = random.NextSingle();
        if (roll < Math.Clamp(config.FightingAiJumpOverChance, 0f, 1f))
        {
            StartJumpOver(npcAlong, playerAlong, rangeMin);
            return;
        }

        comboRemaining = random.NextSingle() < Math.Clamp(config.FightingAiComboChance, 0f, 1f)
            ? random.Next(1, Math.Max(2, config.FightingAiMaxComboHits))
            : 0;
        CommitAttack();
    }

    private void StartJumpOver(float npcAlong, float playerAlong, float rangeMin)
    {
        jumpFrom = npcAlong;
        // Land on the player's OTHER side, at preferred min range.
        jumpTo = playerAlong - pushSide * rangeMin;
        jumpElapsed = 0f;
        airborne = true;
        state = AiState.JumpOver;
    }

    private void TickJumpOver(float dt, float playerAlong, float rangeMin)
    {
        jumpElapsed += dt;
        var duration = MathF.Max(0.2f, config.FightingAiJumpDuration);
        var t = Math.Clamp(jumpElapsed / duration, 0f, 1f);
        var s = t * t * (3f - 2f * t);
        desiredAlong = jumpFrom + (jumpTo - jumpFrom) * s;
        DesiredY = groundY + MathF.Max(0.2f, config.FightingAiJumpHeight) * 4f * t * (1f - t);
        moving = true;

        if (t >= 1f)
        {
            airborne = false;
            DesiredY = groundY;
            // Jump-in pressure: often attack straight off the landing.
            if (random.NextSingle() < Math.Clamp(config.FightingAiJumpInAttackChance, 0f, 1f))
            {
                comboRemaining = 0;
                CommitAttack();
                if (state != AiState.Committing)
                    state = AiState.Neutral;
            }
            else
            {
                state = AiState.Neutral;
                attackCooldown = MathF.Min(attackCooldown, 0.4f);
            }
        }
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
        {
            comboRemaining = 0;
            attackCooldown = 0.5f; // brief re-try backoff instead of spamming
            state = AiState.Neutral;
        }
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
            if (npc.State.AnimationLock > 0f || state is AiState.Committing or AiState.ComboGap)
                ActorVisualStateController.ApplyActionLocked(character, npc.VisualState);
            else if (moving)
                ActorVisualStateController.ApplyMoving(character, npc.VisualState, dt);
            else
                ActorVisualStateController.ApplyCombatIdle(character, npc.VisualState);
        }
        catch { }
    }
}
