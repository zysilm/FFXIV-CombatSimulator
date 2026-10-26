// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>
/// One explicitly requested batch of depth samples (four by default, at most 64). Input
/// positions are texel coordinates in the source depth resource, not final backbuffer
/// coordinates. Up to <see cref="Capacity"/> - 1 input positions are followed by the
/// resource's centre texel. Capture/Poll must run on the existing render-thread immediate context.
///
/// D3D11 forbids partial CopySubresourceRegion of a depth-stencil subresource:
/// https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copysubresourceregion
/// Instead, a small CS reads the existing SRV into a private Nx1 R32_FLOAT
/// texture. Only that tiny color texture is copied to a CPU staging resource.
/// Poll uses DO_NOT_WAIT: no Flush, wait, scene texture write or full-depth copy.
/// </summary>
public sealed unsafe class WorldDepthProbe : IDisposable
{
    public const int MaximumCapacity = 64;
    private ID3D11DeviceContext1* context1;
    private ID3DDeviceContextState* isolatedState;
    private ID3D11ComputeShader* shader;
    private ID3D11Buffer* constants;
    private ID3D11Texture2D* gathered;
    private ID3D11UnorderedAccessView* output;
    private ID3D11Texture2D* staging;
    private readonly Vector2[] coordinates;
    private readonly float[] values;
    private bool used, pending, disposed;
    private int pollCount;
    private string? report;
    private string description = "";
    private nint capturedContext, resourceDevice, resourceContext;

    /// <summary>Samples per batch, the last being the centre texel.</summary>
    public int Capacity { get; }
    /// <summary>Depth values of the completed batch, in input order then the centre texel; empty until read back.</summary>
    public ReadOnlySpan<float> Values => HasValues ? values : default;
    public bool HasValues { get; private set; }

    public WorldDepthProbe(int capacity = 4)
    {
        Capacity = Math.Clamp(capacity, 4, MaximumCapacity);
        coordinates = new Vector2[Capacity];
        values = new float[Capacity];
    }

    private const string ShaderSource = """
        Texture2D<float> SourceDepth : register(t0);
        RWTexture2D<float> Samples : register(u0);
        cbuffer Positions : register(b0) { float4 Pixels[64]; };
        [numthreads(64,1,1)] void main(uint3 p : SV_DispatchThreadID) {
            uint width, height;
            Samples.GetDimensions(width, height);
            if (p.x < width) Samples[uint2(p.x,0)] = SourceDepth.Load(int3(int2(Pixels[p.x].xy),0));
        }
        """;

    /// <summary>
    /// Allows one more batch on this instance, reusing its GPU resources, once the previous
    /// batch has been read back or reported. Returns false while a batch is still pending.
    /// </summary>
    public bool Rearm()
    {
        if (disposed || pending) return false;
        used = false; HasValues = false; report = null; pollCount = 0;
        return true;
    }

    /// <summary>Idempotent until <see cref="Rearm"/>: submits at most one GPU batch.</summary>
    public void Capture(ID3D11Device* device, ID3D11DeviceContext* context,
        ID3D11ShaderResourceView* srv, ReadOnlySpan<Vector2> pixelPositions)
    {
        if (used || disposed) return;
        used = true;
        ID3D11Resource* resource = null;
        ID3D11Texture2D* texture = null;
        try
        {
            if (device == null || context == null || srv == null || (int)context->GetType() != 0)
                throw new InvalidOperationException("Missing immediate context or depth SRV");
            ShaderResourceViewDesc view;
            srv->GetDesc(&view);
            if ((int)view.ViewDimension != 4)
                throw new InvalidOperationException($"Unsupported SRV dimension {view.ViewDimension}");
            srv->GetResource(&resource);
            if (resource == null) throw new InvalidOperationException("Depth SRV has no resource");
            var textureGuid = ID3D11Texture2D.Guid;
            Marshal.ThrowExceptionForHR(resource->QueryInterface(&textureGuid, (void**)&texture));
            Texture2DDesc source;
            texture->GetDesc(&source);
            description = $"resource={source.Width}x{source.Height}, format={source.Format}, SRV={view.Format}, bind=0x{source.BindFlags:X}, mip={source.MipLevels}, array={source.ArraySize}, samples={source.SampleDesc.Count}";
            if (source.Width == 0 || source.Height == 0 || source.MipLevels != 1
                || source.ArraySize != 1 || source.SampleDesc.Count != 1)
                throw new InvalidOperationException($"Unsupported depth shape: {description}");
            // R24G8_TYPELESS -> R24_UNORM_X8_TYPELESS; R32_TYPELESS -> R32_FLOAT.
            // The SRV conversion performs the documented UNORM/float interpretation.
            if (!(((int)source.Format == 44 && (int)view.Format == 46)
                || ((int)source.Format == 39 && (int)view.Format == 41)))
                throw new InvalidOperationException($"Unsupported depth format pair: {description}");
            var count = Math.Min(Capacity - 1, pixelPositions.Length);
            for (var i = 0; i < Capacity; i++)
            {
                var point = i < count ? pixelPositions[i] : new Vector2(source.Width / 2, source.Height / 2);
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                    throw new InvalidOperationException($"Non-finite probe coordinate #{i}");
                coordinates[i] = new Vector2(
                    Math.Clamp(MathF.Floor(point.X), 0, source.Width - 1),
                    Math.Clamp(MathF.Floor(point.Y), 0, source.Height - 1));
            }
            // The centre texel always occupies the last slot.
            coordinates[Capacity - 1] = new Vector2(source.Width / 2, source.Height / 2);
            EnsureResources(device, context);
            var positions = stackalloc Vector4[MaximumCapacity];
            for (var i = 0; i < Capacity; i++) positions[i] = new Vector4(coordinates[i], 0, 0);
            MappedSubresource mapped;
            Marshal.ThrowExceptionForHR(context->Map((ID3D11Resource*)constants, 0, (Map)4, 0, &mapped));
            try { Buffer.MemoryCopy(positions, mapped.PData, MaximumCapacity * 16, MaximumCapacity * 16); }
            finally { context->Unmap((ID3D11Resource*)constants, 0); }

            ID3DDeviceContextState* previous = null;
            context1->SwapDeviceContextState(isolatedState, &previous);
            if (previous == null) throw new InvalidOperationException("Probe context-state capture failed");
            try
            {
                var buffer = constants;
                var uav = output;
                context->CSSetShader(shader, null, 0);
                context->CSSetConstantBuffers(0, 1, &buffer);
                context->CSSetShaderResources(0, 1, &srv);
                context->CSSetUnorderedAccessViews(0, 1, &uav, null);
                context->Dispatch(1, 1, 1);
                ID3D11UnorderedAccessView* empty = null;
                context->CSSetUnorderedAccessViews(0, 1, &empty, null);
                context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)gathered);
            }
            finally
            {
                // The isolated state must not retain the game's source SRV.
                context->ClearState();
                context1->SwapDeviceContextState(previous, null);
                previous->Release();
            }
            capturedContext = (nint)context;
            pending = true;
        }
        catch (Exception ex)
        {
            pending = false;
            report = $"Depth probe rejected: {ex.Message}";
        }
        finally
        {
            if (texture != null) texture->Release();
            if (resource != null) resource->Release();
        }
    }

    /// <summary>Call on a subsequent render frame. Null means pending or already reported.</summary>
    public string? Poll(ID3D11DeviceContext* context)
    {
        if (disposed) return null;
        if (report != null) { var result = report; report = null; return result; }
        if (!pending || context == null) return null;
        if (capturedContext != (nint)context)
        { pending = false; return "Depth probe cancelled: immediate context changed"; }
        // A stalled/lost device must not create an unbounded observer.
        if (++pollCount > 120)
        { pending = false; return $"Depth probe timed out without blocking: {description}"; }
        MappedSubresource mapped;
        var hr = context->Map((ID3D11Resource*)staging, 0, (Map)1, 0x100000, &mapped); // READ, DO_NOT_WAIT
        if ((uint)hr == 0x887A000A) return null; // DXGI_ERROR_WAS_STILL_DRAWING
        if (hr < 0) { pending = false; return $"Depth probe Map failed: 0x{hr:X8}; {description}"; }
        try
        {
            var data = (float*)mapped.PData;
            for (var i = 0; i < Capacity; i++) values[i] = data[i];
            HasValues = true;
            var result = new StringBuilder($"Depth probe readback: {description}");
            if (Capacity > 4) result.Append($"; samples={Capacity}");
            else
                for (var i = 0; i < Capacity; i++)
                    result.Append($"; {(i == Capacity - 1 ? "centre" : "pixel" + i)}=({coordinates[i].X:F0},{coordinates[i].Y:F0}) depth={values[i].ToString("G9", CultureInfo.InvariantCulture)}");
            pending = false;
            return result.ToString();
        }
        finally { context->Unmap((ID3D11Resource*)staging, 0); }
    }

    private void EnsureResources(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        // A rearmed probe keeps its resources while the device and context are unchanged.
        if (staging != null && resourceDevice == (nint)device && resourceContext == (nint)context) return;
        ReleaseResources();
        resourceDevice = (nint)device; resourceContext = (nint)context;
        var guid = ID3D11DeviceContext1.Guid;
        ID3D11DeviceContext1* acquiredContext = null;
        Marshal.ThrowExceptionForHR(context->QueryInterface(&guid, (void**)&acquiredContext));
        context1 = acquiredContext;
        ID3D11Device1* device1 = null;
        guid = ID3D11Device1.Guid;
        Marshal.ThrowExceptionForHR(device->QueryInterface(&guid, (void**)&device1));
        try
        {
            var level = device->GetFeatureLevel();
            var emulate = ID3D11Device.Guid;
            D3DFeatureLevel selected;
            ID3DDeviceContextState* createdState = null;
            Marshal.ThrowExceptionForHR(device1->CreateDeviceContextState(
                (device->GetCreationFlags() & 1) != 0 ? 1u : 0u, &level, 1, 7, &emulate, &selected, &createdState));
            isolatedState = createdState;
        }
        finally { device1->Release(); }
        var bytes = Encoding.UTF8.GetBytes(ShaderSource);
        var hr = D3DCompile(bytes, (nuint)bytes.Length, "world-depth-probe", 0, 0, "main", "cs_5_0", 0, 0, out var code, out var errors);
        try
        {
            if (hr < 0)
            {
                var detail = errors == 0 ? "No compiler diagnostic" : Marshal.PtrToStringAnsi((nint)BlobData(errors), (int)BlobSize(errors));
                throw new InvalidOperationException($"Probe shader: {detail}");
            }
            ID3D11ComputeShader* createdShader = null;
            Marshal.ThrowExceptionForHR(device->CreateComputeShader(BlobData(code), BlobSize(code), null, &createdShader));
            shader = createdShader;
        }
        finally { if (code != 0) Marshal.Release(code); if (errors != 0) Marshal.Release(errors); }
        var bufferDesc = new BufferDesc { ByteWidth = MaximumCapacity * 16, Usage = (Usage)2, BindFlags = 4, CPUAccessFlags = 0x10000 };
        ID3D11Buffer* createdBuffer = null;
        Marshal.ThrowExceptionForHR(device->CreateBuffer(&bufferDesc, null, &createdBuffer));
        constants = createdBuffer;
        var textureDesc = new Texture2DDesc {
            Width = (uint)Capacity, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = (Format)41, SampleDesc = new SampleDesc { Count = 1 },
            Usage = (Usage)0, BindFlags = 0x80,
        };
        ID3D11Texture2D* createdTexture = null;
        Marshal.ThrowExceptionForHR(device->CreateTexture2D(&textureDesc, null, &createdTexture));
        gathered = createdTexture;
        ID3D11UnorderedAccessView* createdOutput = null;
        Marshal.ThrowExceptionForHR(device->CreateUnorderedAccessView((ID3D11Resource*)gathered, null, &createdOutput));
        output = createdOutput;
        textureDesc.Usage = (Usage)3;
        textureDesc.BindFlags = 0;
        textureDesc.CPUAccessFlags = 0x20000;
        createdTexture = null;
        Marshal.ThrowExceptionForHR(device->CreateTexture2D(&textureDesc, null, &createdTexture));
        staging = createdTexture;
    }

    private static void* BlobData(nint blob) => ((delegate* unmanaged[Stdcall]<nint, void*>)(*(void***)blob)[3])(blob);
    private static nuint BlobSize(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nuint>)(*(void***)blob)[4])(blob);
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte[] source, nuint length, string name, nint defines, nint include,
        string entry, string target, uint flags, uint flags2, out nint code, out nint errors);

    public void Dispose()
    {
        disposed = true;
        pending = false;
        report = null;
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        resourceDevice = resourceContext = 0;
        if (isolatedState != null) { isolatedState->Release(); isolatedState = null; }
        if (context1 != null) { context1->Release(); context1 = null; }
        if (shader != null) { shader->Release(); shader = null; }
        if (constants != null) { constants->Release(); constants = null; }
        if (output != null) { output->Release(); output = null; }
        if (gathered != null) { gathered->Release(); gathered = null; }
        if (staging != null) { staging->Release(); staging = null; }
    }
}
