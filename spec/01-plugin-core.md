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
public sealed unsafe class CombatSimulatorPlugin : IDalamudPlugin
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
| `ITargetManager` | Current target tracking |
| `IPluginLog` | Logging |

## Subsystem Initialization Order
1. Static service container (Services.Init)
2. Load configuration
3. Action data provider + damage calculator
4. Chat command executor + Glamourer IPC
5. MovementBlockHook (position/rotation freeze)
6. AnimationController
7. NpcSelector (target management)
8. CombatEngine (simulation core)
9. NpcAiController (AI + target approach)
10. UseActionHook (action interception) — enabled immediately
11. MovementBlockHook — enabled immediately
12. GUI windows (MainWindow, HpBarOverlay, CombatLogWindow)
13. Register chat commands
14. Subscribe to IFramework.Update + UiBuilder.Draw

## Subsystem Disposal Order (reverse)
1. Unsubscribe from IFramework.Update + UiBuilder events
2. Deregister chat commands
3. Stop combat simulation
4. Deselect all NPCs
5. Dispose hooks (UseActionHook, MovementBlockHook)
6. Dispose GUI windows
7. Dispose NpcAiController, CombatEngine, AnimationController, NpcSelector

## Chat Commands

| Command | Description |
|---------|-------------|
| `/combatsim` | Toggle main configuration window |
| `/combatsim start` | Enable combat simulation mode |
| `/combatsim stop` | Disable combat simulation mode |
| `/combatsim reset` | Reset all combat state (HP, cooldowns) |

## Configuration (Persisted)

```csharp
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // General / Display
    public bool ShowMainWindow { get; set; } = false;
    public bool ShowCombatLog { get; set; } = true;
    public bool ShowEnemyHpBar { get; set; } = true;
    public bool ShowPlayerHpBar { get; set; } = true;
    public bool ShowHudPlayerHpBar { get; set; } = false;

    // Simulation
    public float DamageMultiplier { get; set; } = 1.0f;
    public bool EnableCriticalHits { get; set; } = true;
    public bool EnableDirectHits { get; set; } = true;

    // NPC Defaults
    public int DefaultNpcLevel { get; set; } = 90;
    public float DefaultNpcHpMultiplier { get; set; } = 1.0f;
    public int DefaultNpcBehaviorType { get; set; } = 1; // BasicMelee

    // Animation Commands
    public string PlayerMeleeAttackCommand { get; set; } = "";
    public string PlayerRangedAttackCommand { get; set; } = "";
    public string PlayerDeathCommand { get; set; } = "";
    public uint DeathEmoteId { get; set; } = 0;
    public string PlayerVictoryCommand { get; set; } = "";
    public string TargetVictoryCommand { get; set; } = "";

    // Glamourer Integration
    public bool ApplyGlamourerOnDeath { get; set; } = false;
    public string DeathGlamourerDesignId { get; set; } = "";
    public bool ApplyGlamourerOnReset { get; set; } = false;
    public string ResetGlamourerDesignId { get; set; } = "";

    // Target Behaviors
    public bool EnableTargetApproach { get; set; } = false;
    public float TargetApproachDistance { get; set; } = 3.0f;

    // Aggro propagation: nearby idle targets join combat when one is engaged
    public bool EnableAggroPropagation { get; set; } = false;
    public float AggroPropagationRange { get; set; } = 15.0f;

    // Shortcuts bar
    public bool ShowShortcuts { get; set; } = false;

    // Recent NPCs
    public List<uint> RecentNpcIds { get; set; } = new();
}
```

## Framework Update Loop

```
OnFrameworkUpdate(deltaTime):
  // 1. Validate selected NPCs still exist
  npcSelector.Tick()

  if (!simulationActive) return

  deltaTime = 1/60 (fixed timestep)

  // 2. Tick combat engine (cooldowns, casting, combos, status effects, auto-attacks)
  combatEngine.Tick(deltaTime)

  // 3. Tick NPC AI for all selected NPCs
  //    (includes target approach positioning when enabled)
  npcAiController.Tick(deltaTime, npcSelector.SelectedNpcs)
```

## Error Handling
- All hook detours wrapped in try/catch
- On unhandled exception in framework update: log error, stop simulation
- Never crash the game client — fail gracefully and disable
