# Combat Simulator - Plan Index

## Plan Documents

| File | Description |
|------|-------------|
| [00-overview.md](00-overview.md) | Architecture overview, core design, technology stack |
| [01-phases.md](01-phases.md) | Implementation phases (0-7), dependencies, MVP definition |
| [02-technical-decisions.md](02-technical-decisions.md) | Key decisions, trade-offs, risk assessment |

## Quick Summary

**What:** FFXIV Dalamud plugin to spawn battle NPCs and fight them client-side.

**How:** Hook UseAction → simulate damage → fabricate ActionEffect → game plays real animations.

**Safety:** Hyperborea's packet firewall + our UseAction hook = no server communication.

**MVP:** Phases 0-3 (scaffolding, NPC spawn, combat engine, NPC AI).
