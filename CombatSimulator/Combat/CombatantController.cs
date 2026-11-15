// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Combat;

/// <summary>
/// The one controller type for every combatant (refactor stage B9, docs/PLUGIN_ARCHITECTURE.md §2–§3):
/// the seams, the per-combatant turn both sides run, and every per-combatant decision whose side
/// difference is a policy of the combatant (<see cref="Combatant.Policy"/>: how far it reaches, how it
/// acts, how fast it moves and turns). One instance per side, each ticked at its own point of the frame
/// (docs/REFACTOR_PLAN.md §4, "must preserve" #2): the allies' (<c>CombatCompanionManager</c>) before the
/// crowd step and also out of combat, the enemies' (<c>NpcAiController</c>) after it and only in combat.
/// <para>A side's controller keeps what design §2.2 / §3.3 leave with that side: the enemies' sentries,
/// patrols, tactics, casts and telegraph snapshots and their movement pass; the allies' spawning, command
/// anchor, rescue, prayer, captives, where to go without a target, and (for now) both sides' target draws.
/// Death stays each side's (<see cref="OnDeadTurn"/>): the enemies' death animation and ragdoll come from
/// the engine's 0.5 s queue, the allies' from their own next turn.</para>
/// </summary>
/// <typeparam name="TSelf">The side's combatant (<see cref="SimulatedNpc"/>, <c>CombatCompanion</c>).</typeparam>
public abstract unsafe class CombatantController<TSelf> : IDisposable where TSelf : Combatant
{
    protected readonly ICombatLoopEngine combatEngine;
    // The game objects, the movement block hook, the visual states and the animations (stage B4a:
    // GameCombatWorld in the plugin, a flat fake world in Tests/CombatReplay).
    protected readonly ICombatWorld world;
    // Where attacks go (GameAttackSink in the plugin): the enemies' attack router and telegraph
    // snapshot, the engine's companion action.
    protected readonly IAttackSink attackSink;
    protected readonly ICombatAiSettings config;
    protected readonly IPluginLog log;
    // Is this fighter's telegraph still running (by its simulated entity id); asked only for a side whose
    // ActPolicy telegraphs.
    protected readonly Func<uint, bool> isTelegraphBusy;

    /// <summary>
    /// The side's shared skill-start budget, used by a side whose <see cref="ActPolicy.UsesSkillTokens"/>
    /// (the enemies: one bucket for all of them); that side refills it once a frame (<see cref="RefillSkillTokens"/>).
    /// </summary>
    private readonly SkillTokenBucket skillTokens;

    private Func<uint, int>? mpPriceOf;

    protected CombatantController(
        ICombatLoopEngine combatEngine,
        ICombatWorld world,
        IAttackSink attackSink,
        ICombatAiSettings config,
        IPluginLog log,
        Func<uint, bool>? isTelegraphBusy = null)
    {
        this.combatEngine = combatEngine;
        this.world = world;
        this.attackSink = attackSink;
        this.config = config;
        this.log = log;
        this.isTelegraphBusy = isTelegraphBusy ?? (_ => false);
        skillTokens = new SkillTokenBucket(config);
    }

    /// <summary>The crowd on the plugin's navigation mesh: the only thing that moves either side. Null or not ready: they stand.</summary>
    public ICrowdAgents? Crowd { protected get; set; }

    /// <summary>
    /// The random numbers behind the side's own decisions (the enemies: timer stagger, engage jitter,
    /// patrol legs, which ready skill, sidesteps; the allies: the stagger of a new ally's first attack and
    /// skills). The shared generator, unless a caller wants a repeatable run and gives a seeded one. Who
    /// fights whom is drawn separately (<c>CombatCompanionManager.AggroSeedSource</c>).
    /// </summary>
    public Random RandomSource { protected get; set; } = Random.Shared;

    /// <summary>One frame of the side, at its own point of the frame (see the class summary).</summary>
    public abstract void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> enemies);

    public abstract void Dispose();

    // ---- The turn both sides run ----

    /// <summary>
    /// The head of a combatant's turn, both sides: its animation lock and cooldowns run down; a dead one
    /// is handed to its side (<see cref="OnDeadTurn"/>) and the turn ends (false); a living one regains
    /// MP at the one rate both sides have (<see cref="CombatBudgetModel.EnemyMpRegenPerSecond"/>).
    /// </summary>
    protected bool BeginTurn(TSelf self, float deltaTime)
    {
        TickActionTimers(self, deltaTime);
        if (!self.State.IsAlive)
        {
            OnDeadTurn(self);
            return false;
        }

        // MP comes back steadily (and with each auto-attack, in the step), the same for both sides.
        self.State.RegenerateMp(CombatBudgetModel.EnemyMpRegenPerSecond, deltaTime);
        return true;
    }

    /// <summary>A dead combatant's turn: its side's death handling (see the class summary).</summary>
    protected abstract void OnDeadTurn(TSelf self);

    /// <summary>
    /// The animation lock and every skill cooldown run down by <paramref name="deltaTime"/>, to 0 at the
    /// least. (Before stage B9 the enemies skipped a value already at 0 and ran the lock down only once
    /// alive; neither changes anything: the values are never negative, a dead fighter's lock is cleared.)
    /// </summary>
    protected static void TickActionTimers(Combatant self, float deltaTime)
    {
        self.State.AnimationLock = Math.Max(0, self.State.AnimationLock - deltaTime);
        foreach (var skill in self.Behavior.Skills)
            skill.CooldownRemaining = Math.Max(0, skill.CooldownRemaining - deltaTime);
    }

    /// <summary>Drops whatever it was doing: no cast, no lock.</summary>
    protected static void ClearActionState(Combatant self)
    {
        self.State.IsCasting = false;
        self.State.CastActionId = 0;
        self.State.CastTimeElapsed = 0;
        self.State.CastTimeTotal = 0;
        self.State.CastTargetId = 0;
        self.State.AnimationLock = 0;
    }

    /// <summary>
    /// With the target within reach: a ready skill or the auto-attack (<see cref="CombatStep"/>). What the
    /// step is given follows the combatant's <see cref="ActPolicy"/>: the side's generator when it picks a
    /// random ready skill, the side's token bucket when it uses tokens, the telegraph query when it
    /// telegraphs.
    /// </summary>
    protected StepResult Act<TTarget>(TSelf self, TTarget target, float gap, float deltaTime, ICombatStepSink<TSelf, TTarget> sink)
    {
        var act = self.Policy.Act;
        return CombatStep.Run(self, target, gap, deltaTime, sink, new CombatStepContext
        {
            Settings = config,
            Rng = act.Choice == SkillChoice.RandomReady ? RandomSource : null,
            Tokens = act.UsesSkillTokens ? skillTokens : null,
            IsTelegraphBusy = act.UsesCastAndTelegraph ? isTelegraphBusy : null,
            MpPriceOf = MpPrice,
            Pace = ActionPace(),
        });
    }

    /// <summary>Once a frame, before anyone of a token-using side acts.</summary>
    protected void RefillSkillTokens(float deltaTime) => skillTokens.Refill(deltaTime);

    // ---- Per-combatant decisions, the side's difference a policy ----

    /// <summary>
    /// How far it can attack from right now: its auto-attack (a caster's is a melee swing), or the longest
    /// skill it could use this moment. <see cref="ReachPolicy"/>: the settings' range scale on a side
    /// scaled by them, a melee floor, the HP gate.
    /// </summary>
    protected float ReachNowOf(Combatant self)
    {
        var reach = self.Policy.Reach;
        return CombatantMath.ReachNow(self.Behavior, self.State, reach,
            reach.ScaledBySettings ? ReachScale.From(config) : default, MpPrice);
    }

    /// <summary>Its running speed (<see cref="MovePolicy"/>: a scale on its own speed, a fallback).</summary>
    protected static float MoveSpeedOf(Combatant self)
        => CombatantMath.MoveSpeed(self.Behavior.MoveSpeed, self.Policy.Move);

    /// <summary>Its facing turn rate, rad/s (<see cref="MovePolicy"/>: slower when big, with enhanced tactics on, on a side that turns so).</summary>
    protected float TurnRateOf(Combatant self)
    {
        var move = self.Policy.Move;
        return move.TurnSlowsWithSize
            ? CombatantMath.TurnRate(move, config.EnhancedEnemyTactics, world.HitboxRadiusOf(self))
            : CombatantMath.TurnRate(move, false, 0f);
    }

    /// <summary>One frame of turning toward a point at its turn rate, written so the server does not undo it.</summary>
    protected void TurnToward(Combatant self, Vector3 point, float deltaTime)
    {
        if (!CombatantMath.TurnToward(world.PositionOf(self), world.RotationOf(self), point, TurnRateOf(self), deltaTime, out var rotation))
            return;
        world.SetApproachRotation(self, rotation);
    }

    /// <summary>Whether the crowd has it walking (horizontal speed above 0.3 m/s); <paramref name="flat"/> is that velocity.</summary>
    protected static bool IsWalking(Vector3 velocity, out Vector3 flat)
    {
        flat = new Vector3(velocity.X, 0f, velocity.Z);
        return flat.LengthSquared() > 0.3f * 0.3f;
    }

    // ---- Shared small pieces ----

    /// <summary>A skill's price against the player's pool (0 when the action is unknown).</summary>
    protected Func<uint, int> MpPrice => mpPriceOf ??= MpPriceOf;

    private int MpPriceOf(uint actionId) => combatEngine.GetActionData(actionId)?.MpCost ?? 0;

    /// <summary>
    /// Action Mode makes both sides act faster (auto delays and skill cooldowns are divided by this), so
    /// the fight runs at action-game pace instead of the ~3 s tab-target cadence.
    /// </summary>
    protected float ActionPace() => CombatantMath.ActionPace(actionMode: true, config.ActionEnemyAttackSpeed);

    protected static float FlatDistance(Vector3 a, Vector3 b) => CombatantMath.FlatDistance(a, b);
}
