// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Diagnostics;
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
internal sealed unsafe class WorldFluidPass : IDisposable
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
    private readonly SceneColorSnapshot snapshot = new();
    private ID3D11SamplerState* sampler;
    private string viewportDiagnostic = "";
    public string Status => snapshot.Status + viewportDiagnostic;
    private nint ownerDevice;
    private nint ownerContext;
    private long initializationCount, compileCount;
    private double initializationLast, initializationMax, compileLast, compileMax;
    public string InitializationTimingStatus => $"fluid pipeline init attempts={initializationCount}, last={(initializationCount == 0 ? double.NaN : initializationLast):F3}ms, max={(initializationCount == 0 ? double.NaN : initializationMax):F3}ms (excludes scene snapshot allocation/copy); FXC calls={compileCount}, last={(compileCount == 0 ? double.NaN : compileLast):F3}ms, max={(compileCount == 0 ? double.NaN : compileMax):F3}ms (FXC is inside pipeline init, not additive)";

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public Matrix4x4 ViewProjection;
        public Vector4 Camera;
        public Vector4 OutputSize;
        public Vector4 DepthSize;
        public Vector4 Optics;
        public Vector4 Absorption;
        public Matrix4x4 InverseViewProjection;
    }

    private const string Shader = """
        cbuffer Parameters : register(b0) {
            row_major float4x4 ViewProjection;
            float4 Camera; float4 OutputSize; float4 DepthSize;
            float4 Optics; float4 Absorption;
            row_major float4x4 InverseViewProjection;
        };
        Texture2D<float> SceneDepth : register(t0);
        Texture2D<float4> SceneColor : register(t1);
        SamplerState ColorSampler : register(s0);
        struct Vertex { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float2 liquid : TEXCOORD1;
            float3 volumeCenter : TEXCOORD2; float3 volumeRadii : TEXCOORD3; };
        struct Pixel { float4 position : SV_POSITION; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; float2 liquid : TEXCOORD2;
            nointerpolation float3 volumeCenter : TEXCOORD3; nointerpolation float3 volumeRadii : TEXCOORD4; };
        Pixel VSMain(Vertex v) {
            Pixel p; p.position = mul(float4(v.position,1), ViewProjection);
            p.world = v.position; p.normal = v.normal; p.liquid = v.liquid;
            p.volumeCenter=v.volumeCenter; p.volumeRadii=v.volumeRadii; return p;
        }
        bool ProjectWorld(float3 world, out float2 uv) {
            float4 clip = mul(float4(world,1),ViewProjection);
            uv = float2(clip.x,-clip.y)/max(clip.w,0.000001)*0.5+0.5;
            return clip.w > 0.000001 && all(isfinite(uv));
        }
        bool BackgroundWorld(float2 uv, out float3 world) {
            int2 q = clamp(int2(uv*DepthSize.xy),int2(0,0),int2(DepthSize.xy)-1);
            float depth = SceneDepth.Load(int3(q,0));
            float4 homogeneous = mul(float4(uv.x*2-1,1-uv.y*2,depth,1),InverseViewProjection);
            world = homogeneous.xyz/max(abs(homogeneous.w),0.000001)*sign(homogeneous.w);
            return depth > 0 && abs(homogeneous.w)>0.000001 && all(isfinite(world));
        }
        float3 StudioReflection(float3 r, float roughness) {
            // Procedural studio only: no claim of matching game probes or scene lighting.
            float3 base = lerp(float3(0.035,0.04,0.05),float3(0.18,0.22,0.28),saturate(r.y*0.5+0.5));
            float spread = lerp(160,8,roughness);
            float softbox = pow(saturate(dot(r,normalize(float3(-0.5,0.8,-0.25)))),spread);
            float strip = pow(saturate(dot(r,normalize(float3(0.6,0.3,0.4)))),spread*0.7);
            return base + softbox*3 + strip*0.8;
        }
        float4 PSMain(Pixel p) : SV_TARGET {
            float2 uv = p.position.xy / OutputSize.xy;
            int2 q = clamp(int2(uv * DepthSize.xy),int2(0,0),int2(DepthSize.xy)-1);
            float scene = SceneDepth.Load(int3(q,0));
            float tolerance = 0.000001 + abs(p.position.z) * 0.00002;
            bool depthRejected = DepthSize.z > 0 && p.position.z + tolerance < scene;
            // Debug the actual submitted geometry without changing its height:
            // red shows fragments rejected by scene depth; green shows accepted ones.
            if (Absorption.w > 2.5 && Absorption.w < 3.5)
                return depthRejected ? float4(1,0,0,1) : float4(0,1,0,1);
            if (depthRejected) discard;
            float3 n = normalize(p.normal);
            float3 v = normalize(Camera.xyz-p.world);
            // Select the outward near surface geometrically, independent of the
            // game's view handedness or inherited clockwise winding convention.
            float rawFacing = dot(n,v);
            // Retain real scene depth, but show both normal signs before rejecting
            // the back surface. Bright red is back-facing; green is front-facing.
            if (Absorption.w > 3.5 && Absorption.w < 4.5)
            {
                float intensity = 0.25 + 0.75*saturate(abs(rawFacing));
                return rawFacing <= 0 ? float4(intensity,0,0,1) : float4(0,intensity,0,1);
            }
            if (rawFacing <= 0) discard;
            float facing = saturate(rawFacing);
            if (Absorption.w > 1.5 && Absorption.w < 2.5)
                return float4(1-facing,facing,0,1); // Opaque diagnostic, independent of thin-film coverage.
            float eta = 1 / Optics.x;
            float3 incident = -v;
            float3 transmitted = refract(incident,n,eta);
            // Thin branch assumes locally parallel interfaces and normal thickness
            // supplied by the producer. Grazing incidence increases, not decreases,
            // the interior path. An attached curved cap needs a declared substrate
            // normal for a complete one-interface-to-skin model; it remains approximate.
            float thickness = max(0,p.liquid.x)/max(0.1,-dot(transmitted,n));
            // Thin surfaces keep the local liquid-only displacement approximation.
            // Closed ellipsoids declare enough geometry for two Snell interfaces.
            float3 bent = p.world + (transmitted-incident) * thickness * Optics.z;
            float2 bentUv;
            if (!ProjectWorld(bent,bentUv)) bentUv=uv;
            int pathReason=0; // 0: thin surface; codes match renderer diagnostic legend.
            float exitFresnel=0;
            bool totalInternalReflection=false;
            if (all(p.volumeRadii > 0)) {
                pathReason=2; // Analytic entry is not yet proven.
                float3 localEye=(Camera.xyz-p.volumeCenter)/p.volumeRadii;
                float3 localRay=incident/p.volumeRadii;
                float quadratic=dot(localRay,localRay);
                float closest=-dot(localEye,localRay)/quadratic;
                float3 closestPoint=localEye+localRay*closest;
                float halfSquared=(1-dot(closestPoint,closestPoint))/quadratic;
                if (halfSquared > 0 && closest > 0) {
                    float entryDistance=closest-sqrt(halfSquared);
                    float3 front=Camera.xyz+incident*entryDistance;
                    float3 localFront=(front-p.volumeCenter)/p.volumeRadii;
                    float3 frontNormal=normalize(localFront/p.volumeRadii);
                    float3 insideRay=refract(incident,frontNormal,eta);
                    float3 insideLocal=insideRay/p.volumeRadii;
                    float exitDistance=max(0,-2*dot(localFront,insideLocal)/dot(insideLocal,insideLocal));
                    float3 exitPoint=front+insideRay*exitDistance;
                    float3 exitNormal=normalize((exitPoint-p.volumeCenter)/(p.volumeRadii*p.volumeRadii));
                    float3 outsideRay=refract(insideRay,-exitNormal,Optics.x);
                    bool exitValid=exitDistance>0 && all(isfinite(exitPoint)) && all(isfinite(exitNormal)) && all(isfinite(outsideRay));
                    pathReason=exitValid ? 1 : 3;
                    // Beer absorption uses only the actual distance inside liquid.
                    // Scene depth below supplies an air propagation target, never thickness.
                    thickness=exitDistance; n=frontNormal; facing=saturate(dot(n,v));
                    float ef0=(Optics.x-1)/(Optics.x+1); ef0*=ef0;
                    exitFresnel=ef0+(1-ef0)*pow(1-saturate(dot(insideRay,exitNormal)),5);
                    totalInternalReflection=dot(outsideRay,outsideRay)<0.000001;
                    if (!totalInternalReflection) {
                        float2 candidate=uv;
                        bool valid=true;
                        // Three bounded background-plane refinements. Screen space
                        // contains only visible geometry; missing/occluded rays fall back.
                        [unroll] for (int iteration=0;iteration<3;iteration++) {
                            float3 backgroundPoint;
                            if (!BackgroundWorld(candidate,backgroundPoint)) { if (pathReason!=3) pathReason=5; valid=false; break; }
                            float denominator=dot(outsideRay,incident);
                            float airDistance=dot(backgroundPoint-exitPoint,incident)/max(denominator,0.000001);
                            if (denominator <= 0.000001 || airDistance < 0 || airDistance > 20) { if (pathReason!=3) pathReason=6; valid=false; break; }
                            float2 projected;
                            if (!ProjectWorld(exitPoint+outsideRay*airDistance,projected)
                                || any(projected<=0) || any(projected>=1)) { if (pathReason!=3) pathReason=7; valid=false; break; }
                            candidate=projected;
                        }
                        bentUv=valid ? uv+(candidate-uv)*Optics.z : uv;
                    } else { if (pathReason!=3) pathReason=4; bentUv=uv; }
                }
            }
            float edge = saturate(min(min(uv.x,uv.y),min(1-uv.x,1-uv.y))*40);
            float2 offset = clamp(bentUv-uv,-0.04,0.04)*edge;
            float2 refractedUv = uv + offset;
            int2 rq = clamp(int2(refractedUv*DepthSize.xy),int2(0,0),int2(DepthSize.xy)-1);
            float displacedDepth = SceneDepth.Load(int3(rq,0));
            if (DepthSize.z > 0 && displacedDepth > p.position.z+tolerance) { pathReason=8; refractedUv = uv; }
            if (any(refractedUv <= 0) || any(refractedUv >= 1)) { pathReason=7; refractedUv = uv; }
            if (Absorption.w > 4.5 && Absorption.w < 5.5) {
                float3 reasonColor=float3(0.3,0.3,0.3);
                if (pathReason==1) reasonColor=float3(0,1,0);
                if (pathReason==2) reasonColor=float3(1,0,0.6);
                if (pathReason==3) reasonColor=float3(1,1,1);
                if (pathReason==4) reasonColor=float3(0,0.2,1);
                if (pathReason==5) reasonColor=float3(1,1,0);
                if (pathReason==6) reasonColor=float3(1,0.45,0);
                if (pathReason==7) reasonColor=float3(0.55,0,1);
                if (pathReason==8) reasonColor=float3(1,0,0);
                return float4(reasonColor,1);
            }
            if (Absorption.w > 5.5) {
                float pixels=length((refractedUv-uv)*OutputSize.xy);
                float3 offsetColor=pixels<1 ? lerp(float3(0,0,0.2),float3(0,1,0),pixels)
                    : pixels<4 ? lerp(float3(0,1,0),float3(1,1,0),(pixels-1)/3)
                    : lerp(float3(1,1,0),float3(1,0,0),saturate((pixels-4)/12));
                return float4(offsetColor,1);
            }
            float3 background = SceneColor.SampleLevel(ColorSampler,refractedUv,0).rgb;
            if (Absorption.w > 0.5)
                return float4(background,saturate(p.liquid.y));
            float f0 = (Optics.x-1)/(Optics.x+1); f0 *= f0;
            float fresnel = f0 + (1-f0)*pow(1-facing,5);
            fresnel = totalInternalReflection ? 1 : 1-(1-fresnel)*(1-exitFresnel);
            // Explicit artistic reflection weight: scale both optical branches
            // together so weight zero is clear transmission, without a dark rim.
            float effectiveF = saturate(fresnel * Optics.w);
            float3 transmittance = exp(-Absorption.rgb*thickness);
            float3 reflection = StudioReflection(reflect(-v,n),Optics.y);
            float3 rgb = (1-effectiveF)*transmittance*background + effectiveF*reflection;
            // Coverage only: rgb already contains the refracted background.
            return float4(rgb,saturate(p.liquid.y));
        }
        """;

    public bool CaptureScene(ID3D11Device* device, ID3D11DeviceContext* context, uint width, uint height)
    {
        EnsureResources(device, context);
        ID3D11RenderTargetView* target = null;
        context->OMGetRenderTargets(1, &target, null);
        if (target == null) return false;
        // SetTarget only changes output binding. The game's last rasterizer VP
        // may belong to another pass; record it, but do not use it as a copy gate.
        uint count = 16;
        var viewports = stackalloc Viewport[16];
        context->RSGetViewports(&count, viewports);
        var firstViewport = count > 0 ? viewports[0] : default;
        viewportDiagnostic = $"; inherited VP count={count}, XY=({firstViewport.TopLeftX:G9},{firstViewport.TopLeftY:G9}), WH=({firstViewport.Width:G9},{firstViewport.Height:G9}), Z=({firstViewport.MinDepth:G9},{firstViewport.MaxDepth:G9}); fluid VP=(0,0,{width},{height},0,1)";
        ID3DDeviceContextState* previous = null;
        try
        {
            context1->SwapDeviceContextState(privateState, &previous);
            if (previous == null) throw new InvalidOperationException("D3D11 fluid context-state capture failed");
            try
            {
                // Source RTV is held locally, but is unbound throughout the copy/resolve.
                return snapshot.Capture(device, context, target, width, height);
            }
            finally { context->ClearState(); context1->SwapDeviceContextState(previous, null); }
        }
        finally { if (previous != null) previous->Release(); target->Release(); }
    }

    public bool Draw(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11ShaderResourceView* sceneDepth,
        ReadOnlySpan<FluidVertex> vertices, Matrix4x4 viewProjection, Vector3 camera,
        uint width, uint height, uint depthWidth, uint depthHeight, bool testSceneDepth = true, FluidMaterial material = default)
    {
        EnsureResources(device, context);
        if (snapshot.View == null) return false;
        if (!Matrix4x4.Invert(viewProjection, out var inverseViewProjection)) return false;
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
                    OutputSize = new Vector4(width, height, 1, 0),
                    DepthSize = new Vector4(depthWidth, depthHeight, testSceneDepth ? 1 : 0, 0),
                    Optics = new Vector4(material.IndexOfRefraction, material.Roughness, material.RefractionStrength, material.ReflectionStrength),
                    Absorption = new Vector4(material.Absorption, (float)material.DiagnosticView),
                    InverseViewProjection = inverseViewProjection,
                };
                Upload(context, constants, &p, (uint)sizeof(Parameters));
                fixed (FluidVertex* data = vertices)
                    Upload(context, vertexBuffer, data, (uint)(vertices.Length * sizeof(FluidVertex)));
                context->OMSetRenderTargets(1, &target, null);
                var factors = stackalloc float[4] { 0, 0, 0, 0 };
                context->OMSetBlendState(blend, factors, uint.MaxValue);
                context->OMSetDepthStencilState(depthState, 0);
                context->RSSetState(rasterizer);
                var viewport = new Viewport { Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
                context->RSSetViewports(1, &viewport);
                context->IASetInputLayout(layout);
                uint stride = (uint)sizeof(FluidVertex), offset = 0;
                var vb = vertexBuffer;
                context->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
                context->IASetPrimitiveTopology((D3DPrimitiveTopology)4); // TRIANGLELIST
                context->VSSetShader(vertexShader, null, 0);
                context->PSSetShader(pixelShader, null, 0);
                var cb = constants;
                context->VSSetConstantBuffers(0, 1, &cb);
                context->PSSetConstantBuffers(0, 1, &cb);
                context->PSSetShaderResources(0, 1, &sceneDepth);
                var colorView = snapshot.View;
                context->PSSetShaderResources(1, 1, &colorView);
                var colorSampler = sampler;
                context->PSSetSamplers(0, 1, &colorSampler);
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
        var initializedAt = Stopwatch.GetTimestamp();
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
            vertexBuffer = CreateBuffer(device, (uint)(WorldGeometryRenderer.MaxVertices * sizeof(FluidVertex)), 1);
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
            var samplerDesc = new SamplerDesc { Filter = (Filter)21, AddressU = (TextureAddressMode)3, AddressV = (TextureAddressMode)3, AddressW = (TextureAddressMode)3, MaxLOD = float.MaxValue, ComparisonFunc = (ComparisonFunc)1 };
            ID3D11SamplerState* createdSampler = null;
            Marshal.ThrowExceptionForHR(device->CreateSamplerState(&samplerDesc, &createdSampler));
            sampler = createdSampler;
            ownerDevice = (nint)device;
            ownerContext = (nint)context;
        }
        catch { Dispose(); throw; }
        finally
        {
            initializationCount++;
            initializationLast = Stopwatch.GetElapsedTime(initializedAt).TotalMilliseconds;
            initializationMax = Math.Max(initializationMax, initializationLast);
        }
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
            var uv = stackalloc byte[9] { 84,69,88,67,79,79,82,68,0 };
            var inputs = stackalloc InputElementDesc[6];
            inputs[0] = new InputElementDesc { SemanticName = position, Format = (Format)6, AlignedByteOffset = 0 };
            inputs[1] = new InputElementDesc { SemanticName = normal, Format = (Format)6, AlignedByteOffset = 12 };
            inputs[2] = new InputElementDesc { SemanticName = uv, SemanticIndex = 0, Format = (Format)16, AlignedByteOffset = 24 };
            inputs[3] = new InputElementDesc { SemanticName = uv, SemanticIndex = 1, Format = (Format)16, AlignedByteOffset = 32 };
            // FluidVertex is 64 bytes: legacy surface payload 40 + center 12 + radii 12.
            inputs[4] = new InputElementDesc { SemanticName = uv, SemanticIndex = 2, Format = (Format)6, AlignedByteOffset = 40 };
            inputs[5] = new InputElementDesc { SemanticName = uv, SemanticIndex = 3, Format = (Format)6, AlignedByteOffset = 52 };
            ID3D11InputLayout* createdLayout = null;
            Marshal.ThrowExceptionForHR(device->CreateInputLayout(inputs, 6, vsData, vsSize, &createdLayout));
            layout = createdLayout;
        }
        finally { Marshal.Release(vs); if (ps != 0) Marshal.Release(ps); }
    }

    private nint Compile(byte[] bytes, string entry, string profile)
    {
        var compiledAt = Stopwatch.GetTimestamp();
        var hr = D3DCompile(bytes, (nuint)bytes.Length, "combat-world-geometry", 0, 0, entry, profile, 1 << 15, 0, out var blob, out var errors);
        compileCount++; compileLast = Stopwatch.GetElapsedTime(compiledAt).TotalMilliseconds;
        compileMax = Math.Max(compileMax, compileLast);
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
        snapshot.Dispose();
        if (sampler != null) { sampler->Release(); sampler = null; }
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
