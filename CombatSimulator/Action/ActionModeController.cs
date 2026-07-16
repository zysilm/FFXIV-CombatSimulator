using System;
using CombatSimulator.Animation;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Orchestrates the Action-Mode player loop: drains buffered inputs, drives the light
/// combo + hitbox, guard, and enemy telegraphs. Ticked from the framework
/// update only while <c>config.ActionMode</c> is on. Enemy AI and the simulation
/// engine continue to run as normal; only attack resolution differs.
/// </summary>
public sealed class ActionModeController
{
    private const float GuardCancelLockRatio = 0.70f;
    private const float PhysicalRangedBasicPotencyRatio = 0.75f;

    private readonly Configuration config;
    private readonly ActionComboSink comboSink;
    private readonly PlayerHitboxResolver hitbox;
    private readonly PlayerGuardController guard;
    private readonly TelegraphSystem telegraphs;
    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly HitFeedbackController hitFeedback;
    private readonly IGamepadState gamepadState;
    private readonly IPluginLog log;

    private float swingCooldown;
    private float guardLockoutTimer;
    private float attackLockoutTimer;
    private int comboStep;
    private float comboTimer;
    private bool wasActive;
    private bool guardKeyWasDown;
    private bool basicAttackKeyWasDown;
    private int guardGamepadSuppressFrames;
    private int basicAttackGamepadSuppressFrames;

    public ActionModeController(
        Configuration config,
        ActionComboSink comboSink,
        PlayerHitboxResolver hitbox,
        PlayerGuardController guard,
        TelegraphSystem telegraphs,
        CombatEngine combatEngine,
        AnimationController animationController,
        HitFeedbackController hitFeedback,
        IGamepadState gamepadState,
        IPluginLog log)
    {
        this.config = config;
        this.comboSink = comboSink;
        this.hitbox = hitbox;
        this.guard = guard;
        this.telegraphs = telegraphs;
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.hitFeedback = hitFeedback;
        this.gamepadState = gamepadState;
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

        if (!combatEngine.IsActive)
        {
            guard.Reset();
            guardLockoutTimer = 0f;
            attackLockoutTimer = 0f;
            guardKeyWasDown = false;
            basicAttackKeyWasDown = false;
            comboSink.Clear();
            return;
        }

        if (!combatEngine.State.PlayerState.IsAlive)
        {
            guard.Reset();
            guardLockoutTimer = 0f;
            attackLockoutTimer = 0f;
            guardKeyWasDown = false;
            basicAttackKeyWasDown = false;
            comboSink.Clear();
            return;
        }

        if (swingCooldown > 0f) swingCooldown = MathF.Max(0f, swingCooldown - dt);
        if (guardLockoutTimer > 0f) guardLockoutTimer = MathF.Max(0f, guardLockoutTimer - dt);
        if (attackLockoutTimer > 0f) attackLockoutTimer = MathF.Max(0f, attackLockoutTimer - dt);
        if (comboTimer > 0f)
        {
            comboTimer -= dt;
            if (comboTimer <= 0f) comboStep = 0;
        }

        // guard/telegraph ticking moved to the plugin's frame loop — they are shared
        // with Fighting Mode and must advance whichever mode is active.
        SuppressGuardGamepadInputIfNeeded();
        SuppressBasicAttackGamepadInputIfNeeded();
        TickGuardKey();
        TickGuardGamepad();
        TickBasicAttackKey();
        TickBasicAttackGamepad();
        comboSink.Drain(HandleInput);
        SuppressGuardGamepadInputIfNeeded();
        SuppressBasicAttackGamepadInputIfNeeded();
    }

    private void TickBasicAttackGamepad()
    {
        if (config.ActionBasicAttackGamepadButton == GamepadButtons.None)
            return;
        if (gamepadState.Pressed(config.ActionBasicAttackGamepadButton) > 0)
        {
            BasicAttack();
            basicAttackGamepadSuppressFrames = 3;
            SuppressBasicAttackGamepadInput();
        }
    }

    private unsafe void TickBasicAttackKey()
    {
        if (ImGui.GetIO().WantCaptureKeyboard)
        {
            basicAttackKeyWasDown = false;
            return;
        }

        var fw = GameFramework.Instance();
        if (fw == null)
            return;

        var keyValue = config.ActionBasicAttackKey;
        if (keyValue <= 0 || keyValue >= 256)
            return;

        var isDown = fw->KeyboardInputs.KeyState[keyValue].HasFlag(KeyStateFlags.Down);
        if (isDown && !basicAttackKeyWasDown)
            BasicAttack();

        basicAttackKeyWasDown = isDown;
    }

    // The player's weapon basic attack: no hotbar action needed. Ranged for Bard/Archer +
    // Astrologian (longer/wider cone, smallest-angle pick); melee for everyone else (narrow nearest).
    // Plays the weapon auto-attack (action 7) with the configured potency/cadence.
    private void BasicAttack()
    {
        if (attackLockoutTimer > 0f || swingCooldown > 0f)
            return;
        if (!combatEngine.State.PlayerState.IsAlive)
            return;

        const uint AutoAttackId = 7;
        var duration = MathF.Max(0.05f, animationController.ResolveActionAnimationDuration(AutoAttackId));
        guardLockoutTimer = MathF.Max(guardLockoutTimer, duration * GuardCancelLockRatio);
        attackLockoutTimer = MathF.Max(attackLockoutTimer, duration);
        swingCooldown = config.LightSwingInterval;

        var primary = hitbox.ResolveBasicAttackPrimary();
        var potency = PlayerHitboxResolver.IsRangedBasicAttackJob()
            ? Math.Max(1, (int)MathF.Round(config.LightAttackPotency * PhysicalRangedBasicPotencyRatio))
            : config.LightAttackPotency;
        var struck = primary != null
            ? combatEngine.ApplyPlayerActionMode(AutoAttackId, primary.State.EntityId, potency)
            : 0;
        if (struck == 0)
            animationController.PlayPlayerActionAnimationOnly(AutoAttackId);
        else
            hitFeedback.TriggerHit(primary);
    }

    private unsafe void TickGuardKey()
    {
        if (ImGui.GetIO().WantCaptureKeyboard)
        {
            guardKeyWasDown = false;
            return;
        }

        var fw = GameFramework.Instance();
        if (fw == null)
            return;

        var keyValue = config.ActionGuardKey;
        if (keyValue <= 0 || keyValue >= 256)
            return;

        var isDown = fw->KeyboardInputs.KeyState[keyValue].HasFlag(KeyStateFlags.Down);
        if (isDown && !guardKeyWasDown)
            TryGuard();

        guardKeyWasDown = isDown;
    }

    private unsafe void TickGuardGamepad()
    {
        if (config.ActionGuardGamepadButton == GamepadButtons.None)
            return;

        if (gamepadState.Pressed(config.ActionGuardGamepadButton) > 0)
        {
            TryGuard();
            guardGamepadSuppressFrames = 3;
            SuppressGuardGamepadInput();
        }
    }

    private void HandleInput(PlayerActionInput input)
    {
        switch (input.Role)
        {
            case PlayerInputRole.Guard:
                TryGuard();
                break;
            default: // LightAttack / Skill1 / Skill2 → a light swing for this slice
                LightAttack(input.ActionId);
                break;
        }
    }

    private void LightAttack(uint actionId)
    {
        if (attackLockoutTimer > 0f)
            return;
        if (swingCooldown > 0f)
            return;
        if (!combatEngine.State.PlayerState.IsAlive)
            return;

        var duration = MathF.Max(0.05f, animationController.ResolveActionAnimationDuration(actionId));
        if (!combatEngine.TrySpendPlayerActionMp(actionId, duration, out _, out _))
            return;

        guardLockoutTimer = MathF.Max(guardLockoutTimer, duration * GuardCancelLockRatio);
        attackLockoutTimer = MathF.Max(attackLockoutTimer, duration);
        swingCooldown = config.LightSwingInterval;
        comboStep = comboTimer > 0f ? (comboStep + 1) % 3 : 0;
        comboTimer = config.LightComboWindow;

        // Soft-target by the action's real selection cone, then fan out its real shape/potency.
        var (range, angle, smallestAngle) = ResolveSelectionParams(actionId);
        var primary = hitbox.ResolvePrimary(range, angle, smallestAngle);
        var struck = primary != null
            ? combatEngine.ApplyPlayerActionMode(actionId, primary.State.EntityId, animationDuration: duration)
            : 0;
        if (struck == 0)
            animationController.PlayPlayerActionAnimationOnly(actionId); // reliable miss feedback
        else
            hitFeedback.TriggerHit(primary);
    }

    // Derive the soft-target selection cone from the action's real data. Any CIRCLE AoE is treated as
    // centred on the player (full ring) since Action Mode has no placed ground/target reticle; cones
    // and lines stay directional; ranged/magic uses a longer, wider cone + smallest-angle pick.
    private (float Range, float AngleDeg, bool SmallestAngle) ResolveSelectionParams(uint actionId)
    {
        var data = combatEngine.GetActionData(actionId);
        if (data == null)
            return (config.PlayerHitboxRange, config.PlayerHitboxAngleDeg, false);

        if (data.Shape is AoeShape.CircleSelf or AoeShape.Donut or AoeShape.Circle or AoeShape.GroundCircle)
            return (MathF.Max(data.Radius, 1f), 360f, false);

        var ranged = data.DamageType == SimDamageType.Magical || data.Range > 5f;
        var range = data.Range > 0f ? data.Range : (ranged ? config.RangedBasicRange : config.PlayerHitboxRange);
        var angle = ranged ? config.RangedSelectAngleDeg : config.PlayerHitboxAngleDeg;
        return (range, angle, ranged);
    }

    private bool TryGuard()
    {
        if (guardLockoutTimer > 0f)
            return false;

        return guard.TryGuard();
    }

    private void OnModeExit()
    {
        comboSink.Clear();
        guard.Reset();
        telegraphs.Clear();
        animationController.RestorePlayerGuardVisualState();
        swingCooldown = 0f;
        guardLockoutTimer = 0f;
        attackLockoutTimer = 0f;
        comboStep = 0;
        comboTimer = 0f;
        guardKeyWasDown = false;
        basicAttackKeyWasDown = false;
        guardGamepadSuppressFrames = 0;
        basicAttackGamepadSuppressFrames = 0;
    }

    private void SuppressGuardGamepadInputIfNeeded()
    {
        if (guardGamepadSuppressFrames <= 0)
            return;

        SuppressGamepadButton(config.ActionGuardGamepadButton);
        guardGamepadSuppressFrames--;
    }

    private void SuppressGuardGamepadInput() => SuppressGamepadButton(config.ActionGuardGamepadButton);

    private void SuppressBasicAttackGamepadInputIfNeeded()
    {
        if (basicAttackGamepadSuppressFrames <= 0)
            return;

        SuppressGamepadButton(config.ActionBasicAttackGamepadButton);
        basicAttackGamepadSuppressFrames--;
    }

    private void SuppressBasicAttackGamepadInput() => SuppressGamepadButton(config.ActionBasicAttackGamepadButton);

    private unsafe void SuppressGamepadButton(GamepadButtons button)
    {
        var mask = ToGamepadButtonsFlags(button);
        if (mask == GamepadButtonsFlags.None || gamepadState.GamepadInputAddress == nint.Zero)
            return;

        var input = (GamepadInputData*)gamepadState.GamepadInputAddress;
        ClearGamepadButton(ref input->Buttons, mask);
        ClearGamepadButton(ref input->ButtonsPressed, mask);
        ClearGamepadButton(ref input->ButtonsRepeat, mask);
        ClearGamepadButton(ref input->ButtonsReleased, mask);

        switch (button)
        {
            case GamepadButtons.R1: input->R1 = 0f; break;
            case GamepadButtons.L1: input->L1 = 0f; break;
            case GamepadButtons.R2: input->R2 = 0f; break;
            case GamepadButtons.L2: input->L2 = 0f; break;
            case GamepadButtons.East: input->Circle = 0f; break;
            case GamepadButtons.South: input->Cross = 0f; break;
            case GamepadButtons.West: input->Square = 0f; break;
            case GamepadButtons.North: input->Triangle = 0f; break;
        }
    }

    private static void ClearGamepadButton(ref GamepadButtonsFlags buttons, GamepadButtonsFlags mask)
        => buttons &= ~mask;

    private static GamepadButtonsFlags ToGamepadButtonsFlags(GamepadButtons button)
        => button switch
        {
            GamepadButtons.R1 => GamepadButtonsFlags.R1,
            GamepadButtons.L1 => GamepadButtonsFlags.L1,
            GamepadButtons.R2 => GamepadButtonsFlags.R2,
            GamepadButtons.L2 => GamepadButtonsFlags.L2,
            GamepadButtons.East => GamepadButtonsFlags.Circle,
            GamepadButtons.South => GamepadButtonsFlags.Cross,
            GamepadButtons.West => GamepadButtonsFlags.Square,
            GamepadButtons.North => GamepadButtonsFlags.Triangle,
            _ => GamepadButtonsFlags.None,
        };
}
