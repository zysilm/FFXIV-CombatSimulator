using System;
using CombatSimulator.Animation;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Orchestrates the Action-Mode player loop: drains buffered inputs, drives the light
/// combo + hitbox, the dodge, and the enemy telegraphs. Ticked from the framework
/// update only while <c>config.ActionMode</c> is on. Enemy AI and the simulation
/// engine continue to run as normal; only attack resolution differs.
/// </summary>
public sealed class ActionModeController
{
    private readonly Configuration config;
    private readonly ActionComboSink comboSink;
    private readonly PlayerHitboxResolver hitbox;
    private readonly PlayerDodgeController dodge;
    private readonly TelegraphSystem telegraphs;
    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly IPluginLog log;

    private float swingCooldown;
    private int comboStep;
    private float comboTimer;
    private bool wasActive;

    public ActionModeController(
        Configuration config,
        ActionComboSink comboSink,
        PlayerHitboxResolver hitbox,
        PlayerDodgeController dodge,
        TelegraphSystem telegraphs,
        CombatEngine combatEngine,
        AnimationController animationController,
        IPluginLog log)
    {
        this.config = config;
        this.comboSink = comboSink;
        this.hitbox = hitbox;
        this.dodge = dodge;
        this.telegraphs = telegraphs;
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.log = log;
    }

    public TelegraphSystem Telegraphs => telegraphs;

    public void Tick(float dt)
    {
        if (!config.ActionMode)
        {
            if (wasActive)
            {
                OnModeExit();
                wasActive = false;
            }
            return;
        }
        wasActive = true;

        if (swingCooldown > 0f) swingCooldown = MathF.Max(0f, swingCooldown - dt);
        if (comboTimer > 0f)
        {
            comboTimer -= dt;
            if (comboTimer <= 0f) comboStep = 0;
        }

        dodge.Tick(dt);
        telegraphs.Tick(dt);
        comboSink.Drain(HandleInput);
    }

    private void HandleInput(PlayerActionInput input)
    {
        switch (input.Role)
        {
            case PlayerInputRole.Dodge:
                dodge.TryDodge();
                break;
            default: // LightAttack / Skill1 / Skill2 → a light swing for this slice
                LightAttack(input.ActionId);
                break;
        }
    }

    private void LightAttack(uint actionId)
    {
        if (swingCooldown > 0f)
            return;
        if (!combatEngine.State.PlayerState.IsAlive)
            return;

        swingCooldown = config.LightSwingInterval;
        comboStep = comboTimer > 0f ? (comboStep + 1) % 3 : 0;
        comboTimer = config.LightComboWindow;

        var ids = hitbox.ResolveConeTargets();
        var struck = combatEngine.ApplyResolvedPlayerHit(ids, actionId, config.LightAttackPotency);
        if (struck == 0)
            animationController.PlayPlayerActionAnimationOnly(actionId); // reliable whiff (打空) feedback
    }

    private void OnModeExit()
    {
        comboSink.Clear();
        dodge.Reset();
        telegraphs.Clear();
        swingCooldown = 0f;
        comboStep = 0;
        comboTimer = 0f;
    }
}
