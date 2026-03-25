# Combat Simulator - Implementation Phases

## Phase 0: Project Scaffolding
**Goal:** Working Dalamud plugin that loads, shows a window, and detects Hyperborea.

- [x] Create solution and project structure (Dalamud.NET.Sdk)
- [x] Implement IDalamudPlugin with constructor injection
- [x] Register `/combatsim` chat command
- [x] Create main ImGui configuration window
- [x] Implement Hyperborea detection (check if plugin is loaded via Dalamud IPC/reflection)
- [x] Display status: "Hyperborea active/inactive"
- [x] Plugin manifest (CombatSimulator.json)
- [x] Basic configuration system (save/load settings)

**Deliverable:** Plugin loads in-game, shows a window, detects Hyperborea status.

---

## Phase 1: NPC Spawning
**Goal:** Spawn a visible, positioned BattleNpc in the world.

- [x] Implement `NpcSpawner` class using ClientObjectManager
- [x] Allocate BattleChara at client-side index (200+)
- [x] Configure spawned object:
  - ObjectKind = BattleNpc, SubKind = Combatant
  - Name, Position (near player), Rotation (face player)
  - Model (load from BNpcBase game data)
  - HitboxRadius, Scale, Height
  - TargetableStatus flags for player targeting
  - RenderFlags for visibility
- [x] Build NPC type selector UI (dropdown from BNpcName sheet)
- [x] Support spawn/despawn lifecycle
- [x] Track spawned entities in a managed list
- [x] Implement NPC model loading from game data sheets:
  - BNpcBase → model ID, scale, stats template
  - BNpcName → display name
  - ModelChara → visual appearance data

**Deliverable:** Can spawn a visible enemy NPC near the player, target it, walk around it.

---

## Phase 2: Combat Simulation Engine (Core)
**Goal:** Player can attack the NPC, see damage numbers, NPC HP decreases.

- [x] Implement `SimulationState` (tracks all simulated entity state):
  - Per-entity: HP, MaxHP, MP, MaxMP, level, stats, alive/dead
  - Global: simulation active flag, tick counter
- [x] Implement `UseAction` hook:
  - Intercept player action requests
  - Block the real call (prevent server packet)
  - Route to simulation engine instead
- [x] Implement `DamageCalculator`:
  - Simplified FFXIV damage formula:
    `Damage = Potency × f(MainStat) × f(DetStat) × f(CritRate) × RNG`
  - Read player stats from real character data
  - Support physical and magical damage types
  - Critical hit / direct hit RNG
- [x] Implement `ActionEffectFabricator`:
  - Build fake ActionEffect header + target effects
  - Include proper action ID, animation target, damage values
  - Feed to `ActionEffectHandler.Receive()` to trigger client animations
- [x] Implement `CooldownTracker`:
  - Mirror game's RecastDetail system
  - Track GCD and oGCD cooldowns independently
  - Handle charges for multi-charge abilities
- [x] Implement `ComboTracker`:
  - Track combo state and timer (15s window)
  - Validate combo chains per job
- [x] Display enemy HP bar via ImGui overlay

**Deliverable:** Player uses abilities → damage numbers appear → NPC HP goes down.

---

## Phase 3: NPC Fights Back
**Goal:** NPC auto-attacks and uses skills against the player.

- [x] Implement `NpcAiController`:
  - State machine: Idle → Engaged → Attacking → Dead
  - Face player (update rotation each frame)
  - Move toward player if out of range (client-side position updates)
  - Auto-attack timer (every ~3s, matching game auto-attack speed)
- [x] Implement NPC → Player damage:
  - Calculate NPC damage based on NPC level/stats
  - Create ActionEffect targeting the player
  - Feed to ActionEffectHandler.Receive() for hit reaction animation
  - Track simulated player HP (separate from real HP)
- [x] Implement NPC skill usage:
  - Simple skill rotation (1-3 skills per NPC type)
  - Skill cooldowns for NPC
  - Cast bar display for NPC casted abilities
- [x] Implement aggro/engage system:
  - NPC engages when player attacks it
  - NPC disengages if player moves too far away
  - Leash distance (reset if player runs too far)

**Deliverable:** Two-way combat. NPC attacks player, player sees hit reactions.

---

## Phase 4: Animation & VFX Polish
**Goal:** Combat looks and feels like real FFXIV combat.

- [x] Player attack animations via fabricated ActionEffect
- [x] NPC attack animations (auto-attack swing, skill casts)
- [x] Hit reaction animations on both player and NPC
- [x] Death animation (CharacterMode = Dead) when HP reaches 0
- [x] Damage flytext (damage numbers floating up)
- [x] Healing flytext (if player uses healing abilities)
- [x] Critical hit visual indicators
- [x] Cast bar for player abilities with cast times
- [x] Cast bar for NPC abilities with cast times
- [x] Interrupt: player can interrupt NPC casts (if interruptible)

**Deliverable:** Combat visually matches real FFXIV combat quality.

---

## Phase 5: Status Effects & Advanced Combat
**Goal:** Buffs, debuffs, DoTs, HoTs work in simulation.

- [ ] Implement `SimulatedStatusManager`:
  - Apply/remove status effects on simulated entities
  - Status duration tracking with per-frame tick
  - Stack count management
  - Display status icons on entity (via StatusManager writes or overlay)
- [ ] Implement DoT/HoT tick system:
  - 3-second server tick simulation
  - Damage/heal over time calculation
- [ ] Status effect interactions:
  - Damage up/down buffs
  - Vulnerability debuffs
  - Stun/sleep/bind (prevent NPC actions)
- [ ] MP consumption and regeneration
- [ ] TP/resource gauge simulation (job gauges)

**Deliverable:** Full buff/debuff system with DoTs ticking.

---

## Phase 6: UI & Quality of Life
**Goal:** Polished user interface and usability.

- [x] NPC type browser with search/filter
- [x] NPC level selector
- [x] Multiple NPC spawning support
- [x] Combat log window (damage dealt, taken, heals)
- [ ] DPS meter overlay
- [x] Reset combat button
- [x] Respawn NPC button
- [x] Player simulated death and respawn
- [x] Save/load NPC presets
- [ ] Keyboard shortcut to toggle combat mode

**Deliverable:** User-friendly, feature-complete interface.

---

## Phase 7: Stretch Goals (Post-MVP)
- [ ] AoE skill support (area damage to multiple NPCs)
- [ ] Tank/Healer/DPS role-specific behavior
- [ ] NPC special mechanics (telegraphed AoE, stack markers)
- [ ] Party simulation (spawn friendly NPCs that help fight)
- [ ] Encounter scripting system (boss fight scripts)
- [ ] Job gauge simulation per job
- [ ] Positional bonuses (flank/rear for melee DPS)

---

## Phase Dependencies

```
Phase 0 (Scaffolding)
  └── Phase 1 (NPC Spawning)
        └── Phase 2 (Combat Engine)
              ├── Phase 3 (NPC AI)
              │     └── Phase 4 (Animation Polish)
              └── Phase 5 (Status Effects)
                    └── Phase 6 (UI Polish)
                          └── Phase 7 (Stretch)
```

## MVP Definition

**Minimum Viable Product = Phases 0 through 3:**
- Plugin loads, detects Hyperborea
- Can spawn an enemy NPC
- Player can attack NPC with real abilities
- Damage numbers appear, NPC HP decreases
- NPC attacks back
- NPC dies when HP reaches 0
- All client-side, no server communication
