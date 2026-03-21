# Spec: Project Structure & Build System

## Solution Layout

```
CombatSimulator/
├── CombatSimulator.sln
├── RESEARCH_FINDINGS.md                  # Research notes (reference)
├── plan/                                  # Implementation plans
│   ├── 00-overview.md
│   ├── 01-phases.md
│   └── 02-technical-decisions.md
├── spec/                                  # Specifications
│   ├── 00-project-structure.md           # This file
│   ├── 01-plugin-core.md
│   ├── 02-npc-spawner.md
│   ├── 03-combat-engine.md
│   ├── 04-network-safety.md
│   ├── 05-animation-vfx.md
│   ├── 06-npc-ai.md
│   └── 07-ui.md
└── CombatSimulator/                       # Plugin project
    ├── CombatSimulator.csproj
    ├── CombatSimulator.json               # Dalamud plugin manifest
    ├── Plugin.cs                          # Entry point (IDalamudPlugin)
    ├── Configuration.cs                   # Persisted config
    ├── Core/
    │   ├── HyperboreaDetector.cs          # Hyperborea plugin detection
    │   └── Services.cs                    # Static service container
    ├── Simulation/
    │   ├── CombatEngine.cs               # Main simulation engine
    │   ├── SimulationState.cs            # State tracking
    │   ├── SimulatedEntityState.cs       # Per-entity state
    │   ├── DamageCalculator.cs           # Damage formula
    │   ├── CooldownTracker.cs            # Cooldown management
    │   ├── ComboTracker.cs               # Combo chain tracking
    │   ├── ActionDataProvider.cs         # Read action data from Lumina
    │   └── SimulatedStatus.cs            # Status effect data
    ├── Npcs/
    │   ├── NpcSpawner.cs                 # Client-side NPC creation
    │   ├── SimulatedNpc.cs               # NPC wrapper class
    │   ├── NpcBehavior.cs                # AI behavior definitions
    │   └── NpcPresets.cs                 # Preset NPC configurations
    ├── Ai/
    │   ├── NpcAiController.cs            # AI state machine
    │   └── NpcAiState.cs                 # State enum + transitions
    ├── Animation/
    │   ├── AnimationController.cs        # ActionEffect fabrication
    │   └── ActionEffectFabricator.cs     # Native struct building
    ├── Safety/
    │   ├── UseActionHook.cs              # UseAction interception
    │   └── UseActionLocationHook.cs      # Ground-targeted action hook
    └── Gui/
        ├── MainWindow.cs                 # Configuration window
        ├── HpBarOverlay.cs               # Enemy HP bar overlay
        ├── CombatLogWindow.cs            # Combat event log
        ├── PlayerHpOverlay.cs            # Simulated player HP
        └── NpcSelector.cs                # NPC type search/select widget
```

## .csproj Configuration

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project Sdk="Dalamud.NET.Sdk/14.0.2">
  <PropertyGroup>
    <Version>0.1.0</Version>
    <Authors>YourName</Authors>
    <Description>Client-side combat simulation with spawned battle NPCs</Description>
    <PackageProjectUrl>https://github.com/YourUser/CombatSimulator</PackageProjectUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
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
    "Author": "YourName",
    "Punchline": "Spawn battle NPCs and fight them client-side.",
    "Description": "A client-side combat simulator that spawns battle NPCs using Hyperborea's network firewall for safety. Practice rotations, test damage output, or just fight monsters anywhere.",
    "RepoUrl": "https://github.com/YourUser/CombatSimulator",
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
dotnet build CombatSimulator.sln
```

### Deploy (Development)
Output DLL automatically goes to:
```
%APPDATA%\XIVLauncher\devPlugins\CombatSimulator\
```

Or manually copy to devPlugins folder and load via Dalamud's plugin installer
(Dev Tools tab).

### Debug
1. Launch FFXIV via XIVLauncher
2. Attach Visual Studio debugger to `ffxiv_dx11.exe`
3. Set breakpoints in plugin code
4. Use `/combatsim` in-game to test

## Namespace Convention

```
CombatSimulator                    # Root namespace
CombatSimulator.Core               # Plugin core, services, detection
CombatSimulator.Simulation         # Combat engine, damage calc, state
CombatSimulator.Npcs               # NPC spawning and management
CombatSimulator.Ai                 # NPC AI behavior
CombatSimulator.Animation          # Animation/VFX controller
CombatSimulator.Safety             # Network safety hooks
CombatSimulator.Gui                # ImGui windows and overlays
```

## Code Style

- Follow standard C# conventions
- `unsafe` blocks only where needed for game struct access
- All native memory allocations paired with corresponding free
- All hooks disposed in plugin Dispose()
- Try/catch in all hook detours (never crash the game)
- Minimal external dependencies (only what Dalamud provides)
