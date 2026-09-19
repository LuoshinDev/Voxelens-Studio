using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport
{
    private static readonly Lazy<StaticSkyPixels> SkyPixels = new(LoadStaticSky, LazyThreadSafetyMode.ExecutionAndPublication);
    private ID3D11Texture2D? _staticSkyTexture;
    private ID3D11ShaderResourceView? _staticSkyView;

    private void PrepareStaticSky(ID3D11DeviceContext context)
    {
        if(!_enhancedLightingEnabled || !_environment.CloudsEnabled || _staticSkyView is not null) return;
        StaticSkyPixels sky = SkyPixels.Value;
        try
        {
            _staticSkyTexture = _device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm_SRgb,
                (uint)sky.Width, (uint)sky.Height, 1, 1, BindFlags.ShaderResource));
            context.UpdateSubresource(sky.Pixels.AsSpan(), _staticSkyTexture, 0, (uint)(sky.Width * 4), 0);
            _staticSkyView = _device.CreateShaderResourceView(_staticSkyTexture);
        }
        catch { DisposeStaticSky(); throw; }
    }

    private static StaticSkyPixels LoadStaticSky()
    {
        using Stream stream = typeof(VoxelViewport).Assembly.GetManifestResourceStream("ZhuJieJing.Renderer.Assets.StaticSky.png")
            ?? throw new InvalidOperationException("缺少内置静态天空贴图。");
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private void BindStaticSky(ID3D11DeviceContext context) => context.PSSetShaderResource(17, _staticSkyView!);

    private void DisposeStaticSky()
    {
        _staticSkyView?.Dispose(); _staticSkyView = null;
        _staticSkyTexture?.Dispose(); _staticSkyTexture = null;
    }

    private sealed record StaticSkyPixels(int Width, int Height, byte[] Pixels);
}
