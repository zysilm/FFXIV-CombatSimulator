# Enemy skills — acceptance checklist

Branch `feat/monster-skills`. Covers what enemies use, how it is telegraphed and how it resolves.
**Every new option defaults off.** With all of them off, enemies behave as before, except for the
one data fix in section 0. All options live in **Map enemies**, right under the **Humanoid skills**
slider (heading "Enemy skills"). Kit options (monster skills, humanoid AoE, HP phases) only affect
enemies **spawned or registered after** you change them. Shape, omen and cast options take effect
straight away.

Offline tests: `dotnet run --project Tests/MonsterSkills/MonsterSkills.csproj -c Release` (60 checks:
cast-type mapping, geometry, kit selection, snapshot shapes, HP phases).

## Good test subjects

Use the Virtual Enemies spawner by name. Kits below come from the real data with 3 skills per monster.

| Spawn | Family | Expected kit (shape, radius, cast) |
|---|---|---|
| coeurl / jungle coeurl | m0017 | Charged Whisker (circle on self r3, 3s), Megablaster (cone r8, 2.5s), Microblaster (single) |
| goobbue | m0054 | Goobbue's Grief (circle on self r6, 3s), Moldy Sneeze / Sickly Sneeze (cone r6, 2.5s) |
| diremite / banemite | m0001 | Realm Shaker (circle on self r3, 3s), Silkscreen (**line** r15 x4, 2.5s), Sticky Web |
| northern vulture / arbor buzzard | m0007 | Wing Cutter (**60° cone** r6), Yolk Shower (circle r6), Peck |
| deathgaze | m0064 | Bombination (circle r6, 4s), Plague Swipe / Stormwind (cone r12, 3s) |
| territorial cyclops | m0058 | 100-tonze Swing (circle r8, 4s), Glower (**line r17**, 3s) |
| mandragora | m0088 | three circles on self r7, 3.5s |
| wild jackal / watchwolf | m0041 | Sanguine Bite / Nerve Sever (short line), Foul Bite |
| a humanoid with a greatsword or a BLM/WHM weapon | job kit | for the humanoid AoE option |

Of the 202 monster families that have Action rows, 166 get a kit (154 with an area skill, 149 with
a cast and 137 with an omen). That covers 1,244 of the 3,606 monster ModelChara rows BNpcs use.
Families that only resolve through skeleton templates have no Action rows. They keep auto-attacking
only, as before.

---

## 0. Cast-type mapping fix (always on)

**What changed.** `CastTypeMapping` maps cast types from the data (measured on 5,138 monster rows
plus all player damage actions):
- Type 2 is centred on the target only when the action can be aimed at a hostile. Self-targeted
  type 2 (Overpower, Holy, most monster circles) now centres on the caster.
- Types 12/13/8 now map to rect/cone/charge instead of single target.
- 6 (custom) and 11 (cross) stay single target.
- Cone angle comes from the omen name (`gl_fan120` means 120°). It is 90° when there is no omen,
  which covers every player cone.
- Donuts get a safe hole (40% of the radius, which is a heuristic: the sheet has no inner radius).
- The omen VFX path is recorded only when the file exists.

**Visible effect with every option off.** Player PBAoEs that are self-targeted type 2 (Overpower,
Steel Cyclone, Total Eclipse and similar) now splash around **the player** instead of around the
primary target. Their virtual potency moves by a few percent (the CircleSelf factor is 0.95, Circle
was 0.90). Player cones and lines are unchanged.

**Test.** Turn Action Mode off and use Overpower (or a similar move) next to one enemy with a second
enemy 4y behind you. The enemy behind you should now be hit. Before the fix, the circle centred on
the target.

## 1. Monster real skills — `Monster real skills` (+ `Monster skills` count, default 3)

**What changed.** A monster with no job reads its kit from its own model family's Action rows, the
same rows its animations come from. Rules (`MonsterKitBuilder`):
- Spells and weaponskills only.
- Rows dropped: status-only, ally-targeted, room-wide (radius >15, line >20), casts over 6s,
  charges, ground circles, and cross/custom shapes.
- Area skills are preferred, plus one single-target attack.

Skill values:
- Potency: 1.2–3× the monster's auto-attack, with the 3× ceiling only for casts of 2s or more.
- Cooldown: max(recast, 10s) plus the cast time, kept between 8 and 40s.
- Engage range: kept in melee (2.5–5y), so a melee monster never switches to ranged.
- Style: melee, so the windup plays **that skill's own** model timeline.
- A whiffed real skill replays its own timeline instead of a random one.

On its own this option keeps skills **instant**, like the humanoid kits. The cast comes back with option 4.

**Test.** Turn the option on, then spawn a coeurl or goobbue.
- The log shows `MonsterActionKitProvider: ModelChara=… → N skills: …`.
- In combat the monster sometimes uses Megablaster or Sneeze. Its name appears in the combat log
  ("uses Megablaster"), and the animation is that skill's, not the auto-attack's.
- A companion standing in front of the monster is splashed by the cone.

**Unverified / risky.**
- In normal (non-Action) mode, `ActionEffectHandler.Receive` now carries the monster's real action
  id instead of 7. Animation and VFX scheduling are unchanged: the skill's own timeline plus its TMB
  VFX. Watch for **doubled** skill VFX. If you see it, the native Receive is also spawning that
  action's TMB effects.
- Some status skills slip through as damage (Slumber Cloud, Sticky Web variants that have an attack
  type or an omen).

## 2. Real-shape telegraphs — `Real-shape telegraphs`

**What changed.** When an enemy commits an attack, the attack's real area is snapshotted:
- shape: cone, rect, charge, donut, circle on the caster or circle on the target
- the facing toward the target at that moment
- the size, plus the caster's hitbox for cast types 3/4/5

That one snapshot is drawn (`EnemyAreaOverlay`, any mode) and used for the hit test at the strike.
The rules at the strike:
- Everyone hostile inside is hit.
- The primary target is hit only if it is still inside.
- A dodged primary no longer saves companions who are inside.
- A player's guard spares only the player. Companions inside still take the hit, which fixes guard
  cancelling the whole action.
- Single-target strikes keep the old 2.5y circle on the target, drawn only when "Show telegraph
  circles" is on.
- Instant area skills in normal mode use the live area for splash, but only while the primary is
  inside it, so they never whiff.

**Test (Action Mode on).**
1. Spawn a goobbue with options 1 and 2 on.
2. A **cone** appears from the goobbue toward you during the windup, not a circle around you.
3. Sidestep 90° out of the cone before it fills. The result should be dodged, with no damage.
4. Stand behind the goobbue. The cone points at where you *were* when it committed.
5. With a companion inside the cone and you guarding, the companion takes damage and you don't.
6. Diremite Silkscreen: a 15y × 4y rectangle. Step sideways 2.5y to dodge it.

**Unverified.** The overlay's ground height is the caster's Y. On slopes, long lines may float or
sink a little.

## 3. Native omen markers — `Native omen markers` (needs 2) and `Outline over omen`

**What changed.**
- Each area gets the action's Omen-sheet ground marker. If the action has none, it gets a generic
  marker of the same shape: `general_1bf` circle, `general02f` rect, or `gl_fanNNN_1bf` fan.
- The marker is placed and turned to match the snapshot. Rects scale to (half width, 1, length);
  circles, cones and donuts scale to (r, 1, r).
- The transform is written every frame, and the marker is removed when the area resolves.
- If the marker can't be shown, the drawn outline takes over.
- `Outline over omen` draws both, so you can check that they line up.

**Crash-risk mitigations.**
- A path is used only after `IDataManager.FileExists` confirms it, checked twice: once when the
  action data is built and again before spawning. All 57 omen paths referenced by monster rows
  exist. Of the generic fans, 030/060/090/120/150/180 exist; 270 and 135 do not, and those fall
  back to the outline.
- The VFX is bound to the **local player**, not the casting NPC: other plugins hooking actor VFX
  creation have crashed on our modified NPCs. The binding only gives Penumbra a collection; the
  transform is overwritten.
- The pointer lifetime goes through the existing `CreateManualVfx` / `IsManualVfxAlive` /
  `RemoveManualVfx` set. The dtor hook drops pointers the game frees.
- Every area marker has a hard lifetime of its duration + 1s, so a missed removal can't leave an
  omen on the floor. Reset clears everything, and plugin unload removes live omens.

**Test.**
1. Turn on 2 and 3, plus `Outline over omen`.
2. Coeurl Megablaster: the red game fan lines up with the outline.
3. A cyclops Glower line covers the drawn rectangle.
4. Turn the outline option off. Only the game marker shows.
5. Zone out mid-cast: no crash, and no lingering marker.

**Unverified.**
- Whether an actor-bound omen with an overwritten transform renders at the written position: it
  could snap to the player. If it does, turn the option off. The outline still works.
- Rect omen origin and direction (some `general02f` files may be centred instead of starting at
  the origin).
- Fan omens might need an extra yaw offset.

## 4. Telegraphed casts — `Telegraphed casts` (needs 2) and `Longest cast` (default 5s)

**What changed.** Skills that have a drawable area get their real cast time back, capped at
`Longest cast`. Skills without an area stay instant. This applies to monster skills and to humanoid
AoE spells.

In normal mode:
- The area is snapshotted at the **start** of the cast.
- It is drawn for the whole cast, with the enemy's cast bar (the existing `HpBarOverlay` cast bar)
  filling.
- The cast keeps running even if you walk out of range, so stepping back doesn't pause it.
- At the end it resolves against the snapshot. If everyone left, the skill whiffs: its cooldown is
  spent and its own timeline plays with no damage.

In Action Mode, the cast becomes telegraph lead-in, and the HP bar shows a cast bar during the
lead-in.

Interrupts (`ProcessPlayerAction`): Interject (7538) and Head Graze (7551) stop casts flagged
interruptible (all generated skills are), and Low Blow, Leg Sweep and Shield Bash stop any cast.
"You interrupt the cast." is logged and the skill goes on cooldown.

**Test (normal mode).**
1. Turn on 1, 2 and 4, then spawn a deathgaze or cyclops.
2. "begins casting Bombination…" appears with the cast bar filling and the circle drawn for about 4s.
3. Walk out: the cast finishes and whiffs, with no damage.
4. Stay in: you take the hit.
5. Interject mid-cast: the bar disappears and "…is interrupted" is logged.

**Unverified / risky.**
- Enemy **movement during a cast is not blocked.** The approach code belongs to another branch and
  doesn't check `IsCasting`, so a monster may keep stepping while its area stays where it was
  snapshotted.
- Whether role actions reach `ProcessPlayerAction` in your input setup. Action Mode's own player
  pipeline does not call it.
- No cast animation plays during a normal-mode cast; the enemy holds its action-locked idle.

## 5. HP phase skills — `HP phase skills`

**What changed.** After the kit is built (monster or humanoid):
- The longest cast of 2s or more is held back until the enemy is at **≤50% HP**. Area skills get
  first pick, and this only happens when the kit has another skill.
- A copy of the biggest area skill, named "X (Enraged)", unlocks at **≤30% HP**. It has 1.5×
  potency and a cooldown of at least 30s.

**Test.**
1. Turn on 1, 5 (and 2 and 4 to see it).
2. Spawn a cyclops: the kit log line lists 3 skills, and the enraged copy is added as a fourth.
3. Above 50% HP, 100-tonze Swing is never used.
4. Below 30%, "100-tonze Swing (Enraged)" appears in the cast bar and the log.

## 6. Humanoid AoE skill — `Humanoid AoE skill`

**What changed.** If a job kit ranked only single-target skills, the job's newest GCD area skill
takes the weakest slot (or an empty one).
- Caster-centred AoEs such as Overpower, Total Eclipse, Holy and Death Blossom now qualify. They
  are told apart from self-targeted party heals (Medica, Helios, Succor) by `NeedToFaceTarget`.
  Measured: every facing AoE GCD is damage and every non-facing one is a heal, apart from a few BLU
  heals.
- Melee-weapon humanoids still get no magic or long-range skills.

**Test.**
1. Turn the option on, then spawn a humanoid with a greataxe (WAR).
2. The log line for its kit includes Overpower, Steel Cyclone or Decimate.
3. With 2 on, it draws a circle around the enemy.

## Not done

- **Statuses (DoT / vulnerability).** This was a stretch goal and was skipped. `TickStatusEffects`
  still only counts down.
- No per-skill tuning table. Potency, cooldown and phase thresholds are constants in
  `MonsterKitBuilder` and `EnemyPhasePlanner`.
