using System.Numerics;
using System.Reflection;
using System.Text;
using CombatSimulator.Battle;
using CombatSimulator.Battle.Rules;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

// The engine side of a battle, offline (refactor stage B6): how hits are settled (HitSettlement, the
// production code, over a recording fake of the engine), the result banner's words and the killer's
// possession over every combination of the war switches, and the corpse seizure carried through to its end.
// Recorded before the two settlement paths were merged and the rule hooks were wired (README.md); since
// stage B6 the banner, the killer and the war tick are asked of the production rule parts.

/// <summary>The war switches, changed between events.</summary>
sealed class MutableRules : IBattleRuleSettings
{
    public WarFallenRule FallenRule { get; set; }
    public bool Hostages { get; set; }
    public bool Retreat { get; set; }
    public uint CaptiveEmoteId => 19;
    public bool ShowResultBanner => true;
    public bool EnableRagdoll => true;
    public override string ToString() => $"{FallenRule} hostages {Hostages} retreat {Retreat}";
}

/// <summary>
/// The engine as the settlement sees it (IHitSettlementHost): every call written down in order with what it
/// was given. The engine's own state changes are made as CombatEngine makes them (HP on landing, the captive
/// state, a companion's captive flag on death); positions are fixed per fighter; a fighter wielding a bow
/// (the ranged ones) shoots with a delay as CombatEngine.RangedContactDelay computes it; deferred hits land
/// as CombatEngine.TickDeferredHits lands them; the targets in an action's area as
/// CombatEngine.ResolveActionTargets finds them.
/// </summary>
sealed class RecordingHost : IHitSettlementHost
{
    private readonly StringBuilder o;
    private readonly Func<uint, string> name;
    public readonly Dictionary<uint, Vector3> Positions = new();
    public readonly HashSet<uint> Bowmen = new();
    public readonly Dictionary<uint, ActionData> Actions = new();
    public readonly List<(float Timer, Action Resolve)> Deferred = new();

    public RecordingHost(StringBuilder o, Func<uint, string> name, IBattleRuleSettings settings, IReadOnlyList<CombatCompanion> allies)
    {
        this.o = o;
        this.name = name;
        RuleParts = Outcomes.RuleSetFor(settings, State, allies, seizure: null);
    }

    private void L(string text) => o.Append("h ").Append(text).Append('\n');
    private string N(SimulatedEntityState? s) => s == null ? "null" : name(s.EntityId);
    private static string D(DamageResult d) => $"{d.Damage}{(d.IsCritical ? "c" : "")}{(d.IsDirectHit ? "d" : "")}/{d.DamageType}";
    private string Hits(IReadOnlyList<AppliedActionDamage> hits) => string.Join(",", hits.Select(h => $"{N(h.Target)}:{D(h.DamageResult)}"));
    private static string A(ActionData a)
        => $"[{a.ActionId} {a.Name} p{a.Potency} {a.Shape} r{Fmt.H(a.Radius)} rg{Fmt.H(a.Range)} {a.DamageType}]";

    public SimulationState State { get; } = new();
    public BattleRuleSet RuleParts { get; }
    public bool Crit = true, DirectHit = true;
    public float Multiplier = 1f;

    public ActionData? GetActionData(uint actionId)
    {
        var data = Actions.GetValueOrDefault(actionId);
        L($"data {actionId} {(data == null ? "none" : "found")}");
        return data;
    }

    // Crit, DirectHit and Multiplier are still drawn and written on each ally event (so the event stream and its
    // lines stay as recorded), but since Refactoring II stage R2-P2 the settlement no longer reads them: an ally's
    // attack without a potency goes by the action's bare potency, as an enemy's does (one set of numbers).

    // CombatEngine.GetHostileTargets.
    public List<SimulatedEntityState> GetHostileTargets(SimulatedEntityState source)
    {
        L($"hostiles of {N(source)}");
        var result = new List<SimulatedEntityState>();
        if (source.IsPlayer || source.IsCompanion)
        {
            foreach (var entity in State.Entities.Values)
                if (!entity.IsPlayer && !entity.IsCompanion && entity.IsAlive)
                    result.Add(entity);
            return result;
        }
        if (State.PlayerState.IsAlive)
            result.Add(State.PlayerState);
        foreach (var entity in State.Entities.Values)
            if (entity.IsCompanion && entity.IsAlive)
                result.Add(entity);
        return result;
    }

    public Vector3 GetEntityPosition(SimulatedEntityState entity) => Positions.GetValueOrDefault(entity.EntityId);

    // CombatEngine.ResolveActionTargets and IsInsideActionArea.
    public List<SimulatedEntityState> ResolveActionTargets(SimulatedEntityState source, SimulatedEntityState primaryTarget, ActionData actionData, bool primaryAlreadyHit)
    {
        L($"area of {A(actionData)} from {N(source)} on {N(primaryTarget)} hit {primaryAlreadyHit}");
        var targets = new List<SimulatedEntityState>();
        static bool Friendly(SimulatedEntityState e) => e.IsPlayer || e.IsCompanion;
        if ((!primaryAlreadyHit && !primaryTarget.IsAlive) || Friendly(source) == Friendly(primaryTarget))
            return targets;
        targets.Add(primaryTarget);
        if (actionData.Shape == AoeShape.Single || actionData.Radius <= 0)
            return targets;
        foreach (var candidate in GetHostileTargets(source))
        {
            if (targets.Count >= 32)
                break;
            if (candidate.EntityId == primaryTarget.EntityId || !candidate.IsAlive)
                continue;
            var sourcePos = GetEntityPosition(source);
            var primaryPos = GetEntityPosition(primaryTarget);
            var candidatePos = GetEntityPosition(candidate);
            var radius = MathF.Max(0f, actionData.Radius);
            var inside = actionData.Shape switch
            {
                AoeShape.Circle or AoeShape.GroundCircle => CombatGeometry.IsInsideCircle(primaryPos, candidatePos, radius),
                AoeShape.CircleSelf => CombatGeometry.IsInsideCircle(sourcePos, candidatePos, radius),
                AoeShape.Donut => CombatGeometry.IsInsideDonut(sourcePos, candidatePos, actionData.InnerRadius, radius),
                AoeShape.Cone => CombatGeometry.IsInsideConeAngle(sourcePos, primaryPos, candidatePos, radius, actionData.ConeAngleDeg),
                AoeShape.Line => CombatGeometry.IsInsideLine(sourcePos, primaryPos, candidatePos, radius, actionData.Width),
                _ => false,
            };
            if (inside)
                targets.Add(candidate);
        }
        return targets;
    }

    // CombatEngine.RangedContactDelay with the bowmen as the ranged wielders (all bows).
    public float RangedContactDelay(SimulatedEntityState source, SimulatedEntityState target)
    {
        var delay = Bowmen.Contains(source.EntityId)
            ? 0.6f + Vector3.Distance(GetEntityPosition(source), GetEntityPosition(target)) / 45f
            : -1f;
        L($"contact delay {N(source)} -> {N(target)} = {Fmt.H(delay)}");
        return delay;
    }

    public void DeferHit(float delay, Action resolve)
    {
        L($"defer {Fmt.H(delay)}");
        Deferred.Add((MathF.Max(0f, delay), resolve));
    }

    // CombatEngine.TickDeferredHits.
    public void TickDeferred(float deltaTime)
    {
        for (var i = Deferred.Count - 1; i >= 0; i--)
        {
            var (timer, resolve) = Deferred[i];
            timer -= deltaTime;
            if (timer > 0f)
            {
                Deferred[i] = (timer, resolve);
                continue;
            }
            Deferred.RemoveAt(i);
            L("deferred lands");
            resolve();
        }
    }

    // CombatEngine.LandDamage.
    public void LandDamage(SimulatedEntityState target, DamageResult damage, SimulatedEntityState? source)
    {
        target.CurrentHp = Math.Max(0, target.CurrentHp - damage.Damage);
        L($"land {N(target)} {D(damage)} by {N(source)} hp {target.CurrentHp}");
        if (damage.Damage > 0)
            RaiseDamageLanded(target, damage, source);
    }

    public void RaiseHit(SimulatedEntityState source, SimulatedEntityState target, int damage) => L($"on hit {N(source)} -> {N(target)} {damage}");
    public void RaiseDamageLanded(SimulatedEntityState target, DamageResult damage, SimulatedEntityState? source) => L($"damage landed {N(target)} {D(damage)} by {N(source)}");
    public void RaisePlayerDamagedByNpc() => L("player damaged");
    public void RaisePlayerHitByNpc(uint npcId) => L($"player hit by {name(npcId)}");

    // CombatEngine.TakeCaptive (the state; no emote or movement block).
    public void TakeCaptive(SimulatedEntityState friend)
    {
        L($"take captive {N(friend)}");
        if (friend.IsCaptive || !friend.IsAlive)
            return;
        friend.IsCaptive = true;
        friend.CurrentHp = Math.Min(friend.CurrentHp, 1);
        friend.IsCasting = false;
        friend.CastTimeElapsed = 0;
        friend.CastActionId = 0;
    }

    public void AddLogEntry(string message, CombatLogType type) => L($"log {type} {message}");

    // CombatEngine.OnEntityDeath's state changes (the death queue and the victory checks are not run).
    public void OnEntityDeath(SimulatedEntityState entity)
    {
        L($"death {N(entity)} executed {entity.Executed}");
        if (entity.IsCompanion || (entity.IsPlayer && entity.Executed))
            entity.IsCaptive = false;
    }

    public void RecordPlayerKiller(Combatant npc) => L($"player killer {npc.Name} {npc.Address:X} {name(npc.SimulatedEntityId)}");
    public void EngageIdleTarget(uint entityId) => L($"engage {name(entityId)}");

    public void TriggerActionEffect(SimulatedEntityState source, ActionData actionData, IReadOnlyList<AppliedActionDamage> hits,
        bool suppressCastVfx, float contactDelay, bool deferHitReaction)
        => L($"effect {N(source)} {A(actionData)} [{Hits(hits)}] cast-vfx-off {suppressCastVfx} contact {Fmt.H(contactDelay)} defer-react {deferHitReaction}");

    public void TriggerManualNpcHitFeedback(IReadOnlyList<AppliedActionDamage> hits) => L($"manual feedback [{Hits(hits)}]");

    public void PlayActionVfx(SimulatedEntityState source, ActionData actionData, IReadOnlyList<AppliedActionDamage> hits,
        bool suppressCastVfx, bool modelTimelineOwnsVfx)
        => L($"vfx {N(source)} {A(actionData)} [{Hits(hits)}] cast-vfx-off {suppressCastVfx} timeline-owns {modelTimelineOwnsVfx}");

    public void PlayHitReactionOnTarget(SimulatedEntityState target, bool isDamage, bool includePlayer)
        => L($"flinch {N(target)} {isDamage} player {includePlayer}");
}

/// <summary>
/// Random sequences of enemy and ally attacks through the production HitSettlement: every kind of action
/// (auto-attacks, skills with and without data and potency, areas, splash, snapshotted areas, the windup's
/// VFX-only strike, arrows in flight), captives and executions, hostages switched on and off, fighters
/// falling and getting up again. One block per seed.
/// </summary>
static unsafe class SettlementReplay
{
    private const int Steps = 700;

    public static string Run()
    {
        var o = new StringBuilder();
        o.Append("# settlement: random enemy and ally attacks through HitSettlement over a recording engine; seeds 1-3\n");
        for (var seed = 1; seed <= 3; seed++)
            RunSeed(o, seed);
        return o.ToString();
    }

    private static void RunSeed(StringBuilder o, int seed)
    {
        o.Append($"## seed {seed}\n");
        var rng = new Random(seed * 7919);
        var names = new Dictionary<uint, string>();
        string Name(uint id) => names.GetValueOrDefault(id, $"#{id:X}");
        var settings = new MutableRules { FallenRule = WarFallenRule.WarringStates, Hostages = true };
        var allies = new List<CombatCompanion>();
        var host = new RecordingHost(o, Name, settings, allies);
        var damage = new DamageCalculator(new Random(seed)) { IsActionMode = () => true, PlayerMaxHp = () => 60000f };
        var settlement = new HitSettlement(host, damage);
        host.State.IsActive = true;

        // The action data the engine finds (none for auto-attacks and for 200).
        host.Actions[100] = new ActionData { ActionId = 100, Name = "Bolt", Potency = 220, DamageType = SimDamageType.Magical, Range = 25f };
        host.Actions[101] = new ActionData { ActionId = 101, Name = "Quake", Potency = 0, Shape = AoeShape.Circle, Radius = 6f, Range = 20f };
        host.Actions[102] = new ActionData { ActionId = 102, Name = "Whirl", Potency = 160, Shape = AoeShape.CircleSelf, Radius = 5f, DamageType = SimDamageType.Magical };
        host.Actions[103] = new ActionData { ActionId = 103, Name = "Fan", Potency = 300, Shape = AoeShape.Cone, Radius = 8f, ConeAngleDeg = 90f, Range = 8f };
        host.Actions[104] = new ActionData { ActionId = 104, Name = "Lance", Potency = 140, Shape = AoeShape.Line, Radius = 12f, Width = 3f, Range = 12f };
        host.Actions[105] = new ActionData { ActionId = 105, Name = "Ring", Potency = 180, Shape = AoeShape.Donut, Radius = 10f, InnerRadius = 3f, DamageType = SimDamageType.Magical };
        host.Actions[300] = new ActionData { ActionId = 300, Name = "Spark", Potency = 0, Range = 3f };
        uint[] actionIds = { 7, 8, 100, 101, 102, 103, 104, 105, 200 };

        var player = new SimulatedEntityState { EntityId = 0x10000001, Name = "P", IsPlayer = true, MaxHp = 30000, CurrentHp = 30000, MaxMp = 10000, CurrentMp = 10000 };
        host.State.PlayerState = player;
        names[player.EntityId] = "P";
        host.Positions[player.EntityId] = new Vector3(0f, 0f, 0f);

        for (var i = 0; i < 4; i++)
        {
            var id = 0x20000001u + (uint)i;
            var ally = new CombatCompanion
            {
                SimulatedEntityId = id, Name = $"A{i + 1}", BattleChara = (BattleChara*)(0x300000 + i * 0x1000), IsSpawned = true,
                Behavior = i == 3 ? Kits.Ranged() : Kits.Melee(),
                State = new SimulatedEntityState { EntityId = id, Name = $"A{i + 1}", IsCompanion = true, MaxHp = 20000, CurrentHp = 20000, LevelFactor = 0.5f },
            };
            allies.Add(ally);
            host.State.Entities[id] = ally.State;
            names[id] = ally.Name;
            host.Positions[id] = new Vector3((i - 1.5f) * 2.5f, 0f, 1.5f + (i % 2));
            if (i == 3) host.Bowmen.Add(id);
        }
        var enemies = new List<SimulatedNpc>();
        for (var i = 0; i < 5; i++)
        {
            var id = 0xF0000001u + (uint)i;
            var enemy = new SimulatedNpc
            {
                SimulatedEntityId = id, Name = $"E{i + 1}", BattleChara = (BattleChara*)(0x200000 + i * 0x1000), IsSpawned = true,
                Behavior = i == 4 ? Kits.Ranged() : i == 3 ? Kits.Magic() : Kits.Melee(),
                State = new SimulatedEntityState { EntityId = id, Name = $"E{i + 1}", MaxHp = 40000, CurrentHp = 40000, LevelFactor = 1.2f },
            };
            enemies.Add(enemy);
            host.State.Entities[id] = enemy.State;
            names[id] = enemy.Name;
            host.Positions[id] = new Vector3((i - 2f) * 3f, 0f, 7f + (i % 3));
            if (i == 4) host.Bowmen.Add(id);
        }
        var everyone = new List<SimulatedEntityState> { player };
        everyone.AddRange(allies.Select(a => a.State));
        everyone.AddRange(enemies.Select(e => e.State));

        for (var step = 0; step < Steps; step++)
        {
            var kind = rng.Next(100);
            if (kind < 38)
            {
                var npc = enemies[rng.Next(enemies.Count)];
                var pick = rng.Next(12);
                var targetId = pick < 4 ? player.EntityId : pick < 10 ? allies[pick % allies.Count].SimulatedEntityId
                    : pick == 10 ? enemies[0].SimulatedEntityId : 0x12345678u;
                // Often the same enemy keeps at a captive (a wind-up, then the execution).
                var captives = everyone.Where(e => e.IsCaptive && e.IsAlive).ToList();
                if (captives.Count > 0 && rng.Next(2) == 0)
                {
                    targetId = captives[rng.Next(captives.Count)].EntityId;
                    npc = enemies[(int)(targetId % 2)];
                }
                var actionId = actionIds[rng.Next(actionIds.Length)];
                var potency = rng.Next(3) == 0 ? 180 : 0;
                var style = (NpcAttackStyle)rng.Next(4);
                var radius = rng.Next(4) == 0 ? 5f : 0f;
                var suppressCaster = rng.Next(8) == 0;
                var suppressCastVfx = rng.Next(5) == 0;
                var modelVfx = rng.Next(4) switch { 0 => 300u, 1 => 100u, 2 => 999u, _ => 0u };
                var owns = rng.Next(2) == 0;
                TelegraphShape? area = null;
                ulong exclude = 0;
                if (rng.Next(7) == 0)
                {
                    var at = host.Positions.GetValueOrDefault((uint)targetId);
                    area = new TelegraphShape(TelegraphKind.Circle, at + new Vector3(rng.Next(3) - 1, 0f, rng.Next(3) - 1), 0f, 3f + rng.Next(4), 0f, 0f, 0f) { IsArea = true };
                    if (rng.Next(3) == 0) exclude = player.EntityId;
                }
                o.Append($"e {step} enemy {npc.Name} -> {Name((uint)targetId)} action {actionId} p{potency} {style} r{radius} suppress {suppressCaster}/{suppressCastVfx} vfx {modelVfx} owns {owns} area {(area is { } a ? $"{Fmt.H(a.Origin)} {Fmt.H(a.Radius)}" : "-")} exclude {Name((uint)exclude)}\n");
                var r = settlement.ProcessNpcAction(npc, actionId, targetId, potency, style, radius, suppressCaster, suppressCastVfx, modelVfx, owns, area, exclude);
                Result(o, r, Name);
            }
            else if (kind < 70)
            {
                var ally = allies[rng.Next(allies.Count)];
                var target = enemies[rng.Next(enemies.Count)];
                var actionId = actionIds[rng.Next(actionIds.Length)];
                var potency = rng.Next(3) == 0 ? 150 : 0;
                var style = (NpcAttackStyle)rng.Next(4);
                var radius = rng.Next(4) == 0 ? 6f : 0f;
                host.Crit = rng.Next(4) != 0;
                host.DirectHit = rng.Next(4) != 0;
                host.Multiplier = rng.Next(3) == 0 ? 1.5f : 1f;
                o.Append($"e {step} ally {ally.Name} -> {target.Name} action {actionId} p{potency} {style} r{radius} crit {host.Crit} dh {host.DirectHit} x{Fmt.H(host.Multiplier)}\n");
                var r = settlement.ProcessCompanionAction(ally, target, actionId, potency, style, radius);
                Result(o, r, Name);
            }
            else if (kind < 80)
            {
                var dt = 0.1f + rng.Next(20) * 0.1f;
                host.State.SimulationTime += dt;
                o.Append($"e {step} time {Fmt.H(host.State.SimulationTime)}\n");
                host.TickDeferred(dt);
            }
            else if (kind < 84)
            {
                settings.FallenRule = (WarFallenRule)rng.Next(3);
                settings.Hostages = rng.Next(3) != 0;
                settings.Retreat = rng.Next(2) == 0;
                o.Append($"e {step} rules {settings}\n");
            }
            else if (kind < 98)
            {
                var dead = everyone.Where(e => !e.IsAlive || e.IsCaptive).ToList();
                var who = dead.Count > 0 && rng.Next(4) != 0 ? dead[rng.Next(dead.Count)] : everyone[rng.Next(everyone.Count)];
                who.CurrentHp = 2000 + rng.Next(8) * 2500;
                who.IsCaptive = false;
                who.Executed = false;
                o.Append($"e {step} revive {Name(who.EntityId)} hp {who.CurrentHp}\n");
            }
            else
            {
                var npc = enemies[rng.Next(enemies.Count)];
                npc.IsSpawned = !npc.IsSpawned;
                o.Append($"e {step} {npc.Name} spawned {npc.IsSpawned}\n");
            }
            o.Append("s ").Append(string.Join(" ", everyone.Select(e => $"{Name(e.EntityId)}:{e.CurrentHp}{(e.IsCaptive ? "c" : "")}{(e.Executed ? "x" : "")}")))
             .Append($" taken {host.State.TotalDamageTaken} dealt {host.State.TotalDamageDealt}\n");
            if (step % 50 == 49)
                foreach (var won in new[] { false, true })
                    o.Append($"O {step} {(won ? "won" : "lost")} {Outcomes.Describe(won, host.RuleParts)}\n");
        }
    }

    private static void Result(StringBuilder o, SimulatedActionResult r, Func<uint, string> name)
        => o.Append($"r {(r.Success ? "ok" : "fail")} {r.FailReason ?? "-"} dmg {r.Damage} crit {r.IsCritical} dh {r.IsDirectHit} killed {r.TargetKilled} action {r.ActionId} src {name(r.SourceId)} tgt {name((uint)r.TargetId)}\n");
}

/// <summary>The result banner's words for every combination (whether the killer is possessed was here until 2026-11-15).</summary>
static class Outcomes
{
    /// <summary>
    /// The battle's rule parts (the production set, Battle/Rules) over a stub of the engine (its state) and of
    /// the allies (who they are), with the corpse seizure if there is one.
    /// </summary>
    public static BattleRuleSet RuleSetFor(IBattleRuleSettings settings, SimulationState state, IReadOnlyList<CombatCompanion> allies,
        CorpseSeizure<SimulatedNpc>? seizure)
    {
        var rules = new BattleRules(settings, () => state.IsActive, () => state.PlayerState.IsAlive);
        var engine = Stub.Of<ICombatLoopEngine>(("get_State", _ => state));
        var ruleAllies = Stub.Of<IRuleAllies>(("get_All", _ => allies));
        var set = new BattleRuleSet(new BattleContext(rules, engine, null!, ruleAllies), BattleRuleFactory.CreateAll());
        set.Get<CorpseSeizureRule>().Seizure = seizure;
        return set;
    }

    /// <summary>
    /// The banner's title and reason, the battle rules' (BattleRuleSet.DescribeOutcome). Recorded from
    /// Plugin.War.cs DescribeOutcome as it was before stage B6.
    /// </summary>
    public static string Describe(bool playerWon, BattleRuleSet rules)
    {
        var (title, reason) = rules.DescribeOutcome(playerWon);
        return $"{title} | {reason}";
    }

    /// <summary>A corpse seizure that has carried the body off (or not).</summary>
    private static CorpseSeizure<SimulatedNpc> Seizure(BattleRules rules, bool seized)
    {
        var seizure = new CorpseSeizure<SimulatedNpc>(rules, null!, null!, CombatCompanionManager.LordContestRadius);
        typeof(CorpseSeizure<SimulatedNpc>).GetField("bodySeized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(seizure, seized);
        return seizure;
    }

    public static string Table()
    {
        var o = new StringBuilder();
        o.Append("# outcomes: the result banner's words for every combination\n");
        foreach (var fallen in new[] { WarFallenRule.StayDown, WarFallenRule.SpringAndAutumn, WarFallenRule.WarringStates })
        foreach (var hostages in new[] { false, true })
        foreach (var retreat in new[] { false, true })
        foreach (var active in new[] { false, true })
        foreach (var lordAlive in new[] { false, true })
        foreach (var lordExecuted in new[] { false, true })
        foreach (var allyExecuted in new[] { -1, 0, 1 })
        foreach (var seized in new[] { false, true })
        foreach (var won in new[] { false, true })
        {
            var settings = new MutableRules { FallenRule = fallen, Hostages = hostages, Retreat = retreat };
            var state = new SimulationState { IsActive = active };
            state.PlayerState = new SimulatedEntityState { EntityId = 1, IsPlayer = true, MaxHp = 100, CurrentHp = lordAlive ? 100 : 0, Executed = lordExecuted };
            var rules = new BattleRules(settings, () => state.IsActive, () => state.PlayerState.IsAlive);
            var companions = new List<CombatCompanion>
            {
                new() { State = new() { EntityId = 2, IsCompanion = true, MaxHp = 100, CurrentHp = allyExecuted == 0 ? 0 : 50, Executed = allyExecuted == 0 } },
                new() { State = new() { EntityId = 3, IsCompanion = true, MaxHp = 100, CurrentHp = allyExecuted == 1 ? 0 : 50, Executed = allyExecuted == 1 } },
            };
            var set = RuleSetFor(settings, state, companions, Seizure(rules, seized));
            o.Append($"{settings} active {active} lord {(lordAlive ? "alive" : "dead")} executed {lordExecuted}/{allyExecuted} seized {seized} {(won ? "won" : "lost")}: {Describe(won, set)}\n");
        }
        // (Whether the killer is taken over is no longer the rules' to say since 2026-11-15: Enemy Control takes over
        // an enemy only once the battle is lost, under every rule, Battle/KillerPossession.cs, Tests/CorpseSeizure.)
        return o.ToString();
    }
}

/// <summary>An interface answered by the given handlers (by method name, a property's getter as get_Name); anything else throws.</summary>
public class Stub : DispatchProxy
{
    private readonly Dictionary<string, Func<object?[]?, object?>> handlers = new();

    public static T Of<T>(params (string Method, Func<object?[]?, object?> Answer)[] answers) where T : class
    {
        var proxy = Create<T, Stub>();
        foreach (var (method, answer) in answers)
            ((Stub)(object)proxy).handlers[method] = answer;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod != null && handlers.TryGetValue(targetMethod.Name, out var answer)
            ? answer(args)
            : throw new InvalidOperationException($"stub: {targetMethod?.Name} was not expected");
}

/// <summary>
/// The corpse seizure (Battle/CorpseSeizure, production) carried through: the lord dead under Warring States
/// with Retreat, the retainers at the body thinning out, the seizers walking in, the body taken, the grab
/// retried until it holds, the banner's words. A flat fake world logs every call; variants switch Retreat off
/// and on midway, and keep the ragdoll from ever being ready while the outcome is decided elsewhere.
/// </summary>
static unsafe class SeizureReplay
{
    private sealed class World : ICorpseSeizureWorld<SimulatedNpc>, ICorpseGrab<SimulatedNpc>
    {
        private readonly StringBuilder o;
        public readonly Dictionary<SimulatedNpc, Vector3> At = new();
        public readonly Dictionary<SimulatedNpc, Vector3?> Goal = new();
        public List<SimulatedNpc> All = new();
        public float Time;
        public bool Decided;
        public bool NeverReady;
        public int Retainers => Time < 3f ? 2 : Time < 5f ? 1 : 0;
        public bool Controlling;
        private int grabs;
        public bool Holding;

        public World(StringBuilder o) => this.o = o;
        private void L(string text) => o.Append("w ").Append(text).Append('\n');

        public bool OutcomeDecided => Decided;
        public Vector3? LordBodyLocation() => new Vector3(1f, 0f, 2f);
        public IReadOnlyList<SimulatedNpc> Enemies => All;
        public bool IsPresent(SimulatedNpc npc) => npc.IsSpawned && npc.IsAlive && npc.BattleChara != null;
        public bool IsCaptive(SimulatedNpc npc) => npc.State.IsCaptive;
        public bool IsIdleSentry(SimulatedNpc npc) => npc.IsSentry && npc.AiState == CombatantAiState.Idle;
        public Vector3 PositionOf(SimulatedNpc npc) => At[npc];
        public float HitboxRadiusOf(SimulatedNpc npc) => 0.5f;
        public string NameOf(SimulatedNpc npc) => npc.Name;
        public uint IdOf(SimulatedNpc npc) => npc.SimulatedEntityId;
        public void SetSeizeGoal(SimulatedNpc npc, Vector3? goal) { Goal[npc] = goal; L($"goal {npc.Name} {(goal is { } g ? Fmt.H(g) : "-")}"); }
        public void SetSeizeHeld(SimulatedNpc npc, bool held) => L($"held {npc.Name} {held}");
        public void Engage(SimulatedNpc npc) => L($"engage {npc.Name}");
        public void ClearSeizerIds() => L("seizers cleared");
        public void AddSeizerId(uint id) => L($"seizer {id:X}");
        public int RetainersAtBody(Vector3 body, float radius, out string? nearestGuard, out float nearestDistance)
        {
            nearestGuard = Retainers > 0 ? "A1" : Time < 6f ? "A2" : null;
            nearestDistance = Retainers > 0 ? 1.2f : 6.5f;
            return Retainers;
        }
        public bool BodyReady => !NeverReady && Time >= 1.5f;
        public void SetCorpseCaptured(bool captured) => L($"corpse captured {captured}");
        public void SurrenderRetainers() => L("surrender");
        public void ForceEnemyVictory() { L("enemy victory"); Decided = true; }
        public void BattleLog(string text) => L($"log {text}");
        public void Info(string text) => L($"info {text}");
        public void Warning(string text) => L($"warning {text}");

        public bool TakesOverSeizer => true;
        public bool BodyCanBeHeld => BodyReady;
        public bool IsControlling => Controlling;
        public void TakeControl(SimulatedNpc npc) { L($"control {npc.Name}"); Controlling = true; }
        public void Grab() { grabs++; Holding = grabs >= 3; L($"grab {grabs} holding {Holding}"); }
        public bool IsHolding => Holding;
    }

    public static string Run()
    {
        var o = new StringBuilder();
        o.Append("# seizure: the lord's body under Warring States with Retreat, carried through by Battle/CorpseSeizure\n");
        Variant(o, "taken", null, neverReady: false, decideAt: -1f);
        Variant(o, "retreat off at 2 s, on at 3 s", (2f, 3f), neverReady: false, decideAt: -1f);
        Variant(o, "ragdoll never ready, decided at 6 s", null, neverReady: true, decideAt: 6f);
        return o.ToString();
    }

    private static void Variant(StringBuilder o, string label, (float Off, float On)? retreatGap, bool neverReady, float decideAt)
    {
        o.Append($"## {label}\n");
        var settings = new MutableRules { FallenRule = WarFallenRule.WarringStates, Retreat = true };
        var state = new SimulationState { IsActive = true };
        state.PlayerState = new SimulatedEntityState { EntityId = 1, Name = "P", IsPlayer = true, MaxHp = 100, CurrentHp = 0 };
        var rules = new BattleRules(settings, () => state.IsActive, () => state.PlayerState.IsAlive);
        var world = new World(o) { NeverReady = neverReady };
        for (var i = 0; i < 6; i++)
        {
            var npc = new SimulatedNpc
            {
                SimulatedEntityId = 0xF0000001u + (uint)i, Name = $"E{i + 1}", BattleChara = (BattleChara*)(0x200000 + i * 0x1000), IsSpawned = true,
                IsSentry = i == 5, State = new SimulatedEntityState { EntityId = 0xF0000001u + (uint)i, MaxHp = 100, CurrentHp = 100 },
            };
            world.All.Add(npc);
            world.At[npc] = new Vector3(-8f + i * 3.5f, 0f, 9f + (i % 2) * 4f + (i == 5 ? 40f : 0f));
        }
        var seizure = new CorpseSeizure<SimulatedNpc>(rules, world, world, CombatCompanionManager.LordContestRadius);
        var ruleSet = Outcomes.RuleSetFor(settings, state, new List<CombatCompanion>(), seizure);

        const float dt = 1f / 30f;
        for (var frame = 0; frame < 360; frame++)
        {
            world.Time = frame * dt;
            if (retreatGap is { } gap)
            {
                var retreat = world.Time < gap.Off || world.Time >= gap.On;
                if (retreat != settings.Retreat)
                {
                    settings.Retreat = retreat;
                    o.Append($"f {frame} rules {settings}\n");
                }
            }
            if (decideAt >= 0f && !world.Decided && world.Time >= decideAt)
            {
                world.Decided = true;
                o.Append($"f {frame} decided elsewhere\n");
            }
            // The plugin's war tick, after the engine and before the enemy AI.
            ruleSet.TickAfterEngine(dt);
            o.Append($"f {frame} progress {Fmt.H(seizure.Progress)} seized {seizure.BodySeized}\n");
            // The seizers walk at 4 m/s to their goal (to 0.8 m of it), the rest stand.
            foreach (var npc in world.All)
                if (world.Goal.GetValueOrDefault(npc) is { } goal)
                {
                    var to = new Vector3(goal.X - world.At[npc].X, 0f, goal.Z - world.At[npc].Z);
                    var d = to.Length();
                    if (d > 0.8f)
                        world.At[npc] += to * (MathF.Min(4f * dt, d - 0.8f) / d);
                }
        }
        o.Append($"O {Outcomes.Describe(false, ruleSet)}\n");
    }
}
