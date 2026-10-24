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
w.Observe(new VoiceActorSnapshot(true, true, true, 950, 1000), events);
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

Console.WriteLine($"PASS {checks} checks; voice mapping, language fallback, labels, rate limiting, arbitration and transition watch.");
