# Directed encounter resources

Each `*.json` file in this directory is embedded and loaded as one encounter. Loading is strict:
unknown properties, duplicate phase IDs, dangling phase transitions, and unknown actor aliases reject
the resource and write a precise error to the plugin log.

An encounter references an existing combat recipe through `setup.recipe`, binds stable actor aliases
by NPC name/occurrence, and moves through `phases`. A phase executes `onEnter` cues once (optionally at
`atSeconds`), evaluates its ordered transitions every framework tick, then executes `onExit` cues.

Supported cues:

- `title`: local cinematic title overlay.
- `dialogue`: speaker/text overlay and chat transcript.
- `cameraFocus`: temporary request through `CameraModeCoordinator`; it never writes the camera directly.
- `spawnEnemies`: queues additional `CombatRecipeEnemyGroup` entries through `CombatRecipeRunner`.
- `enemyPressure`: multiplies the bound enemy's attack delay, movement speed, and damage-taken scale.
- `playerVictory`: explicitly starts the player victory presentation; directed encounters suppress
  the engine's automatic victory until this cue runs.
- `combatLog`: adds an informational line to the local combat log.

Supported conditions:

- `elapsed`
- `actorHpAtOrBelow`
- `actorDead`
- `playerDead`
- `allEnemiesDead`

Transitions target another phase, `$complete`, or `$failed`. Conditions in one `when` list are ANDed;
the first transition whose conditions are all true wins, and at most one transition occurs per frame.

Set `storyEligible` to allow the encounter to be offered by the optional post-cutscene bridge. The
bridge is opt-in, only reacts to explicit cutscene condition flags, waits for a safely idle client,
and still requires the player to confirm. It never reads or changes quest progress.
