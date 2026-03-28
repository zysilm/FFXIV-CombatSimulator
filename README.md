# Combat Simulator

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV that provides client-side combat simulation. Select existing NPCs as combat targets and fight them locally — no server interaction, no risk.

## Installation

Open Dalamud Settings in-game (`/xlsettings`) and follow these steps:

1. Click the **Experimental** tab.
2. Paste the following URL into the empty box at the bottom of **Custom Plugin Repositories**:
   ```
   https://raw.githubusercontent.com/zysilm/FFXIV-CombatSimulator/main/pluginmaster.json
   ```
3. Click the **+** button, then click **Save**.
4. Open the Plugin Installer (`/xlplugins`) and search for **Combat Simulator**.

## Features

- **Target Selection** — Select any visible NPC as a combat target via normal targeting + attack
- **Full Combat Simulation** — Damage calculation, cooldowns, combos, auto-attacks, status effects
- **NPC AI** — Configurable behavior presets: Training Dummy, Basic Melee, Basic Ranged, Boss
- **Combat Animations** — Real FFXIV animations via ActionEffectHandler (attack swings, damage flytext, death)
- **Battle Stance** — Active targets visually enter combat-ready "drawn weapon" state
- **Target Approach** — NPCs automatically move to stay near your character with configurable distance
- **Aggro Propagation** — Nearby idle targets join the fight when one is attacked (configurable range)
- **HP Bar Overlays** — Enemy and player simulated HP bars rendered in-world
- **Combat Log** — Scrollable log of all combat events
- **Shortcuts Bar** — Optional floating bar with Start, Reset All, and Reboot buttons
- **Death Effects** — Ragdoll physics (BEPUphysics2) on character death with 18-bone capsule ragdoll, per-joint anatomical limits, one-sided knee hyperextension prevention, self-collision, and ground contact
- **Active Camera** — Camera tracks a selected bone with free orbital control, height/side offsets, vertical angle lock, and collision disable. Camera system inspired by [Cammy](https://github.com/UnknownX7/Cammy) (getCameraPosition hook pattern)
- **Death Cam** — Cinematic camera that smoothly transitions to an anchored position on player death, with configurable height/side offsets, bone tracking, and smooth follow. Camera system inspired by [Cammy](https://github.com/UnknownX7/Cammy)
- **Hit VFX** — Configurable visual effects on player when taking damage
- **Glamourer Integration** — Apply glamourer presets on death and on reset
- **Sidebar UI** — Tabbed layout with draggable sidebar for easy navigation
- **Network Safe** — All combat is client-side only; UseAction hook prevents any server packets

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

## Building from Source

```bash
git clone --recursive https://github.com/zysilm/FFXIV-CombatSimulator.git
dotnet build CombatSimulator/CombatSimulator.csproj -c Release
```

Requires .NET 8.0 SDK and Dalamud.

## Credits

- Camera system (Active Camera, Death Cam) inspired by [Cammy](https://github.com/UnknownX7/Cammy) by UnknownX7
- Ragdoll physics powered by [BEPUphysics2](https://github.com/bepu/bepuphysics2) by Ross Nordby

## Safety

The plugin never sends combat data to the FFXIV server:
- UseAction hook intercepts all actions targeting simulated NPCs
- All HP/damage/state is tracked in plugin memory, not game memory
- NPC positions are client-side direct memory writes
- ActionEffectHandler.Receive triggers animations locally only
