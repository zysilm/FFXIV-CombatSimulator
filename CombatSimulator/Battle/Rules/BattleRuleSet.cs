// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// The battle's rule parts (docs/PLUGIN_ARCHITECTURE.md §5.1, §5.4), asked in one fixed order
/// (<see cref="BattleRuleFactory.CreateAll"/>). The parts live as long as the set; the composition is
/// read from the switches live, as the old code read them every frame.
/// </summary>
/// <remarks>
/// <para>What the composition decides: which parts tick (<see cref="TickBeforeAllies"/>) and hear the lord
/// fall. A part that has left it gets <see cref="IBattleRule.OnDeactivated"/> once, at its own place in the
/// tick pass, where the old code dropped its state on the first frame the switch was off (so another part's
/// tick before it still sees that state that frame, as it did).</para>
/// <para>The queries (an ally taken over, who kneels, who tends, an enemy's holder, both sides' target
/// weights, a forced redraw, the leash, the status word) are asked of the live parts only (<see cref="Live"/>):
/// those in the composition, and one that has left it but not yet had <see cref="IBattleRule.OnDeactivated"/>.
/// The second matters on the frame a switch changes: the enemies draw their targets before
/// <see cref="TickBeforeAllies"/>, so a part that has just left still answers from what it holds, as the old
/// code did until it dropped that state. The filter keeps the old answers: a part's task state (a rescue
/// under way, the one praying) is made only in its tick and dropped in <see cref="IBattleRule.OnDeactivated"/>
/// (and on reset and dismissal), so a part that is not live holds none; and the answers that do not read the
/// part's own state (the guard's and the seizure's) are gated by <see cref="BattleRules.LordBodyIsGuarded"/>,
/// which holds only while both are in the composition. <see cref="BeginAllyDraw"/> still goes to every part:
/// the guard's looks up the lord's body (snapping a point to the floor) on every draw whatever the rule, as
/// the old code did, and that call count is kept.</para>
/// <para>Combining: a tick or event goes to each part in order; a yes/no query is "any" (stops at the first
/// yes); a value query takes the first non-empty answer; a weight passes through every part in order.</para>
/// </remarks>
public sealed class BattleRuleSet
{
    private readonly IBattleRule[] parts;
    private readonly bool[] ticked;
    private readonly IBattleRuleSettings settings;

    public BattleRuleSet(BattleContext context, IBattleRule[] parts)
    {
        Context = context;
        settings = context.Rules.Settings;
        this.parts = parts;
        ticked = new bool[parts.Length];
        context.RuleSet = this;
    }

    public BattleContext Context { get; }

    /// <summary>Every part, in order.</summary>
    public IReadOnlyList<IBattleRule> Parts => parts;

    /// <summary>The first part of this type.</summary>
    public T Get<T>() where T : class, IBattleRule
    {
        foreach (var part in parts)
            if (part is T found)
                return found;
        throw new KeyNotFoundException(typeof(T).Name);
    }

    private bool Composed(IBattleRule part)
        => BattleRuleFactory.Composes(part.Id, settings.FallenRule, settings.Hostages, settings.Retreat);

    /// <summary>
    /// The part answers queries: it is in the composition, or it has left it and not yet had
    /// <see cref="IBattleRule.OnDeactivated"/> (its tick ran in the last pass and the next has not come).
    /// </summary>
    private bool Live(int i) => Composed(parts[i]) || ticked[i];

    /// <summary>Once a frame before the allies act (where <c>TickRescues</c> and <c>TickLord</c> ran).</summary>
    public void TickBeforeAllies(float deltaTime)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            if (Composed(parts[i]))
            {
                ticked[i] = true;
                parts[i].TickBeforeAllies(Context, deltaTime);
            }
            else if (ticked[i])
            {
                ticked[i] = false;
                parts[i].OnDeactivated(Context);
            }
        }
    }

    /// <summary>The lord has fallen: every composed part hears it, in order.</summary>
    public void AnnounceLordDown()
    {
        foreach (var part in parts)
            if (Composed(part))
                part.OnLordDown(Context);
    }

    public void Reset()
    {
        foreach (var part in parts)
            part.OnReset(Context);
    }

    public void AlliesDismissed()
    {
        foreach (var part in parts)
            part.OnAlliesDismissed(Context);
    }

    public bool TakesOverAlly(CombatCompanion ally, float deltaTime)
    {
        for (var i = 0; i < parts.Length; i++)
            if (Live(i) && parts[i].TakesOverAlly(Context, ally, deltaTime))
                return true;
        return false;
    }

    public bool Exposes(uint friendId)
    {
        for (var i = 0; i < parts.Length; i++)
            if (Live(i) && parts[i].Exposes(Context, friendId))
                return true;
        return false;
    }

    public bool Tends(uint allyId)
    {
        for (var i = 0; i < parts.Length; i++)
            if (Live(i) && parts[i].Tends(Context, allyId))
                return true;
        return false;
    }

    public uint HolderFor(SimulatedNpc enemy)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            if (!Live(i))
                continue;
            var holder = parts[i].HolderFor(Context, enemy);
            if (holder != 0)
                return holder;
        }
        return 0;
    }

    public float EnemyTargetWeight(in EnemyTargetQuery query, float weight)
    {
        for (var i = 0; i < parts.Length; i++)
            if (Live(i))
                weight = parts[i].EnemyTargetWeight(Context, query, weight);
        return weight;
    }

    /// <summary>An ally's target draw begins: every part hears it, live or not (see the remarks).</summary>
    public void BeginAllyDraw(CombatCompanion ally)
    {
        foreach (var part in parts)
            part.BeginAllyDraw(Context, ally);
    }

    public float AllyTargetWeight(in AllyTargetQuery query, float weight)
    {
        for (var i = 0; i < parts.Length; i++)
            if (Live(i))
                weight = parts[i].AllyTargetWeight(Context, query, weight);
        return weight;
    }

    public bool ForcesRedraw(CombatCompanion ally, SimulatedNpc current)
    {
        for (var i = 0; i < parts.Length; i++)
            if (Live(i) && parts[i].ForcesRedraw(Context, ally, current))
                return true;
        return false;
    }

    public IAllyLeash? LeashFor(CombatCompanion ally)
    {
        for (var i = 0; i < parts.Length; i++)
            if (Live(i) && parts[i].LeashFor(Context, ally) is { } leash)
                return leash;
        return null;
    }

    /// <summary>The word before a name on the HP bars: the first live part's that has one, else "Fighting".</summary>
    public string StatusWord(SimulatedEntityState who)
    {
        for (var i = 0; i < parts.Length; i++)
            if (Live(i) && parts[i].StatusWord(Context, who) is { } word)
                return word;
        return "Fighting";
    }

    // ---- the engine side (stage B6) ----

    /// <summary>
    /// Once a frame while a battle is under way, after the engine and before the enemy AI (the plugin's war
    /// tick): every part, composed or not, as the corpse seizure ran every frame whatever the switches (it
    /// lets go of its seizers and its grab on its own).
    /// </summary>
    public void TickAfterEngine(float deltaTime)
    {
        foreach (var part in parts)
            part.TickAfterEngine(Context, deltaTime);
    }

    /// <summary>
    /// A blow that would fell one of the player's side: the composed parts in order, the first answer other
    /// than <see cref="LethalBlow.Default"/>. The composition, not <see cref="Live"/>: the engine read the
    /// switches themselves at the blow.
    /// </summary>
    public LethalBlow OnLethalBlow(SimulatedEntityState victim, Combatant by, bool primary)
    {
        foreach (var part in parts)
        {
            if (!Composed(part))
                continue;
            var blow = part.OnLethalBlow(Context, victim, by, primary);
            if (blow != LethalBlow.Default)
                return blow;
        }
        return LethalBlow.Default;
    }

    /// <summary>When the player dies, whether Enemy Control leaves the killer alone: any composed part says so.</summary>
    public bool SuppressesKillerPossession()
    {
        foreach (var part in parts)
            if (Composed(part) && part.SuppressesKillerPossession(Context))
                return true;
        return false;
    }

    /// <summary>
    /// The result banner's title and reason: every part is asked in order and the last answer stands (the base
    /// part words every battle; a part that ends a battle its own way speaks after it). Every part, not only the
    /// composition: the body carried off stays the battle's word after the switch is turned off, as it did.
    /// </summary>
    public OutcomeText DescribeOutcome(bool playerWon)
    {
        OutcomeText? text = null;
        foreach (var part in parts)
            if (part.DescribeOutcome(Context, playerWon) is { } said)
                text = said;
        return text.GetValueOrDefault();
    }
}
