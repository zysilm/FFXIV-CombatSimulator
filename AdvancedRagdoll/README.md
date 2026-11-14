# AdvancedRagdoll

Ragdoll physics for .NET games, built on [BepuPhysics 2](https://github.com/bepu/bepuphysics2).

A `Ragdoll` belongs to one character. It builds rigid bodies and joints from the character's skeleton (a hand-tuned
humanoid table from a skeleton profile, or a rig generated from the skeleton's own bones), waits for the moment
physics should take over from the death animation, carries the animation's velocity into the bodies, lands on the
game's ground (and floats in water), and writes the simulated pose back to the skeleton every frame.

It started as the ragdoll of a game plugin and is used there; nothing in the library depends on that game.

> **Status: 0.1, pre-release.** The public API is small and may still change. The library depends on a beta of
> BepuPhysics (2.5.0-beta.22), which only targets .NET 8, so the library needs .NET 8 or later.

## Use

A character and a ground plane, in about twenty lines:

```csharp
using System.Numerics;
using AdvancedRagdoll;
using AdvancedRagdoll.Hosting;
using AdvancedRagdoll.SkeletonProfiles;

// Once: the skeleton (bone names, parent indices, parent-local reference pose) and the ground.
var skeleton = new ArraySkeleton(boneNames, parentIndices, referencePose);
var world = new DelegateWorldQuery((Vector3 origin, Vector3 direction, float max, out RayHit hit) =>
{
    hit = default;
    return MyGame.Physics.Raycast(origin, direction, max, out hit.Point, out hit.Normal, out hit.Distance);
});
var ragdoll = new Ragdoll(new RagdollHost { Skeleton = skeleton, World = world, Actor = myActor }, SkeletonProfile.Auto());

// When the character dies.
ragdoll.Activate(new RagdollActivation(RagdollTriggerMode.FixedDelay, Delay: 0f));

// Every frame.
void Update(float dt)
{
    skeleton.SetRoot(character.Position, character.Rotation, Vector3.One);
    if (ragdoll.Phase == RagdollPhase.Waiting)
        skeleton.SetModelPose(character.AnimatedModelPose);   // before physics: the animation (its motion is carried over)
    ragdoll.Step(dt);
    if (ragdoll.WritePose())
        character.SetModelPose(skeleton.ModelPose);          // once physics runs: the simulated pose
}

// When the character comes back, or goes away.
ragdoll.Deactivate();
ragdoll.Dispose();
```

`myActor` implements `IRagdollActor`: an `ActorId`, whether the character still exists, its fidelity (`Primary` for
the one the camera follows), its facing, and three notifications. The one that matters is `OnPhysicsStarted`: stop
the character's animation there, or the animation overwrites the pose the ragdoll writes. `OnDeactivated` hands the
body back.

`Tests/LibrarySample` in the source repository runs essentially this (a fixed skeleton instead of an animated
character, a flat plane instead of a game's raycast) against a hand-made 22-bone humanoid: it falls, lands, settles,
and does the same thing twice; and when the character vanishes and is activated again, physics starts again.

### Conventions

- World **Y up**, metres, seconds, radians, `System.Numerics`.
- Poses are in **model space** (relative to the skeleton root); `RootTransform` maps model space to world space. A
  reference pose is parent-local. An engine with only local-space poses converts in its adapter
  (`ArraySkeleton.ToModelSpace` composes a local pose).
- The skeleton is read and written **in batches**: `Step` reads each skeleton part once
  (`ISkeletonPose.TryReadModelPose`), `WritePose` writes each part at most once, only the bones that changed.
- A character is an opaque `ActorId`; the library never interprets it.

### The skeleton profile

The constructor takes a `SkeletonProfile`. `SkeletonProfile.Auto()` generates a rig for any skeleton: a body for
every bone that owns a long enough segment, ball joints between them. A humanoid profile (bone names for the
landmarks, a bone table with shapes, masses and joint limits, ranges of motion, a mass budget) gives the hand-tuned
humanoid rig to every skeleton that has the profile's signature bones; others still get the generated rig.

`SkeletonProfile.Auto()` names no bones, so it has no pelvis landmark: the triggers that watch the height of the
body's middle (`FallingFastest`, `Landed`) never see it. They then start physics when the death animation ends (if
`IRagdollActor.TryGetAnimationSample` reports one), and otherwise after the activation's `Delay` (at least one
second). Use `FixedDelay` or the animation triggers with `Auto()`.

### Settings

`RagdollHost.Settings` (an `IRagdollSettingsSource`) hands out two things: `RagdollSettings`, taken once per
activation (anatomical ranges of motion, the knee buckle, the exact ground), and `RagdollLiveSettings`, read at the
start of every step (water, verbose logging). Without one, the defaults apply.

## Threading

- A `Ragdoll` is **not thread-safe**. `Activate`, `Step`, `WritePose`, `Deactivate` and `Dispose` must run on one
  thread, or the host must serialise them.
- The host callbacks (`ISkeletonPose`, `IWorldQuery`, `IRagdollActor`, `IRagdollSettingsSource`, `IRagdollLog`) are
  called on that thread, from inside those calls.
- Each ragdoll has its own BepuPhysics simulation, but ragdolls share some process-wide state (a random source and
  cost counters): drive all of them from the same thread.

## Extension points

The library has extension points that reach into the BepuPhysics simulation (a step hook and a contact hook). In 0.1
they are internal: not part of the public API, and not covered by any compatibility promise when they open up.

## License

[Mozilla Public License 2.0](https://mozilla.org/MPL/2.0/). You may use the library from closed-source code; if you
change one of its files and distribute the result, that file stays under the MPL and its source must be available.

BepuPhysics, the only dependency, is Apache-2.0.
