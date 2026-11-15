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

    private static CaseResult Run(string name, bool controlledKiller, bool forcedEngage)
    {
        var log = new LoopLog();
        var world = new FlatWorld(log);
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
}
