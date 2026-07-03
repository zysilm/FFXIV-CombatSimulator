using System;
using CombatSimulator.ActionCombat;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace CombatSimulator.Fighting;

/// <summary>
/// Fighting Mode player combat: dedicated guard input (this stage) and the
/// weapon-sweep attack state machine (next stage). Reuses the mode-agnostic
/// Action-combat components (PlayerGuardController / TelegraphSystem /
/// HitFeedbackController) — those are ticked centrally by the plugin, not here.
/// </summary>
public sealed unsafe class FightingCombatController
{
    private readonly Configuration config;
    private readonly PlayerGuardController guard;
    private readonly FightingModeController fighting;
    private readonly CombatSimulator.Simulation.CombatEngine combatEngine;
    private readonly IGamepadState gamepad;
    private readonly IPluginLog log;

    private bool guardWasDown;

    public FightingCombatController(
        Configuration config,
        PlayerGuardController guard,
        FightingModeController fighting,
        CombatSimulator.Simulation.CombatEngine combatEngine,
        IGamepadState gamepad,
        IPluginLog log)
    {
        this.config = config;
        this.guard = guard;
        this.fighting = fighting;
        this.combatEngine = combatEngine;
        this.gamepad = gamepad;
        this.log = log;
    }

    public void Tick(float dt)
    {
        if (!config.FightingMode || !fighting.IsEngaged || !combatEngine.State.PlayerState.IsAlive)
        {
            guardWasDown = false;
            return;
        }

        TickGuardInput();
    }

    private void TickGuardInput()
    {
        var down = false;
        try
        {
            var io = ImGui.GetIO();
            if (!io.WantCaptureKeyboard && !io.WantTextInput)
            {
                var fw = GameFramework.Instance();
                if (fw != null)
                    down = fw->KeyboardInputs.KeyState[config.FightingModeGuardKey].HasFlag(KeyStateFlags.Down);
            }

            down |= gamepad.Raw(config.FightingModeGuardGamepadButton) != 0;
        }
        catch { }

        if (down && !guardWasDown)
            guard.TryGuard();
        guardWasDown = down;
    }
}
