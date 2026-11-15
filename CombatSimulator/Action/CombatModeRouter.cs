// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Where an enemy's committed attack goes (NpcAiController calls <c>router.AttackExecutor.Execute(...)</c>): the
/// telegraphed executor, observed so listeners hear every accepted attack.
/// </summary>
public sealed class CombatModeRouter
{
    public CombatModeRouter(IAttackExecutor telegraphed)
    {
        AttackExecutor = new ObservedExecutor(this, telegraphed);
    }

    public IAttackExecutor AttackExecutor { get; }

    /// <summary>
    /// Raised after an executor accepted an attack (telegraphed: at windup start). Lets listeners
    /// such as enemy voices react to the commit without adding raise points to every AI call site.
    /// </summary>
    public Action<Npcs.SimulatedNpc, NpcAttackRequest>? AttackCommitted { get; set; }

    private sealed class ObservedExecutor(CombatModeRouter router, IAttackExecutor inner) : IAttackExecutor
    {
        public bool Execute(Npcs.SimulatedNpc source, in NpcAttackRequest req)
        {
            var ok = inner.Execute(source, req);
            if (ok)
                router.AttackCommitted?.Invoke(source, req);
            return ok;
        }
    }
}
