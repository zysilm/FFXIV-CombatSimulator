using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using CombatSimulator.Combat;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

// Fixed-seed decision replay (docs/PLUGIN_ARCHITECTURE.md §8.1), the part the extracted pure code allows
// at refactor stage B1. See README.md.
//
//   dotnet run -c Release             compare with the golden files, exit 1 on the first difference
//   dotnet run -c Release -- --write  (re)write the golden files
//
// golden/functions.txt: every CombatantMath / AggroModel function on a fixed input table (the function level
// of §8.1). golden/<scenario>.txt: a flat fake world stepped frame by frame in which every decision the pure
// layer makes (targets drawn and their weights, reach, standing gap, in-reach hysteresis, stand points,
// speeds, turning, MP prices, pace) comes from the production code. The frame loop around them is this
// harness's, not the plugin's: the two controllers cannot run offline before stage B4a.

var write = args.Contains("--write");
var dir = Path.Combine(Here(), "golden");
Directory.CreateDirectory(dir);

var outputs = new List<(string File, string Text)> { ("functions.txt", FunctionTable.Build()) };
foreach (var scenario in Scenarios.All)
{
    var sb = new StringBuilder();
    sb.Append("# ").Append(scenario.Name).Append(": ").Append(scenario.Description).Append('\n');
    for (var i = 0; i < 3; i++)
        sb.Append(new Replay(scenario, seed: 1 + i, steps: Steps.ForSeedIndex(i)).Run());
    outputs.Add((scenario.Name + ".txt", sb.ToString()));
}
// The round level (stage B4a): the real combat loops in the flat fake world (LoopReplay.cs).
foreach (var scenario in LoopScenarios.All)
{
    var sb = new StringBuilder();
    sb.Append("# loop-").Append(scenario.Name).Append(": ").Append(scenario.Description).Append('\n');
    for (var i = 0; i < 3; i++)
        sb.Append(new LoopReplay(scenario, seed: 1 + i, steps: Steps.ForSeedIndex(i)).Run());
    outputs.Add(("loop-" + scenario.Name + ".txt", sb.ToString()));
}

var failed = false;
foreach (var (file, text) in outputs)
{
    var path = Path.Combine(dir, file);
    var bytes = Encoding.UTF8.GetBytes(text);
    if (write)
    {
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"wrote {file}: {bytes.Length} bytes, {text.Count(c => c == '\n')} lines");
        continue;
    }
    if (!File.Exists(path))
    {
        Console.WriteLine($"{file}: no golden file (run with --write)");
        failed = true;
        continue;
    }
    var golden = File.ReadAllBytes(path);
    if (golden.AsSpan().SequenceEqual(bytes))
    {
        Console.WriteLine($"{file}: identical ({bytes.Length} bytes)");
        continue;
    }
    failed = true;
    var a = Encoding.UTF8.GetString(golden).Split('\n');
    var b = text.Split('\n');
    var line = 0;
    while (line < a.Length && line < b.Length && a[line] == b[line]) line++;
    Console.WriteLine($"{file}: DIFFERENT at line {line + 1}");
    Console.WriteLine($"  golden: {(line < a.Length ? a[line] : "<end>")}");
    Console.WriteLine($"  now:    {(line < b.Length ? b[line] : "<end>")}");
}
return failed ? 1 : 0;

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static class Fmt
{
    /// <summary>A float as its raw IEEE bits.</summary>
    /// <summary>A float as its raw IEEE bits (any NaN as "NaN": its payload is not fixed, see Tests/CombatMathIdentity).</summary>
    public static string H(float f) => float.IsNaN(f) ? "NaN" : BitConverter.SingleToInt32Bits(f).ToString("X8");
    public static string H(Vector3 v) => $"{H(v.X)},{H(v.Y)},{H(v.Z)}";
}

/// <summary>ICombatAiSettings for the harness (only what the pure layer reads differs from the plugin defaults).</summary>
sealed record Settings : ICombatAiSettings
{
    public bool EnableCombatCompanions { get; init; } = true;
    public int CombatCompanionMaxCount { get; init; } = 10;
    public bool SensePartyMembers { get; init; } = true;
    public bool EnableMapPlayerEnemySensing { get; init; }
    public bool PartyCompanionDeathRagdoll { get; init; } = true;
    public float PartyCommandRange { get; init; } = 10f;
    public float PartyCommandRangeRandomness { get; init; } = 0.15f;
    public bool DevCompanionAppearanceVariant { get; init; }
    public bool EnableNpcAutoEngage { get; init; }
    public float NpcAutoEngageDelay { get; init; } = 2f;
    public bool EnableNpcTargetPlayer { get; init; } = true;
    public bool ActionMode { get; init; } = true;
    public float ActionEnemyAttackSpeed { get; init; } = 1.6f;
    public float NpcSkillRateLimit { get; init; } = 5f;
    public bool EnemyTelegraphRealShapes { get; init; } = true;
    public bool EnableNpcScale { get; init; } = true;
    public float NpcScale { get; init; } = 0.99f;
    public float NpcAttackRangeScale { get; init; } = 1f;
    public bool EnhancedEnemyTactics { get; init; } = true;
    public bool TacticsRespectEngage { get; init; }
    public bool TacticsCharge { get; init; }
    public int EnemyTacticsMeleeAttackers { get; init; } = 8;
    public bool NpcCollisionActive { get; init; } = true;
    public float DefaultNpcHeightOffset { get; init; }
    public uint WarCaptiveEmoteId { get; init; } = 19;
}

static class Steps
{
    private static readonly float[] Jitter = { 0.012f, 0.021f, 0.017f, 0.033f, 0.009f, 0.016f, 0.025f };

    /// <summary>Seed 1: a fixed 1/60 s; seed 2: a fixed 1/30 s; seed 3: a fixed jittered sequence.</summary>
    public static (string Name, Func<int, float> Dt) ForSeedIndex(int i) => i switch
    {
        0 => ("1/60", _ => 1f / 60f),
        1 => ("1/30", _ => 1f / 30f),
        _ => ("jitter", f => Jitter[f % Jitter.Length]),
    };
}

// ------------------------------------------------------------------ kits and scenarios

static class Kits
{
    /// <summary>Player prices (ActionData.MpCost) of the skills below; 0 for the unknown.</summary>
    public static readonly Dictionary<uint, int> Prices = new()
    {
        [201] = 0, [202] = 0, [211] = 0, [212] = 400, [221] = 2000, [222] = 8500, [231] = 1200,
    };

    public static int PriceOf(uint actionId) => Prices.GetValueOrDefault(actionId);

    static NpcSkill Skill(uint id, float range, float cooldown, int potency, int priority, float hp = 1f, NpcAttackStyle style = NpcAttackStyle.Auto)
        => new() { ActionId = id, Name = $"s{id}", Range = range, Cooldown = cooldown, Potency = potency, Priority = priority, HpThreshold = hp, AttackStyle = style };

    public static NpcBehavior Melee(bool skills = true) => new()
    {
        AutoAttackDelay = 2.5f, AutoAttackRange = 3f, AutoAttackPotency = 110, MoveSpeed = 6f, AutoAttackStyle = NpcAttackStyle.Melee,
        Skills = skills ? new() { Skill(201, 3f, 12f, 300, 2), Skill(202, 3f, 30f, 500, 3, hp: 0.5f) } : new(),
    };

    public static NpcBehavior Ranged() => new()
    {
        AutoAttackDelay = 2.5f, AutoAttackRange = 15f, AutoAttackPotency = 100, MoveSpeed = 6f, AutoAttackStyle = NpcAttackStyle.Ranged,
        Skills = new() { Skill(211, 20f, 8f, 260, 2), Skill(212, 15f, 15f, 380, 3, style: NpcAttackStyle.Melee) },
    };

    public static NpcBehavior Magic() => new()
    {
        AutoAttackDelay = 3f, AutoAttackRange = 25f, AutoAttackPotency = 90, MoveSpeed = 5f, AutoAttackStyle = NpcAttackStyle.Magic,
        Skills = new() { Skill(221, 25f, 2.5f, 280, 1), Skill(222, 25f, 0f, 900, 4), Skill(231, 8f, 20f, 400, 2, style: NpcAttackStyle.Melee) },
    };

    public static NpcBehavior Clone(NpcBehavior b)
    {
        var c = new NpcBehavior
        {
            AutoAttackDelay = b.AutoAttackDelay, AutoAttackRange = b.AutoAttackRange, AutoAttackPotency = b.AutoAttackPotency,
            MoveSpeed = b.MoveSpeed, AutoAttackStyle = b.AutoAttackStyle,
        };
        foreach (var s in b.Skills) c.Skills.Add(s.CloneDefinition());
        return c;
    }
}

sealed class Fighter
{
    public string Name = "";
    public uint Id;
    public bool Ally, IsPlayer, IsPrayer, Seizer;
    public NpcBehavior Behavior = new();
    public SimulatedEntityState State = new();
    public Vector3 Position;
    public float Rotation, Radius = 0.5f;
    public bool InReach;
    public float AutoTimer, NextSelectAt;
    public uint Target;
    public Vector3? Goal;
    public bool Dead;
    public ReachPolicy ReachPolicy => Ally ? ReachPolicy.Ally : ReachPolicy.Enemy;
    public MovePolicy MovePolicy => Ally ? MovePolicy.Ally : MovePolicy.Enemy;
    public bool Alive => State.IsAlive;
    public bool Acts => !IsPlayer && !IsPrayer;
}

sealed record Scenario(string Name, string Description, Settings Settings, bool GuardBody, Func<List<Fighter>> Build);

static class Scenarios
{
    static Fighter Make(string name, uint id, bool ally, NpcBehavior kit, Vector3 at, int hp, int maxMp, int mp, float radius = 0.5f, bool tank = false)
        => new()
        {
            Name = name, Id = id, Ally = ally, Behavior = Kits.Clone(kit), Position = at, Radius = radius,
            Rotation = ally ? 0f : MathF.PI,
            State = new SimulatedEntityState { EntityId = id, Name = name, IsCompanion = ally, CurrentHp = hp, MaxHp = hp, MaxMp = maxMp, CurrentMp = mp, IsTank = tank },
        };

    static Fighter Player(Vector3 at, bool alive) => new()
    {
        Name = "P", Id = 0x10000001, Ally = true, IsPlayer = true, Position = at,
        State = new SimulatedEntityState { EntityId = 0x10000001, Name = "P", IsPlayer = true, CurrentHp = alive ? 60000 : 0, MaxHp = 60000, MaxMp = 10000, CurrentMp = 10000 },
    };

    static uint AllyId(int i) => 0xF1000001u + (uint)i;
    static uint EnemyId(int i) => 0xF0000001u + (uint)i;

    public static readonly Scenario[] All =
    {
        new("field-8v8-melee", "8 allies (2 tanks) + the player vs 8 melee enemies, Stay down; defaults (NpcScale 0.99, pace 1.6)",
            new Settings(), false, () =>
            {
                var list = new List<Fighter> { Player(new Vector3(0f, 0f, -4f), alive: true) };
                for (var i = 0; i < 8; i++)
                    list.Add(Make($"A{i + 1}", AllyId(i), true, Kits.Melee(), new Vector3((i - 3.5f) * 2.5f, 0f, (i % 3) * 0.7f), 30000, 10000, 10000, tank: i < 2));
                for (var i = 0; i < 8; i++)
                    list.Add(Make($"E{i + 1}", EnemyId(i), false, Kits.Melee(), new Vector3((i - 3.5f) * 2.5f, 0.3f, 24f + (i % 2) * 1.1f), 25000, 5000, 5000,
                        radius: i % 4 == 3 ? 1.6f : 0.5f, tank: i == 0));
                return list;
            }),
        new("mixed-ranged-magic", "6 allies (melee, ranged, magic) + the player vs 8 mixed enemies with low MP; range scale 1.25, skill rate limit 1/s",
            new Settings { NpcAttackRangeScale = 1.25f, NpcSkillRateLimit = 1f }, false, () =>
            {
                var list = new List<Fighter> { Player(new Vector3(0f, 0f, -6f), alive: true) };
                NpcBehavior[] allyKits = { Kits.Melee(), Kits.Melee(), Kits.Ranged(), Kits.Ranged(), Kits.Magic(), Kits.Magic() };
                for (var i = 0; i < allyKits.Length; i++)
                    list.Add(Make($"A{i + 1}", AllyId(i), true, allyKits[i], new Vector3((i - 2.5f) * 3f, 0f, i >= 2 ? -3f : 0f), 26000, 10000, i >= 4 ? 3000 : 10000, tank: i == 0));
                NpcBehavior[] enemyKits = { Kits.Melee(), Kits.Melee(), Kits.Melee(false), Kits.Ranged(), Kits.Ranged(), Kits.Magic(), Kits.Magic(), Kits.Magic() };
                for (var i = 0; i < enemyKits.Length; i++)
                    list.Add(Make($"E{i + 1}", EnemyId(i), false, enemyKits[i], new Vector3((i - 3.5f) * 2.8f, 0f, 30f + (i >= 3 ? 5f : 0f)), 22000, 5000, i >= 5 ? 1500 : 5000,
                        radius: i == 2 ? 2.4f : 0.5f));
                return list;
            }),
        new("guard-body", "Warring States, retreat: the lord's body at the origin, one praying over it, 6 guards; 8 enemies, 4 going for the body",
            new Settings(), true, () =>
            {
                var list = new List<Fighter> { Player(Vector3.Zero, alive: false) };
                var prayer = Make("A1", AllyId(0), true, Kits.Melee(false), new Vector3(0f, 0f, 1.3f), 30000, 10000, 10000);
                prayer.IsPrayer = true;
                prayer.State.IncomingDamageScale = 4f;
                list.Add(prayer);
                for (var i = 1; i < 7; i++)
                {
                    var angle = i * MathF.PI / 3f;
                    list.Add(Make($"A{i + 1}", AllyId(i), true, i % 3 == 0 ? Kits.Ranged() : Kits.Melee(), new Vector3(MathF.Sin(angle) * 3f, 0f, MathF.Cos(angle) * 3f), 30000, 10000, 10000, tank: i == 1));
                }
                for (var i = 0; i < 8; i++)
                {
                    var e = Make($"E{i + 1}", EnemyId(i), false, i % 4 == 3 ? Kits.Ranged() : Kits.Melee(), new Vector3((i - 3.5f) * 3f, 0f, 22f + (i % 3)), 25000, 5000, 5000);
                    e.Seizer = i % 2 == 0;
                    list.Add(e);
                }
                return list;
            }),
    };
}

// ------------------------------------------------------------------ the replay

/// <summary>
/// One run. Per frame, in the plugin's order (docs/PLUGIN_ARCHITECTURE.md §6.4): threat decays; allies
/// look for targets (throttled to 0.25 s), then enemies; allies act and set their goals; the "crowd" moves
/// everyone with a goal in a straight line; simulation time advances; enemies act and set their goals.
/// Seeds as §8.1: enemy skill draws Random(s), ally timers Random(s+1), the aggro seed source Random(s+2),
/// damage Random(s+3).
/// </summary>
sealed class Replay
{
    private const float Duration = 30f;
    private const float SnapshotEvery = 2f;
    private const float SkillTokenBurst = 3f;
    private const float LordGuardRange = 10f;
    private const float NearBodyTargetReach = 12f;
    private const float ReachabilityRecoveryDistance = 0.75f;
    private const float AttackLock = 0.6f;

    private readonly Scenario scenario;
    private readonly int seed;
    private readonly (string Name, Func<int, float> Dt) steps;
    private readonly List<Fighter> all;
    private readonly Fighter player;
    private readonly Settings cfg;
    private readonly Random enemyRng, allyRng, aggroSeed;
    private readonly DamageCalculator damage;
    private readonly Dictionary<uint, AggroState> aggro = new();
    private readonly Dictionary<uint, Dictionary<uint, float>> threat = new();
    private readonly Func<uint, int> priceOf = Kits.PriceOf;
    private readonly StringBuilder o = new();
    private float time, skillTokens;
    private int frame;

    public Replay(Scenario scenario, int seed, (string Name, Func<int, float> Dt) steps)
    {
        this.scenario = scenario;
        this.seed = seed;
        this.steps = steps;
        cfg = scenario.Settings;
        all = scenario.Build();
        player = all.First(f => f.IsPlayer);
        enemyRng = new Random(seed);
        allyRng = new Random(seed + 1);
        aggroSeed = new Random(seed + 2);
        damage = new DamageCalculator(new Random(seed + 3)) { IsActionMode = () => cfg.ActionMode, PlayerMaxHp = () => 60000f };
        foreach (var f in all.Where(f => f.Acts))
        {
            var rng = f.Ally ? allyRng : enemyRng;
            f.AutoTimer = (float)rng.NextDouble() * f.Behavior.AutoAttackDelay;
        }
    }

    private Vector3 Body => player.Position;
    private ReachScale Scale => ReachScale.From(cfg);
    private float Pace => CombatantMath.ActionPace(cfg.ActionMode, cfg.ActionEnemyAttackSpeed);
    private Fighter? Get(uint id) => id == 0 ? null : all.Find(f => f.Id == id);
    private IEnumerable<Fighter> Allies => all.Where(f => f.Ally && !f.IsPlayer);
    private IEnumerable<Fighter> Enemies => all.Where(f => !f.Ally);
    private float ReachNow(Fighter f) => CombatantMath.ReachNow(f.Behavior, f.State, f.ReachPolicy, f.Ally ? default : Scale, priceOf);
    private float Gap(Fighter a, Fighter b) => CombatantMath.BodyGap(a.Position, b.Position, a.Radius, b.Radius);

    private AggroState AggroOf(uint id)
    {
        if (!aggro.TryGetValue(id, out var s))
            aggro[id] = s = new AggroState { Rng = new Random(aggroSeed.Next()) };
        return s;
    }

    public string Run()
    {
        o.Append($"## seed {seed} (enemy {seed}, ally {seed + 1}, aggro {seed + 2}, damage {seed + 3}), dt {steps.Name}, {Duration:0} s\n");
        var nextSnapshot = 0f;
        while (time < Duration && all.Any(f => f.Ally && f.Acts && f.Alive) && Enemies.Any(f => f.Alive))
        {
            var dt = steps.Dt(frame);
            if (time >= nextSnapshot)
            {
                Snapshot();
                nextSnapshot += SnapshotEvery;
            }
            AlliesPass(dt);
            CrowdStep(dt);
            time += dt;
            EnemiesPass(dt);
            frame++;
        }
        Snapshot();
        o.Append($"end frame {frame} time {Fmt.H(time)} allies {Allies.Count(f => f.Alive)} enemies {Enemies.Count(f => f.Alive)}\n");
        return o.ToString();
    }

    private void Snapshot()
    {
        foreach (var f in all)
        {
            if (!f.Alive || f.IsPlayer) continue;
            var reach = ReachNow(f);
            o.Append($"S {frame} {f.Name} hp {f.State.CurrentHp} mp {f.State.CurrentMp} pos {Fmt.H(f.Position)} rot {Fmt.H(f.Rotation)} " +
                     $"reach {Fmt.H(reach)} stand {Fmt.H(CombatantMath.StandGap(reach))} in {(f.InReach ? 1 : 0)} tgt {Get(f.Target)?.Name ?? "-"} " +
                     $"auto {Fmt.H(f.AutoTimer)} lock {Fmt.H(f.State.AnimationLock)} cd {string.Join(",", f.Behavior.Skills.Select(s => Fmt.H(s.CooldownRemaining)))}\n");
        }
    }

    // ---------------------------------------------------------------- targets (CombatCompanionManager.Tick)

    private uint PlayerFocus()
    {
        if (!player.Alive) return 0;
        Fighter? best = null;
        var bd = float.MaxValue;
        foreach (var e in Enemies)
        {
            if (!e.Alive) continue;
            var d = CombatantMath.FlatDistance(player.Position, e.Position);
            if (d < bd) { bd = d; best = e; }
        }
        return best?.Id ?? 0;
    }

    private bool Exposed(uint id) => Get(id) is { IsPrayer: true, Alive: true };

    /// <summary>LordSeizerTarget: the one praying, while it lives and is at the body; else the companion nearest the body within 5.5 m.</summary>
    private uint Holder(Fighter enemy)
    {
        if (!scenario.GuardBody || !enemy.Seizer) return 0;
        const float atBody = 3.5f + 2f;
        Fighter? nearest = null;
        var best = atBody;
        foreach (var a in Allies)
        {
            if (!a.Alive) continue;
            var d = CombatantMath.FlatDistance(a.Position, Body);
            if (a.IsPrayer && d <= atBody) return a.Id;
            if (d < best) { best = d; nearest = a; }
        }
        return nearest?.Id ?? 0;
    }

    private bool Reachable(Fighter ally, Fighter enemy)
    {
        if (!scenario.GuardBody) return true;
        var standOff = ReachNow(ally) + ally.Radius + enemy.Radius;
        var overflow = CombatantMath.FlatDistance(enemy.Position, Body) - standOff - LordGuardRange;
        return overflow <= ReachabilityRecoveryDistance;
    }

    private (int Melee, int Ranged) CountOn(uint id)
    {
        int melee = 0, ranged = 0;
        foreach (var f in all)
        {
            if (!f.Acts || !f.Alive || f.Target != id) continue;
            if (AggroModel.IsRangedStyle(f.Behavior.AutoAttackStyle)) ranged++; else melee++;
        }
        return (melee, ranged);
    }

    private void DrawEnemyTarget(Fighter e)
    {
        var candidates = all.Where(f => f.Ally && f.Alive).ToList();
        if (candidates.Count == 0) { e.Target = 0; return; }
        var s = AggroOf(e.Id);
        var currentLive = candidates.Exists(c => c.Id == e.Target);
        var holder = Holder(e);
        var due = AggroModel.IsDue(s, time, currentLive, holder != s.Holder);
        s.HitByNew = false;
        s.Holder = holder;
        if (!due) return;

        var self = new AggroModel.Self(e.Id, e.Position, AggroModel.EnemyAggroReach(e.Behavior), AggroModel.IsRangedStyle(e.Behavior.AutoAttackStyle), currentLive ? e.Target : 0);
        var weights = new List<(uint Id, float Weight)>();
        foreach (var c in candidates)
        {
            var friendTarget = c.IsPlayer ? PlayerFocus() : c.Target;
            var (melee, ranged) = CountOn(c.Id);
            var w = AggroModel.Weight(self, new AggroModel.Candidate(c.Id, c.Position, CombatantMath.HpShare(c.State), friendTarget),
                AggroModel.ThreatShare(threat.GetValueOrDefault(e.Id), c.Id), melee, ranged);
            if (c.Id == holder) w *= AggroModel.TaskGain;
            if (Exposed(c.Id)) w *= AggroModel.ExposedGain;
            weights.Add((c.Id, w));
        }
        Commit(e, s, weights);
    }

    private void DrawAllyTarget(Fighter a)
    {
        var enemies = Enemies.Where(f => f.Alive).ToList();
        if (enemies.Count == 0) { a.Target = 0; return; }
        if (scenario.GuardBody && CombatantMath.FlatDistance(a.Position, Body) > LordGuardRange) { a.Target = 0; return; }
        var s = AggroOf(a.Id);
        var current = enemies.Find(f => f.Id == a.Target);
        var focus = PlayerFocus();
        var due = AggroModel.IsDue(s, time, current != null, focus != s.Focus);
        s.HitByNew = false;
        s.Focus = focus;
        if (!due && scenario.GuardBody && !Reachable(a, current!))
            due = true;
        if (!due) return;

        var self = new AggroModel.Self(a.Id, a.Position, ReachNow(a), AggroModel.IsRangedStyle(a.Behavior.AutoAttackStyle), current != null ? a.Target : 0);
        var weights = new List<(uint Id, float Weight)>();
        foreach (var e in enemies)
        {
            var (melee, ranged) = CountOn(e.Id);
            var w = AggroModel.Weight(self, new AggroModel.Candidate(e.Id, e.Position, CombatantMath.HpShare(e.State), e.Target),
                AggroModel.ThreatShare(threat.GetValueOrDefault(a.Id), e.Id), melee, ranged);
            if (focus != 0 && e.Id == focus) w *= AggroModel.FocusGain;
            if (e.Target == player.Id && player.Alive) w *= AggroModel.AttackingPlayerGain;
            if (Exposed(e.Target)) w *= AggroModel.ExposedGain;
            if (scenario.GuardBody)
            {
                if (e.Seizer) w *= AggroModel.GuardSeizerGain;
                w *= 1f + Math.Clamp(1f - CombatantMath.FlatDistance(e.Position, Body) / NearBodyTargetReach, 0f, 1f);
                if (!Reachable(a, e)) w *= AggroModel.UnreachableGain;
            }
            weights.Add((e.Id, w));
        }
        Commit(a, s, weights);
    }

    private void Commit(Fighter f, AggroState s, List<(uint Id, float Weight)> weights)
    {
        var drawn = AggroModel.Draw(s, weights);
        AggroModel.Hold(s, drawn, time);
        o.Append($"D {frame} {f.Name} {Get(f.Target)?.Name ?? "-"} -> {Get(drawn)?.Name ?? "-"} dwell {Fmt.H(s.DwellUntil)} w {string.Join(" ", weights.Select(x => $"{Get(x.Id)!.Name}:{Fmt.H(x.Weight)}"))}\n");
        f.Target = drawn;
    }

    // ---------------------------------------------------------------- allies (TickCompanion)

    private void AlliesPass(float dt)
    {
        foreach (var table in threat.Values)
            foreach (var key in table.Keys.ToList())
                table[key] = EnemyMovementMath.DecayThreat(table[key], dt);

        foreach (var a in Allies)
        {
            if (!a.Alive || !a.Acts) continue;
            var live = Get(a.Target) is { Alive: true, Ally: false };
            if (!live || time >= a.NextSelectAt)
            {
                DrawAllyTarget(a);
                a.NextSelectAt = time + 0.25f + (a.Id % 8) * 0.02f;
            }
        }
        foreach (var e in Enemies)
            if (e.Alive)
                DrawEnemyTarget(e);

        foreach (var a in Allies)
            TickAlly(a, dt);
    }

    private void TickAlly(Fighter a, float dt)
    {
        if (!a.Acts) return;
        a.State.AnimationLock = Math.Max(0, a.State.AnimationLock - dt);
        foreach (var skill in a.Behavior.Skills)
            skill.CooldownRemaining = Math.Max(0, skill.CooldownRemaining - dt);
        if (!a.Alive) { NoteDeath(a); a.Goal = null; return; }
        a.State.RegenerateMp(CombatBudgetModel.EnemyMpRegenPerSecond, dt);

        if (scenario.GuardBody && CombatantMath.FlatDistance(a.Position, Body) > LordGuardRange)
        {
            a.Goal = Body;
            return;
        }
        var target = Get(a.Target);
        if (target is not { Alive: true })
        {
            a.Goal = null;
            return;
        }

        // CrowdApproach
        var reach = ReachNow(a);
        var gap = Gap(a, target);
        var stand = CombatantMath.StandGap(reach);
        var was = a.InReach;
        a.InReach = CombatantMath.NextInReach(a.InReach, gap, reach, stand);
        if (was != a.InReach)
            o.Append($"R {frame} {a.Name} in {(a.InReach ? 1 : 0)} gap {Fmt.H(gap)} reach {Fmt.H(reach)} stand {Fmt.H(stand)}\n");
        var walking = !a.InReach;
        a.Goal = walking ? CombatantMath.StandPoint(a.Position, target.Position, stand + a.Radius + target.Radius) : null;
        if (walking && gap > ReachNow(a))
            return;
        if (!walking)
            Turn(a, target.Position, dt);

        if (a.State.AnimationLock > 0)
            return;
        foreach (var skill in a.Behavior.Skills.OrderByDescending(s => s.Priority))
        {
            if (skill.CooldownRemaining > 0) continue;
            if (gap > CombatantMath.SkillReach(skill, a.Behavior.AutoAttackStyle, ReachPolicy.Ally, default)) continue;
            var mpCost = CombatantMath.SkillMpCost(priceOf(skill.ActionId), a.State.MaxMp);
            if (a.State.CurrentMp < mpCost) continue;
            Hit(a, target, skill.Potency, $"skill {skill.ActionId}");
            a.State.CurrentMp = Math.Max(0, a.State.CurrentMp - mpCost);
            skill.CooldownRemaining = skill.Cooldown / Pace;
            a.State.AnimationLock = AttackLock;
            return;
        }
        a.AutoTimer -= dt;
        if (a.AutoTimer <= 0)
        {
            if (a.Behavior.AutoAttackStyle == NpcAttackStyle.Magic && gap > CombatantMath.MagicAutoReach(ReachPolicy.Ally, default))
                return;
            a.AutoTimer = a.Behavior.AutoAttackDelay / Pace;
            Hit(a, target, a.Behavior.AutoAttackPotency, "auto");
            a.State.CurrentMp = Math.Min(a.State.MaxMp, a.State.CurrentMp + CombatBudgetModel.EnemyAutoAttackMpRestore);
            a.State.AnimationLock = AttackLock;
        }
    }

    // ---------------------------------------------------------------- enemies (NpcAiController.Tick)

    private void EnemiesPass(float dt)
    {
        skillTokens = MathF.Min(SkillTokenBurst, skillTokens + MathF.Max(0f, cfg.NpcSkillRateLimit) * dt);
        foreach (var e in Enemies)
        {
            foreach (var skill in e.Behavior.Skills)
                skill.CooldownRemaining = MathF.Max(0f, skill.CooldownRemaining - dt);
            if (!e.Alive) { NoteDeath(e); e.Goal = null; continue; }
            e.State.AnimationLock = MathF.Max(0f, e.State.AnimationLock - dt);
            e.State.RegenerateMp(CombatBudgetModel.EnemyMpRegenPerSecond, dt);
            EnemyAct(e, dt);
        }
        foreach (var e in Enemies)
            if (e.Alive)
                EnemyGoal(e, dt);
    }

    private bool TryTakeSkillToken()
    {
        if (cfg.NpcSkillRateLimit <= 0f) return true;
        if (skillTokens < 1f) return false;
        skillTokens -= 1f;
        return true;
    }

    private void EnemyAct(Fighter e, float dt)
    {
        var target = Get(e.Target);
        if (target is not { Alive: true }) return;
        var gap = Gap(e, target);
        if (gap > ReachNow(e)) return;
        if (e.State.AnimationLock > 0) return;

        var hpPercent = (float)e.State.CurrentHp / e.State.MaxHp;
        List<NpcSkill>? ready = null;
        foreach (var skill in e.Behavior.Skills)
        {
            if (skill.CooldownRemaining > 0) continue;
            if (gap > CombatantMath.SkillReach(skill, e.Behavior.AutoAttackStyle, ReachPolicy.Enemy, Scale)) continue;
            if (hpPercent > skill.HpThreshold) continue;
            if (e.State.CurrentMp < CombatantMath.SkillMpCost(priceOf(skill.ActionId), e.State.MaxMp)) continue;
            (ready ??= new()).Add(skill);
        }
        if (ready != null && TryTakeSkillToken())
        {
            var skill = ready[enemyRng.Next(ready.Count)];
            var mpCost = CombatantMath.SkillMpCost(priceOf(skill.ActionId), e.State.MaxMp);
            Hit(e, target, skill.Potency, $"skill {skill.ActionId}");
            e.State.CurrentMp = Math.Max(0, e.State.CurrentMp - mpCost);
            skill.CooldownRemaining = skill.Cooldown / Pace;
            e.State.AnimationLock = MathF.Max(e.State.AnimationLock, AttackLock);
            return;
        }
        e.AutoTimer -= dt;
        if (e.AutoTimer <= 0)
        {
            if (e.Behavior.AutoAttackStyle == NpcAttackStyle.Magic && gap > CombatantMath.MagicAutoReach(ReachPolicy.Enemy, Scale))
                return;
            e.AutoTimer = e.Behavior.AutoAttackDelay / Pace;
            Hit(e, target, e.Behavior.AutoAttackPotency, "auto");
            e.State.CurrentMp = Math.Min(e.State.MaxMp, e.State.CurrentMp + CombatBudgetModel.EnemyAutoAttackMpRestore);
            e.State.AnimationLock = MathF.Max(e.State.AnimationLock, AttackLock);
        }
    }

    /// <summary>TryCrowdMove: the body for those going for it (fighting a guard in reach while the body is held), else the standing point.</summary>
    private void EnemyGoal(Fighter e, float dt)
    {
        var target = Get(e.Target);
        e.Goal = null;
        if (scenario.GuardBody && e.Seizer)
        {
            e.InReach = Holder(e) != 0 && target is { Alive: true } && Gap(e, target) <= ReachNow(e);
            if (!e.InReach) e.Goal = Body;
            return;
        }
        if (target is not { Alive: true }) { e.InReach = false; return; }
        var reach = ReachNow(e);
        var gap = Gap(e, target);
        var stand = CombatantMath.StandGap(reach);
        var was = e.InReach;
        e.InReach = CombatantMath.NextInReach(e.InReach, gap, reach, stand);
        if (was != e.InReach)
            o.Append($"R {frame} {e.Name} in {(e.InReach ? 1 : 0)} gap {Fmt.H(gap)} reach {Fmt.H(reach)} stand {Fmt.H(stand)}\n");
        if (!e.InReach)
            e.Goal = CombatantMath.StandPoint(e.Position, target.Position, stand + (e.Radius + target.Radius));
    }

    // ---------------------------------------------------------------- the flat world

    /// <summary>The "crowd": every fighter with a goal walks straight at its speed and faces where it goes; the rest face their target.</summary>
    private void CrowdStep(float dt)
    {
        foreach (var f in all)
        {
            if (!f.Acts || !f.Alive) continue;
            if (f.Goal is { } goal)
            {
                var to = new Vector3(goal.X - f.Position.X, 0f, goal.Z - f.Position.Z);
                var d = to.Length();
                var step = CombatantMath.MoveSpeed(f.Behavior.MoveSpeed, f.MovePolicy) * dt;
                if (d > 1e-4f)
                {
                    Turn(f, goal, dt);
                    f.Position += to * (MathF.Min(step, d) / d);
                }
            }
            else if (Get(f.Target) is { Alive: true } t)
                Turn(f, t.Position, dt);
        }
    }

    private void Turn(Fighter f, Vector3 at, float dt)
    {
        var rate = CombatantMath.TurnRate(f.MovePolicy, cfg.EnhancedEnemyTactics, f.Radius);
        if (CombatantMath.TurnToward(f.Position, f.Rotation, at, rate, dt, out var rot))
            f.Rotation = rot;
    }

    private void Hit(Fighter source, Fighter target, int potency, string what)
    {
        var dmg = damage.CalculateNpcAutoAttack(source.State, target.State, potency).Damage;
        if (target.State.IncomingDamageScale != 1f)
            dmg = (int)MathF.Round(dmg * target.State.IncomingDamageScale);
        target.State.CurrentHp = Math.Max(0, target.State.CurrentHp - dmg);
        if (dmg > 0)
        {
            if (!threat.TryGetValue(target.Id, out var table))
                threat[target.Id] = table = new();
            table[source.Id] = table.GetValueOrDefault(source.Id) + EnemyMovementMath.HitThreat(dmg, source.State.IsTank);
            AggroModel.NoteHit(AggroOf(target.Id), source.Id, time);
        }
        o.Append($"A {frame} {source.Name} {what} -> {target.Name} dmg {dmg} hp {target.State.CurrentHp} mp {source.State.CurrentMp}\n");
    }

    private void NoteDeath(Fighter f)
    {
        if (f.Dead) return;
        f.Dead = true;
        o.Append($"X {frame} {f.Name}\n");
    }
}

// ------------------------------------------------------------------ the function table (§8.1, function level)

static class FunctionTable
{
    static readonly float[] Values = { -1f, -0f, 0f, 0.001f, 0.3f, 0.5f, 1f, 2.4999f, 2.5f, 3f, 3.5f, 4.75f, 5f, 5.0001f, 7f, 15f, 25f, 100f, float.PositiveInfinity, float.NaN };
    static readonly Vector3[] Points =
    {
        Vector3.Zero, new(1f, 5f, 0f), new(-3f, 0f, 4f), new(0.0001f, 0f, 0.0005f), new(10f, -2f, -7.5f), new(2.5f, 0.3f, 2.5f),
    };

    public static string Build()
    {
        var o = new StringBuilder();
        void L(string s) => o.Append(s).Append('\n');
        string H(float f) => Fmt.H(f);
        string V(Vector3 v) => Fmt.H(v);
        L("# Every CombatantMath / AggroModel function on a fixed input table; floats as raw bits.");

        foreach (var p in Points) foreach (var q in Points)
        {
            L($"FlatDistance({V(p)};{V(q)}) = {H(CombatantMath.FlatDistance(p, q))}");
            foreach (var r in new[] { 0f, 0.5f, 1.6f })
                L($"BodyGap({V(p)};{V(q)};{H(r)};{H(0.5f)}) = {H(CombatantMath.BodyGap(p, q, r, 0.5f))}");
            foreach (var d in new[] { 0f, 1.5f, 4.1f })
                L($"StandPoint({V(p)};{V(q)};{H(d)}) = {V(CombatantMath.StandPoint(p, q, d))}");
        }
        foreach (var r in Values)
        {
            L($"StandGap({H(r)}) = {H(CombatantMath.StandGap(r))}");
            L($"ActionPace(true;{H(r)}) = {H(CombatantMath.ActionPace(true, r))} ActionPace(false;{H(r)}) = {H(CombatantMath.ActionPace(false, r))}");
            L($"MoveSpeed({H(r)}) enemy = {H(CombatantMath.MoveSpeed(r, MovePolicy.Enemy))} ally = {H(CombatantMath.MoveSpeed(r, MovePolicy.Ally))}");
            L($"TurnRate({H(r)}) enemy on = {H(CombatantMath.TurnRate(MovePolicy.Enemy, true, r))} off = {H(CombatantMath.TurnRate(MovePolicy.Enemy, false, r))} ally = {H(CombatantMath.TurnRate(MovePolicy.Ally, true, r))}");
            L($"EnemyAggroReach(ranged {H(r)}) = {H(AggroModel.EnemyAggroReach(new NpcBehavior { AutoAttackStyle = NpcAttackStyle.Ranged, AutoAttackRange = r }))} melee = {H(AggroModel.EnemyAggroReach(new NpcBehavior { AutoAttackRange = r }))}");
        }
        foreach (var inReach in new[] { false, true })
        foreach (var gap in new[] { 0f, 1f, 1.25f, 1.2501f, 3f, 3.0001f })
        foreach (var reach in new[] { 2f, 3f, 15f })
            L($"NextInReach({inReach};{H(gap)};{H(reach)};{H(CombatantMath.StandGap(reach))}) = {CombatantMath.NextInReach(inReach, gap, reach, CombatantMath.StandGap(reach))}");

        var settings = new[]
        {
            new Settings(), new Settings { EnableNpcScale = false }, new Settings { NpcScale = 5f, NpcAttackRangeScale = 0.01f },
            new Settings { NpcScale = 0.5f, NpcAttackRangeScale = 1.25f }, new Settings { NpcAttackRangeScale = 1000f },
        };
        foreach (var s in settings)
        {
            var scale = ReachScale.From(s);
            L($"ReachScale(npcScale {s.EnableNpcScale} {H(s.NpcScale)}; range {H(s.NpcAttackRangeScale)}) = {H(scale.NonMelee)} {H(scale.Melee)}");
            foreach (var r in Values)
                L($"  ScaleNonMelee({H(r)}) = {H(CombatantMath.ScaleNonMeleeRange(r, scale))} ScaleMelee = {H(CombatantMath.ScaleMeleeRange(r, scale))}");
            foreach (var style in Enum.GetValues<NpcAttackStyle>())
            {
                var b = new NpcBehavior { AutoAttackStyle = style, AutoAttackRange = 2.5f };
                L($"  AutoReach({style} 2.5) enemy = {H(CombatantMath.AutoReach(b, ReachPolicy.Enemy, scale))} ally = {H(CombatantMath.AutoReach(b, ReachPolicy.Ally, default))}");
                foreach (var skillStyle in Enum.GetValues<NpcAttackStyle>())
                {
                    var skill = new NpcSkill { Range = 6f, AttackStyle = skillStyle };
                    L($"  SkillReach({skillStyle} 6; auto {style}) enemy = {H(CombatantMath.SkillReach(skill, style, ReachPolicy.Enemy, scale))} ally = {H(CombatantMath.SkillReach(skill, style, ReachPolicy.Ally, default))}");
                }
            }
            L($"  MagicAutoReach enemy = {H(CombatantMath.MagicAutoReach(ReachPolicy.Enemy, scale))} ally = {H(CombatantMath.MagicAutoReach(ReachPolicy.Ally, default))}");
            foreach (var kit in new[] { Kits.Melee(), Kits.Ranged(), Kits.Magic(), Kits.Melee(false) })
            foreach (var (hp, mp) in new[] { (25000, 5000), (10000, 5000), (25000, 300), (0, 0) })
            {
                var b = Kits.Clone(kit);
                if (hp == 0 && b.Skills.Count > 0) b.Skills[0].CooldownRemaining = 1f;
                var state = new SimulatedEntityState { CurrentHp = hp, MaxHp = hp == 0 ? 0 : 25000, CurrentMp = mp, MaxMp = 5000 };
                L($"  ReachNow({b.AutoAttackStyle} hp {hp} mp {mp}) enemy = {H(CombatantMath.ReachNow(b, state, ReachPolicy.Enemy, scale, Kits.PriceOf))} ally = {H(CombatantMath.ReachNow(b, state, ReachPolicy.Ally, default, Kits.PriceOf))}");
            }
        }
        foreach (var price in new[] { -5, 0, 1, 400, 2000, 8500, 10000, 20000 })
        foreach (var maxMp in new[] { 0, 1, 5000, 10000 })
            L($"SkillMpCost({price};{maxMp}) = {CombatantMath.SkillMpCost(price, maxMp)}");
        foreach (var hp in new[] { (0, 0), (5, 10), (10, 10), (-3, 10), (7, -1) })
            L($"HpShare({hp.Item1}/{hp.Item2}) = {H(CombatantMath.HpShare(new SimulatedEntityState { CurrentHp = hp.Item1, MaxHp = hp.Item2 }))}");
        foreach (var rot in new[] { -3f, 0f, 1f, 3.1f, 9.5f, -12f })
        foreach (var q in Points)
        foreach (var dt in new[] { 0f, 1f / 60f, 0.5f })
        {
            var wrote = CombatantMath.TurnToward(Points[2], rot, q, 5f, dt, out var newRot);
            L($"TurnToward({V(Points[2])};{H(rot)};{V(q)};5;{H(dt)}) = {wrote} {H(newRot)}");
        }

        // AggroModel
        foreach (var style in Enum.GetValues<NpcAttackStyle>())
            L($"IsRangedStyle({style}) = {AggroModel.IsRangedStyle(style)}");
        var table = new Dictionary<uint, float> { [1] = 0f, [2] = 120f, [3] = 3000f, [4] = 3000.5f };
        foreach (var t in new Dictionary<uint, float>?[] { null, new(), table })
        foreach (var id in new uint[] { 1, 2, 4, 9 })
            L($"ThreatShare({(t == null ? "null" : t.Count.ToString())};{id}) = {H(AggroModel.ThreatShare(t, id))}");
        foreach (var ranged in new[] { false, true })
        foreach (var current in new uint[] { 0, 7 })
        foreach (var candId in new uint[] { 7, 8 })
        foreach (var candTarget in new uint[] { 0, 1 })
        foreach (var share in new[] { 0f, 0.5f, 1f })
        foreach (var (melee, rangedN) in new[] { (0, 0), (1, 0), (3, 2), (0, 5) })
        foreach (var hp in new[] { 1f, 0.2f, -1f })
        {
            var w = AggroModel.Weight(new AggroModel.Self(1, Points[1], 3f, ranged, current), new AggroModel.Candidate(candId, Points[4], hp, candTarget), share, melee, rangedN);
            L($"Weight(ranged {ranged} cur {current} cand {candId} tgt {candTarget} share {H(share)} on {melee}/{rangedN} hp {H(hp)}) = {H(w)}");
        }
        foreach (var live in new[] { false, true })
        foreach (var ext in new[] { false, true })
        foreach (var hitByNew in new[] { false, true })
        foreach (var now in new[] { 1f, 3.4f, 3.5f, 9.99f, 10f })
        {
            var s = new AggroState { HeldSince = 1f, DwellUntil = 10f, HitByNew = hitByNew };
            L($"IsDue(live {live} ext {ext} new {hitByNew} now {H(now)}) = {AggroModel.IsDue(s, now, live, ext)}");
        }
        {
            var s = new AggroState { Target = 2 };
            foreach (var (src, now) in new (uint, float)[] { (1, 0f), (1, 3f), (1, 8.01f), (2, 9f), (3, 9f), (3, 14f), (3, 14.0001f) })
            {
                s.HitByNew = false;
                AggroModel.NoteHit(s, src, now);
                L($"NoteHit({src};{H(now)}) = {s.HitByNew} last {H(s.LastHitBy[src])}");
            }
        }
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var s = new AggroState { Rng = new Random(seed) };
            var weights = new List<(uint, float)> { (5, 0.25f), (6, 3f), (7, 0f), (8, 12.5f) };
            for (var i = 0; i < 6; i++)
            {
                var drawn = AggroModel.Draw(s, weights);
                AggroModel.Hold(s, drawn, i * 1.5f);
                L($"Draw/Hold(seed {seed} #{i}) = {drawn} since {H(s.HeldSince)} until {H(s.DwellUntil)}");
            }
            L($"Draw(empty) = {AggroModel.Draw(s, new List<(uint, float)>())} next {s.Rng.Next()}");
        }
        return o.ToString();
    }
}
