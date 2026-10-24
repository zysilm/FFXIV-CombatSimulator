# Enemy voices: acceptance

Main window, Effects tab, section **Enemy Voices**. Out of the box: **Enemies talk** on, voice
volume 1.0, **Speech bubbles** on. Everything else is under the collapsed **Advanced** node.
Existing configs are switched on once (`EnemyVoiceDefaultsMigrated20261024`); untick to turn off.

All playback is local: `SoundManager.PlaySound` on the client with game files that exist
(`IDataManager.FileExists` plus a header read before every voiced line). Bubbles are the client's
own `NpcYellBalloon` with print-to-log off. Nothing is sent to the server, no packets are built.

## 0. Out of the box (do this first)

1. Start a fight against **human** enemies (soldiers, bandits, player clones). Monsters stay
   quiet by design: no curated line is non-human.
2. Expect, within the first seconds: one enemy calls out as the pack engages ("Do your worst!",
   "En garde!", ...), a speech bubble over its head with the same words, voice matching the
   enemy's gender. During the fight a line every few seconds at most (skills, heavy hits, taunts,
   low HP). Never two enemy lines on screen at once.
3. Kill one enemy: the dying one may say its last words; a nearby ally may react. Let an enemy
   kill you: the killer gloats; if you have no living companions another enemy calls the win.
4. **If silent:** `/xllog`, search `Enemy voices`. The first line spoken logs once per session:
   `Enemy voices: first voiced line vo 8202044 / yell 7775 "Do your worst!" path=sound/voice/vo_line/8202044_en.scd handle=0x... playerAvailable=True`.
   - No such line at all: no line was attempted. Check the enemies are human models and the
     master switch is on; Advanced shows "Lines spoken N".
   - `handle=0x0` or `playerAvailable=False (...)`: PlaySound refused or is unavailable; the
     reason after it (and any `Enemy voices disabled for this session: ...` line) says why. The
     bubble still shows in that case.
   - Bubble but no sound: volume routing; see A3 (Advanced > Volume category).
5. Gender spot check: female enemies only use female recordings, male only male. Report any
   line that sounds wrong (Advanced shows the last line and its `vo` id).

## Voiced lines: how the pool was made

**The vo_line id is not `8200000 + NpcYell row`.** Checked over 3,586 pairs: text length vs.
audio duration correlates at r = 0.03 under that mapping (none). The owner's in-game anchor
`8202037_en` ("There'll be nothing left of you but a smoking crater!") is NpcYell **7768**.
QuestBattle script constants pair `LINEVOICE_X = 820xxxx` with `YELL_X = row`, and the offset
between the two differs per block (QB191: +5731, QB112: +2392, QB159: +3077). Voice files and
yell rows are allocated in blocks with a constant offset per block, and no sheet stores the link.

Recovery (offline tool, not shipped): durations of all 4,100 vo_line files in en/ja/de/fr from
the .scd headers (MS-ADPCM); a per-language duration model (letters + pauses) fitted on the
script-proven block; a dynamic program over piecewise-constant offsets; then every segment's
evidence is compared with the best competing offset. A segment is accepted only with >= 10 files
and a margin >= 20 nats over any other offset; a shuffled-duration null run produced zero
segments passing that bar. Accepted: 304 files in 11 blocks (vo 2000-2146, 2162-2172, 2330-2345,
3000-3011, 3027-3038, 3045-3062, 3101-3122, 3199-3217, 3226-3238, 3420-3439, 5924-5937), all
consistent with the anchor and the QB191 script pairs. Plus 8 story lines paired directly by
QuestBattle constants (Zenos, Yugiri, Fordola, Y'shtola) that also fit the duration model in all
four languages; two Raubahn pairs failed it (French z = 7) and were dropped. These blocks are
Trust / duty-support battle barks, which is why they are short and generic.

Curation (first pass): 163 lines chosen by reading the text, each tagged with one or more events
(Engage 24, Taunt 23, Skill 57, Hurt 14, LowHp 28, KilledPlayer 8, AllyDeath 6, Victory 6,
Death 10; 11 tagged story, weight 0.35). Support lines ("I'll tend your wounds"), place and
character names and quest references were left out.

Gender: each line's en and ja audio decoded (MS-ADPCM), F0 by YIN and F2/F3 formants by LPC;
an LDA calibrated on eight speaker blocks identified from the text and script constants
(Alphinaud, Thancred, Urianger, Exarch / Alisaie, Ryne, Y'shtola, Lyna). Leave-one-speaker-out:
every speaker's median on the right side; per line 121 right, 5 wrong, 21 unsure. Shipped labels
require the line and its neighbours (same speaker block) to agree; inside the eight known blocks
the speaker's gender is used. Independent check: all 9 script-named lines outside the training
blocks (Zenos male; Y'shtola, Yugiri, Fordola, Alisaie female) came out right. Pitch alone does
not work for shouts (male shouts reach 250-420 Hz); F3 is what separates. Pool: 107 male,
70 female, 0 non-human.

Second pass (offline, same tools widened):
- Script constants: every Excel sheet was scanned for VOICE/YELL constant names. Only QuestBattle
  holds voice constants (45; the Quest-sheet values in the voice id range are LOC/LEVEL ids).
  Client Lua (`game_script/*.luab`) is plain Lua 5.1 event/cutscene code; battle scripting is
  server-side, so it holds no further pairs. New: QB112 Y'shtola 1094, 1095, 1097 -> 3486, 3487,
  3489 (002, 005 and 006 are named; 003/004 sit between them in both numbered sequences), Zenos
  1342 -> 4838 and Alisaie 2579 -> 19921 (not shipped). Raubahn 1321 and Zenos 1343 are
  script-paired but fail the duration model or differ in meaning across languages; not shipped.
- Statistics: Japanese mora features (kana, kanji, latin, pauses, ellipses) are the strongest
  single language (held-out AUC 0.86 vs en 0.83); all four combined AUC 0.93. Constant-offset
  segments were scored against three nulls: shuffled durations, a reversed text sheet (keeps the
  local structure of both sides) and the same search on other sheets. Above margin 40, NpcYell
  gives exactly the 11 known blocks plus one narration block (vo 5573-5605), but null margins
  reach 51-56 on the larger sheets, so that block is not counted as proven (and is unusable
  anyway). InstanceContentTextData, PublicContentTextData, MassivePcContentTextData and Balloon
  show no excess over their nulls.
- Correction: vo 3122 and 3199 both claimed yell 13520. Rows 13520-13537 are one male speaker
  set, so 3199 (male) is right and 3122 (female, last file of the previous block) was dropped.
  Block edges carry such seam collisions (3064/3101 -> 13499, 3024-3026/3010-3012), so no edge
  extensions were added. The pool test now rejects two files on one yell row.
- Added 15 lines (4 script-proven, 11 from already-proven blocks that fill thin buckets: female
  hurt/ally-down/kill, male ally-down). Events now: Engage 25, Taunt 25, Skill 60, Hurt 17,
  LowHp 28, KilledPlayer 9, AllyDeath 12, Victory 7, Death 10.

Third pass (speech recognition, offline, not shipped; research files outside the repo):
- Every vo_line file in ja and en (4,100 / 4,092) was decoded to 16 kHz and transcribed with
  Whisper large-v3, language forced per file. Each transcript was scored against every row of
  NpcYell, InstanceContentTextData, Balloon and PublicContentTextData (en word-level, ja
  character-level after NFKC and kana folding). A pair counts as **proven** only if the ja and the
  en audio each read that row (en >= 0.80, ja >= 0.85 similarity), each language ranks it within
  0.2 of its own best row, and the joint score beats the best differently worded row by >= 0.2.
- Checks: on the 304 earlier pairs the rule proves 210, all to the same row (100% precision, 69%
  recall; the rest are short shouts or kana/kanji ASR slips). Three decoy sheets (en and ja of
  each row shuffled apart) give 0 proven. Removing the true row of each known pair and rerunning
  gives 0 proven (a looser rule, kept as "probable" in the research table only, gave 10 of 297,
  all near-identical wordings such as "Thank you." 助かったわ vs 助かったぜ).
- Result: 1,763 proven files, 1,553 of them new; 1,256 match InstanceContentTextData (duty
  dialogue), so the pool now has an optional `sheet` field. Balloon and PublicContentTextData
  matched nothing.
- Gender: wav2vec2 age/gender model (both languages) + the pitch/formant LDA + ECAPA speaker
  clusters per language must agree, otherwise unknown (not used). Trust speaker blocks: 132 of
  133 labeled lines right; the one "miss" (8202056, "The best defense is a good defense!") is a
  Thancred line one file past his assumed block. All 164 labeled old pool lines agree with their
  shipped gender.
- Age: `young` / `adult` / `elderly` from the model's age and child probability, smoothed per
  speaker cluster. Battle shouts read old to the model (Alphinaud ~46), so the bands are relative
  and weak: no female line came out elderly, only 4 pool lines are young. Treat it as a hint.
- Pool: 176 old lines kept (8202097 dropped: its English audio says "I will not yield!" while its
  row says "I'm not done yet!"), 446 new lines read and tagged by hand, generic battle lines first
  plus a few memorable boss lines with the story flag. 622 lines: Engage 105, Taunt 95, Skill
  216, Hurt 58, LowHp 90, KilledPlayer 37, AllyDeath 21, Victory 19, Death 26 (female victory 7,
  kill 12, death 11; male victory 12).

Runtime: `Resources/VoiceLines.json` (voice id, text row, optional sheet, events, gender, age,
story, weight; no text). Text comes from the row's sheet (NpcYell unless `sheet` says
InstanceContentTextData) in the client language; audio is the client-language file only (a line
whose file or text is missing is retired for the session). Enemy body: human models by Customize
sex byte; anything else counts as non-human and gets no line (its labeled monster sounds still
play). Age: the Customize BodyType byte (1 adult, 3 elderly, 4 child) makes the picker prefer
lines of that age (and lines without an age) within each event; any other line of the event is
the fallback, and a fresh line of another age beats repeating a recent matching one. Gate: one line on screen at a time, then 3.5 s of quiet (Advanced) before ordinary events,
0.5 s for engage / kill / victory / death; 8 s per enemy; per-event cooldowns across enemies
(skill 8, hurt 10, taunt 12, engage 6, low HP 6, ally death 10, death 3 s); chance per event
(skill 30%, hurt 35%, engage 70%, death 75%, others 70-100%); the last 24 lines are avoided.
Kill and victory borrow from each other and from taunts. Companion deaths count as kills (the
nearest enemy gloats). The player's death is watched directly (CombatEngine's OnPlayerDeath only
fires with ragdoll on). Phase-1 limits still apply on top. When no line is spoken the labeled
shout path runs exactly as in phase 1.

Audition: Advanced > Audition > source **Voiced line**, enter a vo_line id (default 8202037) or
press **Next curated line**; the curated text, events and gender are shown.

Flags: `EnableEnemyVoices` (true), `EnemyVoiceLines` (true), `EnemyVoiceBubbles` (true),
`EnemyVoiceLineGap` (3.5), plus the phase-1 flags below.

Offline: `dotnet run --project Tests/Voice/Voice.csproj -c Release` (pool shape, anchor pair,
no disproven mapping, one file per sheet row, every entry has an age, every event covered, gender
filter, age preference and fallback, gate timings, no repeats, flood bound, .scd header duration).

Listen for (third pass):
1. Bubble vs voice: new lines from duty dialogue (Advanced > Audition shows
   `InstanceContentTextData <row>`) should say exactly what the bubble says, in en and ja. Report
   any vo id where they differ; ASR only proved ja and en, so check de/fr wording too.
2. Gender: no woman's voice from a male enemy and the reverse, now over 622 lines.
3. Age: an old enemy (BodyType elderly) should lean on deep, older boss voices (e.g. 8203553
   "Good, good. A fitting finale!", 8203568 "Burn!"); a child model on 8202429 "We can't fail
   here! We can't!", 8205310, 8205413. If these sound wrong for their band, the band is the
   suspect, not the pairing.
4. Kept but worth one listen: 8202057 (English ASR heard "Come on!" for "Must hold on...").
5. Story-flagged boss lines (weight 0.35) should be occasional, not frequent.

# Phase 1 details (Advanced: labeled shouts)

Phase-1 shouts play only for sound entries labeled in the audition panel and are the fallback
whenever no voiced line is spoken. The only always-on change is a pass-through wrapper around
the two enemy attack executors (`CombatModeRouter`); it forwards every call unchanged and only
raises a notification.

## 1. Audition checklist

Open **Advanced > Audition and shout labels**.

| # | Check | How | Record |
|---|---|---|---|
| A1 | PlaySound produces sound at all | Source "PC battle shout", `mid_ma`, entry 0, Play | yes / no. If no: does the status line say "Playback unavailable: ..."? Copy it. Check `/xllog` for "Enemy voices disabled". |
| A2 | Nothing crashes on Stop | Play a long monster entry, press Stop at once | stops / keeps playing / crash |
| A3 | Which slider controls it | Play repeatedly while moving each slider in System Configuration > Sound (Master, Voice, Sound Effects, and Player Sound Settings: Own / Party / Others). Try each **Volume category** in our panel. | which slider(s) affect "Other", "Player", "Party", "Bypass" |
| A4 | Plugin volume slider | Voice volume 1.0 vs 0.2 | audible difference yes / no |
| A5 | Positional | Tick "At my position", play, then walk 30 y away from the spot and play again; turn the camera | louder near / pans with camera / no change. Untick: always flat? |
| A6 | In-combat positional | Combat with labeled voices, **Positional (3D)** on vs off | which is better; does 3D get inaudible at range? |
| A7 | PC shout entries | `mid_ma` (and one female, one Au Ra `aur_fc`) entries 0-4; each entry plays a random take, so press Play 3-4 times per entry | per entry: attack grunt / big attack shout / hurt / death / victory / other |
| A8 | Do all PC files share a layout? | Spot-check entry 3 (or whichever is "death") on `lal_fb`, `ros_mc`, `vie_fa` | same meaning yes / no (labels assume yes) |
| A9 | NPC battle entries | NPC voice 1, 2, 40, 84 entries 0-2 | per entry meaning; same layout across files? Gender of a few nn |
| A10 | Monster pack entries | Source "Current target" on a few monsters (or SE pack id, e.g. 3091), entries 0..N-1 | which entry is roar / hurt / death; label them |
| A11 | Language fallback | (only if you can switch client language) German client plays `_de`; missing files fall back to `_en`, then `_ja` | ok / not |
| A12 | Voiced line | Source "Voiced line", vo_line 8202037, Play; then **Next curated line** a few times | the voice says the text shown |
| A13 | a/b/c variants | Compare `mid_ma` vs `mid_mb` | do they sound like different personalities (higher/lower)? Note for the voice-id seam |

Then label: for each meaning, tick the matching box for the current entry ("Entry N is:
engage / attack / hurt / death / victory / taunt"). An entry may have several labels; a label
may have several entries (one is picked at random). Labels are saved to your config immediately
(`EnemyVoiceEntryLabels`, keys `pc`, `npc`, `mon/{sepack}`). To ship good labels to everyone,
copy them into `CombatSimulator/Resources/VoiceEntryLabels.json` (same shape:
`{"pc": {"death": [3]}, "mon/3091": {"attack": [2, 5]}}`; user labels override per key+label).

## 2. Per-event tests

Prerequisite: PC labels done (at least attack, hurt, death, engage), one monster pack labeled.

| Event | Trigger | Expected | Label used (fallbacks) |
|---|---|---|---|
| Engage | Start sim near idle enemies; they aggro | one shout per enemy as it engages (closest few only) | engage (attack) |
| Skill / cast start | Non-Action Mode: enemy starts a cast bar | shout at cast start, **not** again when the cast resolves | attack |
| Skill commit | Instant skill, or Action Mode telegraph windup start | shout at commit; auto-attacks are silent | attack |
| Heavy hit | One frame of damage >= 12% max HP | hurt grunt | hurt |
| Low HP | First crossing below 30% HP (once per life; re-armed by a heal to full / reset) | one line | hurt (engage) |
| Death | Enemy dies | death cry (bypasses the token bucket) | death |
| Ally death | Another enemy within 15 y (setting) of the corpse | nearest living ally reacts | engage (taunt) |
| Killed player | Enemy lands the killing blow on you | killer gloats | victory (taunt, engage) |
| Victory | You die with no living companions | ~1.5 s later the nearest living non-killer enemy calls it | victory (taunt) |
| Idle taunt | Engaged enemies, option on | rare, average every 25 s per enemy, never more than every 20 s | taunt |

Voice choice per enemy (cached per address+entity):
- Player clones (client-spawned humanoids) and companions: PC shout matching race, Hyur tribe
  (mid/hil) and gender. Variant a/b(/c) is a stable hash of the full appearance, because the
  creation voice is not in Customize and ClientStructs does not expose it.
- Humanoid field enemies: generic NPC voice `vo_npc{nn}`, stable per BNpcBase id. Option
  **Humanoid field enemies use appearance voices** switches them to PC shouts (gender-correct).
- Everything else: `sound/battle/mon/{ModelChara.SEPack}.scd` (not BNpcBase.SEPack, which is a
  different id space).

Rate limits (settings): N closest speakers (4), max concurrent (3, counted by an assumed 1.8 s
per voice), global budget (1.5/s, burst 2), one voice per actor per 1.5 s, per-event cooldowns
(engage 30 s, skill 6 s, hurt 5 s, low HP 60 s, ally death 12 s, taunt 20 s). Priority within a
frame: death > killed player > victory > engage > low HP > ally death > skill > hurt > taunt,
nearer first on ties. An event with nothing labeled is spent silently (cooldown only).

Flags: `EnableEnemyVoices` (master, true), `EnemyVoiceVolume`, `EnemyVoicePositional`,
`EnemyVoiceVolumeCategory` (2 = Other), `EnemyVoiceClosestSpeakers`, `EnemyVoiceMaxConcurrent`,
`EnemyVoicesPerSecond`, `EnemyVoiceHumanoidUseAppearance`, `EnemyVoiceIdleTaunts`,
`EnemyVoiceTauntInterval`, `EnemyVoiceAllyDeathRadius`, `EnemyVoiceEntryLabels`.

Regression check with the flag **off**: a full fight (instant mode and Action Mode) behaves
exactly as before, and `/xllog` shows no "Enemy voices" lines.

Offline: `dotnet run --project Tests/Voice/Voice.csproj -c Release`.

## 3. Risks

- **PlaySound parameters.** Called with ClientStructs' documented defaults (speed 1, a9 0,
  midiNote -1, autoRelease true). If the signature drifts after a patch, the address check
  disables the feature; a wrong-but-resolved signature can still crash natively (managed
  try/catch cannot catch that). The owner confirmed playback in game; nothing is played
  before a fight is running, and the first voiced line is logged.
- **Volume category / bus** unverified (A3). Voices may ignore the Voice slider.
- **Positional playback** unverified (A5/A6); falls back to a setting.
- **Stop** reads a recycled SoundData slot; it checks entry number, playing state and file name
  before stopping, but a same-file sound started by the game in between could be cut.
- **Variant guess**: a clone of your character may not use your own creation voice (A13).
- **NPC voice gender** is not matched for humanoid field enemies (hash over 84 files).
- **Concurrency is time-estimated**, not read from the engine; long monster entries can overlap.
- **Skill voice in casts**: the cast-resolve suppression uses the cast time; a cast that is
  interrupted and immediately recast inside the grace could stay silent once.

- **Voiced line content** is proven statistically and by script constants, not by listening to
  every file. A mismatched line would show text different from the voice; report its `vo` id.
- **Gender labels** are acoustic plus speaker blocks; about 3% per-line error before the
  neighbour agreement filter. Report any wrong-gender line.
- **Bubbles on client-spawned actors** use the same balloon call as crowd chatter; a corpse being
  ragdolled may carry its last words for a few seconds.
- **German/French/Japanese clients** use their own audio and text; all 177 files exist and decode
  in all four languages.

## 4. Open questions

1. **More lines.** The remaining ~3,800 vo_line files have no proven text, and the offline
   sources are now exhausted (see "Second pass" above). Listening sessions in the audition
   panel are the remaining route; add rows to `Resources/VoiceLines.json`.
2. **Monster lines.** None curated; monsters keep their labeled SE packs.
3. **Creation voice.** If a reliable read of the actor's voice id appears, plug it into
   `VoiceSelection.PcShoutFromVoiceId` (already mapped and tested for ids 1-168).
4. **Ship default labels** from the owner's recorded set for `pc`, `npc` and common packs.
