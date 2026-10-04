// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>Owns only a detached single-sample texture, never a swapchain resource reference.</summary>
internal sealed unsafe class SceneColorSnapshot : IDisposable
{
    private ID3D11Texture2D* texture;
    private ID3D11ShaderResourceView* view;
    private uint width, height, samples;
    private Format format;
    private nint owner;
    public ID3D11ShaderResourceView* View => view;
    public string Status { get; private set; } = "Scene snapshot not captured";

    public bool Capture(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11RenderTargetView* target,
        uint outputWidth, uint outputHeight)
    {
        // Copy/resolve reads the source subresource, independent of rasterizer
        // viewport state. The SetTarget marker verifies the main backbuffer;
        // reject incompatible source dimensions below, not a preceding pass's VP.
        ID3D11Resource* resource = null;
        ID3D11Texture2D* source = null;
        try
        {
            target->GetResource(&resource);
            if (resource == null) return false;
            var guid = ID3D11Texture2D.Guid;
            if (resource->QueryInterface(&guid, (void**)&source) < 0 || source == null) return false;
            Texture2DDesc desc; source->GetDesc(&desc);
            RenderTargetViewDesc rt; target->GetDesc(&rt);
            // Restrict to known linear/sRGB color formats with explicit typed RTV views.
            var f = (int)rt.Format;
            if (f != 2 && f != 10 && f != 24 && f != 28 && f != 29 && f != 87 && f != 91)
            { Status = $"Fluid skipped: unsupported color format {rt.Format}"; return false; }
            if (outputWidth == 0 || outputHeight == 0 || desc.Width != outputWidth || desc.Height != outputHeight
                || desc.ArraySize != 1 || desc.MipLevels != 1 || desc.SampleDesc.Count == 0
                || (desc.SampleDesc.Count == 1 ? (int)rt.ViewDimension != 4 : (int)rt.ViewDimension != 6))
            { Status = $"Fluid skipped: source={desc.Width}x{desc.Height}, output={outputWidth}x{outputHeight}, array={desc.ArraySize}, mips={desc.MipLevels}, samples={desc.SampleDesc.Count}, view={rt.ViewDimension}"; return false; }
            uint support = 0;
            if (device->CheckFormatSupport(rt.Format, &support) < 0 || (support & 0x200) == 0
                || (desc.SampleDesc.Count > 1 && (support & 0x40000) == 0))
            { Status = "Fluid skipped: color sampling or MSAA resolve unsupported"; return false; }
            if (texture == null || owner != (nint)device || width != desc.Width || height != desc.Height
                || samples != desc.SampleDesc.Count || format != rt.Format)
            {
                Dispose();
                var copyDesc = new Texture2DDesc {
                    Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = rt.Format,
                    SampleDesc = new SampleDesc { Count = 1 }, Usage = (Usage)0, BindFlags = 8,
                };
                ID3D11Texture2D* created = null;
                Marshal.ThrowExceptionForHR(device->CreateTexture2D(&copyDesc, null, &created));
                texture = created;
                ID3D11ShaderResourceView* createdView = null;
                Marshal.ThrowExceptionForHR(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &createdView));
                view = createdView;
                owner = (nint)device; width = desc.Width; height = desc.Height; samples = desc.SampleDesc.Count; format = rt.Format;
            }
            if (desc.SampleDesc.Count > 1)
                context->ResolveSubresource((ID3D11Resource*)texture, 0, resource, 0, rt.Format);
            else context->CopyResource((ID3D11Resource*)texture, resource);
            var encoding = f == 29 || f == 91 ? "sRGB view decode/encode" : f == 2 || f == 10 ? "float view" : "UNORM transfer needs gamma verification";
            Status = $"Scene snapshot {width}x{height}, {format}, samples={samples}, {(samples > 1 ? "resolve" : "copy")}; {encoding}; studio reflection fallback";
            return true;
        }
        finally
        {
            if (source != null) source->Release();
            if (resource != null) resource->Release();
        }
    }
    public void Dispose()
    {
        if (view != null) { view->Release(); view = null; }
        if (texture != null) { texture->Release(); texture = null; }
        owner = 0;
    }
}
