// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Core;
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
    private const double BeadLimit = 0.02e-6;
    private static readonly Vector3 Gravity = new(0, -9.81f, 0);
    private readonly CharacterFluidSurface surface;
    private readonly Configuration config;
    private const int FilmsPerOutlet = 4;
    private SurfaceFilmRuntime[] films = new SurfaceFilmRuntime[FilmsPerOutlet];
    private int[] filmOutlets = new int[FilmsPerOutlet];
    private const int BeadCapacity = 32;
    private readonly Bead[] beads = new Bead[BeadCapacity];
    private readonly Thread[] threads = new Thread[4];
    private readonly Drop[] drops = new Drop[64];
    private long ownerSequence;
    private int dropStepOrder;
    private readonly GroundFilmRuntime?[] grounds = new GroundFilmRuntime[OutletCount];
    private readonly GroundSupportProbe groundProbe;
    private const int OutletCount = 9;
    private readonly OutletState[] outletStates = new OutletState[OutletCount];
    private int outletOrder;
    private double reservoir { get { double total = 0; foreach (var state in outletStates) total += state.Reservoir; return total; } }
    private int sourceBead => outletStates[0].Bead;
    private double acceptedSourceSeconds => outletStates[0].AcceptedSeconds;
    private long runoffTransitions;
    private double coalescedVolume;
    private double wettingTrailTransfer, actualCoatVolume;
    private int lastRuntimeSubsteps;
    private double lastRuntimeStepSeconds;
    private long microstepCount;
    private long skinGenerationChanges;
    private double retiredSkinFilm;
    private uint generation;
    private int terrainBudget, surfaceBudget;
    private int rivuletSampleBudget, beadStepOrder;
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
            var total = reservoir + GroundVolume;
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
        public int Outlet;
        public long Created;
        public double Volume, RunoffDistance;
        public int Film;
        public FluidSurfaceAnchor Anchor;
        public bool WalkBlocked;
        public bool HasWorldSample;
        public FluidSurfaceSample LastWorldSample;
    }
    private struct Drop { public int Outlet; public long Created; public double Volume, SkinCooldown; public Vector3 Position, Velocity; }
    private sealed class Thread
    {
        public int Outlet;
        public long Created;
        // Eight material nodes keep the initial 5mm bridge away from tiny-segment
        // stiffness; render sampling remains smooth and topology can still coarsen.
        public readonly ViscoelasticFilament Model = new(8);
        public readonly Vector3[] Previous = new Vector3[24], RenderPositions = new Vector3[24];
        public readonly float[] RenderRadii = new float[24];
        public FluidSurfaceAnchor Anchor;
        public int Bead = -1;
        public Vector3 PreviousTerminalCenter;
        public bool Attached;
        public bool TipAttached;
        public FluidSurfaceAnchor TipAnchor = default;
        public bool PendingContact;
        public int ContactSegment;
        public double Deferred;
        public double TimeDebt;
        public double RequestedSeconds, SimulatedSeconds;
        public long AcceptedProposals, PendingSteps, SolverNoProgress;
    }

    public SalivaRuntime(CharacterFluidSurface surface, Configuration config)
    {
        this.surface = surface; this.config = config;
        groundProbe = ProbeGroundFilm;
        for (var i = 0; i < outletStates.Length; i++) outletStates[i] = new() { LastSettings = new() { Kind = (BodyFluidOutletKind)i, SettingsVersion = 1 } };
        for (var i = 0; i < films.Length; i++)
        { films[i] = new(surface); filmOutlets[i] = i / FilmsPerOutlet; }
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
        if (seconds > 0.15)
        { DeferredSeconds = seconds; Status = "Paused: source pose gap exceeds 150 ms"; return; }
        // The anatomical selector already requires a supported, outward lower-lip triangle.
        // Manual marker confirmation is diagnostic, not a runtime emission prerequisite:
        // DLL reloads and shape replacements must not silently disable a valid source.
        if (generation != 0 && generation != surface.Generation) ReleaseChangedSkinGeneration();
        generation = surface.Generation;
        PrepareOutlets();
        for (int i = 0; i < films.Length; i++) films[i].SetMaterial(GetOutletSettings((BodyFluidOutletKind)filmOutlets[i]).ViscosityPaSeconds);
        bool sourceAvailable = false;
        foreach (var state in outletStates) sourceAvailable |= state.Available;
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
            rivuletSampleBudget = 8;
            foreach (var ground in grounds) ground?.BeginStep();
            SupplyOutlets(dt);
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
            for (int order = 0; order < grounds.Length; order++)
            {
                int i = (outletOrder + order) % grounds.Length;
                var ground = grounds[i];
                if (ground == null || ground.Volume <= 0) continue;
                ground.SetMaterial(GetOutletSettings((BodyFluidOutletKind)i).ViscosityPaSeconds);
                var spread = ground.Advance(dt, Gravity, groundProbe);
                DeferredSeconds = Math.Max(DeferredSeconds, spread.DeferredSeconds);
            }
            elapsed += dt;
        }
        DeferredSeconds = Math.Max(DeferredSeconds, Math.Max(0, seconds - elapsed));
        double threadDebt = 0;
        foreach (var thread in threads) threadDebt += thread.TimeDebt;
        Status = $"Conservative saliva: cells={CountCells()} volume={TotalVolume * 1e6:F4}ml ledgerError={ConservationError:E2}m³ deferred={DeferredSeconds:F4}s; " +
            $"lastRuntimeSubsteps={lastRuntimeSubsteps},lastRuntimeStepSeconds={lastRuntimeStepSeconds:E9},microstepCount={microstepCount}; groundCells={GroundCellCount}; " +
            $"skinGenerationChanges={skinGenerationChanges},retiredInvalidSkinFilm={retiredSkinFilm * 1e6:F5}ml,threadTimeDebt={threadDebt:F5}s,sourceAvailable={sourceAvailable}; body=thincoat/mobilecaps/permeable-neck";
    }

    private int CountCells() { var count = 0; foreach (var film in films) count += film.CellCount; return count; }
    private int FindFilm(FluidSurfaceAnchor anchor, int outlet = 0)
    {
        int first = outlet * FilmsPerOutlet, end = first + FilmsPerOutlet;
        if (end > films.Length)
        {
            // Allocate additional banks only when a new site actually binds.
            // The default Mouth case keeps the original four-patch working set.
            int previous = films.Length;
            Array.Resize(ref films, end);
            Array.Resize(ref filmOutlets, end);
            for (int i = previous; i < end; i++)
            { films[i] = new(surface); filmOutlets[i] = i / FilmsPerOutlet; }
        }
        for (var i = first; i < end; i++) if (films[i].TryGetCell(anchor, out _)) return i;
        // Preserve the original four-patch capacity and first-empty allocation for
        // each owner. A missing receiver defers transfer; it never clears wet skin.
        for (var i = first; i < end; i++)
            if (films[i].Volume <= 0 && films[i].TryBind(anchor)) return i;
        return -1;
    }

    private double AddBead(int film, FluidSurfaceAnchor anchor, double requested)
    {
        var hasSample = surface.TryEvaluate(anchor, out var worldSample);
        var free = -1;
        for (var i = 0; i < beads.Length; i++)
        {
            if (beads[i].Volume <= 0) { if (free < 0 && !IsSourceBead(i) && !BeadSlotReserved(i)) free = i; continue; }
            if (beads[i].Outlet == filmOutlets[film] && beads[i].Film == film && beads[i].Anchor.Triangle == anchor.Triangle && beads[i].Anchor.Generation == anchor.Generation)
            {
                var accepted = Math.Min(requested, Math.Max(0, BeadLimit - beads[i].Volume));
                beads[i].Volume += accepted;
                if (hasSample) { beads[i].LastWorldSample = worldSample; beads[i].HasWorldSample = true; }
                return accepted;
            }
        }
        if (free < 0)
        {
            for (var i = 0; i < beads.Length; i++)
                if (!IsSourceBead(i) && !BeadSlotReserved(i) &&
                    (free < 0 || beads[i].Created < beads[free].Created)) free = i;
            if (free >= 0) RetiredVolume += beads[free].Volume;
        }
        if (free < 0) return 0;
        var volume = Math.Min(requested, BeadLimit);
        rivulets[free]?.Reset();
        beads[free] = new() { Outlet = filmOutlets[film], Created = ++ownerSequence, Film = film, Anchor = anchor, Volume = volume,
            LastWorldSample = worldSample, HasWorldSample = hasSample };
        return volume;
    }

    private void PoolFilmBeads(int film)
    {
        for (var cell = 0; cell < films[film].CellCount; cell++)
        {
            var sample = films[film].GetCell(cell);
            // Contact film owns a thin coating, not a stationary millimetre-thick
            // receiving reservoir. Recover pooled inventory into mobile contact caps.
            if (sample.Thickness <= 0.00005) continue;
            var excess = sample.Volume - sample.Geometry.Area * 0.000025;
            var received = AddBead(film, films[film].GetAnchor(cell), excess);
            if (received > 0) films[film].TakeVolume(cell, received);
        }
    }

    private void StepBeads(double dt)
    {
        int start = beadStepOrder;
        beadStepOrder = (beadStepOrder + 1) % beads.Length;
        for (var offset = 0; offset < beads.Length; offset++)
        {
            int i = (start + offset) % beads.Length;
            ref var bead = ref beads[i];
            if (bead.Volume <= 0) continue;
            var site = GetOutletSettings((BodyFluidOutletKind)bead.Outlet);
            viscosity = site.ViscosityPaSeconds;
            if (bead.Anchor.Generation != generation)
            {
                if (bead.HasWorldSample && !BeadSlotReserved(i)) ReleaseBead(i, bead.LastWorldSample, bead.LastWorldSample.Velocity);
                continue;
            }
            if (!surface.TryEvaluate(bead.Anchor, out var sample)) continue;
            bead.LastWorldSample = sample; bead.HasWorldSample = true;
            RecordRivulet(i, bead.Anchor, sample);
            BeadDimensions(bead.Volume, out _, out _, out var footprintRadius);
            var tangentGravity = Gravity - sample.Normal * Vector3.Dot(Gravity, sample.Normal);
            var drive = Density * bead.Volume * tangentGravity.Length();
            var retention = SurfaceTension * footprintRadius * 2 * 0.025;
            var outward = Density * bead.Volume * Math.Max(0, Vector3.Dot(Gravity, sample.Normal));
            var adhesion = SurfaceTension * Math.PI * footprintRadius * 2 * 0.15;
            if (outward > adhesion)
            {
                if (!BeadSlotReserved(i) && TryStartThread(i, sample)) continue;
                if (!BeadSlotReserved(i) && ReleaseBead(i, sample, sample.Velocity)) continue;
                continue;
            }
            if (BeadSlotReserved(i)) continue; // An owned neck attachment keeps its cap/slot until release or recovery.
            if (drive <= retention || tangentGravity.LengthSquared() < 1e-12f) continue;
            // Reduced rolling/sliding-cap drag uses Stokes-sized viscous resistance,
            // rather than assuming the whole cap shears a 100µm no-slip layer. This
            // avoids metre-scale transit times. SurfaceSpeed is an artistic m/s cap;
            // size, local world gravity, hysteresis and viscosity still set the load.
            var drag = 6 * Math.PI * viscosity * Radius(bead.Volume);
            var speed = Math.Min(site.SurfaceSpeed, (drive - retention) / Math.Max(1e-9, drag));
            var displacement = Vector3.Normalize(tangentGravity) * (float)(speed * dt);
            var candidate = bead.Anchor;
            bead.WalkBlocked = false;
            if (surfaceBudget <= 0) { bead.WalkBlocked = true; continue; }
            surfaceBudget--;
            var completed = surface.TryWalk(ref candidate, displacement, out var walked);
            bead.WalkBlocked = !completed;
            if (surface.BudgetExhausted) continue;
            // A false walk can still reach a verified edge through known triangles.
            // Commit only that known travel; the unknown remainder never becomes an exit.
            if (!surface.TryEvaluate(candidate, out walked)) continue;
            var travel = Vector3.Distance(sample.Position, walked.Position);
            if (!float.IsFinite(travel) || Vector3.Dot(Gravity, walked.Position - sample.Position) < -1e-8f) continue;
            if (travel <= 1e-7f)
            {
                // A resolved open/split edge is allowed to shed liquid in this reduced
                // model. A budget failure above remains pending, never an inferred exit.
                if (!completed)
                {
                    if (TryStartThread(i, sample)) continue;
                    ReleaseBead(i, sample, sample.Velocity + displacement / (float)dt);
                }
                continue;
            }
            bead.Anchor = candidate;
            bead.LastWorldSample = walked;
            bead.RunoffDistance += travel;
            RecordRivulet(i, candidate, walked);
            if (IsSourceBead(i))
            {
                // Role transition only: inventory/slot/possible thread references stay
                // with this cap. Subsequent supply creates another cap at the mouth.
                ReleaseSourceRole(i);
                runoffTransitions++;
            }
            var patch = FindFilm(candidate, bead.Outlet);
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
                if (beads[i].Outlet != beads[j].Outlet) continue;
                var recipient = IsSourceBead(i) ? j : IsSourceBead(j) ? i :
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
        var hasFreeThread = false;
        foreach (var candidate in threads) if (candidate.Model.TotalVolume <= 0) hasFreeThread = true;
        if (!hasFreeThread)
        {
            var oldest = threads[0];
            foreach (var candidate in threads)
                if (candidate.Created < oldest.Created) oldest = candidate;
            RetiredVolume += oldest.Model.TotalVolume;
            oldest.Model.Clear();
            oldest.Attached = oldest.TipAttached = oldest.PendingContact = false;
            oldest.Bead = -1;
        }
        foreach (var thread in threads)
        {
            if (thread.Model.TotalVolume > 0) continue;
            var direction = Vector3.Normalize(Gravity);
            var neckRadius = Math.Clamp(Radius(beads[bead].Volume) * 0.1, 0.00006, 0.0002);
            // The user-selected reduced transport model allows a neck to pass through
            // the body. Its source stays material-attached; terrain is still checked at
            // the terminal drop. This avoids immediate source-face reabsorption.
            var start = sample.Position + sample.Normal * (float)(neckRadius + 0.00008);
            var end = start + direction * 0.005f;
            // The geometric neck is a small part of the released cap; the remaining
            // volume becomes the real terminal gravity load owned by this same model.
            var accepted = thread.Model.InitializePendant(start, end, sample.Velocity, beads[bead].Volume, neckRadius);
            if (accepted <= 0) return false;
            thread.Outlet = beads[bead].Outlet;
            thread.Created = ++ownerSequence;
            beads[bead].Volume -= accepted;
            rivulets[bead]?.Reset();
            thread.Anchor = beads[bead].Anchor; thread.Bead = bead; thread.Attached = true; thread.TipAttached = false; thread.Deferred = 0;
            thread.RequestedSeconds = thread.SimulatedSeconds = 0; thread.AcceptedProposals = thread.PendingSteps = thread.SolverNoProgress = 0;
            thread.TimeDebt = 0;
            thread.PendingContact = false; thread.ContactSegment = 0;
            thread.Model.SetEndpoint(false, true, start, sample.Velocity);
            return true;
        }
        return false;
    }

    private double Deposit(FluidSurfaceAnchor anchor, double requested, int outlet)
    {
        var film = FindFilm(anchor, outlet);
        if (film < 0) return 0;
        var accepted = 0.0;
        if (films[film].TryGetCell(anchor, out var cell))
        {
            var sample = films[film].GetCell(cell);
            var deficit = Math.Max(0, sample.Geometry.Area * 0.000025 - sample.Volume);
            accepted = films[film].AddVolume(anchor, Math.Min(requested, deficit));
        }
        if (accepted < requested) accepted += AddBead(film, anchor, requested - accepted);
        return accepted;
    }

    private bool ReleaseBead(int index, FluidSurfaceSample sample, Vector3 velocity)
    {
        ref var bead = ref beads[index];
        var radius = (float)Radius(bead.Volume);
        if (!TryAddDrop(sample.Position + sample.Normal * (radius + 0.00008f), velocity, bead.Volume, 0.12, bead.Outlet)) return false;
        bead.Volume = 0;
        rivulets[index]?.Reset();
        ReleaseSourceRole(index);
        return true;
    }

    private void ReleaseChangedSkinGeneration()
    {
        skinGenerationChanges++;
        foreach (var state in outletStates) { state.Bead = -1; state.Available = false; }
        // World-space free material survives shape changes. A stale material anchor
        // cannot safely be rebound by triangle number to a replacement mesh.
        foreach (var thread in threads)
        {
            if (thread.Model.TotalVolume <= 0) continue;
            thread.Model.SetEndpoint(false, false, thread.Model.GetNodePosition(0), thread.Model.GetNodeVelocity(0));
            var last = thread.Model.NodeCount - 1;
            thread.Model.SetEndpoint(true, false, thread.Model.GetNodePosition(last), thread.Model.GetNodeVelocity(last));
            thread.Attached = thread.TipAttached = false; thread.Bead = -1;
        }
        for (var i = 0; i < beads.Length; i++)
            if (beads[i].Volume > 0 && beads[i].HasWorldSample)
                ReleaseBead(i, beads[i].LastWorldSample, beads[i].LastWorldSample.Velocity);
        // A thin old film has no verified replacement support. Explicitly retire only
        // that owner and report it; ground, reservoir, drops and filament are retained.
        foreach (var film in films)
        {
            var retired = film.Clear();
            retiredSkinFilm += retired; RetiredVolume += retired;
        }
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
    private bool TryAddDrop(Vector3 position, Vector3 velocity, double volume, double skinCooldown = 0, int outlet = 0)
    {
        if (volume <= 0) return true;
        for (var i = 0; i < drops.Length; i++)
            if (drops[i].Volume <= 0) { drops[i] = new() { Outlet = outlet, Created = ++ownerSequence, Position = position, Velocity = velocity, Volume = volume, SkinCooldown = skinCooldown }; return true; }
        var oldest = 0;
        for (var i = 1; i < drops.Length; i++) if (drops[i].Created < drops[oldest].Created) oldest = i;
        RetiredVolume += drops[oldest].Volume;
        drops[oldest] = new() { Outlet = outlet, Created = ++ownerSequence, Position = position, Velocity = velocity, Volume = volume, SkinCooldown = skinCooldown };
        return true;
    }

    /// <summary>Explicit lifecycle retirement. Lifetime timers never delete simulation liquid.</summary>
    private void RetireInventory()
    {
        RetiredVolume += TotalVolume;
        foreach (var state in outletStates) state.Reset();
        generation = 0; runoffTransitions = 0; coalescedVolume = 0;
        wettingTrailTransfer = actualCoatVolume = 0;
        lastRuntimeSubsteps = 0; lastRuntimeStepSeconds = 0; microstepCount = 0;
        skinGenerationChanges = 0; retiredSkinFilm = 0;
        foreach (var film in films) film.Clear();
        Array.Clear(beads); Array.Clear(drops);
        foreach (var path in rivulets) path?.Reset();
        foreach (var thread in threads)
        { thread.Model.Clear(); thread.Attached = thread.TipAttached = false; thread.Bead = -1; thread.Deferred = thread.TimeDebt = 0; thread.PendingContact = false; thread.ContactSegment = 0; }
        foreach (var ground in grounds) ground?.Clear();
    }

    public void Clear()
    { RetireInventory(); Emitting = false; DeferredSeconds = 0; Status = "Saliva cleared; inventory explicitly retired"; }
}
