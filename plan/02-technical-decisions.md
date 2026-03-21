# Combat Simulator - Technical Decisions & Trade-offs

## Decision 1: How to Simulate Combat

### Option A: Use Internal Fight System Directly ❌ REJECTED
- Call `UseAction()` and let the game handle everything
- **Problem:** UseAction sends a network packet to the server. Even with
  Hyperborea's firewall blocking outgoing packets, the game still expects
  a server response (ActionEffect). Without it, the action "hangs" —
  no animation plays, the client enters a broken state.
- **Verdict:** Not viable without a server.

### Option B: Full Custom Numeric System ❌ TOO MUCH EFFORT
- Build an entire FFXIV combat engine from scratch
- Replicate every job's rotation, every status effect, every formula
- **Problem:** FFXIV has 20+ jobs, hundreds of skills, complex interactions.
  This would take years to replicate faithfully.
- **Verdict:** Impractical for initial development.

### Option C: Hybrid Simulation ✅ CHOSEN
- Hook UseAction → block the real call
- Run simplified damage calculation (potency-based formula)
- Fabricate ActionEffect data → feed to ActionEffectHandler.Receive()
- The REAL client animation/VFX pipeline handles the visuals
- **Benefit:** We get authentic visuals with manageable simulation code
- **Trade-off:** Damage numbers won't perfectly match official server math,
  but they'll be close enough for a training/fun tool

## Decision 2: How to Handle Player Stats

### Read Real Stats from Character
- Player's actual level, main stat, substats are used in formulas
- Read from the real BattleChara struct in memory
- **Benefit:** Damage feels appropriate for the player's actual gear
- **Trade-off:** Must map stat offsets correctly (may break on patches)

### Simplified Stat Model (Fallback)
- If struct reading fails, use a simplified model:
  - Level → base damage scaling
  - Potency → direct multiplier
  - Fixed crit/DH rates based on level
- Implemented as fallback, not primary

## Decision 3: Hyperborea Dependency Model

### Hard Dependency (Check on Every Frame) ❌
- Block all simulation if Hyperborea not active
- **Problem:** Over-restrictive. Our UseAction hook already blocks packets.

### Soft Dependency with Safety Net ✅ CHOSEN
- Require Hyperborea loaded at plugin startup (show warning if not)
- Our own UseAction hook blocks action packets as safety layer
- Combat simulation works independently of Hyperborea's firewall
- Hyperborea provides ADDITIONAL safety (blocks ALL outgoing packets)
- **Rationale:** Defense in depth. Two layers of protection.

## Decision 4: NPC Model Loading

### From Game Data Sheets ✅ CHOSEN
- Use IDataManager to read BNpcBase, BNpcName, ModelChara sheets
- Get model IDs, names, base stats, scale from official game data
- Load the model via the game's character creation pipeline
- **Benefit:** Any enemy in the game can be spawned with correct appearance
- **Trade-off:** Complex model initialization pipeline

### Hardcoded Models ❌ REJECTED
- Only support a fixed set of NPC models
- **Problem:** Extremely limiting, not extendable

## Decision 5: NPC HP Display

### ImGui Overlay ✅ PRIMARY
- Render HP bar as an ImGui overlay positioned above the NPC
- World-to-screen coordinate conversion via IGameGui
- Full control over appearance and behavior
- **Benefit:** Works reliably, no game state corruption

### Game's Target HP Bar (Bonus)
- Write simulated HP values to the NPC's BattleChara memory
- The game's built-in target info HUD shows the HP bar
- **Risk:** Writing to game structs could cause instability
- **Plan:** Attempt this after ImGui overlay is stable

## Decision 6: Animation Trigger Method

### Fabricated ActionEffect → ActionEffectHandler.Receive() ✅ CHOSEN
- Build the exact data structure the game expects
- Call the game's own handler function
- All downstream animation, VFX, flytext triggers naturally
- **Benefit:** Authentic visuals with one function call
- **Trade-off:** Must get the data structures exactly right

### Direct Animation Function Calls ❌ BACKUP ONLY
- Call individual animation/VFX functions directly
- More control but must coordinate timing manually
- **Plan:** Use only if ActionEffect fabrication proves unreliable

## Decision 7: Frame Update Architecture

### Single Framework Update Handler ✅ CHOSEN
- Register one `IFramework.Update` handler
- Each frame: tick simulation, update AI, check combat events
- **Benefit:** Simple, predictable execution order
- **Ordering:**
  1. Process player input (UseAction hook captures async)
  2. Tick NPC AI (decide actions)
  3. Tick simulation state (cooldowns, DoTs, timers)
  4. Update entity positions/rotations
  5. Check death conditions
  6. Update UI state

## Decision 8: ECommons Usage

### Do NOT use ECommons ✅ DECIDED
- Hyperborea uses ECommons extensively, but it adds complexity
- We use standard Dalamud APIs directly
- Hook via IGameInteropProvider (standard Dalamud)
- Access game structs via FFXIVClientStructs directly
- **Benefit:** Fewer dependencies, easier to maintain, standard patterns

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Game update breaks struct offsets | High | High | Use FFXIVClientStructs (community-maintained) |
| ActionEffect fabrication crashes | Medium | High | Extensive null checks, try/catch, disable on error |
| Player gets banned | Low | Critical | UseAction hook + Hyperborea firewall double protection |
| NPC model fails to load | Medium | Medium | Fallback to simple models (training dummy) |
| Combat feels unresponsive | Medium | Medium | Tune animation lock timing, queue system |
| Memory leaks from spawned NPCs | Medium | Medium | Proper cleanup in Dispose(), track all allocations |
