# Enemy voices: phase 1 acceptance

Branch `feat/voice`. Main window section **Enemy Voices [Experimental]** (under Hit feedback /
NPC Scale). The master switch **Enable enemy voices** is off by default. While it is off, the
director's tick returns after one flag check, the client's sound function is never called, and
no hook or signature scan exists for this feature at all. The only always-on change is a
pass-through wrapper around the two enemy attack executors (`CombatModeRouter`); it forwards
every call unchanged and only raises a notification.

All playback is local: `SoundManager.PlaySound` on the client, with game files that already
exist (checked with `IDataManager.FileExists` before every call). Nothing is sent to the server,
no packets are built, and BattleTalk/DirectorUpdate are not involved.

**Out of the box nothing is audible in combat.** Only sound entries you label in the audition
panel are ever played. Label the PC and NPC families once (they share layouts), then label
monster packs as you meet them.

## 1. Audition checklist (do this first)

Enable the master switch, open **Audition and entry labels**.

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
| A12 | Voiced line | Source "NpcYell line", row 1000 (text shown), Play | plays and matches text |
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

Flags: `EnableEnemyVoices` (master, false), `EnemyVoiceVolume`, `EnemyVoicePositional`,
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
  try/catch cannot catch that). Mitigation: flag defaults off; A1 is the first test.
- **Volume category / bus** unverified (A3). Voices may ignore the Voice slider.
- **Positional playback** unverified (A5/A6); falls back to a setting.
- **Stop** reads a recycled SoundData slot; it checks entry number, playing state and file name
  before stopping, but a same-file sound started by the game in between could be cut.
- **Variant guess**: a clone of your character may not use your own creation voice (A13).
- **NPC voice gender** is not matched for humanoid field enemies (hash over 84 files).
- **Concurrency is time-estimated**, not read from the engine; long monster entries can overlap.
- **Skill voice in casts**: the cast-resolve suppression uses the cast time; a cast that is
  interrupted and immediately recast inside the grace could stay silent once.

## 4. Open questions for phase 2

1. **NpcYell line curation.** 3,854 voiced NpcYell rows exist, but the sheet has no voice/speaker
   column and only 27 Name values, so lines cannot be matched to arbitrary enemies. Options: a
   hand-curated table (line id -> event, gender, tone, "generic soldier"/"beast tamer"/...)
   built with the audition panel's NpcYell source; or restrict lines to the enemies whose
   BNpcName matches a Name value.
2. **Which enemies get lines.** Only named/elite, one per pack, or any humanoid? Monsters stay on
   SE packs. Lines should probably replace (not add to) the shout for the same event.
3. **Speech bubbles.** `VoiceDirector.VoicePlayed(address, event, source, entry)` is the seam; a
   bubble sink (the dev module's local speech bubble) can subscribe once a text source exists
   (NpcYell text for curated lines). Shouts have no text.
4. **Creation voice.** If a reliable read of the actor's voice id appears, plug it into
   `VoiceSelection.PcShoutFromVoiceId` (already mapped and tested for ids 1-168, variant rule
   pending A13).
5. **Companion voices** (they resolve already; no events are wired for them).
6. **Ship default labels** from the owner's recorded set for `pc`, `npc` and common packs.
