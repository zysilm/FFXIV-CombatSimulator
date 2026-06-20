using System;
using CombatSimulator.Npcs;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Action Mode: a committed enemy attack becomes a telegraph (起手快照) instead of
/// instant damage. We snapshot the danger zone now and hold the enemy in its windup
/// (anim-lock = windup) so it visibly winds up; <see cref="TelegraphSystem"/> resolves
/// the hit at the active frame against the target's live position.
/// </summary>
public sealed class TelegraphedAttackExecutor : IAttackExecutor
{
    private readonly TelegraphSystem telegraphs;
    private readonly Configuration config;

    public TelegraphedAttackExecutor(TelegraphSystem telegraphs, Configuration config)
    {
        this.telegraphs = telegraphs;
        this.config = config;
    }

    public bool Execute(SimulatedNpc source, in NpcAttackRequest req)
    {
        // The swing animation plays now (at Spawn) and the hit resolves after the windup,
        // so the animation reads as the windup (swing-start → weapon-connect).
        var windup = MathF.Max(req.CastTime, config.ActionWindupSeconds);
        // Hold the enemy in its windup so it doesn't immediately start another attack.
        source.State.AnimationLock = MathF.Max(source.State.AnimationLock, windup);
        telegraphs.Spawn(source, req, windup);
        return true;
    }
}
