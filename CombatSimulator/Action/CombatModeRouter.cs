// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Selects the active enemy-attack executor by mode, so call sites (NpcAiController)
/// stay mode-agnostic: <c>router.AttackExecutor.Execute(...)</c>. Off = behavior-
/// equivalent instant path; on = telegraphed. Fighting Mode forces the telegraphed
/// path while engaged (its guard timing needs windups), independent of Action Mode.
/// </summary>
public sealed class CombatModeRouter
{
    private readonly Configuration config;
    private readonly IAttackExecutor instant;
    private readonly IAttackExecutor telegraphed;
    private readonly Func<bool>? forceTelegraphed;

    public CombatModeRouter(Configuration config, IAttackExecutor instant, IAttackExecutor telegraphed,
        Func<bool>? forceTelegraphed = null)
    {
        this.config = config;
        this.instant = instant;
        this.telegraphed = telegraphed;
        this.forceTelegraphed = forceTelegraphed;
    }

    public IAttackExecutor AttackExecutor =>
        config.ActionMode || forceTelegraphed?.Invoke() == true ? telegraphed : instant;
}
