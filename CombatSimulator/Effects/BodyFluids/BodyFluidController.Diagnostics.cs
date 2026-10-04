// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;
using CombatSimulator.Core;
using CombatSimulator.Effects.BodyFluids.Surface;

namespace CombatSimulator.Effects.BodyFluids;

public sealed partial class BodyFluidController
{
    private float surfaceProbeTime;
    private bool diagnosticBodySurface;
    private long surfaceProbeDeadline;
    private long nextPoseFailureLog;
    private uint diagnosticGeneration;
    private FluidSurfaceAnchor[] diagnosticTriangles = Array.Empty<FluidSurfaceAnchor>();
    private long nextTraceSample;
    private int traceSamplesRemaining;

    /// <summary>Finite observation of this game's production state; never drives a separate simulation.</summary>
    public void BeginRuntimeTrace()
    {
        lock (gate)
        {
            traceSamplesRemaining = 12;
            nextTraceSample = Environment.TickCount64;
        }
    }

    private void SampleRuntimeTrace()
    {
        if (traceSamplesRemaining <= 0 || Environment.TickCount64 < nextTraceSample) return;
        nextTraceSample = Environment.TickCount64 + 1000;
        traceSamplesRemaining--;
        log.Info($"Fluid live trace ({12 - traceSamplesRemaining}/12): {Describe()}");
    }

    /// <summary>Production pose/surface path, no emission and no separate test simulation.</summary>
    public void BeginSurfaceProbe(bool includeBody = false, bool applyRaceDeformation = true)
    {
        lock (gate)
        {
            if (disposed || !Services.ClientState.IsLoggedIn) return;
            ClearCore();
            diagnosticBodySurface = includeBody;
            surface.FaceOnlyCapture = !includeBody;
            surface.ApplyRaceDeformation = applyRaceDeformation;
            config.BodyFluidsEnabled = enabled = true;
            renderer.SetEnabled(true);
            surfaceProbeTime = 30;
            surfaceProbeDeadline = Environment.TickCount64 + 30000;
            nextPoseFailureLog = 0;
        }
    }

    private void LogUnavailableDiagnosticPose()
    {
        if (surfaceProbeTime <= 0 || Environment.TickCount64 < nextPoseFailureLog) return;
        nextPoseFailureLog = Environment.TickCount64 + 5000;
        log.Info($"Fluid surface probe waiting: {surface.CapturePoseFailureReason}; {surface.Status}");
    }

    public void PrepareMaterialProbe()
    {
        lock (gate)
        {
            if (surfaceProbeTime <= 0) ClearCore();
            manualEmission = koEmission = false;
        }
    }

    public bool ValidateLipAnchor()
    {
        lock (gate) return !disposed && surface.ValidateCurrentLipAnchor();
    }

    private void ExpireDiagnosticPreviews()
    {
        var now = Environment.TickCount64;
        if (surfaceProbeDeadline != 0 && now >= surfaceProbeDeadline)
        {
            surfaceProbeTime = 0;
            surfaceProbeDeadline = 0;
            diagnosticTriangles = Array.Empty<FluidSurfaceAnchor>();
            renderer.Clear();
        }
        if (visualProbeDeadline != 0 && now >= visualProbeDeadline)
        {
            visualProbeTime = 0;
            visualProbeDeadline = 0;
            renderer.Clear();
        }
    }

    private void DrawSurfaceProbe(float dt)
    {
        surfaceProbeTime = MathF.Max(0, surfaceProbeTime - dt);
        geometry.Reset();
        if (surfaceProbeTime == 0) { renderer.Clear(); return; }
        if (diagnosticGeneration != surface.Generation)
        {
            diagnosticGeneration = surface.Generation;
            diagnosticTriangles = diagnosticBodySurface ? surface.GetBodyDiagnosticTriangles(1024) : surface.GetDiagnosticTriangles(128);
            log.Info($"Fluid surface topology: {surface.DescribeTopology()}");
        }
        var faceColor = new Vector4(0.15f, 0.9f, 0.6f, 0.85f);
        foreach (var anchor in diagnosticTriangles)
        {
            if (!surface.TryGetDiagnosticTriangle(anchor, out var a, out var b, out var c, out var normal)) continue;
            // Thin lines make fitting/animation errors visible, without painting over the face.
            var offset = normal * (diagnosticBodySurface ? 0.00008f : 0.0007f);
            if (diagnosticBodySurface)
            {
                AppendSurfaceLine(a + offset, b + offset, normal, faceColor);
                AppendSurfaceLine(b + offset, c + offset, normal, faceColor);
                AppendSurfaceLine(c + offset, a + offset, normal, faceColor);
            }
            else
            {
                geometry.AddTube(a + offset, b + offset, 0.00018f, faceColor);
                geometry.AddTube(b + offset, c + offset, 0.00018f, faceColor);
                geometry.AddTube(c + offset, a + offset, 0.00018f, faceColor);
            }
        }
        if (surface.TryGetMouthAnchor(out var lip) && surface.TryEvaluate(lip, out var sample))
        {
            geometry.AddEllipsoid(sample.Position + sample.Normal * 0.001f, 0.002f, new Vector4(1, 0.25f, 0.1f, 1));
            geometry.AddTube(sample.Position, sample.Position + sample.Normal * 0.015f, 0.0003f, new Vector4(1, 0.25f, 0.1f, 1));
        }
        renderer.SubmitFrame(geometry.Vertices, shaded: false);
    }

    private void AppendSurfaceLine(Vector3 a, Vector3 b, Vector3 normal, Vector4 color)
    {
        var width = Vector3.Cross(normal, b - a);
        if (width.LengthSquared() <= 1e-14f) return;
        width = Vector3.Normalize(width) * 0.00018f;
        geometry.AddTriangle(new(a - width, normal, color), new(b - width, normal, color), new(b + width, normal, color));
        geometry.AddTriangle(new(a - width, normal, color), new(b + width, normal, color), new(a + width, normal, color));
    }
}
