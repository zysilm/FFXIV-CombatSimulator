// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Action = Lumina.Excel.Sheets.Action;

namespace CombatSimulator.Npcs;

/// <summary>
/// The one place that maps an Action row onto <see cref="MonsterActionCandidate"/>, so the skill
/// kit and the model-family attack animation pool judge a row with the same columns.
/// </summary>
public static class MonsterActionRows
{
    public static MonsterActionCandidate ToCandidate(in Action a) => new(
        a.RowId,
        a.Name.ExtractText(),
        a.CastType,
        a.Range,
        a.EffectRange,
        a.XAxisModifier,
        a.Cast100ms * 0.1f,
        a.Recast100ms * 0.1f,
        a.ActionCategory.RowId,
        a.AttackType.RowId,
        a.CanTargetHostile,
        a.CanTargetParty || a.CanTargetAlly || a.CanTargetAlliance || a.CanTargetOwnPet || a.CanTargetPartyPet,
        a.DeadTargetBehaviour == 1,
        a.Omen.RowId != 0,
        a.AnimationEnd.RowId <= ushort.MaxValue ? (ushort)a.AnimationEnd.RowId : (ushort)0);
}
