// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using CombatSimulator.Simulation;

namespace CombatSimulator.Battle;

/// <summary>
/// The questions the battle asks of the war-rule switches (docs/PLUGIN_ARCHITECTURE.md §5.3), one named
/// query each, instead of reading <c>WarFallenRule</c>, <c>WarCaptives</c> and <c>WarRetreat</c> where
/// they are needed. Nothing is cached: every query reads the settings (and the battle's state) when it
/// is asked, as the old expressions did every frame, so changing a switch mid-battle still takes effect
/// at once. Game-free; owned by <c>CombatEngine</c> (<c>CombatEngine.Rules</c>).
/// </summary>
public sealed class BattleRules
{
    private readonly IBattleRuleSettings settings;
    private readonly Func<bool> battleActive;
    private readonly Func<bool> lordAlive;

    /// <param name="settings">The rule switches, read live.</param>
    /// <param name="battleActive">Whether a battle is under way (<c>CombatEngine.IsActive</c>).</param>
    /// <param name="lordAlive">Whether the player is alive (<c>State.PlayerState.IsAlive</c>).</param>
    public BattleRules(IBattleRuleSettings settings, Func<bool> battleActive, Func<bool> lordAlive)
    {
        this.settings = settings;
        this.battleActive = battleActive;
        this.lordAlive = lordAlive;
    }

    /// <summary>Warring States: the player is the lord (the banner's wording, the lord's fall).</summary>
    public bool PlayerIsTheLord => settings.FallenRule == WarFallenRule.WarringStates;

    /// <summary>Spring and Autumn: allies raise or free the fallen.</summary>
    public bool RaisesTheFallen => settings.FallenRule == WarFallenRule.SpringAndAutumn;

    /// <summary>Hostages (Warring States only): a lethal blow on the player's side takes a captive instead.</summary>
    public bool TakesCaptivesOnLethalBlow => settings.Hostages && PlayerIsTheLord;

    /// <summary>
    /// Warring States with Retreat: the lord's body is fought over, so nobody takes it the moment the
    /// lord falls (the killer is not possessed).
    /// </summary>
    public bool LordBodyIsFoughtOver => PlayerIsTheLord && settings.Retreat;

    /// <summary>Retreat: when the lord falls, the retainers fall back to the body (else they are all taken).</summary>
    public bool RetainersRetreatWithTheBody => settings.Retreat;

    /// <summary>Warring States, a battle under way, and the lord dead.</summary>
    public bool LordIsDown => PlayerIsTheLord && battleActive() && !lordAlive();

    /// <summary>Warring States with Retreat, a battle under way, and the lord dead: the retainers guard the body.</summary>
    public bool LordBodyIsGuarded => PlayerIsTheLord && settings.Retreat && battleActive() && !lordAlive();
}
