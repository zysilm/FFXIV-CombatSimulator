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
    private readonly Configuration config;
    private readonly ActionComboSink comboSink;
    private readonly PlayerHitboxResolver hitbox;
    private readonly PlayerGuardController guard;
    private readonly TelegraphSystem telegraphs;
    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly IGamepadState gamepadState;
    private readonly IPluginLog log;

    private float swingCooldown;
    private int comboStep;
    private float comboTimer;
    private bool wasActive;
    private bool guardKeyWasDown;
    private int guardGamepadSuppressFrames;

    public ActionModeController(
        Configuration config,
        ActionComboSink comboSink,
        PlayerHitboxResolver hitbox,
        PlayerGuardController guard,
        TelegraphSystem telegraphs,
        CombatEngine combatEngine,
        AnimationController animationController,
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

        if (!combatEngine.State.PlayerState.IsAlive)
        {
            guard.Reset();
            guardKeyWasDown = false;
            comboSink.Clear();
            telegraphs.Tick(dt);
            return;
        }

        if (swingCooldown > 0f) swingCooldown = MathF.Max(0f, swingCooldown - dt);
        if (comboTimer > 0f)
        {
            comboTimer -= dt;
            if (comboTimer <= 0f) comboStep = 0;
        }

        guard.Tick(dt);
        telegraphs.Tick(dt);
        SuppressGuardGamepadInputIfNeeded();
        TickGuardKey();
        TickGuardGamepad();
        comboSink.Drain(HandleInput);
        SuppressGuardGamepadInputIfNeeded();
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
            guard.TryGuard();

        guardKeyWasDown = isDown;
    }

    private unsafe void TickGuardGamepad()
    {
        if (config.ActionGuardGamepadButton == GamepadButtons.None)
            return;

        if (gamepadState.Pressed(config.ActionGuardGamepadButton) > 0)
        {
            if (guard.TryGuard())
            {
                guardGamepadSuppressFrames = 3;
                SuppressGuardGamepadInput();
            }
        }
    }

    private void HandleInput(PlayerActionInput input)
    {
        switch (input.Role)
        {
            case PlayerInputRole.Guard:
                guard.TryGuard();
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
        guard.Reset();
        telegraphs.Clear();
        swingCooldown = 0f;
        comboStep = 0;
        comboTimer = 0f;
        guardKeyWasDown = false;
        guardGamepadSuppressFrames = 0;
    }

    private void SuppressGuardGamepadInputIfNeeded()
    {
        if (guardGamepadSuppressFrames <= 0)
            return;

        SuppressGuardGamepadInput();
        guardGamepadSuppressFrames--;
    }

    private unsafe void SuppressGuardGamepadInput()
    {
        var mask = ToGamepadButtonsFlags(config.ActionGuardGamepadButton);
        if (mask == GamepadButtonsFlags.None || gamepadState.GamepadInputAddress == nint.Zero)
            return;

        var input = (GamepadInputData*)gamepadState.GamepadInputAddress;
        ClearGamepadButton(ref input->Buttons, mask);
        ClearGamepadButton(ref input->ButtonsPressed, mask);
        ClearGamepadButton(ref input->ButtonsRepeat, mask);
        ClearGamepadButton(ref input->ButtonsReleased, mask);

        switch (config.ActionGuardGamepadButton)
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
