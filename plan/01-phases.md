# Combat Simulator - Implementation Phases

## Phase 0: Project Scaffolding (DONE)
**Goal:** Working Dalamud plugin that loads and shows a window.

- [x] Create solution and project structure (Dalamud.NET.Sdk)
- [x] Implement IDalamudPlugin with constructor injection
- [x] Register `/combatsim` chat command
- [x] Create main ImGui configuration window
- [x] Plugin manifest (CombatSimulator.json)
- [x] Basic configuration system (save/load settings)

---

## Phase 1: Target Selection (DONE)
**Goal:** Select existing NPCs as combat targets.

- [x] Implement `NpcSelector` class for selecting visible BattleNpcs
- [x] Target registration via normal FFXIV targeting + attack
- [x] Track selected entities in a managed list
- [x] Model override system (change visual appearance of selected NPCs)
- [x] Support multiple simultaneous targets
- [x] Aggro propagation (nearby idle NPCs auto-join combat)

---

## Phase 2: Combat Simulation Engine (DONE)
**Goal:** Player can attack NPCs, see damage numbers, NPC HP decreases.

- [x] Implement `SimulationState` (per-entity HP, stats, alive/dead)
- [x] Implement `UseAction` hook (intercept + block server packets)
- [x] Implement `DamageCalculator` (simplified FFXIV damage formula)
- [x] Implement `ActionEffectFabricator` (fabricate ActionEffect for animations)
- [x] Implement `CooldownTracker` (GCD and oGCD cooldowns)
- [x] Implement `ComboTracker` (combo chains with 15s window)
- [x] Display enemy HP bar via ImGui overlay

---

## Phase 3: NPC Fights Back (DONE)
**Goal:** NPC auto-attacks and uses skills against the player.

- [x] Implement `NpcAiController` (state machine: Idle -> Engaged -> Attacking -> Dead)
- [x] NPC auto-attack timer, face player, move toward player
- [x] NPC -> Player damage with ActionEffect for hit reactions
- [x] NPC skill usage with cooldowns and cast bars
- [x] Aggro/engage/disengage system with leash distance

---

## Phase 4: Animation & VFX Polish (DONE)
**Goal:** Combat looks and feels like real FFXIV combat.

- [x] Player and NPC attack animations via fabricated ActionEffect
- [x] Hit reaction animations, death animation, damage/healing flytext
- [x] Critical hit visual indicators
- [x] Cast bars for player and NPC abilities
- [x] Hit VFX on player when taking damage (configurable .avfx path)

---

## Phase 5: Death Effects & Camera (DONE)
**Goal:** Dramatic visual effects on character death and camera control.

- [x] Ragdoll physics via BEPUphysics2 (18-bone capsule ragdoll with wrists)
- [x] Per-joint anatomical limits (asymmetric hinge ranges, swing cones, twist limits)
- [x] Self-collision with 2-hop exclusion filtering (optional, GUI toggle)
- [x] Thick ground plane to prevent capsule tunneling
- [x] Active Camera (getCameraPosition vtable hook, bone tracking with free orbital control)
- [x] Death Cam (smooth camera transition with bone tracking and presets)
- [x] Glamourer integration (apply outfit presets on death/reset)
- [x] Movement blocking on player death (position/rotation hook)

---

## Phase 6: UI & Quality of Life (DONE)
**Goal:** Polished user interface and usability.

- [x] Combat log window
- [x] Shortcuts bar (Start, Reset, Reboot)
- [x] Death Cam toolbar with preset management
- [x] Player simulated death and reset
- [x] NPC default configuration (level, HP multiplier, behavior type)
- [x] Custom animation commands (attack, death, victory)

---

## Phase 7: Stretch Goals (TODO)
- [ ] AoE skill support (area damage to multiple NPCs)
- [ ] Tank/Healer/DPS role-specific NPC behavior
- [ ] NPC special mechanics (telegraphed AoE, stack markers)
- [ ] Encounter scripting system (boss fight scripts)
- [ ] Job gauge simulation per job
- [ ] Positional bonuses (flank/rear for melee DPS)
- [ ] Status effect system (buffs, debuffs, DoTs, HoTs)
