# Combat Simulator - Plan Index

## Plan Documents

| File | Description |
|------|-------------|
| [00-overview.md](00-overview.md) | Architecture overview, core design, technology stack |
| [01-phases.md](01-phases.md) | Implementation phases, dependencies, MVP definition |
| [02-technical-decisions.md](02-technical-decisions.md) | Key decisions, trade-offs, risk assessment |

## Quick Summary

**What:** FFXIV Dalamud plugin to fight existing battle NPCs in client-side combat simulation.

**How:** Hook UseAction -> simulate damage -> fabricate ActionEffect -> game plays real animations.

**Safety:** UseAction hook blocks all server-bound action packets. All combat is client-side only.

**Core features:** Combat simulation, NPC AI, death effects (ragdoll), death cam, glamourer integration.
