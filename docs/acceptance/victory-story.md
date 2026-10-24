# Acceptance: victory presentation, stories, grab variety, spectator reactions

Branch `feat/victory-story` (superproject and `CombatSimulator/Dev/Experimental`).
Every new behavior is behind a setting that defaults **off**; with all of them off the
plugin behaves as before, except for the two unconditional bug fixes in section 1.

Offline tests (all pass):

| Test | Command (from worktree root) |
|---|---|
| Story profiles, solver, picker, cues | `dotnet run --project CombatSimulator/Dev/Experimental/Tests/Stories` |
| Bundled JSON (source-only) | `powershell -File CombatSimulator/Dev/Experimental/Tests/Stories/ValidateProfiles.ps1` |
| Weighted pick + celebration timing | `dotnet run --project Tests/VictoryCelebration` |
| Chatter pool / reaction rules | `dotnet run --project Tests/SpectatorReactions` |

Common setup: private build, Dev Experimental unlocked, death ragdolls on, a few selected
enemies (at least one humanoid and one creature where noted).

---

## 1. Bug fixes (unconditional, private build)

### 1a. Story approach no longer chases the corpse
- **What changed:** `StoryController` freezes the approach goal when a stage starts
  (`stageMoveAnchor`). It only re-anchors while the actor is still more than 1.5 m from the body
  and the body has drifted more than 0.25 m (still settling). If the approach times out, the
  stage still plays when the actor is within 0.3 m (`StoryMovementSolver.TimeoutToleranceRadius`);
  otherwise it stops as before.
- **Test:** Stories tab → profile *In Their Hands* → lose a fight → Play.
- **Expected:** the actor walks to the body and the grab stage starts. You should not see the
  actor pushing the body ahead of itself, and the "could not reach" stop should not appear.
- **Risk:** if the body is still sliding when the actor is closer than 1.5 m, the goal stays
  where it was. The 0.3 m timeout tolerance may need tuning.

### 1b. Bystanders no longer run in place during a story
- **What changed:** when a story starts, any living enemy or companion outside the scene that
  is still playing the run loop (`BaseOverride == 22`) is reset to idle. Playback pauses AI,
  and the AI is what normally clears that loop.
- **Test:** select 3+ enemies, die while some of them are still approaching, and let a story
  play (auto-play or Play).
- **Expected:** the other enemies stand idle. None of them keep a running animation in place.

---

## 2. Victory celebration (public and private builds)

- **Flags / UI:** Main window → the **Victory Celebration** header (just above NPC Scale):
  *Survivors celebrate* (`EnableVictoryCelebration`), *Companions celebrate your wins*
  (`VictoryCelebrationCompanions`), *Start stagger* (`VictoryCelebrationStagger`, 0–5 s).
- **What changed:** `AnimationController.PlayVictory` used to be empty. It now hands the
  survivors to `VictoryCelebrationDirector`:
  - Humanoids (ModelChara 0 or type 1) cycle through a weighted emote pool: victory pose,
    cheer, laugh, fist pump, flex, beckon, clap, humble triumph and battle stance. Each actor
    gets a random start delay and changes emote every 6–11 s, never repeating the same one twice
    in a row.
  - Creatures replay a one-shot from their own model family every 4.5–8 s. A creature with no
    family timeline is left alone.
  - Loops are re-applied after NPC AI each frame (`Plugin.cs`, after `npcAiController.Tick`).
    Without this, AI movement cleanup erases them.
  - The celebration ends and everyone returns to idle on revive, a new living enemy, reset,
    stop, or when the setting is turned off. An actor that Enemy Control takes over is handed
    back to it.
  - Private builds: stories still replace the legacy director. A story releases its own actor
    from the celebration; everyone else keeps celebrating.
- **Test (enemy win):** enable it, fight 3+ enemies (mix of humanoid and creature), and die.
- **Expected:** within about 1.5 s, each living humanoid enemy starts a different-looking emote
  at a slightly different moment and switches emote every several seconds. Creatures do an
  attack-style flourish now and then. Revive in place: everyone stops.
- **Test (party win):** with companions on and *Companions celebrate* checked, win a fight.
- **Expected:** living companions celebrate the same way. Starting a new fight stops them.
- **Unverified / risky:** facing toward the fallen player through the emote target id; whether
  any emote row is missing on your client (missing rows are skipped silently); creature
  flourishes are attack-shaped, so tune the interval if they look aggressive.

## 3. Bystander idle loops during stories (private)

- **Flag / UI:** Stories tab → *Bystanders idle* (`StoryBystanderIdle`).
- **What changed:** at story start, living humanoids outside the scene are added to the
  director's low-key pool (lookout, think, stretch, shrug, converse, at ease, battle stance),
  changing every 9–16 s. Actors that are already celebrating keep celebrating. Creatures stay
  still. The pool is released when the story stops.
- **Test:** lose to 3+ humanoid enemies with celebration **off** and this setting on, then play
  a story.
- **Expected:** the non-actor enemies idle-emote instead of freezing, and return to normal
  after the story.

## 4. Story selection pool and line variants (private)

- **Flags / UI:** Stories tab → *Random pool* (`StoryPoolEnabled`). When it is on, a **Pool**
  tree with one checkbox per profile appears (`StoryPoolExcludedIds`), plus a *Play random*
  button.
- **JSON:** profile `weight` (0–100, default 1; 0 means never picked from the pool). Line
  `variants` (up to 12 alternative texts, picked uniformly together with `text`). Both bundled
  stories now have variants in English and Japanese.
- **What changed:** with the pool on, auto-play rolls once per fight. It picks by weight among
  the included profiles that fit the outcome and the living actors, avoiding the previous pick.
  If nothing fits, nothing plays. With the pool off, the fixed `StoryProfileId` behavior is
  unchanged.
- **Victory stories:** a victory profile with actor `Winner` that moves or grabs is now
  performed by a living companion standing in for the player. Before, it was rejected.
- **Test:** enable the pool, uncheck the JP profiles, and lose several fights with auto-play on.
- **Expected:** the stories alternate. The same line shows different texts across runs.
  Unchecking everything → the status shows "No enabled pool profile fits…".
- **Risk:** EN and JP variants are both in the pool by default, so uncheck one language.

## 5. Grip selection (private)

- **JSON:** `grip.pairs` (1–4 `{actorBone, subjectBone}`) on a `Begin` or `Scripted` stage.
- **What changed:** `ResolveExternalGripPairs` is now implemented. The stage's pairs replace
  the saved Enemy Control pairs for that hold only, including a re-grab after a lost hold. Your
  saved configuration is never changed.
- **Test:** copy `examples.json.example` from the story folder ("Copy folder") to
  `examples.json`, then Reload. Play *Example: drag and toss*.
- **Expected:** the actor takes hold by the right hand/wrist (`j_te_r → j_te_r`), whatever pairs
  are saved in Enemy Control.

## 6. Scripted grab, drag/carry, and throw (private)

- **JSON:** `"grab": "Scripted"` attaches like `Begin` but keeps story control, so later `Keep`
  stages with a `move` drag or carry the body. An `End` stage may have
  `"throw": {forward, up, side}` (m/s; forward ±8, up 0–6, side ±4; total ≤ 8 m/s). The hold is
  kept through that stage's own approach, and the throw velocity is applied to every major body
  in the carrier's facing frame.
- **Reference profiles** (not loaded until renamed; `Resources/Examples/stories-variety.json`):
  *drag and toss* (defeat), *victory pose* (victory, player), *clear the field* (victory,
  companion stand-in drags an enemy body aside).
- **Test:** as in section 5, play *Example: drag and toss*; for victory, beat all enemies with a
  companion alive and play *Example: clear the field*.
- **Expected:** the hold attaches, the actor walks about 2.75 m away dragging the body, then
  releases it with a modest backwards toss. The player cannot steer or release during the
  scripted hold.
- **Unverified / risky:**
  - Drag speed (0.9) versus the grab servo force. A weak servo may let the body lag behind or
    detach.
  - During a carry the actor's yaw follows its movement. A forward throw would therefore throw
    into the actor, which is why the example uses `forward: -3`.
  - The release goes through the existing `CancelGrabAttack` path, the same one as a normal
    `End`.
  - Watch for the known parked-collider launch issue (kinematic teleport) when soft contact is
    off.

## 7. Spectator reactions (public and private)

- **Flags / UI:** Spectators tab → Crowd Chatter → *Outcome-aware finish lines*
  (`SpectatorOutcomeChatter`). When it is on, *Victory Lines* and *Defeat Lines* pools appear
  under Dialogue Pools (`SpectatorVictoryChatterLines`, `SpectatorDefeatChatterLines`).
  Spectators tab → Random Emotes → *React to key moments* (`SpectatorReactionEmotes`).
- **What changed:**
  - The decisive phase speaks from the victory or defeat pool, depending on which side still
    stands. A mutual wipe or an empty pool falls back to Decisive. The "Active dialogue" label
    shows the pool in use.
  - About 70 % of the drawn crowd plays a one-shot (cheer/clap/fist pump on a win;
    shock/surprise/panic/no on a loss) within 0.9 s when the outcome is decided
    (`CombatEngine.OnOutcomeDecided`). The same happens on story start and story grab
    attachment, plus the `crowd` cue (private). The spectators' own loops resume afterwards.
- **Test:** spawn a crowd, turn chatter and both settings on, then win one fight and lose one.
- **Expected:** finish lines match the result; the crowd reacts once per result.
- **Risk:** one-shot blending over a base-override loop is the same mechanism the crowd already
  uses, but it has not been watched on every emote.

## 8. Stage cue timeline (private, stretch)

- **JSON:** stage `cues`: `[{delay, animation?, shake?, crowd?}]`. Each cue fires once at its
  `delay` during the performance. `animation` is skipped while a hold owns the pose. `shake`
  (≤ 3 m/s) nudges the subject body. `crowd` ∈ `PlayerVictory | PlayerDefeat | StoryStarted |
  GrabStarted`.
- **Test:** *Example: victory pose* (cue crowd cheer at 0.4 s, fist pump at 2.4 s).
- **Expected:** the player poses, the crowd reacts, then the player does a fist pump.
- **Not done:** VFX cues (VFX/telegraph code is being reworked elsewhere) and multi-actor
  stories / a bystander enemy role.

## Hook points for voice

- Story lines: `StoryController.Speak` is the single place where a line's final text is chosen
  (after variant selection).
- Fight result: `CombatEngine.OnOutcomeDecided(bool playerWon)`.
- Crowd: `SpectatorController.React(SpectatorReaction)`.
