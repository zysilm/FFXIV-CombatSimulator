# Spec: Network Safety Layer

## Overview
The network safety layer ensures that NO combat-related data is sent to the
FFXIV server during simulation. This is the critical safety component that
prevents GM detection and account action.

## Defense in Depth Strategy

```
Layer 1: Hyperborea's Packet Firewall
  └── Blocks ALL outgoing packets except heartbeat
  └── Blocks ALL incoming packets except whitelisted opcodes
  └── This is the primary protection

Layer 2: Our UseAction Hook
  └── Intercepts UseAction() calls
  └── Blocks the call from reaching the game's network layer
  └── Routes to our simulation engine instead
  └── This works EVEN IF Hyperborea is not active

Layer 3: Client-Only Object Indexes
  └── All spawned NPCs use indexes 200-448
  └── Server has no knowledge of these objects
  └── No entity sync packets are generated for client objects
```

## UseAction Hook

### Hook Target
```csharp
// FFXIVClientStructs: ActionManager.UseAction
// Signature: from ActionManager.MemberFunctionPointers.UseAction
private delegate bool UseActionDelegate(
    ActionManager* actionManager,
    ActionType actionType,
    uint actionId,
    ulong targetId,
    uint extraParam,
    ActionManager.UseActionMode mode,
    uint comboRouteId,
    bool* outOptAreaTargeted);

private Hook<UseActionDelegate> useActionHook;
```

### Hook Behavior

```csharp
private bool UseActionDetour(
    ActionManager* actionManager,
    ActionType actionType,
    uint actionId,
    ulong targetId,
    uint extraParam,
    ActionManager.UseActionMode mode,
    uint comboRouteId,
    bool* outOptAreaTargeted)
{
    // Only intercept during active simulation
    if (!combatEngine.IsActive)
        return useActionHook.Original(...);  // Pass through normally

    // Only intercept combat actions (type 1)
    if (actionType != ActionType.Action)
        return useActionHook.Original(...);  // Pass through non-combat

    // Check if target is a simulated NPC
    if (!IsSimulatedTarget(targetId))
        return useActionHook.Original(...);  // Not our target

    // === INTERCEPT ===
    // Do NOT call original (would send packet to server)
    // Route to our simulation engine instead
    var result = combatEngine.ProcessPlayerAction(
        actionType, actionId, targetId, extraParam);

    // Return true to indicate "action was processed"
    // (prevents the game from showing "unable to use" errors)
    return result.Success;
}
```

### Key Safety Rules

1. **NEVER call `useActionHook.Original()` for simulated combat targets**
   - This is the line that sends packets to the server
   - Only call original for non-simulation actions

2. **Always pass through non-combat action types**
   - Mount, general actions, emotes should work normally
   - Only intercept `ActionType.Action` (value 1)

3. **Always pass through when simulation is inactive**
   - Player should be able to play normally when not simulating

4. **Always pass through for non-simulated targets**
   - If player targets a real game entity, don't intercept

## Target Identification

```csharp
private bool IsSimulatedTarget(ulong targetId)
{
    // Check if the target entity ID matches any of our spawned NPCs
    foreach (var npc in npcSpawner.SpawnedNpcs)
    {
        if (npc.SimulatedEntityId == targetId)
            return true;
    }
    return false;
}
```

Simulated entity IDs use the range `0xF0000000 - 0xF00000FF` which is
not used by real game entities (real entities use server-assigned IDs).

## UseActionLocation Hook (Ground-Targeted Actions)

```csharp
// For ground-targeted abilities (e.g., Salted Earth, Asylum)
private delegate bool UseActionLocationDelegate(
    ActionManager* actionManager,
    ActionType actionType,
    uint actionId,
    ulong targetId,
    Vector3* location,
    uint extraParam,
    byte a7);

private Hook<UseActionLocationDelegate> useActionLocationHook;
```

Same interception logic: block during simulation, pass through otherwise.

## Hyperborea Status Monitoring

```csharp
public class HyperboreaDetector
{
    /// Check if the Hyperborea plugin assembly is loaded.
    public bool IsPluginLoaded()
    {
        var plugins = pluginInterface.InstalledPlugins;
        return plugins.Any(p =>
            p.InternalName == "Hyperborea" && p.IsLoaded);
    }

    /// Periodic check (every 5 seconds) to verify Hyperborea is still active.
    /// If Hyperborea is unloaded during simulation, warn the user.
    public void CheckStatus()
    {
        if (wasActive && !IsPluginLoaded())
        {
            // Hyperborea was unloaded! Emergency stop.
            chatGui.PrintError("[CombatSim] Hyperborea was unloaded! " +
                "Stopping simulation for safety.");
            combatEngine.StopSimulation();
        }
    }
}
```

## What We NEVER Do

| Action | Why It's Dangerous |
|--------|-------------------|
| Call UseAction.Original for sim targets | Sends action request packet |
| Write to real player HP/MP/Status | Server would detect mismatch |
| Modify real ActionManager cooldowns | Could desync client state |
| Send any IPC type 3 packets | Direct server communication |
| Modify targeting system for real entities | Server validates targets |
| Change player position programmatically | Server tracks position |

## What We Safely Do

| Action | Why It's Safe |
|--------|--------------|
| Read player stats from BattleChara | Read-only, no network |
| Spawn objects at index 200+ | Client-only objects |
| Call ActionEffectHandler.Receive with fake data | Client-side animation only |
| Write to spawned NPC memory (position, etc.) | Client-only objects |
| Render ImGui overlays | Pure UI layer |
| Read game data sheets via Lumina | Local file access |

## Emergency Stop

If any error occurs in the hook detour:
```csharp
try
{
    // ... hook logic ...
}
catch (Exception ex)
{
    pluginLog.Error(ex, "UseAction hook error, disabling simulation");
    combatEngine.StopSimulation();
    // ALWAYS pass through to original on error
    return useActionHook.Original(...);
}
```

## Testing Checklist

- [ ] UseAction hook correctly passes through when simulation is inactive
- [ ] UseAction hook correctly passes through for non-simulated targets
- [ ] UseAction hook correctly blocks for simulated targets
- [ ] Non-combat actions (mount, emote) work during simulation
- [ ] Hyperborea detection correctly identifies loaded/unloaded state
- [ ] Emergency stop triggers on hook errors
- [ ] Plugin disposal correctly removes all hooks
- [ ] No action packets appear in network traffic during simulation
