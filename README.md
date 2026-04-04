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

- **Combat System** — Full damage calculation, NPC AI (melee/ranged/boss presets), aggro propagation, HP overlays, and combat log. NPC levels up to 200.
- **Ragdoll Physics** — BEPUphysics2-powered ragdoll on death with 42 configurable bones (spine, arms, legs, clavicles, cloth/skirt). Per-bone editor for capsule volume, joint type, rotation limits. Adjustable friction, self-collision, and 3D debug overlay.
- **Camera** — Customizable death cam and active camera with bone tracking, free orbital control, height/side offsets, and smooth transitions.

All combat is client-side only — no data is sent to the server.

## Usage

1. Open the plugin window with `/combatsim`
2. Target an NPC and attack it to register as a combat target
3. Use **Start Combat** to begin the simulation

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

- Camera system inspired by [Cammy](https://github.com/UnknownX7/Cammy) by UnknownX7
- Ragdoll physics powered by [BEPUphysics2](https://github.com/bepu/bepuphysics2) by Ross Nordby
