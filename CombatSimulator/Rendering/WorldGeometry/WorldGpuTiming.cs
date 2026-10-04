// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>
/// Four in-flight timestamp/disjoint groups; immediate-context polling never waits
/// or flushes. Sampling/polling uses only fixed buffers. Formatting is on demand.
/// https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata
/// </summary>
internal sealed unsafe class WorldGpuTiming : IDisposable
{
    private const int WindowSize = 256;
    private struct Pending
    {
        public ID3D11Query* Disjoint;
        public ID3D11Query* Start;
        public ID3D11Query* Finish;
        public long Frame;
        public bool Submitted;
    }
    // Native BOOL is four bytes; UINT64 + BOOL occupies 16 bytes with native alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct ClockData { public ulong Frequency; public int Disjoint; }
    private readonly Pending[] pending = new Pending[4];
    private readonly double[] samples = new double[WindowSize];
    private nint ownerDevice, ownerContext;
    private long frame, sampleCount, disjointCount, notReadyCount, errorCount, skippedCount;
    private int sampleCursor, windowCount, active = -1;
    private double allTimeMax;
    private bool unavailable;

    public string Status
    {
        get
        {
            Span<double> ordered = stackalloc double[WindowSize];
            samples.AsSpan(0, windowCount).CopyTo(ordered);
            ordered = ordered[..windowCount]; ordered.Sort();
            var p95 = windowCount == 0 ? double.NaN : ordered[Math.Max(0, (int)Math.Ceiling(windowCount * 0.95) - 1)];
            var p99 = windowCount == 0 ? double.NaN : ordered[Math.Max(0, (int)Math.Ceiling(windowCount * 0.99) - 1)];
            var maximum = sampleCount == 0 ? double.NaN : allTimeMax;
            return $"GPU scope=plugin render section with fluid (snapshot + fluid + concurrent ordinary layers); sampleCount={sampleCount}, window={windowCount}/{WindowSize}, p95={p95:F3}ms, p99={p99:F3}ms, max={maximum:F3}ms, disjoint={disjointCount}, notReadyDropped={notReadyCount}, queryErrors={errorCount}, ringSkipped={skippedCount}, queries={(unavailable ? "unavailable" : ownerDevice == 0 ? "released" : "active")}; DONOTFLUSH, 2-4 frame poll";
        }
    }

    public void Begin(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        if (ownerDevice != (nint)device || ownerContext != (nint)context)
        {
            Dispose(); ownerDevice = (nint)device; ownerContext = (nint)context; unavailable = false;
            try
            {
                for (var i = 0; i < pending.Length; i++)
                {
                    pending[i].Disjoint = Create(device, 3);
                    pending[i].Start = Create(device, 2);
                    pending[i].Finish = Create(device, 2);
                }
            }
            catch
            {
                Dispose(); ownerDevice = (nint)device; ownerContext = (nint)context;
                unavailable = true; errorCount++; return;
            }
        }
        if (unavailable || active >= 0) return;
        frame++;
        Poll(context);
        for (var i = 0; i < pending.Length; i++)
        {
            if (pending[i].Submitted) continue;
            active = i; pending[i].Frame = frame;
            context->Begin((ID3D11Asynchronous*)pending[i].Disjoint);
            context->End((ID3D11Asynchronous*)pending[i].Start);
            return;
        }
        skippedCount++;
    }

    public void End(ID3D11DeviceContext* context)
    {
        if (active < 0) return;
        ref var p = ref pending[active];
        context->End((ID3D11Asynchronous*)p.Finish);
        context->End((ID3D11Asynchronous*)p.Disjoint);
        p.Submitted = true; active = -1;
    }

    private void Poll(ID3D11DeviceContext* context)
    {
        for (var i = 0; i < pending.Length; i++)
        {
            ref var p = ref pending[i];
            if (!p.Submitted || frame - p.Frame < 2) continue;
            ClockData clock = default;
            ulong start = 0, finish = 0;
            var a = context->GetData((ID3D11Asynchronous*)p.Disjoint, &clock, (uint)sizeof(ClockData), 1);
            var b = context->GetData((ID3D11Asynchronous*)p.Start, &start, sizeof(ulong), 1);
            var c = context->GetData((ID3D11Asynchronous*)p.Finish, &finish, sizeof(ulong), 1);
            if (a < 0 || b < 0 || c < 0) { errorCount++; p.Submitted = false; continue; }
            if (a != 0 || b != 0 || c != 0)
            {
                if (frame - p.Frame >= 4) { notReadyCount++; p.Submitted = false; }
                continue;
            }
            p.Submitted = false;
            if (clock.Disjoint != 0 || clock.Frequency == 0 || finish < start) { disjointCount++; continue; }
            var milliseconds = (double)(finish - start) / clock.Frequency * 1000;
            if (!double.IsFinite(milliseconds)) { errorCount++; continue; }
            samples[sampleCursor] = milliseconds;
            sampleCursor = (sampleCursor + 1) % WindowSize;
            windowCount = Math.Min(WindowSize, windowCount + 1);
            sampleCount++; allTimeMax = Math.Max(allTimeMax, milliseconds);
        }
    }

    private static ID3D11Query* Create(ID3D11Device* device, int kind)
    {
        var desc = new QueryDesc { Query = (Query)kind };
        ID3D11Query* query = null;
        Marshal.ThrowExceptionForHR(device->CreateQuery(&desc, &query));
        return query;
    }

    public void ResetStatistics()
    {
        Dispose(); unavailable = false;
        sampleCursor = windowCount = 0; allTimeMax = 0;
        sampleCount = disjointCount = notReadyCount = errorCount = skippedCount = frame = 0;
        Array.Clear(samples);
    }

    /// <summary>Release GPU resources; keep successful statistics for inspection after a finite preview.</summary>
    public void Dispose()
    {
        for (var i = 0; i < pending.Length; i++)
        {
            ref var p = ref pending[i];
            if (p.Disjoint != null) p.Disjoint->Release();
            if (p.Start != null) p.Start->Release();
            if (p.Finish != null) p.Finish->Release();
            p = default;
        }
        ownerDevice = ownerContext = 0; active = -1;
    }
}
