# Enemy voices: acceptance

Main window, Effects tab, section **Enemy Voices**. Out of the box: **Enemies talk** on, voice
volume 1.0, **Speech bubbles** on. Existing configs are switched on once
(`EnemyVoiceDefaultsMigrated20261024`); untick to turn off.

The whole UI:
- **Enemies talk**, **Voice volume**, **Speech bubbles**, **Always call out kills**, **Taunt the
  fallen** (+ **Every (s)** while it is on).
- **Advanced** (collapsed): **Pause between lines (s)** (the one chattiness knob: quiet after each
  line, and the mid-fight idle taunts follow it at about 4x the pause, 5-60 s; default 3.5 s ->
  a taunt every ~14 s per engaged enemy), and the **voice picker**: one row per tracked enemy
  (nearest first) with a dropdown "Auto" + every voice that fits it, and **Preview**. With no
  enemy tracked it shows one hint line.

Fixed behaviour (no longer options): voiced lines on, volume category "Other" (the game's
others' sounds slider), positional (3D) playback at the speaker, rate limits at their defaults
(4 closest speakers, 3 concurrent, 1.5 voices/s), idle taunts on, humanoid field enemies keep the
generic NPC battle voice for shouts. The audition/label panel is gone; shipped labels
(`Resources/VoiceEntryLabels.json`) and any labels already in the config still apply.

All playback is local: `SoundManager.PlaySound` on the client with game files that exist
(`IDataManager.FileExists` plus a header read before every voiced line). Bubbles are the client's
own `NpcYellBalloon` with print-to-log off. Nothing is sent to the server, no packets are built.

## 0. Out of the box (do this first)

1. Start a fight against **human** enemies (soldiers, bandits, player clones). Monsters and
   demihumans talk too, with the processed boss voices (ja/en clients; see "Boss voices" below).
2. Expect, within the first seconds: one enemy calls out as the pack engages ("Do your worst!",
   "En garde!", ...), a speech bubble over its head with the same words, voice matching the
   enemy's gender. During the fight a line every few seconds at most (skills, heavy hits, taunts,
   low HP). Never two enemy lines on screen at once.
3. Kill one enemy: the dying one may say its last words; a nearby ally may react. Let an enemy
   kill you: the killer gloats; if you have no living companions another enemy calls the win.
4. **If silent:** `/xllog`, search `Enemy voices`. The first line spoken logs once per session:
   `Enemy voices: first voiced line vo 8202044 / yell 7775 "Do your worst!" path=sound/voice/vo_line/8202044_en.scd handle=0x... playerAvailable=True`.
   - No such line at all: no line was attempted. Check the master switch is on and a fight is
     running (Advanced lists the tracked enemies; **Preview** plays one line on demand).
   - `handle=0x0` or `playerAvailable=False (...)`: PlaySound refused or is unavailable; the
     reason after it (and any `Enemy voices disabled for this session: ...` line) says why. The
     bubble still shows in that case.
   - Bubble but no sound: volume routing; the voices follow the game's "Others" sound slider
     (System Configuration > Sound > Player Sound Settings).
5. Gender spot check: female enemies only use female recordings, male only male. Report any
   line that sounds wrong with the bubble text.

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

Fourth pass (processed voices, offline; research files outside the repo):
- Many recognition-matched lines are boss/primal reads with studio processing (a hall/echo tail,
  layered or "godlike" voice) but were gendered like normal voices, so plain soldiers sounded like
  a primal. Every vo_line file was re-decoded at 32 kHz (ja and en separately) and measured blind.
- Main measure: the final tail. After the last frame within 20 dB of the speech level, a line is
  fitted to the energy envelope (dB) down to -60 dB or the end of the file; RT60 = -60 / slope. A
  file that ends right after the speech (fit unusable) is bounded by its remaining length. Dry
  battle barks: 0.05-0.3 s; processed reads: 0.8-2 s. Over all 4,176 files the distribution is
  bimodal with its valley at 0.4-0.6 s, ja and en agree on 97% of files (different actors, same
  mix), and per speaker cluster it is all-or-nothing (companions stay dry even in their own duty
  dialogue; bosses, primals, Ascians, automata and the arena announcer are reverbed).
- Score = mean over ja/en of log2(RT60 / 0.5 s) + 2 x (share of the speaker's ja/en clusters that
  is processed - 0.5); `fx` if > 0. An AudioSet tagger (AST) was run only as a cross-check: its
  echo tag ranks files the same way (Spearman 0.74). Harmonicity, modulation (SRMR), pause floor
  and echo-autocorrelation features were weaker and are not used.
- Calibration on text-only seeds (god/primal wording vs Trust/duty-support barks; 112 / 269):
  precision 0.96, recall 0.88 at the fixed threshold; 5-fold held-out 0.97 / 0.86. The misses are
  mostly seed errors (dry human villains saying "Kneel or die!", companions talking about gods).
- Pool: 254 of 622 lines are `fx` (253 extended, 1 original: 8203204, borderline). 368 remain for
  human speakers (female: Engage 22, KilledPlayer 4, Victory 5, Death 6; male: Engage 32,
  KilledPlayer 13, Victory 6, Death 10). The `EnemyVoiceExtendedLines` switch is gone: human
  enemies use the whole pool minus `fx`; `extended` stays in the JSON as provenance only. `fx`
  lines stay in the file for a future monster/boss speaker.

Fifth pass (more voices from already-proven lines; offline, no new recognition):
- Of the 1,763 proven files, 1,196 were not in the pool. All of them were read (en + ja), and 319
  were added (161 human, 158 fx): generic battle remarks, quips, grunts with words, rallying cries
  and boss boasts. Lines that name people, places or factions, explain the story, are addressed to
  the player's journey, or are healer/support lines were left out. Lines from speakers just short
  of the persona bar were preferred, and so were the thin events. Old entries are unchanged
  (fx lines got new boss persona ids, see below).
- Added per event: Engage 73, Taunt 34, Skill 159, Hurt 13, LowHp 57, KilledPlayer 14,
  AllyDeath 9, Victory 14, Death 12. Pool 941 lines: human 529 (male Victory 6 -> 16, Engage
  32 -> 62; female Engage 22 -> 40, AllyDeath 10 -> 15), fx 412 (fx now has 2 AllyDeath lines).
- fx uses the same fourth-pass rule (`reverb_scores.pkl` `fx`, score > 0).
- Gender: 29 of the new human lines had an inconclusive line-level gender ("unknown"). They take
  the gender of their speaker persona only when no gender vote contradicts it (e.g. `?/?/F` in a
  female persona) and the line lands in a persona of that gender in both languages. One more
  (8202012) sits in Alphinaud's Trust block. Every other new line has its own agreed gender.
- Personas rebuilt with the same scripts (clustering unchanged, ARI ja/en 0.997). Every
  existing persona id keeps its speaker, gender, band and name; 9 new small ids per language
  (ja-070.., en-071..). Assignable personas: **ja 15 -> 23, en 14 -> 23**. New ones: male
  mature Hythlodaeus (probable), Bakool Ja Ja, Emet-Selch (probable), an unnamed 18-line male
  (ja-016 / en-016, "Here they come!"); male young ja-034 / en-035; female adult three unnamed
  (ja-018 / en-018, ja-033 / en-034, ja-035 / en-036), plus en-015, which now passes in en as well.
  Still none for female elderly; female young is still Ryne and Alisaie only.
- Boss personas rebuilt with the same grouping: **ja 18 -> 26, en 13 -> 22**. Old ids stay on
  the group that holds most of their old lines, but 121 of the 254 old fx lines moved to another
  boss id (clusters that used to be merged now reach 8 lines on their own). A saved monster
  override can therefore sound a bit different.

Listen for (fifth pass):
1. The persona-gendered lines (female ja-033 / en-034: 8203455 "This will hurt.", 8203457, 8203465,
   8203472, 8203482, 8203488, 8203489, 8206400; female knight ja-035 / en-036: 8205746, 8205758,
   8205762, 8205763, 8205765, 8205767; ja-018 / en-018: 8204258, 8204259, 8204267; Hythlodaeus
   ja-017 / en-017: 8203220, 8203223, 8203234, 8203236): right gender, same voice as the rest of
   that persona.
2. The new unnamed male ja-016 / en-016 and females ja-018 / en-018, en-015 each sound like one
   actor in the voice picker's Preview.
3. Kill/victory variety: men now say "That's the last of them.", "Yes, yes! Rise once more! It
   mustn't end yet!", "Down with you!"; monsters "Unenhanced as you are, you never stood a chance."

(The sixth pass, structure/second-recognition pairs, was reverted: it mixed wrong voices into the pool.
Its `proven-v6` pairs are not used.)

Recategorisation (by the Japanese text only):
- Events had been set mostly from the English text, so Trust allies rejoining the fight after a raise
  (8202042 もう一度、やってやる…！, 8202043 悪いな、再開だ！, 8202119 戦線に復帰する……！, 8203050) and "now it
  begins" lines (8203225 ありがと、さてここからだ！, 8202342 ここからだ！) were kill/victory/taunt lines. Every
  pool line was re-read in Japanese and tagged as said by an enemy: 142 lines changed events, 19 left the
  pool (support/heal lines, mechanic call-outs, lines naming people or places, story asides). Log:
  `voice-research\out\recat\changes.csv` (vo id, ja text, old -> new events, reason).
- Kill/victory lines are now only gloats, dismissals and "it's over" calls: human male KilledPlayer
  19 -> 8, Victory 16 -> 5; female 5 -> 3, 7 -> 3; fx 27 -> 13, 10 -> 2. The kill chain (kill -> victory
  -> taunt) still reaches every assignable persona, but a borrowed taunt (and a taunt over the fallen)
  is never a line also tagged Engage ("here I come" over a fallen player). Taunts that only make sense
  while the opponent still stands ("still not yielding?") became Hurt reactions.
- Assignable personas **ja 23 -> 21, en 23 -> 21**: ja-019 / en-019 and ja-024 / en-024 (Emet-Selch,
  probable) dropped to 7 lines. Ids of every other speaker are unchanged.

Idle remarks (`Chatter`):
- New event for plot-neutral talk in a lull (musings, grumbles, observations): 4 pool lines moved
  there, 34 added from earlier `proven` pairs by their Japanese meaning (no names of people, places,
  factions or items, no system text, announcer or mechanic call-outs): 22 human (16 male, 6 female),
  12 fx. Gender, age, fx and persona ids filled with the same tools as before (2 new 1-line ids,
  ja-079 / en-080; boss ids unchanged). Pool 956 lines: male / female / fx Chatter 17 / 7 / 14.
- The idle-talk timer of an engaged enemy asks for a remark or a taunt, evenly when its voice has
  both (8 of 23 ja human personas have their own of each), a taunt otherwise. A remark is never a
  kill or victory line and never a taunt over the fallen. Chatter counts as a line for the persona bar
  but not as one of its 5 events.
- Assignable personas **ja 21 -> 23, en 21 -> 23** (ja-019 / en-019 and ja-024 / en-024 are back with
  a remark each).

Listen for (recategorisation): after a wipe the killer and the taunters say only gloats or "it's over"
lines, never "back to it" / "here I come"; a long fight brings an occasional remark (8204268
獲物は焼いちまうか？それとも生で食うか？, 8206256 ここは露店が並ぶ賑やかな場所だったはず……) between taunts.

Runtime: `Resources/VoiceLines.json` (voice id, text row, optional sheet, events, gender, age,
story, weight, optional `extended` (provenance) and `fx` (processed voice: never picked for a
male or female speaker; the only lines of a non-human one); no text). Text comes from the row's sheet (NpcYell unless `sheet` says
InstanceContentTextData) in the client language; audio is the client-language file only (a line
whose file or text is missing is retired for the session). Enemy body: human models by Customize
sex byte; anything else counts as non-human and speaks only fx lines through a boss persona
(ja/en; on de/fr it gets no line and its labeled monster sounds play). Age: the Customize BodyType byte (1 adult, 3 elderly, 4 child) makes the picker prefer
lines of that age (and lines without an age) within each event; any other line of the event is
the fallback, and a fresh line of another age beats repeating a recent matching one. Gate: one line on screen at a time, then 3.5 s of quiet (Advanced > Pause between lines) before ordinary events,
0.5 s for engage / kill / victory / death; 8 s per enemy; per-event cooldowns across enemies
(skill 8, hurt 10, taunt 12, engage 6, low HP 6, ally death 10, death 3 s); chance per event
(skill 30%, hurt 35%, engage 70%, death 75%, others 70-100%); the last 24 lines are avoided.
Kill and victory borrow from each other and from taunts. Companion deaths count as kills (the
nearest enemy gloats). The player's death is watched directly (CombatEngine's OnPlayerDeath only
fires with ragdoll on). Phase-1 limits still apply on top. When no line is spoken the labeled
shout path runs exactly as in phase 1.

The in-plugin audition panel was removed. To hear one line by id, play
`sound/voice/vo_line/{id}_{lang}.scd` with an external sound explorer, or use **Preview** in the
voice picker (a random line of that enemy's voice).

Flags: `EnableEnemyVoices` (true), `EnemyVoiceBubbles` (true), `EnemyVoiceLineGap` (3.5), plus the
flags listed under Phase 1.

Offline: `dotnet run --project Tests/Voice/Voice.csproj -c Release` (pool shape, anchor pair,
no disproven mapping, one file per sheet row, every entry has an age, every event covered, gender
filter, age preference and fallback, gate timings, no repeats, flood bound, .scd header duration,
fx never picked for a human speaker (also over the shipped pool), the owner's boss examples are fx,
engage/kill/victory/death still covered for both genders after fx).

Listen for (third pass):
1. Bubble vs voice: new lines from duty dialogue (`sheet: InstanceContentTextData` in the pool)
   should say exactly what the bubble says, in en and ja. Report
   any vo id where they differ; ASR only proved ja and en, so check de/fr wording too.
2. Gender: no woman's voice from a male enemy and the reverse, now over 622 lines.
3. Age: an old enemy (BodyType elderly) should lean on deep, older boss voices (e.g. 8203553
   "Good, good. A fitting finale!", 8203568 "Burn!"); a child model on 8202429 "We can't fail
   here! We can't!", 8205310, 8205413. If these sound wrong for their band, the band is the
   suspect, not the pairing.
4. Kept but worth one listen: 8202057 (English ASR heard "Come on!" for "Must hold on...").
5. Story-flagged boss lines (weight 0.35) should be occasional, not frequent.

Listen for (fourth pass, before merging): work through `reverb_listen.xlsx` (research folder) by
vo_line id (external sound explorer) and mark y/n. Section A (highest scores) should all ring on after
the last word; section B straddles the threshold (fx above, clean below; this is where it can be
wrong); section C should be dry; section D is dry by the tail but checked for pitched/layered
voices the tail cannot see. In combat: human enemies should no longer sound like a primal, e.g.
8203663 "Fools, braying before a god!" and 8205955 "You are naught but insects..." never play
from a human (monsters and demihumans may say them, see "Boss voices").

## Speaker personas: one enemy, one voice

Before this, every line was picked on its own, so one enemy changed voice actor from line to line
and a young and an old man drew from the same set. Now each line carries the **persona** of its
recording (`pja` / `pen` in `VoiceLines.json`: one voice actor, found offline by clustering the ja
and en audio, see `voice-research\out\persona\README.md`), and `Resources/VoicePersonas.json`
lists each persona's gender, age band, character name and fallback list.

- **Assignment.** On first sight in a fight an enemy gets one assignable persona (>= 8 lines,
  >= 5 events; ja 23, en 23) of its gender and body band, chosen by a stable hash of BNpcBase,
  BNpcName and entity id. Bands: BodyType elderly -> elderly (female elderly has no persona and
  takes female adult); child -> young; adult (or unknown) -> any young or mature/adult persona.
  A persona already held by an enemy on the field is skipped while another is free, and an enemy
  seen again in the same session gets its previous persona back when it is free.
- **Lines.** An enemy speaks only its persona's lines. With no line for the event (event chain
  included: kill -> victory -> taunt, engage -> taunt) it borrows from the persona's fallbacks in
  order (same gender only, same band first). Nothing in the chain: no voiced line (the labeled
  shout fallback is unchanged). fx lines never reach a human enemy, and lines of small personas
  never reach any enemy.
- **Kills and defeat taunts** check the speaker's persona chain; a killer whose chain has no
  kill-chain line hands the line to the nearest enemy whose chain has one.
- **Languages.** ja and en clients use their own persona set. **de/fr clients pick per line as
  before**: the personas were built from ja/en audio only and the de/fr recordings are other
  actors that were never clustered. Missing or unreadable persona data also means per-line.
- **Voice picker** (Advanced). One row per tracked enemy, nearest first: the enemy's name and a
  dropdown with **Auto** (shows the automatic voice in brackets) plus every assignable voice that
  fits it: every persona of its gender (all bands) for a human, every boss persona for a monster
  or demihuman. Labels are the character name when known ("Thancred", "Y'shtola"), else "Male
  voice 3 (mature)", "Female voice 2 (adult)", "Boss voice 4 (female)". Choosing one applies at
  once to every tracked enemy of that kind and is saved in `EnemyVoicePersonaOverrides`, keyed
  `"{BNpcBase}:{BNpcName}"` (or `"name:{name}"` when both are 0), so the same kind of enemy keeps
  it in later fights and sessions; **Auto** removes the entry. A saved human voice from a ja client
  maps to the same actor on en (same number); a choice that no longer fits (other gender, boss
  voice for a human) is ignored. **Preview** plays one random line of the enemy's current voice at
  the enemy, with its bubble, through the same file/text checks as a combat line (it does not
  hold up the fight's line gate). de/fr clients show a hint instead (no personas there).
  `EnemyVoiceAllowStoryCharacters` (config file only, default true): false leaves out personas
  with a character name (unnamed personas remain for both genders in ja and en).

## Taunts over the fallen: variety in one voice

With **Taunt the fallen** on, after a wipe each taunt is drawn uniformly from the speaker's own
persona's kill + victory + taunt lines together, avoiding the last 24 lines heard (when all are
recent, the one heard longest ago). The voice never changes: only when that combined set has
fewer than 3 lines is it topped up with the persona's fallbacks (same gender for humans, other
boss voices for monsters), one whole persona at a time. Guaranteed in-fight kill lines are
unchanged (kill -> victory -> taunt chain). Pool sizes per assignable persona (own set / after
top-up, min-median-max): humans ja 0-3-9 / 3-5-11, en 0-3-9 / 3-5-9; boss ja 1-3-15 / 3-4-16,
en 1-3-15 / 3-5-15.

Listen for: wipe against two or three humans and wait a minute: each keeps its own voice and
cycles through different lines; a persona with many kill/victory/taunt lines does not repeat one
until it has said the others.

## Boss voices for monsters and demihumans

The 412 fx lines (reverb/layered boss and primal recordings) are never given to a human. They now
voice every speaker `ResolveBodyAndAge` reports as non-human (monsters, beasts, demihumans).
Offline (voice-research `voice_lines.csv` speaker clusters `cluster_ja` / `cluster_en`), the fx
lines are grouped per language by speaker cluster and gender; clusters under 8 lines are merged
into the most similar group of the same gender (ECAPA centroid cosine). Result: **26 ja and 22 en
boss personas** (8-61 / 8-70 lines), ids `ja-bNN` / `en-bNN` in the fx lines' `pja` / `pen`, rows in
`VoicePersonas.json` with `"kind": "boss"` and a fallback list of the other boss personas (same
gender first, most similar first).

- Each monster/demihuman locks onto one boss persona on first sight, regardless of gender, by
  the same stable identity hash and on-field spreading as humans.
- A boss persona with no line for an event borrows from the most similar boss persona that has
  one. Own lines per event (personas with at least one, ja / en): Taunt 24/26, 19/22; Skill 26/26,
  22/22; Engage 22/26, 20/22; KilledPlayer 16/26, 17/22; LowHp 21/26, 19/22; Hurt 14/26, 11/22;
  Death 13/26, 12/22; Victory 8/26, 9/22; AllyDeath 2/26, 2/22 (2 lines; a chain without one stays
  silent and the labeled sounds play). Every boss persona reaches the kill chain through its fallbacks.
- Monster lines go through the normal events, the guaranteed kill line (a monster killer now says
  it itself) and taunts over the fallen.
- de/fr: no personas, so monsters keep only their labeled sounds.

Listen for: a pack of mixed monsters should sound like several different bosses, each keeping its
voice all fight; a female boss voice from a male-looking beast is expected (gender is ignored for
monsters). Report any fx line that sounds like a plain human voice (fx label wrong).

Average lines the answering persona has per event (Tests/Voice prints the full table; event
chain included, "borrow" = candidates that answer from a fallback): adult male (12 personas) Skill
4.6, Engage 5.3, LowHp 3.3, kill ~4.6, Death 1.1 (5 borrow), AllyDeath ~1.9 (7 borrow), Victory
~2.8 (3 borrow); elderly male (2) Skill 11.5, Engage 9.5, Death 1; adult female (9) Skill ~6.9,
Engage ~5.9, kill ~3.6, Death 1.1 (5 borrow). One enemy now repeats itself sooner than with the whole pool.

Listen for:
1. **Same enemy, one voice.** Pick one enemy and listen to all its lines through a fight: engage,
   skills, hurt, low HP, death. Same actor every time (rarely a similar-sounding fallback actor for
   a kill, ally-death or victory line). The voice picker shows its voice as "Auto (...)".
2. **Two adult men sound different.** Two or more adult male enemies in one pack should get
   different personas (the picker shows them) and sound like different people.
3. **Elderly bodies get elderly voices.** An old (BodyType elderly) man gets Gulool Ja Ja or the
   unnamed elderly persona (ja-002 / en-002, "If you would oppose our crusade, then come!"); listen
   to that one once: its band comes only from the age model (low confidence). An old woman gets an
   adult female persona (no elderly female exists). A child model gets a young persona.
4. **Kill line still guaranteed.** Every death of the player or a companion still gets a line.
5. **Session stability.** End the fight and pull the same pack again: each enemy keeps its voice
   unless two of them would now share one.
6. **Hand-picked voice.** In the picker choose another voice for one enemy, press Preview: the
   new voice at that enemy. Its next lines use it; reload the plugin and pull the same kind of
   enemy: still that voice. Set Auto: back to automatic.

# Phase 1 details (labeled shouts)

Phase-1 shouts play only for labeled sound entries and are the fallback whenever no voiced line
is spoken. The only always-on change is a pass-through wrapper around the two enemy attack
executors (`CombatModeRouter`); it forwards every call unchanged and only raises a notification.

## 1. Labels

The audition and labeling panel was removed from the UI. Labels come from
`CombatSimulator/Resources/VoiceEntryLabels.json` (`{"pc": {"death": [3]}, "mon/3091": {"attack":
[2, 5]}}`, keys `pc`, `npc`, `mon/{sepack}`), with any labels already recorded in the config
(`EnemyVoiceEntryLabels`) on top per key+label. To add labels, edit the resource file.

Checks that remain (in combat, fixed settings):

| # | Check | How | Record |
|---|---|---|---|
| A3 | Which slider controls it | Fight while moving each slider in System Configuration > Sound (Master, Voice, Sound Effects, and Player Sound Settings: Own / Party / Others). Voices use category "Other". | which slider(s) affect the voices |
| A4 | Plugin volume slider | Voice volume 1.0 vs 0.2 | audible difference yes / no |
| A6 | In-combat positional | Voices always play at the speaker: walk away from a talking enemy, turn the camera | louder near / pans with camera; inaudible at range? |
| A11 | Language fallback | (only if you can switch client language) German client plays `_de`; missing files fall back to `_en`, then `_ja` | ok / not |

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
| Idle taunt / remark | Engaged enemies (always on) | average every 4x **Pause between lines** per enemy (14 s at the default 3.5 s; 5-60 s), never more than every 20 s; a voiced remark (Chatter) about half the time when the voice has one | taunt |

Voice choice per enemy (cached per address+entity):
- Player clones (client-spawned humanoids) and companions: PC shout matching race, Hyur tribe
  (mid/hil) and gender. Variant a/b(/c) is a stable hash of the full appearance, because the
  creation voice is not in Customize and ClientStructs does not expose it.
- Humanoid field enemies: generic NPC voice `vo_npc{nn}`, stable per BNpcBase id (the old
  appearance-voice option was removed).
- Everything else: `sound/battle/mon/{ModelChara.SEPack}.scd` (not BNpcBase.SEPack, which is a
  different id space).

Rate limits (fixed): N closest speakers (4), max concurrent (3, counted by an assumed 1.8 s
per voice), global budget (1.5/s, burst 2), one voice per actor per 1.5 s, per-event cooldowns
(engage 30 s, skill 6 s, hurt 5 s, low HP 60 s, ally death 12 s, taunt 20 s). Priority within a
frame: death > killed player > victory > engage > low HP > ally death > skill > hurt > taunt,
nearer first on ties. An event with nothing labeled is spent silently (cooldown only).

Flags: `EnableEnemyVoices` (master, true), `EnemyVoiceVolume`, `EnemyVoiceBubbles`,
`EnemyVoiceGuaranteeKills`, `EnemyVoiceDefeatTaunts`, `EnemyVoiceDefeatTauntInterval`,
`EnemyVoiceLineGap`, `EnemyVoicePersonaOverrides` (voice picker), and config-file only
`EnemyVoiceAllyDeathRadius`, `EnemyVoiceAllowStoryCharacters`, `EnemyVoiceEntryLabels`. Removed:
`EnemyVoiceLines`, `EnemyVoicePositional`, `EnemyVoiceVolumeCategory`, `EnemyVoiceClosestSpeakers`,
`EnemyVoiceMaxConcurrent`, `EnemyVoicesPerSecond`, `EnemyVoiceHumanoidUseAppearance`,
`EnemyVoiceIdleTaunts`, `EnemyVoiceTauntInterval` (old values in a saved config are ignored).

Regression check with the flag **off**: a full fight (instant mode and Action Mode) behaves
exactly as before, and `/xllog` shows no "Enemy voices" lines.

Offline: `dotnet run --project Tests/Voice/Voice.csproj -c Release`.

## 3. Risks

- **PlaySound parameters.** Called with ClientStructs' documented defaults (speed 1, a9 0,
  midiNote -1, autoRelease true). If the signature drifts after a patch, the address check
  disables the feature; a wrong-but-resolved signature can still crash natively (managed
  try/catch cannot catch that). The owner confirmed playback in game; nothing is played
  before a fight is running, and the first voiced line is logged.
- **Volume category / bus** unverified (A3); fixed to "Other" now, no in-game switch. Voices may
  ignore the Voice slider.
- **Positional playback** always on (A6); there is no flat fallback in the UI any more.
- **Boss voices** on monsters: grouping by speaker cluster is acoustic only (not listened);
  a merged boss persona may mix two similar processed voices.
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
   sources are now exhausted (see "Second pass" above). Listening sessions (external sound
   explorer) are the remaining route; add rows to `Resources/VoiceLines.json`.
2. **Monster lines.** Monsters and demihumans use the fx boss personas (ja/en); de/fr monsters
   keep only their labeled SE packs.
3. **Creation voice.** If a reliable read of the actor's voice id appears, plug it into
   `VoiceSelection.PcShoutFromVoiceId` (already mapped and tested for ids 1-168).
4. **Ship default labels** from the owner's recorded set for `pc`, `npc` and common packs.
