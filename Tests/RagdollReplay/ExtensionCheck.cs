// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using BepuPhysics;
using BepuPhysics.Constraints;
using BepuPhysics.CollisionDetection;
using CombatSimulator.Animation;
using CombatSimulator.Animation.Recording;
using BepuSimulation = BepuPhysics.Simulation;

namespace RagdollReplay;

/// <summary>Hooks the replay driver calls, for checks that need to reach the controller while it runs.</summary>
internal sealed class ReplayHooks
{
    public Action<RagdollController>? BeforeActivate;
    public Action<RagdollController>? BeforeFrame;
    /// <summary>The recording the replay wrote (still on disk only when the run was asked to keep it).</summary>
    public string? Replayed;
}

/// <summary>
/// Stage A3b: the extension points (IRagdollStepExtension, IRagdollContactExtension) are called where the partial
/// methods were. Replays recordings with dummy extensions registered and checks:
///  1. passive extensions (registered before activation, and more after the simulation exists): the replay is still
///     bit-identical; each fixed step calls the step extension exactly once, before that step's Timestep; contact
///     extensions run only inside a Timestep, on the simulation being stepped; the context shows the controller's state;
///  2. a step extension that handles the step by calling ctx.StepPhysicsSimulation: every output is identical except
///     the "stepped externally" flag of the body states, which is set; a later step extension is never called;
///  3. a step extension that handles the step and does nothing: no Timestep runs at all; a later one is never called;
///  4. a contact extension that handles every manifold: a later one is never called, and the controller's own
///     contact configuration is skipped (the outputs differ).
/// Zero extensions is the plain replay (97/97).
/// </summary>
internal static class ExtensionCheck
{
    private const int FilesWanted = 6;

    public static int Run(string target)
    {
        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*" + RagdollRecordingFormat.FileExtension).OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : new[] { target };
        var failures = 0;
        var checkedFiles = 0;
        foreach (var file in files)
        {
            if (checkedFiles >= FilesWanted) break;
            var name = Path.GetFileName(file);
            List<string> problems;
            string summary;
            try
            {
                if (!CheckFile(file, out problems, out summary)) continue; // no fixed step in it: says nothing
            }
            catch (ReplayUnsupportedException)
            {
                continue;
            }
            catch (Exception ex)
            {
                problems = new List<string> { ex.GetType().Name + ": " + ex.Message };
                summary = string.Empty;
            }
            checkedFiles++;
            Console.WriteLine($"{name}: {(problems.Count == 0 ? "PASS" : "FAIL")}  {summary}");
            foreach (var p in problems) Console.WriteLine("    " + p);
            if (problems.Count > 0) failures++;
        }
        Console.WriteLine($"== extensions: {checkedFiles - failures} of {checkedFiles} recording(s) passed");
        return checkedFiles > 0 && failures == 0 ? 0 : 1;
    }

    private static bool CheckFile(string file, out List<string> problems, out string summary)
    {
        problems = new List<string>();
        var p = problems;

        // 1. Passive extensions: early (before Activate) and late (once the simulation exists).
        var early = new Probe("early");
        var earlyContact = new ContactProbe();
        var late = new Probe("late");
        var lateContact = new ContactProbe();
        var lateRegistered = false;
        var earlyContactsAtLate = 0;
        var hooks = new ReplayHooks
        {
            BeforeActivate = c =>
            {
                c.AddExtension(early);
                c.AddExtension(earlyContact);
                if (!ReferenceEquals(c.ExtensionContext, c.ExtensionContext)) p.Add("ExtensionContext is not one per controller");
            },
            BeforeFrame = c =>
            {
                if (!lateRegistered && c.ExtensionContext.Simulation != null && early.Steps > 0)
                {
                    lateRegistered = true;
                    earlyContactsAtLate = earlyContact.Calls;
                    c.AddExtension(late);
                    c.AddExtension(lateContact);
                }
            },
        };
        earlyContact.Timing = early;
        lateContact.Timing = early;
        var outcome = ReplayDriver.Run(file, false, false, out _, hooks);
        if (early.Steps == 0) { summary = string.Empty; return false; }
        if (!outcome.StartsWith("IDENTICAL (", StringComparison.Ordinal)) p.Add("passive extensions changed the replay: " + FirstLine(outcome));
        p.AddRange(early.Problems);
        p.AddRange(late.Problems);
        p.AddRange(earlyContact.Problems);
        p.AddRange(lateContact.Problems);
        if (early.Timesteps != early.Steps) p.Add($"passive: {early.Steps} step-extension calls but {early.Timesteps} Timesteps");
        if (earlyContact.Calls == 0) p.Add("passive: the contact extension registered before activation was never called");
        if (lateRegistered && late.Steps == 0) p.Add("passive: the step extension registered late was never called");
        if (lateRegistered && lateContact.Calls != earlyContact.Calls - earlyContactsAtLate)
            p.Add($"passive: the contact extension registered after the simulation existed saw {lateContact.Calls} manifolds, the early one {earlyContact.Calls - earlyContactsAtLate} since then");
        var passive = $"passive: {early.Steps} steps, {early.Timesteps} Timesteps, {earlyContact.Calls} contacts (late: {late.Steps} steps, {lateContact.Calls} contacts), " +
                      $"{early.Bones} bodies, {early.Definitions} definitions";

        // 2. Takeover that steps the world the same way.
        var takeover = new Probe("takeover") { Handle = true, StepWorld = true };
        var after = new Probe("after-takeover");
        hooks = new ReplayHooks { BeforeActivate = c => { c.AddExtension(takeover); c.AddExtension(after); } };
        ReplayDriver.Run(file, true, false, out _, hooks);
        var flagged = 0;
        if (hooks.Replayed == null) p.Add("takeover: no replayed recording");
        else
        {
            var difference = CompareIgnoringSteppedExternally(file, hooks.Replayed, out flagged);
            if (difference != null) p.Add("takeover: " + difference);
            Directory.Delete(Path.GetDirectoryName(hooks.Replayed)!, true);
        }
        p.AddRange(takeover.Problems);
        if (after.Steps != 0) p.Add($"takeover: the extension after the one that handled the step was called {after.Steps} times");
        if (takeover.Timesteps != takeover.Steps) p.Add($"takeover: {takeover.Steps} steps but {takeover.Timesteps} Timesteps");

        // 3. Takeover that does nothing: the controller must not step.
        var skip = new Probe("skip") { Handle = true };
        var afterSkip = new Probe("after-skip");
        hooks = new ReplayHooks { BeforeActivate = c => { c.AddExtension(skip); c.AddExtension(afterSkip); } };
        var skipOutcome = ReplayDriver.Run(file, false, false, out _, hooks);
        if (skip.Steps == 0) p.Add("skip: never called");
        if (skip.Timesteps != 0) p.Add($"skip: handled, yet {skip.Timesteps} Timesteps ran");
        if (afterSkip.Steps != 0) p.Add($"skip: the extension after the one that handled the step was called {afterSkip.Steps} times");

        // 4. A contact extension that handles every manifold.
        var timing = new Probe("contact-timing");
        var handling = new ContactProbe { Handle = true, Timing = timing };
        var afterHandling = new ContactProbe { Timing = timing };
        hooks = new ReplayHooks { BeforeActivate = c => { c.AddExtension(timing); c.AddExtension(handling); c.AddExtension(afterHandling); } };
        var contactOutcome = ReplayDriver.Run(file, false, false, out _, hooks);
        p.AddRange(handling.Problems);
        if (handling.Calls == 0) p.Add("contact handled: never called");
        if (afterHandling.Calls != 0) p.Add($"contact handled: the extension after the one that handled it was called {afterHandling.Calls} times");
        if (contactOutcome.StartsWith("IDENTICAL (", StringComparison.Ordinal)) p.Add("contact handled: the outputs did not change, so the controller's own configuration still ran");

        summary = passive + $"; takeover: {takeover.Steps} steps, {flagged} body-state records flagged, rest identical" +
                  $"; skip: {skip.Steps} steps, {skip.Timesteps} Timesteps ({FirstWord(skipOutcome)})" +
                  $"; contact handled: {handling.Calls} manifolds, later one {afterHandling.Calls} ({FirstWord(contactOutcome)})";
        return true;
    }

    private static string FirstLine(string text) => text.Split('\n')[0];
    private static string FirstWord(string text) => text.Split(' ', ':')[0];

    /// <summary>Every output record equal, except that the replay's body states say the step was taken
    /// externally.</summary>
    private static string? CompareIgnoringSteppedExternally(string original, string replayed, out int flagged)
    {
        flagged = 0;
        using var a = RagdollRecordingReader.Open(original);
        using var b = RagdollRecordingReader.Open(replayed);
        static RagdollRecord? Next(RagdollRecordingReader r)
        {
            while (r.Read() is { } record)
                if (RagdollRecordingFormat.IsOutput(record.Type)) return record;
            return null;
        }
        var n = 0;
        while (true)
        {
            var x = Next(a);
            var y = Next(b);
            if (x == null && y == null) return n == 0 ? "no output records" : null;
            n++;
            if (x == null || y == null) return $"output #{n}: one recording ends";
            if (x.Type != y.Type) return $"output #{n}: {x.Type} vs {y.Type}";
            if (x.Type != RecordType.BodyStates)
            {
                if (!x.ContentEquals(y)) return $"output #{n} ({x.Type}) differs";
                continue;
            }
            if (x.Values.Length != y.Values.Length) return $"output #{n}: body states of different length";
            // Tick 0 is the snapshot taken when the bodies were built, before any step: never flagged.
            if (y[3].AsBool != (y[0].AsInt > 0)) return $"output #{n}: body states of tick {y[0].AsInt} flagged {y[3].AsBool}";
            if (y[3].AsBool) flagged++;
            for (var i = 0; i < x.Values.Length; i++)
                if (i != 3 && !x.Values[i].Equals(y.Values[i])) return $"output #{n}: body states differ (tick {x[0].AsInt}, value {i})";
        }
    }

    /// <summary>A step extension that counts its calls and the Timesteps of the simulation it sees, and checks that
    /// each fixed step calls it once, before that step's Timestep.</summary>
    private sealed class Probe : IRagdollStepExtension
    {
        private readonly string name;
        private BepuSimulation? subscribed;
        private bool awaitingTimestep;
        public Probe(string name) { this.name = name; }

        public bool Handle;
        public bool StepWorld;
        public int Steps;
        public int Timesteps;
        public int Bones;
        public int Definitions;
        public volatile bool InTimestep;
        public BepuSimulation? Stepping;
        public readonly List<string> Problems = new();

        public void OnSubstep(RagdollExtensionContext ctx, float dt, ref bool handled)
        {
            var simulation = ctx.Simulation;
            if (simulation == null) { Fail("called without a simulation"); return; }
            if (!ReferenceEquals(simulation, subscribed) && simulation.Timestepper is DefaultTimestepper timestepper)
            {
                subscribed = simulation;
                timestepper.BeforeCollisionDetection += (_, _) =>
                {
                    if (!awaitingTimestep) Fail("a Timestep ran without the step extension called before it");
                    awaitingTimestep = false;
                    Timesteps++;
                    Stepping = simulation;
                    InTimestep = true;
                };
                timestepper.CollisionsDetected += (_, _) => InTimestep = false;
            }
            if (awaitingTimestep && !Handle) Fail("called twice without a Timestep in between");
            if (handled) Fail("called with handled already set");
            if (dt <= 0f) Fail($"dt {dt}");
            if (!ctx.IsActive) Fail("context says inactive during a step");
            if (ctx.BufferPool == null) Fail("no buffer pool");
            Bones = ctx.Bones.Count;
            if (Bones == 0) Fail("no bodies");
            else
            {
                var bone = ctx.Bones[0];
                if (string.IsNullOrEmpty(bone.Name) || !simulation.Bodies.BodyExists(bone.BodyHandle)) Fail("first body is not a live body");
            }
            if (Steps == 0)
            {
                Definitions = ctx.GetBoneDefinitionNames().Length;
                if (Definitions == 0) Fail("no bone definitions");
            }
            Steps++;
            awaitingTimestep = true;
            if (!Handle) return;
            handled = true;
            if (StepWorld) ctx.StepPhysicsSimulation(dt);
            else awaitingTimestep = false;
        }

        private void Fail(string what)
        {
            if (Problems.Count < 3) Problems.Add($"{name}: {what} (step {Steps})");
        }
    }

    /// <summary>A contact extension that counts its calls and checks they come inside a Timestep of the context's
    /// simulation.</summary>
    private sealed class ContactProbe : IRagdollContactExtension
    {
        private int calls;
        private int outside;
        private int wrongSimulation;
        public bool Handle;
        public Probe? Timing;
        public int Calls => Volatile.Read(ref calls);

        public List<string> Problems
        {
            get
            {
                var list = new List<string>();
                if (outside > 0) list.Add($"contact: {outside} calls outside a Timestep");
                if (wrongSimulation > 0) list.Add($"contact: {wrongSimulation} calls whose context shows another simulation");
                return list;
            }
        }

        public void ConfigureContact<TManifold>(RagdollExtensionContext ctx, CollidablePair pair, ref TManifold manifold,
            ref PairMaterialProperties material, ref bool handled) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            Interlocked.Increment(ref calls);
            if (Timing != null && Timing.Steps > 0)
            {
                if (!Timing.InTimestep) Interlocked.Increment(ref outside);
                else if (!ReferenceEquals(ctx.Simulation, Timing.Stepping)) Interlocked.Increment(ref wrongSimulation);
            }
            if (!Handle) return;
            material = new PairMaterialProperties(0.5f, 2f, new SpringSettings(30f, 1f));
            handled = true;
        }
    }
}
