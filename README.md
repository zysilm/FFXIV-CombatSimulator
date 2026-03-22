# Combat Simulator

A Dalamud plugin for FFXIV by **zysilm** that provides client-side combat simulation. Select existing NPCs as combat targets and fight them locally — no server interaction, no risk.

## Features

- **Target Selection** — Select any visible NPC as a combat target via normal targeting + attack
- **Full Combat Simulation** — Damage calculation, cooldowns, combos, auto-attacks, status effects
- **NPC AI** — Configurable behavior presets: Training Dummy, Basic Melee, Basic Ranged, Boss
- **Combat Animations** — Real FFXIV animations via ActionEffectHandler (attack swings, damage flytext, death)
- **Battle Stance** — Active targets visually enter combat-ready "drawn weapon" state
- **Target Approach** — NPCs automatically move to stay near your character with configurable distance
- **NPC Spread** — Multiple targets distribute across a 240° arc to avoid stacking
- **Aggro Propagation** — Nearby idle targets join the fight when one is attacked (configurable range)
- **Death Animation Delay** — 500ms delay so killing blow animation plays before death
- **HP Bar Overlays** — Enemy and player simulated HP bars rendered in-world
- **Combat Log** — Scrollable log of all combat events
- **Shortcuts Bar** — Optional floating bar with Start, Reset All, and Reboot buttons
- **Glamourer Integration** — Apply glamourer presets on death and on reset/respawn
- **Network Safe** — All combat is client-side only; UseAction hook prevents any server packets

## Requirements

- FFXIV with XIVLauncher/Dalamud
- .NET 8.0 SDK (for building)
- Dalamud dev plugins enabled

## Build

```bash
dotnet build CombatSimulator/CombatSimulator.csproj
```

Output goes to `%APPDATA%\XIVLauncher\devPlugins\CombatSimulator\`.

## Usage

1. Open the plugin window with `/combatsim`
2. Target an NPC and attack it to register as a combat target
3. Use **Start Combat** to begin the simulation
4. Configure NPC behavior, damage multipliers, and target behaviors in the settings

### Commands

| Command | Description |
|---------|-------------|
| `/combatsim` | Toggle main window |
| `/combatsim start` | Start simulation |
| `/combatsim stop` | Stop simulation |
| `/combatsim reset` | Reset all combat state |

## Architecture

See [spec/README.md](spec/README.md) for the full specification index.

Key components:
- **CombatEngine** — Core simulation loop (damage calc, action pipeline, death queue)
- **NpcAiController** — Per-NPC state machine + target approach + aggro propagation
- **AnimationController** — ActionEffect fabrication, death animations, battle stance
- **UseActionHook** — Intercepts player actions, routes to simulation instead of server
- **MovementBlockHook** — Position/rotation freeze for player death + NPC approach control
- **GlamourerIpc** — External plugin integration for visual presets

## Safety

The plugin never sends combat data to the FFXIV server:
- UseAction hook intercepts all actions targeting simulated NPCs
- All HP/damage/state is tracked in plugin memory, not game memory
- NPC positions are client-side direct memory writes
- ActionEffectHandler.Receive triggers animations locally only
