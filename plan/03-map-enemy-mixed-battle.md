# Map Enemy Mixed Battle Plan

## Goal

Replace the old solo/non-party enemy flow with one mixed-battle model.

The player is always a friendly combat actor. Party companions are optional
friendly actors. Virtual enemies, recipe enemies, and real map enemies all enter
the same enemy pool. If there are no companions, the mixed battle is simply a
player-only battle.

The user-facing rule should stay simple:

- Start combat.
- Enemies from the selected source enter the enemy pool.
- Map enemies can be sensed and joined naturally when enabled.
- The player attacks whatever they target; if that map enemy is allowed to join,
  it is registered and becomes the current sim target.
- Existing recipes keep their current behavior unless they explicitly include
  map enemies.

## Product Rules

### Unified Battle

There is no separate product concept of "solo mode" versus "party mode".
Internally, the mixed battle model owns target selection, target pursuit,
positioning, and enemy membership.

### Map Enemies

Map enemies are real BattleNpc objects already present in the game world. They
may enter the Combat Simulator enemy pool through sensing or first attack.

Map enemy sensing only runs while combat is active. It never mutates map NPCs
while the simulator is stopped.

When a map enemy is sensed:

- It is registered into the enemy pool.
- It targets the first or nearest friendly actor that caused detection.
- It remains in the battle until death, reset, or stop.
- It counts against a map-enemy-specific limit.

When the player attacks an allowed unregistered map enemy:

- The target is registered immediately.
- The action is routed to Combat Simulator.
- The new enemy becomes the player's current custom sim target.
- No server action should be sent for that intercepted attack.

### Recipes

Existing recipes must keep their current behavior. They should not start sensing
or pulling map enemies by default.

Add a recipe enemy source for map enemies. Only recipes that include that source
enable map enemy sensing for recipe battles.

Recipe map enemy source options should cover:

- max count
- sense range
- level
- HP multiplier
- behavior type

These options should mirror the simple "sense players" idea used for companions,
not create a separate combat mode.

### GUI Direction

Collapse the old target-related controls into one "Map Enemies" section.

Remove or hide these product-level controls:

- Aggro Propagation
- Target Formation
- Active Targets as a primary setup section

Keep existing internal defaults for target approach and formation behavior.
Those settings are already good enough and should not remain exposed as a
separate user-facing mode.

The Professional Mode target area should become "Map Enemies" with:

- enable map enemy sensing
- sense range
- max map enemies
- default level
- HP multiplier
- default behavior

The active enemy list can remain as status/debug information if useful, but it
should not be the main configuration workflow.

## Implementation Phases

### Phase 1: Audit and Boundaries

- Trace current custom targeting, use-action interception, NPC selection,
  aggro propagation, recipe spawning, companion sensing, and party planner paths.
- Identify all GUI controls tied to aggro propagation, target formation, active
  targets, and NPC defaults.
- Document current settings that should become fixed internal defaults.

### Phase 2: Enemy Registration API

Create a unified way to register real map enemies without depending on
`targetManager.Target`.

Required behavior:

- register from current target
- register from an object table entry
- preserve original object kind/subkind and position
- create behavior/profile from configured defaults
- add to selector and combat engine consistently
- avoid duplicate registration
- enforce map-enemy-specific max count

This should reduce direct calls to one-off selection functions in combat logic.

### Phase 3: First-Attack Join

Fix the current custom targeting conflict.

When custom targeting is enabled and there is no locked sim target:

- if the native target is an allowed unregistered map enemy, register it
- lock it as the current sim target
- enqueue the player action into Combat Simulator
- return true from the hook so the action does not reach the server

When map enemies are not allowed for the current battle, preserve current recipe
behavior and do not pull outside enemies into existing recipes.

### Phase 4: Map Enemy Sensing

Add a service/controller that runs during active combat and senses map enemies
near friendly actors.

Friendly actors:

- local player
- living party companions

Detection behavior:

- scan BattleNpc objects within range
- skip already-registered enemies
- skip players and invalid objects
- register up to the configured map enemy limit
- choose initial target as the nearest friendly actor, with stable tie breaking
- initialize AI as engaged

This replaces aggro propagation.

### Phase 5: Recipe Map Enemy Source

Extend recipe data so a recipe can include map enemies explicitly.

Existing recipe files must remain valid without edits. Missing map enemy source
means no map enemy sensing in that recipe.

When a recipe includes map enemies:

- start recipe normally
- spawn configured companions and virtual enemies
- enable map enemy sensing for that recipe using recipe settings
- do not auto-add map enemies before they are sensed or attacked

### Phase 6: Remove Aggro Propagation

Remove old aggro propagation logic from player action processing and GUI.

The new sensing controller becomes the only automatic way map enemies join due
to proximity. First-attack join remains the direct player-initiated path.

### Phase 7: Remove Target Formation GUI

Hide or remove target formation controls from Professional Mode.

Keep internal defaults:

- NPCs can target the player
- target approach remains enabled
- vnavmesh target approach remains enabled when available
- solo target formation option should no longer be user-facing

If any of these settings are still needed internally, leave the configuration
fields in place for compatibility but stop presenting them as a mode.

### Phase 8: Collapse GUI to Map Enemies

Replace "NPC Defaults", aggro controls, and primary active-target workflow with
a "Map Enemies" section.

The section should expose only the settings needed to understand map enemy
sensing:

- enabled
- sense range
- max count
- level
- HP multiplier
- behavior

Keep active enemy status compact and secondary.

### Phase 9: Verification

Build only Release.

Required manual test scenarios:

- Professional Mode start, target a map enemy, attack: enemy joins and becomes
  current sim target.
- Professional Mode start with map sensing enabled: nearby map enemies join when
  near player or companion.
- Existing recipe without map enemies: no outside map enemy joins by proximity.
- Existing recipe without map enemies: attacking virtual recipe enemies still
  works.
- Recipe with map enemies: sensed map enemies join up to the recipe limit.
- Stop/reset restores real map enemies.
- Custom target switching still works after sensed enemies join.

## Risks

- The use-action hook must never allow intercepted simulated attacks to reach
  the server.
- Real map enemy object references can disappear from the object table; selected
  NPC validation must continue to prune stale references safely.
- Recipe battles need a clear "map enemies allowed" flag derived from recipe
  contents, not from a hidden global mode.
- Removing GUI should not remove saved config fields immediately; old configs
  should continue to load.

## Completion Criteria

- One mixed-battle enemy pool handles virtual, recipe, and map enemies.
- Map enemy sensing is the replacement for aggro propagation.
- First attack on an allowed map enemy registers and locks it.
- Existing recipes behave the same until they opt into map enemies.
- Professional Mode target setup is collapsed into Map Enemies.
- Release build succeeds and outputs the expected DLL.
