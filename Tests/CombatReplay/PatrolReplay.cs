using System.Numerics;
using System.Reflection;
using System.Text;
using CombatSimulator.Ai;
using CombatSimulator.Combat;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

/// <summary>
/// Patrol and Enemy Control after a lost fight (2026-11-15, the user's report: "with Patrol on nobody patrols,
/// all stand still"; "Enemy Control and Patrol conflict"). The real <see cref="NpcAiController"/> in the flat
/// world (LoopReplay.cs): the player dead, no allies, a squad of patrolling sentries close together. One case
/// per thing that used to stop everyone's patrol:
/// <list type="bullet">
/// <item><c>controlled-killer</c>: the killer, taken over by Enemy Control mid-fight, is left in Combat (its AI
/// skips it). It used to count as "a neighbour fighting" for every sentry within 10 m, every frame, so none of
/// them ever went back to patrolling.</item>
/// <item><c>forced-engage</c>: party mode without "Idle until engaged" engages every enemy that is not a sentry
/// every frame; with nobody alive to fight they flicker between engaged and idle, and woke the sentries
/// around them the same way.</item>
/// <item><c>plain</c>: neither (the reference: every sentry patrols).</item>
/// </list>
/// Measured per sentry over 20 s: the share of frames it was an unaware sentry (patrolling), how far it walked,
/// how far from its post it got; for the controlled enemy: whether the AI drove it or changed its state.
/// `-- --patrol-metrics` prints the numbers only (they run on the code before the change too); the default run
/// also checks them (<see cref="Check"/>).
/// </summary>
static unsafe class PatrolReplay
{
    private const float Duration = 20f;
    private const float Dt = 1f / 60f;

    public sealed record CaseResult(string Name, string Text, List<string> Failures);

    public static IEnumerable<CaseResult> RunAll()
    {
        yield return Run("plain", controlledKiller: false, forcedEngage: false);
        yield return Run("controlled-killer", controlledKiller: true, forcedEngage: false);
        yield return Run("forced-engage", controlledKiller: false, forcedEngage: true);
        yield return Run("controlled-killer+forced-engage", controlledKiller: true, forcedEngage: true);
        // Review 2026-11-15: the player was off the mesh when it was built (the crowd cannot tell what is reachable):
        // the sentries still patrol (any walkable floor about the post).
        yield return Run("no-player-piece", controlledKiller: true, forcedEngage: false, canJudgeReach: false);
        yield return RunSharedPost();
        yield return RunMissedAutoEngage();
    }

    /// <summary>The checks of the default run; returns false (and prints why) on the first case that fails.</summary>
    public static bool Check()
    {
        var ok = true;
        foreach (var result in RunAll())
        {
            if (result.Failures.Count == 0)
            {
                Console.WriteLine($"patrol {result.Name}: PASS");
                continue;
            }
            ok = false;
            Console.WriteLine($"patrol {result.Name}: FAIL — {string.Join("; ", result.Failures)}");
            Console.Write(result.Text);
        }
        return ok;
    }

    private static CaseResult Run(string name, bool controlledKiller, bool forcedEngage, bool canJudgeReach = true)
    {
        var log = new LoopLog();
        var world = new FlatWorld(log) { CanJudgeReach = canJudgeReach };
        // Party mode on (the user's setting); "Idle until engaged" on unless the case forces engaging.
        var settings = new Settings { SensePartyMembers = false, EnableCombatCompanions = true, TacticsRespectEngage = !forcedEngage };
        var engine = new FakeEngine(world, log, new RuleSettings(WarFallenRule.StayDown, false));
        var attacks = new FakeAttacks(engine, new DamageCalculator(new Random(4)) { PlayerMaxHp = () => 60000f }, log);
        var pluginLog = DispatchProxy.Create<IPluginLog, NullLogProxy>();

        const uint playerId = 0x10000001;
        world.Player = world.Add(new Body { Name = "P", Address = 0x100000, EntityId = playerId, Position = new Vector3(0f, 0f, 14f) });
        engine.State.PlayerState = new SimulatedEntityState { EntityId = playerId, Name = "P", IsPlayer = true, CurrentHp = 0, MaxHp = 60000 };

        var controlled = new HashSet<nint>();
        var ai = new NpcAiController(engine, world, attacks, settings, pluginLog, collectPartyPositions: null,
            isExternallyControlled: controlled.Contains, isTelegraphBusy: attacks.IsBusy)
        {
            Crowd = world,
            RandomSource = new Random(7),
        };

        // A squad of six patrolling sentries posted 2.5 m apart (the war generator's map squad), and with the
        // forced-engage case two plain enemies beside them (not sentries: placed around the player).
        var enemies = new List<SimulatedNpc>();
        var posts = new List<Vector3>();
        SimulatedNpc Add(string n, Vector3 at, bool sentry)
        {
            var i = enemies.Count;
            var id = 0xF0000001u + (uint)i;
            var address = (nint)(0x200000 + i * 0x1000);
            world.Add(new Body { Name = n, Address = address, EntityId = id, Position = at, Rotation = MathF.PI });
            var npc = new SimulatedNpc
            {
                SimulatedEntityId = id, Name = n, BattleChara = (BattleChara*)address, Behavior = Kits.Melee(), SpawnPosition = at,
                IsSpawned = true, IsClientControlled = true, IsSentry = sentry, Patrols = sentry,
                State = new SimulatedEntityState { EntityId = id, Name = n, CurrentHp = 20000, MaxHp = 20000, MaxMp = 5000, CurrentMp = 5000 },
            };
            engine.State.Entities[id] = npc.State;
            enemies.Add(npc);
            posts.Add(at);
            return npc;
        }
        for (var i = 0; i < 6; i++)
            Add($"S{i + 1}", new Vector3((i % 3 - 1) * 2.5f, 0f, 20f + (i / 3) * 2.5f), sentry: true);
        if (forcedEngage)
        {
            Add("N1", new Vector3(-4f, 0f, 17f), sentry: false);
            Add("N2", new Vector3(4f, 0f, 17f), sentry: false);
        }
        engine.Enemies = enemies;

        // The killer: S1, in the middle of the fight that has just ended (Combat), taken over by Enemy Control.
        var killer = enemies[0];
        killer.AiState = CombatantAiState.Combat;
        if (controlledKiller)
            controlled.Add(killer.Address);
        var killerStart = world.At(killer.Address).Position;
        var killerState = killer.AiState;

        engine.State.IsActive = true;
        engine.OnSimulationStarted?.Invoke();

        var frames = 0;
        var unaware = new int[enemies.Count];
        var walked = new float[enemies.Count];
        var farthest = new float[enemies.Count];
        var last = enemies.Select(e => world.At(e.Address).Position).ToArray();
        var killerDriven = 0;
        var killerStateChanged = false;
        while (engine.State.SimulationTime < Duration)
        {
            log.Frame = frames;
            world.Update(Dt);
            engine.Tick(Dt);
            ai.Tick(Dt, enemies);
            for (var i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                var at = world.At(e.Address).Position;
                walked[i] += Vector2.Distance(new(at.X, at.Z), new(last[i].X, last[i].Z));
                last[i] = at;
                farthest[i] = MathF.Max(farthest[i], Vector2.Distance(new(at.X, at.Z), new(posts[i].X, posts[i].Z)));
                if (e.IsSentry && e.AiState == CombatantAiState.Idle)
                    unaware[i]++;
            }
            if (controlledKiller)
            {
                if (world.Agents.ContainsKey(killer.Address)) killerDriven++;
                if (killer.AiState != killerState) killerStateChanged = true;
            }
            frames++;
        }

        var text = new StringBuilder();
        var failures = new List<string>();
        text.Append($"patrol {name} ({frames} frames, {Duration:0} s):\n");
        for (var i = 0; i < enemies.Count; i++)
        {
            var e = enemies[i];
            var share = 100.0 * unaware[i] / frames;
            var role = e == killer && controlledKiller ? "controlled killer" : e.IsSentry ? "patrolling sentry" : "plain enemy";
            text.Append($"  {e.Name} ({role}): unaware {share:F1}% of frames, walked {walked[i]:F1} m, farthest from post {farthest[i]:F1} m, state {e.AiState}\n");
            if (e == killer && controlledKiller)
                continue;
            if (!e.IsSentry)
                continue;
            // Every sentry the AI drives patrols: unaware nearly all the time (the killer, not controlled, first
            // finishes its fight), walking legs about its post, never further than a leg (9 m) and the reach.
            var minShare = e == killer ? 90.0 : 99.0;
            if (share < minShare) failures.Add($"{e.Name} unaware only {share:F1}%");
            if (walked[i] < 8f) failures.Add($"{e.Name} walked only {walked[i]:F1} m");
            if (farthest[i] > 10f) failures.Add($"{e.Name} went {farthest[i]:F1} m from its post");
        }
        if (controlledKiller)
        {
            var moved = Vector3.Distance(world.At(killer.Address).Position, killerStart);
            text.Append($"  controlled killer: driven by the AI on {killerDriven} frames, state changed {killerStateChanged}, moved {moved:F2} m\n");
            if (killerDriven > 0) failures.Add($"the controlled killer was driven on {killerDriven} frames");
            if (killerStateChanged) failures.Add("the controlled killer's AI state was changed");
            if (moved > 1e-4f) failures.Add($"the controlled killer was moved {moved:F2} m");
        }
        return new CaseResult(name, text.ToString(), failures);
    }

    // The flat world and the enemy AI as Run builds them, with the player at playerAt (dead unless alive).
    private static (FlatWorld World, FakeEngine Engine, NpcAiController Ai, HashSet<nint> Controlled, List<SimulatedNpc> Enemies)
        Setup(Settings settings, Vector3 playerAt, bool playerAlive)
    {
        var log = new LoopLog();
        var world = new FlatWorld(log);
        var engine = new FakeEngine(world, log, new RuleSettings(WarFallenRule.StayDown, false));
        var attacks = new FakeAttacks(engine, new DamageCalculator(new Random(4)) { PlayerMaxHp = () => 60000f }, log);
        const uint playerId = 0x10000001;
        world.Player = world.Add(new Body { Name = "P", Address = 0x100000, EntityId = playerId, Position = playerAt });
        engine.State.PlayerState = new SimulatedEntityState { EntityId = playerId, Name = "P", IsPlayer = true, CurrentHp = playerAlive ? 60000 : 0, MaxHp = 60000 };
        var controlled = new HashSet<nint>();
        var ai = new NpcAiController(engine, world, attacks, settings, DispatchProxy.Create<IPluginLog, NullLogProxy>(),
            collectPartyPositions: null, isExternallyControlled: controlled.Contains, isTelegraphBusy: attacks.IsBusy)
        {
            Crowd = world,
            RandomSource = new Random(7),
        };
        var enemies = new List<SimulatedNpc>();
        engine.Enemies = enemies;
        return (world, engine, ai, controlled, enemies);
    }

    private static SimulatedNpc AddEnemy(FlatWorld world, FakeEngine engine, List<SimulatedNpc> enemies, string name, Vector3 at, Vector3 post,
        bool sentry, bool patrols)
    {
        var i = enemies.Count;
        var id = 0xF0000001u + (uint)i;
        var address = (nint)(0x200000 + i * 0x1000);
        world.Add(new Body { Name = name, Address = address, EntityId = id, Position = at, Rotation = MathF.PI });
        var npc = new SimulatedNpc
        {
            SimulatedEntityId = id, Name = name, BattleChara = (BattleChara*)address, Behavior = Kits.Melee(), SpawnPosition = post,
            IsSpawned = true, IsClientControlled = true, IsSentry = sentry, Patrols = patrols,
            State = new SimulatedEntityState { EntityId = id, Name = name, CurrentHp = 20000, MaxHp = 20000, MaxMp = 5000, CurrentMp = 5000 },
        };
        engine.State.Entities[id] = npc.State;
        enemies.Add(npc);
        return npc;
    }

    /// <summary>
    /// Review 2026-11-15: five sentries that hold their post (no patrol) whose posts all came out at one spot (behind
    /// one wall): the posts are spread PostSpacing apart, each walks to its own and stands still there.
    /// </summary>
    private static CaseResult RunSharedPost()
    {
        var (world, engine, ai, _, enemies) = Setup(new Settings { SensePartyMembers = false, EnableCombatCompanions = true, TacticsRespectEngage = true },
            new Vector3(0f, 0f, -40f), playerAlive: false);
        var post = new Vector3(0f, 0f, 20f);
        for (var i = 0; i < 5; i++)
        {
            var angle = i * MathF.Tau / 5f;
            AddEnemy(world, engine, enemies, $"H{i + 1}", post + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * 6f, post, sentry: true, patrols: false);
        }
        engine.State.IsActive = true;
        engine.OnSimulationStarted?.Invoke();
        var lastMoves = new float[enemies.Count];
        var last = enemies.Select(e => world.At(e.Address).Position).ToArray();
        var frames = (int)(Duration / Dt);
        for (var f = 0; f < frames; f++)
        {
            world.Update(Dt);
            engine.Tick(Dt);
            ai.Tick(Dt, enemies);
            for (var i = 0; i < enemies.Count; i++)
            {
                var at = world.At(enemies[i].Address).Position;
                if (f >= frames - 120)
                    lastMoves[i] += Vector2.Distance(new(at.X, at.Z), new(last[i].X, last[i].Z));
                last[i] = at;
            }
        }
        var text = new StringBuilder("patrol shared-post (five holding one post, 20 s):\n");
        var failures = new List<string>();
        var minApart = float.MaxValue;
        for (var i = 0; i < enemies.Count; i++)
            for (var j = i + 1; j < enemies.Count; j++)
                minApart = MathF.Min(minApart, Vector2.Distance(new(enemies[i].SpawnPosition.X, enemies[i].SpawnPosition.Z), new(enemies[j].SpawnPosition.X, enemies[j].SpawnPosition.Z)));
        for (var i = 0; i < enemies.Count; i++)
        {
            var e = enemies[i];
            var at = world.At(e.Address).Position;
            var away = Vector2.Distance(new(at.X, at.Z), new(e.SpawnPosition.X, e.SpawnPosition.Z));
            text.Append($"  {e.Name}: post {e.SpawnPosition.X:F2},{e.SpawnPosition.Z:F2}, {away:F2} m from it, moved {lastMoves[i]:F2} m in the last 2 s, walking {e.HasPatrolGoal}\n");
            if (away > 1.7f) failures.Add($"{e.Name} {away:F2} m from its post");
            if (lastMoves[i] > 0.05f || e.HasPatrolGoal) failures.Add($"{e.Name} still walking at the end");
        }
        text.Append($"  posts at least {minApart:F2} m apart\n");
        if (minApart < 1.4f) failures.Add($"posts only {minApart:F2} m apart");
        return new CaseResult("shared-post", text.ToString(), failures);
    }

    /// <summary>
    /// Review 2026-11-15: auto-engage fires while an enemy is held (Enemy Control / a director): it joins the fight
    /// once it is let go, instead of standing idle for the rest of the battle.
    /// </summary>
    private static CaseResult RunMissedAutoEngage()
    {
        var (world, engine, ai, controlled, enemies) = Setup(new Settings
        {
            SensePartyMembers = false, EnableCombatCompanions = false, EnableNpcAutoEngage = true, NpcAutoEngageDelay = 0.5f,
        }, new Vector3(0f, 0f, 60f), playerAlive: true);
        var held = AddEnemy(world, engine, enemies, "N1", Vector3.Zero, Vector3.Zero, sentry: false, patrols: false);
        var free = AddEnemy(world, engine, enemies, "N2", new Vector3(3f, 0f, 0f), new Vector3(3f, 0f, 0f), sentry: false, patrols: false);
        controlled.Add(held.Address);
        engine.State.IsActive = true;
        engine.OnSimulationStarted?.Invoke();
        var heldStateWhileHeld = CombatantAiState.Idle;
        for (var f = 0; f < (int)(5f / Dt); f++)
        {
            if (engine.State.SimulationTime >= 2f)
                controlled.Clear();
            else
                heldStateWhileHeld = held.AiState;
            world.Update(Dt);
            engine.Tick(Dt);
            ai.Tick(Dt, enemies);
        }
        var failures = new List<string>();
        var text = $"patrol missed-auto-engage: held at the auto-engage (0.5 s), let go at 2 s: state while held {heldStateWhileHeld}, " +
                   $"at 5 s {held.AiState}; the other {free.AiState}\n";
        if (heldStateWhileHeld != CombatantAiState.Idle) failures.Add("the held enemy's state was changed while held");
        if (!held.IsEngaged) failures.Add("the enemy let go never joined the fight");
        if (!free.IsEngaged) failures.Add("auto-engage did not engage the free enemy");
        return new CaseResult("missed-auto-engage", text, failures);
    }
}
