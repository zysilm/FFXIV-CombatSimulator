# Combat Simulator - Architecture Overview

## What This Plugin Does

A FFXIV Dalamud plugin that lets players select existing NPCs and fight them in a
fully client-side combat simulation. No data is sent to the server.

## Core Architecture

```
+-----------------------------------------------------------------+
|                     CombatSimulator Plugin                       |
+-----------------------------------------------------------------+
|                                                                   |
|  +------------------+    +-----------------------------+         |
|  |  NPC Selector     |    |  Plugin Core                |         |
|  |  (Select existing |    |  (IDalamudPlugin, lifecycle, |         |
|  |   BattleNpcs via  |    |   config, commands, UI)     |         |
|  |   targeting)      |    +-----------------------------+         |
|  +------------------+                                            |
|                                                                   |
|  +------------------+    +-----------------------------+         |
|  |  Combat Engine    |    |  NPC AI Controller          |         |
|  |                    |    |                             |         |
|  |  - DamageCalc     |    |  - Targeting (face player)  |         |
|  |  - CooldownTrack  |    |  - Auto-attack timer        |         |
|  |  - ComboTracker   |    |  - Skill rotation           |         |
|  |  - ActionEffect   |    |  - Range/positioning        |         |
|  |  - HP/State mgmt  |    |  - Death state              |         |
|  +------------------+    +-----------------------------+         |
|                                                                   |
|  +------------------+    +-----------------------------+         |
|  |  Animation/VFX    |    |  Death Effects & Camera     |         |
|  |  Controller       |    |                             |         |
|  |                    |    |  - Ragdoll (BEPUphysics2)  |         |
|  |  - Attack anims   |    |  - Active Camera (bone trk) |         |
|  |  - Hit reactions  |    |  - Death Cam (camera ctrl)  |         |
|  |  - Death anim     |    |  - Glamourer integration    |         |
|  |  - Damage flytext |    +-----------------------------+         |
|  +------------------+                                            |
|                                                                   |
|  +------------------------------------------------------+       |
|  |  Network Safety Layer                                  |       |
|  |  - Hook UseAction -> block server-bound action packets |       |
|  |  - Redirect to simulation engine instead               |       |
|  |  - All state tracked in plugin memory, not game memory |       |
|  +------------------------------------------------------+       |
|                                                                   |
|  +------------------------------------------------------+       |
|  |  UI Layer (ImGui)                                      |       |
|  |  - Main config window (settings, NPC management)       |       |
|  |  - Combat HUD overlay (enemy HP bar, combat log)       |       |
|  |  - Shortcuts bar, Death Cam toolbar                    |       |
|  +------------------------------------------------------+       |
+-----------------------------------------------------------------+
```

## Key Design Decisions

### 1. Target Selection (not spawning)
- Players select existing NPCs in the world via normal FFXIV targeting
- Selected NPCs are registered as combat targets
- No NPC spawning — works with any visible BattleNpc

### 2. Combat Approach: Hybrid Simulation
After research, using the internal fight system directly is NOT possible:
- `UseAction()` sends packets to the server — must be blocked
- The server calculates all damage/healing — we must replicate this
- ActionEffect data comes FROM the server — we must fabricate it

**Our approach:**
1. Hook `UseAction()` -> intercept the player's action request
2. Run our own damage calculation (simplified FFXIV formulas)
3. Fabricate `ActionEffect` data and feed it to `ActionEffectHandler.Receive()`
4. This triggers the REAL client-side animation/VFX/flytext pipeline
5. Update our simulated entity state (HP, statuses, etc.)

### 3. Animation Pipeline
- Fabricated ActionEffect -> `ActionEffectHandler.Receive()` -> real animations play
- This is the same path the game uses when receiving server combat data
- Attack animations, hit reactions, damage numbers all work naturally
- Death animation triggered by setting CharacterMode to Dead

### 4. Death Effects & Camera
- **Ragdoll physics** via BEPUphysics2 — 18-bone capsule ragdoll with anatomical
  joint limits (Hinge for knees/elbows, BallSocket for shoulders/hips), self-collision
  filtering, thick ground plane, and bone-to-model mapping
- **Active Camera** — independent camera controller (getCameraPosition vtable hook)
  that tracks a bone with free orbital control, height/side offsets, vertical angle lock,
  and collision disable. Completely separate from Death Cam.
- **Death Cam** — smooth camera transition to an anchored position with bone tracking
- **Glamourer** — apply outfit presets on death/reset via IPC

## Technology Stack

- **Language:** C# (unsafe)
- **Framework:** Dalamud.NET.Sdk (API level 14)
- **Dependencies:**
  - FFXIVClientStructs (game struct access)
  - BEPUphysics2 (ragdoll physics)
  - Dalamud services (IObjectTable, IFramework, IGameInteropProvider, etc.)
  - ImGui (via Dalamud's UiBuilder)
  - Lumina (game data access via IDataManager)
- **Build:** .NET SDK, MSBuild
