using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport
{
    // Test-only A/B control; the product continues to expose one enhancement switch.
    internal bool SunOpticsEnabledForValidation { get; set; } = true;
    private ID3D11PixelShader? _sunOcclusionPixelShader;
    private ID3D11Texture2D? _sunOcclusionTexture;
    private ID3D11RenderTargetView? _sunOcclusionRenderView;
    private ID3D11ShaderResourceView? _sunOcclusionView;
    private bool _sunOcclusionCapturedThisFrame;

    private void CaptureSunOcclusionBeforeTransparency(ID3D11DeviceContext context)
    {
        if(!_enhancedLightingEnabled || _renderingReflection || _postProcessTarget is null) return;
        UnbindPixelShaderResources(context);
        UnbindGeometryInputs(context);
        DrawSunOcclusion(context);
        _sunOcclusionCapturedThisFrame = true;
        context.OMSetRenderTargets([_postProcessTarget.RenderView, _postProcessTarget.SurfaceRenderView, _postProcessTarget.MaterialRenderView], _postProcessTarget.DepthView);
        context.RSSetViewport(new Viewport(0, 0, _postProcessTarget.Width, _postProcessTarget.Height, 0, 1));
        BindVoxelPipeline(context);
    }

    private void DrawSunOcclusion(ID3D11DeviceContext context)
    {
        if(_sunOcclusionTexture is null)
        {
            _sunOcclusionTexture = _device!.CreateTexture2D(new Texture2DDescription(Format.R32G32_Float, 1, 1, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource));
            _sunOcclusionRenderView = _device.CreateRenderTargetView(_sunOcclusionTexture);
            _sunOcclusionView = _device.CreateShaderResourceView(_sunOcclusionTexture);
        }
        // Sample the whole square solar disc once per completed scene, not once per full-screen pixel.
        context.OMSetRenderTargets(_sunOcclusionRenderView!, null);
        context.RSSetViewport(new Viewport(0, 0, 1, 1, 0, 1));
        context.RSSetState(_voxelRasterizerState);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(null);
        context.VSSetShader(_skyVertexShader);
        context.VSSetConstantBuffer(0, _cameraBuffer);
        context.PSSetShader(_sunOcclusionPixelShader);
        BindStaticSky(context);
        context.PSSetConstantBuffer(0, _cameraBuffer);
        context.PSSetShaderResource(4, _postProcessTarget!.DepthShaderView);
        context.PSSetSampler(2, _postProcessSampler);
        context.OMSetBlendState(_opaqueBlendState);
        context.OMSetDepthStencilState(_depthDisabledState, 0);
        context.Draw(3, 0);
        UnbindPixelShaderResources(context);
    }

    private void ReleaseSunOcclusionTarget()
    {
        _sunOcclusionView?.Dispose(); _sunOcclusionView = null;
        _sunOcclusionRenderView?.Dispose(); _sunOcclusionRenderView = null;
        _sunOcclusionTexture?.Dispose(); _sunOcclusionTexture = null;
    }

    private void DisposeSunOptics()
    {
        ReleaseSunOcclusionTarget();
        _sunOcclusionPixelShader?.Dispose(); _sunOcclusionPixelShader = null;
    }
}
