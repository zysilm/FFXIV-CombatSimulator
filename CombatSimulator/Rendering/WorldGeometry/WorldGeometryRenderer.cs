// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using CombatSimulator.Core;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using Silk.NET.Direct3D11;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>
/// Plugin-owned pre-UI 3D triangle service. Eight isolated producers share one pair
/// of native hooks, one D3D pass and three bounded 32766-vertex render snapshots.
/// CPU geometry never changes the game camera, bones or scene update cadence.
/// </summary>
public sealed unsafe class WorldGeometryRenderer : IDisposable
{
    public const int MaxVertices = 32766;
    public const int MaxLayers = 8;
    private delegate void UiDelegate(AtkServer* server, bool flag);
    private delegate void TargetDelegate(ImmediateContext* context, RenderCommandSetTarget* command);
    private readonly object gate = new();
    private readonly IPluginLog log;
    private readonly LayerState?[] layers = new LayerState?[MaxLayers];
    private readonly Slot[] slots = new Slot[3];
    private readonly WorldTrianglePass pass = new();
    private readonly WorldFluidPass fluidPass = new();
    private readonly WorldGpuTiming gpuTiming = new();
    private Hook<UiDelegate>? uiHook;
    private Hook<TargetDelegate>? targetHook;
    private bool disposed, faulted, externalViewConflict, hooksEnabled;
    private int probeRemaining;
    private long probeDeadline, nextCompatibilityCheck;
    private long submitted, drawn, skipped, drawnBatches;
    private bool hasQueuedFrame;
    private bool cameraEyeLogged;
    private uint lastQueuedFrame;
    private string status = "Off";
    private readonly double[] submitWaits = new double[256];
    private int submitWaitCursor, submitWaitWindow;
    private long submitWaitCount;
    private double submitWaitLast, submitWaitMax;

    internal sealed class LayerState
    {
        internal readonly string Name;
        internal readonly int Capacity;
        internal WorldVertex[]? Vertices;
        internal FluidVertex[]? FluidVertices;
        internal bool Fluid;
        internal FluidMaterial Material;
        internal int Count, Generation;
        internal bool Enabled, Disposed, TestSceneDepth = true, ClipSpace, Diagnostic, Shaded = true;
        internal int LastDrawMode = -1;
        internal int LastOpticalShapeMode = -1;
        internal bool ProjectionLogged;
        internal string Status = "Off";
        internal WorldDepthProbe? DepthProbe;
        internal LayerState(string name, int capacity)
        { Name = name; Capacity = capacity; Vertices = new WorldVertex[capacity]; }
    }

    private struct Batch
    {
        public LayerState Layer;
        public int Generation, Start, Count;
        public bool TestSceneDepth, ClipSpace, Diagnostic, Shaded, BudgetLimited;
        public bool Fluid;
        public FluidMaterial Material;
    }

    private sealed class Slot
    {
        public readonly WorldVertex[] Vertices = new WorldVertex[MaxVertices];
        public readonly FluidVertex[] FluidVertices = new FluidVertex[MaxVertices];
        public readonly Batch[] Batches = new Batch[MaxLayers];
        public int BatchCount;
        public nint Marker, Target;
        public Matrix4x4 ViewProjection;
        public Vector3 CameraPosition;
        public uint Width, Height;
    }

    public string Status => Volatile.Read(ref status);
    public bool IsSuspended => Volatile.Read(ref externalViewConflict) || Volatile.Read(ref faulted) || Volatile.Read(ref disposed);
    public long DrawnFrames => Interlocked.Read(ref drawn);
    public long DrawnBatches => Interlocked.Read(ref drawnBatches);
    /// <summary>GPU measurements are asynchronous; NaN percentiles mean no valid samples yet.</summary>
    public string GpuTimingStatus { get { lock (gate) return gpuTiming.Status; } }
    public void ResetGpuTiming() { lock (gate) gpuTiming.ResetStatistics(); }
    public const string RefractionDiagnosticLegend = "path: gray=original local single-interface refraction, purple=offscreen projection, red=foreground rejection; offset: dark blue=0px, green=1px, yellow=4px, red=16px+ (final sampled UV displacement)";
    /// <summary>Initialization and submit-lock waiting are distinct CPU scopes; formatting occurs only on demand.</summary>
    public string CpuTimingStatus
    {
        get
        {
            lock (gate)
            {
                Span<double> ordered = stackalloc double[256];
                submitWaits.AsSpan(0, submitWaitWindow).CopyTo(ordered);
                ordered = ordered[..submitWaitWindow]; ordered.Sort();
                var p95 = submitWaitWindow == 0 ? double.NaN : ordered[Math.Max(0, (int)Math.Ceiling(submitWaitWindow * 0.95) - 1)];
                var last = submitWaitCount == 0 ? double.NaN : submitWaitLast;
                var maximum = submitWaitCount == 0 ? double.NaN : submitWaitMax;
                return $"renderCPU[{fluidPass.InitializationTimingStatus}]; SubmitFluid renderer-lock wait count={submitWaitCount}, window={submitWaitWindow}/256, last={last:F3}ms, p95={p95:F3}ms, max={maximum:F3}ms; scopes exclude producer simulation/model/geometry CPU";
            }
        }
    }

    /// <summary>Main-thread read-only optical proof placement. No game camera or render state is changed.</summary>
    public bool TryGetPreviewOrigin(Vector3 reference, out Vector3 origin)
    {
        origin = default;
        if (IsSuspended || !WorldGeometryBuilder.Finite(reference)) return false;
        var mgr = GameCameraManager.Instance();
        var camera = mgr == null || mgr->Camera == null ? null : &mgr->Camera->CameraBase.SceneCamera;
        if (camera == null || camera->RenderCamera == null || camera->RenderCamera->StandardZ) return false;
        var gameView = camera->ViewMatrix; var gameProjection = camera->RenderCamera->ProjectionMatrix;
        var affineView = *(Matrix4x4*)&gameView;
        affineView.M14 = affineView.M24 = affineView.M34 = 0; affineView.M44 = 1;
        if (!Matrix4x4.Invert(affineView, out var inverseView)) return false;
        var eye = inverseView.Translation;
        var right = Vector3.TransformNormal(Vector3.UnitX, inverseView);
        var towardEye = eye - reference;
        if (!WorldGeometryBuilder.Finite(eye) || !WorldGeometryBuilder.Finite(right)
            || !WorldGeometryBuilder.Finite(towardEye) || right.LengthSquared() < 1e-8f || towardEye.LengthSquared() < 1e-8f) return false;
        var candidate = reference + Vector3.Normalize(right) * 0.22f + Vector3.Normalize(towardEye) * 0.10f;
        if (!WorldGeometryBuilder.Finite(candidate)) return false;
        var clip = Vector4.Transform(new Vector4(candidate, 1), affineView * *(Matrix4x4*)&gameProjection);
        if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) || !float.IsFinite(clip.Z)
            || !float.IsFinite(clip.W) || clip.W <= 1e-6f || clip.Z / clip.W < 0 || clip.Z / clip.W > 1) return false;
        var uv = new Vector2(clip.X / clip.W * 0.5f + 0.5f, 0.5f - clip.Y / clip.W * 0.5f);
        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || uv.X < 0.03f || uv.X > 0.97f || uv.Y < 0.03f || uv.Y > 0.97f) return false;
        origin = candidate; return true;
    }

    public WorldGeometryRenderer(IGameInteropProvider interop, ISigScanner scanner, IPluginLog log)
    {
        this.log = log;
        for (var i = 0; i < slots.Length; i++) slots[i] = new Slot();
        try
        {
            uiHook = interop.HookFromAddress<UiDelegate>((nint)AtkServer.MemberFunctionPointers.ProcessUICommandsAlt, BeforeUi);
            targetHook = interop.HookFromAddress<TargetDelegate>((nint)ImmediateContext.MemberFunctionPointers.DoSetTargetCommand, SetTarget);
        }
        catch (Exception ex)
        { faulted = true; status = "Render hooks unavailable"; log.Error(ex, "World geometry: rendering hooks unavailable"); }
    }

    public WorldGeometryLayer CreateLayer(string name, int capacity = MaxVertices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (capacity < 3 || capacity > MaxVertices) throw new ArgumentOutOfRangeException(nameof(capacity));
        capacity -= capacity % 3;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var free = -1;
            for (var i = 0; i < layers.Length; i++)
            {
                if (layers[i] == null) { if (free < 0) free = i; }
                else if (layers[i]!.Name.Equals(name, StringComparison.Ordinal))
                    throw new ArgumentException("A world-geometry layer with this name already exists", nameof(name));
            }
            if (free < 0) throw new InvalidOperationException("At most eight world-geometry layers can be registered");
            var state = new LayerState(name, capacity);
            layers[free] = state;
            return new WorldGeometryLayer(this, state);
        }
    }

    internal string GetLayerStatus(LayerState layer)
    {
        lock (gate)
        {
            if (layer.Disposed || disposed) return "Disposed";
            if (IsSuspended) return status;
            if (!layer.Enabled) return "Off";
            return layer.Count == 0 ? "Waiting for geometry" : layer.Status;
        }
    }

    internal void SetLayerEnabled(LayerState layer, bool value)
    {
        lock (gate)
        {
            if (disposed || layer.Disposed) return;
            if (value) RefreshCompatibility(); // New enabling requests must not wait for the one-second timer.
            if (layer.Enabled != value)
            {
                layer.Enabled = value;
                layer.Generation++;
                layer.Status = value ? "Waiting for main-view submission" : "Off";
            }
            RefreshHooks();
        }
    }

    internal void Submit(LayerState layer, ReadOnlySpan<WorldVertex> vertices,
        bool testSceneDepth, bool clipSpace, bool diagnostic, bool shaded)
    {
        lock (gate)
        {
            if (disposed || faulted || layer.Disposed || layer.Vertices == null) return;
            var count = Math.Min(vertices.Length, layer.Capacity);
            count -= count % 3;
            if (count == 0) { ClearLayer(layer); return; }
            if (layer.Diagnostic != diagnostic)
            {
                layer.DepthProbe?.Dispose(); layer.DepthProbe = null;
                layer.LastDrawMode = -1; layer.ProjectionLogged = false;
            }
            vertices[..count].CopyTo(layer.Vertices);
            var hadGeometry = layer.Count > 0;
            if (layer.Fluid) layer.Generation++;
            layer.Fluid = false;
            layer.Count = count;
            layer.TestSceneDepth = testSceneDepth;
            layer.ClipSpace = clipSpace;
            layer.Diagnostic = diagnostic;
            layer.Shaded = shaded;
            if (!hadGeometry) layer.Status = "Waiting for main-view submission";
            // Hook changes are performed only by main-thread Tick/SetEnabled/BeginProbe.
        }
    }

    internal void SubmitFluid(LayerState layer, ReadOnlySpan<FluidVertex> vertices, FluidMaterial material, bool testSceneDepth)
    {
        if (!material.IsValid) throw new ArgumentOutOfRangeException(nameof(material));
        var count = Math.Min(vertices.Length, layer.Capacity);
        count -= count % 3;
        for (var i = 0; i < count; i++)
            if (!FluidGeometryBuilder.Valid(vertices[i])) throw new ArgumentException("Fluid surface contains invalid vertex data", nameof(vertices));
        var lockRequested = Stopwatch.GetTimestamp();
        lock (gate)
        {
            submitWaitLast = Stopwatch.GetElapsedTime(lockRequested).TotalMilliseconds;
            submitWaits[submitWaitCursor] = submitWaitLast; submitWaitCursor = (submitWaitCursor + 1) % submitWaits.Length;
            submitWaitWindow = Math.Min(submitWaits.Length, submitWaitWindow + 1); submitWaitCount++;
            submitWaitMax = Math.Max(submitWaitMax, submitWaitLast);
            if (disposed || faulted || layer.Disposed) return;
            if (count == 0) { ClearLayer(layer); return; }
            layer.FluidVertices ??= new FluidVertex[layer.Capacity];
            vertices[..count].CopyTo(layer.FluidVertices);
            if (!layer.Fluid) layer.Generation++;
            if (layer.Material.DiagnosticView != material.DiagnosticView) layer.LastDrawMode = -1;
            layer.Fluid = true; layer.Material = material; layer.Count = count;
            layer.TestSceneDepth = testSceneDepth; layer.ClipSpace = layer.Diagnostic = false;
            layer.Status = "Waiting for liquid scene snapshot";
        }
    }

    internal void ClearLayer(LayerState layer)
    {
        lock (gate)
        {
            if (layer.Disposed) return;
            layer.Count = 0; layer.Generation++;
            layer.LastDrawMode = -1; layer.ProjectionLogged = false;
            layer.DepthProbe?.Dispose(); layer.DepthProbe = null;
            layer.Status = layer.Enabled ? "Waiting for geometry" : "Off";
        }
    }

    internal void RemoveLayer(LayerState layer)
    {
        lock (gate)
        {
            if (layer.Disposed) return;
            ClearLayer(layer);
            layer.Disposed = true; layer.Enabled = false; layer.Vertices = null; layer.FluidVertices = null;
            for (var i = 0; i < layers.Length; i++) if (ReferenceEquals(layers[i], layer)) layers[i] = null;
            RefreshHooks();
        }
    }

    /// <summary>Called once per Framework update on the plugin/main thread.</summary>
    public void Tick()
    {
        lock (gate)
        {
            if (disposed) return;
            if (Environment.TickCount64 >= nextCompatibilityCheck) RefreshCompatibility();
            if (probeRemaining > 0 && Environment.TickCount64 >= probeDeadline)
            {
                probeRemaining = 0;
                status = "Probe timed out before 120 observations; see log";
                log.Info($"World geometry probe timeout: queued={submitted}, drawn={drawn}, skipped={skipped}");
            }
            RefreshHooks();
        }
    }

    private void RefreshCompatibility()
    {
        var conflict = false;
        foreach (var plugin in Services.PluginInterface.InstalledPlugins)
            if (plugin.IsLoaded && plugin.InternalName.Equals("DynamicPortrait", StringComparison.OrdinalIgnoreCase))
            { conflict = true; break; }
        nextCompatibilityCheck = Environment.TickCount64 + 1000;
        if (externalViewConflict == conflict) return;
        externalViewConflict = conflict;
        if (conflict)
        {
            probeRemaining = 0;
            // Invalidate queued snapshots but retain each producer's latest geometry.
            foreach (var layer in layers) if (layer != null) layer.Generation++;
            status = "Paused: DynamicPortrait is loaded; main-view identity is not yet verified";
        }
        else { hasQueuedFrame = false; status = "Waiting for main-view submission"; }
    }

    private bool HasGeometry()
    {
        foreach (var layer in layers)
            if (layer != null && !layer.Disposed && layer.Enabled && layer.Count > 0) return true;
        return false;
    }

    private void RefreshHooks()
    {
        var hasFluid = false;
        foreach (var layer in layers)
            if (layer != null && !layer.Disposed && layer.Enabled && layer.Fluid && layer.Count > 0) hasFluid = true;
        if (!hasFluid || disposed || faulted || externalViewConflict) { fluidPass.Dispose(); gpuTiming.Dispose(); }
        var want = !disposed && !faulted && !externalViewConflict && (HasGeometry() || probeRemaining > 0);
        if (want == hooksEnabled) return;
        if (want) { targetHook?.Enable(); uiHook?.Enable(); hasQueuedFrame = false; }
        else
        {
            uiHook?.Disable(); targetHook?.Disable();
            // Queued commands still execute legitimately while our observer is off.
            // Their addresses must not strand all three managed slots on re-enable.
            foreach (var slot in slots) { slot.Marker = 0; slot.BatchCount = 0; }
            hasQueuedFrame = false;
        }
        hooksEnabled = want;
    }

    public void ClearAll()
    {
        lock (gate)
        {
            foreach (var layer in layers) if (layer != null) ClearLayer(layer);
            probeRemaining = 0;
            foreach (var slot in slots) { slot.BatchCount = 0; Array.Clear(slot.Batches); }
        }
    }

    /// <summary>Bounded shared capability probe; does not change any producer's geometry.</summary>
    public void BeginProbe()
    {
        lock (gate)
        {
            if (disposed || faulted) return;
            RefreshCompatibility();
            if (externalViewConflict) { status = "Paused: DynamicPortrait is loaded; probe skipped"; RefreshHooks(); return; }
            probeRemaining = 120;
            probeDeadline = Environment.TickCount64 + 10000;
            status = "Probing pre-UI scene depth (120 frames)";
            RefreshHooks();
        }
    }

    private void BeforeUi(AtkServer* server, bool flag)
    {
        try
        {
            lock (gate)
                if (!disposed && !faulted && !externalViewConflict && (HasGeometry() || probeRemaining > 0)) Enqueue();
        }
        catch (Exception ex) { Fail(ex); }
        uiHook!.Original(server, flag);
    }

    private void Enqueue()
    {
        var framework = Framework.Instance();
        if (framework == null || (hasQueuedFrame && lastQueuedFrame == framework->FrameCounter)) return;
        var device = Device.Instance();
        var target = device == null || device->SwapChain == null ? null : device->SwapChain->BackBuffer;
        if (target == null || target->MipRenderTargets == null
            || target->MipRenderTargets->D3D11RenderTargetViewOrDepthStencilView == null
            || target->ActualWidth == 0 || target->ActualHeight == 0)
        { status = "No main backbuffer"; return; }
        var mgr = GameCameraManager.Instance();
        var camera = mgr == null || mgr->Camera == null ? null : &mgr->Camera->CameraBase.SceneCamera;
        if (camera == null || camera->RenderCamera == null || camera->RenderCamera->StandardZ)
        { status = "No supported reverse-Z main camera"; return; }
        var locals = ThreadLocals.ThreadLocalInstance();
        var context = locals == null ? null : locals->GraphicsKernelContext;
        if (context == null) return;
        Slot? slot = null;
        foreach (var candidate in slots) if (candidate.Marker == 0) { slot = candidate; break; }
        if (slot == null) { skipped++; return; }
        CollectBatches(slot);
        if (slot.BatchCount == 0 && probeRemaining == 0) return;
        var marker = (RenderCommandSetTarget*)context->AllocateCommand((ulong)sizeof(RenderCommandSetTarget));
        if (marker == null) return;
        *marker = default;
        marker->RenderTargetCount = 1;
        marker->RenderTargets[0] = target;
        var view = camera->ViewMatrix;
        var projection = camera->RenderCamera->ProjectionMatrix;
        // Proven in-game: Scene.ViewMatrix has a padding/zero homogeneous column.
        // Repair only our copied affine matrix, never the game's camera matrices.
        var affineView = *(Matrix4x4*)&view;
        affineView.M14 = affineView.M24 = affineView.M34 = 0;
        affineView.M44 = 1;
        // Derive the eye in exactly the world space used by our proven view
        // transform. RenderCamera.Origin is not a verified world-space eye.
        if (!Matrix4x4.Invert(affineView, out var inverseView)
            || !WorldGeometryBuilder.Finite(inverseView.Translation))
        { status = "Main scene view is not invertible; drawing skipped"; skipped++; return; }
        slot.ViewProjection = affineView * *(Matrix4x4*)&projection;
        slot.CameraPosition = inverseView.Translation;
        var origin = camera->RenderCamera->Origin;
        if (!cameraEyeLogged)
        {
            cameraEyeLogged = true;
            var renderOrigin = new Vector3(origin.X, origin.Y, origin.Z);
            var eyeInView = Vector3.Transform(slot.CameraPosition, affineView);
            log.Info($"World geometry camera: inverseSceneViewEye={slot.CameraPosition}, renderOrigin={renderOrigin}, difference={renderOrigin - slot.CameraPosition}, eyeTransformedToView={eyeInView}, viewDeterminant={affineView.GetDeterminant():G9}; shader uses inverseSceneViewEye; RenderCamera.Origin semantics unverified");
        }
        slot.Width = target->ActualWidth; slot.Height = target->ActualHeight;
        slot.Target = (nint)target; slot.Marker = (nint)marker;
        submitted++;
        context->PushBackCommand(marker);
        lastQueuedFrame = framework->FrameCounter; hasQueuedFrame = true;
    }

    private void CollectBatches(Slot slot)
    {
        Span<int> counts = stackalloc int[MaxLayers];
        var total = 0; var active = 0;
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer == null || layer.Disposed || !layer.Enabled || layer.Count == 0) { counts[i] = 0; continue; }
            counts[i] = layer.Count; total += layer.Count; active++;
        }
        if (total > MaxVertices)
        {
            // Fair initial shares ensure an oversized producer cannot starve all others.
            var share = MaxVertices / active; share -= share % 3;
            var remaining = MaxVertices;
            for (var i = 0; i < counts.Length; i++) { counts[i] = Math.Min(counts[i], share); remaining -= counts[i]; }
            for (var i = 0; i < counts.Length && remaining > 0; i++)
            {
                if (counts[i] == 0) continue;
                var extra = Math.Min(layers[i]!.Count - counts[i], remaining);
                counts[i] += extra; remaining -= extra;
            }
        }
        slot.BatchCount = 0;
        var offset = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            var layer = layers[i]!;
            if (layer.Fluid) layer.FluidVertices!.AsSpan(0, counts[i]).CopyTo(slot.FluidVertices.AsSpan(offset));
            else layer.Vertices!.AsSpan(0, counts[i]).CopyTo(slot.Vertices.AsSpan(offset));
            slot.Batches[slot.BatchCount++] = new Batch {
                Layer = layer, Generation = layer.Generation, Start = offset, Count = counts[i],
                TestSceneDepth = layer.TestSceneDepth, ClipSpace = layer.ClipSpace,
                Diagnostic = layer.Diagnostic, Shaded = layer.Shaded, BudgetLimited = counts[i] < layer.Count,
                Fluid = layer.Fluid, Material = layer.Material,
            };
            offset += counts[i];
            if (counts[i] < layer.Count) layer.Status = "Rendering with a reduced shared-frame vertex budget";
        }
    }

    private void SetTarget(ImmediateContext* context, RenderCommandSetTarget* command)
    {
        targetHook!.Original(context, command);
        try
        {
            lock (gate)
                foreach (var slot in slots)
                {
                    if (slot.Marker != (nint)command) continue;
                    slot.Marker = 0;
                    if (!disposed && !faulted && !externalViewConflict) Draw(context, slot);
                    return;
                }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void Draw(ImmediateContext* context, Slot slot)
    {
        var device = Device.Instance();
        var targets = RenderTargetManager.Instance();
        var depth = targets == null ? null : targets->DepthStencil;
        if (device == null || context == null || device->D3D11DeviceContext == null
            || depth == null || depth->D3D11ShaderResourceView == null)
        { Skip("Scene depth SRV unavailable; drawing skipped"); return; }
        if (device->SwapChain == null || (nint)device->SwapChain->BackBuffer != slot.Target)
        { Skip("Backbuffer changed; drawing skipped"); return; }
        var backbuffer = device->SwapChain->BackBuffer;
        if (backbuffer->ActualWidth != slot.Width || backbuffer->ActualHeight != slot.Height
            || depth->ActualWidth == 0 || depth->ActualHeight == 0)
        { Skip("Resize in flight; drawing skipped"); return; }
        if (depth->ActualWidth > depth->AllocatedWidth || depth->ActualHeight > depth->AllocatedHeight
            || Math.Abs((double)depth->ActualWidth / depth->ActualHeight - (double)slot.Width / slot.Height) > 0.02)
        { Skip("Scene depth rectangle does not match main output; drawing skipped"); return; }
        var srv = (ID3D11ShaderResourceView*)depth->D3D11ShaderResourceView;
        ShaderResourceViewDesc desc;
        srv->GetDesc(&desc);
        if ((int)desc.ViewDimension != 4) { Skip("Unsupported scene depth view; drawing skipped"); return; }
        if (probeRemaining > 0)
        {
            status = "Main target and Texture2D scene depth SRV available";
            if (probeRemaining == 120 || probeRemaining == 1)
                log.Info($"World geometry probe: target={slot.Width}x{slot.Height}, depth={depth->ActualWidth}x{depth->ActualHeight}, allocated={depth->AllocatedWidth}x{depth->AllocatedHeight}, SRV={desc.Format}, reverseZ=true, queued={submitted}, drawn={drawn}, skipped={skipped}");
            FinishProbe();
        }
        if (slot.BatchCount == 0) return;
        var d3d = (ID3D11DeviceContext*)device->D3D11DeviceContext;
        if (d3d->LpVtbl == null) { Skip("Device context interface unavailable; drawing skipped"); return; }
        ID3D11Device* nativeDevice = null;
        srv->GetDevice(&nativeDevice);
        if (nativeDevice == null) return;
        var anyDrawn = false;
        try
        {
            // Capture once, before any producer draw: refracted background never contains plugin geometry.
            var needsFluid = false;
            for (var i = 0; i < slot.BatchCount; i++)
            {
                ref var candidate = ref slot.Batches[i];
                if (candidate.Fluid && !candidate.Layer.Disposed && candidate.Layer.Enabled && candidate.Layer.Generation == candidate.Generation)
                    needsFluid = true;
            }
            if (needsFluid) gpuTiming.Begin(nativeDevice, d3d);
            var fluidReady = false;
            if (needsFluid)
            {
                try { fluidReady = fluidPass.CaptureScene(nativeDevice, d3d, slot.Width, slot.Height); }
                catch (Exception ex)
                {
                    fluidPass.Dispose();
                    foreach (var layer in layers)
                        if (layer != null && layer.Fluid && layer.Enabled)
                        { layer.Count = 0; layer.Generation++; layer.Status = "Liquid resources rejected; see log"; }
                    log.Error(ex, "World liquid resources rejected; ordinary producer layers remain available");
                }
            }
            for (var i = 0; i < slot.BatchCount; i++)
            {
                ref var batch = ref slot.Batches[i];
                var layer = batch.Layer;
                if (layer.Disposed || !layer.Enabled || layer.Generation != batch.Generation) continue;
                var matrix = batch.ClipSpace ? Matrix4x4.Identity : slot.ViewProjection;
                if (batch.Fluid)
                {
                    var ellipsoidVertices = 0;
                    for (var vertex = 0; vertex < batch.Count; vertex++)
                        if (slot.FluidVertices[batch.Start + vertex].VolumeRadii.X > 0) ellipsoidVertices++;
                    var shapeMode = ellipsoidVertices > 0 ? 1 : 0;
                    if (layer.LastDrawMode != 3 || layer.LastOpticalShapeMode != shapeMode)
                    {
                        layer.LastDrawMode = 3; layer.LastOpticalShapeMode = shapeMode;
                        log.Info($"World fluid layer {layer.Name}: {fluidPass.Status}; smoothVertices={batch.Count}, closedEllipsoidVertices={ellipsoidVertices}, thinSurfaceVertices={batch.Count - ellipsoidVertices}, stride={sizeof(FluidVertex)}; IOR={batch.Material.IndexOfRefraction}; diagnostic={batch.Material.DiagnosticView}; normal-selected front surface; original local single-interface refraction for all producers; pre-UI snapshot");
                    }
                    try
                    {
                        if (!fluidReady || !fluidPass.Draw(nativeDevice, d3d, srv, slot.FluidVertices.AsSpan(batch.Start, batch.Count),
                            matrix, slot.CameraPosition, slot.Width, slot.Height, depth->ActualWidth, depth->ActualHeight,
                            batch.TestSceneDepth, batch.Material))
                        { skipped++; layer.Status = fluidPass.Status; continue; }
                    }
                    catch (Exception ex)
                    {
                        skipped++; layer.Count = 0; layer.Generation++;
                        layer.Status = "Liquid draw rejected; see log";
                        log.Error(ex, $"World liquid layer {layer.Name} stopped after draw failure; context restored");
                        continue;
                    }
                    anyDrawn = true; drawnBatches++; layer.Status = fluidPass.Status; continue;
                }
                if (batch.Diagnostic) Diagnose(nativeDevice, d3d, srv, slot, batch, matrix, depth->ActualWidth, depth->ActualHeight);
                if (!pass.Draw(nativeDevice, d3d, srv, slot.Vertices.AsSpan(batch.Start, batch.Count), matrix,
                    slot.CameraPosition, slot.Width, slot.Height, depth->ActualWidth, depth->ActualHeight,
                    batch.TestSceneDepth, batch.Shaded))
                { skipped++; layer.Status = "No D3D render target bound; drawing skipped"; continue; }
                anyDrawn = true; drawnBatches++;
                layer.Status = batch.BudgetLimited ? "Rendering with a reduced shared-frame vertex budget" : "Rendering";
            }
            if (anyDrawn) { drawn++; status = "Rendering world geometry"; }
        }
        finally { gpuTiming.End(d3d); nativeDevice->Release(); }
    }

    private void Diagnose(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11ShaderResourceView* srv,
        Slot slot, Batch batch, Matrix4x4 matrix, uint depthWidth, uint depthHeight)
    {
        var layer = batch.Layer;
        if (layer.DepthProbe?.Poll(context) is { } report) log.Info($"World geometry layer {layer.Name}: {report}");
        if (!batch.ClipSpace && layer.DepthProbe == null && batch.Count == 144)
        {
            layer.DepthProbe = new WorldDepthProbe();
            var pixels = stackalloc Vector2[3];
            for (var i = 0; i < 3; i++)
            {
                var center = Vector3.Zero;
                for (var j = 0; j < 48; j++) center += slot.Vertices[batch.Start + i * 48 + j].Position;
                center /= 48;
                var projected = Vector4.Transform(new Vector4(center, 1), matrix);
                pixels[i] = projected.W > 0 ? new Vector2(
                    (projected.X / projected.W + 1) * 0.5f * depthWidth,
                    (1 - projected.Y / projected.W) * 0.5f * depthHeight) : new Vector2(float.NaN);
                log.Info($"World geometry layer {layer.Name} sample #{i}: world={center}, texel={pixels[i]}, projectedDepth={projected.Z / projected.W:G9}");
            }
            layer.DepthProbe.Capture(device, context, srv, new ReadOnlySpan<Vector2>(pixels, 3));
        }
        var mode = batch.ClipSpace ? 0 : batch.TestSceneDepth ? 2 : 1;
        if (mode != layer.LastDrawMode)
        {
            layer.LastDrawMode = mode;
            var world = slot.Vertices[batch.Start].Position;
            var clip = Vector4.Transform(new Vector4(world, 1), matrix);
            log.Info($"World geometry draw: layer={layer.Name}, mode={mode}, shaded={batch.Shaded}, vertices={batch.Count}, firstWorld={world}, firstClip={clip}, queued={submitted}, drawn={drawn}, skipped={skipped}");
        }
        if (!batch.ClipSpace && !batch.TestSceneDepth && !layer.ProjectionLogged)
        { layer.ProjectionLogged = true; log.Info($"World geometry layer {layer.Name} viewProjection={matrix}"); }
    }

    private void Skip(string reason) { status = reason; skipped++; FinishProbe(); }
    private void FinishProbe()
    {
        if (probeRemaining > 0 && --probeRemaining == 0)
        {
            log.Info($"World geometry probe complete: {status}; queued={submitted}, drawn={drawn}, skipped={skipped}");
            status = "Probe complete; see log";
            // Main-thread Tick disables unneeded hooks, never the GPU consumer.
        }
    }

    private void Fail(Exception ex)
    {
        lock (gate)
        {
            if (faulted) return;
            faulted = true; probeRemaining = 0;
            status = "World renderer stopped after error; see log";
            log.Error(ex, "World geometry: stopped rendering after an error");
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref disposed)) return;
        uiHook?.Disable(); targetHook?.Disable();
        lock (gate)
        {
            if (disposed) return;
            disposed = true; hooksEnabled = false;
            foreach (var layer in layers) if (layer != null)
            {
                layer.Disposed = true; layer.Enabled = false; layer.Vertices = null; layer.FluidVertices = null;
                layer.DepthProbe?.Dispose(); layer.DepthProbe = null;
            }
            Array.Clear(layers);
            foreach (var slot in slots) { slot.Marker = 0; slot.BatchCount = 0; Array.Clear(slot.Batches); }
            pass.Dispose();
            fluidPass.Dispose();
            gpuTiming.Dispose();
        }
        uiHook?.Dispose(); targetHook?.Dispose();
    }
}
