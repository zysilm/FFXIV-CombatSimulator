// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using CombatSimulator.Npcs;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Action Mode: a committed enemy attack becomes a telegraph instead of instant damage.
/// Cast time is treated as lead-in warning time; the parry approach circle stays tied to
/// the final strike windup so long casts do not become slow parry circles.
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
        var windup = MathF.Max(0.01f, config.ActionWindupSeconds);
        var leadIn = MathF.Max(0f, req.CastTime - windup);
        var total = leadIn + windup;

        source.State.AnimationLock = MathF.Max(source.State.AnimationLock, total);
        telegraphs.Spawn(source, req, windup, leadIn);
        return true;
    }
}
