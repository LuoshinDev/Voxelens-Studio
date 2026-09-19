using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using static Vortice.Direct3D9.D3D9;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Owns the D3D9 bridge and explicitly detaches each shared texture before releasing it.</summary>
internal sealed class ViewportD3DImageSource : D3DImage, IDisposable
{
    private readonly IDirect3D9Ex _direct3D;
    private readonly IDirect3DDevice9Ex _device;
    private IDirect3DTexture9? _texture;
    private bool _disposed;

    public ViewportD3DImageSource(Window window)
    {
        _direct3D = Direct3DCreate9Ex();
        var parameters = new Vortice.Direct3D9.PresentParameters
        {
            Windowed = true,
            SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
            DeviceWindowHandle = new WindowInteropHelper(window).EnsureHandle(),
            PresentationInterval = PresentInterval.Default,
        };
        _device = _direct3D.CreateDeviceEx(0, DeviceType.Hardware, IntPtr.Zero,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, parameters);
    }

    internal void SetRenderTargetDX10(ID3D11Texture2D? target)
    {
        Lock();
        try
        {
            SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            _texture?.Dispose();
            _texture = null;
            if(target is null) return;
            if(target.Description.Format != Vortice.DXGI.Format.B8G8R8A8_UNorm)
                throw new ArgumentException("WPF 共享目标需要 BGRA8 格式。", nameof(target));
            using var resource = target.QueryInterface<IDXGIResource>();
            IntPtr handle = resource.SharedHandle;
            _texture = _device.CreateTexture(target.Description.Width, target.Description.Height, 1,
                Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8, Pool.Default, ref handle);
            using var surface = _texture.GetSurfaceLevel(0);
            SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer);
        }
        finally { Unlock(); }
    }

    public void Dispose()
    {
        if(_disposed) return;
        _disposed = true;
        SetRenderTargetDX10(null);
        _device.Dispose();
        _direct3D.Dispose();
    }
}
