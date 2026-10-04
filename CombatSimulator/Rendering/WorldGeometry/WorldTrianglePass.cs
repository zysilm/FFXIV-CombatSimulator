// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>
/// D3D11.1 isolated context state preserves the complete game pipeline, including
/// constant-buffer subranges, class instances and resource-hazard unbindings.
/// Private state is cleared before restoring the game, so it cannot retain a
/// swapchain/depth resource across resize or territory changes.
/// API contract: https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11devicecontext1-swapdevicecontextstate
/// </summary>
internal sealed unsafe class WorldTrianglePass : IDisposable
{
    private ID3D11DeviceContext1* context1;
    private ID3DDeviceContextState* privateState;
    private ID3D11VertexShader* vertexShader;
    private ID3D11PixelShader* pixelShader;
    private ID3D11InputLayout* layout;
    private ID3D11Buffer* vertexBuffer;
    private ID3D11Buffer* constants;
    private ID3D11BlendState* blend;
    private ID3D11RasterizerState* rasterizer;
    private ID3D11DepthStencilState* depthState;
    private nint ownerDevice;
    private nint ownerContext;

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public Matrix4x4 ViewProjection;
        public Vector4 Camera;
        public Vector4 OutputSize;
        public Vector4 DepthSize;
    }

    private const string Shader = """
        cbuffer Parameters : register(b0) {
            row_major float4x4 ViewProjection;
            float4 Camera;
            float4 OutputSize;
            float4 DepthSize;
        };
        Texture2D<float> SceneDepth : register(t0);
        struct Vertex { float3 position : POSITION; float3 normal : NORMAL; float4 color : COLOR; };
        struct Pixel { float4 position : SV_POSITION; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; float4 color : COLOR; };
        Pixel VSMain(Vertex v) {
            Pixel p; p.position = mul(float4(v.position,1), ViewProjection);
            p.world = v.position; p.normal = v.normal; p.color = v.color; return p;
        }
        float4 PSMain(Pixel p) : SV_TARGET {
            // Sample the active depth rectangle, not padded allocation dimensions.
            int2 q = clamp(int2(p.position.xy * DepthSize.xy / OutputSize.xy), int2(0,0), int2(DepthSize.xy)-1);
            float scene = SceneDepth.Load(int3(q,0));
            float tolerance = 0.000001 + abs(p.position.z) * 0.00002;
            if (DepthSize.z > 0 && p.position.z + tolerance < scene) discard; // main scene uses reverse Z
            if (OutputSize.z < 0.5) return p.color; // Preserve colors of unlit debug geometry.
            float3 n = normalize(p.normal + float3(0,0.000001,0));
            float3 v = normalize(Camera.xyz-p.world);
            float facing = saturate(abs(dot(n,v)));
            float fresnel = 0.02 + 0.98 * pow(1-facing,5);
            float3 light = normalize(float3(-0.35,0.8,-0.4));
            float specular = pow(saturate(abs(dot(n,normalize(light+v)))),64);
            float3 rgb = p.color.rgb * (0.45 + 0.3*facing) + (fresnel*0.25 + specular*0.6);
            return float4(rgb, saturate(p.color.a * (0.6 + 0.4*fresnel)));
        }
        """;

    public bool Draw(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11ShaderResourceView* sceneDepth,
        ReadOnlySpan<WorldVertex> vertices, Matrix4x4 viewProjection, Vector3 camera,
        uint width, uint height, uint depthWidth, uint depthHeight, bool testSceneDepth = true, bool shaded = true)
    {
        EnsureResources(device, context);
        ID3D11RenderTargetView* target = null;
        context->OMGetRenderTargets(1, &target, null);
        if (target == null) return false;
        ID3DDeviceContextState* previous = null;
        try
        {
            context1->SwapDeviceContextState(privateState, &previous);
            if (previous == null) throw new InvalidOperationException("D3D11 context-state capture failed");
            try
            {
                var p = new Parameters {
                    ViewProjection = viewProjection, Camera = new Vector4(camera, 0),
                    OutputSize = new Vector4(width, height, shaded ? 1 : 0, 0),
                    DepthSize = new Vector4(depthWidth, depthHeight, testSceneDepth ? 1 : 0, 0),
                };
                Upload(context, constants, &p, (uint)sizeof(Parameters));
                fixed (WorldVertex* data = vertices)
                    Upload(context, vertexBuffer, data, (uint)(vertices.Length * sizeof(WorldVertex)));
                context->OMSetRenderTargets(1, &target, null);
                var factors = stackalloc float[4] { 0, 0, 0, 0 };
                context->OMSetBlendState(blend, factors, uint.MaxValue);
                context->OMSetDepthStencilState(depthState, 0);
                context->RSSetState(rasterizer);
                var viewport = new Viewport { Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
                context->RSSetViewports(1, &viewport);
                context->IASetInputLayout(layout);
                uint stride = (uint)sizeof(WorldVertex), offset = 0;
                var vb = vertexBuffer;
                context->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
                context->IASetPrimitiveTopology((D3DPrimitiveTopology)4); // TRIANGLELIST
                context->VSSetShader(vertexShader, null, 0);
                context->PSSetShader(pixelShader, null, 0);
                var cb = constants;
                context->VSSetConstantBuffers(0, 1, &cb);
                context->PSSetConstantBuffers(0, 1, &cb);
                context->PSSetShaderResources(0, 1, &sceneDepth);
                context->Draw((uint)vertices.Length, 0);
            }
            finally
            {
                // Clear only our isolated state. Otherwise privateState retains the
                // backbuffer and makes DXGI ResizeBuffers fail after the first draw.
                context->ClearState();
                context1->SwapDeviceContextState(previous, null);
            }
        }
        finally
        {
            if (previous != null) previous->Release();
            target->Release();
        }
        return true;
    }

    private void EnsureResources(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        if ((int)context->GetType() != 0) throw new InvalidOperationException("World geometry pass requires the immediate D3D11 context");
        if (ownerDevice == (nint)device && ownerContext == (nint)context && privateState != null) return;
        Dispose();
        try
        {
            ID3D11Device1* device1 = null;
            var deviceGuid = ID3D11Device1.Guid;
            Marshal.ThrowExceptionForHR(device->QueryInterface(&deviceGuid, (void**)&device1));
            try
            {
                var contextGuid = ID3D11DeviceContext1.Guid;
                ID3D11DeviceContext1* createdContext = null;
                Marshal.ThrowExceptionForHR(context->QueryInterface(&contextGuid, (void**)&createdContext));
                context1 = createdContext;
                var level = device->GetFeatureLevel();
                var emulate = ID3D11Device.Guid;
                ID3DDeviceContextState* createdState = null;
                D3DFeatureLevel chosen;
                // Match the device's threading contract and feature level.
                var flags = (device->GetCreationFlags() & 1) != 0 ? 1u : 0u;
                Marshal.ThrowExceptionForHR(device1->CreateDeviceContextState(flags, &level, 1, 7, &emulate, &chosen, &createdState));
                privateState = createdState;
            }
            finally { if (device1 != null) device1->Release(); }
            CreateShaders(device);
            vertexBuffer = CreateBuffer(device, (uint)(WorldGeometryRenderer.MaxVertices * sizeof(WorldVertex)), 1);
            constants = CreateBuffer(device, (uint)sizeof(Parameters), 4);
            var blendDesc = new BlendDesc();
            var rt = new RenderTargetBlendDesc {
                BlendEnable = true, SrcBlend = (Blend)5, DestBlend = (Blend)6, BlendOp = (BlendOp)1,
                SrcBlendAlpha = (Blend)1, DestBlendAlpha = (Blend)2, BlendOpAlpha = (BlendOp)1,
                RenderTargetWriteMask = 7, // Preserve the engine's alpha channel.
            };
            blendDesc.RenderTarget[0] = rt;
            ID3D11BlendState* createdBlend = null;
            Marshal.ThrowExceptionForHR(device->CreateBlendState(&blendDesc, &createdBlend));
            blend = createdBlend;
            var rs = new RasterizerDesc { FillMode = (FillMode)3, CullMode = (CullMode)1, DepthClipEnable = true };
            ID3D11RasterizerState* createdRs = null;
            Marshal.ThrowExceptionForHR(device->CreateRasterizerState(&rs, &createdRs));
            rasterizer = createdRs;
            var ds = new DepthStencilDesc { DepthEnable = false, DepthWriteMask = (DepthWriteMask)0, DepthFunc = (ComparisonFunc)8 };
            ID3D11DepthStencilState* createdDs = null;
            Marshal.ThrowExceptionForHR(device->CreateDepthStencilState(&ds, &createdDs));
            depthState = createdDs;
            ownerDevice = (nint)device;
            ownerContext = (nint)context;
        }
        catch { Dispose(); throw; }
    }

    private static ID3D11Buffer* CreateBuffer(ID3D11Device* device, uint size, uint bind)
    {
        var desc = new BufferDesc { ByteWidth = size, Usage = (Usage)2, BindFlags = bind, CPUAccessFlags = 0x10000 };
        ID3D11Buffer* buffer = null;
        Marshal.ThrowExceptionForHR(device->CreateBuffer(&desc, null, &buffer));
        return buffer;
    }

    private static void Upload(ID3D11DeviceContext* context, ID3D11Buffer* buffer, void* source, uint size)
    {
        MappedSubresource mapped;
        Marshal.ThrowExceptionForHR(context->Map((ID3D11Resource*)buffer, 0, (Map)4, 0, &mapped));
        try { Buffer.MemoryCopy(source, mapped.PData, size, size); }
        finally { context->Unmap((ID3D11Resource*)buffer, 0); }
    }

    private void CreateShaders(ID3D11Device* device)
    {
        var source = Encoding.UTF8.GetBytes(Shader);
        var vs = Compile(source, "VSMain", "vs_5_0");
        var ps = nint.Zero;
        try
        {
            ps = Compile(source, "PSMain", "ps_5_0");
            var vsData = BlobData(vs); var vsSize = BlobSize(vs);
            ID3D11VertexShader* createdVs = null;
            Marshal.ThrowExceptionForHR(device->CreateVertexShader(vsData, vsSize, null, &createdVs));
            vertexShader = createdVs;
            ID3D11PixelShader* createdPs = null;
            Marshal.ThrowExceptionForHR(device->CreatePixelShader(BlobData(ps), BlobSize(ps), null, &createdPs));
            pixelShader = createdPs;
            var position = stackalloc byte[9] { 80,79,83,73,84,73,79,78,0 };
            var normal = stackalloc byte[7] { 78,79,82,77,65,76,0 };
            var color = stackalloc byte[6] { 67,79,76,79,82,0 };
            var inputs = stackalloc InputElementDesc[3];
            inputs[0] = new InputElementDesc { SemanticName = position, Format = (Format)6, AlignedByteOffset = 0 };
            inputs[1] = new InputElementDesc { SemanticName = normal, Format = (Format)6, AlignedByteOffset = 12 };
            inputs[2] = new InputElementDesc { SemanticName = color, Format = (Format)2, AlignedByteOffset = 24 };
            ID3D11InputLayout* createdLayout = null;
            Marshal.ThrowExceptionForHR(device->CreateInputLayout(inputs, 3, vsData, vsSize, &createdLayout));
            layout = createdLayout;
        }
        finally { Marshal.Release(vs); if (ps != 0) Marshal.Release(ps); }
    }

    private static nint Compile(byte[] bytes, string entry, string profile)
    {
        var hr = D3DCompile(bytes, (nuint)bytes.Length, "combat-world-geometry", 0, 0, entry, profile, 1 << 15, 0, out var blob, out var errors);
        try
        {
            if (hr < 0)
            {
                var detail = errors == 0 ? "No compiler diagnostic" : Marshal.PtrToStringAnsi((nint)BlobData(errors), (int)BlobSize(errors));
                if (blob != 0) Marshal.Release(blob);
                throw new InvalidOperationException($"World geometry shader {entry}: {detail} (0x{hr:X8})");
            }
            return blob;
        }
        finally { if (errors != 0) Marshal.Release(errors); }
    }

    private static void* BlobData(nint blob) => ((delegate* unmanaged[Stdcall]<nint, void*>)(*(void***)blob)[3])(blob);
    private static nuint BlobSize(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nuint>)(*(void***)blob)[4])(blob);
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte[] source, nuint length, string sourceName, nint defines, nint include,
        string entry, string profile, uint flags, uint flags2, out nint code, out nint errors);

    public void Dispose()
    {
        if (privateState != null) { privateState->Release(); privateState = null; }
        if (context1 != null) { context1->Release(); context1 = null; }
        if (vertexShader != null) { vertexShader->Release(); vertexShader = null; }
        if (pixelShader != null) { pixelShader->Release(); pixelShader = null; }
        if (layout != null) { layout->Release(); layout = null; }
        if (vertexBuffer != null) { vertexBuffer->Release(); vertexBuffer = null; }
        if (constants != null) { constants->Release(); constants = null; }
        if (blend != null) { blend->Release(); blend = null; }
        if (rasterizer != null) { rasterizer->Release(); rasterizer = null; }
        if (depthState != null) { depthState->Release(); depthState = null; }
        ownerDevice = 0;
        ownerContext = 0;
    }
}
