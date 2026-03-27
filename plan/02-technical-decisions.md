# Combat Simulator - Technical Decisions & Trade-offs

## Decision 1: How to Simulate Combat

### Option A: Use Internal Fight System Directly - REJECTED
- Call `UseAction()` and let the game handle everything
- **Problem:** UseAction sends a network packet to the server. Even with
  a firewall blocking outgoing packets, the game still expects
  a server response (ActionEffect). Without it, the action "hangs" —
  no animation plays, the client enters a broken state.
- **Verdict:** Not viable without a server.

### Option B: Full Custom Numeric System - TOO MUCH EFFORT
- Build an entire FFXIV combat engine from scratch
- Replicate every job's rotation, every status effect, every formula
- **Problem:** FFXIV has 20+ jobs, hundreds of skills, complex interactions.
  This would take years to replicate faithfully.
- **Verdict:** Impractical for initial development.

### Option C: Hybrid Simulation - CHOSEN
- Hook UseAction -> block the real call
- Run simplified damage calculation (potency-based formula)
- Fabricate ActionEffect data -> feed to ActionEffectHandler.Receive()
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
  - Level -> base damage scaling
  - Potency -> direct multiplier
  - Fixed crit/DH rates based on level
- Implemented as fallback, not primary

## Decision 3: Target Selection vs NPC Spawning

### NPC Spawning via ClientObjectManager - REMOVED
- Originally planned to spawn NPCs at client-side indexes (200+)
- NpcSpawner class was implemented but proved unnecessary
- **Issues:** Complex model initialization, client-only objects have limitations
- **Verdict:** Removed from active features

### Select Existing NPCs - CHOSEN
- Players target existing BattleNpcs in the world via normal targeting
- Plugin registers them as combat targets and simulates combat
- **Benefit:** Works with any NPC, no spawning complexity, NPCs already
  have proper models/animations/positioning
- **Trade-off:** Requires existing NPCs in the zone

## Decision 4: Death Effects

### Ragdoll Physics via BEPUphysics2 - CHOSEN
- Pure C# physics engine, no native dependencies
- 16-bone capsule ragdoll with Hinge/BallSocket constraints
- Ground collision via BGCollisionModule raycast
- Bone transforms written to ModelPose in render hook
- **Benefit:** Realistic death physics without game engine support

### Convulsion via Bone Rotation - CHOSEN
- Per-bone sinusoidal rotation on lower body bones
- Uses BoneTransformService render hook (shared with ragdoll)
- Exponential decay over configurable duration
- **Benefit:** Dramatic death effect, simpler than ragdoll

## Decision 5: Animation Trigger Method

### Fabricated ActionEffect -> ActionEffectHandler.Receive() - CHOSEN
- Build the exact data structure the game expects
- Call the game's own handler function
- All downstream animation, VFX, flytext triggers naturally
- **Benefit:** Authentic visuals with one function call

## Decision 6: Frame Update Architecture

### Single Framework Update Handler - CHOSEN
- Register one `IFramework.Update` handler
- Each frame: tick simulation, update AI, check combat events
- **Ordering:**
  1. Process player input (UseAction hook captures async)
  2. Tick NPC AI (decide actions)
  3. Tick simulation state (cooldowns, DoTs, timers)
  4. Update entity positions/rotations
  5. Check death conditions
  6. Update UI state

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Game update breaks struct offsets | High | High | Use FFXIVClientStructs (community-maintained) |
| ActionEffect fabrication crashes | Medium | High | Extensive null checks, try/catch, disable on error |
| Player gets banned | Low | Critical | UseAction hook blocks all server-bound packets |
| Combat feels unresponsive | Medium | Medium | Tune animation lock timing, queue system |
