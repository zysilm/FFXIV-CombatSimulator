using System;
using System.Numerics;
using CombatSimulator.ActionCombat;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace CombatSimulator.Fighting;

/// <summary>
/// Fighting Mode player combat: dedicated guard input plus the weapon-contact attack
/// state machine. An attack input plays the action's animation and opens an active
/// window; damage lands only when the main-hand weapon segment sweeps through the
/// enemy's hurtbox capsule during that window (one hit per swing). Reuses the
/// mode-agnostic Action-combat components (PlayerGuardController / TelegraphSystem /
/// HitFeedbackController) — those are ticked centrally by the plugin, not here.
/// </summary>
public sealed unsafe class FightingCombatController
{
    private enum SwingPhase
    {
        Idle,
        Windup,
        Active,
        Recovery,
    }

    private readonly Configuration config;
    private readonly PlayerGuardController guard;
    private readonly FightingModeController fighting;
    private readonly CombatSimulator.Simulation.CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly WeaponHitboxService weaponHitbox;
    private readonly HitFeedbackController hitFeedback;
    private readonly IGamepadState gamepad;
    private readonly IPluginLog log;

    private bool guardWasDown;
    private bool basicAttackWasDown;

    private SwingPhase swingPhase = SwingPhase.Idle;
    private float swingTimer;
    private float swingDuration;
    private float activeStart;
    private float activeEnd;
    private bool hitLanded;
    private uint currentActionId;
    private WeaponHitboxService.WeaponSegment? prevSegment;

    /// <summary>Fired when a weapon-contact hit lands (Stage G: fighting AI hitstun).</summary>
    public Action<SimulatedNpc>? OnPlayerHitLanded;

    /// <summary>Live weapon segment while a swing's active window is open (debug overlay).</summary>
    public WeaponHitboxService.WeaponSegment? ActiveWeaponSegment { get; private set; }
    public bool IsSwinging => swingPhase != SwingPhase.Idle;

    public FightingCombatController(
        Configuration config,
        PlayerGuardController guard,
        FightingModeController fighting,
        CombatSimulator.Simulation.CombatEngine combatEngine,
        AnimationController animationController,
        WeaponHitboxService weaponHitbox,
        HitFeedbackController hitFeedback,
        IGamepadState gamepad,
        IPluginLog log)
    {
        this.config = config;
        this.guard = guard;
        this.fighting = fighting;
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.weaponHitbox = weaponHitbox;
        this.hitFeedback = hitFeedback;
        this.gamepad = gamepad;
        this.log = log;
    }

    public void Tick(float dt)
    {
        // Guard + basic-attack must work from the moment Fighting Mode is on — NOT only once
        // engaged. Engagement happens on the first attack, so gating input on it made guarding
        // (and the basic attack itself) impossible until you had already attacked.
        if (!config.FightingMode || !combatEngine.State.PlayerState.IsAlive)
        {
            guardWasDown = false;
            basicAttackWasDown = false;
            ResetSwing();
            return;
        }

        TickGuardInput();
        TickBasicAttackInput();
        TickSwing(dt);
    }

    /// <summary>
    /// Player attack input (routed from the UseAction hook via FightingModeController).
    /// Plays the action's animation and opens the sweep window; damage is applied on
    /// weapon contact, not here. Inputs during a swing are ignored (no mash-cancel).
    /// </summary>
    public bool OnAttackInput(uint actionId, SimulatedNpc target)
    {
        if (swingPhase != SwingPhase.Idle)
            return false;

        var duration = MathF.Max(0.3f, animationController.ResolveActionAnimationDuration(actionId));
        if (!combatEngine.TrySpendPlayerActionMp(actionId, duration, out _, out var failReason))
        {
            if (!string.IsNullOrEmpty(failReason))
                log.Debug($"FightingCombat: attack rejected ({failReason})");
            return false;
        }

        animationController.PlayPlayerActionAnimationOnly(actionId);

        var startPct = Math.Clamp(config.FightingModeAttackActiveStartPct, 0f, 0.9f);
        var endPct = Math.Clamp(config.FightingModeAttackActiveEndPct, startPct + 0.05f, 1f);
        swingDuration = duration;
        swingTimer = 0f;
        activeStart = duration * startPct;
        activeEnd = duration * endPct;
        hitLanded = false;
        currentActionId = actionId;
        prevSegment = null;
        swingPhase = SwingPhase.Windup;
        return true;
    }

    public void ResetSwing()
    {
        swingPhase = SwingPhase.Idle;
        swingTimer = 0f;
        hitLanded = false;
        prevSegment = null;
        ActiveWeaponSegment = null;
    }

    private void TickSwing(float dt)
    {
        if (swingPhase == SwingPhase.Idle)
            return;

        swingTimer += dt;
        switch (swingPhase)
        {
            case SwingPhase.Windup:
                if (swingTimer >= activeStart)
                {
                    swingPhase = SwingPhase.Active;
                    prevSegment = null;
                }
                break;

            case SwingPhase.Active:
                SweepTest();
                if (swingTimer >= activeEnd)
                {
                    swingPhase = SwingPhase.Recovery;
                    ActiveWeaponSegment = null;
                }
                break;

            case SwingPhase.Recovery:
                if (swingTimer >= swingDuration)
                    ResetSwing();
                break;
        }
    }

    private void SweepTest()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return;

        var seg = weaponHitbox.GetMainHandSegment(player.Address);
        ActiveWeaponSegment = seg;
        if (seg == null)
        {
            prevSegment = null;
            return;
        }

        if (hitLanded)
        {
            prevSegment = seg;
            return;
        }

        var target = fighting.CurrentTarget;
        if (target is not { IsAlive: true } || target.Address == nint.Zero)
        {
            prevSegment = seg;
            return;
        }

        var enemyPos = target.GameObjectRef?.Position
                       ?? ReadObjectPosition(target.Address);
        var hurtRadius = target.HitboxRadius * MathF.Max(0.1f, config.FightingModeHurtboxRadiusScale);
        var hurtHeight = MathF.Max(0.3f, config.FightingModeHurtboxHeight);
        var hurtA = enemyPos;
        var hurtB = enemyPos + new Vector3(0f, hurtHeight, 0f);
        var threshold = hurtRadius + MathF.Max(0.01f, config.FightingModeWeaponRadius);

        var hit = false;
        if (prevSegment.HasValue)
        {
            // Interpolate between last frame's segment and this one so a fast swing
            // can't teleport through the hurtbox between samples.
            const int samples = 3;
            for (var i = 1; i <= samples && !hit; i++)
            {
                var t = i / (float)samples;
                var b = Vector3.Lerp(prevSegment.Value.Base, seg.Value.Base, t);
                var tip = Vector3.Lerp(prevSegment.Value.Tip, seg.Value.Tip, t);
                hit = WeaponHitboxService.SegmentSegmentDistance(b, tip, hurtA, hurtB) <= threshold;
            }
        }
        else
        {
            hit = WeaponHitboxService.SegmentSegmentDistance(seg.Value.Base, seg.Value.Tip, hurtA, hurtB) <= threshold;
        }

        prevSegment = seg;

        if (!hit)
            return;

        hitLanded = true;
        ApplyHit(target);
    }

    private void ApplyHit(SimulatedNpc target)
    {
        var landed = combatEngine.ApplyPlayerActionMode(
            currentActionId, target.SimulatedEntityId,
            potencyOverride: 0, animationDuration: swingDuration,
            suppressCasterActionEffect: true);
        if (landed <= 0)
            return;

        hitFeedback.TriggerHit(target);
        OnPlayerHitLanded?.Invoke(target);
    }

    private static Vector3 ReadObjectPosition(nint address)
    {
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        return new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
    }

    // Guard shares Action Mode's bind (no separate Fighting Mode key), so both modes guard
    // with the same key/button and there is one place to configure it.
    private void TickGuardInput()
    {
        var down = false;
        try
        {
            var io = ImGui.GetIO();
            if (!io.WantCaptureKeyboard && !io.WantTextInput)
            {
                var fw = GameFramework.Instance();
                if (fw != null && config.ActionGuardKey is > 0 and < 256)
                    down = fw->KeyboardInputs.KeyState[config.ActionGuardKey].HasFlag(KeyStateFlags.Down);
            }

            if (config.ActionGuardGamepadButton != GamepadButtons.None)
                down |= gamepad.Raw(config.ActionGuardGamepadButton) != 0;
        }
        catch { }

        if (down && !guardWasDown)
            guard.TryGuard();
        guardWasDown = down;
    }

    // Basic attack shares Action Mode's basic-attack bind: a weapon swing with no hotbar action.
    private void TickBasicAttackInput()
    {
        var down = false;
        try
        {
            var io = ImGui.GetIO();
            if (!io.WantCaptureKeyboard && !io.WantTextInput)
            {
                var fw = GameFramework.Instance();
                if (fw != null && config.ActionBasicAttackKey is > 0 and < 256)
                    down = fw->KeyboardInputs.KeyState[config.ActionBasicAttackKey].HasFlag(KeyStateFlags.Down);
            }

            if (config.ActionBasicAttackGamepadButton != GamepadButtons.None)
                down |= gamepad.Raw(config.ActionBasicAttackGamepadButton) != 0;
        }
        catch { }

        if (down && !basicAttackWasDown)
            fighting.TryBasicAttack();
        basicAttackWasDown = down;
    }
}
