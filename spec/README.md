# Combat Simulator - Specification Index

## Specifications

| File | Component | Description |
|------|-----------|-------------|
| [00-project-structure.md](00-project-structure.md) | Build & Structure | Solution layout, .csproj, manifest, namespaces |
| [01-plugin-core.md](01-plugin-core.md) | Plugin Core | Lifecycle, services, commands, config, frame loop |
| [03-combat-engine.md](03-combat-engine.md) | Combat Engine | Damage formula, action pipeline, cooldowns, combos, death delay |
| [04-network-safety.md](04-network-safety.md) | Network Safety | UseAction hook, defense-in-depth, safety rules |
| [05-animation-vfx.md](05-animation-vfx.md) | Animation/VFX | ActionEffect fabrication, death anims, battle stance, flytext |
| [06-npc-ai.md](06-npc-ai.md) | NPC AI | State machine, behaviors, presets, movement, aggro propagation |
| [07-ui.md](07-ui.md) | UI Layer | ImGui windows, HP bar overlay, combat log, shortcuts bar |
| [08-target-behaviors.md](08-target-behaviors.md) | Target Behaviors | Target approach, NPC spread, aggro propagation |

## Reading Order

For first-time readers: 00 -> 01 -> 04 -> 03 -> 05 -> 06 -> 08 -> 07

The safety spec (04) is recommended early to understand the core constraint
that shapes the entire architecture.
