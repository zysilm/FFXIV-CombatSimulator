using CombatSimulator.Animation;

// Plays synthetic deaths through the production RagdollTrigger at several frame rates and checks
// when each mode starts physics.
//
// The death: the character is in a 3 s looping stance when it dies; 0.2 s later the death animation
// (2.5 s) has taken over; when that runs out a looping lying pose follows. The body's middle stays
// at 1 m for the first 0.6 s of the death animation, falls to 0.2 m by 1.3 s, and stays there.

var failures = 0;
void Check(bool condition, string what)
{
    if (condition) return;
    failures++;
    Console.WriteLine($"   FAIL: {what}");
}

const float Blend = 0.2f, DeathLength = 2.5f, FallStart = 0.6f, FallEnd = 1.3f;

// Height of the body's middle, `into` seconds into the death animation. `drop` 0 is a death that never falls.
float Height(float into, float drop)
{
    var t = Math.Clamp((into - FallStart) / (FallEnd - FallStart), 0f, 1f);
    return 1f - drop * (t * t * (3f - 2f * t));
}

// Seconds after death at which the trigger fires.
float Run(RagdollTriggerMode mode, float fps, float fixedDelay = 1f, float drop = 0.8f, float stanceAt = 2.1f,
    bool animationReadable = true, bool heightReadable = true, float cutShortAt = float.MaxValue)
{
    var trigger = new RagdollTrigger(mode, fixedDelay);
    var dt = 1f / fps;
    for (var frame = 1; frame < 40 * fps; frame++)
    {
        var elapsed = frame * dt;
        var into = elapsed - Blend;
        RagdollAnimationSample? animation = null;
        if (animationReadable)
        {
            if (into < 0f) animation = new RagdollAnimationSample(1, (stanceAt + elapsed) % 3f, 3f);
            else if (into >= cutShortAt) animation = new RagdollAnimationSample(4, into - cutShortAt, 6f);
            else if (into < DeathLength) animation = new RagdollAnimationSample(2, into, DeathLength);
            else animation = new RagdollAnimationSample(3, (into - DeathLength) % 2f, 2f);
        }
        float? height = heightReadable ? Height(MathF.Max(0f, into), drop) : null;
        if (trigger.Update(dt, elapsed, animation, height))
            return elapsed;
    }
    return float.PositiveInfinity;
}

var deathEnds = Blend + DeathLength;
foreach (var fps in new[] { 30f, 60f, 144f })
{
    var frame = 1f / fps;
    Console.WriteLine($"{fps:F0} frames a second:");

    var fixedAt = Run(RagdollTriggerMode.FixedDelay, fps, fixedDelay: 1f);
    Check(fixedAt >= 1f - 1e-4f && fixedAt < 1f + frame + 1e-4f, $"fixed delay of 1 s fired at {fixedAt:F3}");
    Check(Run(RagdollTriggerMode.FixedDelay, fps, fixedDelay: 0f) <= frame + 1e-4f, "a delay of 0 must fire on the first frame");
    Check(MathF.Abs(Run(RagdollTriggerMode.FixedDelay, fps, fixedDelay: 30f) - 30f) < frame + 1e-3f, "a delay of 30 s must be honoured");

    var last = Run(RagdollTriggerMode.AnimationLastFrame, fps);
    Console.WriteLine($"   one frame before the end: {last:F3} s (animation ends at {deathEnds:F3})");
    Check(last < deathEnds, "fired after the animation had ended");
    Check(last >= deathEnds - (1f / 30f + 2f * frame) - 1e-4f, $"fired {deathEnds - last:F3} s before the end, more than a frame early");

    var ninety = Run(RagdollTriggerMode.AnimationNinetyPercent, fps);
    Console.WriteLine($"   at 90%: {ninety:F3} s (90% is at {Blend + 0.9f * DeathLength:F3})");
    Check(ninety >= Blend + 0.9f * DeathLength - 1e-4f && ninety < Blend + 0.9f * DeathLength + frame + 1e-4f, "did not fire at 90%");

    var falling = Run(RagdollTriggerMode.FallingFastest, fps);
    var fastestAt = Blend + (FallStart + FallEnd) * 0.5f;
    Console.WriteLine($"   falls fastest: {falling:F3} s (fall from {Blend + FallStart:F2} to {Blend + FallEnd:F2}, fastest at {fastestAt:F2})");
    Check(falling > Blend + FallStart && falling <= fastestAt + 0.1f, "did not fire while the body was speeding up");

    // A slow fall never reaches the fast-fall speed; it fires just past its fastest point.
    var slow = Run(RagdollTriggerMode.FallingFastest, fps, drop: 0.35f);
    Console.WriteLine($"   falls fastest, slow fall: {slow:F3} s");
    Check(slow > fastestAt - 0.05f && slow < Blend + FallEnd + 0.05f, "a slow fall did not fire just after its fastest point");

    var landed = Run(RagdollTriggerMode.Landed, fps);
    Console.WriteLine($"   landed: {landed:F3} s (down at {Blend + FallEnd:F2})");
    Check(landed >= Blend + FallEnd - 0.1f && landed < Blend + FallEnd + 0.35f, "did not fire shortly after landing");

    // A death that never falls (dying seated): the animation running out ends the wait.
    foreach (var mode in new[] { RagdollTriggerMode.FallingFastest, RagdollTriggerMode.Landed })
    {
        var seated = Run(mode, fps, drop: 0f);
        Check(seated >= deathEnds - 1e-4f && seated < deathEnds + 2f * frame + 1e-4f, $"{mode}: a death without a fall fired at {seated:F3}, not when the animation ended");
    }

    // The stance the character died in is one frame from the end of its loop: that is not the death ending.
    var nearLoopEnd = Run(RagdollTriggerMode.AnimationLastFrame, fps, stanceAt: 2.95f - frame);
    Check(nearLoopEnd > deathEnds - 0.2f, $"the stance's loop end was taken for the death's end ({nearLoopEnd:F3})");
    var nearLoopEndNinety = Run(RagdollTriggerMode.AnimationNinetyPercent, fps, stanceAt: 2.8f);
    Check(nearLoopEndNinety > 2f, $"the stance's loop was taken for the death at 90% ({nearLoopEndNinety:F3})");

    // The death animation is replaced half way: that is not its end, and the new one is followed instead.
    var cutShort = Run(RagdollTriggerMode.AnimationLastFrame, fps, cutShortAt: 1.2f);
    Check(cutShort > Blend + 1.2f + 5f, $"an animation cut short was taken to have ended ({cutShort:F3})");

    // Nothing readable: the clock decides, at the fixed delay but not before one second.
    Check(MathF.Abs(Run(RagdollTriggerMode.AnimationLastFrame, fps, fixedDelay: 3f, animationReadable: false) - 3f) < frame + 1e-3f, "unreadable animation: did not fall back on the delay");
    Check(MathF.Abs(Run(RagdollTriggerMode.Landed, fps, fixedDelay: 0f, animationReadable: false, heightReadable: false) - 1f) < frame + 1e-3f, "nothing readable: did not fall back after a second");
    // Height unreadable but the animation is: the animation's end still starts it.
    var blindFall = Run(RagdollTriggerMode.FallingFastest, fps, heightReadable: false);
    Check(blindFall >= deathEnds - 1e-4f && blindFall < deathEnds + 2f * frame + 1e-4f, $"height unreadable: fired at {blindFall:F3}");
}

// Whatever happens, physics has started 30 s after death.
foreach (var mode in Enum.GetValues<RagdollTriggerMode>())
{
    var trigger = new RagdollTrigger(mode, 99f);
    var at = float.PositiveInfinity;
    for (var frame = 1; frame < 4000 && float.IsInfinity(at); frame++)
        if (trigger.Update(1f / 60f, frame / 60f, new RagdollAnimationSample(9, 0.5f, 100f), 1f)) at = frame / 60f;
    Check(MathF.Abs(at - RagdollTrigger.MaximumWait) < 0.02f, $"{mode}: started at {at:F2} s, not at the 30 s limit");
}
Check(RagdollTrigger.Labels.Length == Enum.GetValues<RagdollTriggerMode>().Length, "one label per mode");
Check(RagdollTrigger.ClampMode(-4) == RagdollTriggerMode.FixedDelay && RagdollTrigger.ClampMode(99) == RagdollTriggerMode.Landed, "mode clamp");
Check(!new RagdollTrigger(RagdollTriggerMode.FixedDelay, 1f).Automatic && new RagdollTrigger(RagdollTriggerMode.Landed, 1f).Automatic, "which modes are automatic");

Console.WriteLine(failures == 0 ? "RagdollTrigger: all checks passed." : $"RagdollTrigger: {failures} check(s) FAILED.");
return failures;
