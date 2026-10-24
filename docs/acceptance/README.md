# Acceptance pass — interaction review work

Everything below is on branch `integration` (superproject) with the private module on its own `integration`
branch. Nothing has been run in game yet. Every new feature defaults **off**; only the bug fixes in section 1
are active without touching a setting.

Branches kept for selective rollback:

| Branch | Content |
|---|---|
| `fix/interaction-bugs` | Bug fixes from the review (section 1) |
| `feat/voice` | Enemy voices, phase 1 — [voice.md](voice.md) |
| `feat/victory-story` | Victory celebration, story pool/grips/scripted grab, spectator reactions — [victory-story.md](victory-story.md) |
| `feat/monster-skills` | Monster real skills, real-shape telegraphs, omen markers, casts, phases — [monster-skills.md](monster-skills.md) |
| `feat/ai-movement` | Enhanced enemy tactics — [ai-movement.md](ai-movement.md) |

Integration-only commits: "Idle until engaged" defaults off (public builds have no auto-engage control);
casters hold position while a snapshotted area cast channels.

## 0. Do these first (crash / safety)

1. **Voice native call.** Enable *Enemy voices*, open the audition panel, play one PC shout. If the game
   crashes or nothing plays, stop here and leave voices off — see [voice.md](voice.md) "audition checklist".
2. **Omen markers.** Enable *Monster real skills* + *Real-shape telegraphs* + *Native omen markers*, fight one
   coeurl/goobbue. Markers must appear (or fall back to outlines) without errors; turn markers off if they
   misplace — see [monster-skills.md](monster-skills.md).
3. With every new flag **off**, play one ordinary fight and confirm it looks like before apart from section 1.

## 1. Bug fixes (always on)

| # | Fix | How to check |
|---|---|---|
| 1 | Same-job enemies no longer share skill cooldowns | Spawn 3 enemies with the same weapon; they should not all use the same skill at the same moment |
| 2 | MP regen independent of framerate | Uncap fps (>120), drain MP, watch it come back |
| 3 | NPC/companion animation lock ticked once | Enemy attack cadence slightly slower than before (intended) |
| 4 | Enemy target no longer flips every frame | Party fight: enemies commit to a target instead of twitching between two |
| 5 | Slot assignment no longer drops an actor | Party fight with several melee enemies: no two stacked on one spot |
| 6 | AoE that kills its primary still splashes | Enemy AoE killing the player also hits adjacent companions |
| 7 | Victory/defeat can trigger again in one session | Win a wave, let map enemies join, lose → defeat presentation still runs |
| 8 | Enemy spells mitigated by magic defense | Caster enemy damage differs from melee of same level |
| 9 | Solo: no double-speed approach, no silent idle after leash | Solo fight with approach on: enemies walk at normal speed and always attack |
| 10 | Real map NPCs no longer hit from ~33y | Pull a field mob from range: it must close in before hitting |
| 11 | Solo approach walks straight when vnavmesh has no path | Disable/uninstall vnavmesh: enemies still approach and attack |
| 12 | Story grab not released by the attack key | During "In Their Hands", press attack in the take-control stage: hold stays |
| 13 | Story auto-play waits until the fight is over | Die while companions still fight: story waits for them |
| 14 | Failed story takeover keeps the controlled enemy | (rare) — no regression expected |
| 15 | Reset clears skill cooldowns, engage intent, action queue | Reset mid-fight: enemies open with fresh rotations |

Also changed without a flag (from feature branches): downhill float fix and per-frame Fighting Mode floor
sampling; facing written once per frame (enemies turn at the intended 5 rad/s, slower than before); melee hold
spots capped inside attack reach; player self-centred AoEs (Overpower etc.) now centre on the player.

## 2. Features (each defaults off)

| Feature | Where to enable | Detail |
|---|---|---|
| Enemy voices (phase 1) | Main window → Enemy voices section | [voice.md](voice.md) — silent until you label entries in the audition panel |
| Victory celebration | Main window → Victory Celebration | [victory-story.md](victory-story.md) §a |
| Story pool / line variants / grips / scripted grab / cues | Dev → Stories | [victory-story.md](victory-story.md) §b–h; example profiles in `examples.json.example` |
| Spectator reactions / outcome chatter | Spectators section | [victory-story.md](victory-story.md) §f |
| Monster real skills, telegraphs, omens, casts, phases, humanoid AoE | Map enemies → Enemy skills | [monster-skills.md](monster-skills.md) |
| Enhanced enemy tactics (+ sub-switches) | Party tab → Enhanced enemy tactics | [ai-movement.md](ai-movement.md) |

## 3. Interactions to watch when several features are on

- Tactics body-size spacing + monster skills: shapes add the caster hitbox, so big monsters should still
  reach — confirm their cones/circles land on you from their new stand-off distance.
- Monster casts + tactics: casters freeze during a snapshotted cast; strafing/charging resumes after.
- Victory celebration + voices: enemies cheer and shout at the same time; check the rate limit feels right.
- Victory celebration vs stories (private build): a story claiming the outcome takes its actor over.

## 4. Open decisions for you

- Voice phase 2: NpcYell line curation, which enemies get lines, speech bubbles ([voice.md](voice.md) end).
- Tuning constants (tactics timings, monster potency/cooldowns, celebration pools) after first play.
- Skipped this round: DoT/vulnerability statuses, story VFX cues, multi-actor stories, crowd steering by body size.
