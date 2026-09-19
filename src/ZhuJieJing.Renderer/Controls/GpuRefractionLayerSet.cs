using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Full-resolution HDR layers whose readable prefix never overlaps the slice being rendered.</summary>
internal sealed class GpuRefractionLayerSet : IDisposable
{
    internal const int MaximumLayers = 16;
    internal const long MaximumBytes = 1L << 30;
    private const int MaximumTextureDimension = 16384;
    private readonly ID3D11RenderTargetView[] _renderViews;
    private readonly ID3D11ShaderResourceView?[] _completedViews;
    private bool _disposed;

    internal ID3D11Texture2D Texture { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int LayerCount => _renderViews.Length;
    internal long EstimatedBytes => (long)Width * Height * LayerCount * 8;

    private GpuRefractionLayerSet(ID3D11Texture2D texture, int width, int height,
        ID3D11RenderTargetView[] renderViews, ID3D11ShaderResourceView?[] completedViews)
    {
        Texture = texture; Width = width; Height = height;
        _renderViews = renderViews; _completedViews = completedViews;
    }

    internal static bool CanAllocate(int width, int height, int layers) =>
        width is > 0 and <= MaximumTextureDimension && height is > 0 and <= MaximumTextureDimension &&
        layers is > 0 and <= MaximumLayers && (long)width * height <= MaximumBytes / (8L * layers);

    internal static GpuRefractionLayerSet Create(ID3D11Device device, int width, int height, int layers)
    {
        ArgumentNullException.ThrowIfNull(device);
        if(!CanAllocate(width, height, layers))
            throw new ArgumentOutOfRangeException(nameof(layers), "Full-resolution refraction layers exceed the 16-layer, 1 GiB or D3D11 dimension limit.");
        ID3D11Texture2D texture = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float,
            (uint)width, (uint)height, (uint)layers, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        var renderViews = new ID3D11RenderTargetView[layers];
        var completedViews = new ID3D11ShaderResourceView?[layers + 1];
        try
        {
            for(int index = 0; index < layers; index++)
                renderViews[index] = device.CreateRenderTargetView(texture, new RenderTargetViewDescription(
                    RenderTargetViewDimension.Texture2DArray, Format.R16G16B16A16_Float, 0, (uint)index, 1));
            // With one mip per slice, the RTV/subresource at index N is disjoint from this [0, N) SRV.
            // Cache every possible prefix once; changing water height order creates no per-frame views.
            for(int count = 1; count <= layers; count++)
                completedViews[count] = device.CreateShaderResourceView(texture, new ShaderResourceViewDescription(
                    ShaderResourceViewDimension.Texture2DArray, Format.R16G16B16A16_Float, 0, 1, 0, (uint)count));
            return new GpuRefractionLayerSet(texture, width, height, renderViews, completedViews);
        }
        catch
        {
            foreach(ID3D11ShaderResourceView? view in completedViews) view?.Dispose();
            foreach(ID3D11RenderTargetView? view in renderViews) view?.Dispose();
            texture.Dispose();
            throw;
        }
    }

    internal ID3D11RenderTargetView GetRenderView(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if((uint)index >= (uint)LayerCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _renderViews[index];
    }

    internal ID3D11ShaderResourceView? GetCompletedView(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if((uint)count > (uint)LayerCount) throw new ArgumentOutOfRangeException(nameof(count));
        return _completedViews[count];
    }

    public void Dispose()
    {
        if(_disposed) return;
        _disposed = true;
        foreach(ID3D11ShaderResourceView? view in _completedViews) view?.Dispose();
        foreach(ID3D11RenderTargetView view in _renderViews) view.Dispose();
        Texture.Dispose();
    }
}
