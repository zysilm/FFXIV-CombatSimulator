// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;

namespace CombatSimulator.Effects.BodyFluids.Simulation;

/// <summary>
/// Conservative reduced footprint dynamics for one caller-validated, locally planar ground patch.
/// The caller supplies the supported radius; this component never discovers terrain or crosses gaps.
/// Slopes, steps and connected ground cells require SurfaceFilm or another local transport model.
/// Parameters are artistic wetting calibration, not measured saliva/terrain properties.
/// </summary>
public sealed class GroundSpreading
{
    private double viscosity = 0.18;
    private const double SurfaceTension = 0.055;
    private const double EquilibriumContactAngle = 0.055; // rad, shallow-drop approximation.
    private const double ContactLineLogScale = 12;
    private const double MaximumVolume = 20e-6;
    private const double MinimumRadius = 0.0005;
    private const int MaximumSubsteps = 8;
    public double Volume { get; private set; }
    public double Radius { get; private set; } = MinimumRadius;
    public double SupportedRadius { get; private set; } = MinimumRadius;
    public double Area => Math.PI * Radius * Radius;
    public double AverageThickness => Volume / Area;
    public double CenterThickness => 2 * AverageThickness;
    public bool AtSupportBoundary => Radius >= SupportedRadius * (1 - 1e-8);

    /// <summary>Artistic contact-line viscosity in Pa.s; affects spreading rate, never the retained volume.</summary>
    public void SetMaterial(double viscosityPaSeconds)
        => viscosity = double.IsFinite(viscosityPaSeconds) ? Math.Clamp(viscosityPaSeconds, 0.03, 1.2) : 0.18;

    /// <summary>
    /// Increase only after the terrain service validates the complete newly exposed region.
    /// A smaller bound than the existing wet footprint is rejected; topology retirement is explicit.
    /// </summary>
    public bool SetSupportedRadius(double radius)
    {
        if (!double.IsFinite(radius) || radius < Radius || radius > 0.25) return false;
        SupportedRadius = radius;
        return true;
    }

    /// <summary>
    /// Optional initial impact footprint, applied once before liquid is added, within verified support.
    /// Further incoming drops increase inventory and drive subsequent spreading instead of jumping radius.
    /// </summary>
    public bool SetInitialRadius(double radius)
    {
        if (Volume != 0 || !double.IsFinite(radius) || radius < MinimumRadius || radius > SupportedRadius) return false;
        Radius = radius;
        return true;
    }

    public double AddVolume(double requested)
    {
        if (!double.IsFinite(requested) || requested <= 0) return 0;
        var accepted = Math.Min(requested, Math.Max(0, MaximumVolume - Volume));
        Volume += accepted;
        return accepted;
    }

    public double TakeVolume(double requested)
    {
        if (!double.IsFinite(requested) || requested <= 0) return 0;
        var taken = Math.Min(requested, Volume);
        Volume -= taken;
        return taken;
    }

    /// <summary>
    /// Parabolic cap h(r)=2V/(pi R²)*(1-r²/R²), whose area integral is exactly V.
    /// Renderer can evaluate arbitrarily fine geometry without changing simulation inventory.
    /// </summary>
    public double ThicknessAt(double normalizedRadius)
    {
        if (!double.IsFinite(normalizedRadius)) return 0;
        var r = Math.Abs(normalizedRadius);
        return r < 1 ? CenterThickness * (1 - r * r) : 0;
    }

    public FilmStepResult Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return default;
        var elapsed = 0.0;
        var steps = 0;
        while (elapsed < seconds && steps < MaximumSubsteps)
        {
            var equilibriumRadius = Math.Cbrt(4 * Volume / (Math.PI * EquilibriumContactAngle));
            var target = Math.Min(SupportedRadius, Math.Max(Radius, equilibriumRadius));
            if (Volume <= 0 || target - Radius < 1e-10)
                return new(seconds, 0, 0, 0, steps);
            var contactAngle = Math.Min(0.6, 4 * Volume / (Math.PI * Radius * Radius * Radius));
            // Cox-Voinov-inspired viscous contact-line law in its shallow, slow regime.
            // Contact angles outside that regime are bounded rather than extrapolated into splashes.
            var speed = SurfaceTension / (9 * viscosity * ContactLineLogScale) *
                Math.Max(0, contactAngle * contactAngle * contactAngle -
                    EquilibriumContactAngle * EquilibriumContactAngle * EquilibriumContactAngle);
            if (speed <= 0) return new(seconds, 0, 0, 0, steps);
            var dt = Math.Min(seconds - elapsed, Math.Min(1.0 / 60, 0.05 * Radius / speed));
            if (dt < 1e-7) break;
            Radius = Math.Min(target, Radius + speed * dt);
            elapsed += dt;
            steps++;
        }
        return new(elapsed, Math.Max(0, seconds - elapsed), 0, 0, steps);
    }

    public double Clear()
    {
        var retired = Volume;
        Volume = 0;
        Radius = SupportedRadius = MinimumRadius;
        return retired;
    }
}
