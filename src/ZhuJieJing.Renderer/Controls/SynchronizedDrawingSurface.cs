using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Wpf;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Renders privately while WPF stays responsive, publishing only completed GPU frames.</summary>
public sealed class SynchronizedDrawingSurface : ViewportDrawingSurface
{
    internal static readonly TimeSpan GpuWaitWarningThreshold = TimeSpan.FromMilliseconds(250);
    private ID3D11Device1? _device;
    private ID3D11Query? _frameCompletionQuery, _copyCompletionQuery;
    private ID3D11Texture2D? _sceneColor, _sceneDepth;
    private bool _framePending;
    private long _submittedFrame, _pendingFrame;
    private int _renderingSuspended;
    private int _captureRenderLeases;
    private DispatcherTimer? _captureFramePump;
    private DispatcherTimer? _pendingFrameRetry;
    private long _pendingSubmittedAt;
    private int _pendingPollMisses;

    private void RetryPendingFrame(object? sender, EventArgs args)
    {
        _pendingFrameRetry?.Stop();
        if(_framePending && IsVisible && Window.GetWindow(this)?.IsActive == true && !IsRenderingSuspended)
            DrawRequestedFrame();
    }
    protected override bool UsesExplicitFramePump => _captureRenderLeases > 0;
    protected override bool HasPendingWork => _captures.Count != 0 || _captureRenderLeases != 0;
    internal IDisposable RequestCaptureFrames()
    {
        Dispatcher.VerifyAccess();
        _captureRenderLeases++;
        if(_captureRenderLeases == 1)
        {
            // WPF throttles composition callbacks for offscreen windows. Explicit capture requests
            // must still finish independently; normal visible preview stays on the compositor clock.
            _captureFramePump ??= new DispatcherTimer(TimeSpan.FromMilliseconds(2), DispatcherPriority.Background,
                OnCaptureFramePump, Dispatcher);
            _captureFramePump.Start();
        }
        Invalidate();
        return new CaptureFrameLease(this);
    }

    private void OnCaptureFramePump(object? sender, EventArgs args)
    {
        if(_captureRenderLeases > 0) DrawRequestedFrame();
    }

    private void ReleaseCaptureFrames()
    {
        Dispatcher.VerifyAccess();
        if(--_captureRenderLeases == 0) _captureFramePump?.Stop();
    }
    private readonly List<CaptureRequest> _captures = [];
    public ID3D11RenderTargetView? SceneColorTextureView { get; private set; }
    public ID3D11DepthStencilView? SceneDepthStencilView { get; private set; }
    public event EventHandler? FramePresented;
    public bool IsRenderingSuspended
    {
        get => Volatile.Read(ref _renderingSuspended) != 0;
        set => Interlocked.Exchange(ref _renderingSuspended, value ? 1 : 0);
    }

    internal Task<BitmapSource> CaptureSceneAsync(CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        _captures.Add(new CaptureRequest(_submittedFrame + 1, completion, cancellationToken));
        Invalidate();
        return completion.Task.WaitAsync(cancellationToken);
    }

    protected override void RaiseLoadContent(DrawingSurfaceEventArgs args)
    {
        _device = args.Device;
        _frameCompletionQuery = args.Device.CreateQuery(QueryType.Event);
        _copyCompletionQuery = args.Device.CreateQuery(QueryType.Event);
        if(Source is ViewportD3DImageSource imageSource && imageSource.IsFrontBufferAvailable && ColorTextureView is not null)
        {
            imageSource.Lock();
            try
            {
                args.Context.ClearRenderTargetView(ColorTextureView, new Vortice.Mathematics.Color4(0.76f, 0.84f, 0.96f, 1));
                WaitForCopy(args.Context);
                imageSource.AddDirtyRect(new Int32Rect(0, 0, TextureWidth, TextureHeight));
            }
            finally { imageSource.Unlock(); }
        }
        base.RaiseLoadContent(args);
        if(_captureRenderLeases > 0) _captureFramePump?.Start();
    }

    protected override void RaiseDraw(ViewportDrawEventArgs args)
    {
        _captures.RemoveAll(request => request.Token.IsCancellationRequested);
        if(IsRenderingSuspended && !HasPendingWork) return;
        if(Source is not ViewportD3DImageSource imageSource || !imageSource.IsFrontBufferAvailable) return;
        if(_device is null || _frameCompletionQuery is null || _device.DeviceRemovedReason.Failure)
        {
            FailCaptures(new InvalidOperationException("显卡设备已断开，请重新打开预览窗口。"));
            return;
        }
        // Scene shading never waits on the UI thread; WPF keeps the previous complete shared image.
        if(_framePending)
        {
            if(!args.Context.IsDataAvailable(_frameCompletionQuery, AsyncGetDataFlags.DoNotFlush))
            {
                _pendingPollMisses++;
                // A missed compositor callback must not defer a nearly finished frame by a full refresh.
                // One queued retry yields to input and never busy-waits or flushes the GPU.
                if(_captureRenderLeases == 0 && IsVisible && Window.GetWindow(this)?.IsActive == true)
                {
                    _pendingFrameRetry ??= new DispatcherTimer(TimeSpan.FromMilliseconds(2), DispatcherPriority.Background, RetryPendingFrame, Dispatcher);
                    _pendingFrameRetry.Start();
                }
                return;
            }
            _pendingFrameRetry?.Stop();
            Profiler?.SubmissionWait(_pendingFrame, TextureWidth, TextureHeight,
                Stopwatch.GetElapsedTime(_pendingSubmittedAt).TotalMilliseconds, _pendingPollMisses);
            _framePending = false;
            if(_sceneColor is not null && _sceneColor.Description.Width == TextureWidth && _sceneColor.Description.Height == TextureHeight)
            {
                long lockStarted = Stopwatch.GetTimestamp();
                imageSource.Lock();
                long lockCompleted = Stopwatch.GetTimestamp();
                long copyCompleted = lockCompleted;
                try
                {
                    args.Context.OMSetRenderTargets([], null);
                    args.Context.CopyResource(ColorTexture!, _sceneColor);
                    // Only the final copy is fenced. Never expose unfinished writes to WPF's D3D9 compositor.
                    WaitForCopy(args.Context);
                    copyCompleted = Stopwatch.GetTimestamp();
                    imageSource.AddDirtyRect(new Int32Rect(0, 0, TextureWidth, TextureHeight));
                }
                finally { imageSource.Unlock(); }
                Profiler?.Presented(_pendingFrame, TextureWidth, TextureHeight,
                    Stopwatch.GetElapsedTime(lockStarted, lockCompleted).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(lockCompleted, copyCompleted).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(copyCompleted).TotalMilliseconds);
                FramePresented?.Invoke(this, EventArgs.Empty);
                CompleteCaptures(args.Context, _pendingFrame);
            }
        }
        EnsurePrivateTargets(args.Device);
        args.Context.OMSetRenderTargets(SceneColorTextureView!, SceneDepthStencilView);
        Profiler?.BeginFrame(args.Context, _submittedFrame + 1, TextureWidth, TextureHeight);
        try { base.RaiseDraw(args); }
        finally { Profiler?.EndFrame(args.Context); }
        args.Context.OMSetRenderTargets([], null);
        args.Context.End(_frameCompletionQuery);
        args.Context.Flush();
        _pendingFrame = ++_submittedFrame;
        _pendingSubmittedAt = Stopwatch.GetTimestamp();
        _pendingPollMisses = 0;
        _framePending = true;
    }

    private void EnsurePrivateTargets(ID3D11Device device)
    {
        if(_sceneColor is not null && _sceneColor.Description.Width == TextureWidth && _sceneColor.Description.Height == TextureHeight) return;
        DisposePrivateTargets();
        _sceneColor = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)TextureWidth, (uint)TextureHeight, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        SceneColorTextureView = device.CreateRenderTargetView(_sceneColor);
        _sceneDepth = device.CreateTexture2D(new Texture2DDescription(Format.D32_Float, (uint)TextureWidth, (uint)TextureHeight, 1, 1, BindFlags.DepthStencil));
        SceneDepthStencilView = device.CreateDepthStencilView(_sceneDepth);
    }

    private void CompleteCaptures(ID3D11DeviceContext context, long frame)
    {
        CaptureRequest[] ready = _captures.Where(request => request.MinimumFrame <= frame).ToArray();
        if(ready.Length == 0 || _sceneColor is null || _device is null) return;
        try
        {
            Texture2DDescription description = _sceneColor.Description;
            description.BindFlags = BindFlags.None;
            description.Usage = ResourceUsage.Staging;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            using ID3D11Texture2D staging = _device.CreateTexture2D(description);
            context.CopyResource(staging, _sceneColor);
            WaitForCopy(context);
            MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int width = checked((int)description.Width), height = checked((int)description.Height), stride = checked(width * 4);
                byte[] pixels = new byte[checked(stride * height)];
                for(int row = 0; row < height; row++) Marshal.Copy(mapped.DataPointer + checked(row * (int)mapped.RowPitch), pixels, row * stride, stride);
                BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                bitmap.Freeze();
                foreach(CaptureRequest request in ready) request.Completion.TrySetResult(bitmap);
            }
            finally { context.Unmap(staging, 0); }
        }
        catch(Exception exception) { foreach(CaptureRequest request in ready) request.Completion.TrySetException(exception); }
        finally { foreach(CaptureRequest request in ready) _captures.Remove(request); }
    }

    private void WaitForCopy(ID3D11DeviceContext context)
    {
        if(_copyCompletionQuery is null) return;
        context.End(_copyCompletionQuery);
        context.Flush();
        var spinner = new SpinWait();
        long started = Stopwatch.GetTimestamp();
        while(!context.IsDataAvailable(_copyCompletionQuery, AsyncGetDataFlags.DoNotFlush))
        {
            if(_device is null || _device.DeviceRemovedReason.Failure) throw new InvalidOperationException("显卡设备在提交画面时断开。");
            if(Stopwatch.GetElapsedTime(started) >= GpuWaitWarningThreshold) Thread.Sleep(1);
            else spinner.SpinOnce();
        }
    }

    protected override void RaiseUnloadContent(DrawingSurfaceEventArgs args)
    {
        _captureFramePump?.Stop();
        _pendingFrameRetry?.Stop();
        try { base.RaiseUnloadContent(args); }
        finally
        {
            FailCaptures(new InvalidOperationException("预览已关闭。"));
            DisposePrivateTargets();
            _frameCompletionQuery?.Dispose(); _frameCompletionQuery = null;
            _copyCompletionQuery?.Dispose(); _copyCompletionQuery = null;
            _device = null;
            _framePending = false;
        }
    }

    private void DisposePrivateTargets()
    {
        SceneColorTextureView?.Dispose(); SceneColorTextureView = null;
        SceneDepthStencilView?.Dispose(); SceneDepthStencilView = null;
        _sceneColor?.Dispose(); _sceneColor = null;
        _sceneDepth?.Dispose(); _sceneDepth = null;
    }

    private void FailCaptures(Exception exception)
    {
        foreach(CaptureRequest request in _captures) request.Completion.TrySetException(exception);
        _captures.Clear();
    }

    private sealed record CaptureRequest(long MinimumFrame, TaskCompletionSource<BitmapSource> Completion, CancellationToken Token);
    private sealed class CaptureFrameLease(SynchronizedDrawingSurface owner) : IDisposable
    {
        private SynchronizedDrawingSurface? _owner = owner;
        public void Dispose()
        {
            SynchronizedDrawingSurface? current = Interlocked.Exchange(ref _owner, null);
            current?.ReleaseCaptureFrames();
        }
    }
}
