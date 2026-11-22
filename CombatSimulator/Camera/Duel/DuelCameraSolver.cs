// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Camera.DeathShots;

namespace CombatSimulator.Camera.Duel;

/// <summary>The no-roll view the duel camera renders with, and where a point lands in it. Game-free.</summary>
public static class DuelView
{
    /// <summary>Unit direction for an azimuth ((sin, cos) in xz, the convention of <see cref="DeathShotMath"/>) and a
    /// pitch (positive = up).</summary>
    public static Vector3 Direction(float azimuth, float pitch)
    {
        var c = MathF.Cos(pitch);
        return new Vector3(MathF.Sin(azimuth) * c, MathF.Sin(pitch), MathF.Cos(azimuth) * c);
    }

    /// <summary>
    /// A world point in the picture of a camera at <paramref name="eye"/> looking along <paramref name="forward"/>
    /// (unit, no roll): x and y in [−1, 1] across the picture when inside it, z the depth along the view (≤ 0:
    /// behind the camera). Which of left and right is +x is not the game's concern here: the solver only uses extents.
    /// </summary>
    public static Vector3 Project(Vector3 eye, Vector3 forward, float verticalFov, float aspect, Vector3 point)
    {
        var right = Vector3.Cross(Vector3.UnitY, forward);
        if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        var up = Vector3.Cross(forward, right);
        var v = point - eye;
        var z = Vector3.Dot(v, forward);
        var tanV = MathF.Tan(verticalFov * 0.5f);
        var tanH = tanV * aspect;
        var depth = MathF.Max(z, 1e-4f);
        return new Vector3(Vector3.Dot(v, right) / (depth * tanH), Vector3.Dot(v, up) / (depth * tanV), z);
    }
}

/// <summary>
/// The duel camera (docs/DUEL_CAMERA.md): a two-shot of the player and one opponent, from behind and to one side of
/// the player, that an offline check and the plugin both run. Game-free: it is fed two <see cref="DuelSubject"/>s a
/// frame and asks the world three things (<see cref="IDeathShotWorld"/>: ground, water, what blocks a segment).
///
/// The eye orbits the player: its azimuth is "behind the player, seen from the opponent", turned to one side by an
/// angle that shrinks as the fighters separate (close: a wide three-quarter view that puts one on each side of the
/// picture; far: nearly over the shoulder, so the far fighter stays in the picture without the camera backing away).
/// The view is then aimed between "the box around both fighters in the middle of the picture" and "the opponent in
/// the middle" (the player sits further out on their side, the opponent just across the middle), and the distance is
/// the recipe's for that separation, more when the box would not fit, then a wider lens.
///
/// The camera never crosses the line between the fighters on its own account. When the fighters cross over (or the
/// opponent changes) the side is picked again as the one nearer to where the camera is, so it turns the short way,
/// through the side-on view. It crosses only when the player drags it across, or when something has stood in the way
/// on its side for a while and going further round on that side does not help; then it goes behind the player, never
/// round the front.
///
/// Everything is damped relative to the player (the eye's azimuth, elevation and distance about them; the aim as an
/// offset from "towards the player"), so the camera travels with the player without lag of its own and only the
/// composition eases. Key moments (<see cref="Beat"/>) push in for a few tenths of a second on top of that.
///
/// Besides the pose it gives the yaw the player should move by (<see cref="DuelOutput.MoveYaw"/>): the rendered yaw
/// leaned towards the opponent, so that forward goes at the opponent and sideways round them.
/// </summary>
public sealed class DuelCameraSolver
{
    public const float Deg = DeathShotMath.Deg;

    /// <summary>What a framing is made of. Lengths are for the reference body (<see cref="ReferenceHeight"/>).</summary>
    public readonly record struct Recipe(
        /// <summary>The lens (vertical, radians), and the widest it may get when the fighters do not fit.</summary>
        float Fov, float MaxFov,
        /// <summary>The eye's distance from the player when the fighters are close, when they are far apart, when
        /// there is no opponent, and the most it may back away to fit them.</summary>
        float Near, float Far, float Single, float Max,
        /// <summary>How far up the player (feet to head) the part that has to stay in the picture starts: a tight
        /// two-shot shows them from the waist up, a wide one nearly whole.</summary>
        float PlayerFrom);

    public static Recipe RecipeFor(DuelFraming framing) => framing switch
    {
        DuelFraming.Tight => new Recipe(0.62f, 0.90f, 2.2f, 3.4f, 2.9f, 6.5f, 0.45f),
        DuelFraming.Wide => new Recipe(0.82f, 1.10f, 3.3f, 5.2f, 4.4f, 10f, 0.15f),
        _ => new Recipe(0.72f, 1.00f, 2.7f, 4.2f, 3.6f, 8f, 0.30f),
    };

    /// <summary>The player's feet-to-head-bone height the recipes' lengths are for.</summary>
    public const float ReferenceHeight = 1.5f;
    /// <summary>What the eye orbits: this far up the player, feet to head.</summary>
    public const float AnchorHeight = 0.74f;

    // ---- the side-on angle ----
    /// <summary>The eye's angle off "straight behind the player" when the fighters are close ...</summary>
    public const float SideAngleNear = 36f * Deg;
    /// <summary>... and when they are far apart.</summary>
    public const float SideAngleFar = 20f * Deg;
    public const float SideNearRange = 2.5f, SideFarRange = 14f;
    /// <summary>Closer than this the direction from one fighter to the other is not trusted (it spins as they pass
    /// through each other): the last one is kept.</summary>
    public const float FreezeSeparation = 1.0f;
    /// <summary>A turn of that direction larger than this in one frame is a cross-over, not a motion to follow.</summary>
    public const float AxisJump = 60f * Deg;
    /// <summary>The fighters' separations between which the distance goes from the recipe's near to its far one.</summary>
    public const float TightenRange = 2f, WidenRange = 9f;

    // ---- the player's hand ----
    public const float BaseElevation = 10f * Deg;
    public const float MinUserPitch = -14f * Deg, MaxUserPitch = 30f * Deg;
    public const float WheelStep = 0.10f, MinUserSize = 0.6f, MaxUserSize = 1.8f;
    /// <summary>The player's left/right offset goes back to nothing after this long without input, over about this long.</summary>
    public const float YawRecentreDelay = 1.0f, YawRecentreTime = 0.8f;
    /// <summary>The player may turn the camera this far off "behind the player" on either side.</summary>
    public const float MaxOffAxis = 170f * Deg;
    /// <summary>Dragged this far past "behind the player", the camera adopts the other side.</summary>
    public const float UserCrossAngle = 15f * Deg;

    // ---- the yaw the player moves by ----
    public const float DefaultMoveLean = 30f * Deg;
    /// <summary>The lean fades out as the player turns the camera themselves, and is gone at this much.</summary>
    public const float MoveLeanHandOff = 40f * Deg;
    public const float MoveLeanSmooth = 0.15f, MoveLeanCap = 90f * Deg;

    // ---- damping ----
    public const float AnchorSmooth = 0.03f;
    public const float YawSmooth = 0.28f, YawSmoothInHand = 0.12f, YawCap = 160f * Deg;
    /// <summary>The turn rate of the line between the fighters is smoothed over this long before the eye is carried
    /// round with it, and never carries it faster than the cap.</summary>
    public const float AxisRateSmooth = 0.10f, AxisRateCap = 240f * Deg;
    public const float ElevationSmooth = 0.30f, ElevationCap = 90f * Deg;
    public const float DistanceSmooth = 0.35f, DistanceCap = 8f;
    public const float AimSmooth = 0.14f, AimCap = 240f * Deg;
    public const float FovSmooth = 0.35f, FovCap = 0.8f;
    /// <summary>The view never turns faster than this (outside a cut).</summary>
    public const float ViewCap = 240f * Deg;
    /// <summary>The player moved this far in one frame: a teleport, the camera goes with them and the frame is a cut.</summary>
    public const float TeleportDistance = 8f;

    // ---- fit ----
    /// <summary>How much of the picture's half width / half height the box around the fighters may fill.</summary>
    public const float SafeX = 0.86f, SafeY = 0.88f;
    /// <summary>Where the view points between "the box around both fighters centred" (0) and "the opponent in the
    /// middle" (1).</summary>
    public const float AimBias = 0.6f;
    public const float MinDistance = 1.3f;
    /// <summary>Both heads stay within this much of the picture during a push-in.</summary>
    public const float BeatHeadLimit = 0.90f;

    // ---- beats ----
    public const float BeatPush = 0.24f, BeatZoom = 0.16f, BeatAim = 0.35f;
    public const float BeatRise = 0.09f, BeatFall = 0.22f;
    /// <summary>After a push-in, the next one waits this long (the finishing blow does not).</summary>
    public const float BeatGap = 0.9f;

    // ---- constraints ----
    public const float GroundRayLift = 1.5f, GroundClearance = 0.25f, WaterClearance = 0.15f, GroundHardFloor = 0.05f;
    public const float LiftRate = 10f, LiftRelease = 1.5f;
    public const float WallMargin = 0.25f, MinPulled = 0.7f, PullRelease = 2.5f, PullWiden = 0.25f, OverHead = 0.8f;
    /// <summary>The eye is held at less than this much of its distance ...</summary>
    public const float PulledMuch = 0.65f;
    /// <summary>... for this long: the other side is tried. And an opponent hidden behind a wall for this long.</summary>
    public const float BlockedHold = 0.6f, HiddenHold = 1.0f;
    public const float SideCooldown = 2.5f, SideRetest = 0.5f;
    /// <summary>Pulled in for this long, the eye goes further round on its own side, in steps, up to so much.</summary>
    public const float GiveHold = 0.15f, GiveStep = 20f * Deg, MaxGive = 60f * Deg, GiveCooldown = 0.3f;
    /// <summary>The eye counts as pulled in below this much of its distance; a place counts as free with this much;
    /// a place that is not free is still taken when it leaves this much more than the eye has now.</summary>
    public const float PulledSome = 0.85f, GiveFree = 0.9f, GiveGain = 0.25f;
    public const int GiveSteps = 3;
    /// <summary>The eye stays this far outside the opponent's body.</summary>
    public const float BodyClearance = 0.45f;

    private readonly IDeathShotWorld world;

    // begin
    private bool began, started;
    private Vector3 beginEye, beginLook;
    private float beginFov;

    // the player
    private Vector3 anchor, anchorVel;
    private float height, heightVel, stand;

    // the line between the fighters
    private bool haveAxis, wasTwo;
    private float axisAz, axisRate;
    private uint opponentId;
    private int side = 1;
    private int flips, reanchors;
    private float sideCooldown, pulledFor, blockedFor, hiddenFor, give, giveCheck;

    // the orbit about the player, the aim relative to "towards the player", the lens
    private float psi, psiVel, eps, epsVel, dist, distVel, aimYaw, aimYawVel, aimPitch, aimPitchVel, fov, fovVel;
    private float singleAz;

    // the player's hand
    private float userYaw, userPitch, userSize = 1f, yawIdle = 10f;

    // the yaw the player moves by, as a lean off the rendered one
    private float moveLean, moveLeanVel;

    // beat
    private float beat, beatVel, beatStrength, beatRemaining, beatFocus, sinceBeat = 10f;

    // constraints
    private float pull = 1f, lift;

    // output
    private bool haveOut;
    private float outAz, outPitch;
    private DuelOutput last;

    // the points that have to stay in the picture: 0 the player's head, 1 the player low, 2 the opponent's head, 3 the opponent low
    private readonly Vector3[] points = new Vector3[4];
    private readonly float[] radii = new float[4];
    private int pointCount;

    public DuelCameraSolver(IDeathShotWorld world) => this.world = world;

    /// <summary>The player's wheel offset survives from one fight to the next.</summary>
    public float UserSize => userSize;

    /// <summary>How far the yaw the player moves by may lean off the rendered yaw towards the opponent (radians; 0:
    /// the player moves by the rendered yaw alone).</summary>
    public float MoveLean { get; set; } = DefaultMoveLean;

    /// <summary>Start from the camera as it is rendered now (vertical FoV in radians). The yaw and pitch offsets of
    /// the player start at nothing; the wheel's size is kept.</summary>
    public void Begin(Vector3 eye, Vector3 lookAt, float verticalFov)
    {
        beginEye = eye;
        beginLook = lookAt;
        beginFov = float.IsFinite(verticalFov) && verticalFov > 0.05f ? verticalFov : 0.72f;
        began = true;
        started = false;
        haveAxis = wasTwo = false;
        axisRate = 0f;
        opponentId = 0;
        side = 1;
        flips = reanchors = 0;
        sideCooldown = pulledFor = blockedFor = hiddenFor = give = giveCheck = 0f;
        userYaw = userPitch = 0f;
        yawIdle = 10f;
        beat = beatVel = beatStrength = beatRemaining = 0f;
        sinceBeat = 10f;
        pull = 1f;
        moveLean = moveLeanVel = 0f;
        lift = 0f;
        stand = 0f;
        haveOut = false;
        last = default;
    }

    /// <summary>
    /// A key moment: push in briefly. Refused (false) while a stronger push-in is on, or too soon after the last
    /// one; a finishing blow is always taken.
    /// </summary>
    public bool Beat(DuelBeat kind)
    {
        var (strength, hold, focus) = kind switch
        {
            DuelBeat.HitGiven => (1.0f, 0.25f, 1f),
            DuelBeat.HitTaken => (0.8f, 0.25f, -1f),
            DuelBeat.Guard => (1.0f, 0.30f, 0f),
            DuelBeat.Threat => (0.55f, 0.40f, 1f),
            _ => (1.0f, 0.60f, 1f),
        };
        if (kind != DuelBeat.Finish)
        {
            if (beatRemaining > 0f && strength <= beatStrength) return false;
            if (beatRemaining <= 0f && sinceBeat < BeatGap) return false;
        }
        beatStrength = strength;
        beatRemaining = BeatRise * 2f + hold;
        beatFocus = focus;
        sinceBeat = 0f;
        return true;
    }

    /// <summary>The eye's angle off "straight behind the player" for a separation of the fighters.</summary>
    public static float SideAngle(float separation) =>
        SideAngleNear + (SideAngleFar - SideAngleNear) * DeathShotMath.SmoothStep((separation - SideNearRange) / (SideFarRange - SideNearRange));

    public DuelOutput Step(in DuelInput input)
    {
        var dt = Math.Clamp(float.IsFinite(input.Dt) ? input.Dt : 0f, 0f, 0.1f);
        if (!began || !input.Player.IsFinite) return last;
        var aspect = float.IsFinite(input.Aspect) && input.Aspect > 0.2f && input.Aspect < 8f ? input.Aspect : 16f / 9f;
        var recipe = RecipeFor(input.Framing);
        var player = input.Player;
        var two = input.Opponent is { IsFinite: true };
        var opponent = two ? input.Opponent!.Value : default;

        // ---- the player: what the eye orbits ----
        var cut = false;
        var heightNow = Math.Clamp(player.Height, 0.4f, 4f);
        stand = MathF.Max(stand, heightNow);
        height = started ? DeathShotMath.Spring(height, heightNow, ref heightVel, 0.5f, 4f, dt) : heightNow;
        var anchorTarget = player.Feet + new Vector3(0f, AnchorHeight * height, 0f);
        if (!started)
        {
            anchor = anchorTarget;
            anchorVel = Vector3.Zero;
            heightVel = 0f;
            StartFromBegin();
            cut = true;
        }
        else if (Vector3.Distance(anchorTarget, anchor) > TeleportDistance)
        {
            anchor = anchorTarget;
            anchorVel = Vector3.Zero;
            cut = true;
        }
        else
        {
            anchor = DeathShotMath.Spring(anchor, anchorTarget, ref anchorVel, AnchorSmooth, 200f, dt);
        }
        var scale = Math.Clamp(stand / ReferenceHeight, 0.5f, 2.5f);

        // ---- the player's hand ----
        var yawIn = float.IsFinite(input.YawInput) ? Math.Clamp(input.YawInput, -1f, 1f) : 0f;
        var pitchIn = float.IsFinite(input.PitchInput) ? Math.Clamp(input.PitchInput, -1f, 1f) : 0f;
        yawIdle = MathF.Abs(yawIn) > 1e-5f ? 0f : yawIdle + dt;
        userPitch = Math.Clamp(userPitch + pitchIn, MinUserPitch, MaxUserPitch);
        if (float.IsFinite(input.WheelInput) && input.WheelInput != 0f)
            userSize = Math.Clamp(userSize * MathF.Exp(-WheelStep * Math.Clamp(input.WheelInput, -20f, 20f)), MinUserSize, MaxUserSize);

        // ---- the line between the fighters, and the side of it ----
        var sideChanged = false;
        var reanchored = false;
        var reason = "";
        var separation = 0f;
        var phi = 0f;
        var turning = 0f;
        if (two)
        {
            var to = opponent.At(0.7f) - anchorTarget;
            to.Y = 0f;
            separation = to.Length();
            var newOpponent = !wasTwo || input.OpponentId != opponentId;
            phi = SideAngle(separation / scale);
            if ((separation >= FreezeSeparation * scale || !haveAxis) && separation > 1e-3f)
            {
                var az = DeathShotMath.Azimuth(to);
                var turned = haveAxis ? DeathShotMath.AngleDelta(axisAz, az) : MathF.PI;
                var jump = MathF.Abs(turned);
                var hadAxis = haveAxis;
                axisAz = az;
                haveAxis = true;
                if (hadAxis && !newOpponent && jump <= AxisJump)
                {
                    // The line turns as the fighters circle each other: its turn rate, smoothed, carries the eye
                    // round with it (below), so circling leaves no standing lag for the spring to hold.
                    // (Not at arm's length: there the line swings because the fighters pass each other.)
                    turning = dt > 1e-5f ? turned / dt * DeathShotMath.SmoothStep((separation / scale - FreezeSeparation) / 0.5f) : 0f;
                }
                else
                {
                    // The fighters crossed over, or it is another opponent: the side nearer to where the eye is. The
                    // eye does not cross the line for this; it turns the short way.
                    var behind = axisAz + MathF.PI;
                    var plus = MathF.Abs(DeathShotMath.AngleDelta(psi, behind + phi));
                    var minus = MathF.Abs(DeathShotMath.AngleDelta(psi, behind - phi));
                    // (Much the same either way: after a cross-over the side that keeps the eye on its side of the
                    // line, which is the other one by name; with a new opponent the one it has.)
                    var onTie = hadAxis && !newOpponent ? -side : side;
                    side = plus + 0.2f < minus ? 1 : minus + 0.2f < plus ? -1 : onTie;
                    userYaw = 0f;
                    give = 0f;
                    reason = !hadAxis ? "start" : newOpponent ? "opponent" : "crossed";
                    if (started && !cut || hadAxis)
                    {
                        reanchored = true;
                        reanchors++;
                    }
                }
            }
            opponentId = input.OpponentId;
        }
        var twoShot = two && haveAxis;
        if (!twoShot) haveAxis = false;

        float psiTarget;
        if (twoShot)
        {
            userYaw += yawIn;
            if (yawIdle > YawRecentreDelay) userYaw *= MathF.Exp(-dt / YawRecentreTime);
            var beta = Math.Clamp(side * (phi + give) + userYaw, -MaxOffAxis, MaxOffAxis);
            if (MathF.Sign(beta) == -side && MathF.Abs(beta) > UserCrossAngle)
            {
                // The player dragged the camera across the line: it stays there.
                side = -side;
                sideChanged = true;
                reason = "player";
                flips++;
                sideCooldown = SideCooldown;
            }
            userYaw = beta - side * (phi + give);
            psiTarget = axisAz + MathF.PI + beta;
        }
        else
        {
            // Nobody to frame the player against: the azimuth is the player's to turn, and stays where it is left.
            if (wasTwo || !started || cut && !haveOut) singleAz = psi;
            singleAz = DeathShotMath.WrapAngle(singleAz + yawIn);
            userYaw = 0f;
            psiTarget = singleAz;
        }
        wasTwo = twoShot;

        // ---- the orbit about the player ----
        axisRate += (Math.Clamp(twoShot ? turning : 0f, -AxisRateCap, AxisRateCap) - axisRate) * (1f - MathF.Exp(-dt / AxisRateSmooth));
        if (reanchored || cut) axisRate = 0f;
        psi += axisRate * dt;
        var inHand = yawIdle < 0.25f;
        psi = DeathShotMath.WrapAngle(DeathShotMath.Spring(psi, psi + DeathShotMath.AngleDelta(psi, psiTarget), ref psiVel,
            inHand ? YawSmoothInHand : YawSmooth, YawCap, dt));
        eps = DeathShotMath.Spring(eps, BaseElevation + userPitch, ref epsVel, ElevationSmooth, ElevationCap, dt);

        // ---- how far, and the lens: the recipe's for this separation, more when the fighters do not fit ----
        BuildPoints(in opponent, twoShot, scale, recipe.PlayerFrom);
        var preferred = twoShot
            ? recipe.Near + (recipe.Far - recipe.Near) * DeathShotMath.SmoothStep((separation / scale - TightenRange) / (WidenRange - TightenRange))
            : recipe.Single;
        preferred = MathF.Max(MinDistance * scale, preferred * scale * userSize);
        var distanceTarget = preferred;
        var fovTarget = recipe.Fov;
        var over = Overflow(EyeAt(preferred), recipe.Fov, aspect);
        if (over > 1f)
        {
            var most = MathF.Max(preferred, recipe.Max * scale);
            var overAtMost = Overflow(EyeAt(most), recipe.Fov, aspect);
            if (overAtMost > 1f)
            {
                distanceTarget = most;
                fovTarget = MathF.Min(recipe.MaxFov, 2f * MathF.Atan(MathF.Tan(recipe.Fov * 0.5f) * overAtMost));
            }
            else
            {
                float lo = preferred, hi = most;
                for (var i = 0; i < 10; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (Overflow(EyeAt(mid), recipe.Fov, aspect) > 1f) lo = mid;
                    else hi = mid;
                }
                distanceTarget = hi;
            }
        }
        dist = DeathShotMath.Spring(dist, distanceTarget, ref distVel, DistanceSmooth, DistanceCap, dt);
        fov = DeathShotMath.Spring(fov, fovTarget, ref fovVel, FovSmooth, FovCap, dt);

        // ---- the aim: the box around the fighters in the middle of the picture ----
        Compose(EyeAt(dist), out var yawGoal, out var pitchGoal, out _, out _);
        KeepPlayer(EyeAt(dist), fov, aspect, ref yawGoal, ref pitchGoal);
        aimYaw = DeathShotMath.Spring(aimYaw, aimYaw + DeathShotMath.AngleDelta(aimYaw, yawGoal), ref aimYawVel, AimSmooth, AimCap, dt);
        aimPitch = DeathShotMath.Spring(aimPitch, pitchGoal + eps, ref aimPitchVel, AimSmooth, AimCap, dt);
        var viewAz = psi + MathF.PI + aimYaw;
        var viewPitch = -eps + aimPitch;

        // ---- the push-in of a key moment, as far as both heads stay in the picture ----
        sinceBeat += dt;
        beatRemaining -= dt;
        var beatGoal = beatRemaining > 0f ? beatStrength : 0f;
        beat = DeathShotMath.Spring(beat, beatGoal, ref beatVel, beatGoal > beat ? BeatRise : BeatFall, 30f, dt);
        if (beat < 1e-4f && beatGoal <= 0f) beat = 0f;
        var distanceOut = dist;
        var fovOut = fov;
        var applied = 0f;
        if (beat > 1e-3f)
        {
            applied = beat;
            if (!BeatFits(applied, twoShot, aspect, viewAz, viewPitch, out distanceOut, out fovOut, out var az, out var pitch))
            {
                float lo = 0f, hi = applied;
                for (var i = 0; i < 6; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (BeatFits(mid, twoShot, aspect, viewAz, viewPitch, out _, out _, out _, out _)) lo = mid;
                    else hi = mid;
                }
                applied = lo;
                BeatFits(applied, twoShot, aspect, viewAz, viewPitch, out distanceOut, out fovOut, out az, out pitch);
            }
            viewAz = az;
            viewPitch = pitch;
        }

        // ---- what stands in the way: walls and terrain between the player and the eye pull the eye in ----
        var from = anchor + new Vector3(0f, 0.22f * height, 0f);
        var unconstrained = EyeAt(distanceOut);
        var reach = unconstrained - from;
        var length = MathF.Max(reach.Length(), 1e-3f);
        var allowed = 1f;
        if (world.Blocked(from, unconstrained + reach / length * WallMargin, out var hitAt))
            allowed = Math.Clamp((hitAt - MathF.Min(WallMargin, hitAt * 0.5f)) / length, 0.02f, 1f);
        pull = cut || allowed < pull ? allowed : MathF.Min(allowed, pull + PullRelease * dt / length);
        var eye = from + reach * pull;
        // Pulled in closer than a body allows (the player has their back to a wall): up over their head instead of
        // into it, as far as nothing is in the way above.
        var tooNear = MinPulled * scale - length * pull;
        if (tooNear > 0f)
        {
            var rise = tooNear * OverHead;
            if (world.Blocked(eye, eye + new Vector3(0f, rise + WallMargin, 0f), out var ceiling))
                rise = MathF.Max(0f, ceiling - WallMargin);
            eye.Y += rise;
        }
        fovOut = MathF.Min(MathF.Max(fovOut, recipe.MaxFov), fovOut * (1f + PullWiden * (1f - pull)));

        // Something in the way for a while (of the eye, or between the eye and the opponent): first further round
        // on this side (a player with their back to a wall gets a side-on view along it), then the other side.
        sideCooldown -= dt;
        giveCheck -= dt;
        // (Judged by the place the eye is heading for: an eye that is still on its way to a free place is not stuck.)
        var ahead = twoShot && pull < PulledSome ? Room(from, side * (phi + give)) : 1f;
        pulledFor = pull < PulledSome && ahead < GiveFree ? pulledFor + dt : MathF.Max(0f, pulledFor - 2f * dt);
        blockedFor = pull < PulledMuch && ahead < PulledMuch ? blockedFor + dt : MathF.Max(0f, blockedFor - 2f * dt);
        var hidden = twoShot && world.Blocked(points[2], eye, out _);
        hiddenFor = hidden ? hiddenFor + dt : MathF.Max(0f, hiddenFor - 2f * dt);
        if (!twoShot)
        {
            give = 0f;
        }
        else if (sideCooldown <= 0f && (pulledFor >= GiveHold || hiddenFor >= HiddenHold))
        {
            // The nearest step round on this side that is free; failing that, the one that leaves the eye most
            // room, when that is clearly more than it has now.
            var found = false;
            var best = 0;
            var bestRoom = hiddenFor >= HiddenHold ? 0f : pull + GiveGain;
            for (var k = 1; k <= GiveSteps && !found; k++)
            {
                if (give + k * GiveStep > MaxGive + 1e-3f) break;
                var room = Room(from, side * (phi + give + k * GiveStep));
                if (room >= GiveFree)
                {
                    best = k;
                    found = true;
                }
                else if (room > bestRoom)
                {
                    best = k;
                    bestRoom = room;
                }
            }
            if (best > 0)
            {
                give += best * GiveStep;
                sideCooldown = GiveCooldown;
                found = true;
            }
            else if (blockedFor >= BlockedHold || hiddenFor >= HiddenHold)
            {
                for (var k = 0; k <= GiveSteps && !found; k++)
                {
                    if (Room(from, -side * (phi + k * GiveStep)) < GiveFree) continue;
                    side = -side;
                    give = k * GiveStep;
                    userYaw = 0f;
                    sideChanged = true;
                    reason = blockedFor >= BlockedHold ? "blocked" : "hidden";
                    flips++;
                    sideCooldown = SideCooldown;
                    found = true;
                }
            }
            if (found) pulledFor = blockedFor = hiddenFor = 0f;
            else sideCooldown = GiveCooldown;
        }
        else if (give > 0f && giveCheck <= 0f && pulledFor <= 0f && hiddenFor <= 0f)
        {
            // Back towards the usual place as soon as it is free again.
            giveCheck = SideRetest;
            if (Room(from, side * (phi + give - GiveStep)) >= GiveFree) give = MathF.Max(0f, give - GiveStep);
        }

        // ---- not inside the opponent ----
        if (two)
            eye = PushOut(eye, opponent.Feet, opponent.Head, Math.Clamp(opponent.Radius, 0.25f, 1.5f) + BodyClearance, reach);

        // ---- not under the ground or the water ----
        float? floor = null;
        var ground = world.GroundBelow(eye + new Vector3(0f, GroundRayLift, 0f));
        if (ground is { } g && float.IsFinite(g)) floor = g + GroundClearance;
        if (world.WaterBelow(eye + new Vector3(0f, GroundRayLift, 0f)) is { } w && float.IsFinite(w))
            floor = MathF.Max(floor ?? float.NegativeInfinity, w + WaterClearance);
        var need = floor is { } f ? MathF.Max(0f, f - eye.Y) : 0f;
        lift = cut ? need : need > lift ? MathF.Min(need, lift + LiftRate * dt) : MathF.Max(need, lift - LiftRelease * dt);
        eye.Y += lift;
        if (ground is { } hard && float.IsFinite(hard) && eye.Y < hard + GroundHardFloor) eye.Y = hard + GroundHardFloor;

        // The eye was moved: keep the player where they were in the picture.
        var before = from - unconstrained;
        var after = from - eye;
        if (Horizontal(before) > 0.05f && Horizontal(after) > 0.05f)
        {
            viewAz += DeathShotMath.AngleDelta(DeathShotMath.Azimuth(before), DeathShotMath.Azimuth(after));
            viewPitch += MathF.Atan2(after.Y, Horizontal(after)) - MathF.Atan2(before.Y, Horizontal(before));
        }
        viewPitch = Math.Clamp(viewPitch, -80f * Deg, 80f * Deg);
        viewAz = DeathShotMath.WrapAngle(viewAz);

        // ---- the view never whips ----
        if (haveOut && !cut && dt > 1e-5f)
        {
            var turn = DeathShotMath.AngleDelta(outAz, viewAz);
            var tilt = viewPitch - outPitch;
            var across = turn * MathF.Cos(outPitch);
            var angle = MathF.Sqrt(across * across + tilt * tilt);
            var most = ViewCap * dt;
            if (angle > most)
            {
                var k = most / angle;
                viewAz = DeathShotMath.WrapAngle(outAz + turn * k);
                viewPitch = outPitch + tilt * k;
            }
        }
        outAz = viewAz;
        outPitch = viewPitch;
        haveOut = true;
        started = true;

        var forward = DuelView.Direction(viewAz, viewPitch);

        // ---- the yaw the player moves by ----
        // The game moves the player relative to its own camera yaw, which the plugin writes every frame. Written as
        // the rendered yaw alone, "forward" would point where the view points: between the fighters, some 20° off
        // the opponent. Sideways would then be a spiral (in on one side, out on the other), and the spiral in ends
        // in a tight orbit the camera has to whirl round. So the yaw is leaned from the rendered one towards "behind
        // the player on the line to the opponent", by no more than MoveLean: with the camera in its place forward is
        // exactly towards the opponent and sideways a circle round them; with the camera on its way somewhere, or
        // turned by the player's own hand, it stays within that of the picture.
        var yawOut = DeathShotMath.WrapAngle(viewAz + MathF.PI);
        var leanGoal = 0f;
        if (twoShot && MoveLean > 0f)
            leanGoal = Math.Clamp(DeathShotMath.AngleDelta(yawOut, axisAz + MathF.PI), -MoveLean, MoveLean) *
                       (1f - DeathShotMath.SmoothStep(MathF.Abs(userYaw) / MoveLeanHandOff));
        moveLean = DeathShotMath.Spring(moveLean, leanGoal, ref moveLeanVel, MoveLeanSmooth, MoveLeanCap, dt);
        var subject = twoShot ? (anchor + opponent.At(0.7f)) * 0.5f : anchor;
        var depth = MathF.Max(1f, Vector3.Distance(subject, eye));
        last = new DuelOutput
        {
            Eye = eye,
            LookAt = eye + forward * depth,
            Fov = Math.Clamp(fovOut, 0.25f, 1.4f),
            Yaw = yawOut,
            MoveYaw = DeathShotMath.WrapAngle(yawOut + moveLean),
            Cut = cut,
            TwoShot = twoShot,
            Side = side,
            SideChanged = sideChanged,
            Reason = reason,
            SideChanges = flips,
            Reanchored = reanchored,
            Reanchors = reanchors,
            Separation = separation,
            Distance = dist,
            OffAxis = twoShot ? DeathShotMath.AngleDelta(axisAz + MathF.PI, psi) : 0f,
            Beat = applied,
            Pull = pull,
            Lift = lift,
            UserYaw = userYaw,
            UserPitch = userPitch,
            UserSize = userSize,
            Give = give,
        };
        return last;
    }

    /// <summary>How much of its distance the eye would have at an angle off "behind the player" (signed) before
    /// something stands in the way (1: all of it); 0 when the opponent could not be seen from there.</summary>
    private float Room(Vector3 from, float offAxis)
    {
        var place = anchor + DeathShotMath.OrbitDirection(axisAz + MathF.PI + offAxis, eps) * dist;
        var reach = place - from;
        var length = MathF.Max(reach.Length(), 1e-3f);
        var room = world.Blocked(from, place + reach / length * WallMargin, out var hit) ? Math.Clamp((hit - WallMargin) / length, 0f, 1f) : 1f;
        return world.Blocked(points[2], from + reach * room, out _) ? 0f : room;
    }

    /// <summary>The first frame: the orbit, the aim and the lens of the camera the shot starts from.</summary>
    private void StartFromBegin()
    {
        var rel = beginEye - anchor;
        var d = rel.Length();
        if (!(d > 0.2f) || !float.IsFinite(d))
        {
            rel = new Vector3(0f, 0.3f, -1f) * 3f;
            d = rel.Length();
        }
        dist = Math.Clamp(d, 0.5f, 40f);
        psi = DeathShotMath.Azimuth(rel);
        eps = MathF.Asin(Math.Clamp(rel.Y / d, -1f, 1f));
        var forward = beginLook - beginEye;
        if (forward.LengthSquared() < 1e-6f || !float.IsFinite(forward.LengthSquared())) forward = -rel;
        forward = Vector3.Normalize(forward);
        aimYaw = Horizontal(forward) > 1e-3f ? DeathShotMath.AngleDelta(psi + MathF.PI, DeathShotMath.Azimuth(forward)) : 0f;
        aimPitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)) + eps;
        fov = beginFov;
        psiVel = epsVel = distVel = aimYawVel = aimPitchVel = fovVel = 0f;
        singleAz = psi;
    }

    private Vector3 EyeAt(float distance) => anchor + DeathShotMath.OrbitDirection(psi, eps) * distance;

    private static float Horizontal(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    /// <summary>What has to stay in the picture: each fighter's head and a point low on the body (not the feet: a
    /// close two-shot may lose them).</summary>
    private void BuildPoints(in DuelSubject opponent, bool twoShot, float scale, float playerFrom)
    {
        // The player's points ride on the eased anchor, so the player's own bob does not shake the aim.
        var feet = anchor - new Vector3(0f, AnchorHeight * height, 0f);
        points[0] = new Vector3(feet.X, feet.Y + height + 0.05f * scale, feet.Z);
        radii[0] = 0.16f * scale;
        // Wheeled in closer than the recipe: the player asked for less of the bodies, not for the camera to back off again.
        var closer = MathF.Max(0f, 1f - userSize);
        points[1] = new Vector3(feet.X, feet.Y + MathF.Min(0.7f, playerFrom + closer) * height, feet.Z);
        radii[1] = 0.25f * scale;
        pointCount = 2;
        if (!twoShot) return;
        var tall = opponent.Height;
        points[2] = opponent.Head;
        radii[2] = Math.Clamp(0.16f * tall / ReferenceHeight, 0.12f, 0.6f);
        points[3] = opponent.At(MathF.Min(0.6f, 0.2f + closer));
        radii[3] = Math.Clamp(opponent.Radius, 0.25f, 1.5f);
        pointCount = 4;
    }

    /// <summary>
    /// From <paramref name="eye"/>: the view direction that puts the box around the points in the middle of the
    /// picture (its yaw as an offset from "towards the player", its pitch), and the box's half extents as angles.
    /// </summary>
    private void Compose(Vector3 eye, out float yawOffset, out float pitch, out float halfX, out float halfY)
    {
        if (!Span(eye, 0, pointCount, out var minX, out var maxX, out var minY, out var maxY))
        {
            yawOffset = 0f;
            pitch = -eps;
            halfX = halfY = 0f;
            return;
        }
        yawOffset = (minX + maxX) * 0.5f;
        if (pointCount == 4 && Span(eye, 2, 1, out var left, out var right, out _, out _))
        {
            // Lean towards the opponent: the nearer "into the picture" is to "towards the opponent", the truer the
            // player's forward and sideways are to the fight (they move by the rendered view).
            yawOffset += ((left + right) * 0.5f - yawOffset) * AimBias;
        }
        pitch = (minY + maxY) * 0.5f;
        halfX = MathF.Max(maxX - yawOffset, yawOffset - minX);
        halfY = (maxY - minY) * 0.5f;
    }

    /// <summary>The angles a run of the points spans from <paramref name="eye"/>: left/right as offsets from
    /// "towards the player", up/down as pitch.</summary>
    private bool Span(Vector3 eye, int first, int count, out float minX, out float maxX, out float minY, out float maxY)
    {
        var reference = psi + MathF.PI;
        minX = minY = float.PositiveInfinity;
        maxX = maxY = float.NegativeInfinity;
        for (var i = first; i < first + count; i++)
        {
            var v = points[i] - eye;
            var distance = v.Length();
            if (!(distance > 1e-3f)) continue;
            var flat = Horizontal(v);
            var around = MathF.Asin(MathF.Min(0.9f, radii[i] / distance));
            var x = flat > 1e-4f ? DeathShotMath.AngleDelta(reference, DeathShotMath.Azimuth(v)) : 0f;
            var y = MathF.Atan2(v.Y, flat);
            minX = MathF.Min(minX, x - around);
            maxX = MathF.Max(maxX, x + around);
            minY = MathF.Min(minY, y - around);
            maxY = MathF.Max(maxY, y + around);
        }
        return float.IsFinite(minX);
    }

    /// <summary>
    /// When the box around both fighters is wider than the picture (the camera is on its way round to a new
    /// opponent, or a fighter is right beside it), the aim that centres the box would lose both: the player stays in
    /// the picture, and the other one comes in as the camera gets round.
    /// </summary>
    private void KeepPlayer(Vector3 eye, float lens, float aspect, ref float yawOffset, ref float pitch)
    {
        if (!Span(eye, 0, 2, out var minX, out var maxX, out var minY, out var maxY)) return;
        var tanV = MathF.Tan(lens * 0.5f);
        var limitX = MathF.Atan(SafeX * tanV * aspect);
        var limitY = MathF.Atan(SafeY * tanV);
        if (maxX - limitX <= minX + limitX) yawOffset = Math.Clamp(yawOffset, maxX - limitX, minX + limitX);
        else yawOffset = (minX + maxX) * 0.5f;
        if (maxY - limitY <= minY + limitY) pitch = Math.Clamp(pitch, maxY - limitY, minY + limitY);
        else pitch = maxY - limitY;
    }

    /// <summary>How much of the safe part of the picture the box around the fighters would fill from
    /// <paramref name="eye"/> (above 1: it does not fit).</summary>
    private float Overflow(Vector3 eye, float lens, float aspect)
    {
        Compose(eye, out _, out _, out var halfX, out var halfY);
        var tanV = MathF.Tan(lens * 0.5f);
        var tanH = tanV * aspect;
        var x = MathF.Tan(MathF.Min(halfX, 1.4f)) / (SafeX * tanH);
        var y = MathF.Tan(MathF.Min(halfY, 1.4f)) / MathF.Max(0.2f, MathF.Cos(MathF.Min(halfX, 1.2f))) / (SafeY * tanV);
        return MathF.Max(x, y);
    }

    /// <summary>The camera with <paramref name="amount"/> of a push-in, and whether both heads are still in the
    /// picture with it.</summary>
    private bool BeatFits(float amount, bool twoShot, float aspect, float viewAz, float viewPitch,
        out float distance, out float lens, out float az, out float pitch)
    {
        distance = MathF.Max(MinDistance * 0.8f, dist * (1f - BeatPush * amount));
        lens = fov * (1f - BeatZoom * amount);
        az = viewAz;
        pitch = viewPitch;
        var eye = EyeAt(distance);
        if (beatFocus != 0f && (twoShot || beatFocus < 0f))
        {
            // Lean the aim towards whoever the moment is about.
            var to = (beatFocus > 0f ? points[2] : points[0]) - eye;
            if (Horizontal(to) > 1e-3f)
            {
                var weight = BeatAim * amount;
                az = viewAz + DeathShotMath.AngleDelta(viewAz, DeathShotMath.Azimuth(to)) * weight;
                pitch = viewPitch + (MathF.Atan2(to.Y, Horizontal(to)) - viewPitch) * weight;
            }
        }
        var forward = DuelView.Direction(az, pitch);
        for (var i = 0; i < pointCount; i += 2)
        {
            var p = DuelView.Project(eye, forward, lens, aspect, points[i]);
            if (p.Z < 0.1f || MathF.Abs(p.X) > BeatHeadLimit || MathF.Abs(p.Y) > BeatHeadLimit) return false;
        }
        return true;
    }

    /// <summary>Moves <paramref name="eye"/> sideways out of an upright body (feet to head, with a radius).</summary>
    private static Vector3 PushOut(Vector3 eye, Vector3 feet, Vector3 head, float radius, Vector3 fallback)
    {
        if (eye.Y < MathF.Min(feet.Y, head.Y) - 0.2f || eye.Y > MathF.Max(feet.Y, head.Y) + 0.4f) return eye;
        var axis = head - feet;
        var t = axis.LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(eye - feet, axis) / axis.LengthSquared(), 0f, 1f) : 0f;
        var nearest = feet + axis * t;
        var out2 = new Vector3(eye.X - nearest.X, 0f, eye.Z - nearest.Z);
        var d = out2.Length();
        if (d >= radius) return eye;
        if (d < 1e-3f)
        {
            out2 = new Vector3(fallback.X, 0f, fallback.Z);
            if (out2.LengthSquared() < 1e-6f) out2 = Vector3.UnitX;
            out2 = Vector3.Normalize(out2);
            d = 0f;
        }
        else
        {
            out2 /= d;
        }
        return eye + out2 * (radius - d);
    }
}
