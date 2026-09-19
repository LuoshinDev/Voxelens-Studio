// D3D11/WPF interop host based on Vortice.Windows DrawingSurface (MIT, Copyright Amer Koleci and Contributors).
// Lifecycle and presentation scheduling are owned here so Closed + Unloaded cannot destroy a device twice.
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Wpf;
using static Vortice.Direct3D11.D3D11;

namespace ZhuJieJing.Renderer.Controls;

public sealed class ViewportDrawEventArgs(ViewportDrawingSurface surface, ID3D11Device1 device, ID3D11DeviceContext1 context) : DrawingSurfaceEventArgs(device, context)
{
    public ViewportDrawingSurface Surface { get; } = surface;
}

public class ViewportDrawingSurface : Image
{
    private ID3D11Device1? _hostDevice;
    private ID3D11DeviceContext1? _hostContext;
    private ViewportD3DImageSource? _image;
    private Window? _window;
    private bool _needsRefresh = true, _rendering, _shuttingDown;
    private int _renderScale = 1;
    internal int RenderScale
    {
        get => _renderScale;
        set
        {
            if(_renderScale == value) return;
            _renderScale = value;
            if(_hostDevice is not null) CreateTargets();
        }
    }
    public bool AlwaysRefresh { get; set; }
    protected virtual bool HasPendingWork => false;
    protected virtual bool UsesExplicitFramePump => false;
    public int TextureWidth { get; private set; }
    public int TextureHeight { get; private set; }
    public ID3D11Texture2D? ColorTexture { get; private set; }
    public ID3D11RenderTargetView? ColorTextureView { get; private set; }
    public event EventHandler<DrawingSurfaceEventArgs>? LoadContent;
    public event EventHandler<DrawingSurfaceEventArgs>? UnloadContent;
    public event EventHandler<ViewportDrawEventArgs>? Draw;
    internal ViewportRenderProfiler? Profiler { get; private set; }

    public ViewportDrawingSurface()
    {
        Stretch = Stretch.Fill;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += HostLoaded;
        Unloaded += HostUnloaded;
    }

    public void Invalidate() => _needsRefresh = true;

    internal string StartDebugRecording(string directory)
    {
        Dispatcher.VerifyAccess();
        if(_hostDevice is null) throw new InvalidOperationException("视口尚未准备好。");
        if(Profiler is not null) throw new InvalidOperationException("性能记录已在运行。");
        Profiler = new ViewportRenderProfiler(_hostDevice, directory);
        return Profiler.OutputPath;
    }

    internal Task StopDebugRecording()
    {
        Dispatcher.VerifyAccess();
        ViewportRenderProfiler? profiler = Profiler;
        Profiler = null;
        profiler?.Dispose();
        return profiler?.Completion ?? Task.CompletedTask;
    }

    private void HostLoaded(object sender, RoutedEventArgs e)
    {
        if(DesignerProperties.GetIsInDesignMode(this) || _hostDevice is not null) return;
        D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0,
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        using(device) using(context)
        {
            _hostDevice = device!.QueryInterface<ID3D11Device1>();
            _hostContext = context!.QueryInterface<ID3D11DeviceContext1>();
        }
        _window = Window.GetWindow(this) ?? throw new InvalidOperationException("渲染视口必须附着到桌面窗口。");
        Profiler = ViewportRenderProfiler.Create(_hostDevice);
        _window.Closed += HostClosed;
        _image = new ViewportD3DImageSource(_window);
        _image.IsFrontBufferAvailableChanged += FrontBufferChanged;
        Source = _image;
        CreateTargets();
        RaiseLoadContent(new DrawingSurfaceEventArgs(_hostDevice, _hostContext));
        CompositionTarget.Rendering += HostRendering;
        _rendering = true;
        _needsRefresh = true;
    }

    private void HostRendering(object? sender, EventArgs e)
    {
        if(e is RenderingEventArgs rendering) Profiler?.Callback(rendering.RenderingTime);
        if(UsesExplicitFramePump) return;
        DrawRequestedFrame();
    }

    protected void DrawRequestedFrame()
    {
        if(!_rendering || _hostDevice is null || _hostContext is null || ColorTexture is null || _image is null || !_image.IsFrontBufferAvailable) return;
        if((!IsVisible || _window?.WindowState == WindowState.Minimized) && !HasPendingWork) return;
        if(!_needsRefresh && !AlwaysRefresh && !HasPendingWork) return;
        _needsRefresh = false;
        RaiseDraw(new ViewportDrawEventArgs(this, _hostDevice, _hostContext));
    }

    private void CreateTargets()
    {
        if(_hostDevice is null || _image is null) return;
        _image.SetRenderTargetDX10(null);
        ColorTextureView?.Dispose();
        ColorTexture?.Dispose();
        // Match shader pixel coordinates exactly. Explicit off-screen captures remain independent of desktop DPI.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        TextureWidth = Math.Max((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX * _renderScale), 1);
        TextureHeight = Math.Max((int)Math.Ceiling(ActualHeight * dpi.DpiScaleY * _renderScale), 1);
        ColorTexture = _hostDevice.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)TextureWidth, (uint)TextureHeight, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource)
        {
            MiscFlags = ResourceOptionFlags.Shared,
        });
        ColorTextureView = _hostDevice.CreateRenderTargetView(ColorTexture);
        _image.SetRenderTargetDX10(ColorTexture);
        _needsRefresh = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        if(_hostDevice is not null) CreateTargets();
        base.OnRenderSizeChanged(sizeInfo);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if(_hostDevice is not null) CreateTargets();
    }

    private void FrontBufferChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if(_image?.IsFrontBufferAvailable == true) CreateTargets();
    }

    private void HostUnloaded(object sender, RoutedEventArgs e) => Shutdown();
    private void HostClosed(object? sender, EventArgs e) => Shutdown();

    internal void Shutdown()
    {
        if(_shuttingDown || _hostDevice is null) return;
        _shuttingDown = true;
        _rendering = false;
        CompositionTarget.Rendering -= HostRendering;
        if(_window is not null) _window.Closed -= HostClosed;
        try
        {
            if(_hostContext is not null) RaiseUnloadContent(new DrawingSurfaceEventArgs(_hostDevice, _hostContext));
        }
        finally
        {
            if(_image is not null)
            {
                _image.IsFrontBufferAvailableChanged -= FrontBufferChanged;
                _image.SetRenderTargetDX10(null);
            }
            Source = null;
            _image?.Dispose(); _image = null;
            ColorTextureView?.Dispose(); ColorTextureView = null;
            ColorTexture?.Dispose(); ColorTexture = null;
            _hostContext?.ClearState(); _hostContext?.Flush();
            Profiler?.Dispose(); Profiler = null;
            _hostContext?.Dispose(); _hostContext = null;
            _hostDevice.Dispose(); _hostDevice = null;
            _window = null;
            _shuttingDown = false;
        }
    }

    protected virtual void RaiseLoadContent(DrawingSurfaceEventArgs args) => LoadContent?.Invoke(this, args);
    protected virtual void RaiseUnloadContent(DrawingSurfaceEventArgs args) => UnloadContent?.Invoke(this, args);
    protected virtual void RaiseDraw(ViewportDrawEventArgs args) => Draw?.Invoke(this, args);
}
