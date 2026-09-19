using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport
{
    private ID3D11ComputeShader? _emissionCoverageCompute, _shadowRangeCompute, _rangeReductionCompute;
    private GpuEffectRangeTexture? _emissionCoverage, _sunShadowRanges;
    private bool _sunShadowRangesReady;

    private void BuildEmissionCoverage(ID3D11DeviceContext context)
    {
        int width = (_postProcessTarget!.Width + 63) / 64;
        int height = (_postProcessTarget.Height + 63) / 64;
        EnsureEffectRangeTexture(ref _emissionCoverage, width, height, 1);
        // The material target contains this frame's final opaque AND transparent emission.
        context.OMSetRenderTargets([], null);
        try
        {
            context.CSSetShader(_emissionCoverageCompute);
            context.CSSetShaderResource(6, _postProcessTarget.MaterialView);
            context.CSSetUnorderedAccessView(0, _emissionCoverage!.WriteViews[0]);
            context.Dispatch((uint)width, (uint)height, 1);
        }
        finally { UnbindEffectCompute(context); }
    }

    private void BuildSunShadowRanges(ID3D11DeviceContext context)
    {
        if(!_enhancedLightingEnabled) return;
        int width = checked((int)_shadowMap!.Texture.Description.Width) / 16;
        int height = checked((int)_shadowMap.Texture.Description.Height) / 16;
        int levels = 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(width, height));
        EnsureEffectRangeTexture(ref _sunShadowRanges, width, height, levels);
        _sunShadowRangesReady = false;
        context.OMSetRenderTargets([], null);
        try
        {
            // Rebuild only with the actual shadow map. No camera-history or CPU readback is used.
            context.CSSetShader(_shadowRangeCompute);
            context.CSSetShaderResource(1, _shadowMap.View);
            context.CSSetUnorderedAccessView(0, _sunShadowRanges!.WriteViews[0]);
            context.Dispatch((uint)width, (uint)height, 1);
            context.CSSetShaderResource(1, null!);
            context.CSSetUnorderedAccessView(0, null!);
            context.CSSetShader(_rangeReductionCompute);
            for(int level = 1; level < levels; level++)
            {
                // Restrict the source view to the preceding mip, disjoint from the destination UAV.
                context.CSSetShaderResource(15, _sunShadowRanges.LevelViews[level - 1]);
                context.CSSetUnorderedAccessView(0, _sunShadowRanges.WriteViews[level]);
                context.Dispatch((uint)(Math.Max(width >> level, 1) + 7) / 8,
                    (uint)(Math.Max(height >> level, 1) + 7) / 8, 1);
                context.CSSetShaderResource(15, null!);
                context.CSSetUnorderedAccessView(0, null!);
            }
            _sunShadowRangesReady = true;
        }
        finally { UnbindEffectCompute(context); }
    }

    private void EnsureEffectRangeTexture(ref GpuEffectRangeTexture? target, int width, int height, int levels)
    {
        if(target is not null && target.Width == width && target.Height == height && target.LevelViews.Length == levels) return;
        GpuEffectRangeTexture replacement = new(_device!, width, height, levels);
        GpuEffectRangeTexture? previous = target;
        target = replacement;
        previous?.Dispose();
    }

    private static void UnbindEffectCompute(ID3D11DeviceContext context)
    {
        context.CSSetUnorderedAccessView(0, null!);
        context.CSSetShaderResource(1, null!);
        context.CSSetShaderResource(6, null!);
        context.CSSetShaderResource(15, null!);
        context.CSSetShader(null!);
    }

    private void ReleaseEffectCoverageTargets()
    {
        _emissionCoverage?.Dispose(); _emissionCoverage = null;
        _sunShadowRanges?.Dispose(); _sunShadowRanges = null;
        _sunShadowRangesReady = false;
    }

    private void DisposeEffectCoverage()
    {
        ReleaseEffectCoverageTargets();
        _emissionCoverageCompute?.Dispose(); _emissionCoverageCompute = null;
        _shadowRangeCompute?.Dispose(); _shadowRangeCompute = null;
        _rangeReductionCompute?.Dispose(); _rangeReductionCompute = null;
    }

    private sealed class GpuEffectRangeTexture : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        public ID3D11Texture2D Texture { get; }
        public ID3D11ShaderResourceView View { get; }
        public ID3D11ShaderResourceView[] LevelViews { get; }
        public ID3D11UnorderedAccessView[] WriteViews { get; }

        public GpuEffectRangeTexture(ID3D11Device device, int width, int height, int levels)
        {
            Width = width;
            Height = height;
            LevelViews = new ID3D11ShaderResourceView[levels];
            WriteViews = new ID3D11UnorderedAccessView[levels];
            Texture = device.CreateTexture2D(new Texture2DDescription(Format.R32G32_Float,
                (uint)width, (uint)height, 1, (uint)levels, BindFlags.ShaderResource | BindFlags.UnorderedAccess));
            try
            {
                View = device.CreateShaderResourceView(Texture);
                for(int level = 0; level < levels; level++)
                {
                    LevelViews[level] = device.CreateShaderResourceView(Texture,
                        new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D,
                            Format.R32G32_Float, (uint)level, 1));
                    WriteViews[level] = device.CreateUnorderedAccessView(Texture,
                        new UnorderedAccessViewDescription(UnorderedAccessViewDimension.Texture2D,
                            Format.R32G32_Float, (uint)level));
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach(var view in WriteViews) view?.Dispose();
            foreach(var view in LevelViews) view?.Dispose();
            View?.Dispose();
            Texture.Dispose();
        }
    }
}
