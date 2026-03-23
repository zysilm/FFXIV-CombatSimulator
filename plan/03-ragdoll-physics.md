# Ragdoll Physics System — Implementation Plan

## Overview

Replace the current "Torture Mode" (which simply replays the death animation) with a ragdoll physics system. When a dead character is hit, individual bones respond physically to the impact force, creating realistic body deformation while preserving the death pose.

**GUI label change:** "Experimental → Torture" becomes **"Ragdoll (Experimental)"** — a top-level collapsing header like Death Cam.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     CombatEngine                            │
│  OnHitDeadEntity(entityId, hitDirection, force)             │
│           │                                                 │
│           ▼                                                 │
│  ┌─────────────────────┐                                    │
│  │  RagdollController  │  (new top-level controller)        │
│  │                     │                                    │
│  │  - Owns physics state per entity                         │
│  │  - Tick(dt) updates simulation                           │
│  │  - Writes bone transforms via BoneManipulator            │
│  └────────┬────────────┘                                    │
│           │                                                 │
│           ▼                                                 │
│  ┌─────────────────────┐     ┌───────────────────────────┐  │
│  │   RagdollSimulation │     │    BoneManipulator        │  │
│  │                     │     │                           │  │
│  │  - Per-bone angular  │     │  - Hooks UpdateBonePhysics│  │
│  │    velocity          │     │  - Reads hkaPose bones    │  │
│  │  - Constraints       │     │  - Writes rotations back  │  │
│  │  - Gravity           │     │  - Freezes animation when │  │
│  │  - Floor collision   │     │    ragdoll is active      │  │
│  │  - Damping           │     │                           │  │
│  └──────────────────────┘     └───────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

---

## Phase 1: BoneManipulator — Read/Write Bone Transforms

The foundation. Provides the ability to read and write bone rotations on any character, surviving the game's animation loop.

### 1.1 Pointer Chain

```
Character* (from IClientState or IObjectTable)
  → ((GameObject*)character)->DrawObject        // DrawObject*
  → (CharacterBase*)drawObject                  // CharacterBase* (offset 0xA0 has Skeleton*)
  → characterBase->Skeleton                     // Render.Skeleton*
  → skeleton->PartialSkeletons[partialIdx]      // PartialSkeleton (stride 0x230)
  → partial.GetHavokPose(poseIdx)               // hkaPose*
  → pose->LocalPose / pose->ModelPose           // hkArray<hkQsTransformf>
  → pose->AccessBoneModelSpace(boneIdx, prop)   // hkQsTransformf* (direct pointer)
```

**Key struct:** `hkQsTransformf` (0x30 bytes)
- `Translation` (hkVector4f, 0x00) — bone position
- `Rotation` (hkQuaternionf, 0x10) — bone orientation (XYZW quaternion)
- `Scale` (hkVector4f, 0x20) — bone scale

### 1.2 Preventing Animation Overwrite

The game's animation system computes bone poses every frame. Without intervention, our ragdoll transforms would be overwritten. Three mechanisms (from Brio's approach):

1. **Hook `UpdateBonePhysics`** — fires after the game's bone physics pass completes.
   - Signature: `"48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4"`
   - In our detour: call Original first, then overwrite bones with ragdoll state.
   - This is the hot path — keep it fast.

2. **Freeze animation timeline** — set `Character->Timeline.OverallSpeed = 0` on the dead character.
   - The death pose loop timeline already holds the character still, but freezing speed ensures zero animation advancement.
   - Only freeze when ragdoll is actively simulating.

3. **Optionally NOP physics update** — patch the game's built-in bone physics (hair/cloth/tail).
   - Signature: `"0F 11 48 10 41 0F 10 44 24 ?? 0F 11 40 20 48 8B 46 28"`
   - Prevents physics jitter on affected bones.
   - **Note:** This is global — affects ALL characters. Only apply when ragdoll is active, and restore on deactivate.

**Recommended approach for v1:** Use mechanism (1) + (2). Skip (3) unless hair/cloth causes visible artifacts.

### 1.3 Writing Transforms

```csharp
// In UpdateBonePhysics detour (after Original returns):
var pose = partial->GetHavokPose(0);
if (pose == null) return;

var modelSpace = pose->AccessBoneModelSpace(boneIdx, PropagateOrNot.Propagate);
// Modify rotation only (preserve translation and scale to avoid tearing)
modelSpace->Rotation = newQuaternion;
```

**Critical:** Use `PropagateOrNot.Propagate` when writing to ensure child bones update their world-space positions.

### 1.4 Bone Name Resolution

Bone names come from `hkaSkeleton->Bones[i]` (each `hkaBone` has a name pointer). Build a name→index map at init time for readable code.

**Key FFXIV humanoid bones for ragdoll:**

| Bone | Role | Children |
|------|------|----------|
| `n_hara` | Root/pelvis | Spine, legs |
| `j_kosi` | Waist | Upper body, legs |
| `j_sebo_a/b/c` | Spine segments | Chest, shoulders |
| `j_mune_l/r` | Chest | (cosmetic) |
| `j_sako_l/r` | Clavicle | Arms |
| `j_ude_a_l/r` | Upper arm | Forearm |
| `j_ude_b_l/r` | Forearm | Hands |
| `j_te_l/r` | Hand | Fingers |
| `j_kubi` | Neck | Head |
| `j_kao` | Head | Face bones |
| `j_asi_a_l/r` | Thigh | Lower leg |
| `j_asi_b_l/r` | Shin | Foot |
| `j_asi_c_l/r` | Foot | Toes |

### 1.5 Files

- `CombatSimulator/Physics/BoneManipulator.cs` — hook setup, read/write, freeze/unfreeze

---

## Phase 2: Death Pose Capture

After the death animation finishes playing, capture the final bone state as the "rest pose" for the ragdoll simulation.

### 2.1 Timing

- Death animation plays via `EmoteTimelinePlayer.PlayLoopedEmote(intro, loop)`.
- The intro timeline (falling down) has a finite duration. The loop timeline (lying still) repeats.
- **Configurable wait time** (`DeathPoseSettleTime`, default ~2.0s) — how long to wait after triggering death before capturing the pose.
- After the timer expires, snapshot all bone local-space rotations.

### 2.2 Snapshot Structure

```csharp
public class DeathPoseSnapshot
{
    public hkQsTransformf[] BoneLocalTransforms;  // One per bone
    public hkQsTransformf[] BoneModelTransforms;  // One per bone (for floor detection)
    public int BoneCount;
    public float TimeCapture;
}
```

### 2.3 Floor Surface Estimation

Once pose is captured, estimate the floor plane from the lowest bone positions:

1. Read model-space positions of key bones: head, hands, feet, pelvis.
2. Find the lowest Y values — these contact the floor.
3. Fit a floor plane (in most cases, simply `Y = min(bone positions).Y`).
4. Determine pose orientation: stomach-up, stomach-down, or side, based on spine→head direction relative to floor normal.

This floor plane is the collision boundary for ragdoll simulation — bones cannot penetrate below it.

### 2.4 Files

- `CombatSimulator/Physics/DeathPoseSnapshot.cs` — snapshot data + floor estimation

---

## Phase 3: Ragdoll Physics Simulation

A simplified articulated-body physics simulation operating on bone rotations.

### 3.1 Design Principles

- **Rotation-only**: Bones rotate at their joints. No translation changes (prevents mesh tearing).
- **Angular velocity per bone**: Each bone tracks angular velocity (Vector3, axis-angle).
- **Joint constraints**: Limit rotation range per bone type (e.g., elbow: 0–150 degrees, spine: ±30 degrees).
- **Gravity**: Unconstrained bones (e.g., arm hanging off a ledge) sag downward.
- **Damping**: Angular velocity decays over time (configurable damping factor).
- **Floor collision**: Bones cannot rotate through the floor plane.

### 3.2 Per-Bone State

```csharp
public struct BonePhysicsState
{
    public Quaternion CurrentRotation;     // Current local-space rotation
    public Quaternion RestRotation;        // Death pose rotation (from snapshot)
    public Vector3 AngularVelocity;       // Current angular velocity (radians/sec)
    public float Mass;                     // Relative mass (heavier = harder to move)
    public float MaxAngleFromRest;        // Max deviation from rest pose (joint limit)
    public float ReturnStiffness;         // Spring force back to rest pose
}
```

### 3.3 Hit Force Application

When `CombatEngine` detects a hit on a dead entity:

1. **Pick random impact bone**: Select a random bone from the ragdoll-eligible set (not root, not fingers).
2. **Calculate force direction**: Based on the attacking NPC's position relative to the character.
3. **Apply angular impulse**: Convert linear force to angular impulse on the hit bone.
4. **Propagate to neighbors**: Parent and child bones receive attenuated impulse (50% per joint hop, up to 2–3 hops).

```csharp
public void ApplyHitImpulse(int boneIndex, Vector3 forceDirection, float magnitude)
{
    // Convert force to angular impulse (cross product with bone direction)
    var boneDir = GetBoneWorldDirection(boneIndex);
    var torque = Vector3.Cross(boneDir, forceDirection) * magnitude;

    bones[boneIndex].AngularVelocity += torque / bones[boneIndex].Mass;

    // Propagate to neighbors with attenuation
    PropagateImpulse(boneIndex, torque * 0.5f, depth: 2);
}
```

### 3.4 Physics Tick (per frame)

```
for each bone in simulation:
    1. Apply gravity torque (if bone endpoint is above floor)
    2. Apply damping: angularVelocity *= (1 - damping * dt)
    3. Apply return spring: torque toward rest pose, scaled by stiffness
    4. Integrate: rotation += angularVelocity * dt (via quaternion multiplication)
    5. Clamp: limit rotation deviation from rest pose by MaxAngleFromRest
    6. Floor collision: if bone endpoint would penetrate floor, reflect velocity & clamp
```

### 3.5 Continuous Hits

New hits arrive while the simulation is running. They simply add more angular impulse — the simulation integrates them naturally. No need for special handling.

### 3.6 Configurable Parameters

| Parameter | Default | GUI | Description |
|-----------|---------|-----|-------------|
| `DeathPoseSettleTime` | 2.0 | Slider (0.5–5.0) | Seconds before pose capture |
| `RagdollDamping` | 3.0 | Slider (0.5–10.0) | How quickly motion dies |
| `RagdollStiffness` | 1.0 | Slider (0.0–5.0) | Spring force back to rest pose |
| `HitForceMagnitude` | 1.0 | Slider (0.1–5.0) | Impulse strength per hit |
| `MaxBoneAngle` | 30° | Slider (5–90°) | Max bone deviation from rest |
| `GravityStrength` | 1.0 | Slider (0–3.0) | Gravity effect on dangling bones |

### 3.7 Files

- `CombatSimulator/Physics/RagdollSimulation.cs` — physics tick, impulse, constraints
- `CombatSimulator/Physics/BoneConstraints.cs` — per-bone-type joint limits

---

## Phase 4: RagdollController (Orchestrator)

Ties everything together. Manages per-entity ragdoll state and lifecycle.

### 4.1 Lifecycle

```
[Entity alive] → [Death triggered] → [Death animation playing]
                                          │
                                     (settle timer)
                                          │
                                          ▼
                                    [Pose captured]
                                    [Ragdoll READY]
                                          │
                                     (entity hit)
                                          │
                                          ▼
                                    [Ragdoll ACTIVE]
                                    [Physics ticking]
                                          │
                                    (no hits for N sec + settled)
                                          │
                                          ▼
                                    [Ragdoll IDLE]
                                    (still captured, physics paused)
                                          │
                                     (entity hit again)
                                          │
                                          ▼
                                    [Ragdoll ACTIVE] (resume)
                                          │
                                     (simulation reset/stop)
                                          │
                                          ▼
                                    [Cleanup: unfreeze, restore bones]
```

### 4.2 Integration with CombatEngine

Replace current torture-mode logic:

```csharp
// OLD (in ProcessNpcAction / ProcessPlayerAction):
if (config.EnableTorture)
    ReapplyDeathPose(target);

// NEW:
if (config.EnableRagdoll && ragdollController.IsReady(target.EntityId))
    ragdollController.ApplyHit(target.EntityId, attackerPosition, hitForce);
```

### 4.3 Files

- `CombatSimulator/Physics/RagdollController.cs` — lifecycle, per-entity state, hit routing

---

## Phase 5: GUI

### 5.1 New Section: "Ragdoll (Experimental)"

Top-level collapsing header (same level as Death Cam), replaces the old "Experimental → Torture" checkbox.

```
▼ Ragdoll (Experimental)
  [x] Enable Ragdoll
  (?) Experimental: replaces torture mode. Dead characters physically react to hits.

  --- Timing ---
  Death Pose Settle Time    [====|====] 2.0 sec
  (?) How long to wait after death animation before capturing the rest pose.

  --- Physics ---
  Hit Force                 [====|====] 1.0
  Damping                   [====|====] 3.0
  Return Stiffness          [====|====] 1.0
  Max Bone Angle            [====|====] 30°
  Gravity                   [====|====] 1.0
```

### 5.2 Config Changes

```csharp
// Replace:
public bool EnableTorture { get; set; } = false;

// With:
public bool EnableRagdoll { get; set; } = false;
public float DeathPoseSettleTime { get; set; } = 2.0f;
public float RagdollDamping { get; set; } = 3.0f;
public float RagdollStiffness { get; set; } = 1.0f;
public float RagdollHitForce { get; set; } = 1.0f;
public float RagdollMaxBoneAngle { get; set; } = 30f;
public float RagdollGravity { get; set; } = 1.0f;
```

**Backward compat:** Keep `EnableTorture` as a fallback read (if `EnableRagdoll` is false and `EnableTorture` was true, migrate on load).

---

## Phase 6: Testing & Polish

- Test with different death poses (stomach-up, stomach-down, side)
- Test with multiple dead NPCs simultaneously
- Test continuous rapid hits
- Tune default parameters for natural-looking motion
- Ensure cleanup on simulation stop / zone change / plugin unload
- Verify no bone state leaks after ragdoll deactivates

---

## Implementation Order

1. **BoneManipulator** — hook + read/write bones (proof of concept: manually rotate one bone)
2. **DeathPoseSnapshot** — capture pose after death
3. **RagdollSimulation** — physics math
4. **RagdollController** — orchestration + CombatEngine integration
5. **GUI** — ragdoll section, replace old torture section
6. **Tuning** — parameter defaults, constraints per bone type

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| UpdateBonePhysics sig breaks on game update | Ragdoll stops working | Graceful fallback to old torture mode |
| Performance with many ragdolled entities | Frame drops | Limit to N simultaneous ragdolls (default 3) |
| Bone rotation causes mesh artifacts | Visual glitches | Tight MaxAngleFromRest constraints per bone |
| Game's animation system fights our writes | Jittering | Freeze timeline + post-physics hook |
| Floor estimation fails (stairs/slopes) | Body clips through floor | Conservative flat-floor assumption with margin |

---

## Dependencies

- **FFXIVClientStructs**: `Skeleton`, `PartialSkeleton`, `hkaPose`, `hkQsTransformf`, `CharacterBase`
- **Dalamud**: `IGameInteropProvider` (for hooks), `ISigScanner`
- **No external plugin dependency** (CustomizePlus/Brio not required — direct bone access via Havok)
