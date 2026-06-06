# Combat Simulator

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV that provides client-side combat simulation. Build local battles with field enemies, companions, and NPC targets - no server interaction, no risk.

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

- **Combat System** - Mixed battles with field enemies, spawned NPCs, target selection, full damage calculation, HP overlays, combat log, and melee/ranged/boss AI.
- **Party Companions** - Clone visible players or configured companions into friendly combat actors with target planning, movement, and field skirmish recipes.
- **Ragdoll & Camera** - High-precision BEPUphysics2 ragdolls with per-bone tuning, non-humanoid support, death cam, active camera, and smooth bone tracking.

All combat is client-side only - no data is sent to the server.

## Usage

1. Open the plugin window with `/combatsim`
2. Choose a combat recipe such as **Field Combat**
3. Use **Start** to begin the local simulation

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

Requires .NET 10.0 SDK and Dalamud.

## Credits

- Camera system inspired by [Cammy](https://github.com/UnknownX7/Cammy) by UnknownX7
- Ragdoll physics powered by [BEPUphysics2](https://github.com/bepu/bepuphysics2) by Ross Nordby
