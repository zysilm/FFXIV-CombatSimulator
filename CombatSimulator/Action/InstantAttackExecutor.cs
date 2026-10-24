// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Behavior-equivalent default: routes straight to <see cref="CombatEngine.ProcessNpcAction"/>,
/// which already applies damage + visuals with no range gate (range gating lives in
/// the NPC AI). Used whenever Action Mode is off.
/// </summary>
public sealed class InstantAttackExecutor : IAttackExecutor
{
    private readonly CombatEngine engine;
    private readonly Configuration? config;
    private readonly System.Action<SimulatedNpc, uint>? playWhiff;

    public InstantAttackExecutor(CombatEngine engine, Configuration? config = null,
        System.Action<SimulatedNpc, uint>? playWhiff = null)
    {
        this.engine = engine;
        this.config = config;
        this.playWhiff = playWhiff;
    }

    public bool Execute(SimulatedNpc source, in NpcAttackRequest req)
    {
        // A cast that began with a snapshotted area resolves against that area. Everyone may have
        // left it: that is a dodge, not a failed attack — the skill is spent and the swing whiffs.
        if (req.Area is { } castArea)
        {
            var hit = engine.ProcessNpcAction(source, req.ActionId, req.TargetId, req.Potency, req.Style, req.Radius,
                area: castArea).Success;
            if (!hit)
                playWhiff?.Invoke(source, req.ActionId);
            return true;
        }

        // Real-shape option, instant skill: the live area decides the splash, but only when the
        // primary is inside it — with no cast there was nothing to dodge, so an instant skill keeps
        // the legacy guarantee of landing on its target instead of whiffing every other frame.
        TelegraphShape? area = null;
        if (config?.EnemyTelegraphRealShapes == true && !req.IsAutoAttack)
        {
            var target = engine.State.GetEntity(req.TargetId);
            if (target != null)
            {
                var snap = TelegraphSystem.SnapshotShape(engine, source, req, target);
                if (snap.IsArea && snap.Contains(engine.GetSimulatedEntityPosition(target)))
                    area = snap;
            }
        }

        return engine.ProcessNpcAction(source, req.ActionId, req.TargetId, req.Potency, req.Style, req.Radius,
            area: area).Success;
    }
}
