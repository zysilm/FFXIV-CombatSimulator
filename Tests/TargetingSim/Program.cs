using CombatSimulator.Simulation;

// Offline A/B of target selection for mass battles (20 allies + player vs 20 enemies).
// Ports the scoring of CombatCompanionManager (enemy: ScoreEnemyTargetCandidate / SelectEnemyTargetInternal,
// ally: ScoreCompanionTarget / SelectCompanionTarget, LordGuardScore / LordSeizerTarget) onto a 2D
// point model with walk speed, reach, melee slots and auto-attacks, and compares it with probabilistic
// candidates. Threat math is the production EnemyMovementMath (linked). See README.md.

var seeds = args.Length > 0 && int.TryParse(args[0], out var s0) ? s0 : 40;
var models = new[] { Model.Current, Model.CurrentNoOverride, Model.Softmax, Model.Weighted, Model.WeightedCalm };

foreach (var scenario in new[] { Scenario.Open, Scenario.Protect })
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
Console.WriteLine();
Console.WriteLine("maxAtk = most actors targeting one actor during the run; P(>6) = share of 0.5s samples with someone targeted by >6;");
Console.WriteLine("meanAtk = mean attackers per targeted actor; sw/min = voluntary switches (old target alive) per actor-minute;");
Console.WriteLine("idle = alive time not swinging (no target / walking / blocked); blocked = in reach but no melee slot (6 per target);");
Console.WriteLine("tankShr = share of enemy damage landing on ally tanks; starveT = actors alive >=30s never targeted (per run);");
Console.WriteLine("starveD = enemies alive >=30s never damaged; prayTTD = time until the praying retainer dies (cap if survives);");
Console.WriteLine("seizOnHld = share of seizer alive-time spent targeting the holder; atkOnHld = mean actors targeting the holder.");
Console.WriteLine("win = allies' win rate; tWin over allied wins only. Values: mean +- sd over seeds.");

enum Side { Ally, Enemy }
enum Role { Tank, Melee, Ranged, Player, Prayer }
enum Model { Current, CurrentNoOverride, Softmax, Weighted, WeightedCalm }
enum Scenario { Open, Protect }

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
    // Weighted defaults; WeightedCalm overrides some in the constructor.
    const float NewAttackerWindow = 5f;
    readonly float DwellMin = 3f, DwellMax = 6f, MinHoldForHitRedraw = 1.5f;
    const float ThreatFloor = 0.25f; readonly float ThreatGain = 1.5f;   // w_threat = floor + gain * share^2
    readonly float CrowdK = 3f;                                          // w_crowd = 1 / (1 + (n/K)^2)
    const float DistanceScale = 10f;                                     // w_dist = 1 / (1 + max(0, d - reach)/scale)
    const float FinishGain = 0.5f, MutualGain = 1.5f; readonly float StickyGain = 2f;
    const float FocusGain = 2f, AttackingPlayerGain = 1.5f; readonly float GuardSeizerGain = 3f, TaskGain = 12f;
    bool Probabilistic => model is Model.Softmax or Model.Weighted or Model.WeightedCalm;

    readonly Model model;
    readonly Scenario scenario;
    readonly Actor[] a;
    readonly int n, player, prayer = -1;
    readonly float bodyX = 0f, bodyZ = -4f;
    readonly float[] threat; // threat[victim * n + source]
    readonly float decay = MathF.Exp(-Dt / EnemyMovementMath.ThreatDecayTau);
    float t;
    int lastFocus = -1, lastHolder = -1;

    // metrics
    double maxAttackers, samples, samplesOver6, atkSum, atkCount, switches, aliveFrames, idleFrames, blockedFrames;
    double enemyDmg, enemyDmgOnTanks, seizerFrames, seizerOnHolderFrames, holderSamples, holderAtkSum, seizerPrayFrames, seizerOnPrayFrames;

    public Sim(Model model, Scenario scenario, int seed)
    {
        this.model = model;
        this.scenario = scenario;
        if (model == Model.WeightedCalm)
        {
            DwellMin = 4f; DwellMax = 8f; MinHoldForHitRedraw = 2.5f;
            StickyGain = 6f; ThreatGain = 1.5f; CrowdK = 4f; TaskGain = 100f; // task sweep: 25->46%, 50->59%, 100->73%, 200->80% seizers on the prayer
            // Guards on seizers. Sweep over 100 seeds (guarding win rate): x3 29%, x10 42%, x30 45%, x100 50%;
            // the fixed choice it replaces won 39%. x30 is what the plugin uses (CombatCompanionManager.Aggro.cs).
            GuardSeizerGain = 30f;
        }
        // Tuning hook: TS_STICKY=4 TS_TASK=25 ... override the probabilistic parameters for every probabilistic model.
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
        Add(Side.Ally, Role.Tank, 3);
        Add(Side.Ally, Role.Melee, 10);
        Add(Side.Ally, Role.Ranged, 7);
        Add(Side.Ally, Role.Player, 1);
        if (scenario == Scenario.Protect) Add(Side.Ally, Role.Prayer, 1);
        Add(Side.Enemy, Role.Tank, 2);
        Add(Side.Enemy, Role.Melee, 12);
        Add(Side.Enemy, Role.Ranged, 6);
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
        threat = new float[n * n];
        foreach (var x in a)
        {
            x.LastHitBy = Enumerable.Repeat(-100f, n).ToArray();
            // Per-actor stream so draws do not depend on iteration order. Not HashCode.Combine: it is
            // salted per process, which made runs irreproducible.
            x.Rng = new Random((int)Mix((uint)seed * 0x9E3779B9u ^ (uint)x.Id));
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
            if (scenario == Scenario.Protect && x.Side == Side.Ally && DistToBody(c) > LordGuardRange + x.Reach) continue;
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
            if (!x.Alive || x.Role == Role.Prayer) continue;
            float gx, gz, stop;
            if (Live(x.Target)) { gx = a[x.Target].X; gz = a[x.Target].Z; stop = x.Reach - 0.3f; }
            else if (scenario == Scenario.Protect && x.Side == Side.Ally) { gx = bodyX; gz = bodyZ; stop = 3f; }
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
            if (!x.Alive || !x.Fights || !Live(x.Target)) continue;
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
            var inReach = Live(x.Target) && Dist(x, a[x.Target]) <= x.Reach;
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
            var dmg = x.Damage * (v.Role == Role.Prayer ? RaiserDamageScale : 1f);
            v.Hp -= dmg;
            v.EverDamaged = true;
            threat[v.Id * n + x.Id] += EnemyMovementMath.HitThreat((int)dmg, x.IsTank);
            if (t - v.LastHitBy[x.Id] > NewAttackerWindow && v.Target != x.Id) v.HitByNew = true;
            v.LastHitBy[x.Id] = t;
            if (x.Side == Side.Enemy)
            {
                enemyDmg += dmg;
                if (v.IsTank) enemyDmgOnTanks += dmg;
            }
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
