# Spec: Project Structure & Build System

## Solution Layout

```
FFXIV-CombatSimulator/
├── CombatSimulator.sln
├── KNOWN_ISSUES.md                          # Known issues & workarounds
├── RESEARCH_FINDINGS.md                     # Technical research notes
├── plan/                                    # Implementation plans
│   ├── 00-overview.md
│   ├── 01-phases.md
│   └── 02-technical-decisions.md
├── spec/                                    # Specifications
│   ├── 00-project-structure.md             # This file
│   ├── 01-plugin-core.md
│   ├── 02-npc-spawner.md
│   ├── 03-combat-engine.md
│   ├── 04-network-safety.md
│   ├── 05-animation-vfx.md
│   ├── 06-npc-ai.md
│   ├── 07-ui.md
│   └── 08-target-behaviors.md
├── FFXIVClientStructs/                      # Game struct definitions (submodule)
└── CombatSimulator/                         # Plugin project
    ├── CombatSimulator.csproj
    ├── CombatSimulator.json                 # Dalamud plugin manifest
    ├── Plugin.cs                            # Entry point (IDalamudPlugin)
    ├── Configuration.cs                     # Persisted config
    ├── Core/
    │   ├── HyperboreaDetector.cs            # Hyperborea plugin detection
    │   └── Services.cs                      # Static service container
    ├── Simulation/
    │   ├── CombatEngine.cs                 # Main simulation engine
    │   ├── SimulationState.cs              # Global state tracking
    │   ├── SimulatedEntityState.cs         # Per-entity state
    │   ├── DamageCalculator.cs             # Damage formula
    │   ├── ActionDataProvider.cs           # Read action data from Lumina
    │   └── SimulatedStatus.cs              # Status effect data
    ├── Npcs/
    │   ├── NpcSelector.cs                  # Select existing NPCs as combat targets
    │   ├── SimulatedNpc.cs                 # NPC wrapper class
    │   └── NpcBehavior.cs                  # AI behavior definitions + presets
    ├── Ai/
    │   ├── NpcAiController.cs              # AI state machine + target approach
    │   └── NpcAiState.cs                   # State enum
    ├── Animation/
    │   ├── AnimationController.cs          # Action effects, death, victory anims
    │   └── ChatCommandExecutor.cs          # Execute chat commands from plugin
    ├── Safety/
    │   ├── UseActionHook.cs                # UseAction interception
    │   └── MovementBlockHook.cs            # Position/rotation freeze (player death + NPC approach)
    ├── Integration/
    │   └── GlamourerIpc.cs                 # Glamourer IPC for death presets
    └── Gui/
        ├── MainWindow.cs                   # Configuration window
        ├── HpBarOverlay.cs                 # Enemy + player HP bar overlays
        └── CombatLogWindow.cs              # Combat event log
```

## .csproj Configuration

```xml
<Project Sdk="Dalamud.NET.Sdk/14.0.2">
  <PropertyGroup>
    <Version>0.1.0</Version>
    <Authors>CombatSimulator</Authors>
    <Description>Client-side combat simulation with spawned battle NPCs</Description>
    <IsPackable>false</IsPackable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```

The `Dalamud.NET.Sdk` automatically provides:
- Target framework (net8.0-windows)
- x64 platform
- All Dalamud DLL references (from `$(APPDATA)\XIVLauncher\addon\Hooks\dev\`)
- FFXIVClientStructs
- DalamudPackager build step
- ImGui bindings

## Plugin Manifest (CombatSimulator.json)

```json
{
    "Name": "Combat Simulator",
    "Author": "CombatSimulator",
    "Punchline": "Spawn battle NPCs and fight them client-side.",
    "Description": "A client-side combat simulator that lets you select existing NPCs as combat targets. Practice rotations, test damage output, or just fight monsters anywhere.",
    "Tags": ["combat", "simulation", "training"],
    "CategoryTags": ["other"],
    "LoadPriority": 0
}
```

## Build & Development

### Prerequisites
- .NET 8.0 SDK
- FFXIV installed with XIVLauncher/Dalamud
- Visual Studio 2022 or JetBrains Rider
- Dalamud dev plugins enabled in XIVLauncher

### Build
```bash
dotnet build CombatSimulator/CombatSimulator.csproj
```

### Deploy (Development)
Output DLL automatically goes to:
```
%APPDATA%\XIVLauncher\devPlugins\CombatSimulator\
```

## Namespace Convention

```
CombatSimulator                    # Root namespace
CombatSimulator.Core               # Plugin core, services, detection
CombatSimulator.Simulation         # Combat engine, damage calc, state
CombatSimulator.Npcs               # NPC selection and management
CombatSimulator.Ai                 # NPC AI behavior + target approach
CombatSimulator.Animation          # Animation/VFX controller
CombatSimulator.Safety             # Network safety hooks + position control
CombatSimulator.Integration        # External plugin IPC (Glamourer)
CombatSimulator.Gui                # ImGui windows and overlays
```

## Code Style

- Follow standard C# conventions
- `unsafe` blocks only where needed for game struct access
- All hooks disposed in plugin Dispose()
- Try/catch in all hook detours (never crash the game)
- Minimal external dependencies (only what Dalamud provides)
