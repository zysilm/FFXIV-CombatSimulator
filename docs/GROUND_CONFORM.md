# Enemy Control: flying a creature down into the ground

Code: `Animation/GroundConformSolver.cs` (pure), `Npcs/GroundConformPoser.cs` (reads and writes the
pose each rendered frame), `Npcs/EnemyControlController.cs` (free-flight branch). Test:
`Tests/GroundConform`.

In free flight (Enemy Control, "Walk on ground (no fly)" off) a creature could be flown down through
the floor and simply sank out of sight. Now the floor holds it: its feet stay on the ground and its
legs fold; pushed further it goes down on its front; and it cannot be pushed deeper than leaves it
lying flat (1.2 times its height).

## How a skeleton is read

By its shape alone; no bone is looked up by name.

- **Taking part**: a bone whose own length plus what hangs from it is at least 10% of the pose's
  height. Fingers, toes, ears and the like are carried rigidly by the bone they hang from.
- **Core**: the bone the body hangs from the skeleton's root by, every bone that branches (at least
  two things hanging from it that are each 20% of the height: legs and a spine, arms and a neck), and
  everything between them. It moves as one piece.
- **Limbs**: the chains hanging from the core, each followed outward along whichever bone carries
  most. A limb is a leg (the body stands on it) if one of its bones from the third on is low in the
  pose (within 15% of the height of the ground) and well below where the limb is attached; the leg is
  the limb down to that bone, and what is beyond it (a foot, toes) keeps to the ground as animated.

## What happens as the ground rises

1. **Crouch.** The core stays where the animation holds it, so it comes down with its owner. Each leg
   folds to keep its end where it stood: every joint bends the way that brings the leg's ends
   together (found by trying it), all by the same share, until the end is the right distance from the
   hip; a straight joint bends so that the joint goes forward. A bone shorter than 12% of its limb is
   not a joint of its own (a knee made of two bones a few centimetres apart bends as one).
2. **The crouch's limit.** A leg closed up still has its ends some distance apart; the hip cannot
   come nearer the foot than that. From there the legs hold the core up.
3. **Down on its front.** Pushed past that, the core tips forward: not at all at the limit, flat when
   its owner is down to where the body lies. On the way each leg bone turns from where the crouch has
   it to where the lying body carries it, by way of pointing down, so the knees are on the ground and
   hold the hips up meanwhile.
4. **A core without legs under it**, or taller than they let it lie, tips forward as the ground carries
   it past where its top was, as far as its flattest angle. A body that is long rather than tall (four
   legs, a belly) is flattest as it stands and never tips.
5. **Hanging limbs** (arms, a neck, a tail, cloth) go where the core takes them.
6. **Everything is laid clear**: any joint of a limb then under the floor, or over the ceiling, is
   put back by leaning its bone, keeping the bone's length and the way it points along the ground.

Bones keep their lengths exactly at every step.

## The fit's own check

Not every skeleton comes through this well. The first time a creature goes under the ground its
skeleton is tried out on the pose it has: pushed all the way down in 60 steps, it must end with nothing
under the ground and without its core or legs jumping between steps. A skeleton that fails is left to
sink into the ground as before. The log says which: `GroundConform: ... is fitted to the ground` or
`... is left to sink`.

Offline, on reference poses: 40 of 43 skeletons pass (3 human, 37 of 40 monsters). 14 of the 40
monsters end lower than 35% of their height; the rest end as flat as a rigid core lets them.

## Known limits

- Verified offline on reference poses only, not on animations, and not in game.
- The ground is taken as level at the height found under the creature's root.
- A biped's hips rise on the way from crouching to lying (it goes over its knees).
- Cloth and hair chains are treated as hanging limbs and may move abruptly as the body tips.
