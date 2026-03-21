# Spec: Plugin Core

## Overview
The core plugin module handles lifecycle, configuration, dependency detection,
command registration, and coordinates all subsystems.

## Plugin Identity
- **Internal Name:** CombatSimulator
- **Display Name:** Combat Simulator
- **Command:** `/combatsim`
- **Dalamud API Level:** 14

## Entry Point

```csharp
public unsafe class CombatSimulatorPlugin : IDalamudPlugin
```

### Constructor-Injected Services
| Service | Usage |
|---------|-------|
| `IDalamudPluginInterface` | Plugin metadata, UI builder, config storage |
| `ICommandManager` | Chat command registration |
| `IClientState` | Player state, login/logout events |
| `IObjectTable` | Game object access |
| `IFramework` | Per-frame update tick |
| `IGameInteropProvider` | Hook creation |
| `IDataManager` | Game data sheet access (Lumina) |
| `IGameGui` | World-to-screen coordinate conversion |
| `IChatGui` | Chat message output |
| `ICondition` | Game condition flags |
| `IPluginLog` | Logging |

## Subsystem Initialization Order
1. Load configuration
2. Initialize Hyperborea detector
3. Initialize Network Safety Layer (UseAction hook - disabled initially)
4. Initialize NPC Spawner
5. Initialize Combat Simulation Engine
6. Initialize NPC AI Controller
7. Initialize Animation/VFX Controller
8. Initialize UI windows
9. Register chat commands
10. Subscribe to IFramework.Update

## Subsystem Disposal Order (reverse)
1. Unsubscribe from IFramework.Update
2. Deregister chat commands
3. Dispose UI windows
4. Stop combat simulation
5. Despawn all NPCs
6. Dispose hooks (Network Safety Layer)
7. Save configuration

## Chat Commands

| Command | Description |
|---------|-------------|
| `/combatsim` | Toggle main configuration window |
| `/combatsim spawn [npcId]` | Spawn an NPC by BNpcName ID |
| `/combatsim despawn` | Despawn all simulated NPCs |
| `/combatsim start` | Enable combat simulation mode |
| `/combatsim stop` | Disable combat simulation mode |
| `/combatsim reset` | Reset all combat state (HP, cooldowns) |

## Configuration (Persisted)

```csharp
public class CombatSimulatorConfig
{
    // General
    public bool ShowMainWindow { get; set; } = false;
    public bool ShowCombatLog { get; set; } = true;
    public bool ShowEnemyHpBar { get; set; } = true;

    // Simulation
    public float DamageMultiplier { get; set; } = 1.0f;
    public bool EnableCriticalHits { get; set; } = true;
    public bool EnableDirectHits { get; set; } = true;

    // NPC Defaults
    public int DefaultNpcLevel { get; set; } = 90;
    public float DefaultNpcHpMultiplier { get; set; } = 1.0f;

    // Safety
    public bool RequireHyperborea { get; set; } = true;

    // Recent NPCs (for quick re-spawn)
    public List<uint> RecentNpcIds { get; set; } = new();
}
```

## Hyperborea Detection

### Detection Method
Check if the Hyperborea plugin is loaded via Dalamud's plugin interface:

```csharp
public class HyperboreaDetector
{
    // Check via InstalledPlugins enumeration
    public bool IsHyperboreaLoaded { get; }

    // Optional: check if firewall is active via IPC or reflection
    public bool IsFirewallActive { get; }
}
```

### Behavior When Hyperborea Not Detected
- If `RequireHyperborea` config is true:
  - Show prominent warning in main window
  - Block combat simulation from starting
  - Chat message: "Combat Simulator requires Hyperborea to be active for safety."
- If `RequireHyperborea` config is false:
  - Show warning but allow simulation
  - UseAction hook provides safety independently

## Framework Update Loop

```
OnFrameworkUpdate(deltaTime):
  if (!simulationActive) return

  // 1. Process queued player actions (from UseAction hook)
  ProcessPlayerActionQueue()

  // 2. Tick NPC AI for all spawned NPCs
  foreach (npc in spawnedNpcs)
    npcAiController.Tick(npc, deltaTime)

  // 3. Tick simulation state
  simulationEngine.Tick(deltaTime)
    - Cooldown timers
    - DoT/HoT ticks
    - Status effect durations
    - Animation lock countdown
    - Cast progress
    - Combo timer

  // 4. Update entity world state
  UpdateNpcPositions()
  UpdateNpcRotations()

  // 5. Check win/lose conditions
  CheckDeathConditions()

  // 6. Update UI state
  UpdateHpBars()
  UpdateCombatLog()
```

## Error Handling
- All hook detours wrapped in try/catch
- On unhandled exception: disable simulation, log error, show user message
- Never crash the game client — fail gracefully and disable
