// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Effects.BodyFluids.Simulation;

public readonly record struct FilamentSegmentSample(Vector3 A, Vector3 B, double Volume,
    double Radius, double TensileStress, bool Broken, double PendingBreakVolume);

/// <summary>
/// Bounded Lagrangian slender thread with segment inventory, implicit extensional viscosity,
/// Maxwell stress relaxation and conservative capillary redistribution. No fixed rest-length rods.
/// This reduced calibration model does not provide a full mucin constitutive law or bending DVT.
/// Endpoint/contact ownership and swept collision queries belong to the runtime controller.
/// </summary>
public sealed class ViscoelasticFilament
{
    private const double Density = 1000, SurfaceTension = 0.055, ElasticModulus = 8;
    private double viscosity = 0.18, relaxationSeconds = 0.45;
    private const double MaximumInventory = 1e-6, MinimumLength = 1e-5;
    private const double NeckRadius = 25e-6, MaximumStress = 100;
    private readonly Vector3[] positions, velocities, forces, rhs, solved;
    private readonly double[] volumes, brokenVolumes, stresses, masses, pressure, flux, outgoing, volumeDelta;
    private readonly double[] savedVolumes, savedStresses;
    private readonly Matrix3[] blocks, couplings, inverseBlocks;
    private readonly bool[] broken;
    private readonly bool[] pinned = new bool[2];
    private int activeNodes;
    public int NodeCount => activeNodes;
    public int SegmentCount => activeNodes - 1;
    // Actual owners are authoritative. Repeated +/- accounting can leave a positive
    // roundoff ghost after every physical segment/reservoir has been fully transferred.
    public double TotalVolume
    {
        get
        {
            var total = TerminalVolume;
            for (var i = 0; i < SegmentCount; i++) total += volumes[i] + brokenVolumes[i];
            return total;
        }
    }
    public double InternalTransferredVolume { get; private set; }
    public double TerminalVolume { get; private set; }
    public double LastRequestedSeconds { get; private set; }
    public double LastSimulatedSeconds { get; private set; }
    public int LastSubsteps { get; private set; }
    public string LastStopReason { get; private set; } = "Not advanced";
    public int LastStrainRejections { get; private set; }
    public int LastRejectedSegment { get; private set; } = -1;
    public double LastRejectedLengthBefore { get; private set; }
    public double LastRejectedLengthAfter { get; private set; }
    public double LastRejectedTrialSeconds { get; private set; }
    public string LastStrainRejectionReason { get; private set; } = "None";
    public int CoarsenedSegments { get; private set; }
    public double CoarsenedVolume { get; private set; }
    public double LastRemeshMomentumError { get; private set; }
    public Vector3 TerminalCenter
    {
        get
        {
            var radius = Math.Cbrt(TerminalVolume * 3 / (4 * Math.PI));
            var direction = positions[NodeCount - 1] - positions[NodeCount - 2];
            direction = direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : new Vector3(0, -1, 0);
            return positions[NodeCount - 1] + direction * (float)radius;
        }
    }
    public Vector3 TerminalVelocity => velocities[NodeCount - 1];

    /// <summary>
    /// Viscosity controls implicit extensional drag/capillary redistribution; relaxation controls
    /// exponential Maxwell stress decay. Updating either preserves all current segment inventories.
    /// </summary>
    public void SetMaterial(double viscosityPaSeconds, double stressRelaxationSeconds)
    {
        viscosity = double.IsFinite(viscosityPaSeconds) ? Math.Clamp(viscosityPaSeconds, 0.03, 1.2) : 0.18;
        relaxationSeconds = double.IsFinite(stressRelaxationSeconds) ? Math.Clamp(stressRelaxationSeconds, 0.05, 2) : 0.45;
    }

    public ViscoelasticFilament(int nodeCount = 24)
    {
        if (nodeCount < 4 || nodeCount > 32) throw new ArgumentOutOfRangeException(nameof(nodeCount));
        activeNodes = nodeCount;
        positions = new Vector3[nodeCount]; velocities = new Vector3[nodeCount];
        forces = new Vector3[nodeCount]; rhs = new Vector3[nodeCount]; solved = new Vector3[nodeCount];
        masses = new double[nodeCount];
        volumes = new double[nodeCount - 1]; brokenVolumes = new double[nodeCount - 1]; stresses = new double[nodeCount - 1];
        pressure = new double[nodeCount - 1]; outgoing = new double[nodeCount - 1]; volumeDelta = new double[nodeCount - 1];
        flux = new double[nodeCount - 2]; broken = new bool[nodeCount - 1];
        savedVolumes = new double[nodeCount - 1]; savedStresses = new double[nodeCount - 1];
        blocks = new Matrix3[nodeCount]; couplings = new Matrix3[nodeCount - 1]; inverseBlocks = new Matrix3[nodeCount];
    }

    /// <summary>Initialize an empty bridge; caller retains rejected seed volume.</summary>
    public double Initialize(Vector3 start, Vector3 end, Vector3 inheritedVelocity, double seedVolume)
    {
        var span = Vector3.Distance(start, end);
        if (TotalVolume > 0 || !Finite(start) || !Finite(end) || !Finite(inheritedVelocity) || !float.IsFinite(span) ||
            span < MinimumLength * (positions.Length - 1) || !double.IsFinite(seedVolume) || seedVolume <= 0) return 0;
        Clear();
        var accepted = Math.Min(seedVolume, MaximumInventory);
        for (var i = 0; i < NodeCount; i++)
        { positions[i] = Vector3.Lerp(start, end, (float)i / SegmentCount); velocities[i] = inheritedVelocity; }
        for (var i = 0; i < SegmentCount; i++) volumes[i] = accepted / SegmentCount;
        return accepted;
    }

    /// <summary>
    /// Release one attached cap as a true gravity-loaded terminal reservoir plus a geometrical neck.
    /// The caller removes exactly the accepted total from its bead owner. No volume is duplicated.
    /// </summary>
    public double InitializePendant(Vector3 start, Vector3 attachment, Vector3 inheritedVelocity,
        double releasedVolume, double neckRadius)
    {
        var length = Vector3.Distance(start, attachment);
        if (!double.IsFinite(neckRadius) || neckRadius <= 0 || !float.IsFinite(length) || length <= 0) return 0;
        var neckVolume = Math.PI * neckRadius * neckRadius * length;
        var acceptedTotal = Math.Min(releasedVolume, MaximumInventory);
        if (!double.IsFinite(acceptedTotal) || acceptedTotal <= neckVolume * 2) return 0;
        var acceptedNeck = Initialize(start, attachment, inheritedVelocity, neckVolume);
        if (acceptedNeck <= 0) return 0;
        TerminalVolume = acceptedTotal - acceptedNeck;
        return acceptedTotal;
    }

    public double TakeTerminalVolume(double requested)
    {
        if (!double.IsFinite(requested) || requested <= 0) return 0;
        var taken = Math.Min(requested, TerminalVolume);
        TerminalVolume -= taken;
        return taken;
    }

    public bool TerminalConnectedToSource
    {
        get
        {
            for (var segment = 0; segment < SegmentCount; segment++) if (broken[segment] || volumes[segment] <= 0) return false;
            return true;
        }
    }

    /// <summary>Source/contact endpoints use validated poses; unpinning retains inherited velocity.</summary>
    public bool SetEndpoint(bool atEnd, bool attached, Vector3 position, Vector3 velocity)
    {
        if (!Finite(position) || !Finite(velocity) || !float.IsFinite(velocity.LengthSquared())) return false;
        pinned[atEnd ? 1 : 0] = attached;
        var i = atEnd ? NodeCount - 1 : 0;
        if (attached) positions[i] = position;
        velocities[i] = velocity;
        return true;
    }

    public Vector3 GetNodePosition(int index) => positions[index];
    public Vector3 GetNodeVelocity(int index) => velocities[index];

    public FilamentSegmentSample GetSegment(int i)
    {
        var length = Length(i);
        return new(positions[i], positions[i + 1], volumes[i], Math.Sqrt(volumes[i] / (Math.PI * length)),
            stresses[i], broken[i], brokenVolumes[i]);
    }

    public double AddVolume(int segment, double requested)
    {
        if (segment < 0 || segment >= SegmentCount || broken[segment] || !double.IsFinite(requested) || requested <= 0) return 0;
        var accepted = Math.Min(requested, Math.Max(0, MaximumInventory - TotalVolume));
        volumes[segment] += accepted;
        return accepted;
    }

    public double TakeVolume(int segment, double requested)
    {
        if (segment < 0 || segment >= SegmentCount || !double.IsFinite(requested) || requested <= 0) return 0;
        var taken = Math.Min(requested, volumes[segment]);
        volumes[segment] -= taken;
        return taken;
    }

    /// <summary>Broken neck inventory stays owned until bead/drop allocation accepts it, including partial transfers.</summary>
    public double TakeBreakVolume(int segment, double requested)
    {
        if (segment < 0 || segment >= SegmentCount || !double.IsFinite(requested) || requested <= 0) return 0;
        var taken = Math.Min(requested, brokenVolumes[segment]);
        brokenVolumes[segment] -= taken;
        return taken;
    }

    /// <summary>
    /// Between resolved contact proposals only: remove at most four unresolved short
    /// material intervals, merging their inventory/stress into an intact neighbour.
    /// Previous CCD node samples are remapped with the topology. Broken gaps and long
    /// intervals are retained. No rest length, spring, volume deletion or fake rupture.
    /// </summary>
    public int CoarsenShortSegments(Span<Vector3> previousPositions)
    {
        if (previousPositions.Length < NodeCount) throw new ArgumentException("CCD snapshot too short", nameof(previousPositions));
        // Pending fragments still use this topology's midpoint/velocity ownership.
        // Resolve their receiving inventory before changing any indexed endpoints.
        for (var segment = 0; segment < SegmentCount; segment++) if (brokenVolumes[segment] > 0) return 0;
        var removed = 0;
        for (var i = 0; i < SegmentCount && removed < 4 && NodeCount > 2; i++)
        {
            if (broken[i] || volumes[i] <= 0 || brokenVolumes[i] > 0) continue;
            var resolution = Math.Max(WorldResolution(positions[i]), WorldResolution(positions[i + 1]));
            var threshold = Math.Min(0.0001, Math.Max(3 * MinimumLength, 4 * resolution));
            if (Vector3.Distance(positions[i], positions[i + 1]) >= threshold) continue;
            var right = i + 1 < SegmentCount && !broken[i + 1] && volumes[i + 1] > 0;
            var left = i > 0 && !broken[i - 1] && volumes[i - 1] > 0;
            if ((!right && !left) || (NodeCount == 3 && pinned[0] && pinned[1])) continue;
            var neighbour = right ? i + 1 : i - 1;
            var deleteNode = right ? i + 1 : i;
            var firstAffected = right ? i : i - 1;
            var oldMomentum = Momentum(firstAffected, firstAffected + 2);
            var terminalCenter = TerminalCenter;
            var touchesTerminal = TerminalVolume > 0 && (i == SegmentCount - 1 || neighbour == SegmentCount - 1);
            if (touchesTerminal)
            {
                var newPrevious = right ? positions[i] : positions[i - 1];
                // Keep the actual reservoir center; rotating the neck's attachment
                // on its sphere is swept by the following CCD proposal.
                if (pinned[1] || Vector3.Distance(newPrevious, terminalCenter) <=
                    Math.Cbrt(TerminalVolume * 3 / (4 * Math.PI)) + MinimumLength) continue;
            }
            var shortVolume = volumes[i];
            var mergedVolume = volumes[neighbour] + shortVolume;
            stresses[neighbour] = (stresses[neighbour] * volumes[neighbour] + stresses[i] * shortVolume) / mergedVolume;
            volumes[neighbour] = mergedVolume;
            for (var s = i; s < SegmentCount - 1; s++)
            {
                volumes[s] = volumes[s + 1]; brokenVolumes[s] = brokenVolumes[s + 1];
                stresses[s] = stresses[s + 1]; broken[s] = broken[s + 1];
            }
            volumes[SegmentCount - 1] = brokenVolumes[SegmentCount - 1] = stresses[SegmentCount - 1] = 0;
            broken[SegmentCount - 1] = false;
            for (var node = deleteNode; node < NodeCount - 1; node++)
            {
                positions[node] = positions[node + 1]; velocities[node] = velocities[node + 1];
                previousPositions[node] = previousPositions[node + 1];
            }
            activeNodes--;
            positions[NodeCount] = velocities[NodeCount] = Vector3.Zero;
            if (touchesTerminal)
            {
                var axis = Vector3.Normalize(terminalCenter - positions[NodeCount - 2]);
                positions[NodeCount - 1] = terminalCenter - axis *
                    (float)Math.Cbrt(TerminalVolume * 3 / (4 * Math.PI));
            }
            var newMomentum = Momentum(firstAffected, firstAffected + 1);
            double freeWeight = 0;
            for (var node = firstAffected; node <= firstAffected + 1; node++)
                if (!Pinned(node)) freeWeight += NodeVolumeWeight(node);
            if (freeWeight > 0)
            {
                var adjustment = (oldMomentum - newMomentum).ToVector3(1 / freeWeight);
                for (var node = firstAffected; node <= firstAffected + 1; node++)
                    if (!Pinned(node)) velocities[node] += adjustment;
            }
            LastRemeshMomentumError = (Momentum(firstAffected, firstAffected + 1) - oldMomentum).Length * Density;
            CoarsenedSegments++; CoarsenedVolume += shortVolume;
            removed++; i = Math.Max(-1, i - 2);
        }
        return removed;
    }

    private double NodeVolumeWeight(int node) =>
        (node > 0 ? volumes[node - 1] * 0.5 : 0) +
        (node < SegmentCount ? volumes[node] * 0.5 : 0) +
        (node == NodeCount - 1 ? TerminalVolume : 0);

    private Momentum3 Momentum(int first, int last)
    {
        var value = default(Momentum3);
        for (var node = first; node <= last; node++) value += new Momentum3(velocities[node], NodeVolumeWeight(node));
        return value;
    }

    private static double WorldResolution(Vector3 p) => Math.Max(Math.Abs(MathF.BitIncrement(p.X) - p.X),
        Math.Max(Math.Abs(MathF.BitIncrement(p.Y) - p.Y), Math.Abs(MathF.BitIncrement(p.Z) - p.Z)));

    private readonly struct Momentum3
    {
        private readonly double x, y, z;
        public Momentum3(Vector3 v, double weight) => (x, y, z) = (v.X * weight, v.Y * weight, v.Z * weight);
        private Momentum3(double x, double y, double z) => (this.x, this.y, this.z) = (x, y, z);
        public double Length => Math.Sqrt(x * x + y * y + z * z);
        public Vector3 ToVector3(double factor) => new((float)(x * factor), (float)(y * factor), (float)(z * factor));
        public static Momentum3 operator +(Momentum3 a, Momentum3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Momentum3 operator -(Momentum3 a, Momentum3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    }

    public FilmStepResult Advance(double seconds, Vector3 gravity)
    {
        LastStrainRejections = 0; LastRejectedSegment = -1;
        LastRejectedLengthBefore = LastRejectedLengthAfter = LastRejectedTrialSeconds = 0;
        LastStrainRejectionReason = "None";
        LastRequestedSeconds = double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
        LastSimulatedSeconds = 0; LastSubsteps = 0;
        LastStopReason = "Invalid input";
        if (!double.IsFinite(seconds) || seconds <= 0 || !Finite(gravity)) return default;
        LastStopReason = "Full requested step consumed";
        double elapsed = 0, moved = 0;
        var steps = 0;
        while (elapsed < seconds && steps < 32)
        {
            var dt = Math.Min(seconds - elapsed, 1.0 / 240);
            for (var i = 0; i < SegmentCount; i++)
            {
                if (broken[i] || volumes[i] <= 0) continue;
                var length = Length(i);
                var relativeSpeed = (velocities[i + 1] - velocities[i]).Length();
                if (relativeSpeed > 0) dt = Math.Min(dt, 0.05 * length / relativeSpeed);
            }
            if (!double.IsFinite(dt) || dt < 1e-7)
            { LastStopReason = seconds - elapsed < 1e-7 ? "Residual requested time below resolution" :
                "Relative-speed timestep below resolution"; break; }
            Array.Copy(volumes, savedVolumes, SegmentCount);
            Array.Copy(stresses, savedStresses, SegmentCount);
            var savedTerminal = TerminalVolume;
            var acceptedStep = false;
            var transferred = 0.0;
            for (var trial = 0; trial < 8 && dt >= 1e-7; trial++)
            {
                Array.Copy(savedVolumes, volumes, SegmentCount);
                Array.Copy(savedStresses, stresses, SegmentCount);
                TerminalVolume = savedTerminal;
                transferred = Redistribute(dt, gravity);
                if (!SolveVelocities(dt, gravity)) LastStopReason = "Block solve rejected";
                else if (!AcceptStrain(dt)) LastStopReason = "Strain trial rejected";
                else { acceptedStep = true; LastStopReason = "Full requested step consumed"; break; }
                dt *= 0.5;
            }
            if (!acceptedStep)
            {
                Array.Copy(savedVolumes, volumes, SegmentCount);
                Array.Copy(savedStresses, stresses, SegmentCount); TerminalVolume = savedTerminal;
                break;
            }
            moved += transferred;
            for (var i = 0; i < NodeCount; i++) velocities[i] = solved[i];
            for (var i = 0; i < NodeCount; i++)
                if (!Pinned(i)) positions[i] += velocities[i] * (float)dt;
            for (var i = 0; i < SegmentCount; i++)
            {
                if (broken[i] || volumes[i] <= 0) continue;
                var length = Length(i);
                var radius = Math.Sqrt(volumes[i] / (Math.PI * length));
                var direction = (positions[i + 1] - positions[i]) / (float)length;
                var extending = Vector3.Dot(velocities[i + 1] - velocities[i], direction) > 0;
                if (radius < NeckRadius && (extending || stresses[i] > ElasticModulus))
                { brokenVolumes[i] += volumes[i]; volumes[i] = 0; stresses[i] = 0; broken[i] = true; }
            }
            elapsed += dt; steps++;
        }
        LastSimulatedSeconds = elapsed; LastSubsteps = steps;
        if (steps >= 32 && elapsed < seconds) LastStopReason = "Bounded substeps consumed";
        InternalTransferredVolume += moved;
        return new(elapsed, Math.Max(0, seconds - elapsed), moved, 0, steps);
    }

    private double Redistribute(double dt, Vector3 gravity)
    {
        Array.Clear(outgoing); Array.Clear(volumeDelta);
        for (var i = 0; i < SegmentCount; i++)
        {
            var radius = Math.Sqrt(volumes[i] / (Math.PI * Length(i)));
            pressure[i] = radius > 0 && !broken[i] ? SurfaceTension / radius : 0;
        }
        for (var i = 0; i < SegmentCount - 1; i++)
        {
            flux[i] = 0;
            if (broken[i] || broken[i + 1] || volumes[i] <= 0 || volumes[i + 1] <= 0) continue;
            var length = 0.5 * (Length(i) + Length(i + 1));
            var area = Math.Min(volumes[i] / Length(i), volumes[i + 1] / Length(i + 1));
            flux[i] = area * area / (8 * Math.PI * viscosity * length) * (pressure[i] - pressure[i + 1]);
            outgoing[flux[i] >= 0 ? i : i + 1] += Math.Abs(flux[i]);
        }
        var moved = 0.0;
        for (var i = 0; i < SegmentCount - 1; i++)
        {
            var donor = flux[i] >= 0 ? i : i + 1;
            var receiver = donor == i ? i + 1 : i;
            var limit = outgoing[donor] > 0 ? Math.Min(1, 0.2 * volumes[donor] / (outgoing[donor] * dt)) : 0;
            var amount = Math.Abs(flux[i]) * dt * limit;
            volumeDelta[donor] -= amount; volumeDelta[receiver] += amount; moved += amount;
        }
        for (var i = 0; i < SegmentCount; i++) volumes[i] += volumeDelta[i];
        var last = SegmentCount - 1;
        if (TerminalVolume > 0 && !broken[last] && volumes[last] > 0)
        {
            var radius = Math.Cbrt(TerminalVolume * 3 / (4 * Math.PI));
            var length = Math.Max(MinimumLength, Length(last) * 0.5 + radius);
            var head = Density * Vector3.Dot(gravity, TerminalCenter - (positions[last] + positions[last + 1]) * 0.5f);
            var area = volumes[last] / Length(last);
            var rate = area * area / (8 * Math.PI * viscosity * length) *
                (pressure[last] - 2 * SurfaceTension / radius + head);
            var donor = rate >= 0 ? volumes[last] : TerminalVolume;
            var amount = Math.Min(0.2 * donor, Math.Abs(rate) * dt);
            if (rate >= 0) { volumes[last] -= amount; TerminalVolume += amount; }
            else { TerminalVolume -= amount; volumes[last] += amount; }
            moved += amount;
        }
        return moved;
    }

    private bool AcceptStrain(double dt)
    {
        for (var i = 0; i < NodeCount; i++) if (!Finite(solved[i]))
        { RecordStrainRejection(-1, 0, double.NaN, dt, "Nonfinite solved velocity"); return false; }
        for (var i = 0; i < SegmentCount; i++)
        {
            if (broken[i] || volumes[i] <= 0) continue;
            var a = Pinned(i) ? positions[i] : positions[i] + solved[i] * (float)dt;
            var b = Pinned(i + 1) ? positions[i + 1] : positions[i + 1] + solved[i + 1] * (float)dt;
            var before = Length(i); var after = Vector3.Distance(a, b);
            if (!float.IsFinite(after) || after < MinimumLength || Math.Abs(after / before - 1) > 0.1)
            {
                RecordStrainRejection(i, before, after, dt, !float.IsFinite(after) ? "Nonfinite trial length" :
                    after < MinimumLength ? "Trial length below minimum" : "Trial relative strain exceeds limit");
                return false;
            }
        }
        return true;
    }

    private void RecordStrainRejection(int segment, double before, double after, double dt, string reason)
    {
        LastStrainRejections++; LastRejectedSegment = segment;
        LastRejectedLengthBefore = before; LastRejectedLengthAfter = after;
        LastRejectedTrialSeconds = dt; LastStrainRejectionReason = reason;
    }

    private bool SolveVelocities(double dt, Vector3 gravity)
    {
        Array.Clear(forces); Array.Clear(masses); Array.Clear(blocks); Array.Clear(couplings);
        for (var i = 0; i < SegmentCount; i++)
        {
            if (broken[i] || volumes[i] <= 0) continue;
            var length = Length(i);
            var direction = (positions[i + 1] - positions[i]) / (float)length;
            var area = volumes[i] / length;
            var strainRate = Vector3.Dot(velocities[i + 1] - velocities[i], direction) / length;
            var decay = Math.Exp(-dt / relaxationSeconds);
            stresses[i] = Math.Clamp(stresses[i] * decay + 3 * ElasticModulus * relaxationSeconds * strainRate * (1 - decay),
                -MaximumStress, MaximumStress);
            var tension = SurfaceTension * Math.PI * Math.Sqrt(area / Math.PI) + area * stresses[i];
            var force = direction * (float)tension;
            forces[i] += force; forces[i + 1] -= force;
            var mass = Density * volumes[i] * 0.5;
            masses[i] += mass; masses[i + 1] += mass;
            var axial = Matrix3.Outer(direction);
            // Axial viscous drag is objective for infinitesimal rigid rotation. Positive
            // transverse tension stiffness is implicit. The capillary axial derivative is
            // negative (-T/2L at fixed V), so it remains explicit with accepted-step strain
            // control instead of pretending the full capillary Hessian is positive definite.
            var coefficient = axial * (dt * 3 * viscosity * area / length) +
                (Matrix3.Identity - axial) * (dt * dt * Math.Max(0, tension) / length);
            blocks[i] += coefficient; blocks[i + 1] += coefficient;
            couplings[i] = coefficient * -1;
        }
        masses[NodeCount - 1] += Density * TerminalVolume; // Terminal reservoir is the actual pendant gravity load.
        for (var i = 0; i < NodeCount; i++)
        {
            if (masses[i] <= 0 && !Pinned(i))
            {
                // Zero-inventory nodes carry no inertia or gravity. They cannot form
                // ghost free-fall particles or poison velocity/precision diagnostics.
                blocks[i] = Matrix3.Identity; rhs[i] = Vector3.Zero;
                continue;
            }
            var mass = Math.Max(1e-12, masses[i]);
            blocks[i] += Matrix3.Identity * mass;
            rhs[i] = velocities[i] * (float)mass + (gravity * (float)mass + forces[i]) * (float)dt;
        }
        for (var i = 0; i < NodeCount; i++)
        {
            if (!Pinned(i)) continue;
            if (i > 0) { rhs[i - 1] -= couplings[i - 1].Apply(velocities[i]); couplings[i - 1] = default; }
            if (i < NodeCount - 1) { rhs[i + 1] -= couplings[i].Apply(velocities[i]); couplings[i] = default; }
            blocks[i] = Matrix3.Identity; rhs[i] = velocities[i];
        }
        // Symmetric 3x3 block Thomas solve; small, fixed arrays, no allocations.
        if (!blocks[0].TryInverse(out inverseBlocks[0])) return false;
        for (var i = 1; i < NodeCount; i++)
        {
            var factor = couplings[i - 1] * inverseBlocks[i - 1];
            blocks[i] -= factor * couplings[i - 1];
            rhs[i] -= factor.Apply(rhs[i - 1]);
            if (!blocks[i].TryInverse(out inverseBlocks[i])) return false;
        }
        solved[NodeCount - 1] = inverseBlocks[NodeCount - 1].Apply(rhs[NodeCount - 1]);
        for (var i = NodeCount - 2; i >= 0; i--)
            solved[i] = inverseBlocks[i].Apply(rhs[i] - couplings[i].Apply(solved[i + 1]));
        return true;
    }

    public double Clear()
    {
        var retired = TotalVolume;
        Array.Clear(volumes); Array.Clear(brokenVolumes); Array.Clear(stresses); Array.Clear(broken);
        Array.Clear(positions); Array.Clear(velocities); Array.Clear(pinned);
        activeNodes = positions.Length;
        TerminalVolume = InternalTransferredVolume = LastRequestedSeconds = LastSimulatedSeconds = 0;
        LastSubsteps = 0;
        CoarsenedSegments = 0; CoarsenedVolume = LastRemeshMomentumError = 0;
        LastStopReason = "Not advanced";
        LastStrainRejections = 0; LastRejectedSegment = -1;
        LastRejectedLengthBefore = LastRejectedLengthAfter = LastRejectedTrialSeconds = 0;
        LastStrainRejectionReason = "None";
        return retired;
    }

    private readonly struct Matrix3
    {
        private readonly double a, b, c, d, e, f, g, h, i;
        private Matrix3(double a, double b, double c, double d, double e, double f, double g, double h, double i)
            => (this.a, this.b, this.c, this.d, this.e, this.f, this.g, this.h, this.i) = (a, b, c, d, e, f, g, h, i);
        public static Matrix3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);
        public static Matrix3 Outer(Vector3 v) => new(v.X * v.X, v.X * v.Y, v.X * v.Z,
            v.Y * v.X, v.Y * v.Y, v.Y * v.Z, v.Z * v.X, v.Z * v.Y, v.Z * v.Z);
        public Vector3 Apply(Vector3 v) => new((float)(a * v.X + b * v.Y + c * v.Z),
            (float)(d * v.X + e * v.Y + f * v.Z), (float)(g * v.X + h * v.Y + i * v.Z));
        public bool TryInverse(out Matrix3 inverse)
        {
            var determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (!double.IsFinite(determinant) || determinant <= 0)
            { inverse = default; return false; }
            inverse = new Matrix3(e * i - f * h, c * h - b * i, b * f - c * e,
                f * g - d * i, a * i - c * g, c * d - a * f,
                d * h - e * g, b * g - a * h, a * e - b * d) * (1 / determinant);
            return true;
        }
        public static Matrix3 operator +(Matrix3 x, Matrix3 y) => new(x.a + y.a, x.b + y.b, x.c + y.c,
            x.d + y.d, x.e + y.e, x.f + y.f, x.g + y.g, x.h + y.h, x.i + y.i);
        public static Matrix3 operator -(Matrix3 x, Matrix3 y) => x + y * -1;
        public static Matrix3 operator *(Matrix3 x, double y) => new(x.a * y, x.b * y, x.c * y,
            x.d * y, x.e * y, x.f * y, x.g * y, x.h * y, x.i * y);
        public static Matrix3 operator *(Matrix3 x, Matrix3 y) => new(
            x.a * y.a + x.b * y.d + x.c * y.g, x.a * y.b + x.b * y.e + x.c * y.h, x.a * y.c + x.b * y.f + x.c * y.i,
            x.d * y.a + x.e * y.d + x.f * y.g, x.d * y.b + x.e * y.e + x.f * y.h, x.d * y.c + x.e * y.f + x.f * y.i,
            x.g * y.a + x.h * y.d + x.i * y.g, x.g * y.b + x.h * y.e + x.i * y.h, x.g * y.c + x.h * y.f + x.i * y.i);
    }

    private double Length(int i) => Math.Max(MinimumLength, Vector3.Distance(positions[i], positions[i + 1]));
    private bool Pinned(int i) => (i == 0 && pinned[0]) || (i == NodeCount - 1 && pinned[1]);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
