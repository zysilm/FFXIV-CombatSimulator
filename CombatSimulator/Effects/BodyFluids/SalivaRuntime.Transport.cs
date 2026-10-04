// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Effects.BodyFluids;

internal sealed partial class SalivaRuntime
{
    private void StepThreads(double dt)
    {
        foreach (var thread in threads)
        {
            var site = GetOutletSettings((BodyFluidOutletKind)thread.Outlet);
            viscosity = site.ViscosityPaSeconds;
            thread.Model.SetMaterial(viscosity, site.FilamentRelaxation, site.Stringiness);
            if (!thread.PendingContact && thread.Model.TotalVolume > 0) ConvertCollapsedPieces(thread);
            if (thread.Model.TotalVolume <= 0)
            {
                // The actual segment/break/terminal owners are empty. Releasing a slot
                // is safe even if past floating-point accounting had a positive residue.
                thread.Model.Clear();
                thread.Attached = thread.TipAttached = thread.PendingContact = false;
                thread.Bead = -1; thread.ContactSegment = 0; thread.Deferred = thread.TimeDebt = 0;
                continue;
            }
            thread.RequestedSeconds += dt;
            thread.TimeDebt += dt;
            if (!thread.PendingContact)
            {
                if (thread.TipAttached)
                {
                    if (!surface.TryEvaluate(thread.TipAnchor, out var tip)) continue;
                    var last = thread.Model.GetSegment(thread.Model.SegmentCount - 1);
                    if (last.Broken || last.Volume <= 0)
                    {
                        thread.Model.SetEndpoint(true, false, tip.Position, tip.Velocity);
                        thread.TipAttached = false;
                    }
                    else thread.Model.SetEndpoint(true, true, tip.Position + tip.Normal * (float)Math.Max(last.Radius, 0.0001), tip.Velocity);
                }
                if (thread.Attached)
                {
                    if (thread.Bead >= 0 && beads[thread.Bead].Volume > 0) thread.Anchor = beads[thread.Bead].Anchor;
                    if (!surface.TryEvaluate(thread.Anchor, out var source)) continue;
                    var sourceNeck = thread.Model.GetSegment(0);
                    thread.Model.SetEndpoint(false, true, source.Position + source.Normal * (float)(Math.Max(sourceNeck.Radius, 0.000025) + 0.00008), source.Velocity);
                    if (thread.Model.GetSegment(0).Broken || thread.Model.GetSegment(0).Volume <= 0)
                    {
                        // Loss of actual proximal inventory/contact releases the bridge;
                        // stopping emission alone does not strip an otherwise attached thread.
                        thread.Model.SetEndpoint(false, false, source.Position, source.Velocity);
                        thread.Attached = false;
                    }
                    else if (thread.Bead >= 0 && (thread.Model.TerminalConnectedToSource || thread.TipAttached))
                    {
                        var bead = beads[thread.Bead];
                        var first = thread.Model.GetSegment(0);
                        BeadDimensions(bead.Volume, out var beadRadius, out _, out _);
                        var length = Math.Max(1e-5, Vector3.Distance(first.A, first.B));
                        // Local neck is fed by reservoir/head pressure and the extensional
                        // tension already transmitted by the thread, not unconditional flow.
                        var head = Density * Vector3.Dot(Gravity, first.B - first.A);
                        var direction = (first.B - first.A) / (float)length;
                        var strainRate = Vector3.Dot(thread.Model.GetNodeVelocity(1) - thread.Model.GetNodeVelocity(0), direction) / length;
                        var traction = Math.Max(0, first.TensileStress) + 3 * viscosity * Math.Max(0, strainRate);
                        // A reduced throat pressure closure: axial stretching supplies a
                        // tensile traction, capillary curvature opposes a thin neck. No
                        // reservoir volume is spent when the resulting pressure is negative.
                        var sourcePressure = bead.Volume > 0 ? 2 * SurfaceTension / beadRadius : 0;
                        var pressure = head + sourcePressure - SurfaceTension / Math.Max(first.Radius, 1e-5) + traction;
                        var rate = Math.PI * Math.Pow(Math.Max(first.Radius, 1e-6), 4) / (8 * viscosity * length) * Math.Max(0, pressure);
                        var accepted = thread.Model.AddVolume(0, Math.Min(bead.Volume * 0.2, rate * dt));
                        beads[thread.Bead].Volume -= accepted;
                        if (accepted < rate * dt && films[bead.Film].TryGetCell(thread.Anchor, out var cell))
                        {
                            var sample = films[bead.Film].GetCell(cell);
                            var available = Math.Max(0, sample.Volume - sample.Geometry.Area * 0.00001);
                            var received = thread.Model.AddVolume(0, Math.Min(available, rate * dt - accepted));
                            films[bead.Film].TakeVolume(cell, received);
                        }
                    }
                }
                for (var i = 0; i < thread.Model.NodeCount; i++) thread.Previous[i] = thread.Model.GetNodePosition(i);
                thread.PreviousTerminalCenter = thread.Model.TerminalCenter;
                // Topology changes only between resolved proposals. Previous samples
                // are remapped with node indices, then the resulting geometry is swept.
                thread.Model.CoarsenShortSegments(thread.Previous);
                // Keep elapsed physical time while a proposal awaits acceptance. The
                // solver retains its own fixed substep budget, and catch-up per call
                // is capped at two normal steps; no unchecked large-dt jump is used.
                var requestedTime = Math.Min(thread.TimeDebt, 1.0 / 30);
                var result = thread.Model.Advance(requestedTime, Gravity);
                if (result.SimulatedSeconds <= 0) thread.SolverNoProgress++;
                thread.SimulatedSeconds += result.SimulatedSeconds;
                thread.TimeDebt = Math.Max(0, thread.TimeDebt - result.SimulatedSeconds);
                thread.Deferred = thread.TimeDebt;
                DeferredSeconds = Math.Max(DeferredSeconds, result.DeferredSeconds);
                thread.PendingContact = true;
                thread.ContactSegment = 0;
            }
            // Reduced body transport: the material lip endpoint follows the current
            // mesh, while the neck can pass through body geometry. Only the loaded
            // terminal is terrain-swept; broken fragments become terrain-swept drops.
            // This costs one terrain query per thread instead of 69 body/terrain
            // queries and cannot indefinitely reabsorb the source into its own film.
            thread.ContactSegment = thread.Model.SegmentCount;
            if (thread.Model.TerminalVolume <= 0) thread.PendingContact = false;
            else if (terrainBudget > 0)
            {
                var radius = (float)Radius(thread.Model.TerminalVolume);
                var hit = TryGround(thread.PreviousTerminalCenter,
                    thread.Model.TerminalCenter - Vector3.UnitY * radius,
                    out var point, out var normal, out _, out var a, out var b, out var c);
                if (!hit) thread.PendingContact = false;
                else
                {
                    var inventory = thread.Model.TerminalVolume;
                    var received = AddPuddle(point, normal, a, b, c, inventory, thread.Outlet);
                    thread.Model.TakeTerminalVolume(received);
                    // An unaccepted terrain inventory stays pending at the proposal,
                    // never silently falls through the ground or loses its volume.
                    if (received + 1e-18 >= inventory) thread.PendingContact = false;
                }
            }
            if (thread.PendingContact) DeferredSeconds = Math.Max(DeferredSeconds, dt);
            if (thread.PendingContact) thread.PendingSteps++;
            else thread.AcceptedProposals++;
            for (var i = 0; i < thread.Model.SegmentCount; i++)
            {
                var segment = thread.Model.GetSegment(i);
                if (segment.PendingBreakVolume <= 0) continue;
                if (thread.PendingContact) continue;
                var position = (segment.A + segment.B) * 0.5f;
                var velocity = (thread.Model.GetNodeVelocity(i) + thread.Model.GetNodeVelocity(i + 1)) * 0.5f;
                if (TryAddDrop(position, velocity, segment.PendingBreakVolume, 0.12, thread.Outlet)) thread.Model.TakeBreakVolume(i, segment.PendingBreakVolume);
            }
            if (!thread.PendingContact) ConvertCollapsedPieces(thread);
        }
    }

    private void ConvertCollapsedPieces(Thread thread)
    {
        RecoverProximalPiece(thread);
        ConvertCompactFreePieces(thread);
        if (thread.Model.TerminalVolume <= 0 || thread.Model.TerminalConnectedToSource) return;
        var last = thread.Model.SegmentCount - 1;
        var first = last;
        while (first >= 0 && !thread.Model.GetSegment(first).Broken && thread.Model.GetSegment(first).Volume > 0) first--;
        first++;
        double distalVolume = 0;
        for (var i = first; i <= last; i++) distalVolume += thread.Model.GetSegment(i).Volume;
        var total = thread.Model.TerminalVolume + distalVolume;
        var diameter = 2 * Radius(total);
        double length = 0;
        var minimum = thread.Model.TerminalCenter; var maximum = minimum;
        for (var i = first; i <= last; i++)
        {
            var segment = thread.Model.GetSegment(i);
            length += Vector3.Distance(segment.A, segment.B);
            minimum = Vector3.Min(minimum, Vector3.Min(segment.A, segment.B));
            maximum = Vector3.Max(maximum, Vector3.Max(segment.A, segment.B));
        }
        // A compact, disconnected neck can capillary-relax into its terminal drop.
        // Both arc length and spatial extent must fit the combined drop diameter;
        // a long slender downstream filament keeps its own material and centerline.
        if (length > diameter || Vector3.Distance(minimum, maximum) > diameter) return;
        var position = thread.Model.TerminalCenter * (float)thread.Model.TerminalVolume;
        var momentum = thread.Model.TerminalVelocity * (float)thread.Model.TerminalVolume;
        for (var i = first; i <= last; i++)
        {
            var segment = thread.Model.GetSegment(i);
            position += (segment.A + segment.B) * (float)(segment.Volume * 0.5);
            momentum += (thread.Model.GetNodeVelocity(i) + thread.Model.GetNodeVelocity(i + 1)) * (float)(segment.Volume * 0.5);
        }
        if (!TryAddDrop(position / (float)total, momentum / (float)total, total, 0.12, thread.Outlet)) return;
        thread.Model.TakeTerminalVolume(thread.Model.TerminalVolume);
        for (var i = first; i <= last; i++) thread.Model.TakeVolume(i, thread.Model.GetSegment(i).Volume);
    }

    private void ConvertCompactFreePieces(Thread thread)
    {
        var segment = 0;
        while (segment < thread.Model.SegmentCount)
        {
            while (segment < thread.Model.SegmentCount && thread.Model.GetSegment(segment).Volume <= 0) segment++;
            if (segment >= thread.Model.SegmentCount) break;
            var first = segment;
            var origin = thread.Model.GetSegment(first).A;
            var minimum = origin; var maximum = origin;
            double volume = 0, length = 0, px = 0, py = 0, pz = 0, vx = 0, vy = 0, vz = 0;
            while (segment < thread.Model.SegmentCount && thread.Model.GetSegment(segment).Volume > 0)
            {
                var sample = thread.Model.GetSegment(segment);
                volume += sample.Volume; length += Vector3.Distance(sample.A, sample.B);
                minimum = Vector3.Min(minimum, Vector3.Min(sample.A, sample.B));
                maximum = Vector3.Max(maximum, Vector3.Max(sample.A, sample.B));
                var offset = (sample.A - origin) + (sample.B - origin);
                var velocity = thread.Model.GetNodeVelocity(segment) + thread.Model.GetNodeVelocity(segment + 1);
                px += offset.X * sample.Volume * 0.5; py += offset.Y * sample.Volume * 0.5; pz += offset.Z * sample.Volume * 0.5;
                vx += velocity.X * sample.Volume * 0.5; vy += velocity.Y * sample.Volume * 0.5; vz += velocity.Z * sample.Volume * 0.5;
                segment++;
            }
            if (first == 0 && thread.Attached || segment == thread.Model.SegmentCount &&
                (thread.TipAttached || thread.Model.TerminalVolume > 0)) continue;
            var diameter = 2 * Radius(volume);
            if (length > diameter || Vector3.Distance(minimum, maximum) > diameter) continue;
            var center = origin + new Vector3((float)(px / volume), (float)(py / volume), (float)(pz / volume));
            var inherited = new Vector3((float)(vx / volume), (float)(vy / volume), (float)(vz / volume));
            if (!TryAddDrop(center, inherited, volume, 0.12, thread.Outlet)) return;
            for (var i = first; i < segment; i++) thread.Model.TakeVolume(i, thread.Model.GetSegment(i).Volume);
        }
    }

    private void RecoverProximalPiece(Thread thread)
    {
        if (!thread.Attached || thread.Model.TerminalConnectedToSource || thread.Bead < 0) return;
        double volume = 0, length = 0;
        var end = 0;
        while (end < thread.Model.SegmentCount)
        {
            var segment = thread.Model.GetSegment(end);
            if (segment.Broken || segment.Volume <= 0) break;
            volume += segment.Volume; length += Vector3.Distance(segment.A, segment.B); end++;
        }
        // A disconnected proximal neck can rejoin its lip reservoir only after actual
        // contraction into a cap-sized region. Long fragments retain their own inventory.
        if (volume <= 0 || length > 2 * Radius(volume)) return;
        ref var cap = ref beads[thread.Bead];
        var accepted = Math.Min(volume, Math.Max(0, BeadLimit - cap.Volume));
        if (accepted <= 0) return;
        cap.Volume += accepted;
        cap.Anchor = thread.Anchor;
        var remaining = accepted;
        for (var i = 0; i < end && remaining > 0; i++) remaining -= thread.Model.TakeVolume(i, remaining);
        if (accepted + 1e-18 >= volume)
        {
            thread.Model.SetEndpoint(false, false, thread.Model.GetNodePosition(0), thread.Model.GetNodeVelocity(0));
            thread.Attached = false;
        }
    }

    private void StepDrops(double dt)
    {
        var start = dropStepOrder++ % drops.Length;
        for (var order = 0; order < drops.Length; order++)
        {
            var i = (start + order) % drops.Length;
            ref var drop = ref drops[i];
            if (drop.Volume <= 0 || terrainBudget <= 0 || drop.SkinCooldown <= 0 && surfaceBudget <= 0) continue;
            var radius = (float)Radius(drop.Volume);
            var velocity = drop.Velocity + Gravity * (float)dt;
            var next = drop.Position + (drop.Velocity + velocity) * (float)(dt * 0.5);
            FluidSurfaceAnchor anchor = default;
            FluidSurfaceSample skinSample = default;
            var skinFraction = 1f;
            var skinHit = false;
            if (drop.SkinCooldown <= 0)
            {
                surfaceBudget--;
                skinHit = surface.TryContact(drop.Position, next, radius, out anchor, out skinSample, out skinFraction);
                if (surface.BudgetExhausted) continue;
            }
            drop.SkinCooldown = Math.Max(0, drop.SkinCooldown - dt);
            var groundHit = TryGround(drop.Position, next - Vector3.UnitY * radius,
                out var point, out var normal, out var groundFraction, out var a, out var b, out var c);
            if (skinHit && (!groundHit || skinFraction <= groundFraction))
            {
                var accepted = Deposit(anchor, drop.Volume, drop.Outlet);
                drop.Volume -= accepted;
                if (drop.Volume > 0)
                {
                    // Pool/patch saturation must not pin a remaining drop forever.
                    // Pass through body geometry briefly, preserving gravity velocity.
                    drop.Position = next; drop.Velocity = velocity; drop.SkinCooldown = 0.12;
                }
            }
            else if (groundHit)
            {
                var accepted = AddPuddle(point, normal, a, b, c, drop.Volume, drop.Outlet);
                drop.Volume -= accepted;
                if (drop.Volume > 0) { drop.Position = point + normal * (radius * 0.99f); drop.Velocity = Vector3.Zero; }
            }
            else { drop.Position = next; drop.Velocity = velocity; }
        }
    }

    private bool TryGround(Vector3 start, Vector3 end, out Vector3 point, out Vector3 normal,
        out float fraction, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        point = normal = a = b = c = default; fraction = 1;
        if (terrainBudget <= 0) return false;
        var delta = end - start; var length = delta.Length();
        if (!float.IsFinite(length) || length < 1e-6f) return false;
        terrainBudget--;
        if (!BGCollisionModule.RaycastMaterialFilter(start, delta / length, out var hit, length)) return false;
        point = new(hit.Point.X, hit.Point.Y, hit.Point.Z);
        a = new(hit.V1.X, hit.V1.Y, hit.V1.Z); b = new(hit.V2.X, hit.V2.Y, hit.V2.Z); c = new(hit.V3.X, hit.V3.Y, hit.V3.Z);
        normal = new(hit.Normal.X, hit.Normal.Y, hit.Normal.Z);
        if (!WorldGeometryBuilder.Finite(normal) || normal.LengthSquared() < 1e-8f) normal = Vector3.Cross(b - a, c - a);
        if (!WorldGeometryBuilder.Finite(point) || !WorldGeometryBuilder.Finite(normal) || normal.LengthSquared() < 1e-8f) return false;
        normal = Vector3.Normalize(normal);
        if (Vector3.Dot(normal, delta) > 0) normal = -normal;
        fraction = Math.Clamp(Vector3.Distance(start, point) / length, 0, 1);
        return true;
    }

    private double AddPuddle(Vector3 point, Vector3 normal, Vector3 a, Vector3 b, Vector3 c, double requested, int outlet)
    {
        var ground = grounds[outlet] ??= new GroundFilmRuntime();
        var accepted = ground.AddContact(point, normal, a, b, c, requested);
        if (accepted < requested && ground.CapacityFull)
        {
            // Preserve the puddle until the larger pool is genuinely full and a
            // new impact cannot be admitted. Retire its ledger before reuse.
            RetiredVolume += ground.Recycle();
            accepted += ground.AddContact(point, normal, a, b, c, requested - accepted);
        }
        return accepted;
    }

    private GroundProbeResult ProbeGroundFilm(Vector3 from, Vector3 to, out GroundSupportHit hit)
    {
        hit = default;
        if (terrainBudget <= 0) return GroundProbeResult.Pending;
        if (!TryGround(from, to, out var point, out var normal, out _, out var a, out var b, out var c))
            return GroundProbeResult.Miss;
        hit = new GroundSupportHit(point, normal, a, b, c);
        return GroundProbeResult.Hit;
    }
}
