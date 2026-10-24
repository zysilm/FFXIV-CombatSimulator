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
    Check(lines.Count >= 150 && lines.Count <= 900, $"pool size {lines.Count} outside 150..900");
    // The default pool is the original data-proven set; recognition-matched lines are opt-in.
    var baseCount = lines.Count(l => !(l.TryGetProperty("extended", out var x) && x.GetBoolean()));
    Check(baseCount >= 150 && baseCount < lines.Count, $"default (non-extended) pool size {baseCount}");
    foreach (var gender in new[] { "male", "female" })
        foreach (var ev in new[] { "Engage", "KilledPlayer", "Victory", "Death" })
            Check(lines.Any(l => !(l.TryGetProperty("extended", out var x) && x.GetBoolean()) &&
                                 l.GetProperty("gender").GetString() == gender &&
                                 l.GetProperty("events").EnumerateArray().Any(e => e.GetString() == ev)),
                $"default pool has no {gender} {ev} line");
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

Console.WriteLine($"PASS {checks} checks; voice mapping, language fallback, labels, rate limiting, arbitration, transition watch, voiced line pool and picker.");
