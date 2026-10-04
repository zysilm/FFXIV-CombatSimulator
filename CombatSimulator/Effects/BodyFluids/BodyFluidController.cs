// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Diagnostics;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Core;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Effects.BodyFluids;

/// <summary>One read-only actor surface and bounded cosmetic runtime. Never writes poses or cameras.</summary>
public sealed partial class BodyFluidController : IDisposable
{
    private const float Step = 1f / 60f;
    private readonly object gate = new();
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly BoneTransformService bones;
    private readonly CharacterFluidSurface surface;
    private readonly SalivaRuntime simulation;
    private readonly WorldGeometryRenderer worldRenderer;
    private readonly WorldGeometryLayer renderer;
    private readonly WorldGeometryBuilder geometry = new();
    private readonly FluidGeometryBuilder fluidGeometry = new(WorldGeometryRenderer.MaxVertices);
    private readonly FluidTimingWindow frameworkTimings = new(), poseTimings = new();
    private float accumulatedTime, timedEmission, visualProbeTime;
    private long visualProbeDeadline;
    private Vector3 visualProbeOrigin;
    private bool manualEmission, enabled, disposed;
    private FluidDiagnosticView productionView;
    private long steps, poseFrames, frameworkFrame, capturedFrame, previousPoseTicks;
    private nint actorIdentity;
    private ulong objectIdentity;
    private uint territoryIdentity;
    public double LastCpuMilliseconds { get; private set; }
    public bool Emitting => manualEmission || timedEmission > 0;
    public string SurfaceStatus => $"{surface.Status}; {surface.LipStatus}; {surface.DeformationStatus}";
    public string RenderStatus => renderer.Status;

    public BodyFluidController(Configuration config, BoneTransformService bones, WorldGeometryRenderer worldRenderer, IPluginLog log)
    {
        this.config = config; this.bones = bones; this.worldRenderer = worldRenderer; this.log = log;
        surface = new CharacterFluidSurface(bones);
        simulation = new SalivaRuntime(surface, config);
        renderer = worldRenderer.CreateLayer("Saliva");
        bones.OnPosePrepared += OnPosePrepared;
    }

    public void Tick(float dt)
    {
        lock (gate)
        {
            var started = Stopwatch.GetTimestamp();
            var activeWork = false;
            try
            {
                if (disposed) return;
                ExpireDiagnosticPreviews();
                SampleRuntimeTrace();
                if (enabled != config.BodyFluidsEnabled)
                {
                    enabled = config.BodyFluidsEnabled;
                    config.ClampBodyFluids();
                    renderer.SetEnabled(enabled);
                    if (!enabled) ClearCore();
                }
                if (!enabled || renderer.IsSuspended) return;
                var player = Services.ObjectTable.LocalPlayer;
                if (!Services.ClientState.IsLoggedIn || player == null) { ClearCore(); return; }
                // Shape/cloth rebuilds retain world liquid, but another actor or
                // territory owns a different world. Retire it before replacing IDs.
                if (actorIdentity != 0 && (actorIdentity != player.Address ||
                    objectIdentity != player.GameObjectId || territoryIdentity != Services.ClientState.TerritoryType))
                { ClearCore(); return; }
                if (!Emitting && !HasLiquid()) return;
                activeWork = true;
                frameworkFrame++;
                actorIdentity = player.Address; objectIdentity = player.GameObjectId;
                territoryIdentity = Services.ClientState.TerritoryType;
                if (visualProbeTime <= 0) surface.UpdateActor(actorIdentity, objectIdentity, territoryIdentity);
                accumulatedTime = MathF.Min(accumulatedTime + Math.Clamp(dt, 0, .1f), Step * 2);
            }
            finally
            {
                if (activeWork) frameworkTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    public void Start(bool largeVisibilityPreview = false)
    {
        lock (gate)
        {
            if (disposed || !Services.ClientState.IsLoggedIn) return;
            if (visualProbeTime > 0 || surfaceProbeTime > 0) ClearCore();
            simulation.LargeVisibilityPreview = largeVisibilityPreview;
            productionView = FluidDiagnosticView.Composite;
            enabled = config.BodyFluidsEnabled = true;
            config.ClampBodyFluids();
            surface.IncludeBodySurface = true;
            surface.ApplyRaceDeformation = true;
            renderer.SetEnabled(true);
            if (renderer.IsSuspended) return;
            manualEmission = true;
        }
    }

    public void StopEmission()
    {
        lock (gate) { manualEmission = false; timedEmission = 0; simulation.Emitting = false; }
    }

    public void SetGeometryInspection(FluidDiagnosticView view)
    {
        lock (gate) productionView = view;
    }

    public void OnPlayerKo()
    {
        lock (gate)
            if (!disposed && surfaceProbeTime <= 0 && visualProbeTime <= 0 && config.BodyFluidsEnabled && config.BodyFluidsOnPlayerKo)
            { surface.IncludeBodySurface = true; surface.ApplyRaceDeformation = true; timedEmission = 10; }
    }

    public void BeginProbe() { lock (gate) worldRenderer.BeginProbe(); }

    public void BeginVisualProbe()
    {
        lock (gate)
        {
            var player = Services.ObjectTable.LocalPlayer;
            if (disposed || !Services.ClientState.IsLoggedIn || player == null) return;
            ClearCore();
            enabled = config.BodyFluidsEnabled = true;
            renderer.SetEnabled(true);
            if (renderer.IsSuspended) return;
            visualProbeOrigin = bones.GetBoneWorldPos(player.Address, "j_kao") ?? player.Position + Vector3.UnitY * 1.3f;
            visualProbeTime = 12; visualProbeDeadline = Environment.TickCount64 + 12000;
        }
    }

    public void Clear() { lock (gate) ClearCore(); }

    private void ClearCore()
    {
        manualEmission = false;
        productionView = FluidDiagnosticView.Composite;
        timedEmission = accumulatedTime = visualProbeTime = surfaceProbeTime = 0;
        visualProbeDeadline = surfaceProbeDeadline = 0;
        diagnosticTriangles = Array.Empty<FluidSurfaceAnchor>(); diagnosticGeneration = 0;
        simulation.Emitting = false; simulation.Clear();
        simulation.LargeVisibilityPreview = false;
        surface.Clear();
        surface.ApplyRaceDeformation = true;
        geometry.Reset(); fluidGeometry.Reset(); renderer.Clear();
        steps = poseFrames = 0;
        actorIdentity = 0; objectIdentity = 0;
        previousPoseTicks = frameworkFrame = capturedFrame = 0;
        frameworkTimings.Clear(); poseTimings.Clear();
    }

    private bool HasLiquid() => surfaceProbeTime > 0 || visualProbeTime > 0 || simulation.TotalVolume > 1e-15;

    private void OnPosePrepared()
    {
        lock (gate)
        {
            if (disposed || !enabled || renderer.IsSuspended || accumulatedTime <= 0 || capturedFrame == frameworkFrame) return;
            var player = Services.ObjectTable.LocalPlayer;
            if (!Services.ClientState.IsLoggedIn || player == null || player.Address != actorIdentity ||
                player.GameObjectId != objectIdentity || Services.ClientState.TerritoryType != territoryIdentity)
            { ClearCore(); return; }
            capturedFrame = frameworkFrame;
            var started = Stopwatch.GetTimestamp();
            try
            {
                var poseDelta = previousPoseTicks == 0 ? Step : (float)Stopwatch.GetElapsedTime(previousPoseTicks, started).TotalSeconds;
                previousPoseTicks = started;
                if (visualProbeTime > 0) { DrawLegacyGeometryProbe(poseDelta); return; }
                if (!surface.CapturePose(poseDelta))
                { LogUnavailableDiagnosticPose(); accumulatedTime = 0; renderer.Clear(); return; }
                poseFrames++;
                if (surfaceProbeTime > 0) { DrawSurfaceProbe(Math.Clamp(poseDelta, 0, .1f)); accumulatedTime = 0; return; }
                if (poseDelta > .15f) { accumulatedTime = 0; renderer.Clear(); return; }
                var count = 0;
                while (accumulatedTime + 1e-6f >= Step && count++ < 2)
                {
                    config.ClampBodyFluids();
                    simulation.Emitting = Emitting;
                    simulation.Advance(Step);
                    timedEmission = MathF.Max(0, timedEmission - Step);
                    accumulatedTime = MathF.Max(0, accumulatedTime - Step);
                    steps++;
                }
                fluidGeometry.Reset();
                simulation.AppendGeometry(fluidGeometry);
                if (simulation.LargeVisibilityPreview)
                    fluidGeometry.ExaggerateForVisibilityPreview(16, .003f, 4);
                else if (config.BodyFluidThicknessScale > 1)
                    fluidGeometry.ExaggerateForVisibilityPreview(config.BodyFluidThicknessScale, 0, config.BodyFluidThicknessScale);
                var material = config.CreateBodyFluidMaterial() with { DiagnosticView = productionView };
                // The requested large visibility preview also restores the original
                // material test's full reflection weight. Normal settings stay intact.
                if (simulation.LargeVisibilityPreview) material = material with { ReflectionStrength = 1 };
                renderer.SubmitFluidFrame(fluidGeometry.Vertices, material);
            }
            catch (Exception ex)
            {
                config.BodyFluidsEnabled = false;
                ClearCore();
                log.Error(ex, "Body fluids disabled after a runtime fault.");
            }
            finally
            {
                LastCpuMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                poseTimings.Add(LastCpuMilliseconds);
            }
        }
    }

    private void DrawLegacyGeometryProbe(float dt)
    {
        visualProbeTime = MathF.Max(0, visualProbeTime - Math.Clamp(dt, 0, .1f));
        accumulatedTime = 0; geometry.Reset();
        if (visualProbeTime > 8)
            geometry.AddSurfaceTriangle(new(-.8f, .8f, .5f), new(-.5f, .8f, .5f), new(-.65f, .5f, .5f), Vector3.UnitZ, new(1, 0, 1, 1));
        else if (visualProbeTime > 0)
        {
            geometry.AddEllipsoid(visualProbeOrigin + Vector3.UnitX * .2f, .025f, new(1, .12f, .12f, .85f));
            geometry.AddEllipsoid(visualProbeOrigin, .025f, new(.12f, .25f, 1, .85f));
            geometry.AddEllipsoid(visualProbeOrigin - Vector3.UnitX * .2f, .025f, new(.12f, 1, .2f, .85f));
        }
        renderer.SubmitFrame(geometry.Vertices, testSceneDepth: visualProbeTime <= 4, clipSpace: visualProbeTime > 8, diagnostic: true);
    }

    public string Describe()
    {
        lock (gate)
            return $"Body fluids: {(enabled ? "enabled" : "off")}, emitting={Emitting}, largeVisibilityPreview={simulation.LargeVisibilityPreview}, poseFrames={poseFrames}, steps={steps}, " +
                $"vertices={(surfaceProbeTime > 0 || visualProbeTime > 0 ? geometry.Count : fluidGeometry.Count)}, geometryOverflow={fluidGeometry.Overflowed}, view={productionView}, " +
                $"{fluidGeometry.DescribeVisibility()}, " +
                $"rivulets={simulation.RivuletsDrawn},visibleThickness={config.BodyFluidThicknessScale:F2}x, " +
                $"framework[{frameworkTimings.Describe()}],pose[{poseTimings.Describe()}], " +
                $"skinVertices={surface.SkinVerticesThisFrame},triangleTests={surface.TriangleTestsThisFrame},candidates={surface.BroadPhaseCandidatesThisFrame},boundsBuilt={surface.TriangleBoundsBuiltThisFrame},budgetHit={surface.BudgetExhausted},contactPending='{surface.ContactPendingReason}'; " +
                $"runtime={simulation.Status},volume error={simulation.ConservationError * 1e6:F5} ml; {simulation.InventoryDiagnostics}; {simulation.CapGeometryDiagnostics}; surface={SurfaceStatus}; render={renderer.Status}; " +
                $"GPU={worldRenderer.GpuTimingStatus}";
    }

    public void Dispose()
    {
        bones.OnPosePrepared -= OnPosePrepared;
        lock (gate)
        {
            if (disposed) return;
            ClearCore(); disposed = true;
            renderer.Dispose();
        }
    }
}
