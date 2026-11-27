// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Every way a battle can end on the result banner: a stable key (what the settings store), a short label (the
/// GUI's dropdown) and the default title and reason. The rules pick the situation
/// (<see cref="IBattleRule.DescribeOutcome"/>); the user's words, where set, replace the defaults when the banner
/// is shown (<see cref="Resolve"/>). Only overrides are stored, so a default changed here still reaches every
/// situation the user left alone.
/// </summary>
public static class OutcomeTexts
{
    public const string VictoryPlayerAlive = "Victory.PlayerAlive";
    public const string VictoryPlayerFell = "Victory.PlayerFell";
    public const string DefeatAnnihilated = "Defeat.Annihilated";
    public const string DefeatExecuted = "Defeat.Executed";
    public const string LordFellCaptivesExecuted = "WarringStates.LordFellCaptivesExecuted";
    public const string RemainsSecured = "WarringStates.RemainsSecured";
    public const string CorpseSeized = "WarringStates.CorpseSeized";

    /// <summary>One way a battle ends.</summary>
    public sealed record Situation(string Key, string Label, string Title, string Reason);

    /// <summary>Every situation, in the GUI's order.</summary>
    public static readonly IReadOnlyList<Situation> All = new Situation[]
    {
        new(VictoryPlayerAlive, "Victory (you survived)", "VICTORY", "Every enemy has been slain. Your army is victorious."),
        new(VictoryPlayerFell, "Victory (you fell)", "VICTORY", "Every enemy has been slain, though you fell in the battle."),
        new(DefeatAnnihilated, "Defeat: party annihilated", "ANNIHILATED", "Every member of your party lies dead on the field."),
        new(DefeatExecuted, "Defeat: party executed", "ROUTED", "Your party has been executed."),
        new(LordFellCaptivesExecuted, "Warring States: you fell, captives executed", "ROUTED", "You have fallen and your captured soldiers have been executed."),
        new(RemainsSecured, "Warring States: won after you fell", "REMAINS SECURED", "You fell, but your remains are secured and will be carried back to Eorzea."),
        new(CorpseSeized, "Warring States: corpse carried off", "DISGRACED", "Your corpse has been dragged off as a trophy. Your army is executed."),
    };

    private static readonly Dictionary<string, Situation> ByKey = BuildIndex();

    private static Dictionary<string, Situation> BuildIndex()
    {
        var index = new Dictionary<string, Situation>();
        foreach (var s in All)
            index[s.Key] = s;
        return index;
    }

    /// <summary>The situation with this key, or null.</summary>
    public static Situation? Find(string? key) => key != null && ByKey.TryGetValue(key, out var s) ? s : null;

    /// <summary>The default words of a situation (its key carried along).</summary>
    public static OutcomeText Default(string key)
    {
        var s = ByKey[key];
        return new OutcomeText(s.Title, s.Reason) { Key = s.Key };
    }

    /// <summary>The words shown: the user's where set (non-empty), the given words otherwise.</summary>
    public static OutcomeText Resolve(OutcomeText text, IReadOnlyDictionary<string, OutcomeTextOverride>? overrides)
    {
        if (overrides == null || text.Key == null || !overrides.TryGetValue(text.Key, out var o) || o == null)
            return text;
        return text with
        {
            Title = string.IsNullOrEmpty(o.Title) ? text.Title : o.Title!,
            Reason = string.IsNullOrEmpty(o.Reason) ? text.Reason : o.Reason!,
        };
    }
}

/// <summary>The user's words for one banner situation; an empty field keeps the default.</summary>
public sealed class OutcomeTextOverride
{
    public string? Title { get; set; }
    public string? Reason { get; set; }

    public bool IsEmpty => string.IsNullOrEmpty(Title) && string.IsNullOrEmpty(Reason);
}
