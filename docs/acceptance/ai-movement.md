# Acceptance: enemy movement, positioning and target selection

Branch `feat/ai-movement`. Offline math tests: `dotnet run --project Tests/AiMovement/AiMovement.csproj -c Release`
(112 checks: terrain follow, hitbox spacing, melee ring, flank cost, circling/flee goals, turn rate, threat).
Nothing here sends network traffic; all movement goes through the existing client-side `MovementBlockHook` writes.

## Where the switches are

Main window > **Party** tab > collapsing header **Enhanced enemy tactics** (below "Ranged attack range").

- **Enable enhanced enemy tactics** (`EnhancedEnemyTactics`, default **off**). Master switch. While it is off,
  every behaviour in section B is inert and enemies move as before.
- Sub-switches (each default **on** except "Idle until engaged", which defaults **off** because public builds
  have no auto-engage UI; only effective with the master on, greyed out otherwise):
  Idle until engaged, Spread enemies around the player, Body-size aware spacing, Circle while holding range,
  Threat-based aggro, Take turns in melee, Call for help / flee at low HP, Reposition after attacks,
  Big monsters turn slowly, Charge from far away.

To isolate one behaviour: master on, every other sub-switch off.

Common setup for the tests below unless stated otherwise: default config (Combat Companions **on**, Target
Approach on, vnavmesh approach on), a flat open area, Fast Combat or spawned virtual enemies. "No companions" =
clear companions in the Party tab.

---

## A. Unconditional bug fixes (no switch)

### A1. Downhill float (commit 8650f11)
- **Changed:** terrain-follow descent for enemies and companions was capped at `max(0.02, 1.5·dt)` per frame
  (frame-rate dependent, ~1.5 y/s, far below the ~9 y/s run). Descent is now capped by horizontal distance moved
  (`EnemyMovementMath.FollowTerrainY`, up to ~60° slopes); a standing actor still settles at 1.5 y/s.
  Applies where the height correction already ran (vnavmesh path, or NPC collision + corpse traversal).
- **Test:** stand at the bottom of a hill, spawn 3 melee enemies at the top (or pull them down a ramp). Watch them
  run down. Repeat with the frame limiter at 30 and at 144 fps. Also let a companion chase an enemy downhill.
- **Expected:** feet stay on the slope the whole way, same at both frame rates. Stepping off a corpse while
  standing still still eases down rather than snapping.
- **Unverified:** very steep drops (>60°, ledges) now descend at the 60° rate rather than 1.5 y/s — should read as
  "walking down", but check that nobody visibly drops through a staircase.

### A2. Fighting Mode ground height (e97d1af)
- **Changed:** `FightingAiController` latched the first ground Y. Each frame `FightingModeController` now casts one
  short downward BG-collision ray under the enemy (`TerrainHeightService.TrySampleGround`) and the AI keeps its
  initial clearance (clamped ±0.5y). Jump-over arcs ride on the tracked floor.
- **Test:** start Fighting Mode against an enemy on sloped or stepped ground; let it approach, retreat and jump over.
- **Expected:** the enemy follows the ground along the lane instead of floating or sinking; jumps land on the floor.
- **Risk:** the ray starts 3y above the enemy; under a low overhang/bridge it may hit the underside of nothing
  (miss → keeps last height) or a ledge (snap). Watch for one-frame height pops near walls.

### A3. Solo-ring hold spot outside reach (5e94b21, requested by coordinator)
- **Changed:** in the solo ring (`ComputeApproachGoal`), for melee-intent enemies the hold ring and its release band
  are capped at the melee swing reach minus 0.25y. Default settings (1.5y hold, ~4y reach) are unchanged.
- **Test:** party off (or "solo formation when no companions"), Target Approach Distance (or Party melee range)
  raised to 3.0–4.0y. Engage a basic melee enemy, then sidestep ~1–1.5y while it holds.
- **Expected:** it keeps attacking; previously it could hold a spot just out of reach and never swing.

### A4. Facing written once per frame (ba85bfd)
- **Changed:** `TickCombat` / `Engaging` no longer rotate an enemy the approach pass ticks this frame
  (`ApproachOwnsRotation`); the approach is the only writer (it faces the path while moving, the target while
  holding; the no-plan branch now faces the target too).
- **Test:** default config, 4–5 enemies chasing you while you run in circles; then stand still.
- **Expected:** no facing jitter while they move; they turn to you when they stop.
- **Note:** a held enemy previously turned up to 3× per frame (effectively 10–15 rad/s). It now turns at the intended
  5 rad/s — slightly less snappy when you dash behind it. Intended; flag if it feels sluggish.

---

## B. Enhanced enemy tactics (master switch + sub-switch)

### B1. Idle until engaged (2a49c83) — `EnemyTacticsRespectEngage` — review bug 6
- **Changed:** party mode called `EngageNpc` on every enemy every frame, so NPC auto-engage/delay did nothing and
  enemies had no idle phase. With this on, that forced engage is skipped and Idle enemies do not walk. Enemies
  engage on a hit (player or companion), Dev "NPC Auto Engage" + delay (private build only — the public build has
  no UI for auto-engage), map-enemy sensing, or B6's call for help.
- **Test:** master + this only. Spawn 3 enemies 15y away. Wait 10s. Hit one.
- **Expected:** all stand still and idle; the hit one engages after ~0.5s. With B6 also on, others within 10y join.
  With Dev auto-engage on (delay 3s) all engage ~3s after Start.
- **Risk:** in the public build with this on, enemies never start the fight by themselves — by design, but users
  may read it as "enemies broken". Companions still attack idle enemies (which engages them).

### B2. Spread enemies around the player (0a700d5) — `EnemyTacticsPlayerSlots` — review bug 1 / feature c
- **Changed:** the player is added to `PartyEngagePlanner` as a Friendly target node (never an actor), so enemies
  targeting the player get MCMF slots like companion targets instead of the per-actor pursuit point. Slot cost gets
  `Frontness × 1.5` relative to the player's facing (mild flank/rear lean). Runs in default config even with zero
  companions (that path goes through the planner).
- **Test:** master + this only, no companions, 5 melee enemies approaching from one side. Stand still; then turn 180°.
- **Expected:** enemies fan out around you (front arc first, some to flanks/back) instead of stacking ~0.3y apart in
  front. Sticky slots: turning around does not immediately reshuffle them.
- **Risk:** the player node also counts in companion slot density/battle axis, so companion placement shifts
  slightly when this is on. Ranged enemies on the player now use the backline row (≥5y, on the enemy side).

### B3. Body-size aware spacing (06d9402) — `EnemyTacticsHitboxSpacing` — review bug 3
- **Changed:** `HitboxSpacingExtra = max(0, rA−0.5) + max(0, rB−0.5)` (only the part of each hitbox radius above a
  humanoid's) is subtracted from centre distance in every range test (`TickCombat`, `TickPartyApproach`, companion
  stop range) and added to every stand-off (planner rings and backline, solo ring). Melee slots become evenly spaced
  with neighbours ≥ max(1.2y, 2·radius+0.2) apart (6–7 slots on a 1.5y ring instead of 8 overlapping ones).
- **Test:** spawn one large monster (hitbox ≥2y, e.g. a big virtual enemy) plus 2–3 companions; then 6 humanoid
  enemies on you with B2 on.
- **Expected:** companions stop at the monster's surface instead of inside it, and still attack; the monster stops
  at your edge, not with you inside its body. Humanoid packs keep the same distances as before but no longer overlap.
- **Risk / unverified:** raw `GameObject.HitboxRadius` is used (same as the player hitbox resolver); with NPC Scale
  overriding the visual size, the radius may not match what you see. Action Mode telegraph/hit checks were **not**
  changed (out of scope): a large enemy now stands farther from you, so its telegraph from centre may need to be
  longer to reach — check large-enemy melee still lands in Action Mode.

### B4. Circle while holding range (ea95fb0) — `EnemyTacticsStrafe` — feature a
- **Changed:** in `TickPartyApproach`'s hold branch, every 2.5–5s (per enemy) a 50% roll sends the enemy on a
  30–60° arc around its target at 55% run speed, clamped into [0.75·hold, hold] and away from other bodies.
  Cancelled by casts, swings, telegraph windups or after 2s.
- **Test:** master + this, 3 enemies on you, stand still 30s.
- **Expected:** enemies occasionally shuffle sideways around you, facing their walking direction, then turn back;
  attacks still come at the normal rate.
- **Tuning:** constants in `Ai/NpcAiController.Tactics.cs`. Uses the run animation at reduced speed (no strafe
  anim) — may look like a slow jog.

### B5. Threat-based aggro (148b517) — `EnemyTacticsThreat` — feature b
- **Changed:** `CombatCompanionManager` keeps per-enemy decaying threat (τ=15s); each hit adds damage, ×3 for tanks
  (player `IsTank`, companion `State.IsTank`). Target scoring uses the candidate's share of the top threat × 150
  instead of `recent DPS × 0.25`; switching needs +60 instead of +25. Assignment/pressure/graph terms unchanged.
- **Test:** clone a tank and a DPS companion, 3 enemies. Let the tank hit first.
- **Expected:** most enemies settle on the tank and stay on it even when DPS out-damages it ~2×; a fresh enemy that
  nobody hit still distributes by pressure. Stopping attacks on an enemy lets its interest drift away over ~15–30s.
- **Note:** only applies while companions are alive (with none, enemies always target the player).

### B6. Take turns in melee (a5203b9) — `EnemyTacticsStagger` — feature d
- **Changed:** each frame (party approach) at most 3 melee-intent engaged enemies per target hold an "attacker
  place"; places already held are kept; free ones go to the closest waiter. Waiters stand on a ring hold+3.5y out
  facing the target, spaced apart. First engagement gets +0–1.2s random engage delay.
- **Test:** master + this (+B2), no companions, 6 melee enemies on you. Kill one of the three in melee.
- **Expected:** three fight, three wait ~5y out; within a frame of the kill the closest waiter walks in.
- **Risk:** a waiter that was already in range when demoted may land one swing while walking out.

### B7. Call for help / flee at low HP (83c18a6) — `EnemyTacticsFlee` — feature e
- **Changed:** an enemy entering the fight wakes Idle allies within `NpcBehavior.AggroRange` (10y when 0 — the usual
  case), one hop. Below 20% HP, enemies whose stable per-id roll is <0.35 flee once: run (80% speed, no attacks,
  cast cancelled) to just behind the nearest other living enemy 3–30y away, else to their spawn point; stop after
  4s or on arrival, then re-engage.
- **Test:** (help) B1 + B7, two clusters of enemies 8y and 25y apart; hit one in the near cluster.
  (flee) 6 enemies, bring each to ~15% HP.
- **Expected:** the near cluster joins, the far one stays idle. Roughly a third of enemies log "X flees!", run behind
  an ally, then come back.
- **Risk:** without B1, party mode engages everyone anyway, so the help call is only visible in solo mode or with B1.

### B8. Reposition after attacks (4311225) — `EnemyTacticsReposition` — feature f
- **Changed:** in the hold branch: after an attack finishes, a ranged/caster-intent enemy has a 50% chance (8s
  cooldown) to run 25–45° around its target to a clear spot inside its band. Any enemy closer than 0.8y to another
  body steps up to 0.7y away (kept in band, 1.5s cooldown).
- **Test:** 2 ranged + 2 casters; walk into the middle of a melee pack.
- **Expected:** ranged enemies occasionally shift after shooting/casting, never mid-cast. Melee enemies you walk into
  take one small step out rather than standing inside you.

### B9. Big monsters turn slowly (99d636a) — `EnemyTacticsHeavyTurn` — feature g
- **Changed:** facing turn rate = 5 / (radius/0.5)^0.75 rad/s, floor 1.2 (humanoids unchanged).
- **Test:** large monster, run circles around it at melee range.
- **Expected:** it visibly lags turning to you; humanoids track as before.
- **Unverified:** whether any attack/telegraph uses facing; if telegraph direction is computed toward the target, slow
  turning is purely visual and you may see a cone fire "sideways" relative to the body.

### B10. Charge from far away (70f3315) — `EnemyTacticsCharge` — feature h
- **Changed:** an engaged enemy >15y (body-adjusted) from its target moves at 1.8× approach speed until within 8y
  (solo ring and party approach).
- **Test:** engage an enemy from 25–30y.
- **Expected:** it sprints in, slows to a normal run around 8y. Run animation speed is not changed, so feet may slide.

---

## Not done / notes
- Movement constants (strafe timing, wait ring, flee thresholds, threat weights) are code constants in
  `Ai/NpcAiController.Tactics.cs` and `Companions/CombatCompanionManager.cs`, not config — tune after in-game review.
- LocalSteering separation still ignores hitbox radius.
