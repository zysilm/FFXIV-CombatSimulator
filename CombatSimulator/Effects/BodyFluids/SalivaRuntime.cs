// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Effects.BodyFluids.Simulation;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Effects.BodyFluids;

/// <summary>
/// Production bounded saliva ownership graph. The controller supplies a current final pose and
/// verified lip landmark; this runtime owns neither pose capture nor render hooks. Inventories
/// change owner only after acceptance. Unknown contact/budget results freeze the affected liquid.
/// Slow film/bead/thread parameters are reduced calibration values, not physiological measurements.
/// </summary>
internal sealed partial class SalivaRuntime
{
    private const double Density = 1000, SurfaceTension = 0.055;
    private double viscosity = 0.18;
    private const double InventoryLimit = 20e-6, BeadLimit = 0.02e-6;
    private static readonly Vector3 Gravity = new(0, -9.81f, 0);
    private readonly CharacterFluidSurface surface;
    private readonly Configuration config;
    private readonly SurfaceFilmRuntime[] films = new SurfaceFilmRuntime[4];
    private readonly Bead[] beads = new Bead[16];
    private readonly Thread[] threads = new Thread[2];
    private readonly Drop[] drops = new Drop[32];
    private readonly GroundFilmRuntime ground = new();
    private readonly GroundSupportProbe groundProbe;
    private double reservoir;
    private double acceptedSourceSeconds;
    private int sourceBead = -1;
    private long runoffTransitions;
    private double coalescedVolume;
    private double wettingTrailTransfer, actualCoatVolume;
    private int lastRuntimeSubsteps;
    private double lastRuntimeStepSeconds;
    private long microstepCount;
    private uint generation;
    private int terrainBudget, surfaceBudget;
    public bool Emitting { get; set; }
    /// <summary>Temporary visibility experiment: more supply and exaggerated rendered thickness, never saved to settings.</summary>
    public bool LargeVisibilityPreview { get; set; }
    public double EmittedVolume { get; private set; }
    public double RetiredVolume { get; private set; }
    public double DeferredSeconds { get; private set; }
    public string Status { get; private set; } = "Waiting for verified lip";
    public double TotalVolume
    {
        get
        {
            var total = reservoir + ground.Volume;
            foreach (var film in films) total += film.Volume;
            foreach (var bead in beads) total += bead.Volume;
            foreach (var thread in threads) total += thread.Model.TotalVolume;
            foreach (var drop in drops) total += drop.Volume;
            return total;
        }
    }
    public double ConservationError => EmittedVolume - RetiredVolume - TotalVolume;

    private struct Bead
    {
        public double Volume, RunoffDistance;
        public int Film;
        public FluidSurfaceAnchor Anchor;
        public bool WalkBlocked;
    }
    private struct Drop { public double Volume; public Vector3 Position, Velocity; }
    private sealed class Thread
    {
        public readonly ViscoelasticFilament Model = new(24);
        public readonly Vector3[] Previous = new Vector3[24], RenderPositions = new Vector3[24];
        public readonly float[] RenderRadii = new float[24];
        public FluidSurfaceAnchor Anchor;
        public int Bead = -1;
        public Vector3 PreviousTerminalCenter;
        public bool Attached;
        public bool TipAttached;
        public FluidSurfaceAnchor TipAnchor;
        public bool PendingContact;
        public int ContactSegment;
        public double Deferred;
        public double RequestedSeconds, SimulatedSeconds;
        public long AcceptedProposals, PendingSteps, SolverNoProgress;
    }

    public SalivaRuntime(CharacterFluidSurface surface, Configuration config)
    {
        this.surface = surface; this.config = config;
        groundProbe = ProbeGroundFilm;
        for (var i = 0; i < films.Length; i++) films[i] = new(surface);
        for (var i = 0; i < threads.Length; i++) threads[i] = new();
    }

    /// <summary>Call once per valid final pose. Stale pose time is reported, never caught up through a new pose.</summary>
    public void Advance(double seconds)
    {
        DeferredSeconds = 0;
        lastRuntimeSubsteps = 0; lastRuntimeStepSeconds = 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        if (seconds < 1e-7)
        { DeferredSeconds = seconds; Status = "Paused: requested runtime time below numerical resolution"; return; }
        config.ClampBodyFluids();
        viscosity = config.BodyFluidViscosityPaSeconds;
        foreach (var film in films) film.SetMaterial(viscosity);
        foreach (var thread in threads) thread.Model.SetMaterial(viscosity, config.BodyFluidFilamentRelaxation);
        ground.SetMaterial(viscosity);
        if (seconds > 0.15)
        { DeferredSeconds = seconds; Status = "Paused: source pose gap exceeds 150 ms"; return; }
        // The anatomical selector already requires a supported, outward lower-lip triangle.
        // Manual marker confirmation is diagnostic, not a runtime emission prerequisite:
        // DLL reloads and shape replacements must not silently disable a valid source.
        if (!surface.TryGetMouthAnchor(out var lip))
        { DeferredSeconds = seconds; Status = "Paused: no current anatomical lip anchor; " + surface.LipStatus; return; }
        if (!surface.TryEvaluate(lip, out var mouth))
        { DeferredSeconds = seconds; Status = "Paused: lip surface evaluation unavailable/budget"; return; }
        if (generation != 0 && generation != lip.Generation) RetireInventory();
        generation = lip.Generation;
        var sourceFilm = FindFilm(lip);
        if (sourceFilm < 0) { DeferredSeconds = seconds; Status = "Paused: verified local lip patch unavailable/budget"; return; }
        // Controller fixed time is a float, slightly larger than exact double 1/60.
        // Equal subdivisions consume the full admitted time once; a tolerance in
        // step-count selection must never create a nanosecond collision-only step.
        var admittedTime = Math.Min(seconds, 4.0 / 60);
        var subdivisions = Math.Clamp((int)Math.Ceiling(admittedTime * 60 - 1e-6), 1, 4);
        var dt = admittedTime / subdivisions;
        var elapsed = 0.0;
        for (var step = 0; step < subdivisions; step++)
        {
            lastRuntimeSubsteps++; lastRuntimeStepSeconds = dt;
            if (dt < 1e-7) microstepCount++;
            terrainBudget = 32; surfaceBudget = 40;
            ground.BeginStep();
            if (Emitting)
            {
                var requested = config.BodyFluidFlowMlPerSecond * (LargeVisibilityPreview ? 20 : 1) * 1e-6 * dt;
                var accepted = Math.Min(requested, Math.Max(0, InventoryLimit - TotalVolume));
                reservoir += accepted; EmittedVolume += accepted;
                acceptedSourceSeconds += requested > 0 ? dt * accepted / requested : 0;
            }
            // Mouth supply accumulates in a true lip cap. The skin film is a receiving/
            // wetting owner, rather than the default destination for the entire source flux.
            if (sourceBead < 0)
            {
                for (var bead = 0; bead < beads.Length; bead++)
                    if (beads[bead].Volume <= 0 && !BeadSlotReserved(bead))
                    { beads[bead] = default; sourceBead = bead; break; }
            }
            if (sourceBead >= 0)
            {
                ref var cap = ref beads[sourceBead];
                cap.Film = sourceFilm; cap.Anchor = lip;
                var accepted = Math.Min(reservoir, Math.Max(0, BeadLimit - cap.Volume));
                cap.Volume += accepted; reservoir -= accepted;
                if (films[sourceFilm].TryGetCell(lip, out var cell))
                {
                    var sample = films[sourceFilm].GetCell(cell);
                    BeadDimensions(cap.Volume, out _, out _, out var footprintRadius);
                    var contactArea = Math.Min(sample.Geometry.Area, Math.PI * footprintRadius * footprintRadius);
                    // Maintain only a local 25µm contact wetting layer. This is a bounded
                    // artistic wetting target, not a fixed proportion of incoming flow.
                    var wettingDeficit = Math.Max(0, contactArea * 0.000025 - sample.Volume);
                    var wetted = films[sourceFilm].AddVolume(lip, Math.Min(cap.Volume, wettingDeficit));
                    cap.Volume -= wetted;
                }
            }
            for (var i = 0; i < films.Length; i++)
            {
                if (films[i].CellCount == 0) continue;
                if (!films[i].Advance(dt, Gravity)) { DeferredSeconds = Math.Max(DeferredSeconds, dt); continue; }
                DeferredSeconds = Math.Max(DeferredSeconds, films[i].DeferredSeconds);
                PoolFilmBeads(i);
            }
            CoalesceContactCaps();
            StepBeads(dt);
            StepThreads(dt);
            StepDrops(dt);
            if (ground.Volume > 0)
            {
                var spread = ground.Advance(dt, Gravity, groundProbe);
                DeferredSeconds = Math.Max(DeferredSeconds, spread.DeferredSeconds);
            }
            elapsed += dt;
        }
        DeferredSeconds = Math.Max(DeferredSeconds, Math.Max(0, seconds - elapsed));
        Status = $"Conservative saliva: cells={CountCells()} volume={TotalVolume * 1e6:F4}ml ledgerError={ConservationError:E2}m³ deferred={DeferredSeconds:F4}s; " +
            $"lastRuntimeSubsteps={lastRuntimeSubsteps},lastRuntimeStepSeconds={lastRuntimeStepSeconds:E9},microstepCount={microstepCount}; groundCells={ground.CellCount},terrainPending={ground.PendingProbes}";
    }

    private int CountCells() { var count = 0; foreach (var film in films) count += film.CellCount; return count; }
    private int FindFilm(FluidSurfaceAnchor anchor)
    {
        for (var i = 0; i < films.Length; i++) if (films[i].TryGetCell(anchor, out _)) return i;
        for (var i = 0; i < films.Length; i++)
            if (films[i].Volume <= 0 && films[i].TryBind(anchor)) return i;
        return -1;
    }

    private double AddBead(int film, FluidSurfaceAnchor anchor, double requested)
    {
        var free = -1;
        for (var i = 0; i < beads.Length; i++)
        {
            if (beads[i].Volume <= 0) { if (free < 0 && i != sourceBead && !BeadSlotReserved(i)) free = i; continue; }
            if (beads[i].Film == film && beads[i].Anchor.Triangle == anchor.Triangle && beads[i].Anchor.Generation == anchor.Generation)
            {
                var accepted = Math.Min(requested, Math.Max(0, BeadLimit - beads[i].Volume));
                beads[i].Volume += accepted; return accepted;
            }
        }
        if (free < 0) return 0;
        var volume = Math.Min(requested, BeadLimit);
        beads[free] = new() { Film = film, Anchor = anchor, Volume = volume };
        return volume;
    }

    private void PoolFilmBeads(int film)
    {
        for (var cell = 0; cell < films[film].CellCount; cell++)
        {
            var sample = films[film].GetCell(cell);
            if (sample.Thickness <= 0.0008) continue;
            var excess = sample.Volume - sample.Geometry.Area * 0.0006;
            var received = AddBead(film, films[film].GetAnchor(cell), excess);
            if (received > 0) films[film].TakeVolume(cell, received);
        }
    }

    private void StepBeads(double dt)
    {
        for (var i = 0; i < beads.Length; i++)
        {
            ref var bead = ref beads[i];
            if (bead.Volume <= 0 || !surface.TryEvaluate(bead.Anchor, out var sample)) continue;
            BeadDimensions(bead.Volume, out _, out _, out var footprintRadius);
            var tangentGravity = Gravity - sample.Normal * Vector3.Dot(Gravity, sample.Normal);
            var drive = Density * bead.Volume * tangentGravity.Length();
            var retention = SurfaceTension * footprintRadius * 2 * 0.025;
            var outward = Density * bead.Volume * Math.Max(0, Vector3.Dot(Gravity, sample.Normal));
            var adhesion = SurfaceTension * Math.PI * footprintRadius * 2 * 0.15;
            if (outward > adhesion)
            {
                if (!BeadSlotReserved(i) && TryStartThread(i, sample)) continue;
                // Unknown initial contact or a full thread pool retains the released-cap
                // inventory here; neither condition proves a safe free-drop transition.
                continue;
            }
            if (BeadSlotReserved(i)) continue; // An owned neck attachment keeps its cap/slot until release or recovery.
            if (drive <= retention || tangentGravity.LengthSquared() < 1e-12f) continue;
            // Contact-layer viscous drag scales with footprint area/thickness, so speed
            // changes with volume, orientation and pinning load instead of a global slider.
            var drag = 3 * viscosity * Math.PI * footprintRadius * footprintRadius / 0.0001;
            var speed = (drive - retention) / Math.Max(1e-9, drag);
            var displacement = Vector3.Normalize(tangentGravity) * (float)(speed * dt);
            var candidate = bead.Anchor;
            bead.WalkBlocked = false;
            if (surfaceBudget <= 0) { bead.WalkBlocked = true; continue; }
            surfaceBudget--;
            var completed = surface.TryWalk(ref candidate, displacement, out var walked);
            bead.WalkBlocked = !completed;
            // A false walk can still reach a verified edge through known triangles.
            // Commit only that known travel; the unknown remainder never becomes an exit.
            if (!surface.TryEvaluate(candidate, out walked)) continue;
            var travel = Vector3.Distance(sample.Position, walked.Position);
            if (!float.IsFinite(travel) || travel <= 1e-7f || Vector3.Dot(Gravity, walked.Position - sample.Position) < -1e-8f) continue;
            bead.Anchor = candidate;
            bead.RunoffDistance += travel;
            if (i == sourceBead)
            {
                // Role transition only: inventory/slot/possible thread references stay
                // with this cap. Subsequent supply creates another cap at the mouth.
                sourceBead = -1;
                runoffTransitions++;
            }
            var patch = FindFilm(candidate);
            if (patch >= 0)
            {
                bead.Film = patch;
                if (films[patch].TryGetCell(candidate, out var cell))
                {
                    const double coatThickness = 0.000025;
                    var sampleCell = films[patch].GetCell(cell);
                    // A translating contact footprint sweeps width * verified travel.
                    // Only its thin coat is transferred, bounded by the receiving cell's
                    // current deficit. Existing liquid is never coated a second time.
                    var sweptCoat = 2 * footprintRadius * travel * coatThickness;
                    var deficit = Math.Max(0, sampleCell.Geometry.Area * coatThickness - sampleCell.Volume);
                    var coat = Math.Min(bead.Volume, Math.Min(sweptCoat, deficit));
                    actualCoatVolume += coat;
                    var trail = films[patch].AddVolume(candidate, coat);
                    bead.Volume -= trail;
                    wettingTrailTransfer += trail;
                }
            }
        }
    }

    private bool HasThread(int bead)
    { foreach (var thread in threads) if (thread.Model.TotalVolume > 0 && thread.Attached && thread.Bead == bead &&
        (thread.Model.TerminalConnectedToSource || thread.TipAttached)) return true; return false; }

    private bool BeadSlotReserved(int bead)
    {
        foreach (var thread in threads)
            if (thread.Model.TotalVolume > 0 && thread.Attached && thread.Bead == bead) return true;
        return false;
    }

    private void CoalesceContactCaps()
    {
        // Fixed 16-slot pair bound. Spatial footprint overlap alone cannot prove
        // a shared material surface: a successful local walk must also connect it.
        for (var i = 0; i < beads.Length; i++)
        {
            if (beads[i].Volume <= 0 || BeadSlotReserved(i)) continue;
            for (var j = i + 1; j < beads.Length; j++)
            {
                if (beads[i].Volume <= 0) break;
                if (beads[j].Volume <= 0 || BeadSlotReserved(j) || beads[i].Film != beads[j].Film) continue;
                var recipient = i == sourceBead ? j : j == sourceBead ? i :
                    beads[i].Volume >= beads[j].Volume ? i : j;
                var donor = recipient == i ? j : i;
                // Keep the established runoff cap at its own anchor. An empty source
                // cap keeps its source role, allowing supply through this real overlap.
                if (beads[recipient].RunoffDistance <= 0 || beads[recipient].Volume >= BeadLimit) continue;
                if (!surface.TryEvaluate(beads[recipient].Anchor, out var target) ||
                    !surface.TryEvaluate(beads[donor].Anchor, out var origin)) continue;
                if (Vector3.Dot(target.Normal, origin.Normal) < 0.95f) continue;
                BeadDimensions(beads[recipient].Volume, out _, out _, out var targetFootprint);
                BeadDimensions(beads[donor].Volume, out _, out _, out var donorFootprint);
                var offset = target.Position - origin.Position;
                if (offset.Length() >= targetFootprint + donorFootprint ||
                    Math.Abs(Vector3.Dot(offset, origin.Normal)) > 0.0001f ||
                    !films[beads[i].Film].TryGetCell(beads[i].Anchor, out _) ||
                    !films[beads[j].Film].TryGetCell(beads[j].Anchor, out _)) continue;
                if (surfaceBudget <= 0) return;
                surfaceBudget--;
                var connecting = beads[donor].Anchor;
                if (!surface.TryWalk(ref connecting, offset, out var reached) ||
                    Vector3.DistanceSquared(reached.Position, target.Position) > 1e-8f) continue;
                var accepted = Math.Min(beads[donor].Volume, BeadLimit - beads[recipient].Volume);
                beads[recipient].Volume += accepted;
                beads[donor].Volume -= accepted;
                coalescedVolume += accepted;
            }
        }
    }
    private bool TryStartThread(int bead, FluidSurfaceSample sample)
    {
        if (beads[bead].Volume < 0.003e-6) return false;
        foreach (var thread in threads)
        {
            if (thread.Model.TotalVolume > 0) continue;
            var direction = Vector3.Normalize(Gravity);
            var neckRadius = Math.Clamp(Radius(beads[bead].Volume) * 0.1, 0.00006, 0.0002);
            // Probe the initial bridge sweep before seeding. A close chin/neck transfers
            // directly into its film; an unknown surface budget cannot become a free thread.
            var start = sample.Position + sample.Normal * (float)(neckRadius + 0.00008);
            var end = start + direction * 0.005f;
            if (surfaceBudget <= 0) return false;
            surfaceBudget--;
            if (surface.TryContact(start, end, (float)neckRadius, out var contact, out _, out _))
            {
                var received = Deposit(contact, beads[bead].Volume);
                beads[bead].Volume -= received;
                return received > 0;
            }
            if (surface.BudgetExhausted) return false;
            // The geometric neck is a small part of the released cap; the remaining
            // volume becomes the real terminal gravity load owned by this same model.
            var accepted = thread.Model.InitializePendant(start, end, sample.Velocity, beads[bead].Volume, neckRadius);
            if (accepted <= 0) return false;
            beads[bead].Volume -= accepted;
            thread.Anchor = beads[bead].Anchor; thread.Bead = bead; thread.Attached = true; thread.TipAttached = false; thread.Deferred = 0;
            thread.RequestedSeconds = thread.SimulatedSeconds = 0; thread.AcceptedProposals = thread.PendingSteps = thread.SolverNoProgress = 0;
            thread.PendingContact = false; thread.ContactSegment = 0;
            thread.Model.SetEndpoint(false, true, start, sample.Velocity);
            return true;
        }
        return false;
    }

    private double Deposit(FluidSurfaceAnchor anchor, double requested)
    {
        var film = FindFilm(anchor);
        if (film < 0) return 0;
        var accepted = films[film].AddVolume(anchor, requested);
        if (accepted < requested) accepted += AddBead(film, anchor, requested - accepted);
        return accepted;
    }

    private static double Radius(double volume) => Math.Cbrt(Math.Max(0, volume) * 3 / (4 * Math.PI));
    private static void BeadDimensions(double volume, out double sphereRadius, out double height, out double footprintRadius)
    {
        // One shallow spherical cap, entirely above its material tangent plane.
        // V=pi R³(1-cos(theta))²(2+cos(theta))/3, theta is an artistic 0.9-rad wetting angle.
        const double contactAngle = 0.9;
        var cosine = Math.Cos(contactAngle);
        sphereRadius = Math.Cbrt(Math.Max(0, volume) * 3 / (Math.PI * (1 - cosine) * (1 - cosine) * (2 + cosine)));
        height = sphereRadius * (1 - cosine);
        footprintRadius = sphereRadius * Math.Sin(contactAngle);
    }
    private bool TryAddDrop(Vector3 position, Vector3 velocity, double volume)
    {
        if (volume <= 0) return true;
        for (var i = 0; i < drops.Length; i++)
            if (drops[i].Volume <= 0) { drops[i] = new() { Position = position, Velocity = velocity, Volume = volume }; return true; }
        return false;
    }

    /// <summary>Explicit lifecycle retirement. Lifetime timers never delete simulation liquid.</summary>
    private void RetireInventory()
    {
        RetiredVolume += TotalVolume;
        reservoir = 0; generation = 0; sourceBead = -1; acceptedSourceSeconds = 0; runoffTransitions = 0; coalescedVolume = 0;
        wettingTrailTransfer = actualCoatVolume = 0;
        lastRuntimeSubsteps = 0; lastRuntimeStepSeconds = 0; microstepCount = 0;
        foreach (var film in films) film.Clear();
        Array.Clear(beads); Array.Clear(drops);
        foreach (var thread in threads)
        { thread.Model.Clear(); thread.Attached = thread.TipAttached = false; thread.Bead = -1; thread.Deferred = 0; thread.PendingContact = false; thread.ContactSegment = 0; }
        ground.Clear();
    }

    public void Clear()
    { RetireInventory(); Emitting = false; DeferredSeconds = 0; Status = "Saliva cleared; inventory explicitly retired"; }
}
