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
    private int chainCount;

    public bool IsGuardActive => activeTimer > 0f;
    /// <summary>Attacks absorbed by the in-progress guard chain (live).</summary>
    public int ChainCount => chainCount;
    /// <summary>Attacks absorbed by the last completed chain — the instantaneous count output.</summary>
    public int LastChainGuardCount { get; private set; }
    /// <summary>Fired when a guard chain ends, with how many attacks it absorbed (always &gt; 0).</summary>
    public event Action<int>? OnChainGuardResolved;

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

        // Can't start a new guard while one is already open (the chain absorbs follow-ups for
        // you — no need to re-press), or while recovering/cooling down from the last chain.
        if (activeTimer > 0f || cooldownTimer > 0f || recoveryTimer > 0f)
            return false;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return false;

        activeTimer = MathF.Max(0.01f, config.GuardActiveWindow);
        chainCount = 0;
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
        {
            activeTimer = MathF.Max(0f, activeTimer - dt);
            if (activeTimer <= 0f)
                EndChain();
        }

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

        // Absorb this attack.
        chainCount++;
        animationController.PlayPlayerGuardSuccess();
        log.Debug($"Chain guard absorbed attack #{chainCount}.");

        // Cap the chain: a single press absorbs a short burst, but it must not tank an unbroken
        // swarm forever (each block refreshes the window, so a continuous crowd would otherwise
        // keep one press alive indefinitely). Once the cap is hit, end the chain NOW so the window
        // closes and further attacks land until the player re-guards (after recovery + cooldown).
        var maxChain = config.GuardMaxChain;
        if (maxChain > 0 && chainCount >= maxChain)
        {
            EndChain();
            return;
        }

        // Otherwise KEEP the guard open: any further attack arriving within the chain window is
        // absorbed by the same press, so a burst of attacks costs one parry, not many.
        activeTimer = MathF.Max(activeTimer, MathF.Max(0.01f, config.ChainGuardWindow));
    }

    // The chain ends when the open window finally lapses with no new attack. Output the count and
    // only NOW apply recovery + cooldown, so the cooldown never cuts a chain short.
    private void EndChain()
    {
        activeTimer = 0f;
        LastChainGuardCount = chainCount;
        if (chainCount > 0)
        {
            OnChainGuardResolved?.Invoke(chainCount);
            log.Debug($"Guard chain ended: absorbed {chainCount} attack(s).");
        }
        chainCount = 0;
        recoveryTimer = MathF.Max(0f, config.GuardRecovery);
        cooldownTimer = MathF.Max(0f, config.GuardCooldown);
    }

    public void Reset()
    {
        activeTimer = 0f;
        recoveryTimer = 0f;
        cooldownTimer = 0f;
        chainCount = 0;
    }
}
