# Combat Simulator - Architecture Overview

## What This Plugin Does

A FFXIV Dalamud plugin that lets players spawn battle NPCs and fight them in a
fully client-side combat simulation. No data is sent to the server.

## Core Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     CombatSimulator Plugin                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│  ┌──────────────────┐    ┌─────────────────────────────────┐    │
│  │  Hyperborea       │    │  Plugin Core                     │    │
│  │  Integration       │    │  (IDalamudPlugin, lifecycle,     │    │
│  │  (Detect active,  │    │   config, commands, UI)          │    │
│  │   require running)│    └─────────────────────────────────┘    │
│  └──────────────────┘                                            │
│                                                                   │
│  ┌──────────────────┐    ┌─────────────────────────────────┐    │
│  │  NPC Spawner      │    │  Combat Simulation Engine        │    │
│  │                    │    │                                   │    │
│  │  - ClientObj idx  │    │  - SimulatedActionManager         │    │
│  │    200+            │    │  - DamageCalculator               │    │
│  │  - BattleChara    │    │  - CooldownTracker                │    │
│  │    creation        │    │  - ComboTracker                   │    │
│  │  - Model/appear.  │    │  - StatusEffectManager            │    │
│  │  - HP/stats setup │    │  - CastBarSimulator               │    │
│  └──────────────────┘    └─────────────────────────────────┘    │
│                                                                   │
│  ┌──────────────────┐    ┌─────────────────────────────────┐    │
│  │  Animation/VFX    │    │  NPC AI Controller               │    │
│  │  Controller       │    │                                   │    │
│  │                    │    │  - Targeting (face player)        │    │
│  │  - Attack anims   │    │  - Auto-attack timer              │    │
│  │  - Hit reactions  │    │  - Skill rotation                 │    │
│  │  - Death anim     │    │  - Range/positioning              │    │
│  │  - Damage flytext │    │  - Aggro behavior                 │    │
│  │  - Skill VFX      │    │  - Death state                    │    │
│  └──────────────────┘    └─────────────────────────────────┘    │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │  Network Safety Layer                                      │    │
│  │  - Hook UseAction → block server-bound action packets      │    │
│  │  - Redirect to simulation engine instead                   │    │
│  │  - All spawned entities use client-side indexes (200+)     │    │
│  │  - Never write to server-synced fields on real objects     │    │
│  └──────────────────────────────────────────────────────────┘    │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │  UI Layer (ImGui)                                          │    │
│  │  - Main config window (spawn NPC, select type/level)       │    │
│  │  - Combat HUD overlay (enemy HP bar, combat log)           │    │
│  │  - Status panel (buffs/debuffs, cooldowns)                 │    │
│  └──────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

## Key Design Decisions

### 1. Hyperborea Dependency
- Plugin checks if Hyperborea is loaded and its firewall is active
- Without Hyperborea's packet firewall, combat simulation is UNSAFE
  (UseAction would send real packets to the server)
- We add our OWN UseAction hook as a safety net, but Hyperborea's
  firewall is the primary protection

### 2. Combat Approach: Hybrid Simulation
After research, using the internal fight system directly is NOT possible:
- `UseAction()` sends packets to the server — must be blocked
- The server calculates all damage/healing — we must replicate this
- ActionEffect data comes FROM the server — we must fabricate it

**Our approach:**
1. Hook `UseAction()` → intercept the player's action request
2. Run our own damage calculation (simplified FFXIV formulas)
3. Fabricate `ActionEffect` data and feed it to `ActionEffectHandler.Receive()`
4. This triggers the REAL client-side animation/VFX/flytext pipeline
5. Update our simulated entity state (HP, statuses, etc.)

### 3. NPC Spawning via ClientObjectManager
- Use indexes 200-448 (client-only, invisible to server)
- Create BattleChara objects with proper ObjectKind/SubKind
- Load models from game data (BNpcBase/BNpcName sheets)
- Manage HP/stats in our own simulation state (not game memory)

### 4. Animation Pipeline
- Fabricated ActionEffect → `ActionEffectHandler.Receive()` → real animations play
- This is the same path the game uses when receiving server combat data
- Attack animations, hit reactions, damage numbers all work naturally
- Death animation triggered by setting CharacterMode to Dead

## Technology Stack

- **Language:** C# (unsafe)
- **Framework:** Dalamud.NET.Sdk (API level 14)
- **Dependencies:**
  - FFXIVClientStructs (game struct access)
  - Dalamud services (IObjectTable, IFramework, IGameInteropProvider, etc.)
  - ImGui (via Dalamud's UiBuilder)
  - Lumina (game data access via IDataManager)
- **Build:** .NET SDK, MSBuild
- **Reference plugins:** Hyperborea, Brio, RaidsRewritten, BossMod
