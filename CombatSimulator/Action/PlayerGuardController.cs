using System;
using CombatSimulator.Animation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Action Mode guard: a stationary, timing-based defensive input. Pressing guard
/// opens a short active window; enemy telegraphs that resolve into the player
/// during that window are blocked, play guard feedback, and deal no damage.
/// A mistimed guard has recovery but no invulnerability.
/// </summary>
public sealed class PlayerGuardController
{
    private readonly AnimationController animationController;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly Func<bool> playerCanGuard;

    private float activeTimer;
    private float recoveryTimer;
    private float cooldownTimer;

    public bool IsGuardActive => activeTimer > 0f;

    public PlayerGuardController(
        AnimationController animationController,
        Configuration config,
        Func<bool> playerCanGuard,
        IPluginLog log)
    {
        this.animationController = animationController;
        this.config = config;
        this.playerCanGuard = playerCanGuard;
        this.log = log;
    }

    public bool TryGuard()
    {
        if (!playerCanGuard())
        {
            Reset();
            return false;
        }

        if (cooldownTimer > 0f || recoveryTimer > 0f)
            return false;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return false;

        activeTimer = MathF.Max(0.01f, config.GuardActiveWindow);
        recoveryTimer = MathF.Max(activeTimer, config.GuardRecovery);
        cooldownTimer = MathF.Max(0f, config.GuardCooldown);
        animationController.PlayPlayerGuardAnimation();
        return true;
    }

    public void Tick(float dt)
    {
        if (!playerCanGuard())
        {
            Reset();
            return;
        }

        if (activeTimer > 0f)
            activeTimer = MathF.Max(0f, activeTimer - dt);

        if (recoveryTimer > 0f)
            recoveryTimer = MathF.Max(0f, recoveryTimer - dt);

        if (cooldownTimer > 0f)
            cooldownTimer = MathF.Max(0f, cooldownTimer - dt);
    }

    public void NotifyPerfectGuard()
    {
        if (!playerCanGuard())
        {
            Reset();
            return;
        }

        activeTimer = 0f;
        animationController.PlayPlayerGuardSuccess();
        log.Debug("Perfect guard resolved.");
    }

    public void Reset()
    {
        activeTimer = 0f;
        recoveryTimer = 0f;
        cooldownTimer = 0f;
    }
}
