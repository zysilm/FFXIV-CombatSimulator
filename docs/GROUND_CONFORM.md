# Enemy Control: flying a creature down into the ground

Code: `Animation/GroundConformSolver.cs` (pure), `Npcs/GroundConformPoser.cs` (reads and writes the
pose each rendered frame), `Npcs/EnemyControlController.cs` (free-flight branch). Test:
`Tests/GroundConform`.

In free flight (Enemy Control, "Walk on ground (no fly)" off) a creature could be flown down through
the floor and simply sank out of sight. Now the floor holds it. Pushed down, it goes down on its
knees and hands early (after a short crouch, not a deep one); pushed further it comes down low over
the ground, but not flat: it stays on its limbs, and whatever moves them (a walk, a run) moves it as
a crawl. It cannot be pushed deeper than that. A human is on all fours when pushed about a third of
its height into the ground, and as low as it goes at about half.

## How a skeleton is read

By its shape alone; no bone is looked up by name.

- **Taking part**: a bone whose own length plus what hangs from it is at least 10% of the pose's
  height (the tallest the pose has been of late, so a pose that ducks does not change what a toe
  is). Fingers, toes, ears and the like are carried rigidly by the bone they hang from. A bone the
  model has no use for sits at the skeleton's origin, far from the bone it hangs from (the chest
  bones of some male bodies): it takes no part.
- **Core**: the bone the body hangs from the skeleton's root by, every bone that branches (at least
  two things hanging from it that are each 20% of the height: legs and a spine, arms and a neck), and
  everything between them. It moves as one piece.
- **Limbs**: the chains hanging from the core, each followed outward along whichever bone carries
  most. A limb is a leg (the body stands on it) if one of its bones from the third on is low in the
  pose (within 15% of the height of the ground) and well below where the limb is attached; the leg is
  the limb down to that bone, and what is beyond it (a foot, toes) keeps to the ground as animated.
  A short leg has its knee that low too: the leg then goes on down the next bone if that goes
  steeply down. Legs are held at about one height and stand on the same ground: a limb held 12% of
  the height over the lowest leg (an arm that swings low in a run), or ending 2% of it over where the
  lowest leg stands (cloth that hangs to the shins), is not one. A leg whose foot is lifted high in
  a step (a run) is still a leg, and as much of it: it is remembered for 60 solves, no longer, so
  that an arm put to the ground once does not stay a leg.

## What happens as the ground rises

1. **Crouch.** The core stays where the animation holds it, so it comes down with its owner. Each leg
   folds to keep its end on the ground: every joint bends the way that brings the leg's ends together
   (found by trying it), all by the same share; a straight joint bends so that the joint goes forward.
   A bone shorter than 12% of its limb is not a joint of its own. A foot stands as far out from under
   its hip as the animation has it for the height the hip is now at, so a lower body takes shorter
   steps; and it is not lifted nearer the hip than the leg closes up.
2. **Down on its knees early.** A body whose hips ride well over where they are when it kneels (a
   thigh's length over the knee, with the core level) does not crouch deep first: it starts down on
   its knees when the hips have come a quarter of the way down to there, and is on all fours when
   they are there, so the knees meet the ground as the body arrives and are not pushed into it and
   out again. This is counted from as high as the hips ride and as near the ground as the leg's end
   has been (both remembered, and forgotten slowly): a foot lifted or swung in a step, and the bob of
   the hips in a walk or a run, do not change how far down the body is. A body that has no such way
   down (hips no higher than its thighs are long, or a core that lies on the ground as it stands)
   folds its legs as far as they go first, as before, and goes on from there.
3. **On all fours** (stage 0 to 1). The core tips forward
   until it is level (by how steeply it stands over the bone it is held by; a core that is level
   already, four legs under a belly, does not tip and stops at the crouch; one that stands more
   upright than not goes all the way). Each leg bone turns from where the crouch has it to kneeling,
   round the body's side by way of pointing down, never up over it. Kneeling, the thigh points the
   way the whole leg does in the animation, from hip to foot, by 85% of the angle: the knee steps
   where the foot stepped. A knee made of two bones a few centimetres apart bends at the further
   one. The rest of the leg lies back behind the knee, 14 degrees up off the ground, and the foot
   trails from it, toes down. The thighs stand a little further apart than the animation has them and
   the shins lie further out to the side: the feet of a walk are under the body, the knees of a
   crawl are wider than the hips and the feet wider than the knees. The body is held up by its
   knees, which rest nearer the ground than a foot stands on it.
   A limb in front of the legs that hangs and has the reach is a hand: it is put down on the ground
   under its shoulder, three quarters as far out to the side as the shoulder is, and as far ahead or
   behind as the animation has the wrist of the shoulder (no further than the arm at 35 degrees, or
   than it reaches), however the animation holds the arm; its elbow points back and a little out. It
   stands on its wrist: where the limb ends in a short bone that takes part (a finger of a big hand),
   that lies on the ground ahead. Other limbs that hang (cloth, a tail) keep the way they point.
4. **Low** (stage 1 to 2: a further push of 20% of the pose's height). The thighs lean back, up to 60 degrees, and the knees slide
   back; hips and chest come down to about half a thigh's length; the arms fold further. A thigh
   that leans back swings less (its swing would lift its knee).
5. **No deeper.** Past stage 2 the pose is the same and the ground carries it; the solver reports that
   depth (`LastDeepest`) and Enemy Control does not push the creature below it.
6. **A core without legs under it** tips forward as the ground carries it past where its top was, as
   far as its flattest angle.
7. **Hanging limbs**: one that is held up (a neck, a raised arm) goes where the core takes it, but
   is lifted half way back: the head looks ahead and down, not at the ground. One that hangs or
   trails (an arm at rest, a tail, cloth) keeps the way it points. Whatever is then under the floor
   is put back by leaning its bone, keeping the bone's length and the way it points along the ground.
8. **Hands and toes.** The small bones a limb's bone carries (fingers) take no part; if they would
   point into the ground the bone is turned up about its own joint by the least that brings them
   clear, so a hand lies on the ground instead of standing on its fingertips. A foot that is carried
   by its shin is turned on by 30 degrees as the shin is laid back, so that it trails.

Bones keep their lengths exactly at every step.

## Fitting the body

Bodies differ: arms are longer or shorter against the legs, heads bigger. On all fours the core is
tipped a little further, or less far (no more than 20 degrees either way), until the hands reach the
ground from where it holds the shoulders with 92% of the arm's length (a little bent, as measured on
the crawl made by hand), and as low as it goes with 70%. So a body with long arms carries its chest
higher than its hips, and one with short arms lower.

Measured on every playable body with its own walk and run loops (heights as shares of the body's
height; on all fours, walking): knees 2.2 to 3.4% off the ground while they travel back, hands 3.0 to
4.5%, hips 23 to 36%, head 25 to 42%, no bone of the body under the ground, and no bone moving more
than 10% of the height in a thirtieth of a second (16% running). Before, the two biggest male bodies
did not kneel at all (a bone parked at the origin was taken for a limb on the ground), the smallest
stood on its knees as feet (its knee is as low as an ankle), and in a run most hung their hands 20 to
30% of their height in the air.

## Moving on all fours

The animation still walks or runs upright; on all fours that is made a crawl, from the pose alone.

- **Which leg is stepping.** The leg a walk swings forward is drawn up: shorter from hip to foot
  than the leg it stands on (by 6% of its length for fully so), with its foot further off the ground
  (by 1% of the pose's height). Both are counted from the leg that is least so, so a body that
  stands with its knees bent is not stepping.
- **The knee that steps is lifted**, by 2.8% of the pose's height, by its hip: the hips roll about
  the bone that carries the legs (no further than 18 degrees, with what the animation has them
  rolled already) until each knee is as far off the ground as it should be, and the body rests on
  the knee that is lifted least. The chest does not roll with them.
- **The knee ahead stays up until the body comes over it.** A thigh that reaches forward (from 12
  degrees, fully at 21) has its knee lifted as a stepping one has: a body on its knees stands on the
  knee behind.
- **The shin comes up as the leg is drawn up**, by up to 18 degrees more, as evenly as the animation
  draws it.
- **The body's weight goes over the knees it stands on**: it shifts sideways, by 80% of the way, half
  for the knee that is not lifted and half for the knee that is behind.
- **The hand across from a stepping leg is lifted with it**, by 5% of the pose's height: the limbs of
  a body on four step in pairs across it. A limb that is stood on the ground in front of the legs
  takes its step from the legs behind it on the other side.

## The fit's own check

Not every skeleton comes through this well. When a creature first goes under the ground its skeleton
is tried out on the pose it has: pushed all the way down in 60 steps, it must end with nothing under
the ground and without its core or legs jumping between steps. A pose can be a poor one to judge by
(a foot lifted high in a running step), so one that fails is tried again on up to five later poses,
seven frames apart. A skeleton that still fails is left to sink into the ground as before. The log
says which: `GroundConform: ... is fitted to the ground` or `... is left to sink`.

Offline, on reference poses (humans with their arms let down): 42 of 43 skeletons pass (3 human, 39 of
40 monsters). With a human's legs and arms swung 24 degrees either way, or one leg drawn up in a step,
one degree more moves no bone more than 3 cm at any depth.

## Against a crawl made by hand

Compared with a crawl animation made by hand (the walk loop of the "[Feri] Craaawl" mod, decoded
offline, c0101; not part of this repository). Ours is the game's own walk and run loops put through
the solver at a fixed depth; theirs in brackets.

The pose on all fours (reference pose): hips 0.51 m (0.51), chest 0.52 (0.53), head 0.55 (0.52),
face 45 degrees below level (42), knee bent 101 degrees (109), shin 14 degrees up (21), foot 24
degrees down behind the ankle (35), wrist 0.06 (0.06). Still different: their elbows are bent 50
degrees (ours 25). Knees 21 cm apart (14; hips 18), ankles 30 (23), hands 23 (20; shoulders 28):
before, 15, 6 and 58.

The walk on all fours, before this was made a crawl and now:

| | before | now | theirs |
|---|---|---|---|
| thigh swing, off theirs (degrees, root mean square) | 10.8 | 6.4 | |
| knee, off theirs in height (cm) | 6.6 | 2.6 | |
| knee while it travels back (cm off the ground) | 10.1 | 4.4 | 3.5 |
| ankle at its highest (cm) | 48 | 29 | 33 |
| hand while it travels back (cm off the ground) | 8.4 | 5.9 | 4.2 |
| hand height follows theirs (1 is exactly) | -0.5 | 0.9 | |
| hips roll, least to most (degrees) | 19.5 | 36 | 30 |
| hips roll follows theirs | -0.3 | 0.8 | |
| hips sway side to side (cm) | 2.6 | 10.9 | 14.7 |
| head (cm up, on the whole) | 40 | 49 | 52 |
| largest move of a bone in a thirtieth of a second (cm) | 13.1 | 11.1 | 6.8 |

As low as it goes, the knees used to swing up to 46 cm off the ground and the ankles to 79; now 14
and 30. In a run on all fours the knees went up to 40 cm and a bone moved up to 46 cm in a thirtieth
of a second (a leg whose foot was lifted high stopped being a leg); now 10 and 17.

## Known limits

- Verified offline, on reference poses and on the game's walk and run loops for the sixteen playable
  bodies, not in game, and not on monsters' own animations.
- The ground is taken as level at the height found under the creature's root.
- On the way down to all fours the hands are not yet on the ground, and the knees are over it.
- As low as it goes the arms are folded hard, and from one frame to the next an elbow can move 15% of
  the height.
- What steps is read from the pose alone, so a knee comes down a little before its thigh is furthest
  forward, and the hips' sway and roll change faster than in a crawl made by hand.
- The hips do not turn about the upright (theirs do, 9 degrees either way), and the elbows bend less.
- A bone that points straight down and has to be laid on the ground has no way of its own to go, and
  goes forward; next to that pose a small change can lay it the other way.
- Most monsters have a core that is level already or is carried as one piece, and only crouch.
