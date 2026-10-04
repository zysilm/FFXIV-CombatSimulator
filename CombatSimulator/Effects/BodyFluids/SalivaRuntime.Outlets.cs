// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Core;
using CombatSimulator.Effects.BodyFluids.Surface;

namespace CombatSimulator.Effects.BodyFluids;

internal sealed partial class SalivaRuntime
{
    private readonly long[] outletFilmUses = new long[OutletCount * FilmsPerOutlet];
    private long outletFilmSequence;
    private sealed class OutletState
    {
        public BodyFluidOutletSettings? Settings;
        public BodyFluidOutletSettings LastSettings = new() { SettingsVersion = 1 };
        public FluidSurfaceAnchor Anchor, PreviousAnchor;
        public FluidSurfaceSample Sample;
        public int Bead = -1, Film = -1;
        public bool Available;
        public string Status = "Start preview to bind this site";
        public double Reservoir, AcceptedSeconds;
        public long NextLog;
        public void Reset()
        {
            Settings = null; Anchor = PreviousAnchor = default; Sample = default;
            Bead = Film = -1; Available = false; Reservoir = AcceptedSeconds = 0; NextLog = 0;
            Status = "Start preview to bind this site";
        }
    }

    private bool IsSourceBead(int index)
    {
        foreach (var state in outletStates) if (state.Bead == index) return true;
        return false;
    }

    private void ReleaseSourceRole(int index)
    {
        foreach (var state in outletStates) if (state.Bead == index) state.Bead = -1;
    }

    private void PrepareOutlets()
    {
        foreach (var state in outletStates) { state.Settings = null; state.Available = false; }
        foreach (var settings in config.BodyFluidOutlets)
            if (settings.Enabled)
            {
                var state = outletStates[(int)settings.Kind];
                state.Settings = state.LastSettings = settings;
            }
        int start = outletOrder;
        outletOrder = (outletOrder + 1) % OutletCount;
        for (int order = 0; order < OutletCount; order++)
        {
            int index = (start + order) % OutletCount;
            var state = outletStates[index];
            if (state.Settings == null)
            {
                // Removal stops only that supply. Existing material keeps its owner
                // and contact; no shared ground or other outlet is cleared.
                RetiredVolume += state.Reservoir;
                state.Reservoir = 0; state.Bead = -1;
                continue;
            }
            if (!surface.TryGetOutletAnchor(state.Settings, out var anchor))
            { state.Status = surface.OutletStatus; continue; }
            if (!surface.TryEvaluate(anchor, out var sample))
            { state.Status = "Posed anchor budget pending"; continue; }
            state.Anchor = anchor; state.Sample = sample;
            int film = FindOutletFilm(index, anchor);
            state.Film = film; state.Available = film >= 0;
            if (state.Available)
            {
                // Match the original transition timing: an unavailable receiver
                // must not release the previous cap's source role.
                var previous = state.PreviousAnchor;
                if (state.Bead >= 0 && previous.Generation == anchor.Generation &&
                    (previous.Triangle != anchor.Triangle || Vector3.DistanceSquared(previous.Barycentric, anchor.Barycentric) > 1e-8f))
                    state.Bead = -1;
                state.PreviousAnchor = anchor;
                films[film].SetMaterial(state.Settings.ViscosityPaSeconds);
            }
            state.Status = state.Available ? surface.GetOutletStatus(state.Settings.Kind) : "Receiving surface patch pending";
        }
    }

    private int FindOutletFilm(int source, FluidSurfaceAnchor anchor)
    {
        int film = FindFilm(anchor, source);
        if (film >= 0)
        {
            outletFilmUses[film] = ++outletFilmSequence;
            return film;
        }
        // A source must not permanently stop after visiting four wet patches.
        // This policy applies only to source binding; ordinary body receivers
        // retain their existing conservative capacity/optional-transfer behavior.
        int first = source * FilmsPerOutlet, end = first + FilmsPerOutlet;
        int victim = -1, bestRank = int.MaxValue;
        for (int candidate = first; candidate < end; candidate++)
        {
            int rank = 0;
            bool canRelease = true;
            for (int i = 0; i < beads.Length; i++)
            {
                if (beads[i].Film != candidate || beads[i].Outlet != source) continue;
                if (BeadSlotReserved(i)) rank = 2;
                else if (beads[i].Volume > 0) rank = Math.Max(rank, 1);
                if (beads[i].Volume > 0 && !beads[i].HasWorldSample)
                {
                    if (!surface.TryEvaluate(beads[i].Anchor, out var verified))
                    { canRelease = false; break; }
                    beads[i].LastWorldSample = verified; beads[i].HasWorldSample = true;
                }
            }
            if (!canRelease) continue;
            if (victim < 0 || rank < bestRank || rank == bestRank && outletFilmUses[candidate] < outletFilmUses[victim])
            { victim = candidate; bestRank = rank; }
        }
        if (victim < 0) return -1; // Unknown poses still defer; never guess a release position.
        for (int i = 0; i < beads.Length; i++)
        {
            if (beads[i].Film != victim || beads[i].Outlet != source) continue;
            PreserveRivulet(i);
            // An old neck retains its actual material endpoint and model inventory.
            // It stops borrowing from this receiving bank before the bank is reused.
            foreach (var thread in threads)
                if (thread.Bead == i) thread.Bead = -1;
            if (beads[i].Volume > 0)
            {
                var sample = beads[i].LastWorldSample;
                if (surface.TryEvaluate(beads[i].Anchor, out var current)) sample = current;
                ReleaseBead(i, sample, sample.Velocity);
            }
            else ReleaseSourceRole(i);
        }
        RetiredVolume += films[victim].Clear();
        outletFilmUses[victim] = ++outletFilmSequence;
        return films[victim].TryBind(anchor) ? victim : -1;
    }

    public string DescribeOutlet(BodyFluidOutletKind kind)
    {
        int index = (int)kind;
        if ((uint)index >= OutletCount) return "Unknown site";
        var state = outletStates[index];
        return $"{(state.Available ? "Ready" : "Pending")}: {state.Status}";
    }

    private int AllocateSourceBead()
    {
        int oldest = -1;
        for (int i = 0; i < beads.Length; i++)
        {
            if (IsSourceBead(i) || BeadSlotReserved(i)) continue;
            if (beads[i].Volume <= 0) { oldest = i; break; }
            if (oldest < 0 || beads[i].Created < beads[oldest].Created) oldest = i;
        }
        if (oldest < 0) return -1;
        PreserveRivulet(oldest);
        RetiredVolume += beads[oldest].Volume;
        beads[oldest] = new() { Created = ++ownerSequence };
        rivulets[oldest]?.Reset();
        return oldest;
    }

    private void SupplyOutlets(double dt)
    {
        for (int order = 0; order < OutletCount; order++)
        {
            int index = (outletOrder + order) % OutletCount;
            var state = outletStates[index];
            if (!state.Available || state.Settings == null) continue;
            if (Emitting)
            {
                double requested = state.Settings.FlowMlPerSecond *
                    (LargeVisibilityPreview ? 20 : 1) * 1e-6 * dt;
                state.Reservoir += requested; EmittedVolume += requested; state.AcceptedSeconds += dt;
            }
            if (state.Bead < 0) state.Bead = AllocateSourceBead();
            if (state.Bead < 0) continue;
            ref var cap = ref beads[state.Bead];
            cap.Outlet = index;
            cap.Film = state.Film; cap.Anchor = state.Anchor;
            double accepted = Math.Min(state.Reservoir, Math.Max(0, BeadLimit - cap.Volume));
            cap.Volume += accepted; state.Reservoir -= accepted;
            if (films[state.Film].TryGetCell(state.Anchor, out var cell))
            {
                var sample = films[state.Film].GetCell(cell);
                BeadDimensions(cap.Volume, out _, out _, out var footprint);
                double area = Math.Min(sample.Geometry.Area, Math.PI * footprint * footprint);
                double deficit = Math.Max(0, area * .000025 - sample.Volume);
                cap.Volume -= films[state.Film].AddVolume(state.Anchor, Math.Min(cap.Volume, deficit));
            }
            long now = Environment.TickCount64;
            if (now >= state.NextLog)
            {
                state.NextLog = now + 5000;
                Services.Log.Info($"Body fluid supply: kind={state.Settings.Kind},film={state.Film},sourceBead={state.Bead}," +
                    $"sourceTriangle={state.Anchor.Triangle},capTriangle={cap.Anchor.Triangle},position={state.Sample.Position}," +
                    $"accepted={accepted * 1e6:F6}ml; {surface.GetOutletStatus(state.Settings.Kind)}");
            }
        }
    }

    public BodyFluidOutletSettings GetOutletSettings(BodyFluidOutletKind kind)
        => outletStates[(int)kind].LastSettings;

    private double GroundVolume { get { double volume = 0; foreach (var ground in grounds) if (ground != null) volume += ground.Volume; return volume; } }
    private int GroundCellCount { get { int count = 0; foreach (var ground in grounds) if (ground != null) count += ground.CellCount; return count; } }
}
