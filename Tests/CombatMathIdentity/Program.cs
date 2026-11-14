using System.Numerics;
using CombatSimulator.Combat;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

// Refactor stage B1: every function moved into CombatantMath / AggroModel must give bit-identical
// results to the code it replaced (Original.cs, copied from f3568b9), over a fixed table of edge values
// and a seeded random sweep. Draw-based functions must also consume the generator identically, and the
// MP-price lookups must happen in the same number and order. Exit code 1 on any difference.

var cases = args.Length > 0 && int.TryParse(args[0], out var n0) ? n0 : 200_000;
var t = new Table();
var rng = new Random(20261114);

float[] edge =
{
    0f, -0f, float.Epsilon, 1e-7f, 0.001f, 0.0099f, 0.01f, 0.1f, 0.25f, 0.4999f, 0.5f, 0.99f, 1f, 1.0001f, 1.5f, 2f,
    2.5f, 2.9999f, 3f, 3.25f, 3.5f, 4.75f, 4.9999f, 5f, 5.0001f, 6f, 7.5f, 10f, 25f, 99.99f, 100f, 100.01f, 1e6f,
    float.MaxValue, -0.0001f, -1f, -3.5f, -1e6f, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
};
float R() => rng.Next(8) switch
{
    0 => edge[rng.Next(edge.Length)],
    1 => BitConverter.Int32BitsToSingle(rng.Next(int.MinValue, int.MaxValue)),
    2 => (float)(rng.NextDouble() * 2 - 1),
    3 => (float)rng.Next(-5, 40) * 0.5f,
    _ => (float)(rng.NextDouble() * 50 - 5),
};
float RFinite() { float v; do v = R(); while (!float.IsFinite(v) || MathF.Abs(v) > 1e4f); return v; }
Vector3 RV() => rng.Next(6) == 0 ? new Vector3(R(), R(), R()) : new Vector3(RFinite(), RFinite(), RFinite());
NpcAttackStyle RStyle() => (NpcAttackStyle)rng.Next(4);
OrigConfig RCfg() => new()
{
    EnableNpcScale = rng.Next(2) == 0, NpcScale = R(), NpcAttackRangeScale = R(),
    ActionMode = rng.Next(2) == 0, ActionEnemyAttackSpeed = R(), EnhancedEnemyTactics = rng.Next(2) == 0,
};

// ---------------------------------------------------------------- distances and standing
foreach (var a in edge) foreach (var b in edge)
{
    var p = new Vector3(a, 1f, b); var q = new Vector3(b, -2f, a);
    t.F("FlatDistance (enemy copy)", OrigEnemy.FlatDistance(p, q), CombatantMath.FlatDistance(p, q));
    t.F("FlatDistance (ally copy)", OrigAlly.FlatDistance(p, q), CombatantMath.FlatDistance(p, q));
}
for (var i = 0; i < cases; i++)
{
    var p = RV(); var q = RV(); var r1 = R(); var r2 = R();
    t.F("FlatDistance (enemy copy)", OrigEnemy.FlatDistance(p, q), CombatantMath.FlatDistance(p, q));
    t.F("FlatDistance (ally copy)", OrigAlly.FlatDistance(p, q), CombatantMath.FlatDistance(p, q));
    t.F("BodyGap (enemy CrowdGap)", OrigEnemy.CrowdGap(r1, r2, p, q), CombatantMath.BodyGap(p, q, r1, r2));
    t.F("BodyGap (ally CrowdGap)", OrigAlly.CrowdGap(p, q, r1, r2), CombatantMath.BodyGap(p, q, r1, r2));
    var stand = R();
    t.V("StandPoint (enemy goal)", OrigEnemy.Goal(r1, r2, p, q, stand), CombatantMath.StandPoint(p, q, stand + (r1 + r2)));
    t.V("StandPoint (ally goal)", OrigAlly.Goal(p, q, stand, r1, r2), CombatantMath.StandPoint(p, q, stand + r1 + r2));
}
// Coincident and nearly coincident bodies (the +Z fallback).
foreach (var d in new[] { 0f, 1e-4f, 9.99e-4f, 1e-3f, 1.001e-3f, 1e-2f })
foreach (var stand in edge)
{
    var p = new Vector3(3f, 0f, 4f); var q = new Vector3(3f + d, 7f, 4f - d);
    t.V("StandPoint (enemy goal)", OrigEnemy.Goal(0.5f, 0.75f, p, q, stand), CombatantMath.StandPoint(p, q, stand + (0.5f + 0.75f)));
    t.V("StandPoint (ally goal)", OrigAlly.Goal(p, q, stand, 0.5f, 0.75f), CombatantMath.StandPoint(p, q, stand + 0.5f + 0.75f));
}
foreach (var reach in edge.Concat(Enumerable.Range(0, cases).Select(_ => R())))
{
    t.F("StandGap (enemy)", OrigEnemy.Stand(reach), CombatantMath.StandGap(reach));
    t.F("StandGap (ally)", OrigAlly.Stand(reach), CombatantMath.StandGap(reach));
}
foreach (var inReach in new[] { false, true })
foreach (var gap in edge) foreach (var reach in edge) foreach (var stand in edge)
{
    t.B("NextInReach (enemy)", OrigEnemy.InReach(inReach, gap, reach, stand), CombatantMath.NextInReach(inReach, gap, reach, stand));
    t.B("NextInReach (ally)", OrigAlly.InReach(inReach, gap, reach, stand), CombatantMath.NextInReach(inReach, gap, reach, stand));
}

// ---------------------------------------------------------------- reach
NpcSkill RSkill() => new()
{
    ActionId = (uint)rng.Next(1, 12), Range = R(), AttackStyle = RStyle(), HpThreshold = rng.Next(3) == 0 ? R() : 1f,
    CooldownRemaining = rng.Next(3) == 0 ? R() : 0f,
};
NpcBehavior RBehavior()
{
    var b = new NpcBehavior { AutoAttackStyle = RStyle(), AutoAttackRange = R(), MoveSpeed = R() };
    var k = rng.Next(6);
    for (var i = 0; i < k; i++) b.Skills.Add(RSkill());
    return b;
}
SimulatedEntityState RState() => new()
{
    CurrentHp = rng.Next(4) == 0 ? rng.Next(-5, 5) : rng.Next(0, 20000), MaxHp = rng.Next(5) == 0 ? rng.Next(-2, 2) : rng.Next(1, 20000),
    CurrentMp = rng.Next(0, 10001), MaxMp = rng.Next(5) == 0 ? rng.Next(-1, 3) : rng.Next(1, 10001),
};
var prices = new int[12];
for (var i = 0; i < cases; i++)
{
    var cfg = RCfg();
    var scale = ReachScale.From(new FakeSettings(cfg));
    var b = RBehavior();
    var s = RState();
    for (var k = 0; k < prices.Length; k++) prices[k] = rng.Next(4) == 0 ? 0 : rng.Next(-100, 11000);
    var origCalls = new List<uint>(); var newCalls = new List<uint>();
    int OrigPrice(uint id) { origCalls.Add(id); return prices[id]; }
    int NewPrice(uint id) { newCalls.Add(id); return prices[id]; }

    t.F("ReachNow (enemy)", OrigEnemy.ReachNow(cfg, b, s, OrigPrice), CombatantMath.ReachNow(b, s, ReachPolicy.Enemy, scale, NewPrice));
    t.B("ReachNow (enemy) price lookups", true, origCalls.SequenceEqual(newCalls));
    origCalls.Clear(); newCalls.Clear();
    t.F("ReachNow (ally)", OrigAlly.CrowdReachNow(b, s, OrigPrice), CombatantMath.ReachNow(b, s, ReachPolicy.Ally, default, NewPrice));
    t.B("ReachNow (ally) price lookups", true, origCalls.SequenceEqual(newCalls));

    foreach (var skill in b.Skills)
    {
        t.F("SkillReach (enemy)", OrigEnemy.SkillReach(cfg, b, skill), CombatantMath.SkillReach(skill, b.AutoAttackStyle, ReachPolicy.Enemy, scale));
        t.F("SkillReach (ally)", OrigAlly.SkillReach(skill), CombatantMath.SkillReach(skill, b.AutoAttackStyle, ReachPolicy.Ally, default));
        t.I("SkillMpCost (enemy)", OrigEnemy.SkillMpCost(s, skill.ActionId, id => prices[id]), CombatantMath.SkillMpCost(prices[skill.ActionId], s.MaxMp));
        t.I("SkillMpCost (ally)", OrigAlly.SkillMpCost(s, skill.ActionId, id => prices[id]), CombatantMath.SkillMpCost(prices[skill.ActionId], s.MaxMp));
    }
    var range = R();
    t.F("ScaleNonMeleeRange", OrigEnemy.ScaleNpcNonMeleeRange(cfg, range), CombatantMath.ScaleNonMeleeRange(range, scale));
    t.F("ScaleMeleeRange", OrigEnemy.ScaleNpcMeleeRange(cfg, range), CombatantMath.ScaleMeleeRange(range, scale));
    var dist = R();
    t.B("MagicAutoReach (enemy swing held)", OrigEnemy.HoldsMagicSwing(cfg, dist), dist > CombatantMath.MagicAutoReach(ReachPolicy.Enemy, scale));
    t.B("MagicAutoReach (ally swing held)", OrigAlly.HoldsMagicSwing(dist), dist > CombatantMath.MagicAutoReach(ReachPolicy.Ally, default));

    t.F("ActionPace (enemy)", OrigEnemy.ActionPace(cfg), CombatantMath.ActionPace(cfg.ActionMode, cfg.ActionEnemyAttackSpeed));
    t.F("ActionPace (ally, Math.Max)", OrigAlly.ActionPace(cfg), CombatantMath.ActionPace(cfg.ActionMode, cfg.ActionEnemyAttackSpeed));
    t.F("MoveSpeed (enemy)", OrigEnemy.Speed(b), CombatantMath.MoveSpeed(b.MoveSpeed, MovePolicy.Enemy));
    t.F("MoveSpeed (ally)", OrigAlly.Speed(b), CombatantMath.MoveSpeed(b.MoveSpeed, MovePolicy.Ally));
    var radius = R();
    t.F("TurnRate (enemy)", OrigEnemy.NpcTurnRate(cfg, radius), CombatantMath.TurnRate(MovePolicy.Enemy, cfg.EnhancedEnemyTactics, radius));
}
// The scale grid (every edge value of both settings, both switch positions).
foreach (var on in new[] { false, true })
foreach (var npcScale in edge) foreach (var rangeScale in edge) foreach (var range in edge)
{
    var cfg = new OrigConfig { EnableNpcScale = on, NpcScale = npcScale, NpcAttackRangeScale = rangeScale };
    var scale = ReachScale.From(new FakeSettings(cfg));
    t.F("ScaleNonMeleeRange", OrigEnemy.ScaleNpcNonMeleeRange(cfg, range), CombatantMath.ScaleNonMeleeRange(range, scale));
    t.F("ScaleMeleeRange", OrigEnemy.ScaleNpcMeleeRange(cfg, range), CombatantMath.ScaleMeleeRange(range, scale));
}
foreach (var mode in new[] { false, true }) foreach (var speed in edge)
{
    var cfg = new OrigConfig { ActionMode = mode, ActionEnemyAttackSpeed = speed };
    t.F("ActionPace (enemy)", OrigEnemy.ActionPace(cfg), CombatantMath.ActionPace(mode, speed));
    t.F("ActionPace (ally, Math.Max)", OrigAlly.ActionPace(cfg), CombatantMath.ActionPace(mode, speed));
    var b = new NpcBehavior { MoveSpeed = speed };
    t.F("MoveSpeed (enemy)", OrigEnemy.Speed(b), CombatantMath.MoveSpeed(speed, MovePolicy.Enemy));
    t.F("MoveSpeed (ally)", OrigAlly.Speed(b), CombatantMath.MoveSpeed(speed, MovePolicy.Ally));
    var c2 = new OrigConfig { EnhancedEnemyTactics = mode };
    t.F("TurnRate (enemy)", OrigEnemy.NpcTurnRate(c2, speed), CombatantMath.TurnRate(MovePolicy.Enemy, mode, speed));
}

// ---------------------------------------------------------------- turning
float RRot() => rng.Next(4) == 0 ? edge[rng.Next(edge.Length)] is var e && float.IsFinite(e) && MathF.Abs(e) < 50 ? e : 0f : (float)(rng.NextDouble() * 40 - 20);
for (var i = 0; i < cases; i++)
{
    var cfg = new OrigConfig { EnhancedEnemyTactics = rng.Next(2) == 0 };
    var radius = R(); var p = new Vector3(RFinite(), RFinite(), RFinite());
    var q = rng.Next(10) == 0 ? p + new Vector3(0.01f, 0f, 0.01f) : new Vector3(RFinite(), RFinite(), RFinite());
    var rot = RRot(); var dt = rng.Next(5) == 0 ? edge[rng.Next(edge.Length)] : (float)rng.NextDouble() * 0.1f;
    if (!float.IsFinite(p.X) || !float.IsFinite(q.X) || !float.IsFinite(p.Z) || !float.IsFinite(q.Z)) continue; // an infinite diff never leaves the wrap loop (both versions)
    var a = Try(() => (OrigEnemy.ForceRotateToward(cfg, radius, p, rot, q, dt, out var r), r));
    var b = Try(() => (CombatantMath.TurnToward(p, rot, q, CombatantMath.TurnRate(MovePolicy.Enemy, cfg.EnhancedEnemyTactics, radius), dt, out var r), r));
    t.Turn("TurnToward (enemy ForceRotateToward)", a, b);
    a = Try(() => (OrigAlly.RotateToward(p, rot, q, dt, out var r), r));
    b = Try(() => (CombatantMath.TurnToward(p, rot, q, CombatantMath.TurnRate(MovePolicy.Ally, false, radius), dt, out var r), r));
    t.Turn("TurnToward (ally RotateToward)", a, b);
}

// ---------------------------------------------------------------- aggro
foreach (var style in Enum.GetValues<NpcAttackStyle>())
{
    t.B("IsRangedStyle", OrigAlly.IsRangedStyle(style), AggroModel.IsRangedStyle(style));
    foreach (var range in edge)
        t.F("EnemyAggroReach", OrigAggro.EnemyReach(new NpcBehavior { AutoAttackStyle = style, AutoAttackRange = range }),
            AggroModel.EnemyAggroReach(new NpcBehavior { AutoAttackStyle = style, AutoAttackRange = range }));
}
for (var i = 0; i < cases; i++)
{
    var s = RState();
    t.F("HpShare", OrigAggro.HpShare(s), CombatantMath.HpShare(s));
    var b = new NpcBehavior { AutoAttackStyle = RStyle(), AutoAttackRange = R() };
    t.F("EnemyAggroReach", OrigAggro.EnemyReach(b), AggroModel.EnemyAggroReach(b));
}
for (var i = 0; i < cases; i++)
{
    uint Id() => (uint)rng.Next(0, 6);
    var threat = new Dictionary<uint, Dictionary<uint, float>>();
    var selfId = Id();
    var kind = rng.Next(4);
    if (kind > 0)
    {
        var table = new Dictionary<uint, float>();
        if (kind > 1)
            for (var k = rng.Next(0, 6); k > 0; k--) table[Id()] = rng.Next(5) == 0 ? R() : (float)rng.NextDouble() * 5000;
        threat[selfId] = table;
    }
    var counts = new Dictionary<uint, (int, int)>();
    for (var k = rng.Next(0, 5); k > 0; k--) counts[Id()] = (rng.Next(0, 8), rng.Next(0, 8));
    var selfPos = RV(); var candPos = RV(); var reach = R(); var ranged = rng.Next(2) == 0; var current = Id();
    var candId = Id(); var hp = R(); var candTarget = Id();
    var orig = OrigAggro.AggroWeight(threat, counts, selfId, selfPos, reach, ranged, current, candId, candPos, hp, candTarget);
    var (melee, rangedN) = counts.GetValueOrDefault(candId);
    var moved = AggroModel.Weight(new AggroModel.Self(selfId, selfPos, reach, ranged, current),
        new AggroModel.Candidate(candId, candPos, hp, candTarget),
        AggroModel.ThreatShare(threat.GetValueOrDefault(selfId), candId), melee, rangedN);
    t.F("Weight", orig, moved);
}
for (var i = 0; i < cases; i++)
{
    var seed = rng.Next();
    var list = new List<(uint, float)>();
    for (var k = rng.Next(0, 9); k > 0; k--) list.Add(((uint)rng.Next(1, 30), rng.Next(6) == 0 ? R() : (float)rng.NextDouble() * 50));
    var o = new OrigAggro.AggroState { Rng = new Random(seed) };
    var m = new AggroState { Rng = new Random(seed) };
    t.U("Draw (id)", OrigAggro.DrawAggro(o, list), AggroModel.Draw(m, list));
    t.I("Draw (generator after)", o.Rng.Next(), m.Rng.Next());

    var now = rng.Next(4) == 0 ? R() : (float)rng.NextDouble() * 600;
    var target = (uint)rng.Next(0, 30);
    OrigAggro.HoldAggro(o, target, now);
    AggroModel.Hold(m, target, now);
    t.U("Hold (target)", o.Target, m.Target);
    t.F("Hold (held since)", o.HeldSince, m.HeldSince);
    t.F("Hold (dwell until)", o.DwellUntil, m.DwellUntil);
    t.I("Hold (generator after)", o.Rng.Next(), m.Rng.Next());

    // A short run of hits, then both due checks, on twin states.
    var sources = new uint[] { 0, 1, 2, 3 };
    for (var k = rng.Next(0, 6); k > 0; k--)
    {
        var src = sources[rng.Next(sources.Length)];
        now += rng.Next(3) == 0 ? R() : (float)rng.NextDouble() * 4;
        OrigAggro.NoteHit(o, src, now);
        AggroModel.NoteHit(m, src, now);
        t.B("NoteHit (hit by new)", o.HitByNew, m.HitByNew);
        t.F("NoteHit (last hit)", o.LastHitBy[src], m.LastHitBy[src]);
        if (rng.Next(3) == 0) { o.HitByNew = m.HitByNew = false; }
    }
    o.Holder = m.Holder = (uint)rng.Next(0, 3);
    o.Focus = m.Focus = (uint)rng.Next(0, 3);
    var later = rng.Next(4) == 0 ? R() : now + (float)rng.NextDouble() * 10 - 2;
    var live = rng.Next(4) != 0;
    var holder = (uint)rng.Next(0, 3);
    var focus = (uint)rng.Next(0, 3);
    t.B("IsDue (enemy)", OrigAggro.EnemyDue(o, later, live, holder), AggroModel.IsDue(m, later, live, holder != m.Holder));
    t.B("IsDue (ally)", OrigAggro.AllyDue(o, later, live ? new object() : null, focus), AggroModel.IsDue(m, later, live, focus != m.Focus));
}

return t.Report();

static (bool Ok, bool Wrote, float Rot, string? Error) Try(Func<(bool, float)> f)
{
    try { var (w, r) = f(); return (true, w, r, null); }
    catch (Exception e) { return (false, false, 0f, e.GetType().Name); }
}

/// <summary>ICombatAiSettings over the copies' config (only the reach settings matter here).</summary>
sealed class FakeSettings(OrigConfig c) : ICombatAiSettings
{
    public bool EnableCombatCompanions => true;
    public int CombatCompanionMaxCount => 10;
    public bool SensePartyMembers => true;
    public bool EnableMapPlayerEnemySensing => false;
    public bool PartyCompanionDeathRagdoll => true;
    public float PartyCommandRange => 10f;
    public float PartyCommandRangeRandomness => 0.15f;
    public bool DevCompanionAppearanceVariant => false;
    public bool EnableNpcAutoEngage => false;
    public float NpcAutoEngageDelay => 2f;
    public bool EnableNpcTargetPlayer => true;
    public bool ActionMode => c.ActionMode;
    public float ActionEnemyAttackSpeed => c.ActionEnemyAttackSpeed;
    public float NpcSkillRateLimit => 5f;
    public bool EnemyTelegraphRealShapes => true;
    public bool EnableNpcScale => c.EnableNpcScale;
    public float NpcScale => c.NpcScale;
    public float NpcAttackRangeScale => c.NpcAttackRangeScale;
    public bool EnhancedEnemyTactics => c.EnhancedEnemyTactics;
    public bool TacticsRespectEngage => false;
    public bool TacticsCharge => false;
    public int EnemyTacticsMeleeAttackers => 8;
    public bool NpcCollisionActive => true;
    public float DefaultNpcHeightOffset => 0f;
}

/// <summary>
/// Bit-for-bit comparison. A NaN result is NaN in both versions, but which NaN (sign, payload, quiet
/// bit) is not fixed when two different NaNs meet or a signalling NaN goes through MathF.Max: IEEE 754
/// leaves the payload open and the JIT may commute operands. Those cases are counted in their own
/// column (no comparison or decision can tell NaNs apart) and are not differences. The random sweep
/// feeds raw bit patterns, so it produces such NaNs; game data and settings do not.
/// </summary>
sealed class Table
{
    private readonly SortedDictionary<string, (long Cases, long Diffs, long NanPayload, string? First)> rows = new(StringComparer.Ordinal);

    private void Add(string name, bool same, bool nanOnly, Func<string> describe)
    {
        var (c, d, n, first) = rows.GetValueOrDefault(name);
        if (!same && !nanOnly && first == null) first = describe();
        rows[name] = (c + 1, d + (same || nanOnly ? 0 : 1), n + (!same && nanOnly ? 1 : 0), first);
    }

    private static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static bool BothNan(float a, float b) => float.IsNaN(a) && float.IsNaN(b);
    private static string Hex(float a) => $"{a:R} (0x{BitConverter.SingleToInt32Bits(a):X8})";

    public void F(string name, float a, float b) => Add(name, Bits(a, b), BothNan(a, b), () => $"{Hex(a)} vs {Hex(b)}");
    public void V(string name, Vector3 a, Vector3 b) => Add(name,
        Bits(a.X, b.X) && Bits(a.Y, b.Y) && Bits(a.Z, b.Z),
        (Bits(a.X, b.X) || BothNan(a.X, b.X)) && (Bits(a.Y, b.Y) || BothNan(a.Y, b.Y)) && (Bits(a.Z, b.Z) || BothNan(a.Z, b.Z)),
        () => $"{Hex(a.X)},{Hex(a.Y)},{Hex(a.Z)} vs {Hex(b.X)},{Hex(b.Y)},{Hex(b.Z)}");
    public void B(string name, bool a, bool b) => Add(name, a == b, false, () => $"{a} vs {b}");
    public void I(string name, int a, int b) => Add(name, a == b, false, () => $"{a} vs {b}");
    public void U(string name, uint a, uint b) => Add(name, a == b, false, () => $"{a} vs {b}");
    public void Turn(string name, (bool Ok, bool Wrote, float Rot, string? Error) a, (bool Ok, bool Wrote, float Rot, string? Error) b)
    {
        var same = a.Ok == b.Ok && a.Error == b.Error && a.Wrote == b.Wrote;
        Add(name, same && (!a.Wrote || Bits(a.Rot, b.Rot)), same && a.Wrote && BothNan(a.Rot, b.Rot), () => $"{a} vs {b}");
    }

    public int Report()
    {
        long total = 0, diffs = 0, nans = 0;
        Console.WriteLine($"{"function",-42}{"cases",12}{"different",11}{"NaN payload",13}");
        foreach (var (name, (c, d, n, first)) in rows)
        {
            Console.WriteLine($"{name,-42}{c,12}{d,11}{n,13}{(first != null ? "   first: " + first : "")}");
            total += c; diffs += d; nans += n;
        }
        Console.WriteLine($"{"total",-42}{total,12}{diffs,11}{nans,13}");
        Console.WriteLine(diffs == 0
            ? "IDENTICAL: every moved function matches the f3568b9 expressions bit for bit (NaN results: NaN in both)."
            : "DIFFERENT");
        return diffs == 0 ? 0 : 1;
    }
}
