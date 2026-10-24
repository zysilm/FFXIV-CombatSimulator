using CombatSimulator.Voice;

int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }

// ---- Appearance -> PC shout. Race/gender always right; variant stable per seed. ----
Check(VoiceSelection.PcShoutFromCustomize(1, 1, 0, 0).Code == "mid_ma", "Midlander male");
Check(VoiceSelection.PcShoutFromCustomize(1, 2, 1, 1).Code == "hil_fb", "Highlander female splits by tribe");
Check(VoiceSelection.PcShoutFromCustomize(7, 13, 1, 2).Code == "ros_fc", "Hrothgar uses ros with three variants");
Check(VoiceSelection.PcShoutFromCustomize(8, 16, 0, 5).Code == "vie_mc", "Viera 5 % 3 = c");
Check(VoiceSelection.PcShoutFromCustomize(4, 7, 0, 5).Code == "miq_mb", "Miqo'te has only a/b");
Check(VoiceSelection.PcShoutFromCustomize(0, 0, 0, 0).IsNone, "unknown race fabricated a voice");
Check(VoiceSelection.PcShoutFromCustomize(1, 1, 2, 0).IsNone, "invalid sex fabricated a voice");
for (uint seed = 0; seed < 50; seed++)
    Check(VoiceSelection.PcShoutFromCustomize(6, 11, 1, seed) == VoiceSelection.PcShoutFromCustomize(6, 11, 1, seed), "variant not stable");

// Every combo the mapping can produce is one that exists in the game (42, from the FileExists sweep).
var all = VoiceSelection.AllPcShoutCodes();
Check(all.Count == 42 && all.Distinct().Count() == 42, $"expected 42 shout combos, got {all.Count}");
for (byte race = 1; race <= 8; race++)
    for (byte tribe = 1; tribe <= 16; tribe++)
        for (byte sex = 0; sex <= 1; sex++)
            for (uint seed = 0; seed < 6; seed++)
            {
                var v = VoiceSelection.PcShoutFromCustomize(race, tribe, sex, seed);
                Check(all.Contains(v.Code), $"race {race} tribe {tribe} sex {sex} produced missing combo {v.Code}");
            }

// Voice-id mapping (future seam): blocks follow CharaMakeType.VoiceStruct.
Check(VoiceSelection.PcShoutFromVoiceId(1).Code == "mid_ma", "voice 1");
Check(VoiceSelection.PcShoutFromVoiceId(2).Code == "mid_mb", "voice 2");
Check(VoiceSelection.PcShoutFromVoiceId(49).Code == "hil_ma", "voice 49 is a Highlander male voice (borrowed by Elezen)");
Check(VoiceSelection.PcShoutFromVoiceId(96).Code == "rog_fb", "voice 96");
Check(VoiceSelection.PcShoutFromVoiceId(97).Code == "aur_ma", "voice 97");
Check(VoiceSelection.PcShoutFromVoiceId(99).Code == "aur_mc", "voice 99");
Check(VoiceSelection.PcShoutFromVoiceId(109).Code == "aur_fa", "voice 109");
Check(VoiceSelection.PcShoutFromVoiceId(133).Code == "ros_fa", "voice 133");
Check(VoiceSelection.PcShoutFromVoiceId(168).Code == "vie_fc", "voice 168");
Check(VoiceSelection.PcShoutFromVoiceId(0).IsNone && VoiceSelection.PcShoutFromVoiceId(169).IsNone, "voice id out of range");
for (var id = 1; id <= 168; id++)
    Check(all.Contains(VoiceSelection.PcShoutFromVoiceId(id).Code), $"voice id {id} maps to a missing combo");

// NPC battle voice: stable, in 1..84, spread out.
var nn = new HashSet<uint>();
for (uint id = 1; id < 2000; id++)
{
    var v = VoiceSelection.NpcBattleFromId(id);
    Check(v.Number >= 1 && v.Number <= 84, "npc voice out of range");
    Check(v == VoiceSelection.NpcBattleFromId(id), "npc voice not stable");
    nn.Add(v.Number);
}
Check(nn.Count == 84, "npc voice hash does not reach every file");
Check(VoiceSelection.NpcBattleFromId(0).IsNone, "id 0 fabricated an npc voice");
Check(VoiceSelection.MonsterFromSePack(0).IsNone, "SE pack 0 fabricated a monster voice");

// Paths and language fallback; a missing file is never returned.
var pc = VoiceSelection.PcShoutFromCustomize(1, 1, 0, 0);
Check(pc.PathFor("en") == "sound/voice/vo_battle/vo_battle_pc_mid_ma_en.scd", "pc path");
Check(VoiceSelection.NpcBattleFromId(1).PathFor("ja").StartsWith("sound/voice/vo_battle/vo_npc") &&
      VoiceSelection.NpcBattleFromId(1).PathFor("ja").EndsWith("_battle_ja.scd"), "npc path");
Check(new VoiceSource(VoiceSourceKind.NpcBattle, "", 7).PathFor("en") == "sound/voice/vo_battle/vo_npc07_battle_en.scd", "npc path padding");
Check(VoiceSelection.MonsterFromSePack(3091).PathFor("de") == "sound/battle/mon/3091.scd", "monster path ignores language");
Check(new VoiceSource(VoiceSourceKind.YellLine, "", 1000).PathFor("en") == "sound/voice/vo_line/8201000_en.scd", "yell path");
var have = new HashSet<string> { pc.PathFor("en"), pc.PathFor("ja") };
Check(VoiceSelection.ResolvePath(pc, "de", have.Contains) == pc.PathFor("en"), "German falls back to English");
Check(VoiceSelection.ResolvePath(pc, "ja", have.Contains) == pc.PathFor("ja"), "client language preferred");
Check(VoiceSelection.ResolvePath(pc, "fr", p => p == pc.PathFor("ja")) == pc.PathFor("ja"), "falls back to Japanese last");
Check(VoiceSelection.ResolvePath(pc, "en", _ => false) == null, "missing file returned");
Check(VoiceSelection.ResolvePath(VoiceSource.None, "en", _ => true) == null, "none resolved to a path");
Check(VoiceSelection.LanguageOrder("en").SequenceEqual(new[] { "en", "ja" }), "language order dedupes");

// ---- Labels: unknown means silent; fallbacks only use labeled entries. ----
var labels = new VoiceLabelTable();
Check(labels.Pick("mon/3091", VoiceEvent.Death, 30, _ => 0) == -1, "unlabeled monster played something");
labels.Set("mon/3091", VoiceLabels.Death, new[] { 7 });
labels.Set("mon/3091", VoiceLabels.Attack, new[] { 2, 3, 99 });
Check(labels.Pick("mon/3091", VoiceEvent.Death, 30, _ => 0) == 7, "labeled death");
Check(labels.Pick("mon/3091", VoiceEvent.Engage, 30, _ => 1) == 3, "engage falls back to attack");
Check(labels.Pick("mon/3091", VoiceEvent.Skill, 30, n => n - 1) == 3, "out-of-range entry 99 is skipped");
Check(labels.Pick("mon/3091", VoiceEvent.Hurt, 30, _ => 0) == -1, "hurt guessed from another label");
Check(labels.Pick("mon/1", VoiceEvent.Death, 30, _ => 0) == -1, "labels leaked across packs");
var user = new Dictionary<string, Dictionary<string, List<int>>> { ["mon/3091"] = new() { [VoiceLabels.Death] = new List<int>() } };
labels.Merge(user);
Check(labels.Pick("mon/3091", VoiceEvent.Death, 30, _ => 0) == -1, "user could not un-label a shipped entry");
Check(new VoiceSource(VoiceSourceKind.PcShout, "mid_ma", 0).LabelKey == new VoiceSource(VoiceSourceKind.PcShout, "vie_fc", 0).LabelKey, "PC shouts share one label set");

// ---- Priority order from the design. ----
int P(VoiceEvent e) => VoiceEventPolicy.Priority(e);
Check(P(VoiceEvent.Death) > P(VoiceEvent.Engage) && P(VoiceEvent.Engage) > P(VoiceEvent.LowHp) &&
      P(VoiceEvent.LowHp) > P(VoiceEvent.Skill) && P(VoiceEvent.Skill) > P(VoiceEvent.Hurt) &&
      P(VoiceEvent.Hurt) > P(VoiceEvent.Taunt), "priority order");

// ---- Rate limiting. ----
var settings = new VoiceRateSettings { TokensPerSecond = 1f, Burst = 2f, MaxConcurrent = 2, AssumedVoiceSeconds = 1.5f, SameActorGap = 1f };
var rl = new VoiceRateLimiter(settings);
Check(rl.TryAdmit(1, VoiceEvent.Skill), "first voice refused");
Check(!rl.TryAdmit(1, VoiceEvent.Hurt), "same actor talked over itself");
Check(rl.TryAdmit(2, VoiceEvent.Skill), "second actor refused");
Check(!rl.TryAdmit(3, VoiceEvent.Skill), "token bucket / concurrency not enforced");
Check(!rl.TryAdmit(3, VoiceEvent.Death), "critical event exceeded concurrency");
rl.Advance(1.6f);
Check(rl.ActiveVoices == 0, "voices did not expire");
Check(rl.TryAdmit(3, VoiceEvent.Death), "critical event refused with an empty bucket");
Check(!rl.TryAdmit(4, VoiceEvent.Taunt), "bucket empty but taunt admitted");
rl.Advance(1.1f);
Check(!rl.TryAdmit(1, VoiceEvent.Skill), "per-event cooldown ignored");
Check(rl.TryAdmit(1, VoiceEvent.Hurt), "other event of a rested actor refused");
// Sustained flood: 10 actors, every frame, 60s -> bounded by ~rate * time + burst.
var flood = new VoiceRateLimiter(new VoiceRateSettings { TokensPerSecond = 1.5f, Burst = 2f, MaxConcurrent = 3 });
var admitted = 0;
for (var frame = 0; frame < 60 * 60; frame++)
{
    flood.Advance(1f / 60f);
    for (ulong a = 0; a < 10; a++)
        if (flood.TryAdmit(a, frame % 2 == 0 ? VoiceEvent.Hurt : VoiceEvent.Taunt)) admitted++;
}
Check(admitted <= 1.5f * 60 + 2 && admitted > 20, $"flood admitted {admitted}");
// Cooldown-only spend blocks retries without using budget.
var spend = new VoiceRateLimiter(settings);
spend.SpendCooldownOnly(9, VoiceEvent.Skill);
Check(spend.IsOnCooldown(9, VoiceEvent.Skill) && spend.Tokens == settings.Burst && spend.ActiveVoices == 0, "cooldown-only consumed budget");
spend.Forget(9);
Check(!spend.IsOnCooldown(9, VoiceEvent.Skill), "forget kept the old actor's cooldowns");

// ---- Arbitration: closest N, one per actor, priority then distance. ----
var closest = VoiceRateLimiter.Closest(new[] { (1ul, 10f), (2ul, 3f), (3ul, 30f), (4ul, 5f) }, 2);
Check(closest.SetEquals(new[] { 2ul, 4ul }), "closest set");
var order = VoiceRateLimiter.Arbitrate(new[]
{
    new VoiceCandidate(2, VoiceEvent.Hurt, 3f),
    new VoiceCandidate(2, VoiceEvent.Death, 3f),
    new VoiceCandidate(4, VoiceEvent.Skill, 5f),
    new VoiceCandidate(3, VoiceEvent.Death, 30f),
}, closest);
Check(order.Count == 2 && order[0].ActorKey == 2 && order[0].Event == VoiceEvent.Death && order[1].Event == VoiceEvent.Skill, "arbitration order");
var tie = VoiceRateLimiter.Arbitrate(new[] { new VoiceCandidate(1, VoiceEvent.Skill, 9f), new VoiceCandidate(2, VoiceEvent.Skill, 2f) }, new HashSet<ulong> { 1, 2 });
Check(tie[0].ActorKey == 2, "nearer actor not preferred on a tie");

// ---- Transition watch. ----
var events = new List<VoiceEvent>();
var w = new VoiceActorWatch();
w.Observe(new VoiceActorSnapshot(true, true, false, 1000, 1000), events);
Check(events.Count == 0, "first observation raised events (mid-fight enable)");
w = new VoiceActorWatch();
w.Observe(new VoiceActorSnapshot(true, false, false, 1000, 1000), events);
w.Observe(new VoiceActorSnapshot(true, true, false, 1000, 1000), events);
Check(events.SequenceEqual(new[] { VoiceEvent.Engage }), "engage");
events.Clear();
w.Observe(new VoiceActorSnapshot(true, true, true, 1000, 1000), events);
Check(events.SequenceEqual(new[] { VoiceEvent.Skill }), "cast start");
events.Clear();
w.Observe(new VoiceActorSnapshot(true, true, true, 980, 1000), events);
Check(events.Count == 0, "small hit counted as heavy");
w.Observe(new VoiceActorSnapshot(true, true, false, 800, 1000), events);
Check(events.SequenceEqual(new[] { VoiceEvent.Hurt }), "heavy hit");
events.Clear();
w.Observe(new VoiceActorSnapshot(true, true, false, 290, 1000), events);
Check(events.SequenceEqual(new[] { VoiceEvent.LowHp }), "low HP replaces the heavy-hit voice on the crossing frame");
events.Clear();
w.Observe(new VoiceActorSnapshot(true, true, false, 280, 1000), events);
w.Observe(new VoiceActorSnapshot(true, true, false, 1000, 1000), events);
w.Observe(new VoiceActorSnapshot(true, true, false, 400, 1000), events);
w.Observe(new VoiceActorSnapshot(true, true, false, 200, 1000), events);
Check(events.Count(e => e == VoiceEvent.LowHp) == 1, "a heal to full (combat reset) must re-arm low HP exactly once");
events.Clear();
w.Observe(new VoiceActorSnapshot(false, false, false, 0, 1000), events);
Check(events.SequenceEqual(new[] { VoiceEvent.Death }), "death raised anything else");
events.Clear();
w.Observe(new VoiceActorSnapshot(false, false, false, 0, 1000), events);
Check(events.Count == 0, "death raised twice");


// ---- Curated voiced lines: the shipped pool parses and stays inside what was proven offline. ----
var poolPath = Path.Combine(AppContext.BaseDirectory, "VoiceLines.json");
using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(poolPath)))
{
    var lines = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();
    Check(lines.Count >= 150 && lines.Count <= 1200, $"pool size {lines.Count} outside 150..1200");
    // Human enemies use the whole pool minus "fx" (studio-processed boss/primal voices). "extended" is
    // provenance only and filters nothing.
    bool Flag(System.Text.Json.JsonElement l, string name) => l.TryGetProperty(name, out var x) && x.GetBoolean();
    var fxCount = lines.Count(l => Flag(l, "fx"));
    var humanCount = lines.Count - fxCount;
    Check(fxCount > 0, "no line is marked fx");
    Check(humanCount >= 300, $"pool for human speakers only {humanCount} lines after fx");
    Check(lines.Count(l => Flag(l, "extended") && !Flag(l, "fx")) > 0, "no extended line left for human speakers");
    // Owner's ear-checked boss/primal examples must be fx; the anchor smoking-crater line must not.
    foreach (var v in new uint[] { 8203663, 8203653, 8205955, 8206395, 8206378, 8206393, 8203358 })
        Check(lines.Any(l => l.GetProperty("voice").GetUInt32() == v && Flag(l, "fx")), $"boss line {v} not marked fx");
    Check(!lines.Any(l => l.GetProperty("voice").GetUInt32() == 8202037 && Flag(l, "fx")), "anchor 8202037 marked fx");
    foreach (var gender in new[] { "male", "female" })
        foreach (var ev in new[] { "Engage", "KilledPlayer", "Victory", "Death" })
        {
            var n = lines.Count(l => !Flag(l, "fx") && l.GetProperty("gender").GetString() == gender &&
                                     l.GetProperty("events").EnumerateArray().Any(e => e.GetString() == ev));
            Check(n >= 3, $"human pool has only {n} {gender} {ev} lines after fx");
        }
    var voices = new HashSet<uint>();
    var yells = new HashSet<string>();
    var perEvent = new Dictionary<string, int>();
    foreach (var l in lines)
    {
        var voice = l.GetProperty("voice").GetUInt32();
        var yell = l.GetProperty("yell").GetUInt32();
        var sheetName = l.TryGetProperty("sheet", out var sh) ? sh.GetString() : null;
        var sheet = VoiceLineEntry.ParseSheet(sheetName);
        Check(sheet != null, $"voice {voice}: unknown text sheet {sheetName}");
        Check(voices.Add(voice), $"voice {voice} listed twice");
        // One text row is one recorded line: two files claiming the same row means one pair is wrong.
        Check(yells.Add($"{sheet}:{yell}"), $"{sheet} row {yell} claimed by two voice files");
        Check(voice >= 8201000 && voice < 8300000, $"voice {voice} outside the vo_line range");
        Check(yell > 0 && yell < (sheet == VoiceLineTextSheet.NpcYell ? 20000u : 50000u), $"row {yell} outside {sheet}");
        if (sheet == VoiceLineTextSheet.NpcYell)
            Check(yell != voice - VoiceSelection.YellLineBase, $"voice {voice} uses the disproven 8200000+row mapping");
        var gender = l.GetProperty("gender").GetString();
        Check(gender is "male" or "female", $"voice {voice}: gender {gender}");
        Check(l.TryGetProperty("age", out var age) && age.GetString() is "young" or "adult" or "elderly", $"voice {voice}: age missing or unknown");
        foreach (var e in l.GetProperty("events").EnumerateArray())
        {
            Check(Enum.TryParse<VoiceEvent>(e.GetString(), out _), $"voice {voice}: unknown event {e.GetString()}");
            perEvent[e.GetString()!] = perEvent.GetValueOrDefault(e.GetString()!) + 1;
        }
    }
    // The owner's in-game anchor: this file says "...nothing left of you but a smoking crater" (NpcYell 7768), female.
    Check(lines.Any(l => l.GetProperty("voice").GetUInt32() == 8202037 && l.GetProperty("yell").GetUInt32() == 7768 &&
                         l.GetProperty("gender").GetString() == "female"), "anchor line 8202037 -> 7768 (female) missing");
    foreach (var e in Enum.GetValues<VoiceEvent>())
        Check(perEvent.GetValueOrDefault(e.ToString()) >= 5, $"event {e} has fewer than 5 lines");
}

VoiceLineEntry L(uint v, VoiceLineVoice g, params VoiceEvent[] ev) => new(v, v - 8200000 + 5731, ev, g, false, 1f);
var lineSettings = new VoiceLineSettings { Gap = 3.5f, PriorityGap = 0.5f, ActorCooldown = 8f, RecentMemory = 2 };
var picker = new VoiceLinePicker(lineSettings);
picker.SetEntries(new[]
{
    L(8202001, VoiceLineVoice.Male, VoiceEvent.Skill), L(8202002, VoiceLineVoice.Male, VoiceEvent.Skill),
    L(8202003, VoiceLineVoice.Male, VoiceEvent.Skill), L(8202004, VoiceLineVoice.Female, VoiceEvent.Skill),
    L(8202005, VoiceLineVoice.Male, VoiceEvent.Taunt), L(8202006, VoiceLineVoice.Female, VoiceEvent.KilledPlayer),
    new VoiceLineEntry(8202007, 1, Array.Empty<VoiceEvent>(), VoiceLineVoice.Male, false, 1f),
});
Check(picker.Count == 6, "an entry without events was kept");
var rnd = new Random(3);
// Gender filter: a female speaker never gets a male line, monsters get nothing.
for (var i = 0; i < 50; i++)
    Check(picker.Pick(VoiceEvent.Skill, VoiceLineVoice.Female, rnd.NextDouble)?.VoiceId == 8202004, "female skill line");
Check(picker.Pick(VoiceEvent.Skill, VoiceLineVoice.NonHuman, rnd.NextDouble) == null, "monster got a human line");
Check(picker.Available(VoiceEvent.Engage, VoiceLineVoice.Male) == 1, "engage borrows taunt lines");
Check(picker.Pick(VoiceEvent.Victory, VoiceLineVoice.Female, rnd.NextDouble)?.VoiceId == 8202006, "victory borrows kill lines");
Check(picker.Pick(VoiceEvent.Death, VoiceLineVoice.Male, rnd.NextDouble) == null, "no death line must mean silence, not another category");

// Processed (fx) lines are never given to a human speaker, even when they are the only fit.
{
    var fxPicker = new VoiceLinePicker(new VoiceLineSettings());
    fxPicker.SetEntries(new[]
    {
        new VoiceLineEntry(8203663, 1, new[] { VoiceEvent.Taunt }, VoiceLineVoice.Male, false, 1f, Fx: true),
        new VoiceLineEntry(8203664, 2, new[] { VoiceEvent.Taunt }, VoiceLineVoice.Male, false, 1f, Extended: true),
        new VoiceLineEntry(8203665, 3, new[] { VoiceEvent.Death }, VoiceLineVoice.Female, false, 1f, Fx: true),
    });
    for (var i = 0; i < 50; i++)
        Check(fxPicker.Pick(VoiceEvent.Taunt, VoiceLineVoice.Male, rnd.NextDouble)?.VoiceId == 8203664, "a male speaker got an fx line");
    Check(fxPicker.Available(VoiceEvent.Taunt, VoiceLineVoice.Male) == 1, "fx line counted as available to a human");
    Check(fxPicker.Pick(VoiceEvent.Death, VoiceLineVoice.Female, rnd.NextDouble) == null, "the only female death line is fx: must be silence");
    Check(fxPicker.Count == 3, "fx lines must stay in the pool (kept for future monster/boss speakers)");
}

// The shipped pool as the director loads it: no human pick is ever an fx line, and every outcome
// moment still has a line for both genders.
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(poolPath));
    var entries = new List<VoiceLineEntry>();
    foreach (var l in doc.RootElement.GetProperty("lines").EnumerateArray())
    {
        var evs = l.GetProperty("events").EnumerateArray().Select(e => Enum.Parse<VoiceEvent>(e.GetString()!)).ToArray();
        var g = l.GetProperty("gender").GetString() == "female" ? VoiceLineVoice.Female : VoiceLineVoice.Male;
        entries.Add(new VoiceLineEntry(l.GetProperty("voice").GetUInt32(), l.GetProperty("yell").GetUInt32(), evs, g, false, 1f,
            Extended: l.TryGetProperty("extended", out var x) && x.GetBoolean(), Fx: l.TryGetProperty("fx", out var f) && f.GetBoolean()));
    }
    var shipped = new VoiceLinePicker(new VoiceLineSettings { RecentMemory = 0 });
    shipped.SetEntries(entries);
    var fxIds = entries.Where(e => e.Fx).Select(e => e.VoiceId).ToHashSet();
    foreach (var g in new[] { VoiceLineVoice.Male, VoiceLineVoice.Female })
        foreach (var ev in Enum.GetValues<VoiceEvent>())
            for (var i = 0; i < 200; i++)
            {
                var pick = shipped.Pick(ev, g, rnd.NextDouble);
                Check(pick == null || !fxIds.Contains(pick.VoiceId), $"shipped pool gave fx line {pick?.VoiceId} to a {g} speaker");
            }
    foreach (var g in new[] { VoiceLineVoice.Male, VoiceLineVoice.Female })
        foreach (var ev in new[] { VoiceEvent.Engage, VoiceEvent.KilledPlayer, VoiceEvent.Victory, VoiceEvent.Death })
            Check(shipped.Available(ev, g) > 0, $"shipped pool: no {g} {ev} line for human speakers");
}

// One line at a time, a quiet gap after it, priority moments sooner.
Check(picker.CanSpeak(1, VoiceEvent.Skill), "fresh picker refused");
var first = picker.Pick(VoiceEvent.Skill, VoiceLineVoice.Male, rnd.NextDouble)!;
picker.Commit(1, VoiceEvent.Skill, first, 2f);
Check(picker.IsBusy && !picker.CanSpeak(2, VoiceEvent.Death), "a second line started while one is on screen");
picker.Advance(2.6f);
Check(!picker.CanSpeak(2, VoiceEvent.Taunt), "ordinary event ignored the quiet gap");
Check(picker.CanSpeak(2, VoiceEvent.Death), "death waited the ordinary gap");
picker.Advance(3.0f);
Check(picker.CanSpeak(2, VoiceEvent.Taunt), "gap never ends");
Check(!picker.CanSpeak(2, VoiceEvent.Skill), "skill event cooldown (8 s, across enemies) ignored");
Check(!picker.CanSpeak(1, VoiceEvent.Taunt), "per-enemy cooldown ignored");
picker.Advance(3f);
Check(picker.CanSpeak(1, VoiceEvent.Skill), "cooldowns never end");

// No immediate repeats: three male skill lines and a memory of two never repeat the previous one.
var picker2 = new VoiceLinePicker(lineSettings);
picker2.SetEntries(picker.Entries);
uint previous = 0;
for (var i = 0; i < 30; i++)
{
    var line = picker2.Pick(VoiceEvent.Skill, VoiceLineVoice.Male, rnd.NextDouble)!;
    Check(line.VoiceId != previous, "same line twice in a row");
    previous = line.VoiceId;
    picker2.Commit((ulong)i, VoiceEvent.Skill, line, 1f);
    picker2.Advance(30f);
}
// With only one candidate the recent memory gives way instead of going silent.
var lone = picker2.Pick(VoiceEvent.KilledPlayer, VoiceLineVoice.Female, rnd.NextDouble)!;
picker2.Commit(99, VoiceEvent.KilledPlayer, lone, 1f);
picker2.Advance(30f);
Check(picker2.Pick(VoiceEvent.KilledPlayer, VoiceLineVoice.Female, rnd.NextDouble)?.VoiceId == lone.VoiceId, "single line blocked by recent memory");
picker2.MarkUnavailable(lone.VoiceId);
Check(picker2.Pick(VoiceEvent.KilledPlayer, VoiceLineVoice.Female, rnd.NextDouble) == null, "an unavailable line was offered");

// Long-run budget: six enemies asking every frame for a minute get a handful of taunts, not a wall.
var picker3 = new VoiceLinePicker(new VoiceLineSettings());
picker3.SetEntries(picker.Entries);
var spoken = 0;
for (var frame = 0; frame < 60 * 60; frame++)
{
    picker3.Advance(1f / 60f);
    var actor = (ulong)(frame % 6);
    if (!picker3.CanSpeak(actor, VoiceEvent.Taunt))
        continue;
    var line = picker3.Pick(VoiceEvent.Taunt, VoiceLineVoice.Male, rnd.NextDouble);
    if (line == null)
        continue;
    picker3.Commit(actor, VoiceEvent.Taunt, line, 2f);
    spoken++;
}
Check(spoken >= 3 && spoken <= 6, $"taunt flood produced {spoken} lines in 60 s");
Check(VoiceLinePicker.IsPriority(VoiceEvent.Victory) && !VoiceLinePicker.IsPriority(VoiceEvent.Skill), "priority set");

// Age preference: a matching (or age-less) line first, any line of the event as the fallback.
Check(VoiceLineEntry.AgeFromBodyType(1) == VoiceLineAge.Adult && VoiceLineEntry.AgeFromBodyType(3) == VoiceLineAge.Elderly &&
      VoiceLineEntry.AgeFromBodyType(4) == VoiceLineAge.Young && VoiceLineEntry.AgeFromBodyType(0) == VoiceLineAge.Any, "body type -> age");
Check(VoiceLineEntry.ParseAge("Elderly") == VoiceLineAge.Elderly && VoiceLineEntry.ParseAge(null) == VoiceLineAge.Any &&
      VoiceLineEntry.ParseAge("ancient") == VoiceLineAge.Any, "age parse");
VoiceLineEntry A(uint v, VoiceLineAge age, VoiceEvent ev) => new(v, v - 8200000 + 5731, new[] { ev }, VoiceLineVoice.Male, false, 1f, age);
var aged = new VoiceLinePicker(new VoiceLineSettings { RecentMemory = 0 });
aged.SetEntries(new[]
{
    A(8202101, VoiceLineAge.Adult, VoiceEvent.Skill), A(8202102, VoiceLineAge.Adult, VoiceEvent.Skill),
    A(8202103, VoiceLineAge.Elderly, VoiceEvent.Skill), A(8202104, VoiceLineAge.Any, VoiceEvent.Taunt),
    A(8202105, VoiceLineAge.Elderly, VoiceEvent.Taunt), A(8202106, VoiceLineAge.Adult, VoiceEvent.Death),
});
for (var i = 0; i < 40; i++)
{
    Check(aged.Pick(VoiceEvent.Skill, VoiceLineVoice.Male, rnd.NextDouble, VoiceLineAge.Elderly)?.VoiceId == 8202103, "elderly speaker got an adult skill line");
    Check(aged.Pick(VoiceEvent.Skill, VoiceLineVoice.Male, rnd.NextDouble, VoiceLineAge.Adult)?.VoiceId != 8202103, "adult speaker got the elderly line");
    // An age-less line suits everyone: the adult speaker's taunt pool is that line alone.
    Check(aged.Pick(VoiceEvent.Taunt, VoiceLineVoice.Male, rnd.NextDouble, VoiceLineAge.Adult)?.VoiceId == 8202104, "adult taunt");
    // No young line exists: the young speaker still talks.
    Check(aged.Pick(VoiceEvent.Death, VoiceLineVoice.Male, rnd.NextDouble, VoiceLineAge.Young)?.VoiceId == 8202106, "age silenced a speaker");
}
// Without a speaker age every line of the event is in play, as before.
var seenAny = new HashSet<uint>();
for (var i = 0; i < 200; i++)
    seenAny.Add(aged.Pick(VoiceEvent.Skill, VoiceLineVoice.Male, rnd.NextDouble)!.VoiceId);
Check(seenAny.SetEquals(new uint[] { 8202101, 8202102, 8202103 }), "age-less pick narrowed the pool");
// A fresh other-age line beats repeating the only matching one.
var agedRecent = new VoiceLinePicker(new VoiceLineSettings { RecentMemory = 4 });
agedRecent.SetEntries(aged.Entries);
agedRecent.Commit(1, VoiceEvent.Skill, aged.Entries[2], 1f);
Check(agedRecent.Pick(VoiceEvent.Skill, VoiceLineVoice.Male, rnd.NextDouble, VoiceLineAge.Elderly)?.VoiceId != 8202103, "recent elderly line repeated over a fresh one");

// SCD header duration: a synthetic MS-ADPCM header (block 70, 128 samples per block, 44100 Hz).
var scd = new byte[0x200];
"SEDB"u8.CopyTo(scd);
BitConverter.GetBytes((ushort)0x30).CopyTo(scd, 0x0E);
BitConverter.GetBytes(0x70).CopyTo(scd, 0x30 + 0x0C);
BitConverter.GetBytes(0x100).CopyTo(scd, 0x70);
BitConverter.GetBytes(70 * 345).CopyTo(scd, 0x100);
BitConverter.GetBytes(1).CopyTo(scd, 0x104);
BitConverter.GetBytes(44100).CopyTo(scd, 0x108);
BitConverter.GetBytes(0x0C).CopyTo(scd, 0x10C);
BitConverter.GetBytes((ushort)70).CopyTo(scd, 0x12C);
BitConverter.GetBytes((ushort)128).CopyTo(scd, 0x132);
Check(VoiceLineAudio.TryReadDuration(scd, out var secs) && Math.Abs(secs - 345 * 128 / 44100f) < 1e-3, $"adpcm duration {secs}");
Check(!VoiceLineAudio.TryReadDuration(new byte[0x20], out _), "short file read as audio");
Check(!VoiceLineAudio.TryReadDuration(new byte[0x200], out _), "zero file read as audio");
BitConverter.GetBytes(0x06).CopyTo(scd, 0x10C);
Check(!VoiceLineAudio.TryReadDuration(scd, out _), "unknown codec read as audio");
Check(VoiceLineAudio.BubbleSeconds(0.5f) == 2.5f && VoiceLineAudio.BubbleSeconds(30f) == 7f, "bubble clamp");

// ---- Speaker personas: one enemy, one voice actor. ----
{
    var table = VoicePersonaTable.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VoicePersonas.json")));
    Check(table.Count >= 100, $"persona table has only {table.Count} rows");
    int AssignableIn(string lang) => table.All.Count(p => p.Assignable && !p.IsBoss && p.Language == lang);
    Check(AssignableIn("ja") == 23 && AssignableIn("en") == 23, $"assignable personas ja {AssignableIn("ja")} en {AssignableIn("en")}");
    Check(table.Get("ja-010")?.Character == "Alphinaud" && table.Get("ja-010")!.Voice == VoiceLineVoice.Male &&
          table.Get("ja-010")!.Band == VoiceLineAge.Young, "ja-010 is young male Alphinaud");
    Check(table.Get("en-004")?.BandName == "mature" && table.Get("en-003")?.BandName == "adult", "band names");
    Check(VoicePersonaTable.PersonaLanguage("de") == null && VoicePersonaTable.PersonaLanguage("fr") == null &&
          VoicePersonaTable.PersonaLanguage("en") == "en", "only ja/en have personas");

    // Fallback chains: itself first, then assignable personas of the same language and gender, same band first.
    foreach (var p in table.All.Where(p => p.Assignable))
        foreach (var allowStory in new[] { true, false })
        {
            if (!allowStory && p.IsStoryCharacter)
                continue;
            var chain = table.ChainFor(p, allowStory);
            Check(chain[0] == p.Id && chain.Distinct().Count() == chain.Count, $"{p.Id}: chain must start with itself, no repeats");
            var members = chain.Select(id => table.Get(id)!).ToList();
            // Boss personas are for non-human speakers: their chain stays among boss voices of any gender.
            Check(members.All(m => m.Assignable && m.SpeakerVoice == p.SpeakerVoice && m.Language == p.Language), $"{p.Id}: chain crosses gender/kind/language or uses a small persona");
            Check(allowStory || members.All(m => !m.IsStoryCharacter), $"{p.Id}: story character in a chain with story characters off");
            var firstOther = members.FindIndex(m => m.Band != p.Band);
            Check(firstOther < 0 || members.Skip(firstOther).All(m => m.Band != p.Band), $"{p.Id}: same-band fallback after another band");
        }

    // The shipped pool with its persona ids, as the director loads it.
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(poolPath));
    var entries = new List<VoiceLineEntry>();
    string? S(System.Text.Json.JsonElement l, string name) => l.TryGetProperty(name, out var x) ? x.GetString() : null;
    foreach (var l in doc.RootElement.GetProperty("lines").EnumerateArray())
    {
        var evs = l.GetProperty("events").EnumerateArray().Select(e => Enum.Parse<VoiceEvent>(e.GetString()!)).ToArray();
        var g = l.GetProperty("gender").GetString() == "female" ? VoiceLineVoice.Female : VoiceLineVoice.Male;
        entries.Add(new VoiceLineEntry(l.GetProperty("voice").GetUInt32(), l.GetProperty("yell").GetUInt32(), evs, g, false, 1f,
            VoiceLineEntry.ParseAge(S(l, "age")), Fx: l.TryGetProperty("fx", out var f) && f.GetBoolean(), PersonaJa: S(l, "pja"), PersonaEn: S(l, "pen")));
    }
    Check(entries.Count(e => e.PersonaJa != null) > 350 && entries.Count(e => e.PersonaEn != null) > 350, "pool lines lack persona ids");
    foreach (var e in entries)
    {
        foreach (var (lang, id) in new[] { ("ja", e.PersonaJa), ("en", e.PersonaEn) })
        {
            if (id == null)
                continue;
            var p = table.Get(id);
            Check(p != null && p.Language == lang, $"vo {e.VoiceId}: persona {id} unknown or of another language");
            Check(e.Fx || !p!.Assignable || p.Voice == e.Voice,$"vo {e.VoiceId}: persona {id} is {p!.Voice}, line is {e.Voice}");
        }
    }
    var shipped = new VoiceLinePicker(new VoiceLineSettings { RecentMemory = 24 });
    shipped.SetEntries(entries);
    var fxIds = entries.Where(e => e.Fx).Select(e => e.VoiceId).ToHashSet();

    // Every gender x body age, every candidate persona: every event is reachable through its chain, and the
    // persona that answers is in the chain, assignable, and never an fx line.
    var bodies = new[] { VoiceLineAge.Young, VoiceLineAge.Adult, VoiceLineAge.Elderly, VoiceLineAge.Any };
    var coverage = new List<string>();
    foreach (var lang in new[] { "ja", "en" })
        foreach (var g in new[] { VoiceLineVoice.Male, VoiceLineVoice.Female })
            foreach (var body in bodies)
            {
                var candidates = table.Candidates(lang, g, body, true);
                Check(candidates.Count > 0, $"{lang} {g} {body}: no persona");
                if (body == VoiceLineAge.Elderly)
                    Check(candidates.All(c => c.Band == (g == VoiceLineVoice.Male ? VoiceLineAge.Elderly : VoiceLineAge.Adult)), $"{lang} {g} elderly body band");
                if (body == VoiceLineAge.Young)
                    Check(candidates.All(c => c.Band == VoiceLineAge.Young), $"{lang} {g} child body band");
                if (body == VoiceLineAge.Adult)
                    Check(candidates.Any(c => c.Band == VoiceLineAge.Young) && candidates.Any(c => c.Band == VoiceLineAge.Adult) &&
                          candidates.All(c => c.Band != VoiceLineAge.Elderly), $"{lang} {g} adult body spreads over young and adult");
                var perEventLines = new List<string>();
                foreach (var ev in Enum.GetValues<VoiceEvent>())
                {
                    var own = 0.0;
                    var answering = 0.0;
                    foreach (var c in candidates)
                    {
                        var chain = table.ChainFor(c, true);
                        Check(shipped.Available(ev, g, chain, lang) > 0, $"{lang} {c.Id} ({g} {body}): {ev} unreachable");
                        // How many lines the persona that actually answers has for this event (event chain included).
                        var who = chain.First(p => VoiceLinePicker.Chain(ev).Any(ce => shipped.CountFor(ce, g, p, lang) > 0));
                        answering += VoiceLinePicker.Chain(ev).Sum(ce => shipped.CountFor(ce, g, who, lang));
                        if (who == c.Id)
                            own++;
                        for (var i = 0; i < 20; i++)
                        {
                            var pick = shipped.Pick(ev, g, chain, lang, rnd.NextDouble);
                            Check(pick != null && !fxIds.Contains(pick.VoiceId), $"{lang} {c.Id}: {ev} gave {pick?.VoiceId}");
                            Check(pick!.PersonaFor(lang) == who, $"{lang} {c.Id}: {ev} skipped to {pick.PersonaFor(lang)} instead of {who}");
                        }
                    }
                    perEventLines.Add($"{ev} {answering / candidates.Count:0.0}{(own < candidates.Count ? $" ({candidates.Count - own} borrow)" : "")}");
                }
                if (body != VoiceLineAge.Any)
                    coverage.Add($"  {lang} {g} {body} ({candidates.Count} personas): {string.Join(", ", perEventLines)}");
            }

    // Assignment: stable for one identity, spread across concurrent enemies, varied across identities.
    var assigner = new VoicePersonaAssigner();
    assigner.Configure(table, "en", true);
    Check(assigner.Active && assigner.Language == "en", "assigner inactive with data");
    var seed = VoicePersonaAssigner.IdentitySeed(1234, 567, 0x40001234);
    var a1 = assigner.Assign(1, seed, VoiceLineVoice.Male, VoiceLineAge.Adult);
    Check(a1.Persona != null && a1.Chain[0] == a1.Persona.Id, "adult male got no persona");
    Check(assigner.Assign(1, seed + 1, VoiceLineVoice.Female, VoiceLineAge.Young) == a1, "an assigned enemy changed persona");
    var fresh = new VoicePersonaAssigner();
    fresh.Configure(table, "en", true);
    Check(fresh.Assign(9, seed, VoiceLineVoice.Male, VoiceLineAge.Adult).Persona == a1.Persona, "same identity, different persona");
    assigner.Clear();
    assigner.Assign(50, seed + 77, VoiceLineVoice.Male, VoiceLineAge.Adult);
    var again = assigner.Assign(51, seed, VoiceLineVoice.Male, VoiceLineAge.Adult);
    Check(again.Persona == a1.Persona || assigner.Get(50)!.Persona == a1.Persona, "identity lost its persona after a reset");
    // Concurrent adult men: all different until the candidates run out.
    var crowd = new VoicePersonaAssigner();
    crowd.Configure(table, "ja", true);
    var adultMale = table.Candidates("ja", VoiceLineVoice.Male, VoiceLineAge.Adult, true).Count;
    var held = new List<string>();
    for (uint i = 0; i < adultMale; i++)
        held.Add(crowd.Assign(100 + i, VoicePersonaAssigner.IdentitySeed(4000, 300, 0x40000000 + i), VoiceLineVoice.Male, VoiceLineAge.Adult).Persona!.Id);
    Check(held.Distinct().Count() == adultMale, $"{adultMale} concurrent adult men share personas: {string.Join(",", held)}");
    var extra = crowd.Assign(999, VoicePersonaAssigner.IdentitySeed(4000, 300, 0x40009999), VoiceLineVoice.Male, VoiceLineAge.Adult);
    Check(extra.Persona != null, "a full field left an enemy without a persona");
    // Independent identities (no spreading): usually different.
    var differ = 0;
    for (uint i = 0; i < 400; i++)
    {
        var x = new VoicePersonaAssigner(); x.Configure(table, "en", true);
        var y = new VoicePersonaAssigner(); y.Configure(table, "en", true);
        if (x.Assign(1, VoicePersonaAssigner.IdentitySeed(i, 7, 0x40000000 + i), VoiceLineVoice.Male, VoiceLineAge.Adult).Persona !=
            y.Assign(1, VoicePersonaAssigner.IdentitySeed(i + 1000, 8, 0x40100000 + i), VoiceLineVoice.Male, VoiceLineAge.Adult).Persona)
            differ++;
    }
    Check(differ >= 300, $"two independent adult men differ only {differ}/400 times");
    // The hash reaches every candidate.
    var reached = new HashSet<string>();
    for (uint i = 0; i < 500; i++)
    {
        var x = new VoicePersonaAssigner(); x.Configure(table, "ja", true);
        reached.Add(x.Assign(1, VoicePersonaAssigner.IdentitySeed(i, i * 3, i * 7), VoiceLineVoice.Female, VoiceLineAge.Adult).Persona!.Id);
    }
    Check(reached.Count == table.Candidates("ja", VoiceLineVoice.Female, VoiceLineAge.Adult, true).Count, "assignment never reaches some personas");
    Check(assigner.Assign(77, seed, VoiceLineVoice.NonHuman, VoiceLineAge.Any).Persona is { IsBoss: true }, "a monster did not get a boss persona");

    // Story characters off: only unnamed personas (English women now have unnamed ones too).
    var noStory = new VoicePersonaAssigner();
    noStory.Configure(table, "en", false);
    var plain = noStory.Assign(1, seed, VoiceLineVoice.Male, VoiceLineAge.Adult);
    Check(plain.Persona != null && plain.Chain.All(id => !table.Get(id)!.IsStoryCharacter), "story character assigned with the flag off");
    var plainF = noStory.Assign(2, seed, VoiceLineVoice.Female, VoiceLineAge.Adult);
    Check(plainF.Persona != null && plainF.Chain.All(id => !table.Get(id)!.IsStoryCharacter), "en female without story characters");

    // No persona data, or a language without personas: inactive, and the per-line picker still works.
    foreach (var (data, lang) in new[] { (VoicePersonaTable.Empty, "en"), (table, "de"), (table, "fr") })
    {
        var off = new VoicePersonaAssigner();
        off.Configure(data, lang, true);
        Check(!off.Active && off.Language == null, $"personas active for {lang} without data");
        Check(off.Assign(1, seed, VoiceLineVoice.Male, VoiceLineAge.Adult) == VoicePersonaAssignment.None, "inactive assigner assigned");
    }
    var switching = new VoicePersonaAssigner();
    switching.Configure(table, "en", true);
    switching.Assign(1, seed, VoiceLineVoice.Male, VoiceLineAge.Adult);
    switching.Configure(table, "de", true);
    Check(switching.Get(1) == null && !switching.Active, "language change kept English personas");
    var bare = new VoiceLinePicker(new VoiceLineSettings());
    bare.SetEntries(entries.Select(e => e with { PersonaJa = null, PersonaEn = null }));
    Check(bare.Pick(VoiceEvent.KilledPlayer, VoiceLineVoice.Male, rnd.NextDouble) is { Fx: false }, "per-line pick without persona ids");
    Check(bare.Available(VoiceEvent.Taunt, VoiceLineVoice.Male, new[] { "en-004" }, "en") == 0, "persona pick on lines without ids");
    var thrown = false;
    try { VoicePersonaTable.Parse("{ \"nope\": 1 }"); } catch (Exception) { thrown = true; }
    Check(thrown, "malformed persona file parsed silently (the director must see it fail and fall back)");
    // Pool lines in small (non-assignable) personas never reach an assigned speaker.
    var smallIds = table.All.Where(p => !p.Assignable).Select(p => p.Id).ToHashSet();
    foreach (var lang in new[] { "ja", "en" })
        foreach (var p in table.All.Where(p => p.Assignable && p.Language == lang))
            foreach (var ev in Enum.GetValues<VoiceEvent>())
                for (var i = 0; i < 10; i++)
                    // A boss chain without a line for the event (AllyDeath is rare there) stays silent, never a small persona.
                    Check(shipped.Pick(ev, p.SpeakerVoice, table.ChainFor(p, true), lang, rnd.NextDouble)?.PersonaFor(lang) is { } got ? !smallIds.Contains(got)
                            : p.IsBoss && shipped.Available(ev, p.SpeakerVoice, table.ChainFor(p, true), lang) == 0,
                        $"{p.Id}: {ev} used a small persona's line");

    Console.WriteLine("Persona coverage (avg lines of the answering persona per event; borrow = candidates using a fallback):");
    foreach (var c in coverage)
        Console.WriteLine(c);

    // ---- Taunts over the fallen: the speaker's own kill + victory + taunt lines, topped up only below 3. ----
    var poolStats = new List<string>();
    foreach (var lang in new[] { "ja", "en" })
    foreach (var bossKind in new[] { false, true })
    {
        var ownSizes = new List<int>();
        var poolSizes = new List<int>();
        foreach (var p in table.All.Where(p => p.Assignable && p.Language == lang && p.IsBoss == bossKind))
        {
            var speaker = p.SpeakerVoice;
            var chain = table.ChainFor(p, true);
            var own = entries.Count(l => l.PersonaFor(lang) == p.Id && VoiceLinePicker.SuitsSpeaker(l, speaker) &&
                                         VoiceLinePicker.FallenTauntEvents.Any(ev => l.Events.Contains(ev)));
            var pool = shipped.FallenTauntPool(speaker, chain, lang);
            ownSizes.Add(own);
            poolSizes.Add(pool.Count);
            Check(pool.Count > 0, $"{p.Id}: nothing to say over the fallen");
            Check(pool.Count(l => l.PersonaFor(lang) == p.Id) == own, $"{p.Id}: fallen pool misses own lines");
            Check(pool.All(l => l.Events.Any(ev => VoiceLinePicker.FallenTauntEvents.Contains(ev))), $"{p.Id}: fallen pool has a non-taunt line");
            Check(pool.All(l => VoiceLinePicker.SuitsSpeaker(l, speaker)), $"{p.Id}: fallen pool crosses kind (fx/human)");
            if (own >= VoiceLinePicker.MinFallenTauntPool)
                Check(pool.All(l => l.PersonaFor(lang) == p.Id), $"{p.Id}: {own} own lines but the voice changed");
            else
                Check(pool.Count >= VoiceLinePicker.MinFallenTauntPool || pool.Count == entries.Count(l => chain.Contains(l.PersonaFor(lang)!) &&
                          VoiceLinePicker.SuitsSpeaker(l, speaker) && l.Events.Any(ev => VoiceLinePicker.FallenTauntEvents.Contains(ev))),
                    $"{p.Id}: small own pool ({own}) not topped up from fallbacks");
            Check(pool.All(l => chain.Contains(l.PersonaFor(lang)!)), $"{p.Id}: fallen pool left the persona chain");
            if (!p.IsBoss)
                Check(pool.All(l => table.Get(l.PersonaFor(lang))!.Voice == p.Voice), $"{p.Id}: fallen pool crosses gender");
            // Variety: 30 taunts in a row use the whole pool and never repeat the previous line.
            var taunter = new VoiceLinePicker(new VoiceLineSettings());
            taunter.SetEntries(entries);
            var heard = new HashSet<uint>();
            uint last = 0;
            for (var i = 0; i < 30; i++)
            {
                var line = taunter.PickFallenTaunt(speaker, chain, lang, rnd.NextDouble)!;
                Check(pool.Contains(line), $"{p.Id}: fallen taunt outside its pool");
                Check(pool.Count == 1 || line.VoiceId != last, $"{p.Id}: fallen taunt repeated the previous line");
                last = line.VoiceId;
                heard.Add(line.VoiceId);
                taunter.Commit(1, VoiceEvent.KilledPlayer, line, 2f);
                taunter.Advance(8f);
            }
            Check(heard.Count == Math.Min(pool.Count, 30), $"{p.Id}: 30 fallen taunts used {heard.Count} of {pool.Count} lines");
        }
        foreach (var (kind, sizes) in new[] { ("own", ownSizes), ("pool", poolSizes) })
        {
            sizes.Sort();
            poolStats.Add($"  {lang} {(bossKind ? "boss" : "human")} {kind}: min {sizes[0]}, median {sizes[sizes.Count / 2]}, max {sizes[^1]} ({sizes.Count} personas)");
        }
    }

    // ---- Boss personas: the processed (fx) boss voices, for monsters and demihumans only. ----
    var bosses = table.All.Where(p => p.IsBoss).ToList();
    Check(bosses.Count >= 20 && bosses.All(b => b.Assignable && b.BandName == "boss" && b.SpeakerVoice == VoiceLineVoice.NonHuman),
        $"boss personas: {bosses.Count}");
    foreach (var e in entries)
        foreach (var (lang, id) in new[] { ("ja", e.PersonaJa), ("en", e.PersonaEn) })
            Check((table.Get(id)?.IsBoss == true) == e.Fx, $"vo {e.VoiceId} ({lang}): fx {e.Fx} but persona {id}");
    var fxLine = entries.First(e => e.Fx);
    var plainLine = entries.First(e => !e.Fx);
    Check(VoiceLinePicker.SuitsSpeaker(fxLine, VoiceLineVoice.NonHuman) && !VoiceLinePicker.SuitsSpeaker(fxLine, fxLine.Voice) &&
          !VoiceLinePicker.SuitsSpeaker(plainLine, VoiceLineVoice.NonHuman), "fx lines are for non-humans only");
    var fxEvents = entries.Where(e => e.Fx).SelectMany(e => e.Events).Distinct().ToList();
    var bossCoverage = new List<string>();
    foreach (var lang in new[] { "ja", "en" })
    {
        var langBosses = table.Choices(lang, VoiceLineVoice.NonHuman, true);
        Check(langBosses.Count > 0 && langBosses.All(b => b.IsBoss), $"{lang}: no boss choices");
        Check(table.Candidates(lang, VoiceLineVoice.NonHuman, VoiceLineAge.Any, true).Count == langBosses.Count, $"{lang}: monster candidates are not the boss personas");
        foreach (var g in new[] { VoiceLineVoice.Male, VoiceLineVoice.Female })
            Check(table.Choices(lang, g, true).All(p => !p.IsBoss && p.Voice == g) &&
                  table.Candidates(lang, g, VoiceLineAge.Adult, true).All(p => !p.IsBoss), $"{lang} {g}: boss persona offered to a human");
        foreach (var b in langBosses)
        {
            var chain = table.ChainFor(b, true);
            Check(chain[0] == b.Id && chain.All(id => table.Get(id)!.IsBoss && table.Get(id)!.Language == lang), $"{b.Id}: chain leaves the boss personas");
            Check(shipped.Available(VoiceEvent.KilledPlayer, VoiceLineVoice.NonHuman, chain, lang) > 0, $"{b.Id}: kill chain unreachable");
            foreach (var ev in fxEvents)
            {
                Check(shipped.Available(ev, VoiceLineVoice.NonHuman, chain, lang) > 0, $"{b.Id}: {ev} unreachable");
                for (var i = 0; i < 5; i++)
                    Check(shipped.Pick(ev, VoiceLineVoice.NonHuman, chain, lang, rnd.NextDouble) is { Fx: true } pick && chain.Contains(pick.PersonaFor(lang)!),
                        $"{b.Id}: {ev} gave a non-boss line");
            }
        }
        var perEvent = fxEvents.Select(ev =>
            $"{ev} {langBosses.Count(b => shipped.CountFor(ev, VoiceLineVoice.NonHuman, b.Id, lang) > 0)}/{langBosses.Count}");
        bossCoverage.Add($"  {lang} ({langBosses.Count} boss personas, own lines per event): {string.Join(", ", perEvent)}");
    }
    // Humans never hear fx; monsters only hear fx.
    foreach (var lang in new[] { "ja", "en" })
        foreach (var g in new[] { VoiceLineVoice.Male, VoiceLineVoice.Female })
            for (var i = 0; i < 50; i++)
                Check(shipped.Pick(VoiceEvent.Skill, g, rnd.NextDouble) is { Fx: false }, "human per-line pick gave an fx line");
    // Monsters spread over the boss personas and keep theirs per identity.
    var pack = new VoicePersonaAssigner();
    pack.Configure(table, "en", true);
    var enBosses = table.Choices("en", VoiceLineVoice.NonHuman, true).Count;
    var packHeld = new List<string>();
    for (uint i = 0; i < enBosses; i++)
        packHeld.Add(pack.Assign(200 + i, VoicePersonaAssigner.IdentitySeed(9000, 40, 0x40000000 + i), VoiceLineVoice.NonHuman, VoiceLineAge.Any).Persona!.Id);
    Check(packHeld.Distinct().Count() == enBosses, "concurrent monsters share boss personas");
    var lonely = new VoicePersonaAssigner(); lonely.Configure(table, "en", true);
    Check(lonely.Assign(1, VoicePersonaAssigner.IdentitySeed(9000, 40, 0x40000000), VoiceLineVoice.NonHuman, VoiceLineAge.Any).Persona!.Id == packHeld[0],
        "same monster identity, different boss persona");

    // ---- Hand-picked voices: key, lookup, validation, persistence. ----
    Check(VoicePersonaAssigner.OverrideKey(1234, 567, "Bandit") == "1234:567" && VoicePersonaAssigner.OverrideKey(0, 567, null) == "0:567" &&
          VoicePersonaAssigner.OverrideKey(0, 0, " Bandit ") == "name:Bandit" && VoicePersonaAssigner.OverrideKey(0, 0, "") == null, "override key");
    var enMale = table.Choices("en", VoiceLineVoice.Male, true);
    Check(enMale.Select(p => p.Band).Distinct().Count() == 3, "male choices do not span every band");
    var pickedMale = enMale.Last();
    var chooser = new VoicePersonaAssigner();
    chooser.Configure(table, "en", true);
    Check(chooser.Assign(1, seed, VoiceLineVoice.Male, VoiceLineAge.Young, pickedMale.Id).Persona == pickedMale, "hand-picked voice ignored");
    Check(chooser.Assign(1, seed, VoiceLineVoice.Male, VoiceLineAge.Young, null).Persona == pickedMale, "assignment changed without a Forget");
    chooser.Forget(1);
    Check(chooser.Assign(1, seed, VoiceLineVoice.Male, VoiceLineAge.Young, null).Persona != null, "auto after clearing the choice");
    // Saved in the config, so a new session (fresh assigner) applies it again.
    var nextSession = new VoicePersonaAssigner();
    nextSession.Configure(table, "en", true);
    Check(nextSession.Assign(5, VoicePersonaAssigner.IdentitySeed(1, 2, 3), VoiceLineVoice.Male, VoiceLineAge.Adult, pickedMale.Id).Persona == pickedMale,
        "hand-picked voice lost in a new session");
    // A hand-picked voice is honoured whatever the body: a woman's or a boss voice for a male soldier,
    // a human voice for a monster. Only unknown ids are ignored.
    var femaleId = table.Choices("en", VoiceLineVoice.Female, true)[0].Id;
    var bossId = table.Choices("en", VoiceLineVoice.NonHuman, true)[0].Id;
    Check(table.ResolveChoice(femaleId, "en", VoiceLineVoice.Male, true)?.Id == femaleId &&
          table.ResolveChoice(bossId, "en", VoiceLineVoice.Male, true)?.Id == bossId &&
          table.ResolveChoice(pickedMale.Id, "en", VoiceLineVoice.NonHuman, true)?.Id == pickedMale.Id,
        "cross-kind hand-picked voice refused");
    Check(table.ResolveChoice("nope", "en", VoiceLineVoice.Male, true) == null, "unknown voice id accepted");
    Check(table.ResolveChoice(bossId, "en", VoiceLineVoice.NonHuman, true)?.Id == bossId, "boss voice choice for a monster");
    var crossed = new VoicePersonaAssigner(); crossed.Configure(table, "en", true);
    Check(crossed.Assign(1, seed, VoiceLineVoice.Male, VoiceLineAge.Adult, femaleId).Persona is { Voice: VoiceLineVoice.Female }, "female choice for a male body not applied");
    // The picker lists every assignable voice of the language, any kind, each with a filter category.
    var pickerChoices = table.AllChoices("en", true);
    Check(pickerChoices.Any(p => p.IsBoss) && pickerChoices.Any(p => p.Voice == VoiceLineVoice.Female && !p.IsBoss) && pickerChoices.Any(p => p.Voice == VoiceLineVoice.Male && !p.IsBoss),
        "picker does not list every kind");
    Check(pickerChoices.All(p => p.Category is "Boss" or "Male young" or "Male mature" or "Male elderly" or "Female young" or "Female adult" or "Female elderly"),
        "unknown picker category");
    // A human choice saved on a Japanese client maps to the same actor on an English one; boss ids do not map.
    var jaMale = table.Choices("ja", VoiceLineVoice.Male, true).First(p => table.Get("en" + p.Id.Substring(2)) is { Assignable: true });
    Check(table.ResolveChoice(jaMale.Id, "en", VoiceLineVoice.Male, true)?.Id == "en" + jaMale.Id.Substring(2), "ja choice not mapped to en");
    Check(table.ResolveChoice("ja-b01", "en", VoiceLineVoice.NonHuman, true) == null, "boss choice mapped across languages");
    // Labels: names when known, else numbered voices.
    Check(table.Label(table.Get("ja-010")!) == "Alphinaud", "named label");
    foreach (var lang in new[] { "ja", "en" })
    {
        var voiceLabels = new List<string>();
        foreach (var v in new[] { VoiceLineVoice.Male, VoiceLineVoice.Female, VoiceLineVoice.NonHuman })
            foreach (var p in table.Choices(lang, v, true))
            {
                var label = table.Label(p);
                voiceLabels.Add($"{v}:{label}");
                Check(p.IsStoryCharacter ? label == p.Character
                    : System.Text.RegularExpressions.Regex.IsMatch(label, p.IsBoss ? @"^Boss voice \d+ \((male|female)\)$" : @"^(Male|Female) voice \d+ \((young|mature|adult|elderly)\)$"),
                    $"{p.Id}: label {label}");
            }
        Check(voiceLabels.Distinct().Count() == voiceLabels.Count, $"{lang}: duplicate voice labels");
    }

    Console.WriteLine("Fallen taunt pool sizes (own = persona's kill+victory+taunt lines; pool = after top-up):");
    foreach (var s in poolStats)
        Console.WriteLine(s);
    Console.WriteLine("Boss personas:");
    foreach (var s in bossCoverage)
        Console.WriteLine(s);
}

Console.WriteLine($"PASS {checks} checks; voice mapping, language fallback, labels, rate limiting, arbitration, transition watch, voiced line pool, picker and personas.");
