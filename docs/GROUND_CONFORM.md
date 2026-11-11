# Enemy Control: flying a creature down into the ground

Code: `Animation/GroundConformSolver.cs` (pure), `Npcs/GroundConformPoser.cs` (reads and writes the
pose each rendered frame), `Npcs/EnemyControlController.cs` (free-flight branch). Test:
`Tests/GroundConform`.

In free flight (Enemy Control, "Walk on ground (no fly)" off) a creature could be flown down through
the floor and simply sank out of sight. Now the floor holds it. Pushed down, it crouches; pushed
further it goes on all fours; further still it comes down low over the ground, but not flat: it stays
on its limbs, so that whatever moves them (a walk) still shows. It cannot be pushed deeper than that.

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
   folds to keep its end on the ground: every joint bends the way that brings the leg's ends together
   (found by trying it), all by the same share; a straight joint bends so that the joint goes forward.
   A bone shorter than 12% of its limb is not a joint of its own. A foot stands as far out from under
   its hip as the animation has it for the height the hip is now at, so a lower body takes shorter
   steps; and it is not lifted nearer the hip than the leg closes up.
2. **The crouch's limit.** A leg closed up still has its ends some distance apart; the hip cannot come
   nearer the ground than that. This is counted from a leg standing under its hip, so a foot lifted or
   swung in a step does not change how far down the body is.
3. **On all fours** (stage 0 to 1: a further push of 20% of the pose's height). The core tips forward
   until it is level (by how steeply it stands over the bone it is held by; a core that is level
   already, four legs under a belly, does not tip and stops at the crouch). Each leg bone turns from
   where the crouch has it to kneeling: the thigh points the way it is animated, hanging from the hip,
   and the rest of the leg lies back along the ground. The body is held up by its knees. Limbs that
   hang down (arms) keep the way they point while the core tips, so they meet the ground under the
   shoulders; there they are stood on it and fold as a leg does.
4. **Low** (stage 1 to 2: as much again). The thighs lean back, up to 60 degrees, and the knees slide
   back; hips and chest come down to about half a thigh's length; the arms fold further.
5. **No deeper.** Past stage 2 the pose is the same and the ground carries it; the solver reports that
   depth (`LastDeepest`) and Enemy Control does not push the creature below it.
6. **A core without legs under it** tips forward as the ground carries it past where its top was, as
   far as its flattest angle.
7. **Hanging limbs**: one that is held up (a neck, a raised arm) goes where the core takes it; one
   that hangs or trails (an arm at rest, a tail, cloth) keeps the way it points. Whatever is then under
   the floor is put back by leaning its bone, keeping the bone's length and the way it points along
   the ground.

Bones keep their lengths exactly at every step.

## The fit's own check

Not every skeleton comes through this well. The first time a creature goes under the ground its
skeleton is tried out on the pose it has: pushed all the way down in 60 steps, it must end with nothing
under the ground and without its core or legs jumping between steps. A skeleton that fails is left to
sink into the ground as before. The log says which: `GroundConform: ... is fitted to the ground` or
`... is left to sink`.

Offline, on reference poses (humans with their arms let down): 42 of 43 skeletons pass (3 human, 39 of
40 monsters). With a human's legs and arms swung 24 degrees either way, as in a walk, one degree more
swing moves no bone more than 3 cm at any depth, and the swing still shows on all fours and low (hands
travel 37 to 54 cm, knees 15 to 36 cm).

## Known limits

- Verified offline on reference poses and a rigid swing of the limbs, not on real animations, and not
  in game.
- The ground is taken as level at the height found under the creature's root.
- The hips rise a little on the way from the crouch to all fours (they are higher on the knees than
  over the heels).
- The head goes where the chest takes it: on all fours it looks at the ground.
- A foot keeps the way it stands while the shin lies back, so it is not turned over as a kneeling
  foot would be.
- A bone that points straight down and has to be laid on the ground has no way of its own to go, and
  goes forward; next to that pose a small change can lay it the other way.
- Most monsters have a core that is level already or is carried as one piece, and only crouch.
