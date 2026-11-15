// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Npcs;

namespace CombatSimulator;

// Enemy Control (post-defeat possession of one enemy): movement, camera, grab pairs, and
// saved grab profiles. Moved out of the private Dev/Experimental module — see Npcs/EnemyControlController.
public partial class Configuration
{
    // Shown under Effects > Enemy Control. The only Enemy Control setting on that page; everything
    // else lives in the window this opens, and every feature inside it defaults off.
    public bool ShowEnemyControlGui { get; set; } = false;

    public bool EnemyControlControlKiller { get; set; } = false;
    public float EnemyControlMoveSpeed { get; set; } = 6f;
    [Newtonsoft.Json.JsonIgnore] public float EnemyControlVerticalSpeed => 2.5f;
    public int EnemyControlAttackKey { get; set; } = 0x59;
    public bool EnemyControlCameraFollowsControlledTarget { get; set; } = true;
    public bool EnemyControlGroundWalk { get; set; } = true;
    // One pair per hold: which of the creature's own bones grabs, and which of the grabbed body's
    // bones it catches. Arbitrarily many can be live at once — e.g. one hand on the neck, the other
    // on the pelvis, for a carry/lift pose — so this is a list rather than a single fixed pair. The
    // default seeds exactly the old single-pair behavior for configs written before this existed.
    public List<EnemyControlGrabPairConfig> EnemyControlGrabPairs { get; set; } =
        new() { new EnemyControlGrabPairConfig() };
    // Which animation the creature plays while holding a grab. 0 = the built-in grab-and-hold pose
    // (normal/aettouch_loop); otherwise an Emote sheet row id whose own loop ActionTimeline is used
    // instead — see EnemyControlController.ResolveGrabAnimationTimeline and MainWindow's
    // DrawEnemyControlGrabAnimationPicker.
    public uint EnemyControlGrabEmoteId { get; set; } = 0;
    // Saved snapshots of the whole grab setup below (pairs, animation, fitting) — see
    // EnemyControlGrabProfile and Configuration.SeedEnemyControlGrabProfiles. GUI mirrors the Ragdoll bone
    // profile list (load/overwrite/delete/save).
    public List<EnemyControlGrabProfile> EnemyControlGrabProfiles { get; set; } = new();
    // Widen the grabbed-bone picker from the handful of usual suspects to every bone the ragdoll has
    // a body for — the whole set a grab can actually attach to. Shared across every pair's picker.
    public bool EnemyControlGrabAnyBone { get; set; } = false;
    // Same, for the creature's end of the grab: not everything that kills you has hands.
    public bool EnemyControlGrabAnyNpcBone { get; set; } = false;
    // Grab contact & conform (see GrabConformSolver). The old grab pinned the grabbed bone's body
    // centre onto the hand's bone origin with the creature's collider switched off — the two reasons
    // the hand went through the body.
    //
    // Keep the creature's collider live through the grab, so the body has something to rest against.
    // It used to be torn out at grab time and put back on release, and putting a collider back while
    // it is already overlapping the corpse is what injected the explosive impulses that got blamed on
    // the collider in the first place. Never removing it never re-inserts it.
    public bool EnemyControlGrabSoftContact { get; set; } = true;
    // Hold the caught bone exactly, by making it kinematic and driving it, rather than letting a spring
    // pull it. It is instant and it never trails a walking grabber — but it reads worse than the spring
    // in practice, so it is off. The spring's own two defects (a force ceiling that ignored the body's
    // weight, and the stretch that followed from raising it) are fixed on the spring path itself.
    public bool EnemyControlGrabRigid { get; set; } = false;
    // Multiplier on the hand's own measured grip depth. 1 = sit exactly where the creature's digits
    // put the centre of the grip volume; lower pulls the body back towards the wrist, higher pushes it
    // out past the tips. 1.75 rather than 1.0 because the measured centroid sits closer to the palm
    // than a grip actually closes around — it averages in the knuckles, which barely move — and 1.75
    // is what read right in game.
    public float EnemyControlGrabGripReach { get; set; } = 1.75f;
    // Spring-grab servo tuning (ignored by a rigid grab, which holds the bone kinematically).
    //
    // Force is floored internally at the rig's own weight plus headroom (ResolveGrabServoForce), so
    // a value below that does nothing; speed is a ceiling on how fast the hold may move the bone and
    // is rarely the binding constraint.
    //
    // Frequency 120 Hz. It was blamed for the bouncing and briefly dropped to 25; that was wrong on
    // the part that mattered. Two separate things bounce a grab, and only one of them is frequency's
    // fault:
    //
    //   - Numerical ringing. A spring wants roughly 10x its own frequency in solve rate, and at the
    //     old 8 substeps (480 Hz) 120 Hz got 4x. That was real, and raising RagdollSolverSubsteps to
    //     16 (960 Hz, ~8x) fixed it without touching the spring.
    //   - Force saturation. Stiffness is k = mw^2, so 120 Hz on a 2-6 kg torso bone is on the order
    //     of a million N/m, and past about a millimetre of error the servo hits its ceiling. BEPU
    //     clamps the whole impulse, so the DAMPING term is cut along with the spring, and a saturated
    //     servo is a full-force-either-way controller. This one is INDEPENDENT of the timestep: no
    //     number of substeps touches it, and raising the frequency makes it worse as 1/w^2.
    //
    // So 120 stays. Dropping it to 25 widened the linear range but sagged the body ~1.4 cm out of
    // the grip, which reads as a loose hold rather than a settled one — the tight grip is worth more
    // than the last of the saturation. Raising it FURTHER buys nothing: sag is already 0.6 mm, i.e.
    // invisible, while the linear range would shrink fourfold. Widen the range with force instead.
    public float EnemyControlGrabServoForce { get; set; } = 1000f;
    public float EnemyControlGrabServoSpeed { get; set; } = 50f;
    public float EnemyControlGrabServoFrequency { get; set; } = 120f;
    // Curl the creature's digits onto the grabbed body's surface instead of letting them animate
    // straight through it.
    public bool EnemyControlGrabConformFingers { get; set; } = true;
    // How far each joint is moved towards its conformed pose per frame. Below 1 it eases in, which
    // hides the pop when the grab lands and rides out a body that is still thrashing.
    public float EnemyControlGrabFingerStrength { get; set; } = 0.6f;
    // Ceiling on how far one joint may be bent away from its animated pose. Keeps a digit from
    // folding inside out to reach a surface it was never going to touch.
    public float EnemyControlGrabFingerMaxAngle { get; set; } = 45f;
    // Extra gap left between the digits and the skin, on top of the digit's own thickness. Raise it
    // if the fingers still read as sunk in; a small negative value lets them dimple the surface.
    public float EnemyControlGrabFingerClearance { get; set; } = 0f;

    /// <summary>
    /// Seeds the two built-in EnemyControl grab profiles once, idempotent by name (safe to call every
    /// load). "Default" mirrors this file's own field
    /// defaults; "Embrace" is a known-good two-hand carry pose baked in from tuned values.
    /// </summary>
    public void SeedEnemyControlGrabProfiles()
    {
        var changed = false;

        EnemyControlGrabPairs ??= new List<EnemyControlGrabPairConfig>();
        EnemyControlGrabProfiles ??= new List<EnemyControlGrabProfile>();
        changed |= DeduplicateEnemyControlGrabPairs(EnemyControlGrabPairs, ensureOne: true);
        foreach (var profile in EnemyControlGrabProfiles)
        {
            profile.Pairs ??= new List<EnemyControlGrabPairConfig>();
            changed |= DeduplicateEnemyControlGrabPairs(profile.Pairs, ensureOne: false);
        }

        if (!EnemyControlGrabProfiles.Exists(p => p.Name == "Default"))
        {
            EnemyControlGrabProfiles.Add(new EnemyControlGrabProfile
            {
                Name = "Default",
                Pairs = new List<EnemyControlGrabPairConfig> { new() },
                GrabAnyBone = false,
                GrabAnyNpcBone = false,
                GrabRigid = false,
                GrabSoftContact = true,
                GrabGripReach = 1.75f,
                GrabServoForce = 1000f,
                GrabServoSpeed = 50f,
                GrabServoFrequency = 120f,
                GrabConformFingers = true,
                GrabFingerStrength = 0.6f,
                GrabFingerMaxAngle = 45f,
                GrabFingerClearance = 0f,
            });
            changed = true;
        }

        if (!EnemyControlGrabProfiles.Exists(p => p.Name == "Embrace"))
        {
            EnemyControlGrabProfiles.Add(new EnemyControlGrabProfile
            {
                Name = "Embrace",
                Pairs = new List<EnemyControlGrabPairConfig>
                {
                    new() { NpcBone = "j_te_r", PlayerBone = "j_sebo_c" },
                    new() { NpcBone = "j_te_l", PlayerBone = "j_asi_b_l" },
                    new() { NpcBone = "j_te_l", PlayerBone = "j_asi_b_r" },
                },
                GrabAnyBone = true,
                GrabAnyNpcBone = false,
                GrabRigid = false,
                GrabSoftContact = true,
                GrabGripReach = 1.55f,
                GrabServoForce = 1000f,
                GrabServoSpeed = 50f,
                GrabServoFrequency = 120f,
                GrabConformFingers = true,
                GrabFingerStrength = 0.6f,
                GrabFingerMaxAngle = 45f,
                GrabFingerClearance = 0f,
            });
            changed = true;
        }

        if (changed) Save();
    }

    // Pairs differ by bones AND anchors (two picked points under the same pair of bones are two holds).
    private static bool DeduplicateEnemyControlGrabPairs(List<EnemyControlGrabPairConfig> pairs, bool ensureOne)
        => EnemyControlGrabPairList.Deduplicate(pairs, ensureOne);

}
