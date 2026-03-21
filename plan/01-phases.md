# Combat Simulator - Implementation Phases

## Phase 0: Project Scaffolding
**Goal:** Working Dalamud plugin that loads, shows a window, and detects Hyperborea.

- [ ] Create solution and project structure (Dalamud.NET.Sdk)
- [ ] Implement IDalamudPlugin with constructor injection
- [ ] Register `/combatsim` chat command
- [ ] Create main ImGui configuration window
- [ ] Implement Hyperborea detection (check if plugin is loaded via Dalamud IPC/reflection)
- [ ] Display status: "Hyperborea active/inactive"
- [ ] Plugin manifest (CombatSimulator.json)
- [ ] Basic configuration system (save/load settings)

**Deliverable:** Plugin loads in-game, shows a window, detects Hyperborea status.

---

## Phase 1: NPC Spawning
**Goal:** Spawn a visible, positioned BattleNpc in the world.

- [ ] Implement `NpcSpawner` class using ClientObjectManager
- [ ] Allocate BattleChara at client-side index (200+)
- [ ] Configure spawned object:
  - ObjectKind = BattleNpc, SubKind = Combatant
  - Name, Position (near player), Rotation (face player)
  - Model (load from BNpcBase game data)
  - HitboxRadius, Scale, Height
  - TargetableStatus flags for player targeting
  - RenderFlags for visibility
- [ ] Build NPC type selector UI (dropdown from BNpcName sheet)
- [ ] Support spawn/despawn lifecycle
- [ ] Track spawned entities in a managed list
- [ ] Implement NPC model loading from game data sheets:
  - BNpcBase → model ID, scale, stats template
  - BNpcName → display name
  - ModelChara → visual appearance data

**Deliverable:** Can spawn a visible enemy NPC near the player, target it, walk around it.

---

## Phase 2: Combat Simulation Engine (Core)
**Goal:** Player can attack the NPC, see damage numbers, NPC HP decreases.

- [ ] Implement `SimulationState` (tracks all simulated entity state):
  - Per-entity: HP, MaxHP, MP, MaxMP, level, stats, alive/dead
  - Global: simulation active flag, tick counter
- [ ] Implement `UseAction` hook:
  - Intercept player action requests
  - Block the real call (prevent server packet)
  - Route to simulation engine instead
- [ ] Implement `DamageCalculator`:
  - Simplified FFXIV damage formula:
    `Damage = Potency × f(MainStat) × f(DetStat) × f(CritRate) × RNG`
  - Read player stats from real character data
  - Support physical and magical damage types
  - Critical hit / direct hit RNG
- [ ] Implement `ActionEffectFabricator`:
  - Build fake ActionEffect header + target effects
  - Include proper action ID, animation target, damage values
  - Feed to `ActionEffectHandler.Receive()` to trigger client animations
- [ ] Implement `CooldownTracker`:
  - Mirror game's RecastDetail system
  - Track GCD and oGCD cooldowns independently
  - Handle charges for multi-charge abilities
- [ ] Implement `ComboTracker`:
  - Track combo state and timer (15s window)
  - Validate combo chains per job
- [ ] Display enemy HP bar via ImGui overlay

**Deliverable:** Player uses abilities → damage numbers appear → NPC HP goes down.

---

## Phase 3: NPC Fights Back
**Goal:** NPC auto-attacks and uses skills against the player.

- [ ] Implement `NpcAiController`:
  - State machine: Idle → Engaged → Attacking → Dead
  - Face player (update rotation each frame)
  - Move toward player if out of range (client-side position updates)
  - Auto-attack timer (every ~3s, matching game auto-attack speed)
- [ ] Implement NPC → Player damage:
  - Calculate NPC damage based on NPC level/stats
  - Create ActionEffect targeting the player
  - Feed to ActionEffectHandler.Receive() for hit reaction animation
  - Track simulated player HP (separate from real HP)
- [ ] Implement NPC skill usage:
  - Simple skill rotation (1-3 skills per NPC type)
  - Skill cooldowns for NPC
  - Cast bar display for NPC casted abilities
- [ ] Implement aggro/engage system:
  - NPC engages when player attacks it
  - NPC disengages if player moves too far away
  - Leash distance (reset if player runs too far)

**Deliverable:** Two-way combat. NPC attacks player, player sees hit reactions.

---

## Phase 4: Animation & VFX Polish
**Goal:** Combat looks and feels like real FFXIV combat.

- [ ] Player attack animations via fabricated ActionEffect
- [ ] NPC attack animations (auto-attack swing, skill casts)
- [ ] Hit reaction animations on both player and NPC
- [ ] Death animation (CharacterMode = Dead) when HP reaches 0
- [ ] Damage flytext (damage numbers floating up)
- [ ] Healing flytext (if player uses healing abilities)
- [ ] Critical hit visual indicators
- [ ] Cast bar for player abilities with cast times
- [ ] Cast bar for NPC abilities with cast times
- [ ] Interrupt: player can interrupt NPC casts (if interruptible)

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

- [ ] NPC type browser with search/filter
- [ ] NPC level selector
- [ ] Multiple NPC spawning support
- [ ] Combat log window (damage dealt, taken, heals)
- [ ] DPS meter overlay
- [ ] Reset combat button
- [ ] Respawn NPC button
- [ ] Player simulated death and respawn
- [ ] Save/load NPC presets
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
