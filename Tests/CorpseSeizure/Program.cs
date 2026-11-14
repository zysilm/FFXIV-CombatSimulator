// Scenario tests for the corpse seizure (CombatSimulator/Battle/CorpseSeizure.cs, linked).
//
// Every expectation below was worked out by reading the code as it stood in Plugin.War.cs at commit
// e93e51e (before refactor stage B3 moved it), not from the moved code. Time steps are powers of two
// (1/64 s, 1/4 s) so the float sums are exact and the frame on which something happens is exact.

using System.Globalization;
using System.Numerics;
using CombatSimulator.Battle;
using CombatSimulator.Simulation;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var failures = 0;
var checks = 0;
void Check(string scenario, bool ok, string what)
{
    checks++;
    if (ok)
        return;
    failures++;
    Console.WriteLine($"FAIL [{scenario}] {what}");
}

const float Dt = 1f / 64f;          // exact in binary

// --- Stalemate: guards >= seizers at the body -> no progress, the seizers are held off. -----------------------
{
    const string s = "stalemate";
    var w = new World();
    var a = w.Enemy("A", 1f, 0f);
    var b = w.Enemy("B", -1f, 0f);
    w.Enemy("C", 30f, 0f);
    w.Enemy("D", 31f, 0f);
    w.Guard("G1", 0f, 1f);
    w.Guard("G2", 0f, -1f);
    var seizure = w.Build();
    for (var i = 0; i < 640; i++)
        seizure.Tick(Dt);
    Check(s, seizure.Progress == 0f, $"progress stays 0 (got {seizure.Progress})");
    Check(s, !seizure.BodySeized, "not seized");
    Check(s, a.Held && b.Held, "seizers are held off (SeizeHeld)");
    Check(s, a.Goal == Vector3.Zero && b.Goal == Vector3.Zero, "seizers go for the body");
    Check(s, w.BattleLogs.Count == 0, "no 'Nobody stands over' line while held");

    // Down to one guard against the two seizers there: the hold is lost.
    w.Guards.RemoveAt(1);
    seizure.Tick(Dt);
    Check(s, !a.Held && !b.Held, "one guard against two: no longer held");
    Check(s, w.BattleLogs.Count == 1 && w.BattleLogs[0] == "Nobody stands over the lord's body!", "the line is logged once on losing the hold");
    Check(s, seizure.Progress == Dt, $"progress starts filling ({seizure.Progress})");
    for (var i = 0; i < 126; i++)
        seizure.Tick(Dt);
    Check(s, !seizure.BodySeized && seizure.Progress == 127 * Dt, "127 frames: not yet");
    seizure.Tick(Dt);
    Check(s, seizure.BodySeized, "128 frames (2 s): seized");
    Check(s, w.BattleLogs.Count == 2, "only one 'Nobody stands over' line");
}

// --- Outnumbered: progress fills in exactly 2 s, then the body is carried off. ---------------------------------
{
    const string s = "outnumbered";
    var w = new World();
    var a = w.Enemy("A", 1f, 0f);
    var b = w.Enemy("B", -1.2f, 0f);
    w.Enemy("C", 30f, 0f);
    w.Enemy("D", 31f, 0f);
    w.Guard("G1", 0f, 1f);
    var seizure = w.Build();
    for (var i = 0; i < 127; i++)
        seizure.Tick(Dt);
    Check(s, !seizure.BodySeized && seizure.Progress == 127 * Dt, $"127 frames: not seized ({seizure.Progress})");
    Check(s, w.BattleLogs.Count == 0, "uncontested from the start: no change, no line");
    seizure.Tick(Dt);
    Check(s, seizure.BodySeized, "frame 128: seized");
    Check(s, w.CorpseCaptured == true, "corpse captured");
    Check(s, w.Surrendered == 1 && w.EnemyVictories == 1, "retainers surrendered, enemy victory forced, once");
    Check(s, w.BattleLogs.Count == 1 && w.BattleLogs[0] == "A carries off the lord's body!", "carried off by the closest (A, gap 0.5)");
    Check(s, a.Goal == null && !a.Held && b.Goal == null && !b.Held && w.SeizerIds.Count == 0, "seizers released");
    Check(s, w.ControlledBy == a && w.Grabs == 1, "the grab is tried at once on A");
    Check(s, seizure.Progress == 2f, "progress is left full");
    w.OutcomeDecided = true;
    seizure.Tick(Dt);
    Check(s, seizure.Progress == 0f, "after the outcome: progress back to 0");
    Check(s, w.EnemyVictories == 1, "no second seizure");
}

// --- Pushed out: drains at half rate, not reset; back in, it finishes from where it was. ------------------------
{
    const string s = "pushed-out";
    var w = new World();
    var a = w.Enemy("A", 1f, 0f);       // gap 0.5
    w.Enemy("C", 30f, 0f);
    var seizure = w.Build();
    for (var i = 0; i < 64; i++)
        seizure.Tick(Dt);
    Check(s, seizure.Progress == 1f, $"1 s at the body: 1 s of progress ({seizure.Progress})");
    a.Position = new Vector3(2.5f, 0f, 0f);   // gap 2.0 > 1.5: out of reach, still a seizer
    for (var i = 0; i < 64; i++)
        seizure.Tick(Dt);
    Check(s, seizure.Progress == 0.5f, $"1 s pushed out: drained by half a second ({seizure.Progress})");
    Check(s, a.Goal != null, "still a seizer");
    a.Position = new Vector3(1f, 0f, 0f);
    for (var i = 0; i < 95; i++)
        seizure.Tick(Dt);
    Check(s, !seizure.BodySeized, "95 frames back in: not yet (0.5 + 95/64 < 2)");
    seizure.Tick(Dt);
    Check(s, seizure.BodySeized, "96 frames back in: seized (0.5 + 1.5 = 2)");

    // A long push drains to 0 and stops there.
    var w2 = new World();
    var a2 = w2.Enemy("A", 1f, 0f);
    var s2 = w2.Build();
    for (var i = 0; i < 32; i++)
        s2.Tick(Dt);
    a2.Position = new Vector3(5f, 0f, 0f);
    for (var i = 0; i < 640; i++)
        s2.Tick(Dt);
    Check(s, s2.Progress == 0f, "drains to 0, not below");
}

// --- 95% cap while the lord's ragdoll is not up; completes once it is. ------------------------------------------
{
    const string s = "ragdoll-cap";
    var w = new World { BodyReady = false };
    w.Enemy("A", 1f, 0f);
    var seizure = w.Build();
    for (var i = 0; i < 320; i++)
        seizure.Tick(Dt);
    Check(s, seizure.Progress == 2f * 0.95f, $"held at 95% ({seizure.Progress})");
    Check(s, !seizure.BodySeized, "not seized while the ragdoll waits");
    Check(s, w.Infos.Count > 0 && w.Infos[^1].Contains("; body not yet fallen (ragdoll waiting); seized 95 %."), $"log says so: {w.Infos.LastOrDefault()}");
    w.BodyReady = true;
    for (var i = 0; i < 6; i++)
        seizure.Tick(Dt);
    Check(s, !seizure.BodySeized, "6 frames after: not yet (1.9 + 6/64 < 2)");
    seizure.Tick(Dt);
    Check(s, seizure.BodySeized, "7th frame after the ragdoll is up: seized");
}

// --- Grab retry: every 0.4 s for up to 8 s. ---------------------------------------------------------------------
{
    // 1/64 s steps: the 0.4 s timer reaches <= 0 after 26 frames (0.4f - 25/64 > 0); 8 s run out at 512 frames.
    // Retries on frames 26, 52, ..., 494 (19); on frame 512 the timer is not due, so the warning is never logged.
    const string s = "grab-retry-1/64";
    var w = new World { HoldWorks = false };
    w.Enemy("A", 1f, 0f);
    var seizure = w.Build();
    for (var i = 0; i < 128; i++)
        seizure.Tick(Dt);
    Check(s, seizure.BodySeized && w.Grabs == 1, "seized; first grab");
    var grabFrames = new List<int>();
    for (var f = 1; f <= 700; f++)
    {
        var before = w.Grabs;
        seizure.Tick(Dt);
        if (w.Grabs != before)
            grabFrames.Add(f);
    }
    var expected = Enumerable.Range(1, 19).Select(j => 26 * j).ToList();
    Check(s, grabFrames.SequenceEqual(expected), $"retries on frames {string.Join(",", grabFrames)} (expected 26..494 every 26)");
    Check(s, w.Warnings.Count == 0, "no warning (the last retry is not on the frame the 8 s run out)");
}
{
    // 1/4 s steps: timer 0.4 -> 0.15 -> -0.1: a retry every 2 frames; 8 s run out on frame 32, a retry frame,
    // so the warning is logged once there.
    const string s = "grab-retry-1/4";
    var w = new World { HoldWorks = false };
    w.Enemy("A", 1f, 0f);
    var seizure = w.Build();
    for (var i = 0; i < 8; i++)
        seizure.Tick(0.25f);
    Check(s, seizure.BodySeized && w.Grabs == 1, "seized after 8 quarter seconds; first grab");
    var grabFrames = new List<int>();
    for (var f = 1; f <= 40; f++)
    {
        var before = w.Grabs;
        seizure.Tick(0.25f);
        if (w.Grabs != before)
            grabFrames.Add(f);
        if (f == 32)
            Check(s, w.Warnings.Count == 1 && w.Warnings[0] == "War: the seizer could not take hold of the lord's body.", "warning on frame 32");
    }
    Check(s, grabFrames.SequenceEqual(Enumerable.Range(1, 16).Select(j => 2 * j)), $"retries on frames {string.Join(",", grabFrames)}");
    Check(s, w.Warnings.Count == 1, "one warning");
}
{
    const string s = "grab-takes";
    var w = new World { HoldWorks = false };
    w.Enemy("A", 1f, 0f);
    var seizure = w.Build();
    for (var i = 0; i < 8; i++)
        seizure.Tick(0.25f);
    seizure.Tick(0.25f);
    w.HoldWorks = true;             // the retry on the next frame (frame 2) takes hold
    seizure.Tick(0.25f);
    Check(s, w.Holding && w.Grabs == 2, "second grab takes");
    seizure.Tick(0.25f);
    Check(s, w.Infos.Contains("War: the seizer has the lord's body in hand."), "logged on the next frame");
    var grabs = w.Grabs;
    for (var i = 0; i < 40; i++)
        seizure.Tick(0.25f);
    Check(s, w.Grabs == grabs && w.Infos.Count(l => l.Contains("in hand")) == 1, "no more retries, logged once");
}
{
    const string s = "grab-preconditions";
    // Ragdoll not up (BodyCanBeHeld false) -> no control, no grab; control that does not start -> no grab, no retry.
    var w = new World { BodyCanBeHeldOverride = false };
    w.Enemy("A", 1f, 0f);
    var seizure = w.Build();
    for (var i = 0; i < 128; i++)
        seizure.Tick(Dt);
    Check(s, seizure.BodySeized && w.ControlledBy == null && w.Grabs == 0, "no control without a ragdoll to hold");
    var w2 = new World { ControlStarts = false };
    w2.Enemy("A", 1f, 0f);
    var s2 = w2.Build();
    for (var i = 0; i < 700; i++)
        s2.Tick(Dt);
    Check(s, s2.BodySeized && w2.TakeControlCalls == 1 && w2.Grabs == 0, "control did not start: no grab, no retry");
    var w3 = new World { AlreadyControlling = true };
    w3.Enemy("A", 1f, 0f);
    var s3 = w3.Build();
    for (var i = 0; i < 700; i++)
        s3.Tick(Dt);
    Check(s, s3.BodySeized && w3.TakeControlCalls == 0 && w3.Grabs == 0, "something already under control: left alone");
}

// --- Seizer review every 3 s: a seizer beyond max(10 m, 2x the nearest non-seizer) goes back to the fight. ------
{
    // The review timer starts at 0: the first tick reviews and sets 3 s; the next review is 192 frames later.
    const string s = "review";
    var w = new World();
    var a = w.Enemy("A", 3f, 0f);
    var b = w.Enemy("B", 5f, 0f);
    var c = w.Enemy("C", 8f, 0f);
    var d = w.Enemy("D", 25f, 0f);
    var seizure = w.Build();
    seizure.Tick(Dt);   // frame 1
    Check(s, a.Goal != null && b.Goal != null && c.Goal == null && d.Goal == null, "4 living: the 2 nearest go");
    for (var f = 2; f < 10; f++)
        seizure.Tick(Dt);
    b.Position = new Vector3(30f, 0f, 0f);  // keep = max(10, 2 x 8) = 16 < 30
    for (var f = 10; f <= 192; f++)
        seizure.Tick(Dt);
    Check(s, b.Goal != null && c.Goal == null, "frame 192: B still a seizer (no review yet)");
    seizure.Tick(Dt);   // frame 193
    Check(s, b.Goal == null && !b.Held && c.Goal != null, "frame 193: B dropped, C takes its place");
    Check(s, w.SeizerIds.SetEquals(new[] { a.Id, c.Id }), "seizer ids follow");
    Check(s, w.Engaged.Count(n => n == c) == 1, "C woken once");

    // The 10 m floor: nearest non-seizer at 4 m -> keep 10.
    var w2 = new World();
    w2.Enemy("A", 2.5f, 0f);       // gaps 2.0 and 2.5: out of reach, no progress
    var b2 = w2.Enemy("B", 3f, 0f);
    w2.Enemy("C", 4f, 0f);
    w2.Enemy("D", 4.5f, 0f);
    var s2 = w2.Build();
    s2.Tick(Dt);
    b2.Position = new Vector3(9.5f, 0f, 0f);
    for (var f = 2; f <= 193; f++)
        s2.Tick(Dt);
    Check(s, b2.Goal != null, "B at 9.5 m with keep 10: kept");
    b2.Position = new Vector3(10.5f, 0f, 0f);
    for (var f = 194; f <= 384; f++)
        s2.Tick(Dt);
    Check(s, b2.Goal != null, "B at 10.5 m: kept until the next review (frame 385)");
    s2.Tick(Dt);        // frame 385: reviews are at 1, 193, 385
    Check(s, b2.Goal == null, "B at 10.5 m: dropped at the review");

    // No non-seizer candidate: nobody is dropped however far.
    var w3 = new World();
    var a3 = w3.Enemy("A", 3f, 0f);
    var s3 = w3.Build();
    s3.Tick(Dt);
    a3.Position = new Vector3(80f, 0f, 0f);
    for (var f = 2; f <= 600; f++)
        s3.Tick(Dt);
    Check(s, a3.Goal != null, "the only enemy stays a seizer");
}

// --- Sentries: idle sentries beyond 40 m are not woken for the body. --------------------------------------------
{
    const string s = "sentries";
    var w = new World();
    var n1 = w.Enemy("N1", 5f, 0f);
    var s1 = w.Enemy("S1", 45f, 0f, idleSentry: true);
    var s2 = w.Enemy("S2", 50f, 0f);        // a sentry already in the fight: not idle
    var n2 = w.Enemy("N2", 60f, 0f);
    var seizure = w.Build();
    for (var i = 0; i < 400; i++)
        seizure.Tick(Dt);
    Check(s, n1.Goal != null && s2.Goal != null, "4 living (S1 counts): 2 go, N1 and S2");
    Check(s, s1.Goal == null && !w.Engaged.Contains(s1), "S1 (idle, 45 m) never woken");
    Check(s, n2.Goal == null, "N2 not needed");

    var wb = new World();
    var near = wb.Enemy("S", 39f, 0f, idleSentry: true);
    wb.Enemy("F1", 60f, 0f);
    wb.Enemy("F2", 61f, 0f);
    var sb = wb.Build();
    sb.Tick(Dt);
    Check(s, near.Goal != null && wb.Engaged.Contains(near), "an idle sentry at 39 m is woken and goes");

    var wc = new World();
    var far = wc.Enemy("S", 40.5f, 0f, idleSentry: true);
    var sc = wc.Build();
    for (var i = 0; i < 64; i++)
        sc.Tick(Dt);
    Check(s, far.Goal == null && wc.Engaged.Count == 0 && sc.Progress == 0f, "the only enemy an idle sentry at 40.5 m: nobody goes");
}

// --- Seizer share: ceil(half the living enemies), at least 1; captives and absent ones are not counted. ---------
{
    const string s = "share";
    foreach (var (n, want) in new[] { (0, 0), (1, 1), (2, 1), (3, 2), (4, 2), (5, 3), (7, 4), (10, 5) })
    {
        var w = new World();
        for (var i = 0; i < n; i++)
            w.Enemy($"E{i}", 2f + i, 0f);
        var seizure = w.Build();
        seizure.Tick(Dt);
        Check(s, w.SeizerIds.Count == want, $"{n} living -> {want} (got {w.SeizerIds.Count})");
    }
    var wx = new World();
    for (var i = 0; i < 4; i++)
        wx.Enemy($"E{i}", 10f + i, 0f);
    var cap1 = wx.Enemy("Cap1", 1f, 0f); cap1.Captive = true;
    var cap2 = wx.Enemy("Cap2", 1.5f, 0f); cap2.Captive = true;
    var gone = wx.Enemy("Gone", 2f, 0f); gone.Present = false;
    var sx = wx.Build();
    sx.Tick(Dt);
    Check(s, wx.SeizerIds.Count == 2 && cap1.Goal == null && gone.Goal == null, "4 present + 2 captive + 1 absent -> 2, neither captive nor absent");

    // A seizer that dies is dropped and replaced the same tick.
    var wd = new World();
    var e0 = wd.Enemy("E0", 2f, 0f);
    var e1 = wd.Enemy("E1", 3f, 0f);
    var e2 = wd.Enemy("E2", 4f, 0f);
    var sd = wd.Build();
    sd.Tick(Dt);
    Check(s, e0.Goal != null && e1.Goal != null && e2.Goal == null, "3 living -> 2");
    e0.Present = false;
    sd.Tick(Dt);
    Check(s, e0.Goal == null && e1.Goal != null && e2.Goal == null, "E0 dead: 2 living -> 1, E0 released");
}

// --- The rule switches gate it, read live. ----------------------------------------------------------------------
{
    const string s = "rules";
    var w = new World();
    var a = w.Enemy("A", 1f, 0f);
    var seizure = w.Build();
    for (var i = 0; i < 64; i++)
        seizure.Tick(Dt);
    Check(s, a.Goal != null && seizure.Progress == 1f, "seizing under way");
    w.Settings.Retreat = false;     // switched off mid-battle
    seizure.Tick(Dt);
    Check(s, a.Goal == null && seizure.Progress == 0f && w.SeizerIds.Count == 0, "Retreat off: released, progress 0");
    w.Settings.Retreat = true;
    w.Settings.FallenRule = WarFallenRule.SpringAndAutumn;
    seizure.Tick(Dt);
    Check(s, a.Goal == null, "Spring and Autumn: nothing");
    w.Settings.FallenRule = WarFallenRule.WarringStates;
    w.LordAlive = true;
    seizure.Tick(Dt);
    Check(s, a.Goal == null, "lord alive: nothing");
    w.LordAlive = false;
    w.BattleActive = false;
    seizure.Tick(Dt);
    Check(s, a.Goal == null, "battle not active: nothing");
    w.BattleActive = true;
    w.HasLord = false;
    seizure.Tick(Dt);
    Check(s, a.Goal == null, "no local player: nothing");
    w.HasLord = true;
    seizure.Tick(Dt);
    Check(s, a.Goal != null, "all set again: going");

    // Reset: progress, seizure and the captured flag cleared; seizers released.
    for (var i = 0; i < 200; i++)
        seizure.Tick(Dt);
    Check(s, seizure.BodySeized && w.CorpseCaptured == true, "seized");
    seizure.Reset();
    Check(s, !seizure.BodySeized && seizure.Progress == 0f && w.CorpseCaptured == false, "reset clears it");
}

// --- The War: line, word for word (the template at Plugin.War.cs:261-265, e93e51e). ----------------------------
{
    const string s = "log-line";
    var w = new World();
    w.Enemy("A", 1f, 0f);
    w.Guard("G1", 3f, 4f);           // 5 m from the body: outside the contest radius
    var seizure = w.Build();
    seizure.Tick(Dt);
    var expected = "War: lord's body at <0.0, 0.0, 0.0>; 1 going for it, the closest A 0.5 m from it; " +
                   "1 seizers and 0 retainers within 3.5 m; not held, nearest retainer G1 5.0 m away; seized 1 %.";
    Check(s, w.Infos.Count == 1 && w.Infos[0] == expected, $"first frame line:\n  got      {w.Infos.FirstOrDefault()}\n  expected {expected}");
    for (var i = 0; i < 127; i++)
        seizure.Tick(Dt);
    Check(s, w.Infos.Count == 1, "no line for the next 127 frames");
    var w2 = new World();
    w2.Enemy("A", 1f, 0f);
    w2.Guard("G1", 0f, 1f);
    w2.Guard("G2", 0f, -1f);
    var s2 = w2.Build();
    s2.Tick(Dt);
    var expected2 = "War: lord's body at <0.0, 0.0, 0.0>; 1 going for it, the closest A 0.5 m from it; " +
                    "1 seizers and 2 retainers within 3.5 m; held off, nearest retainer G1 1.0 m from it; seized 0 %.";
    Check(s, w2.Infos.Count == 1 && w2.Infos[0] == expected2, $"held line:\n  got      {w2.Infos.FirstOrDefault()}\n  expected {expected2}");
    for (var i = 0; i < 128; i++)
        s2.Tick(Dt);
    Check(s, w2.Infos.Count == 2, "reported again 128 frames (2 s) later");
    var w3 = new World();
    var s3 = w3.Build();
    s3.Tick(Dt);
    var expected3 = "War: lord's body at <0.0, 0.0, 0.0>; 0 going for it, the closest none; " +
                    "0 seizers and 0 retainers within 3.5 m; no retainer left; seized 0 %.";
    Check(s, w3.Infos.Count == 1 && w3.Infos[0] == expected3, $"empty line:\n  got      {w3.Infos.FirstOrDefault()}\n  expected {expected3}");
}

Console.WriteLine(failures == 0 ? $"OK: {checks} checks passed." : $"{failures} of {checks} checks failed.");
return failures == 0 ? 0 : 1;

sealed class Npc(string name, uint id, Vector3 position)
{
    public string Name = name;
    public uint Id = id;
    public Vector3 Position = position;
    public bool Present = true;
    public bool Captive;
    public bool IdleSentry;
    public float Hitbox = 0.5f;
    public Vector3? Goal;
    public bool Held;
}

sealed class Settings : IBattleRuleSettings
{
    public WarFallenRule FallenRule { get; set; } = WarFallenRule.WarringStates;
    public bool Hostages { get; set; }
    public bool Retreat { get; set; } = true;
    public uint CaptiveEmoteId => 0;
    public bool ShowResultBanner => true;
    public bool EnableRagdoll => true;
}

sealed class World : ICorpseSeizureWorld<Npc>, ICorpseGrab<Npc>
{
    public readonly Settings Settings = new();
    public bool BattleActive = true;
    public bool LordAlive;
    public bool HasLord = true;
    public Vector3 Body = Vector3.Zero;
    public readonly List<Npc> EnemyList = new();
    public readonly List<Npc> Guards = new();
    public readonly HashSet<uint> SeizerIds = new();
    public readonly List<Npc> Engaged = new();
    public readonly List<string> BattleLogs = new();
    public readonly List<string> Infos = new();
    public readonly List<string> Warnings = new();
    public bool? CorpseCaptured;
    public int Surrendered;
    public int EnemyVictories;
    public bool BodyReady { get; set; } = true;
    public bool OutcomeDecided { get; set; }

    // Grab
    public bool? BodyCanBeHeldOverride;
    public bool ControlStarts = true;
    public bool AlreadyControlling;
    public bool HoldWorks = true;
    public Npc? ControlledBy;
    public int TakeControlCalls;
    public int Grabs;
    public bool Holding;
    private uint nextId = 1;

    public Npc Enemy(string name, float x, float z, bool idleSentry = false)
    {
        var npc = new Npc(name, nextId++, new Vector3(x, 0f, z)) { IdleSentry = idleSentry };
        EnemyList.Add(npc);
        return npc;
    }

    public Npc Guard(string name, float x, float z)
    {
        var npc = new Npc(name, 1000 + nextId++, new Vector3(x, 0f, z));
        Guards.Add(npc);
        return npc;
    }

    public CorpseSeizure<Npc> Build()
        => new(new BattleRules(Settings, () => BattleActive, () => LordAlive), this, this, 3.5f);

    public Vector3? LordBodyLocation() => HasLord ? Body : null;
    public IReadOnlyList<Npc> Enemies => EnemyList;
    public bool IsPresent(Npc npc) => npc.Present;
    public bool IsCaptive(Npc npc) => npc.Captive;
    public bool IsIdleSentry(Npc npc) => npc.IdleSentry;
    public Vector3 PositionOf(Npc npc) => npc.Position;
    public float HitboxRadiusOf(Npc npc) => npc.Hitbox;
    public string NameOf(Npc npc) => npc.Name;
    public uint IdOf(Npc npc) => npc.Id;
    public void SetSeizeGoal(Npc npc, Vector3? goal) => npc.Goal = goal;
    public void SetSeizeHeld(Npc npc, bool held) => npc.Held = held;
    public void Engage(Npc npc) => Engaged.Add(npc);
    public void ClearSeizerIds() => SeizerIds.Clear();
    public void AddSeizerId(uint id) => SeizerIds.Add(id);

    // CombatCompanionManager.RetainersAtBody: flat distance, nearest of all who can fight, count within radius.
    public int RetainersAtBody(Vector3 body, float radius, out string? nearestGuard, out float nearestDistance)
    {
        var count = 0;
        nearestGuard = null;
        nearestDistance = float.MaxValue;
        foreach (var g in Guards)
        {
            var d = new Vector2(g.Position.X - body.X, g.Position.Z - body.Z).Length();
            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearestGuard = g.Name;
            }
            if (d <= radius)
                count++;
        }
        return count;
    }

    public void SetCorpseCaptured(bool captured) => CorpseCaptured = captured;
    public void SurrenderRetainers() => Surrendered++;
    public void ForceEnemyVictory() { EnemyVictories++; OutcomeDecided = true; }
    public void BattleLog(string text) => BattleLogs.Add(text);
    public void Info(string text) => Infos.Add(text);
    public void Warning(string text) => Warnings.Add(text);

    public bool BodyCanBeHeld => BodyCanBeHeldOverride ?? BodyReady;
    public bool IsControlling => AlreadyControlling || ControlledBy != null;
    public void TakeControl(Npc npc)
    {
        TakeControlCalls++;
        if (ControlStarts)
            ControlledBy = npc;
    }
    public void Grab()
    {
        Grabs++;
        if (HoldWorks)
            Holding = true;
    }
    public bool IsHolding => Holding;
}
