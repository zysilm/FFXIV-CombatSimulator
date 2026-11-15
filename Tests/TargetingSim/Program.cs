using System.Numerics;
using CombatSimulator.Battle;
using CombatSimulator.Combat;
using CombatSimulator.Simulation;

// Offline A/B of target selection for mass battles (20 allies + player vs 20 enemies).
// Ports the old fixed scoring of CombatCompanionManager (enemy: ScoreEnemyTargetCandidate /
// SelectEnemyTargetInternal, ally: ScoreCompanionTarget / SelectCompanionTarget, LordGuardScore /
// LordSeizerTarget) onto a 2D point model with walk speed, reach, melee slots and auto-attacks, and
// compares it with probabilistic candidates. The Production model is the plugin's own: the weights,
// the draw, the hold, the due check and "hit by someone new" are the linked AggroModel, applied the way
// CombatCompanionManager.Aggro.cs applies them. Threat math is the production EnemyMovementMath
// (linked). See README.md.

var seeds = args.FirstOrDefault(x => int.TryParse(x, out _)) is { } s0 ? int.Parse(s0) : 40;
// --seize: only the corpse seizure table (the plugin's CorpseSeizure, linked).
var seizeOnly = args.Contains("--seize");
var models = new[] { Model.Current, Model.CurrentNoOverride, Model.Softmax, Model.Weighted, Model.Production };

foreach (var scenario in seizeOnly ? Array.Empty<Scenario>() : new[] { Scenario.Open, Scenario.Protect })
{
    Console.WriteLine();
    Console.WriteLine($"=== Scenario {scenario}: 20 allies (3 tank / 10 melee / 7 ranged) + player vs 20 enemies (2 tank / 12 melee / 6 ranged)" +
        (scenario == Scenario.Protect ? " + praying retainer (x4 dmg taken, no attacks); 8 enemies are seizers" : "") +
        $", {seeds} seeds, 30 fps, cap {Sim.MaxTime:0}s ===");
    var header = $"{"model",-14}{"maxAtk",11}{"P(>6)",12}{"meanAtk",10}{"sw/min",12}{"idle",11}{"blocked",11}{"win",6}{"tWin s",11}{"tankShr",11}{"starveT",8}{"starveD",8}";
    if (scenario == Scenario.Protect) header += $"{"prayTTD s",11}{"seizOnHld",12}{"atkOnHld",11}{"seizOnPray",12}";
    Console.WriteLine(header);
    foreach (var model in models)
    {
        if (scenario == Scenario.Open && model == Model.CurrentNoOverride)
            continue; // identical to Current without seizers
        var results = new RunResult[seeds];
        Parallel.For(0, seeds, i => results[i] = new Sim(model, scenario, 1000 + i).Run());
        string F(Func<RunResult, double> f, string fmt) => Stat.Format(results.Select(f).ToArray(), fmt);
        var line = $"{model,-14}{F(r => r.MaxAttackers, "0.0"),11}{F(r => r.ShareOver6, "0.00"),12}{F(r => r.MeanAttackers, "0.0"),10}" +
                   $"{F(r => r.SwitchesPerMin, "0.0"),12}{F(r => r.IdleShare, "0.00"),11}{F(r => r.BlockedShare, "0.00"),11}" +
                   $"{results.Count(r => r.AlliesWon) * 100.0 / seeds,5:0}%{F(r => r.TimeToWin, "0"),11}{F(r => r.TankShare, "0.00"),11}" +
                   $"{results.Average(r => r.StarvedTargeted),8:0.00}{results.Average(r => r.StarvedDamaged),8:0.00}";
        if (scenario == Scenario.Protect)
            line += $"{F(r => r.PrayerDeath, "0.0"),11}{F(r => r.SeizersOnHolder, "0.00"),12}{F(r => r.AttackersOnHolder, "0.0"),11}{F(r => r.SeizersOnPrayer, "0.00"),12}";
        Console.WriteLine(line);
    }
}
if (!seizeOnly)
{
    Console.WriteLine();
    Console.WriteLine("maxAtk = most actors targeting one actor during the run; P(>6) = share of 0.5s samples with someone targeted by >6;");
    Console.WriteLine("meanAtk = mean attackers per targeted actor; sw/min = voluntary switches (old target alive) per actor-minute;");
    Console.WriteLine("idle = alive time not swinging (no target / walking / blocked); blocked = in reach but no melee slot (6 per target);");
    Console.WriteLine("tankShr = share of enemy damage landing on ally tanks; starveT = actors alive >=30s never targeted (per run);");
    Console.WriteLine("starveD = enemies alive >=30s never damaged; prayTTD = time until the praying retainer dies (cap if survives);");
    Console.WriteLine("seizOnHld = share of seizer alive-time spent targeting the holder; atkOnHld = mean actors targeting the holder.");
    Console.WriteLine("win = allies' win rate; tWin over allied wins only. Values: mean +- sd over seeds.");
}

// --- The corpse seizure: the lord has just fallen (Warring States with Retreat), the plugin's CorpseSeizure decides. ---
Console.WriteLine();
Console.WriteLine($"=== Scenario Seize: the lord has fallen, retainers (one praying) around the body, enemies 7-13 m off; " +
                  $"Production targeting, production CorpseSeizure; ragdoll up {Sim.SeizeRagdollUpSeconds:0.#} s after the fall; {seeds} seeds, cap {Sim.MaxTime:0}s ===");
Console.WriteLine($"{"case",-26}{"seized",8}{"t seize s",13}{"annihil",9}{"secured",9}{"t end s",11}{"guards@body",13}{"seizers@body",14}{"outnumb",12}{"guardOnSz",12}{"hits@body",11}");
foreach (var c in SeizeCase.All)
{
    var results = new SeizeResult[seeds];
    Parallel.For(0, seeds, i => results[i] = new Sim(Model.Production, Scenario.Seize, 1000 + i, c).RunSeize());
    string F(Func<SeizeResult, double> f, string fmt) => Stat.Format(results.Select(f).ToArray(), fmt);
    Console.WriteLine($"{c.Name,-26}{results.Count(r => r.Seized) * 100.0 / seeds,7:0}%{F(r => r.Seized ? r.TimeToSeize : double.NaN, "0.0"),13}" +
                      $"{results.Count(r => r.Annihilated) * 100.0 / seeds,8:0}%{results.Count(r => r.Secured) * 100.0 / seeds,8:0}%{F(r => r.End, "0"),11}{F(r => r.GuardsAtBody, "0.0"),13}" +
                      $"{F(r => r.SeizersAtBody, "0.0"),14}{F(r => r.OutnumberedShare, "0.00"),12}{F(r => r.GuardsOnSeizers, "0.00"),12}{F(r => r.HitsAtBody, "0"),11}");
}
Console.WriteLine();
Console.WriteLine("seized = the body carried off while a retainer stands (DISGRACED); t seize = seconds from the fall (seized runs only);");
Console.WriteLine("annihil = every retainer fell first (ANNIHILATED: the outcome is decided, the seizure stops); secured = every enemy slain first;");
Console.WriteLine("guards@body / seizers@body = mean retainers within 3.5 m of the body / seizers whose edge is within 3.5 m, over the");
Console.WriteLine("frames anyone goes for it; outnumb = share of those frames with more seizers than retainers there; guardOnSz = share");
Console.WriteLine("of guard alive-time spent on a seizer; hits@body = allied hits on a seizer within 3.5 m of the body (per run).");

enum Side { Ally, Enemy }
enum Role { Tank, Melee, Ranged, Player, Prayer }
enum Model { Current, CurrentNoOverride, Softmax, Weighted, Production }
enum Scenario { Open, Protect, Seize }

/// <summary>Who stands at the body when the lord falls (retainers include the one praying) and who comes for it.</summary>
sealed record SeizeCase(string Name, int AllyTank, int AllyMelee, int AllyRanged, int EnemyTank, int EnemyMelee, int EnemyRanged)
{
    public int Retainers => AllyTank + AllyMelee + AllyRanged + (AllyTank + AllyMelee + AllyRanged > 0 ? 1 : 0);
    // Two senses of "even" (see README.md): "field-even" is a coin flip when nobody goes for the body (TS_NO_SEIZE=1);
    // "guard-even" is a coin flip when the body cannot be taken (SeizeSecondsPerMargin set huge in a scratch build). The
    // one praying fights no one, takes x4, draws the seizers x100 and is replaced by a guard when it falls, so equal
    // numbers lose the guarding fight outright.
    public static readonly SeizeCase[] All =
    {
        // The log (ddddWS, 9 v 9): the lord fell, 8 retainers (one praying) against 9.
        new("9 v 9 lord down: 8 v 9", 1, 4, 2, 1, 5, 3),
        new("equal numbers: 8 v 8", 1, 4, 2, 1, 5, 2),
        new("Palace: 20 v 20", 3, 10, 6, 2, 12, 6),
        new("outnumbered: 8 v 12", 1, 4, 2, 2, 7, 3),
        new("field-even: 20 v 16", 3, 10, 6, 2, 9, 5),
        new("guard-even: 8 v 4", 1, 4, 2, 1, 2, 1),
        new("guard-even: 20 v 12", 3, 10, 6, 2, 7, 3),
        new("outnumbering: 20 v 10", 3, 10, 6, 1, 6, 3),
        // Nobody at the body (read as the retainers being elsewhere): how fast it goes uncontested.
        new("undefended: 0 v 4", 0, 0, 0, 0, 4, 0),
    };
}

record struct SeizeResult(bool Seized, double TimeToSeize, bool Secured, bool Annihilated, double End, double GuardsAtBody, double SeizersAtBody,
    double OutnumberedShare, double GuardsOnSeizers, double HitsAtBody);

sealed class Actor
{
    public int Id;
    public Side Side;
    public Role Role;
    public float X, Z, Hp, MaxHp, Reach, Damage, AtkTimer, DeathTime = -1f;
    public bool Alive = true, Seizer;
    public int Target = -1;
    public float NextSelectAt, DwellUntil, HeldSince;
    public bool EverTargeted, EverDamaged;
    public float[] LastHitBy = Array.Empty<float>();
    public bool HitByNew;
    public Random Rng = null!;
    /// <summary>Production model: the plugin's per-fighter state (its generator is <see cref="Rng"/>).</summary>
    public AggroState Aggro = null!;
    /// <summary>Seize scenario: the body the seizure sends this enemy to, whether a retainer holds it off there
    /// (both set by the production CorpseSeizure), and whether it fights the guard in its reach (NpcAiController.TryCrowdMove).</summary>
    public Vector3? SeizeGoal;
    public bool SeizeHeld, SeizeInReach;
    /// <summary>Seize scenario: the one praying has reached its post before the body (x4 damage, open to the enemy).</summary>
    public bool AtPost;
    public bool IsTank => Role == Role.Tank;
    public bool IsRanged => Role == Role.Ranged;
    public bool Fights => Role != Role.Prayer;
    public bool IsCompanion => Side == Side.Ally && Role != Role.Player; // the prayer is a companion too
}

record struct RunResult(
    double MaxAttackers, double ShareOver6, double MeanAttackers, double SwitchesPerMin, double IdleShare, double BlockedShare,
    bool AlliesWon, double TimeToWin, double TankShare, double StarvedTargeted, double StarvedDamaged,
    double PrayerDeath, bool PrayerSurvived, double SeizersOnHolder, double AttackersOnHolder, double SeizersOnPrayer);

static class Stat
{
    public static string Format(double[] v, string fmt)
    {
        var finite = v.Where(double.IsFinite).ToArray();
        if (finite.Length == 0) return "n/a";
        var m = finite.Average();
        var sd = Math.Sqrt(finite.Sum(x => (x - m) * (x - m)) / Math.Max(1, finite.Length - 1));
        return $"{m.ToString(fmt)}+-{sd.ToString(fmt)}";
    }
}

sealed class Sim
{
    public const float Dt = 1f / 30f, MaxTime = 300f;
    const float Speed = 6f, AttackInterval = 2.5f;
    const int MeleeSlots = 6;

    // --- CombatCompanionManager constants (CombatCompanionManager.cs:44-107, Lord.cs:28-33) ---
    const float ThreatScoreWeight = 150f, ThreatSwitchThreshold = 60f;
    const float SelectedTargetBonus = 130f, AttackingPlayerBonus = 120f, PreviousTargetBonus = 35f, LowHpFinishBonus = 35f;
    const float AssignmentPenalty = 95f, PlayerFocusOvercapPenalty = 160f, DistancePenaltyPerYalm = 1f, TargetSwitchThreshold = 20f;
    const int MaxPlayerFocusAssistants = 2;
    const float EnemyTargetAssignmentPenalty = 80f, EnemyTargetCurrentBonus = 50f;
    const int CompanionMeleePressureCap = 2, CompanionRangedPressureCap = 3, CompanionFocusPressureBonus = 1;
    const int EnemyMeleePressureCap = 3, EnemyRangedPressureCap = 2;
    const float TargetPressureOverCapPenalty = 260f;
    const int TargetGraphMaxPreferredDepth = 2;
    const float TargetGraphChainDepthPenalty = 75f, TargetGraphMutualPairBonus = 45f, TargetGraphPeelForPlayerBonus = 55f, TargetGraphBreakLongChainBonus = 30f;
    const float TargetSelectInterval = 0.25f;
    const float LordGuardRange = 10f, SeizerTargetBonus = 400f, NearBodyTargetBonus = 200f, NearBodyTargetReach = 12f;
    const float RaiserDamageScale = 4f;

    // --- probabilistic candidates ---
    const float SoftmaxTemperature = 50f;
    // Weighted (the first, untuned candidate). The tuned values it led to are AggroModel's constants, used by Production.
    const float NewAttackerWindow = 5f;
    readonly float DwellMin = 3f, DwellMax = 6f, MinHoldForHitRedraw = 1.5f;
    const float ThreatFloor = 0.25f; readonly float ThreatGain = 1.5f;   // w_threat = floor + gain * share^2
    readonly float CrowdK = 3f;                                          // w_crowd = 1 / (1 + (n/K)^2)
    const float DistanceScale = 10f;                                     // w_dist = 1 / (1 + max(0, d - reach)/scale)
    const float FinishGain = 0.5f, MutualGain = 1.5f; readonly float StickyGain = 2f;
    const float FocusGain = 2f, AttackingPlayerGain = 1.5f; readonly float GuardSeizerGain = 3f, TaskGain = 12f;
    bool Probabilistic => model is Model.Softmax or Model.Weighted or Model.Production;

    readonly Model model;
    readonly Scenario scenario;
    readonly Actor[] a;
    readonly int n, player;
    int prayer = -1;
    readonly float bodyX = 0f, bodyZ = -4f;
    readonly float[] threat; // threat[victim * n + source]
    readonly float decay = MathF.Exp(-Dt / EnemyMovementMath.ThreatDecayTau);
    float t;
    int lastFocus = -1, lastHolder = -1;

    // metrics
    double maxAttackers, samples, samplesOver6, atkSum, atkCount, switches, aliveFrames, idleFrames, blockedFrames;
    double enemyDmg, enemyDmgOnTanks, seizerFrames, seizerOnHolderFrames, holderSamples, holderAtkSum, seizerPrayFrames, seizerOnPrayFrames;

    public Sim(Model model, Scenario scenario, int seed, SeizeCase? seizeCase = null)
    {
        this.model = model;
        this.scenario = scenario;
        // The tuning that led to the production constants (the old WeightedCalm model, now AggroModel): dwell 4-8 s,
        // hit hold 2.5 s, sticky x6, threat gain 1.5, crowd K 4, task x100 (sweep: 25->46%, 50->59%, 100->73%, 200->80%
        // seizers on the prayer), guards on seizers x30 (sweep over 100 seeds, guarding win rate: x3 29%, x10 42%,
        // x30 45%, x100 50%; the fixed choice it replaced won 39%).
        // Tuning hook: TS_STICKY=4 TS_TASK=25 ... override the parameters of Softmax and Weighted (not Production,
        // which is the plugin's code as it is).
        static float Env(string name, float fallback)
            => float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        DwellMin = Env("TS_DWELLMIN", DwellMin); DwellMax = Env("TS_DWELLMAX", DwellMax); MinHoldForHitRedraw = Env("TS_HITHOLD", MinHoldForHitRedraw);
        StickyGain = Env("TS_STICKY", StickyGain); ThreatGain = Env("TS_THREAT", ThreatGain); CrowdK = Env("TS_CROWDK", CrowdK);
        TaskGain = Env("TS_TASK", TaskGain); GuardSeizerGain = Env("TS_GUARD", GuardSeizerGain);
        var rng = new Random(seed);
        var list = new List<Actor>();
        void Add(Side side, Role role, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var act = new Actor { Id = list.Count, Side = side, Role = role };
                (act.MaxHp, act.Damage, act.Reach) = role switch
                {
                    Role.Tank => (9000f, 140f, 3f),
                    Role.Ranged => (5000f, 200f, 15f),
                    Role.Prayer => (6000f, 0f, 0f),
                    _ => (6000f, 260f, 3f),
                };
                act.Hp = act.MaxHp;
                var line = side == Side.Ally ? 0f : 30f;
                act.X = (float)(rng.NextDouble() * 30 - 15);
                act.Z = line + (role == Role.Ranged ? (side == Side.Ally ? -6f : 6f) : 0f) + (float)(rng.NextDouble() * 4 - 2);
                act.AtkTimer = (float)rng.NextDouble() * AttackInterval;
                list.Add(act);
            }
        }
        if (scenario == Scenario.Seize)
        {
            // The lord has just fallen: the retainers stand around the body (2-6 m), one kneels at its post before
            // it, the enemy is 7-13 m off on one side (ranged 5 m further back).
            var c = seizeCase!;
            void Place(Side side, Role role, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    Add(side, role, 1);
                    var act = list[^1];
                    if (side == Side.Ally)
                    {
                        var angle = (float)(rng.NextDouble() * MathF.Tau);
                        var r = 2f + (float)rng.NextDouble() * 4f;
                        (act.X, act.Z) = (bodyX + MathF.Sin(angle) * r, bodyZ + MathF.Cos(angle) * r);
                    }
                    else
                    {
                        act.X = bodyX + (float)(rng.NextDouble() * 16 - 8);
                        act.Z = bodyZ + 7f + (float)rng.NextDouble() * 6f + (role == Role.Ranged ? 5f : 0f);
                    }
                }
            }
            Place(Side.Ally, Role.Tank, c.AllyTank);
            Place(Side.Ally, Role.Melee, c.AllyMelee);
            Place(Side.Ally, Role.Ranged, c.AllyRanged);
            Add(Side.Ally, Role.Player, 1);
            list[^1].Alive = false;
            list[^1].Hp = 0f;
            list[^1].DeathTime = 0f;
            list[^1].X = bodyX;
            list[^1].Z = bodyZ;
            if (c.Retainers > 0)
            {
                Add(Side.Ally, Role.Prayer, 1);
                (list[^1].X, list[^1].Z, list[^1].AtPost) = (PostX, PostZ, true);
            }
            Place(Side.Enemy, Role.Tank, c.EnemyTank);
            Place(Side.Enemy, Role.Melee, c.EnemyMelee);
            Place(Side.Enemy, Role.Ranged, c.EnemyRanged);
        }
        else
        {
            Add(Side.Ally, Role.Tank, 3);
            Add(Side.Ally, Role.Melee, 10);
            Add(Side.Ally, Role.Ranged, 7);
            Add(Side.Ally, Role.Player, 1);
            if (scenario == Scenario.Protect) Add(Side.Ally, Role.Prayer, 1);
            Add(Side.Enemy, Role.Tank, 2);
            Add(Side.Enemy, Role.Melee, 12);
            Add(Side.Enemy, Role.Ranged, 6);
        }
        a = list.ToArray();
        n = a.Length;
        player = Array.FindIndex(a, x => x.Role == Role.Player);
        if (scenario == Scenario.Protect)
        {
            prayer = Array.FindIndex(a, x => x.Role == Role.Prayer);
            a[prayer].X = bodyX;
            a[prayer].Z = bodyZ;
            var seizers = 0;
            foreach (var e in a)
                if (e.Side == Side.Enemy && e.Role == Role.Melee && seizers < 8) { e.Seizer = true; seizers++; }
        }
        if (scenario == Scenario.Seize)
        {
            prayer = Array.FindIndex(a, x => x.Role == Role.Prayer);
            hadRetainers = seizeCase!.Retainers > 0;
            seizeWorld = new SeizeWorld(this);
            var rules = new BattleRules(new SeizeRules(), () => true, () => false);
            seizure = new CorpseSeizure<Actor>(rules, seizeWorld, seizeWorld, LordContestRadius);
        }
        threat = new float[n * n];
        foreach (var x in a)
        {
            x.LastHitBy = Enumerable.Repeat(-100f, n).ToArray();
            // Per-actor stream so draws do not depend on iteration order. Not HashCode.Combine: it is
            // salted per process, which made runs irreproducible.
            x.Rng = new Random((int)Mix((uint)seed * 0x9E3779B9u ^ (uint)x.Id));
            x.Aggro = new AggroState { Rng = x.Rng };
        }
    }

    static uint Mix(uint x) // same finalizer as CombatCompanionManager.StableUnit
    {
        x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
        return x;
    }

    float Dist(Actor p, Actor q) => MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z));
    float DistToBody(Actor p) => MathF.Sqrt((p.X - bodyX) * (p.X - bodyX) + (p.Z - bodyZ) * (p.Z - bodyZ));
    bool Live(int id) => id >= 0 && a[id].Alive;
    bool SideAlive(Side side) => a.Any(x => x.Alive && x.Side == side);

    /// <summary>LordSeizerTarget (Lord.cs:210): the prayer while it lives, else the retainer nearest the body.</summary>
    int Holder()
    {
        if (scenario == Scenario.Seize)
        {
            // CorpseSeizureRule.HolderFor: the one praying, else the retainer nearest the body, within 3.5 + 2 m of it.
            const float atBody = LordContestRadius + 2f;
            if (Live(prayer) && DistToBody(a[prayer]) <= atBody) return prayer;
            int near = -1; var nd = atBody;
            foreach (var c in a)
                if (c.Alive && c.IsCompanion && DistToBody(c) < nd) { nd = DistToBody(c); near = c.Id; }
            return near;
        }
        if (scenario != Scenario.Protect) return -1;
        if (Live(prayer)) return prayer;
        int best = -1; var bd = float.MaxValue;
        foreach (var c in a)
            if (c.Alive && c.IsCompanion && DistToBody(c) < bd) { bd = DistToBody(c); best = c.Id; }
        return best;
    }

    public RunResult Run()
    {
        var frame = 0;
        while (t < MaxTime && SideAlive(Side.Ally) && SideAlive(Side.Enemy))
        {
            for (var i = 0; i < threat.Length; i++) threat[i] *= decay;   // DecayThreat
            UpdatePlayer();
            SelectTargets();
            Move();
            Attack();
            if (frame % 15 == 0) Sample();
            t += Dt;
            frame++;
        }

        var alliesWon = SideAlive(Side.Ally) && !SideAlive(Side.Enemy);
        var end = t;
        double starvedT = 0, starvedD = 0;
        foreach (var x in a)
        {
            var lived = x.Alive ? end : x.DeathTime;
            if (lived < 30f) continue;
            if (!x.EverTargeted) starvedT++;
            if (x.Side == Side.Enemy && !x.EverDamaged) starvedD++;
        }
        return new RunResult(
            maxAttackers, samplesOver6 / Math.Max(1, samples), atkSum / Math.Max(1, atkCount),
            switches / Math.Max(1e-6, aliveFrames * Dt / 60.0), idleFrames / Math.Max(1, aliveFrames), blockedFrames / Math.Max(1, aliveFrames),
            alliesWon, alliesWon ? end : double.NaN, enemyDmgOnTanks / Math.Max(1, enemyDmg), starvedT, starvedD,
            prayer < 0 ? double.NaN : (a[prayer].Alive ? end : a[prayer].DeathTime), prayer >= 0 && a[prayer].Alive,
            seizerOnHolderFrames / Math.Max(1, seizerFrames), holderAtkSum / Math.Max(1, holderSamples), seizerOnPrayFrames / Math.Max(1, seizerPrayFrames));
    }

    // ------------------------------------------------------------------ the corpse seizure (Scenario.Seize)

    /// <summary>CombatCompanionManager.LordContestRadius.</summary>
    public const float LordContestRadius = 3.5f;
    /// <summary>The lord's ragdoll starts this long after the fall (the body can be taken in hand from then).</summary>
    public const float SeizeRagdollUpSeconds = 2f;
    // LordGuardRule: the one praying kneels 1.3 m before the body; it is at its post within 0.6 m of it.
    float PostX => bodyX;
    float PostZ => bodyZ + 1.3f;

    readonly CorpseSeizure<Actor>? seizure;
    readonly SeizeWorld? seizeWorld;
    readonly bool hadRetainers;
    /// <summary>CombatEngine.TriggerEnemyVictoryIfPartyDefeated: the lord dead and no retainer left (ANNIHILATED); the
    /// outcome is decided and the seizure stops. ("undefended" has no retainers at all: read as retainers far off.)</summary>
    bool Annihilated => hadRetainers && !a.Any(x => x.Alive && x.IsCompanion);
    bool seized;
    float seizedAt = float.NaN;
    double seizeFrames, guardsAtBodySum, seizersAtBodySum, outnumberedFrames, guardFrames, guardOnSeizerFrames, hitsAtBody;

    /// <summary>
    /// The lord down: allies look then enemies (production weights, the seizure's seizers and holder), everyone moves
    /// (seizers as NpcAiController.TryCrowdMove sends them), attacks land, then the war tick (the production
    /// CorpseSeizure). Ends when the body is carried off, every enemy is slain, or at the cap.
    /// </summary>
    /// <summary>TS_NO_SEIZE=1: nobody goes for the body (the fight alone, for comparison).</summary>
    static readonly bool Trace = Environment.GetEnvironmentVariable("TS_TRACE") == "1";
    static readonly bool NoSeize = Environment.GetEnvironmentVariable("TS_NO_SEIZE") == "1";

    public SeizeResult RunSeize()
    {
        while (t < MaxTime && !seized && !Annihilated && SideAlive(Side.Enemy))
        {
            for (var i = 0; i < threat.Length; i++) threat[i] *= decay;
            ReplacePrayer();
            SelectTargets();
            Move();
            Attack();
            if (!NoSeize) seizure!.Tick(Dt);
            SampleSeize();
            if (Trace && ((int)MathF.Round(t / Dt)) % 30 == 0)
                Console.WriteLine($"t {t:0} allies {a.Count(x => x.Alive && x.IsCompanion)} enemies {a.Count(x => x.Alive && x.Side == Side.Enemy)} seizers {a.Count(x => x.Alive && x.SeizeGoal != null)} at {a.Count(x => x.Alive && x.SeizeGoal != null && GapToBody(x) <= LordContestRadius)} held {a.Any(x => x.Alive && x.SeizeHeld)} guards@ {RetainersWithin(LordContestRadius)} progress {seizure!.Progress:0.00} prayer {(Live(prayer) ? DistToBody(a[prayer]).ToString("0.0") : "-")}");
            t += Dt;
        }
        return new SeizeResult(seized, seizedAt, !seized && !SideAlive(Side.Enemy), !seized && Annihilated, t,
            guardsAtBodySum / Math.Max(1, seizeFrames), seizersAtBodySum / Math.Max(1, seizeFrames),
            outnumberedFrames / Math.Max(1, seizeFrames), guardOnSeizerFrames / Math.Max(1, guardFrames), hitsAtBody);
    }

    /// <summary>LordGuardRule.TickBeforeAllies: the one praying fell, the retainer nearest the post goes to pray.</summary>
    void ReplacePrayer()
    {
        if (prayer < 0 || a[prayer].Alive) return;
        int best = -1; var bd = float.MaxValue;
        foreach (var c in a)
        {
            if (!c.Alive || !c.IsCompanion) continue;
            var d = MathF.Sqrt((c.X - PostX) * (c.X - PostX) + (c.Z - PostZ) * (c.Z - PostZ));
            if (d < bd) { bd = d; best = c.Id; }
        }
        if (best < 0) return;
        prayer = best;
        a[best].Role = Role.Prayer;
        a[best].AtPost = false;
        SetTarget(a[best], -1);
    }

    float GapToBody(Actor x) => DistToBody(x) - 0.5f;

    int RetainersWithin(float radius)
    {
        var count = 0;
        foreach (var c in a)
            if (c.Alive && c.IsCompanion && DistToBody(c) <= radius) count++;
        return count;
    }

    void SampleSeize()
    {
        foreach (var c in a)
        {
            if (!c.Alive || !c.IsCompanion || !c.Fights) continue;
            guardFrames++;
            if (Live(c.Target) && a[c.Target].Seizer) guardOnSeizerFrames++;
        }
        var seizersAt = 0;
        var any = false;
        foreach (var e in a)
        {
            if (!e.Alive || e.SeizeGoal == null) continue;
            any = true;
            if (GapToBody(e) <= LordContestRadius) seizersAt++;
        }
        if (!any) return;
        seizeFrames++;
        var guards = RetainersWithin(LordContestRadius);
        guardsAtBodySum += guards;
        seizersAtBodySum += seizersAt;
        if (seizersAt > guards) outnumberedFrames++;
    }

    sealed class SeizeRules : IBattleRuleSettings
    {
        public WarFallenRule FallenRule => WarFallenRule.WarringStates;
        public bool Hostages => false;
        public bool Retreat => true;
        public uint CaptiveEmoteId => 0;
        public bool ShowResultBanner => true;
        public bool EnableRagdoll => true;
    }

    /// <summary>The plugin's BattleModule.CorpseSeizureWorld over this 2D world. No grab (nothing to hold).</summary>
    sealed class SeizeWorld : ICorpseSeizureWorld<Actor>, ICorpseGrab<Actor>
    {
        readonly Sim sim;
        readonly List<Actor> enemies;
        public SeizeWorld(Sim sim)
        {
            this.sim = sim;
            enemies = sim.a.Where(x => x.Side == Side.Enemy).ToList();
        }
        public bool OutcomeDecided => sim.seized || sim.Annihilated;
        public Vector3? LordBodyLocation() => new Vector3(sim.bodyX, 0f, sim.bodyZ);
        public IReadOnlyList<Actor> Enemies => enemies;
        public bool IsPresent(Actor npc) => npc.Alive;
        public bool IsCaptive(Actor npc) => false;
        public bool IsIdleSentry(Actor npc) => false;
        public Vector3 PositionOf(Actor npc) => new(npc.X, 0f, npc.Z);
        public float HitboxRadiusOf(Actor npc) => 0.5f;
        public string NameOf(Actor npc) => $"E{npc.Id}";
        public uint IdOf(Actor npc) => Uid(npc.Id);
        public void SetSeizeGoal(Actor npc, Vector3? goal) => npc.SeizeGoal = goal;
        public void SetSeizeHeld(Actor npc, bool held) => npc.SeizeHeld = held;
        public void Engage(Actor npc) { }
        public void ClearSeizerIds() { foreach (var e in enemies) e.Seizer = false; }
        public void AddSeizerId(uint id) => sim.a[(int)id - 1].Seizer = true;
        public int RetainersAtBody(Vector3 body, float radius, out string? nearestGuard, out float nearestDistance)
        {
            nearestGuard = null;
            nearestDistance = float.MaxValue;
            foreach (var c in sim.a)
                if (c.Alive && c.IsCompanion && sim.DistToBody(c) < nearestDistance)
                {
                    nearestDistance = sim.DistToBody(c);
                    nearestGuard = $"A{c.Id}";
                }
            return sim.RetainersWithin(radius);
        }
        public bool BodyReady => sim.t >= SeizeRagdollUpSeconds;
        public void SetCorpseCaptured(bool captured) { }
        public void SurrenderRetainers() { }
        public void ForceEnemyVictory()
        {
            sim.seized = true;
            sim.seizedAt = sim.t;
        }
        public void BattleLog(string text) { }
        public void Info(string text) { }
        public void Warning(string text) { }
        public bool BodyCanBeHeld => false;
        public bool IsControlling => false;
        public void TakeControl(Actor npc) { }
        public void Grab() { }
        public bool IsHolding => false;
    }

    void UpdatePlayer()
    {
        var p = a[player];
        if (!p.Alive || Live(p.Target)) return;
        int best = -1; var bd = float.MaxValue;
        foreach (var e in a)
        {
            if (!e.Alive || e.Side != Side.Enemy) continue;
            var d = scenario == Scenario.Protect ? DistToBody(e) : Dist(p, e);
            if (d < bd) { bd = d; best = e.Id; }
        }
        SetTarget(p, best);
    }

    void SetTarget(Actor x, int target)
    {
        if (target == x.Target) return;
        if (Live(x.Target) && target >= 0) switches++;
        x.Target = target;
        x.HeldSince = t;
        if (target >= 0) a[target].EverTargeted = true;
    }

    IEnumerable<Actor> Candidates(Actor x)
    {
        foreach (var c in a)
        {
            if (!c.Alive || c.Side == x.Side) continue;
            // Guarding the lord, allies only fight inside the guard range (AssignCompanionTarget: !IsWithinCommandRange clears the target).
            if (scenario != Scenario.Open && x.Side == Side.Ally && DistToBody(c) > LordGuardRange + x.Reach) continue;
            yield return c;
        }
    }

    // ------------------------------------------------------------------ selection

    void SelectTargets()
    {
        var holder = Holder();
        var holderChanged = holder != lastHolder;
        lastHolder = holder;
        var focus = a[player].Alive ? a[player].Target : -1;
        var focusChanged = focus != lastFocus;
        lastFocus = focus;

        if (model is Model.Current or Model.CurrentNoOverride)
        {
            // Tick (CombatCompanionManager.cs:322): companions first (throttled), then every enemy every frame.
            var assigned = new int[n];
            var cPressure = new int[n * 2];
            foreach (var c in a)
            {
                if (!c.Alive || !c.IsCompanion || !c.Fights) continue;
                if (!Live(c.Target) || t >= c.NextSelectAt)
                {
                    var target = SelectCompanionCurrent(c, assigned, cPressure);
                    SetTarget(c, target);
                    if (target >= 0)
                    {
                        assigned[target]++; // AssignCompanionTarget increments again after SelectCompanionTarget did (cs:1274 and cs:1401)
                        cPressure[target * 2 + (c.IsRanged ? 1 : 0)]++;
                    }
                    c.NextSelectAt = t + TargetSelectInterval + (c.Id % 8) * 0.02f;
                }
                else
                {
                    assigned[c.Target]++;
                    cPressure[c.Target * 2 + (c.IsRanged ? 1 : 0)]++;
                }
            }
            var ePressure = new int[n * 2];
            foreach (var e in a)
            {
                if (!e.Alive || e.Side != Side.Enemy) continue;
                SetTarget(e, SelectEnemyCurrent(e, ePressure, holder));
                if (e.Target >= 0) ePressure[e.Target * 2 + (e.IsRanged ? 1 : 0)]++;
            }
            return;
        }

        if (model == Model.Production)
        {
            SelectProduction(holder, focus);
            return;
        }

        // Probabilistic: every actor (both sides) re-draws only on events.
        var counts = BuildCounts();
        foreach (var x in a)
        {
            if (!x.Alive || !x.Fights || x.Role == Role.Player) continue;
            var due = !Live(x.Target) || t >= x.DwellUntil
                      || (x.HitByNew && t - x.HeldSince >= MinHoldForHitRedraw)
                      || (x.Side == Side.Ally && focusChanged)
                      || (x.Seizer && holderChanged);
            // A target that left the guard range is dropped at once (same as the deterministic path).
            if (!due && scenario == Scenario.Protect && x.Side == Side.Ally && DistToBody(a[x.Target]) > LordGuardRange + x.Reach)
                due = true;
            x.HitByNew = false;
            if (!due) continue;
            var target = model == Model.Softmax ? DrawSoftmax(x, counts, holder) : DrawWeighted(x, counts, holder);
            SetTarget(x, target);
            x.DwellUntil = t + DwellMin + (float)x.Rng.NextDouble() * (DwellMax - DwellMin);
            counts = BuildCounts();
        }
    }

    /// <summary>[target*2 + ranged] = how many live actors (any side) target it.</summary>
    int[] BuildCounts()
    {
        var counts = new int[n * 2];
        foreach (var x in a)
            if (x.Alive && x.Fights && x.Target >= 0)
                counts[x.Target * 2 + (x.IsRanged ? 1 : 0)]++;
        return counts;
    }

    // --- port of SelectEnemyTargetInternal (cs:483) / ScoreEnemyTargetCandidate (cs:564) ---
    int SelectEnemyCurrent(Actor e, int[] pressure, int holder)
    {
        var cands = Candidates(e).ToList();
        if (cands.Count == 0) return -1;
        if (model == Model.Current && e.Seizer && holder >= 0) return holder; // LordSeizerTarget override
        if (!Live(e.Target))
            return cands.OrderByDescending(c => ScoreEnemy(e, -1, c, pressure, holder)).ThenBy(c => TieBreak(e.Id, c.Id)).First().Id;
        var cur = a[e.Target];
        var best = cands.OrderByDescending(c => ScoreEnemy(e, cur.Id, c, pressure, holder)).ThenBy(c => TieBreak(e.Id, c.Id)).First();
        return best.Id != cur.Id && ScoreEnemy(e, cur.Id, best, pressure, holder) > ScoreEnemy(e, cur.Id, cur, pressure, holder) + ThreatSwitchThreshold
            ? best.Id : cur.Id;
    }

    float ScoreEnemy(Actor e, int currentId, Actor c, int[]? pressure, int holder)
    {
        var assigned = 0;
        foreach (var o in a)
            if (o.Alive && o.Side == Side.Enemy && o.Id != e.Id && o.Target == c.Id) assigned++;
        var score = ThreatScore(e.Id, c.Id);
        score -= assigned * EnemyTargetAssignmentPenalty;
        if (c.Id == currentId) score += EnemyTargetCurrentBonus;
        if (pressure != null) score -= PressurePenalty(pressure, c.Id, e.IsRanged, EnemyMeleePressureCap, EnemyRangedPressureCap, 0, e);
        score += GraphScore(e.Id, c.Id);
        // Pre-override behaviour of seizers (what the in-game bug report showed): a bonus, not a hard target.
        if (e.Seizer && c.Id == holder) score += SeizerTargetBonus;
        return score;
    }

    float ThreatScore(int self, int cand)
    {
        var max = 0f;
        for (var k = 0; k < n; k++) max = MathF.Max(max, threat[self * n + k]);
        return EnemyMovementMath.ThreatShare(threat[self * n + cand], max) * ThreatScoreWeight;
    }

    // pressure == null-safe: when "self" is given its own registration is excluded (probabilistic path uses a full map).
    float PressurePenalty(int[] map, int target, bool ranged, int meleeCap, int rangedCap, int extraCap, Actor self)
    {
        var cap = (ranged ? rangedCap : meleeCap) + extraCap;
        var count = map[target * 2 + (ranged ? 1 : 0)];
        if (self.Target == target && Probabilistic) count--;
        return count >= cap ? (count - cap + 1) * TargetPressureOverCapPenalty : 0f;
    }

    static uint TieBreak(int enemyId, int targetId) => ((uint)enemyId * 1103515245u + (uint)targetId * 2654435761u) & 0xFFFFu;

    // --- target graph (cs:621-677) ---
    int GraphTarget(int id, int overrideActor = -1, int overrideTarget = -1)
    {
        if (id == overrideActor) return overrideTarget;
        if (id < 0 || !a[id].Alive || id == player) return -1; // the player is in neither map
        return a[id].Target;
    }

    int ChainDepth(int from, int to)
    {
        var depth = 1;
        Span<bool> seen = stackalloc bool[n];
        seen[from] = true;
        var current = to;
        while (current >= 0 && !seen[current])
        {
            seen[current] = true;
            var next = GraphTarget(current, from, to);
            if (next < 0 || next == player) break;
            depth++;
            current = next;
        }
        return depth;
    }

    float GraphScore(int actor, int cand)
    {
        var score = 0f;
        var depth = ChainDepth(actor, cand);
        if (depth > TargetGraphMaxPreferredDepth) score -= (depth - TargetGraphMaxPreferredDepth) * TargetGraphChainDepthPenalty;
        if (GraphTarget(cand) == actor) score += TargetGraphMutualPairBonus;
        if (a[cand].Side == Side.Enemy && GraphTarget(cand) == player) score += TargetGraphPeelForPlayerBonus;
        var current = GraphTarget(actor);
        if (current >= 0)
        {
            var cd = ChainDepth(actor, current);
            if (cd > depth) score += Math.Min(2, cd - depth) * TargetGraphBreakLongChainBonus;
        }
        return score;
    }

    // --- port of SelectCompanionTarget (cs:1365) / ScoreCompanionTarget (cs:1406) ---
    int SelectCompanionCurrent(Actor c, int[] assigned, int[] pressure)
    {
        var cands = Candidates(c).ToList();
        if (cands.Count == 0) return -1;
        var scored = cands.Select(e => (e, s: ScoreCompanion(c, e, assigned, pressure))).OrderByDescending(x => x.s).ToList();
        var best = scored[0];
        if (Live(c.Target) && scored.Any(x => x.e.Id == c.Target))
        {
            var cur = scored.First(x => x.e.Id == c.Target);
            if (best.e.Id != cur.e.Id && best.s < cur.s + TargetSwitchThreshold) best = cur;
        }
        assigned[best.e.Id]++;
        return best.e.Id;
    }

    float ScoreCompanion(Actor c, Actor e, int[] assigned, int[] pressure)
    {
        var score = 0f;
        var assignedCount = assigned[e.Id];
        if (Probabilistic && c.Target == e.Id) assignedCount--; // full map includes self
        var isFocus = a[player].Alive && a[player].Target == e.Id;
        if (isFocus) score += SelectedTargetBonus;
        if (e.Target == player) score += AttackingPlayerBonus;
        if (c.Target == e.Id) score += PreviousTargetBonus;
        score += (1f - Math.Clamp(e.Hp / e.MaxHp, 0f, 1f)) * LowHpFinishBonus;
        var penalized = Math.Max(0, assignedCount - (isFocus ? MaxPlayerFocusAssistants : 0));
        score -= penalized * AssignmentPenalty;
        if (isFocus && assignedCount >= MaxPlayerFocusAssistants)
            score -= (assignedCount - MaxPlayerFocusAssistants + 1) * PlayerFocusOvercapPenalty;
        score -= PressurePenalty(pressure, e.Id, c.IsRanged, CompanionMeleePressureCap, CompanionRangedPressureCap, isFocus ? CompanionFocusPressureBonus : 0, c);
        score += GraphScore(c.Id, e.Id);
        score -= Dist(e, a[player]) * DistancePenaltyPerYalm;
        score += LordGuardScore(e);
        return score;
    }

    float LordGuardScore(Actor e)
    {
        if (scenario != Scenario.Protect) return 0f;
        var score = e.Seizer ? SeizerTargetBonus : 0f;
        return score + NearBodyTargetBonus * Math.Clamp(1f - DistToBody(e) / NearBodyTargetReach, 0f, 1f);
    }

    // --- candidate 1: softmax over the production scores, redrawn on events ---
    int DrawSoftmax(Actor x, int[] counts, int holder)
    {
        var cands = Candidates(x).ToList();
        if (cands.Count == 0) return -1;
        float[] s;
        if (x.Side == Side.Enemy)
            s = cands.Select(c => ScoreEnemy(x, x.Target, c, counts, holder)).ToArray();
        else
        {
            var assigned = new int[n];
            foreach (var o in a) if (o.Alive && o.IsCompanion && o.Fights && o.Target >= 0) assigned[o.Target]++;
            s = cands.Select(e => ScoreCompanion(x, e, assigned, counts)).ToArray();
        }
        var max = s.Max();
        var w = s.Select(v => MathF.Exp((v - max) / SoftmaxTemperature)).ToArray();
        return cands[Roulette(x.Rng, w)].Id;
    }

    // --- candidate 2: one multiplicative weight for both sides ---
    int DrawWeighted(Actor x, int[] counts, int holder)
    {
        var cands = Candidates(x).ToList();
        if (cands.Count == 0) return -1;
        var w = new float[cands.Count];
        for (var i = 0; i < cands.Count; i++) w[i] = Weight(x, cands[i], counts, holder);
        return cands[Roulette(x.Rng, w)].Id;
    }

    float Weight(Actor x, Actor c, int[] counts, int holder)
    {
        var max = 0f;
        for (var k = 0; k < n; k++) max = MathF.Max(max, threat[x.Id * n + k]);
        var share = EnemyMovementMath.ThreatShare(threat[x.Id * n + c.Id], max);
        var w = ThreatFloor + ThreatGain * share * share;

        // Crowding: others of my style on it count fully, the other style half. Never reaches zero.
        var same = counts[c.Id * 2 + (x.IsRanged ? 1 : 0)] - (x.Target == c.Id ? 1 : 0);
        var other = counts[c.Id * 2 + (x.IsRanged ? 0 : 1)];
        var crowd = same + 0.5f * other;
        w /= 1f + crowd * crowd / (CrowdK * CrowdK);

        w /= 1f + MathF.Max(0f, Dist(x, c) - x.Reach) / DistanceScale;
        w *= 1f + FinishGain * (1f - c.Hp / c.MaxHp);
        if (c.Target == x.Id) w *= MutualGain;
        if (c.Id == x.Target) w *= StickyGain;

        // Situation (the same kind of multiplier on both sides).
        if (x.Seizer && c.Id == holder) w *= TaskGain;
        if (x.Side == Side.Ally)
        {
            if (a[player].Alive && a[player].Target == c.Id) w *= FocusGain;
            if (c.Target == player) w *= AttackingPlayerGain;
            if (scenario == Scenario.Protect)
            {
                if (c.Seizer) w *= GuardSeizerGain;
                w *= 1f + Math.Clamp(1f - DistToBody(c) / NearBodyTargetReach, 0f, 1f);
            }
        }
        return w;
    }

    // --- Production: CombatCompanionManager.Tick's two passes with the linked AggroModel ---
    static uint Uid(int id) => id < 0 ? 0u : (uint)id + 1;
    static Vector3 Pos(Actor x) => new(x.X, 0f, x.Z);

    /// <summary>
    /// CombatCompanionManager.Tick: allies first, each looking only when it has no live target or its
    /// 0.25 s (+ id stagger) is up (AssignCompanionTarget), then every enemy every frame
    /// (SelectEnemyTargetInternal -> DrawEnemyTarget).
    /// </summary>
    void SelectProduction(int holder, int focus)
    {
        foreach (var c in a)
        {
            if (!c.Alive || !c.IsCompanion || !c.Fights) continue;
            if (!Live(c.Target) || t >= c.NextSelectAt)
            {
                DrawProduction(c, holder, focus);
                c.NextSelectAt = t + 0.25f + (c.Id % 8) * 0.02f;
            }
        }
        foreach (var e in a)
            if (e.Alive && e.Side == Side.Enemy)
                DrawProduction(e, holder, focus);
    }

    /// <summary>DrawEnemyTarget / DrawCompanionTarget (CombatCompanionManager.Aggro.cs).</summary>
    void DrawProduction(Actor x, int holder, int focus)
    {
        var s = x.Aggro;
        var cands = Candidates(x).ToList();
        if (cands.Count == 0)
        {
            // No candidate: the enemy gets no target; the ally's target is cleared (SelectCompanionTarget -> null).
            SetTarget(x, -1);
            return;
        }
        var currentLive = Live(x.Target) && cands.Exists(c => c.Id == x.Target);
        bool due;
        // LordSeizerTarget: only those going for the body have a holder.
        var holderId = x.Side == Side.Enemy && x.Seizer ? Uid(holder) : 0u;
        var focusId = Live(focus) ? Uid(focus) : 0u;
        if (x.Side == Side.Enemy)
        {
            due = AggroModel.IsDue(s, t, currentLive, holderId != s.Holder);
            s.HitByNew = false;
            s.Holder = holderId;
        }
        else
        {
            due = AggroModel.IsDue(s, t, currentLive, focusId != s.Focus);
            s.HitByNew = false;
            s.Focus = focusId;
        }
        if (!due)
            return;

        var counts = ProductionCounts();
        var self = new AggroModel.Self(Uid(x.Id), Pos(x), x.Reach, x.IsRanged, currentLive ? Uid(x.Target) : 0u);
        var max = 0f;
        for (var k = 0; k < n; k++) max = MathF.Max(max, threat[x.Id * n + k]);
        var weights = new List<(uint Id, float Weight)>(cands.Count);
        foreach (var c in cands)
        {
            var share = EnemyMovementMath.ThreatShare(threat[x.Id * n + c.Id], max);
            var cand = new AggroModel.Candidate(Uid(c.Id), Pos(c), c.Hp / c.MaxHp, Uid(c.Target));
            var w = AggroModel.Weight(self, cand, share, counts[c.Id * 2], counts[c.Id * 2 + 1]);
            if (x.Side == Side.Enemy)
            {
                if (Uid(c.Id) == holderId) w *= AggroModel.TaskGain;
                if (IsExposed(c.Id)) w *= AggroModel.ExposedGain;
            }
            else
            {
                if (focusId != 0 && Uid(c.Id) == focusId) w *= AggroModel.FocusGain;
                if (c.Target == player && a[player].Alive) w *= AggroModel.AttackingPlayerGain;
                if (IsExposed(c.Target)) w *= AggroModel.ExposedGain;
                if (scenario != Scenario.Open)
                {
                    if (c.Seizer) w *= scenario == Scenario.Seize ? SeizeGuardSeizerGain : AggroModel.GuardSeizerGain;
                    w *= 1f + Math.Clamp(1f - DistToBody(c) / NearBodyTargetReach, 0f, 1f);
                    // (Out of a guard's reach: left out of the candidates here, x0.05 in the plugin.)
                }
            }
            weights.Add((Uid(c.Id), w));
        }
        var drawn = AggroModel.Draw(s, weights);
        AggroModel.Hold(s, drawn, t);
        SetTarget(x, (int)drawn - 1);
    }

    /// <summary>IsExposed: the one praying over the body (always at its post here); nobody raises in this model.</summary>
    bool IsExposed(int id) => id >= 0 && id == prayer && Live(prayer) && (scenario != Scenario.Seize || a[prayer].AtPost);

    /// <summary>The guards' weight on a seizer in the Seize scenario: production's, or TS_SEIZE_GUARD for a sweep.</summary>
    static readonly float SeizeGuardSeizerGain =
        float.TryParse(Environment.GetEnvironmentVariable("TS_SEIZE_GUARD"), System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : AggroModel.GuardSeizerGain;

    /// <summary>CountAggro: [target*2 + ranged] over living enemies and companions with a target (the player is not counted).</summary>
    int[] ProductionCounts()
    {
        var counts = new int[n * 2];
        foreach (var x in a)
            if (x.Alive && x.Fights && x.Role != Role.Player && x.Target >= 0)
                counts[x.Target * 2 + (x.IsRanged ? 1 : 0)]++;
        return counts;
    }

    static int Roulette(Random rng, float[] w)
    {
        var total = 0.0;
        foreach (var v in w) total += v;
        var r = rng.NextDouble() * total;
        for (var i = 0; i < w.Length; i++)
        {
            r -= w[i];
            if (r <= 0) return i;
        }
        return w.Length - 1;
    }

    // ------------------------------------------------------------------ movement / damage

    void Move()
    {
        foreach (var x in a)
        {
            if (!x.Alive) continue;
            float gx, gz, stop;
            if (x.Role == Role.Prayer)
            {
                // Seize: a new one praying walks to its post before the body and kneels there.
                if (scenario != Scenario.Seize || x.AtPost) continue;
                (gx, gz, stop) = (PostX, PostZ, 0f);
                var pd = MathF.Sqrt((gx - x.X) * (gx - x.X) + (gz - x.Z) * (gz - x.Z));
                if (pd <= 0.6f) { x.AtPost = true; continue; }
            }
            else if (x.SeizeGoal is { } goal)
            {
                // NpcAiController.TryCrowdMove: held off, it fights a guard in its reach where it stands; otherwise
                // straight on to the body (to 0.8 m of it: bodies do not stack in the crowd).
                x.SeizeInReach = x.SeizeHeld && Live(x.Target) && Dist(x, a[x.Target]) <= x.Reach;
                if (x.SeizeInReach) continue;
                (gx, gz, stop) = (goal.X, goal.Z, 0.8f);
            }
            else if (Live(x.Target)) { gx = a[x.Target].X; gz = a[x.Target].Z; stop = x.Reach - 0.3f; }
            else if (scenario != Scenario.Open && x.Side == Side.Ally) { gx = bodyX; gz = bodyZ; stop = 3f; }
            else continue;
            var dx = gx - x.X; var dz = gz - x.Z;
            var d = MathF.Sqrt(dx * dx + dz * dz);
            if (d <= stop) continue;
            var step = MathF.Min(Speed * Dt, d - stop);
            x.X += dx / d * step;
            x.Z += dz / d * step;
        }
    }

    void Attack()
    {
        // Melee slots: only the MeleeSlots nearest melee in reach of a target can swing (crowd ring).
        var slotOk = new bool[n];
        var byTarget = new List<Actor>[n];
        foreach (var x in a)
        {
            if (!x.Alive || !x.Fights || !Live(x.Target) || (x.SeizeGoal != null && !x.SeizeInReach)) continue;
            if (x.IsRanged) { slotOk[x.Id] = true; continue; }
            if (Dist(x, a[x.Target]) > x.Reach) continue;
            (byTarget[x.Target] ??= new()).Add(x);
        }
        for (var i = 0; i < n; i++)
        {
            if (byTarget[i] == null) continue;
            var tgt = a[i];
            foreach (var m in byTarget[i].OrderBy(m => Dist(m, tgt)).Take(MeleeSlots)) slotOk[m.Id] = true;
        }

        var dying = new List<Actor>();
        foreach (var x in a)
        {
            if (!x.Alive || !x.Fights) continue;
            aliveFrames++;
            // One going for the body swings only at the guard holding it off (NpcAiController.TryCrowdMove).
            var inReach = Live(x.Target) && Dist(x, a[x.Target]) <= x.Reach && (x.SeizeGoal == null || x.SeizeInReach);
            if (!inReach || !slotOk[x.Id])
            {
                idleFrames++;
                if (inReach) blockedFrames++;
                continue;
            }
            x.AtkTimer -= Dt;
            if (x.AtkTimer > 0) continue;
            x.AtkTimer += AttackInterval;
            var v = a[x.Target];
            var dmg = x.Damage * (v.Role == Role.Prayer && (scenario != Scenario.Seize || v.AtPost) ? RaiserDamageScale : 1f);
            v.Hp -= dmg;
            v.EverDamaged = true;
            threat[v.Id * n + x.Id] += EnemyMovementMath.HitThreat((int)dmg, x.IsTank);
            if (model == Model.Production)
                AggroModel.NoteHit(v.Aggro, Uid(x.Id), t);
            else
            {
                if (t - v.LastHitBy[x.Id] > NewAttackerWindow && v.Target != x.Id) v.HitByNew = true;
                v.LastHitBy[x.Id] = t;
            }
            if (x.Side == Side.Enemy)
            {
                enemyDmg += dmg;
                if (v.IsTank) enemyDmgOnTanks += dmg;
            }
            if (scenario == Scenario.Seize && x.Side == Side.Ally && v.SeizeGoal != null && GapToBody(v) <= LordContestRadius) hitsAtBody++;
            // BattleModule.NoteDamage: every hit that lands is put to the seizure (it knocks the seizing back on a seizer at the body).
            if (x.Side == Side.Ally) seizure?.NoteHit(Uid(v.Id));
            if (v.Hp <= 0 && v.Alive) dying.Add(v);
        }
        foreach (var v in dying)
        {
            v.Alive = false;
            v.DeathTime = t;
        }
    }

    void Sample()
    {
        samples++;
        var counts = new int[n];
        foreach (var x in a)
            if (x.Alive && x.Fights && Live(x.Target)) counts[x.Target]++;
        var max = 0;
        for (var i = 0; i < n; i++)
        {
            if (counts[i] == 0) continue;
            max = Math.Max(max, counts[i]);
            atkSum += counts[i];
            atkCount++;
        }
        maxAttackers = Math.Max(maxAttackers, max);
        if (max > 6) samplesOver6++;

        var holder = Holder();
        if (holder >= 0) { holderSamples++; holderAtkSum += counts[holder]; }
        foreach (var x in a)
        {
            if (!x.Alive || !x.Seizer) continue;
            seizerFrames++;
            if (x.Target == holder) seizerOnHolderFrames++;
            if (Live(prayer)) { seizerPrayFrames++; if (x.Target == prayer) seizerOnPrayFrames++; }
        }
    }
}
