using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Camera;
using ZhuJieJing.Renderer.Meshing;
using ZhuJieJing.Renderer.Minecraft;
using ZhuJieJing.Renderer.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Wpf;
using DrawEventArgs = ZhuJieJing.Renderer.Controls.ViewportDrawEventArgs;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport : UserControl, IDisposable
{
    public const float MinimumKeyboardMovementSpeed = 1f;
    public const float MaximumKeyboardMovementSpeed = 256f;
    public const float MinimumObserverMouseSensitivity = 0.01f;
    public const float MaximumObserverMouseSensitivity = 1.5f;
    public const float DefaultObserverMouseSensitivity = 1f;

    private const int MaximumUploadsPerFrame = 48;
    private const long MaximumUploadBytesPerFrame = 64L * 1024 * 1024;
    private static readonly TimeSpan StandardUploadTimePerFrame = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan EnhancedUploadTimePerFrame = TimeSpan.FromMilliseconds(4);
    private Vector3 _lastSelectionDirection;
    private readonly List<GpuSectionGeometry> _visibleSectionGeometry = [];
    private const double MinimumShadowRefreshIntervalSeconds = 1d / 10d;
    private const double FrameRateUpdateIntervalSeconds = 0.75d;
    private const double MaximumFrameElapsedSeconds = 0.10d;
    private const float ShadowCasterRadius = ShadowCameraMath.DefaultRadius;
    private const int VirtualKeyEscape = 0x1B;
    private const string ShaderResourceName = "ZhuJieJing.Renderer.Assets.VoxelPreview.hlsl";
    private static readonly int VertexStride = Marshal.SizeOf<VoxelVertex>();

    private readonly OrbitCamera _camera = new();
    private readonly ViewportEnvironmentSettings _environment = new();
    private readonly Stopwatch _animationClock = Stopwatch.StartNew();
    private readonly ActiveSectionRefreshPolicy _activeSectionRefreshPolicy = new();
    private readonly SemaphoreSlim _sectionMeshBuildGate = new(1, 1);
    private readonly object _sectionMeshTaskGate = new();
    private readonly HashSet<Task> _sectionMeshTasks = [];
    private readonly Task<ShaderBytecodeBundle> _shaderBytecodePreparation;
    private CancellationTokenSource _sectionMeshEpochCancellation = new();
    private SectionRenderCache _sectionCache;
    private readonly Dictionary<SectionCoordinate, GpuSectionGeometry> _sectionGpuCache = [];
    private readonly Dictionary<SectionCoordinate, GpuSectionGeometry> _stagedSectionGpuCache = [];
    private readonly Dictionary<SectionCoordinate, long> _sectionGpuLastUsed = [];
    private readonly HashSet<SectionCoordinate> _pendingSectionGpuRemovals = [];
    private TaskCompletionSource<bool>? _sectionGpuPublication;
    private Minecraft1122BlockRenderResources? _minecraftResources;
    private GpuAtlas? _gpuAtlas;
    private GpuAtlas? _stagedGpuAtlas;
    private ResourceTransitionState? _resourceTransition;
    private bool _resourceTransitionReady;
    private GpuGeometry? _previewGpuGeometry;
    private GpuImportedModel? _modelGpu;
    private GpuGeometry? _terrainGpuGeometry;
    private GpuShadowMap? _shadowMap;
    private ID3D11Buffer? _cameraBuffer;
    private ID3D11Buffer? _materialBuffer;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11VertexShader? _shadowVertexShader;
    private ID3D11PixelShader? _shadowPixelShader;
    private ID3D11VertexShader? _skyVertexShader;
    private ID3D11PixelShader? _skyPixelShader;
    private ID3D11VertexShader? _rainVertexShader;
    private ID3D11PixelShader? _rainPixelShader;
    private ID3D11PixelShader? _groundPixelShader;
    private ID3D11PixelShader? _postProcessPixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11SamplerState? _atlasSampler;
    private ID3D11SamplerState? _pointAtlasSampler;
    private ID3D11SamplerState? _shadowSampler;
    private ID3D11SamplerState? _postProcessSampler;
    private ID3D11RasterizerState? _voxelRasterizerState;
    private ID3D11RasterizerState? _terrainRasterizerState;
    private ID3D11RasterizerState? _shadowRasterizerState;
    private ID3D11BlendState? _opaqueBlendState;
    private ID3D11BlendState? _alphaBlendState;
    private ID3D11BlendState? _standardOpaqueBlendState;
    private ID3D11BlendState? _standardAlphaBlendState;
    private ID3D11DepthStencilState? _depthWriteState;
    private ID3D11DepthStencilState? _depthReadState;
    private ID3D11DepthStencilState? _depthDisabledState;
    private GpuPostProcessTarget? _postProcessTarget;
    private ID3D11Device1? _device;
    private SceneDelta? _previewDelta;
    private ImportedModel? _model;
    private IReadOnlyList<SectionRenderGeometry> _activeSections = [];
    private IReadOnlyList<SectionRenderGeometry>? _pendingActiveSections;
    private ViewportContent _content = ViewportContent.Preview;
    private ViewportContent _contentBeforeModel = ViewportContent.Preview;
    private bool _previewGeometryDirty = true;
    private bool _modelGpuDirty = true;
    private bool _activeSectionsDirty = true;
    private Point? _lastPointer;
    private int _lastPointerTimestamp;
    private CameraGesture _cameraGesture;
    private float? _keyboardMovementSpeedOverride;
    private float _observerMouseSensitivity = DefaultObserverMouseSensitivity;
    private float _sectionDrawDistance = 384f;
    private int _maximumActiveSections = 768;
    private string? _activeDimension;
    private bool _terrainGeometryDirty = true;
    private int _terrainAnchorX = int.MinValue;
    private int _terrainAnchorZ = int.MinValue;
    private double _lastNavigationSeconds;
    private double _lastTargetNotificationSeconds;
    private bool _targetNotificationPending;
    private bool _navigationTargetNotificationPending;
    private bool _translationMotionActive;
    private bool _shadowDirty = true;
    private bool _hasShadowFrame;
    private ShadowCameraFrame _shadowFrame;
    private long _sectionGpuGeneration;
    private double _lastShadowRefreshSeconds = double.NegativeInfinity;
    private bool _shadowRefreshDeferred;
    private NativePoint _observerRestorePointer;
    private bool _hasObserverRestorePointer;
    private bool _changingNavigationMode;
    private bool _releasingGestureCapture;
    private bool _cameraGestureStartedInsideSurface;
    private double _frameRateWindowStartSeconds = double.NaN;
    private int _frameRateWindowFrames;
    private bool _enhancedLightingEnabled;
    private bool _renderingReflection;
    private bool _hasOpaqueSceneCopy;
    private float? _reflectionPlaneHeight;
    private float[] _reflectionPlaneHeights = [];
    private bool _escapeKeyWasDown;
    private bool _shaderPipelineCreated;

    public VoxelViewport() : this(new CanonicalFallbackBlockRenderResolver())
    {
    }

    public VoxelViewport(IBlockRenderResolver renderResolver)
    {
        _sectionCache = new SectionRenderCache(
            renderResolver ?? throw new ArgumentNullException(nameof(renderResolver)),
            renderResolver as IBlockFaceMaterialResolver,
            missingSectionsAreAir: false);
        InitializeComponent();
        Surface.FramePresented += (_, _) => TrackRenderedFrame();
        _shaderBytecodePreparation = Task.Run(() => CompileShaderBundle(ReportShaderPreparationProgress));
        Surface.AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(OnPreviewMouseButtonUp),
            handledEventsToo: true);
        Dispatcher.ShutdownStarted += OnDispatcherShutdown;
        _ = RefreshAfterShaderPreparationAsync();
    }

    private void ReportShaderPreparationProgress(int completed, int total)
    {
        if(Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if(_shaderPipelineCreated || RendererStartupOverlay.Visibility != Visibility.Visible) return;
            int safeTotal = Math.Max(1, total);
            int safeCompleted = Math.Clamp(completed, 0, safeTotal);
            RendererStartupStageText.Text = "正在编译光影着色器";
            RendererStartupProgressText.Text = $"{safeCompleted:N0} / {safeTotal:N0}";
            RendererStartupProgressBar.IsIndeterminate = false;
            RendererStartupProgressBar.Value = safeCompleted * 100d / safeTotal;
        });
    }

    private async Task RefreshAfterShaderPreparationAsync()
    {
        try
        {
            await _shaderBytecodePreparation.ConfigureAwait(false);
        }
        catch(Exception exception)
        {
            Debug.WriteLine($"后台着色器编译失败：{exception}");
            return;
        }

        if(Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if(_device is not null) Surface.InvalidateVisual();
        });
    }

    public int LoadedSectionCount => _sectionCache.Count;

    public ImportedModel? LoadedModel => _model;

    public float SectionDrawDistance
    {
        get => _sectionDrawDistance;
        set
        {
            if(!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            if(_sectionDrawDistance == value) return;
            _sectionDrawDistance = value;
            InvalidateTerrainGeometry();
            InvalidateActiveSections();
        }
    }

    public int MaximumActiveSections
    {
        get => _maximumActiveSections;
        set
        {
            if(value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            if(_maximumActiveSections == value) return;
            _maximumActiveSections = value;
            InvalidateActiveSections();
        }
    }

    public string? ActiveDimension => _activeDimension;

    public Vector3 CameraTarget => _camera.NavigationPosition;

    public Vector3 NavigationPosition => _camera.NavigationPosition;

    public float ZoomTargetDistance => _camera.ZoomTargetDistance;

    public float VerticalFieldOfViewDegrees => _camera.VerticalFieldOfView * (180f / MathF.PI);

    public bool EnhancedLightingEnabled => _enhancedLightingEnabled;

    public ViewportNavigationMode NavigationMode => _camera.NavigationMode == CameraNavigationMode.Observer
        ? ViewportNavigationMode.Observer
        : ViewportNavigationMode.Orbit;

    /// <summary>Whether observer mouse-look currently owns the pointer after the user clicked the viewport.</summary>
    public bool IsObserverPointerLocked =>
        NavigationMode == ViewportNavigationMode.Observer && Surface.IsMouseCaptured;

    /// <summary>Maximum WASD/Space/Ctrl navigation speed in blocks per second.</summary>
    public float KeyboardMovementSpeed
    {
        get => _keyboardMovementSpeedOverride ?? CalculateAdaptiveKeyboardMovementSpeed();
        set
        {
            if(!float.IsFinite(value) || value is < MinimumKeyboardMovementSpeed or > MaximumKeyboardMovementSpeed)
                throw new ArgumentOutOfRangeException(nameof(value));
            if(_keyboardMovementSpeedOverride == value) return;
            _keyboardMovementSpeedOverride = value;
            if(IsInitialized) Surface.InvalidateVisual();
        }
    }

    public bool IsKeyboardMovementSpeedAutomatic => _keyboardMovementSpeedOverride is null;

    /// <summary>Multiplier applied only to relative mouse look in observer mode.</summary>
    public float ObserverMouseSensitivity
    {
        get => _observerMouseSensitivity;
        set
        {
            ValidateObserverMouseSensitivity(value);
            _observerMouseSensitivity = value;
        }
    }

    /// <summary>Ends an orbit-mode mouse gesture and removes its queued rotation/pan inertia.</summary>
    public void StopPointerNavigationMotion()
    {
        if(NavigationMode != ViewportNavigationMode.Orbit) return;
        _camera.StopPointerMotion();
        ResetCameraGesture();
        Surface.InvalidateVisual();
    }

    /// <summary>Pauses expensive scene submission while the native host window is being moved or resized.</summary>
    public void SetHostWindowRenderingSuspended(bool suspended)
    {
        if(suspended) StopPointerNavigationMotion();
        if(Surface.IsRenderingSuspended == suspended) return;
        _frameRateWindowStartSeconds = double.NaN;
        _frameRateWindowFrames = 0;
        Surface.IsRenderingSuspended = suspended;
        // AlwaysRefresh keeps WPF's render/composition loop awake even when RaiseDraw exits early.
        // Stop that loop for the complete native move/resize transaction so the system caption
        // can track the pointer just like an ordinary desktop window.
        Surface.AlwaysRefresh = !suspended;
        if(!suspended) Surface.InvalidateVisual();
    }

    /// <summary>Restores the distance-aware speed used when no fixed UI value has been selected.</summary>
    public void UseAutomaticKeyboardMovementSpeed()
    {
        if(_keyboardMovementSpeedOverride is null) return;
        _keyboardMovementSpeedOverride = null;
        if(IsInitialized) Surface.InvalidateVisual();
    }

    public event Action<Vector3>? CameraTargetChanged;

    /// <summary>Raised only for direct keyboard or pointer translation, never for programmatic jumps.</summary>
    public event Action<Vector3>? NavigationTargetChanged;

    public event Action<ViewportNavigationMode>? NavigationModeChanged;

    public event Action<float>? ZoomTargetDistanceChanged;

    /// <summary>Raised at a throttled cadence from successfully completed viewport draw callbacks.</summary>
    public event Action<float>? FrameRateUpdated;

    public bool FocusNavigation() => Surface.Focus();

    public string StartDebugRecording(string directory) => Surface.StartDebugRecording(directory);
    public Task StopDebugRecording() => Surface.StopDebugRecording();

    public void SuspendNavigationInput()
    {
        ReleaseObserverPointerLock(false);
        ResetCameraGesture();
        _camera.StopMotion();
        _translationMotionActive = false;
        _escapeKeyWasDown = false;
    }

    public bool SetZoomTargetDistance(float distance)
    {
        bool changed = _camera.SetZoomTargetDistance(distance);
        if(!changed) return false;
        ZoomTargetDistanceChanged?.Invoke(_camera.ZoomTargetDistance);
        Surface.InvalidateVisual();
        return true;
    }

    public bool SetNavigationMode(ViewportNavigationMode mode)
    {
        if(!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Dispatcher.VerifyAccess();
        ViewportNavigationMode previousMode = NavigationMode;
        if(previousMode == mode) return false;
        CameraNavigationMode cameraMode = mode == ViewportNavigationMode.Observer
            ? CameraNavigationMode.Observer
            : CameraNavigationMode.Orbit;
        float previousZoomTarget = _camera.ZoomTargetDistance;

        _changingNavigationMode = true;
        try
        {
            // Release observer capture before changing input semantics so its final cursor-warp event cannot be
            // interpreted as an orbit drag. Restoring the saved pointer also makes leaving observer mode atomic.
            if(previousMode == ViewportNavigationMode.Observer) ReleaseObserverPointerLock(true);
            if(!_camera.SetNavigationMode(cameraMode)) return false;
            ResetCameraGesture();
            _camera.StopPointerMotion();
            _escapeKeyWasDown = false;
            _translationMotionActive = false;
            _targetNotificationPending = false;
            _navigationTargetNotificationPending = false;
        }
        finally
        {
            _changingNavigationMode = false;
        }

        _activeSectionsDirty = true;
        _activeSectionRefreshPolicy.Reset();
        RequestShadowRefresh(true);
        NotifyCameraTargetChanged(true, false);
        NotifyZoomTargetDistanceChanged(previousZoomTarget);
        NavigationModeChanged?.Invoke(mode);
        Surface.InvalidateVisual();
        return true;
    }

    public bool EnterObserverMode() => SetNavigationMode(ViewportNavigationMode.Observer);

    public bool ExitObserverMode() => SetNavigationMode(ViewportNavigationMode.Orbit);

    public void SetTimeOfDay(float hours)
    {
        _environment.SetTimeOfDay(hours);
        _shadowDirty = true;
        Surface.InvalidateVisual();
    }

    public void SetCloudsEnabled(bool enabled)
    {
        _environment.SetCloudsEnabled(enabled);
        Surface.InvalidateVisual();
    }

    public void SetRainEnabled(bool enabled)
    {
        _environment.SetRainEnabled(enabled);
        Surface.InvalidateVisual();
    }

    public void SetFogEnabled(bool enabled)
    {
        _environment.SetFogEnabled(enabled);
        Surface.InvalidateVisual();
    }

    public void SetEnhancedLightingEnabled(bool enabled)
    {
        if(_enhancedLightingEnabled == enabled) return;
        _enhancedLightingEnabled = enabled;
        _reflectionPlaneHeight = FindDominantWaterPlane(_activeSections);
        // Fixed 2x per-axis supersampling, resolved by WPF's high-quality image filter.
        // Native DPI resolution is retained in standard mode; quality never adapts downward.
        Surface.RenderScale = enabled ? 2 : 1;
        if(!enabled)
        {

            ReleaseEffectCoverageTargets();
            ReleaseSunOcclusionTarget();
            ReleasePlanarRefractionTargets();
            _postProcessTarget?.Dispose();
            _postProcessTarget = null;
        }
        _shadowDirty = true;
        _shadowRefreshDeferred = false;
        Surface.InvalidateVisual();
    }

    public void SetVerticalFieldOfViewDegrees(float degrees)
    {
        if(!float.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
        float radians = degrees * (MathF.PI / 180f);
        if(!_camera.SetFieldOfView(radians)) return;
        Surface.InvalidateVisual();
    }

    public void SetTerrainMode(ViewportTerrainMode mode)
    {
        bool changed = _environment.TerrainMode != mode;
        _environment.SetTerrainMode(mode);
        if(changed) InvalidateTerrainGeometry();
        Surface.InvalidateVisual();
    }

    /// <summary>
    /// Atomically opens and installs a local 1.12.2 client/resource-pack stack. Configure resources before
    /// loading Sections so an existing world cache is never silently discarded.
    /// </summary>
    public void ConfigureMinecraft1122Resources(Minecraft1122ResourceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureResourceConfigurationCanChange();
        var replacement = Minecraft1122BlockRenderResources.Open(configuration);
        InstallMinecraftResources(replacement);
    }

    /// <summary>
    /// Opens and indexes the resource stack on a worker thread, then installs the completed replacement on the
    /// dispatcher. Cancellation never publishes a partial stack; a replacement that finishes after cancellation is
    /// disposed when its worker completes.
    /// </summary>
    public async Task ConfigureMinecraft1122ResourcesAsync(
        Minecraft1122ResourceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Dispatcher.VerifyAccess();
        EnsureResourceConfigurationCanChange();
        cancellationToken.ThrowIfCancellationRequested();

        Minecraft1122BlockRenderResources? replacement = null;
        try
        {
            replacement = await OpenMinecraftResourcesAsync(configuration, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Dispatcher.VerifyAccess();
            EnsureResourceConfigurationCanChange();
            InstallMinecraftResources(replacement);
            replacement = null;
        }
        finally
        {
            replacement?.Dispose();
        }
    }

    /// <summary>
    /// Starts a two-phase resource transition. The currently published Section buffers and atlas stay visible while
    /// callers clear/rebuild the CPU cache and install another resolver. Call PrepareResourceTransitionForPublication
    /// after the replacement CPU generation commits, then await WaitForSectionGpuPublicationAsync.
    /// </summary>
    public void BeginResourceTransition()
    {
        Dispatcher.VerifyAccess();
        if(_resourceTransition is not null) throw new InvalidOperationException("已有 Minecraft 资源切换正在进行。");
        InvalidateSectionMeshBuilds();
        _resourceTransition = new ResourceTransitionState(
            _minecraftResources,
            _sectionCache,
            _activeDimension,
            _content);
        _resourceTransitionReady = false;
        DisposeStagedSectionGpuCache();
        DisposeStagedGpuAtlas();
        _pendingSectionGpuRemovals.Clear();
        _activeSectionsDirty = false;
        BeginSectionGpuPublication();
    }

    /// <summary>Allows the render loop to stage the replacement atlas and buffers, then swap them in one frame.</summary>
    public void PrepareResourceTransitionForPublication()
    {
        Dispatcher.VerifyAccess();
        if(_resourceTransition is null) throw new InvalidOperationException("没有可发布的 Minecraft 资源切换。");
        _resourceTransitionReady = true;
        InvalidateActiveSections(immediateShadowRefresh: false);
    }

    /// <summary>Abandons a two-phase transition and restores the resolver and CPU cache that began it.</summary>
    public void CancelResourceTransition()
    {
        Dispatcher.VerifyAccess();
        ResourceTransitionState? transition = _resourceTransition;
        if(transition is null) return;

        Minecraft1122BlockRenderResources? abandonedResources = ReferenceEquals(
            _minecraftResources,
            transition.PreviousResources)
            ? null
            : _minecraftResources;
        InvalidateSectionMeshBuilds(abandonedResources);
        _minecraftResources = transition.PreviousResources;
        _sectionCache = transition.PreviousSectionCache;
        _activeDimension = transition.PreviousActiveDimension;
        _content = transition.PreviousContent;
        _resourceTransition = null;
        _resourceTransitionReady = false;
        DisposeStagedSectionGpuCache();
        DisposeStagedGpuAtlas();
        _pendingSectionGpuRemovals.Clear();
        _activeSectionsDirty = false;
        InvalidateTerrainGeometry();
        _sectionGpuPublication?.TrySetResult(true);
        Surface.InvalidateVisual();
    }

    internal static async Task<Minecraft1122BlockRenderResources> OpenMinecraftResourcesAsync(
        Minecraft1122ResourceConfiguration configuration,
        CancellationToken cancellationToken,
        Func<Minecraft1122ResourceConfiguration, Minecraft1122BlockRenderResources>? opener = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        Minecraft1122ResourceConfiguration snapshot = configuration with
        {
            Overlays = configuration.Overlays?.ToArray(),
        };
        Func<Minecraft1122ResourceConfiguration, Minecraft1122BlockRenderResources> effectiveOpener =
            opener ?? Minecraft1122BlockRenderResources.Open;
        Task<Minecraft1122BlockRenderResources> openTask = Task.Run(
            () => effectiveOpener(snapshot),
            CancellationToken.None);
        try
        {
            return await openTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = DisposeOpenedMinecraftResourcesAsync(openTask);
            throw;
        }
    }

    private static async Task DisposeOpenedMinecraftResourcesAsync(
        Task<Minecraft1122BlockRenderResources> openTask)
    {
        try
        {
            Minecraft1122BlockRenderResources resources = await openTask.ConfigureAwait(false);
            resources.Dispose();
        }
        catch
        {
            // The caller observes the original open failure. This continuation only owns late-result cleanup.
        }
    }

    /// <summary>Returns to stable temporary colors. Loaded Sections must be cleared first.</summary>
    public void ClearMinecraftResources()
    {
        EnsureResourceConfigurationCanChange();
        _previewGeometryDirty = true;
        var previous = _minecraftResources;
        if(_resourceTransition is not null)
        {
            IDisposable? superseded = ReferenceEquals(previous, _resourceTransition.PreviousResources)
                ? null
                : previous;
            InvalidateSectionMeshBuilds(superseded);
            _minecraftResources = null;
            _sectionCache = CreateSectionCache(null);
            _resourceTransitionReady = false;
            DisposeStagedSectionGpuCache();
            DisposeStagedGpuAtlas();
            InvalidateTerrainGeometry();
            Surface.InvalidateVisual();
            return;
        }

        InvalidateSectionMeshBuilds(previous);
        _minecraftResources = null;
        _sectionCache = CreateSectionCache(null);
        DisposeSectionGpuCache();
        DisposeGpuAtlas();
        InvalidateTerrainGeometry();
        _activeSections = [];
        _activeSectionsDirty = true;
        _shadowDirty = true;
        Surface.InvalidateVisual();
    }

    public void UseFallbackResources() => ClearMinecraftResources();

    private void InstallMinecraftResources(Minecraft1122BlockRenderResources replacement)
    {
        _previewGeometryDirty = true;
        var previous = _minecraftResources;
        if(_resourceTransition is not null)
        {
            IDisposable? superseded = ReferenceEquals(previous, _resourceTransition.PreviousResources)
                ? null
                : previous;
            InvalidateSectionMeshBuilds(superseded);
            _minecraftResources = replacement;
            _sectionCache = CreateSectionCache(replacement);
            _resourceTransitionReady = false;
            DisposeStagedSectionGpuCache();
            DisposeStagedGpuAtlas();
            InvalidateTerrainGeometry();
            Surface.InvalidateVisual();
            return;
        }

        InvalidateSectionMeshBuilds(previous);
        _minecraftResources = replacement;
        _sectionCache = CreateSectionCache(replacement);
        DisposeSectionGpuCache();
        DisposeGpuAtlas();
        InvalidateTerrainGeometry();
        _activeSections = [];
        _activeSectionsDirty = true;
        _shadowDirty = true;
        Surface.InvalidateVisual();
    }

    private static SectionRenderCache CreateSectionCache(Minecraft1122BlockRenderResources? resources) =>
        resources is null
            ? new SectionRenderCache(new CanonicalFallbackBlockRenderResolver(), missingSectionsAreAir: false)
            : new SectionRenderCache(resources, resources, missingSectionsAreAir: false);

    private void EnsureResourceConfigurationCanChange()
    {
        if(_sectionCache.Count != 0)
        {
            throw new InvalidOperationException("更换 Minecraft 资源前必须先调用 ClearSections，避免静默丢弃 Section 缓存。");
        }
    }

    public void JumpTo(Vector3 worldPosition)
    {
        SetCameraTarget(worldPosition, null);
    }

    /// <summary>Moves the orbit target and selects a useful overview distance for newly loaded content.</summary>
    public void FrameAt(Vector3 worldPosition, float distance)
    {
        if(!float.IsFinite(distance) || distance is < 3f or > 512f)
        {
            throw new ArgumentOutOfRangeException(nameof(distance));
        }

        SetCameraTarget(worldPosition, distance);
    }

    private void SetCameraTarget(Vector3 worldPosition, float? distance)
    {
        if(_cameraGesture != CameraGesture.None) CancelCameraGesture();
        float previousZoomTarget = _camera.ZoomTargetDistance;
        float? effectiveDistance = NavigationMode == ViewportNavigationMode.Observer ? null : distance;
        _camera.SetNavigationPosition(worldPosition, effectiveDistance);
        _translationMotionActive = false;
        _activeSectionsDirty = true;
        _activeSectionRefreshPolicy.Reset();
        _shadowDirty = true;
        _targetNotificationPending = false;
        _navigationTargetNotificationPending = false;
        NotifyCameraTargetChanged(true, false);
        NotifyZoomTargetDistanceChanged(previousZoomTarget);
        Surface.InvalidateVisual();
    }

    public void LoadPreview(SceneDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        _previewDelta = delta;
        _content = ViewportContent.Preview;
        _previewGeometryDirty = true;
        _shadowDirty = true;
        DisposeSectionGpuCache();
        Surface.InvalidateVisual();
    }

    /// <summary>Imports a self-contained glTF/GLB file and makes it the active viewport content.</summary>
    public ImportedModel LoadModel(string path, bool autoFrame = true)
    {
        ImportedModel model = GltfModelImporter.Load(path);
        LoadModel(model, autoFrame);
        return model;
    }

    /// <summary>Loads already imported model data without clearing cached Minecraft Sections.</summary>
    public void LoadModel(ImportedModel model, bool autoFrame = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        if(_content != ViewportContent.Model) _contentBeforeModel = _content;
        _model = model;
        _content = ViewportContent.Model;
        _modelGpuDirty = true;
        _shadowDirty = true;
        DisposeModelGpu();
        InvalidateTerrainGeometry();
        if(autoFrame) FrameModel();
        Surface.InvalidateVisual();
    }

    /// <summary>Frames the loaded model while respecting the orbit camera's supported distance.</summary>
    public bool FrameModel()
    {
        if(_model is null || _model.Bounds.IsEmpty) return false;
        float width = Math.Max((float)ActualWidth, 1f);
        float height = Math.Max((float)ActualHeight, 1f);
        float verticalFieldOfView = _camera.VerticalFieldOfView;
        float horizontalFieldOfView = 2f * MathF.Atan(MathF.Tan(verticalFieldOfView * 0.5f) * width / height);
        float limitingFieldOfView = MathF.Min(verticalFieldOfView, horizontalFieldOfView);
        float distance = _model.Bounds.Radius <= 0.0001f
            ? OrbitCamera.MinimumDistance
            : _model.Bounds.Radius / MathF.Sin(limitingFieldOfView * 0.5f) * 1.15f;
        distance = Math.Clamp(distance, OrbitCamera.MinimumDistance, OrbitCamera.MaximumDistance);
        SetCameraTarget(_model.Bounds.Center, distance);
        return true;
    }

    /// <summary>Unloads model CPU/GPU data and restores the content mode that was visible before it.</summary>
    public void ClearModel()
    {
        _model = null;
        _modelGpuDirty = true;
        _shadowDirty = true;
        DisposeModelGpu();
        if(_content == ViewportContent.Model)
        {
            _content = _contentBeforeModel;
            _previewGeometryDirty |= _content == ViewportContent.Preview;
            _activeSectionsDirty |= _content == ViewportContent.Sections;
            InvalidateTerrainGeometry();
        }
        Surface.InvalidateVisual();
    }

    /// <summary>Replaces the loaded Section set while preserving unchanged per-Section CPU meshes.</summary>
    public void LoadSections(IEnumerable<NormalizedMinecraftSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        InvalidateSectionMeshBuilds();
        _sectionCache.SetMissingSectionsAreAir(false);
        var batch = sections.ToArray();
        var change = _sectionCache.SetSections(batch);
        _activeDimension = batch.Select(section => section.Coordinate.Dimension).FirstOrDefault();
        ActivateSections(change);
    }

    /// <summary>Adds or replaces only the supplied Sections and their boundary-neighbor meshes.</summary>
    public void ReplaceSections(IEnumerable<NormalizedMinecraftSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        InvalidateSectionMeshBuilds();
        _sectionCache.SetMissingSectionsAreAir(false);
        var batch = sections.ToArray();
        var change = _sectionCache.ReplaceSections(batch);
        _activeDimension ??= batch.Select(section => section.Coordinate.Dimension).FirstOrDefault();
        ActivateSections(change);
    }

    /// <summary>
    /// Stages Section references on the dispatcher, builds CPU meshes on one serialized worker, then publishes
    /// the completed generation back on the dispatcher. No WPF or D3D object crosses the worker boundary.
    /// </summary>
    public Task ReplaceSectionsAsync(
        IEnumerable<NormalizedMinecraftSection> sections,
        CancellationToken cancellationToken = default,
        IProgress<SectionMeshBuildProgress>? progress = null) =>
        ReplaceSectionsWithBoundaryPolicyAsync(sections, cancellationToken, progress, missingSectionsAreAir: false);

    /// <summary>
    /// Replaces Sections from a complete sparse scene, where an absent neighboring Section is authoritative air.
    /// </summary>
    public Task ReplaceSparseSectionsAsync(
        IEnumerable<NormalizedMinecraftSection> sections,
        CancellationToken cancellationToken = default,
        IProgress<SectionMeshBuildProgress>? progress = null) =>
        ReplaceSectionsWithBoundaryPolicyAsync(sections, cancellationToken, progress, missingSectionsAreAir: true);

    private Task ReplaceSectionsWithBoundaryPolicyAsync(
        IEnumerable<NormalizedMinecraftSection> sections,
        CancellationToken cancellationToken,
        IProgress<SectionMeshBuildProgress>? progress,
        bool missingSectionsAreAir)
    {
        ArgumentNullException.ThrowIfNull(sections);
        _sectionCache.SetMissingSectionsAreAir(missingSectionsAreAir);
        NormalizedMinecraftSection[] batch = sections.ToArray();
        Task operation;
        lock(_sectionMeshTaskGate)
        {
            CancellationToken epochToken = _sectionMeshEpochCancellation.Token;
            operation = ReplaceSectionsCoreAsync(batch, cancellationToken, epochToken, progress);
            _sectionMeshTasks.Add(operation);
        }
        _ = StopTrackingSectionMeshBuildAsync(operation);
        return operation;
    }

    /// <summary>
    /// Synchronizes the complete loaded Section set. New and changed Sections are built off-thread first; Sections
    /// absent from the supplied snapshot are then removed so a full-plan refresh cannot leave stale geometry behind.
    /// </summary>
    public async Task SynchronizeSectionsAsync(
        IEnumerable<NormalizedMinecraftSection> sections,
        CancellationToken cancellationToken = default,
        IProgress<SectionMeshBuildProgress>? progress = null) =>
        await SynchronizeSectionsWithBoundaryPolicyAsync(sections, cancellationToken, progress, missingSectionsAreAir: false);

    /// <summary>
    /// Synchronizes a complete sparse scene, exposing faces beside absent Sections while retaining normal culling
    /// for neighbors that are actually present.
    /// </summary>
    public async Task SynchronizeSparseSectionsAsync(
        IEnumerable<NormalizedMinecraftSection> sections,
        CancellationToken cancellationToken = default,
        IProgress<SectionMeshBuildProgress>? progress = null) =>
        await SynchronizeSectionsWithBoundaryPolicyAsync(sections, cancellationToken, progress, missingSectionsAreAir: true);

    private async Task SynchronizeSectionsWithBoundaryPolicyAsync(
        IEnumerable<NormalizedMinecraftSection> sections,
        CancellationToken cancellationToken,
        IProgress<SectionMeshBuildProgress>? progress,
        bool missingSectionsAreAir)
    {
        ArgumentNullException.ThrowIfNull(sections);
        NormalizedMinecraftSection[] snapshot = sections.ToArray();
        await ReplaceSectionsWithBoundaryPolicyAsync(snapshot, cancellationToken, progress, missingSectionsAreAir);
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeOnDispatcherAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                HashSet<SectionCoordinate> expected = snapshot.Select(static section => section.Coordinate).ToHashSet();
                SectionCoordinate[] obsolete = _sectionCache.Coordinates
                    .Where(coordinate => !expected.Contains(coordinate))
                    .ToArray();
                if(obsolete.Length != 0) RemoveSections(obsolete);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Waits until the most recently activated CPU Section generation has all required GPU buffers staged and is
    /// published as one visible frame. If a newer generation supersedes it, the wait follows that newer generation.
    /// </summary>
    public async Task WaitForSectionGpuPublicationAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        while(true)
        {
            TaskCompletionSource<bool>? publication = _sectionGpuPublication;
            if(publication is null) return;
            bool completed = await publication.Task.WaitAsync(cancellationToken);
            Dispatcher.VerifyAccess();
            if(completed && ReferenceEquals(publication, _sectionGpuPublication)) return;
        }
    }

    public bool RemoveSection(SectionCoordinate coordinate) => RemoveSections([coordinate]) > 0;

    public int RemoveSections(IEnumerable<SectionCoordinate> coordinates)
    {
        InvalidateSectionMeshBuilds();
        var change = _sectionCache.RemoveSections(coordinates);
        if(_activeDimension is not null && !_sectionCache.Coordinates.Any(coordinate => string.Equals(coordinate.Dimension, _activeDimension, StringComparison.Ordinal)))
        {
            _activeDimension = _sectionCache.Coordinates.Select(coordinate => coordinate.Dimension).FirstOrDefault();
        }
        ActivateSections(change);
        return change.Removed.Count;
    }

    public void ClearSections()
    {
        InvalidateSectionMeshBuilds();
        if(_resourceTransition is not null)
        {
            _sectionCache = CreateSectionCache(_minecraftResources);
            _activeDimension = null;
            _content = ViewportContent.Sections;
            return;
        }
        var change = _sectionCache.Clear();
        _activeDimension = null;
        ActivateSections(change);
    }

    private async Task ReplaceSectionsCoreAsync(
        NormalizedMinecraftSection[] batch,
        CancellationToken cancellationToken,
        CancellationToken epochToken,
        IProgress<SectionMeshBuildProgress>? progress)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, epochToken);
        CancellationToken linkedToken = linkedCancellation.Token;
        bool enteredBuildGate = false;
        try
        {
            await _sectionMeshBuildGate.WaitAsync(linkedToken).ConfigureAwait(false);
            enteredBuildGate = true;
            StagedSectionBuild staged = await InvokeOnDispatcherAsync(
                () =>
                {
                    linkedToken.ThrowIfCancellationRequested();
                    SectionRenderCache cache = _sectionCache;
                    SectionRenderBuildRequest request = cache.StageReplaceSections(batch);
                    string? dimension = batch.Select(section => section.Coordinate.Dimension).FirstOrDefault();
                    return new StagedSectionBuild(cache, request, dimension);
                },
                linkedToken).ConfigureAwait(false);

            SectionRenderBuildResult result = await Task.Run(
                () => SectionRenderCache.Build(staged.Request, linkedToken, progress),
                linkedToken).ConfigureAwait(false);

            await InvokeOnDispatcherAsync(
                () =>
                {
                    linkedToken.ThrowIfCancellationRequested();
                    if(!ReferenceEquals(staged.Cache, _sectionCache) ||
                       !staged.Cache.TryCommit(result, out SectionCacheChange change))
                    {
                        throw new OperationCanceledException("Section 网格结果已被更新的预览代次淘汰。", linkedToken);
                    }
                    _activeDimension ??= staged.Dimension;
                    ActivateSections(change);
                    return true;
                },
                linkedToken).ConfigureAwait(false);
        }
        finally
        {
            if(enteredBuildGate) _sectionMeshBuildGate.Release();
        }
    }

    private async Task<T> InvokeOnDispatcherAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if(Dispatcher.CheckAccess()) return action();
        return await Dispatcher.InvokeAsync(
            action,
            // Background publications must yield to camera input and rendering while chunks stream in.
            System.Windows.Threading.DispatcherPriority.Background,
            cancellationToken).Task.ConfigureAwait(false);
    }

    private async Task StopTrackingSectionMeshBuildAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // The returned task preserves the original cancellation or failure for its caller.
        }
        finally
        {
            lock(_sectionMeshTaskGate) _sectionMeshTasks.Remove(operation);
        }
    }

    private void InvalidateSectionMeshBuilds(IDisposable? disposeAfterBuilds = null)
    {
        CancellationTokenSource invalidatedCancellation;
        Task[] activeBuilds;
        lock(_sectionMeshTaskGate)
        {
            invalidatedCancellation = _sectionMeshEpochCancellation;
            _sectionMeshEpochCancellation = new CancellationTokenSource();
            activeBuilds = _sectionMeshTasks.ToArray();
        }
        invalidatedCancellation.Cancel();
        _ = DisposeAfterSectionMeshBuildsAsync(invalidatedCancellation, disposeAfterBuilds, activeBuilds);
    }

    private static async Task DisposeAfterSectionMeshBuildsAsync(
        CancellationTokenSource invalidatedCancellation,
        IDisposable? resource,
        IReadOnlyList<Task> activeBuilds)
    {
        try
        {
            await Task.WhenAll(activeBuilds).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation/failure is owned by each public operation; lifetime cleanup must still finish.
        }
        finally
        {
            resource?.Dispose();
            invalidatedCancellation.Dispose();
        }
    }

    public void SetActiveDimension(string dimension)
    {
        if(string.IsNullOrWhiteSpace(dimension)) throw new ArgumentException("维度不能为空。", nameof(dimension));
        if(string.Equals(_activeDimension, dimension, StringComparison.Ordinal) && _content == ViewportContent.Sections) return;
        _activeDimension = dimension;
        _content = ViewportContent.Sections;
        InvalidateActiveSections();
    }

    private void ActivateSections(SectionCacheChange change)
    {
        _content = ViewportContent.Sections;
        DisposePreviewGeometry();
        _previewGeometryDirty = true;
        if(_resourceTransition is not null) return;

        BeginSectionGpuPublication();
        // A Chunk update rebuilds its complete 3x3x3 neighborhood. Keep the currently published topology visible
        // while every replacement GPU buffer is staged, then publish the new active set in one frame. Otherwise a
        // new Section and the neighbor faces it removes can straddle the per-frame upload budget and visibly blink.
        foreach(SectionCoordinate coordinate in change.Rebuilt) _pendingSectionGpuRemovals.Remove(coordinate);
        foreach(SectionCoordinate coordinate in change.Removed) _pendingSectionGpuRemovals.Add(coordinate);
        InvalidateActiveSections(immediateShadowRefresh: false);
    }

    private void BeginSectionGpuPublication()
    {
        _sectionGpuPublication?.TrySetResult(false);
        _sectionGpuPublication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void InvalidateActiveSections(bool immediateShadowRefresh = true)
    {
        _activeSectionsDirty = true;
        _activeSectionRefreshPolicy.Reset();
        RequestShadowRefresh(immediateShadowRefresh);
        Surface.InvalidateVisual();
    }

    private void OnLoadContent(object? sender, DrawingSurfaceEventArgs e)
    {
        _frameRateWindowStartSeconds = double.NaN;
        _frameRateWindowFrames = 0;
        _device = e.Device;
        _shaderPipelineCreated = false;
        _lastNavigationSeconds = _animationClock.Elapsed.TotalSeconds;
        _cameraBuffer = e.Device.CreateBuffer((uint)Marshal.SizeOf<CameraConstants>(), BindFlags.ConstantBuffer);
        _materialBuffer = e.Device.CreateBuffer((uint)Marshal.SizeOf<MaterialConstants>(), BindFlags.ConstantBuffer);
        _atlasSampler = e.Device.CreateSamplerState(new SamplerDescription(
            Filter.Anisotropic,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            0f,
            16,
            ComparisonFunction.Never,
            0f,
            float.MaxValue));
        _pointAtlasSampler = e.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            0f, 1, ComparisonFunction.Never, 0f, 0f));
        SamplerDescription shadowSamplerDescription = new(
            Filter.ComparisonMinMagMipPoint,
            TextureAddressMode.Border,
            TextureAddressMode.Border,
            TextureAddressMode.Border,
            0f,
            1,
            ComparisonFunction.LessEqual,
            0f,
            float.MaxValue)
        {
            BorderColor = new Color4(1f, 1f, 1f, 1f),
        };
        _shadowSampler = e.Device.CreateSamplerState(shadowSamplerDescription);
        _postProcessSampler = e.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            0f,
            1,
            ComparisonFunction.Never,
            0f,
            float.MaxValue));
        _voxelRasterizerState = e.Device.CreateRasterizerState(ViewportRasterization.CreateVoxelDescription());
        _terrainRasterizerState = e.Device.CreateRasterizerState(ViewportRasterization.CreateTerrainDescription());
        _shadowRasterizerState = e.Device.CreateRasterizerState(ViewportRasterization.CreateShadowDescription());
        _shadowMap = GpuShadowMap.Create(e.Device, _enhancedLightingEnabled ? 8192 : 2048);
        _opaqueBlendState = e.Device.CreateBlendState(BlendDescription.Opaque);
        _alphaBlendState = e.Device.CreateBlendState(BlendDescription.NonPremultiplied);
        _standardOpaqueBlendState = e.Device.CreateBlendState(PreserveSceneOpacity(BlendDescription.Opaque));
        _standardAlphaBlendState = e.Device.CreateBlendState(PreserveSceneOpacity(BlendDescription.NonPremultiplied));
        CreateTransparencyStates(e.Device);
        var depthWrite = DepthStencilDescription.Default;
        depthWrite.DepthFunc = ComparisonFunction.LessEqual;
        var depthRead = DepthStencilDescription.DepthRead;
        depthRead.DepthFunc = ComparisonFunction.LessEqual;
        _depthWriteState = e.Device.CreateDepthStencilState(depthWrite);
        _depthReadState = e.Device.CreateDepthStencilState(depthRead);
        _depthDisabledState = e.Device.CreateDepthStencilState(DepthStencilDescription.None);
        _shadowDirty = true;
        _hasShadowFrame = false;
        _terrainGeometryDirty = true;
        if(_content == ViewportContent.Preview)
        {
            ReplacePreviewGeometry(e.Device, PreviewGeometry.Build(_previewDelta, PreviewWhiteTextureRegion));
        }
        else if(_content == ViewportContent.Model)
        {
            _modelGpuDirty = true;
        }
        else
        {
            _activeSectionsDirty = true;
        }
        CreatePreparedShaderPipeline(e.Device);
    }

    private void OnUnloadContent(object? sender, DrawingSurfaceEventArgs e)
    {
        if(NavigationMode == ViewportNavigationMode.Observer) ExitObserverMode();
        else ReleaseObserverPointerLock(true);
        _camera.StopMotion();
        ResetCameraGesture();
        _translationMotionActive = false;
        UnbindPixelShaderResources(e.Context);
        UnbindGeometryInputs(e.Context);
        DisposePreviewGeometry();
        DisposeModelGpu();
        DisposeSectionGpuCache();
        DisposeGpuAtlas();
        DisposeTerrainGeometry();
        DisposeObserverBodyShadow();
        _shadowMap?.Dispose();
        _cameraBuffer?.Dispose();
        _materialBuffer?.Dispose();
        _vertexShader?.Dispose();
        _pixelShader?.Dispose();
        _shadowVertexShader?.Dispose();
        _shadowPixelShader?.Dispose();
        _skyVertexShader?.Dispose();
        _skyPixelShader?.Dispose();
        _rainVertexShader?.Dispose();
        _rainPixelShader?.Dispose();
        _groundPixelShader?.Dispose();
        _postProcessPixelShader?.Dispose();
        _inputLayout?.Dispose();
        _atlasSampler?.Dispose();
        _pointAtlasSampler?.Dispose();
        _shadowSampler?.Dispose();
        _postProcessSampler?.Dispose();
        _voxelRasterizerState?.Dispose();
        _terrainRasterizerState?.Dispose();
        _shadowRasterizerState?.Dispose();
        _opaqueBlendState?.Dispose();
        _alphaBlendState?.Dispose();
        _standardOpaqueBlendState?.Dispose();
        _standardAlphaBlendState?.Dispose();
        _depthWriteState?.Dispose();
        _depthReadState?.Dispose();
        _depthDisabledState?.Dispose();
        _postProcessTarget?.Dispose();
        DisposeTransparency();
        DisposeReflectionCoverage();
        DisposeSunOptics();
        DisposeEffectCoverage();
        DisposeStaticSky();
        DisposePlanarRefraction();

        _cameraBuffer = null;
        _materialBuffer = null;
        _vertexShader = null;
        _pixelShader = null;
        _shadowVertexShader = null;
        _shadowPixelShader = null;
        _skyVertexShader = null;
        _skyPixelShader = null;
        _rainVertexShader = null;
        _rainPixelShader = null;
        _groundPixelShader = null;
        _postProcessPixelShader = null;
        _inputLayout = null;
        _atlasSampler = null;
        _pointAtlasSampler = null;
        _shadowSampler = null;
        _postProcessSampler = null;
        _voxelRasterizerState = null;
        _terrainRasterizerState = null;
        _shadowRasterizerState = null;
        _shadowMap = null;
        _opaqueBlendState = null;
        _alphaBlendState = null;
        _standardOpaqueBlendState = null;
        _standardAlphaBlendState = null;
        _depthWriteState = null;
        _depthReadState = null;
        _depthDisabledState = null;
        _postProcessTarget = null;
        _hasShadowFrame = false;
        _shaderPipelineCreated = false;
        _device = null;
    }

    private void OnDraw(object? sender, DrawEventArgs e)
    {
        Surface.Profiler?.Scene(_activeSections.Count, _reflectionPlaneHeights.Length, _enhancedLightingEnabled);
        CreatePreparedShaderPipeline(e.Device);
        UpdateKeyboardNavigation();
        Surface.Profiler?.CameraContext(_camera.CapturePose(), Window.GetWindow(this)?.IsActive == true, _pendingActiveSections?.Count ?? 0);
        if(Vector3.Dot(_lastSelectionDirection, _camera.ViewDirection) < 0.995f)
        {
            _lastSelectionDirection = _camera.ViewDirection;
            _activeSectionsDirty = true;
        }
        FlushPendingTargetNotification();
        _hasOpaqueSceneCopy = false;
        _sunOcclusionCapturedThisFrame = false;
        UnbindPixelShaderResources(e.Context);
        UnbindGeometryInputs(e.Context);

        if(_device is null || _cameraBuffer is null || _materialBuffer is null || _vertexShader is null || _pixelShader is null ||
           _shadowVertexShader is null || _shadowPixelShader is null || _shadowMap is null ||
           _skyVertexShader is null || _skyPixelShader is null || _rainVertexShader is null || _rainPixelShader is null || _groundPixelShader is null || _postProcessPixelShader is null ||
           _inputLayout is null || _atlasSampler is null || _shadowSampler is null || _postProcessSampler is null || _voxelRasterizerState is null || _terrainRasterizerState is null || _shadowRasterizerState is null || _opaqueBlendState is null ||
           _alphaBlendState is null || _standardOpaqueBlendState is null || _standardAlphaBlendState is null || _depthWriteState is null || _depthReadState is null || _depthDisabledState is null)
        {
            BindPresentationRenderTarget(e.Context, e);
            e.Context.ClearRenderTargetView(Surface.SceneColorTextureView, new Color4(0.76f, 0.84f, 0.96f, 1f));
            return;
        }

        if(_enhancedLightingEnabled)
        {
            EnsurePostProcessTarget(
                _device,
                Math.Max(e.Surface.TextureWidth, 1),
                Math.Max(e.Surface.TextureHeight, 1));
        }
        int shadowResolution = _enhancedLightingEnabled ? 8192 : 2048;
        if(_shadowMap.Texture.Description.Width != shadowResolution)
        {
            _sunShadowRangesReady = false;
            _shadowMap.Dispose();
            _shadowMap = GpuShadowMap.Create(_device, shadowResolution);
            _shadowDirty = true;
            _hasShadowFrame = false;
        }
        BindSceneRenderTarget(e.Context, e);
        ID3D11RenderTargetView sceneRenderTarget = _enhancedLightingEnabled
            ? _postProcessTarget!.RenderView
            : Surface.SceneColorTextureView!;
        e.Context.ClearRenderTargetView(sceneRenderTarget, new Color4(0.76f, 0.84f, 0.96f, 1f));
        if(_enhancedLightingEnabled)
        {
            e.Context.ClearRenderTargetView(_postProcessTarget!.SurfaceRenderView, new Color4(0.5f, 1f, 0.5f, 0f));
            e.Context.ClearRenderTargetView(_postProcessTarget.MaterialRenderView, new Color4(0f, 0f, 0f, 0f));
            e.Context.ClearDepthStencilView(_postProcessTarget.DepthView, DepthStencilClearFlags.Depth, 1f, 0);
        }
        else if(Surface.SceneDepthStencilView is not null)
        {
            e.Context.ClearDepthStencilView(Surface.SceneDepthStencilView, DepthStencilClearFlags.Depth, 1f, 0);
        }

        Surface.Profiler?.Mark(e.Context, "prepare_targets");
        try
        {
            EnsureTerrainGeometry(_device);
            EnsureGpuAtlas(e.Context, _device);
            UpdateAtlasAnimations(e.Context);
            PrepareActiveContentGpu(e.Context, _device);
            Surface.Profiler?.Mark(e.Context, "uploads");
            e.Context.RSSetState(_voxelRasterizerState);
            float atmosphereCameraDistance = NavigationMode == ViewportNavigationMode.Observer
                ? ViewportEnvironmentMath.ReferenceCameraDistance
                : _camera.Distance;
            ViewportEnvironmentFrame environment = _environment.CreateFrame(atmosphereCameraDistance);
            Vector3 navigationPosition = _camera.NavigationPosition;
            float shadowRadius = _enhancedLightingEnabled ? Math.Clamp(_sectionDrawDistance, 112f, 512f) : ShadowCasterRadius;
            ShadowCameraFrame nextShadowFrame = ShadowCameraMath.Calculate(navigationPosition, environment.SunDirection, shadowRadius);
            if(!_hasShadowFrame || _shadowFrame.Radius != shadowRadius || ShadowCameraMath.NeedsUpdate(_shadowFrame, navigationPosition, environment.SunDirection))
                RequestShadowRefresh(!_hasShadowFrame);
            UpdateObserverBodyShadow(e.Context);
            PromoteDeferredShadowRefresh();
            bool shadowEnabled = environment.SunlightStrength > 0.01f;
            ShadowCameraFrame effectiveShadowFrame = _shadowDirty || !_hasShadowFrame ? nextShadowFrame : _shadowFrame;
            CameraConstants camera = CreateCameraConstants(environment, effectiveShadowFrame, shadowEnabled);
            PrepareStaticSky(e.Context);
            _currentViewProjection = camera.ViewProjection;
            _currentEye = _camera.EyePosition;
            _currentForward = _camera.ViewDirection;
            e.Context.UpdateSubresource(in camera, _cameraBuffer);
            if(shadowEnabled && _shadowDirty)
            {
                Surface.Profiler?.Mark(e.Context, "shadow_setup");
                RenderSunShadow(e.Context, e, effectiveShadowFrame);
                Surface.Profiler?.Mark(e.Context, "shadow_render");
                BuildSunShadowRanges(e.Context);
                Surface.Profiler?.Mark(e.Context, "shadow_ranges");
                BindSceneRenderTarget(e.Context, e);
                _shadowFrame = effectiveShadowFrame;
                _hasShadowFrame = true;
                _shadowDirty = false;
                _shadowRefreshDeferred = false;
                _lastShadowRefreshSeconds = _animationClock.Elapsed.TotalSeconds;
            }
            RenderObserverBodyShadow(e.Context, e, camera, shadowEnabled);
            Surface.Profiler?.Mark(e.Context, "shadow");
            if(_enhancedLightingEnabled && _reflectionPlaneHeight is float reflectionPlane)
            {
                for(int planeIndex = 0; planeIndex < _reflectionPlaneHeights.Length; planeIndex++)
                {
                    RenderPlanarReflection(e.Context, e, camera, _reflectionPlaneHeights[planeIndex], planeIndex);
                    Surface.Profiler?.Mark(e.Context, "reflection_" + planeIndex);
                }
            }

            if(!_enhancedLightingEnabled) DrawSky(e.Context);
            Surface.Profiler?.Mark(e.Context, "sky");
            BindVoxelPipeline(e.Context);
            if(!_enhancedLightingEnabled || _environment.TerrainMode != ViewportTerrainMode.Chunks) DrawTerrain(e.Context);

            if(_content == ViewportContent.Preview)
            {
                DrawPreview(e.Context);
                DrawBackgroundAfterOpaque(e.Context);
            }
            else if(_content == ViewportContent.Model)
            {
                DrawModel(e.Context);
            }
            else
            {
                DrawSections(e.Context, e);
            }
            Surface.Profiler?.Mark(e.Context, "geometry");
            DrawRain(e.Context);
            Surface.Profiler?.Mark(e.Context, "rain");
            if(_enhancedLightingEnabled) DrawEnhancedOutput(e.Context, e);
            Surface.Profiler?.Mark(e.Context, "postprocess");
        }
        finally
        {
            UnbindPixelShaderResources(e.Context);
            UnbindGeometryInputs(e.Context);
            BindPresentationRenderTarget(e.Context, e);
        }

    }

    private void TrackRenderedFrame()
    {
        double now = _animationClock.Elapsed.TotalSeconds;
        if(!double.IsFinite(_frameRateWindowStartSeconds))
        {
            _frameRateWindowStartSeconds = now;
            _frameRateWindowFrames = 1;
            return;
        }

        _frameRateWindowFrames++;
        double elapsed = now - _frameRateWindowStartSeconds;
        if(elapsed < FrameRateUpdateIntervalSeconds) return;

        float framesPerSecond = (float)(_frameRateWindowFrames / elapsed);
        _frameRateWindowStartSeconds = now;
        _frameRateWindowFrames = 0;
        FrameRateUpdated?.Invoke(framesPerSecond);
    }

    private void UpdateKeyboardNavigation()
    {
        // WPF keyboard focus can remain on the surface after another application becomes active.
        if(Window.GetWindow(this)?.IsActive != true)
        {
            SuspendNavigationInput();
            _lastNavigationSeconds = _animationClock.Elapsed.TotalSeconds;
            return;
        }
        double now = _animationClock.Elapsed.TotalSeconds;
        float elapsed = CalculateFrameElapsedSeconds(now, _lastNavigationSeconds);
        _lastNavigationSeconds = now;
        if(elapsed <= 0f) return;

        bool escapeKeyDown = IsObserverPointerLocked &&
                             (GetAsyncKeyState(VirtualKeyEscape) & 0x8000) != 0;
        if(escapeKeyDown && !_escapeKeyWasDown)
        {
            _escapeKeyWasDown = true;
            ExitObserverMode();
            return;
        }
        _escapeKeyWasDown = escapeKeyDown;

        bool acceptsKeyboard = Surface.IsKeyboardFocusWithin;
        float forward = acceptsKeyboard ? Axis(Key.W, Key.S) : 0f;
        float right = acceptsKeyboard ? Axis(Key.D, Key.A) : 0f;
        float vertical = acceptsKeyboard
            ? (Keyboard.IsKeyDown(Key.Space) ? 1f : 0f) -
              (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ? 1f : 0f)
            : 0f;
        OrbitCameraMotion motion = _camera.UpdateMotion(
            forward,
            right,
            vertical,
            KeyboardMovementSpeed,
            elapsed);
        bool translationStopped = _translationMotionActive && !motion.TranslationActive;
        _translationMotionActive = motion.TranslationActive;
        if(motion.TranslationChanged)
        {
            InvalidateActiveSectionsForCameraMovement();
            NotifyCameraTargetChanged(translationStopped, true);
        }
        else if(translationStopped)
        {
            NotifyCameraTargetChanged(true, true);
        }
        if(_activeSectionRefreshPolicy.ReleaseDeferred(now, motion.TranslationActive)) _activeSectionsDirty = true;

        static float Axis(Key positive, Key negative) =>
            (Keyboard.IsKeyDown(positive) ? 1f : 0f) - (Keyboard.IsKeyDown(negative) ? 1f : 0f);
    }

    private void NotifyCameraTargetChanged(bool force, bool navigation)
    {
        double now = _animationClock.Elapsed.TotalSeconds;
        if(!force && now - _lastTargetNotificationSeconds < 1d / 12d)
        {
            _targetNotificationPending = true;
            _navigationTargetNotificationPending |= navigation;
            return;
        }
        bool notifyNavigation = navigation || _navigationTargetNotificationPending;
        _targetNotificationPending = false;
        _navigationTargetNotificationPending = false;
        _lastTargetNotificationSeconds = now;
        CameraTargetChanged?.Invoke(_camera.NavigationPosition);
        if(notifyNavigation) NavigationTargetChanged?.Invoke(_camera.NavigationPosition);
    }

    private void NotifyZoomTargetDistanceChanged(float previousTarget)
    {
        if(previousTarget == _camera.ZoomTargetDistance) return;
        ZoomTargetDistanceChanged?.Invoke(_camera.ZoomTargetDistance);
    }

    private void FlushPendingTargetNotification()
    {
        if(_targetNotificationPending && _animationClock.Elapsed.TotalSeconds - _lastTargetNotificationSeconds >= 1d / 12d)
        {
            NotifyCameraTargetChanged(true, _navigationTargetNotificationPending);
        }
    }

    private void InvalidateActiveSectionsForCameraMovement()
    {
        if(_activeSectionRefreshPolicy.RequestForMovement(
               _camera.NavigationPosition,
               _animationClock.Elapsed.TotalSeconds)) _activeSectionsDirty = true;
        // OnDraw checks whether the cached light camera still covers this position. Motion inside that
        // coverage does not alter any caster and should not rebuild an 8192px map every quarter second.
    }

    private void DrawSky(ID3D11DeviceContext context)
    {
        BindReflectionCoverageMask(context);
        BindStaticSky(context);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(null);
        context.VSSetShader(_skyVertexShader);
        context.VSSetConstantBuffer(0, _cameraBuffer);
        context.PSSetShader(_skyPixelShader);
        context.PSSetConstantBuffer(0, _cameraBuffer);
        context.PSSetSampler(2, _postProcessSampler);
        context.OMSetBlendState(_skyBlendState);
        // At far-plane depth, opaque terrain and cutout texels reject the sky before cloud shading.
        // Glass and water have not drawn yet, so their actual background remains fully rendered.
        context.OMSetDepthStencilState(_enhancedLightingEnabled ? _depthReadState : _depthDisabledState, 0);
        context.Draw(3, 0);
    }

    private void DrawBackgroundAfterOpaque(ID3D11DeviceContext context)
    {
        if(!_enhancedLightingEnabled) return;
        DrawSky(context);
        BindVoxelPipeline(context);
        // The translucent reference grid needs the finished sky behind it and must not be
        // overwritten by the late background. Solid reference terrain already populated depth.
        if(!_renderingReflection && _environment.TerrainMode == ViewportTerrainMode.Chunks) DrawTerrain(context);
    }

    private void RequestShadowRefresh(bool immediate)
    {
        if(immediate)
        {
            _shadowDirty = true;
            _shadowRefreshDeferred = false;
            return;
        }
        _shadowRefreshDeferred = true;
    }

    private void PromoteDeferredShadowRefresh()
    {
        if(!_shadowRefreshDeferred || _shadowDirty) return;
        double now = _animationClock.Elapsed.TotalSeconds;
        if(now - _lastShadowRefreshSeconds >= MinimumShadowRefreshIntervalSeconds)
        {
            _shadowDirty = true;
        }
    }

    private void RenderSunShadow(ID3D11DeviceContext context, DrawEventArgs drawEvent, ShadowCameraFrame frame)
    {
        UnbindPixelShaderResources(context);
        try
        {
            context.OMSetRenderTargets([], _shadowMap!.DepthView);
            context.RSSetViewport(new Viewport(0f, 0f, _shadowMap.Texture.Description.Width, _shadowMap.Texture.Description.Height, 0f, 1f));
            context.ClearDepthStencilView(_shadowMap.DepthView, DepthStencilClearFlags.Depth, 1f, 0);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetInputLayout(_inputLayout);
            context.RSSetState(_shadowRasterizerState);
            context.VSSetShader(_shadowVertexShader);
            context.VSSetConstantBuffer(0, _cameraBuffer);
            context.PSSetConstantBuffer(1, _materialBuffer);
            context.PSSetSampler(0, _enhancedLightingEnabled ? _atlasSampler : _pointAtlasSampler);
            context.OMSetBlendState(_opaqueBlendState);
            context.OMSetDepthStencilState(_depthWriteState, 0);

            if(_content == ViewportContent.Preview && _previewGpuGeometry is not null)
            {
                context.PSSetShader(null!);
                DrawGeometry(context, _previewGpuGeometry.VertexBuffer, _previewGpuGeometry.IndexBuffer, _previewGpuGeometry.IndexCount, 0);
            }
            else if(_content == ViewportContent.Model && _modelGpu is not null)
            {
                foreach(GpuModelPrimitive primitive in _modelGpu.Primitives)
                {
                    if(primitive.AlphaMode == ImportedModelAlphaMode.Blend) continue;
                    if(primitive.AlphaMode == ImportedModelAlphaMode.Mask)
                    {
                        context.PSSetShader(_shadowPixelShader);
                        SetMaterialAlphaCutoff(context, primitive.AlphaCutoff, primitive.TextureIndex is null);
                        ID3D11ShaderResourceView texture = primitive.TextureIndex is int textureIndex
                            ? _modelGpu.Textures[textureIndex].View
                            : _gpuAtlas!.View;
                        context.PSSetShaderResource(0, texture);
                    }
                    else
                    {
                        context.PSSetShader(null!);
                    }
                    DrawGeometry(
                        context,
                        primitive.Geometry.VertexBuffer,
                        primitive.Geometry.IndexBuffer,
                        primitive.Geometry.IndexCount,
                        0);
                }
            }
            else if(_content == ViewportContent.Sections)
            {
                context.PSSetShaderResource(0, _gpuAtlas!.View);
                foreach(SectionRenderGeometry geometry in _activeSections)
                {
                    if(!IntersectsShadowVolume(geometry.Coordinate, frame) ||
                       !_sectionGpuCache.TryGetValue(geometry.Coordinate, out GpuSectionGeometry? gpuSection)) continue;
                    foreach(SectionDrawRange range in gpuSection.Source.EffectiveDrawRanges)
                    {
                        if(range.Layer == BlockRenderLayer.Translucent) continue;
                        if(range.Layer == BlockRenderLayer.Cutout)
                        {
                            context.PSSetShader(_shadowPixelShader);
                            SetMaterialAlphaCutoff(context, 0.10f);
                        }
                        else
                        {
                            context.PSSetShader(null!);
                        }
                        DrawGeometry(
                            context,
                            gpuSection.Geometry.VertexBuffer,
                            gpuSection.Geometry.IndexBuffer,
                            range.IndexCount,
                            range.StartIndex);
                    }
                }
            }
        }
        finally
        {
            context.RSSetState(_voxelRasterizerState);
            UnbindPixelShaderResources(context);
            BindSceneRenderTarget(context, drawEvent);
        }
    }

    private void DrawEnhancedOutput(ID3D11DeviceContext context, DrawEventArgs drawEvent)
    {
        if(_postProcessTarget is null) return;
        UnbindPixelShaderResources(context);
        UnbindGeometryInputs(context);
        if(!_sunOcclusionCapturedThisFrame) DrawSunOcclusion(context);
        BuildEmissionCoverage(context);
        BindPresentationRenderTarget(context, drawEvent, includeDepth: false);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(null);
        context.RSSetState(_voxelRasterizerState);
        context.VSSetShader(_skyVertexShader);
        context.PSSetShader(_postProcessPixelShader);
        context.PSSetConstantBuffer(0, _cameraBuffer);
        context.PSSetShaderResource(2, _postProcessTarget.View);
        context.PSSetShaderResource(3, _postProcessTarget.SurfaceView);
        context.PSSetShaderResource(4, _postProcessTarget.DepthShaderView);
        context.PSSetShaderResource(6, _postProcessTarget.MaterialView);
        context.PSSetShaderResource(9, _sunOcclusionView!);
        context.PSSetShaderResource(14, _emissionCoverage!.View);
        context.PSSetShaderResource(15, (_sunShadowRangesReady ? _sunShadowRanges?.View : null)!);
        context.PSSetShaderResource(1, _shadowMap!.View);
        context.PSSetSampler(1, _shadowSampler);
        BindObserverBodyShadow(context);
        context.PSSetSampler(2, _postProcessSampler);
        // This target is newly allocated when render scale changes and is not cleared as part of
        // the HDR scene. The final shader must write alpha=1, not preserve uninitialized alpha.
        context.OMSetBlendState(_opaqueBlendState);
        context.OMSetDepthStencilState(_depthDisabledState, 0);
        context.Draw(3, 0);
        context.PSSetShaderResource(2, null!);
        context.PSSetShaderResource(3, null!);
        context.PSSetShaderResource(4, null!);
        context.PSSetShaderResource(5, null!);
        context.PSSetShaderResource(6, null!);
        context.PSSetShaderResource(7, null!);
        context.PSSetShaderResource(8, null!);
        context.PSSetShaderResource(9, null!);
    }

    private void RenderPlanarReflection(
        ID3D11DeviceContext context,
        DrawEventArgs drawEvent,
        CameraConstants mainCamera,
        float planeY, int planeIndex)
    {
        if(_postProcessTarget is null) return;
        CameraConstants reflectionCamera = CreateReflectionCameraConstants(mainCamera, planeY);
        RefractionCopyBounds coverage = GetReflectionCoverage(mainCamera, reflectionCamera, planeY);
        if(coverage.IsEmpty) return;
        UnbindPixelShaderResources(context);
        UnbindGeometryInputs(context);
        try
        {
            _renderingReflection = true;
            _currentViewProjection = reflectionCamera.ViewProjection;
            _currentEye = new Vector3(reflectionCamera.CameraPosition.X, reflectionCamera.CameraPosition.Y, reflectionCamera.CameraPosition.Z);
            _currentForward = new Vector3(reflectionCamera.CameraForward.X, reflectionCamera.CameraForward.Y, reflectionCamera.CameraForward.Z);
            SetReflectionCoverage(context, coverage);
            context.UpdateSubresource(in reflectionCamera, _cameraBuffer!);
            context.OMSetRenderTargets(_postProcessTarget.ReflectionRenderView[planeIndex], _postProcessTarget.ReflectionDepthView);
            context.RSSetViewport(new Viewport(
                0f,
                0f,
                _postProcessTarget.ReflectionWidth,
                _postProcessTarget.ReflectionHeight,
                0f,
                1f));
            context.ClearRenderTargetView(
                _postProcessTarget.ReflectionRenderView[planeIndex],
                new Color4(0.08f, 0.13f, 0.20f, 1f));
            context.ClearDepthStencilView(
                _postProcessTarget.ReflectionDepthView,
                DepthStencilClearFlags.Depth,
                1f,
                0);

            BindVoxelPipeline(context);
            if(_content == ViewportContent.Preview)
            {
                DrawPreview(context);
                DrawBackgroundAfterOpaque(context);
            }
            else if(_content == ViewportContent.Model) DrawModel(context);
            else DrawSections(context, drawEvent);
        }
        finally
        {
            _renderingReflection = false;
            context.RSSetState(_voxelRasterizerState);
            _currentViewProjection = mainCamera.ViewProjection;
            _currentEye = _camera.EyePosition;
            _currentForward = _camera.ViewDirection;
            UnbindPixelShaderResources(context);
            UnbindGeometryInputs(context);
            context.UpdateSubresource(in mainCamera, _cameraBuffer!);
            BindSceneRenderTarget(context, drawEvent);
        }
    }

    private void EnsurePostProcessTarget(ID3D11Device device, int width, int height)
    {
        if(_postProcessTarget is not null &&
           _postProcessTarget.Width == width &&
           _postProcessTarget.Height == height) return;
        GpuPostProcessTarget replacement = GpuPostProcessTarget.Create(device, width, height);
        GpuPostProcessTarget? previous = _postProcessTarget;
        _postProcessTarget = replacement;
        previous?.Dispose();
    }

    private void BindSceneRenderTarget(ID3D11DeviceContext context, DrawEventArgs drawEvent)
    {
        if(_enhancedLightingEnabled && _postProcessTarget is not null)
        {
            context.OMSetRenderTargets(
                [_postProcessTarget.RenderView, _postProcessTarget.SurfaceRenderView, _postProcessTarget.MaterialRenderView],
                _postProcessTarget.DepthView);
        }
        else
        {
            context.OMSetRenderTargets(Surface.SceneColorTextureView!, Surface.SceneDepthStencilView);
        }
        SetMainViewport(context, drawEvent);
    }

    private void BindPresentationRenderTarget(
        ID3D11DeviceContext context,
        DrawEventArgs drawEvent,
        bool includeDepth = true)
    {
        context.OMSetRenderTargets(
            Surface.SceneColorTextureView!,
            includeDepth ? Surface.SceneDepthStencilView : null);
        SetMainViewport(context, drawEvent);
    }

    private static void SetMainViewport(ID3D11DeviceContext context, DrawEventArgs drawEvent)
    {
        context.RSSetViewport(new Viewport(
            0f,
            0f,
            Math.Max(drawEvent.Surface.TextureWidth, 1),
            Math.Max(drawEvent.Surface.TextureHeight, 1),
            0f,
            1f));
    }

    private static void UnbindPixelShaderResources(ID3D11DeviceContext context)
    {
        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);
        context.PSSetShaderResource(2, null!);
        context.PSSetShaderResource(3, null!);
        context.PSSetShaderResource(4, null!);
        context.PSSetShaderResource(5, null!);
        context.PSSetShaderResource(6, null!);
        context.PSSetShaderResource(7, null!);
        context.PSSetShaderResource(8, null!);
        context.PSSetShaderResource(9, null!);
        context.PSSetShaderResource(12, null!);
        context.PSSetShaderResource(10, null!);
        context.PSSetShaderResource(13, null!);
        context.PSSetShaderResource(14, null!);
        context.PSSetShaderResource(15, null!);
        context.PSSetShaderResource(16, null!);
        context.PSSetShaderResource(17, null!);
    }

    private static void UnbindGeometryInputs(ID3D11DeviceContext context)
    {
        context.IASetVertexBuffer(0, null!, 0);
        context.IASetIndexBuffer(null!, Format.Unknown, 0);
    }

    private static bool IntersectsShadowVolume(SectionCoordinate coordinate, ShadowCameraFrame frame)
    {
        float minimumX = coordinate.X * 16f;
        float minimumY = coordinate.Y * 16f;
        float minimumZ = coordinate.Z * 16f;
        float dx = AxisDistance(frame.Center.X, minimumX, minimumX + 16f);
        float dy = AxisDistance(frame.Center.Y, minimumY, minimumY + 16f);
        float dz = AxisDistance(frame.Center.Z, minimumZ, minimumZ + 16f);
        return dx * dx + dy * dy + dz * dz <= frame.Radius * frame.Radius;

        static float AxisDistance(float value, float minimum, float maximum) =>
            value < minimum ? minimum - value : value > maximum ? value - maximum : 0f;
    }

    private void BindVoxelPipeline(ID3D11DeviceContext context)
    {
        BindStaticSky(context);
        BindReflectionCoverageMask(context);
        BindPlanarRefraction(context);
        BindObserverBodyShadow(context);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_inputLayout);
        context.VSSetShader(_vertexShader);
        context.VSSetConstantBuffer(0, _cameraBuffer);
        context.PSSetShader(_pixelShader);
        context.PSSetConstantBuffer(0, _cameraBuffer);
        context.PSSetConstantBuffer(1, _materialBuffer);
        context.PSSetShaderResource(0, _gpuAtlas!.View);
        context.PSSetShaderResource(1, _shadowMap!.View);
        context.PSSetShaderResource(
            5,
            _enhancedLightingEnabled && !_renderingReflection && _postProcessTarget is not null
                ? _postProcessTarget.ReflectionView
                : null!);
        context.PSSetShaderResource(
            7,
            _enhancedLightingEnabled && !_renderingReflection && _hasOpaqueSceneCopy && _postProcessTarget is not null
                ? _postProcessTarget.OpaqueView
                : null!);
        context.PSSetShaderResource(
            8,
            _enhancedLightingEnabled && !_renderingReflection && _hasOpaqueSceneCopy && _postProcessTarget is not null
                ? _postProcessTarget.OpaqueDepthView
                : null!);
        context.PSSetSampler(0, _enhancedLightingEnabled ? _atlasSampler : _pointAtlasSampler);
        context.PSSetSampler(1, _shadowSampler);
        context.PSSetSampler(2, _postProcessSampler);
    }

    private void DrawTerrain(ID3D11DeviceContext context)
    {
        if(_terrainGpuGeometry is null || _environment.TerrainMode == ViewportTerrainMode.Transparent) return;
        context.RSSetState(_terrainRasterizerState);
        SetMaterialAlphaCutoff(context, 0.02f);
        context.PSSetShader(_groundPixelShader);
        if(_environment.TerrainMode == ViewportTerrainMode.Chunks)
        {
            context.OMSetBlendState(_enhancedLightingEnabled ? _alphaBlendState : _standardAlphaBlendState);
            context.OMSetDepthStencilState(_depthReadState, 0);
        }
        else
        {
            context.OMSetBlendState(_enhancedLightingEnabled ? _opaqueBlendState : _standardOpaqueBlendState);
            context.OMSetDepthStencilState(_depthWriteState, 0);
        }
        DrawGeometry(context, _terrainGpuGeometry.VertexBuffer, _terrainGpuGeometry.IndexBuffer, _terrainGpuGeometry.IndexCount, 0);
        context.PSSetShader(_pixelShader);
        context.RSSetState(_voxelRasterizerState);
    }

    private void DrawRain(ID3D11DeviceContext context)
    {
        if(!_environment.RainEnabled) return;
        context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        context.IASetInputLayout(null);
        context.VSSetShader(_rainVertexShader);
        context.VSSetConstantBuffer(0, _cameraBuffer);
        context.PSSetShader(_rainPixelShader);
        context.PSSetConstantBuffer(0, _cameraBuffer);
        context.OMSetBlendState(_enhancedLightingEnabled ? _alphaBlendState : _standardAlphaBlendState);
        context.OMSetDepthStencilState(_depthReadState, 0);
        context.Draw(ViewportRainMath.DropCount * 2, 0);
    }

    private void PrepareActiveContentGpu(ID3D11DeviceContext context, ID3D11Device device)
    {
        if(_content == ViewportContent.Preview)
        {
            if(_previewGeometryDirty) ReplacePreviewGeometry(device, PreviewGeometry.Build(_previewDelta, PreviewWhiteTextureRegion));
            return;
        }
        if(_content == ViewportContent.Model)
        {
            if(_modelGpuDirty) ReplaceModelGpu(context, device, _model);
            return;
        }

        if(_resourceTransition is not null && !_resourceTransitionReady) return;

        if(_activeSectionsDirty) RefreshActiveSections();
        IReadOnlyList<SectionRenderGeometry>? pending = _pendingActiveSections;
        if(pending is null) return;
        int uploadsRemaining = MaximumUploadsPerFrame;
        TimeSpan uploadBudget = _enhancedLightingEnabled ? EnhancedUploadTimePerFrame : StandardUploadTimePerFrame;
        long uploadStarted = Stopwatch.GetTimestamp();
        long uploadedBytes = 0;
        foreach(SectionRenderGeometry geometry in pending)
        {
            if(geometry.Indices.Length == 0) continue;
            if(_sectionGpuCache.TryGetValue(geometry.Coordinate, out GpuSectionGeometry? current) &&
               ReferenceEquals(current.Source, geometry))
            {
                continue;
            }
            if(_stagedSectionGpuCache.TryGetValue(geometry.Coordinate, out GpuSectionGeometry? staged) &&
               ReferenceEquals(staged.Source, geometry))
            {
                continue;
            }
            if(uploadsRemaining == 0 || (uploadedBytes > 0 && (uploadedBytes >= MaximumUploadBytesPerFrame || Stopwatch.GetElapsedTime(uploadStarted) >= uploadBudget))) break;

            GpuGeometry replacementGeometry = GpuGeometry.Create(device, geometry.Vertices, geometry.Indices);
            var replacement = new GpuSectionGeometry(geometry, replacementGeometry);
            if(_stagedSectionGpuCache.Remove(geometry.Coordinate, out GpuSectionGeometry? superseded))
                superseded.Dispose();
            _stagedSectionGpuCache.Add(geometry.Coordinate, replacement);
            uploadsRemaining--;
            uploadedBytes += (long)geometry.Vertices.Length * VertexStride + (long)geometry.Indices.Length * sizeof(uint);
        }

        if(IsPendingActiveSectionPublicationReady(pending)) PublishPendingActiveSections(pending);
    }

    private void DrawPreview(ID3D11DeviceContext context)
    {
        if(_previewGpuGeometry is null) return;
        SetMaterialAlphaCutoff(context, 0.02f);
        SetRenderLayer(context, BlockRenderLayer.Opaque);
        DrawGeometry(context, _previewGpuGeometry.VertexBuffer, _previewGpuGeometry.IndexBuffer, _previewGpuGeometry.IndexCount, 0);
    }

    private void DrawModel(ID3D11DeviceContext context)
    {
        if(_modelGpu is null)
        {
            DrawBackgroundAfterOpaque(context);
            return;
        }

        foreach(ImportedModelAlphaMode alphaMode in new[] { ImportedModelAlphaMode.Opaque, ImportedModelAlphaMode.Mask })
        {
            SetRenderLayer(context, alphaMode == ImportedModelAlphaMode.Opaque ? BlockRenderLayer.Opaque : BlockRenderLayer.Cutout);
            foreach(GpuModelPrimitive primitive in _modelGpu.Primitives.Where(primitive => primitive.AlphaMode == alphaMode))
            {
                DrawModelPrimitive(context, primitive);
            }
        }

        DrawBackgroundAfterOpaque(context);
        CaptureSunOcclusionBeforeTransparency(context);
        SetRenderLayer(context, BlockRenderLayer.Translucent);
        for(int index = _modelGpu.Primitives.Count - 1; index >= 0; index--)
        {
            GpuModelPrimitive primitive = _modelGpu.Primitives[index];
            if(primitive.AlphaMode == ImportedModelAlphaMode.Blend) DrawModelPrimitive(context, primitive);
        }

        void DrawModelPrimitive(ID3D11DeviceContext drawContext, GpuModelPrimitive primitive)
        {
            SetMaterialAlphaCutoff(drawContext, primitive.AlphaMode == ImportedModelAlphaMode.Mask ? primitive.AlphaCutoff : 0.001f, primitive.TextureIndex is null);
            ID3D11ShaderResourceView texture = primitive.TextureIndex is int textureIndex
                ? _modelGpu.Textures[textureIndex].View
                : _gpuAtlas!.View;
            drawContext.PSSetShaderResource(0, texture);
            DrawGeometry(
                drawContext,
                primitive.Geometry.VertexBuffer,
                primitive.Geometry.IndexBuffer,
                primitive.Geometry.IndexCount,
                0);
        }
    }

    private void DrawSections(ID3D11DeviceContext context, DrawEventArgs drawEvent)
    {
        // Visibility and GPU lookup are shared by all three material passes, rather than repeated
        // for every layer and every Section on each camera frame.
        _visibleSectionGeometry.Clear();
        var frustum = new SectionFrustum(_currentViewProjection);
        foreach(SectionRenderGeometry geometry in _activeSections)
            if(frustum.Intersects(geometry.Coordinate) && _sectionGpuCache.TryGetValue(geometry.Coordinate, out GpuSectionGeometry? gpu))
                _visibleSectionGeometry.Add(gpu);
        foreach(var layer in new[] { BlockRenderLayer.Opaque, BlockRenderLayer.Cutout })
        {
            SetMaterialAlphaCutoff(context, layer == BlockRenderLayer.Cutout ? 0.10f : 0.02f);
            SetRenderLayer(context, layer);
            foreach(var geometry in _visibleSectionGeometry) DrawGeometryLayer(context, geometry, layer);
        }

        if(_enhancedLightingEnabled)
        {
            DrawBackgroundAfterOpaque(context);
            CaptureSunOcclusionBeforeTransparency(context);
            DrawSortedTransparency(context, drawEvent);
        }
        else
        {
            // Standard mode retains the original per-Section transparency path. Publishing a chunk
            // must not merge and re-upload all transparent vertices in the visible world.
            SetMaterialAlphaCutoff(context, 0.001f);
            SetRenderLayer(context, BlockRenderLayer.Translucent);
            for(int index = _visibleSectionGeometry.Count - 1; index >= 0; index--)
                DrawGeometryLayer(context, _visibleSectionGeometry[index], BlockRenderLayer.Translucent);
        }

        void DrawGeometryLayer(ID3D11DeviceContext drawContext, GpuSectionGeometry gpuSection, BlockRenderLayer layer)
        {
            foreach(var range in gpuSection.Source.EffectiveDrawRanges)
            {
                if(range.Layer != layer) continue;
                DrawGeometry(
                    drawContext,
                    gpuSection.Geometry.VertexBuffer,
                    gpuSection.Geometry.IndexBuffer,
                    range.IndexCount,
                    range.StartIndex);
            }
        }
    }

    private void CaptureOpaqueScene(ID3D11DeviceContext context, DrawEventArgs drawEvent, RefractionCopyBounds bounds)
    {
        if(_postProcessTarget is null) return;
        Surface.Profiler?.Copy();
        context.PSSetShaderResource(7, null!);
        if(!_hasOpaqueSceneCopy) context.PSSetShaderResource(8, null!);
        context.OMSetRenderTargets([], null);
        // Transparent color draws do not write depth; the nearest-surface metadata pass runs last.
        // Snapshot depth once, but preserve every ordered refraction layer's current color locally.
        if(!_hasOpaqueSceneCopy)
            context.CopyResource(_postProcessTarget.OpaqueDepthTexture, _postProcessTarget.DepthTexture);
        context.CopySubresourceRegion(_postProcessTarget.OpaqueTexture, 0, (uint)bounds.Left, (uint)bounds.Top, 0,
            _postProcessTarget.Texture, 0, new Box(bounds.Left, bounds.Top, 0, bounds.Right, bounds.Bottom, 1));
        _hasOpaqueSceneCopy = true;
        BindSceneRenderTarget(context, drawEvent);
        context.PSSetShaderResource(7, _postProcessTarget.OpaqueView);
        context.PSSetShaderResource(8, _postProcessTarget.OpaqueDepthView);
    }

    private void SetMaterialAlphaCutoff(ID3D11DeviceContext context, float alphaCutoff, bool usesAtlas = true)
    {
        var material = new MaterialConstants
        {
            Options = new Vector4(alphaCutoff, _hasOpaqueSceneCopy && !_renderingReflection ? 1f : 0f,
                usesAtlas ? _gpuAtlas?.LogicalTilesPerRow ?? 0 : 0, _gpuAtlas?.TileSize ?? 1)
        };
        context.UpdateSubresource(in material, _materialBuffer!);
    }

    private void RefreshActiveSections()
    {
        long selectionStarted = Surface.Profiler is not null ? Stopwatch.GetTimestamp() : 0;
        IReadOnlyList<SectionRenderGeometry> requested = _sectionCache.SelectActive(
            _camera.NavigationPosition,
            _sectionDrawDistance,
            _maximumActiveSections,
            _activeDimension,
            _camera.CreateViewProjection(Math.Max((float)ActualWidth, 1), Math.Max((float)ActualHeight, 1)));
        var requestedByCoordinate = requested.ToDictionary(geometry => geometry.Coordinate);
        foreach((SectionCoordinate coordinate, GpuSectionGeometry staged) in _stagedSectionGpuCache.ToArray())
        {
            if(requestedByCoordinate.TryGetValue(coordinate, out SectionRenderGeometry? requestedGeometry) &&
               ReferenceEquals(staged.Source, requestedGeometry)) continue;
            _stagedSectionGpuCache.Remove(coordinate);
            staged.Dispose();
        }
        bool sameGeometry = _resourceTransition is null && _pendingSectionGpuRemovals.Count == 0 &&
                            requested.Count == _activeSections.Count && _activeSections.All(geometry =>
                                requestedByCoordinate.TryGetValue(geometry.Coordinate, out SectionRenderGeometry? next) &&
                                ReferenceEquals(geometry, next));
        if(sameGeometry)
        {
            // Camera sorting alone does not change any caster or buffer. Avoid treating it as a
            // new world publication, rebuilding shadows and sweeping the entire GPU cache.
            _activeSections = requested;
            _pendingActiveSections = null;
            _reflectionPlaneHeight = FindDominantWaterPlane(requested);
            _sectionGpuPublication?.TrySetResult(true);
        }
        else _pendingActiveSections = requested;
        _activeSectionRefreshPolicy.MarkRefreshed(_camera.NavigationPosition, _animationClock.Elapsed.TotalSeconds);
        _lastSelectionDirection = _camera.ViewDirection;
        _activeSectionsDirty = false;
        Surface.Profiler?.Cpu("active_section_selection", selectionStarted);
    }

    private bool IsPendingActiveSectionPublicationReady(IReadOnlyList<SectionRenderGeometry> pending)
    {
        if(_resourceTransition is not null && _stagedGpuAtlas is null) return false;
        return SectionGpuPublicationPolicy.IsReady(pending, IsPublished, IsStaged);

        bool IsPublished(SectionRenderGeometry geometry)
        {
            return _sectionGpuCache.TryGetValue(geometry.Coordinate, out GpuSectionGeometry? current) &&
                   ReferenceEquals(current.Source, geometry);
        }

        bool IsStaged(SectionRenderGeometry geometry)
        {
            return _stagedSectionGpuCache.TryGetValue(geometry.Coordinate, out GpuSectionGeometry? staged) &&
                   ReferenceEquals(staged.Source, geometry);
        }
    }

    private void PublishPendingActiveSections(IReadOnlyList<SectionRenderGeometry> pending)
    {
        ResourceTransitionState? transition = _resourceTransition;
        foreach(SectionRenderGeometry geometry in pending)
        {
            if(_sectionGpuCache.TryGetValue(geometry.Coordinate, out GpuSectionGeometry? current) &&
               ReferenceEquals(current.Source, geometry)) continue;
            if(!_stagedSectionGpuCache.Remove(geometry.Coordinate, out GpuSectionGeometry? replacement) ||
               !ReferenceEquals(replacement.Source, geometry))
            {
                throw new InvalidOperationException($"Section {geometry.Coordinate} 的 GPU 发布批次尚未就绪。");
            }
            _sectionGpuCache[geometry.Coordinate] = replacement;
            current?.Dispose();
        }

        if(transition is not null)
        {
            var transitionCoordinates = pending.Select(geometry => geometry.Coordinate).ToHashSet();
            foreach(SectionCoordinate coordinate in _sectionGpuCache.Keys
                        .Where(coordinate => !transitionCoordinates.Contains(coordinate))
                        .ToArray())
            {
                DisposeSectionGpuGeometry(coordinate);
            }
            foreach(GpuSectionGeometry staged in _stagedSectionGpuCache.Values) staged.Dispose();
            _stagedSectionGpuCache.Clear();

            GpuAtlas replacementAtlas = _stagedGpuAtlas ??
                                        throw new InvalidOperationException("Minecraft 资源切换缺少已暂存的 GPU 图集。");
            _stagedGpuAtlas = null;
            GpuAtlas? previousAtlas = _gpuAtlas;
            _gpuAtlas = replacementAtlas;
            previousAtlas?.Dispose();
            _resourceTransition = null;
            _resourceTransitionReady = false;
            _pendingSectionGpuRemovals.Clear();
            if(!ReferenceEquals(transition.PreviousResources, _minecraftResources))
                InvalidateSectionMeshBuilds(transition.PreviousResources);
            InvalidateTerrainGeometry();
        }

        var activeCoordinates = pending.Select(geometry => geometry.Coordinate).ToHashSet();
        foreach(SectionCoordinate coordinate in _pendingSectionGpuRemovals.ToArray())
        {
            _pendingSectionGpuRemovals.Remove(coordinate);
            if(activeCoordinates.Contains(coordinate)) continue;
            DisposeSectionGpuGeometry(coordinate);
        }

        _activeSections = pending;
        _reflectionPlaneHeight = FindDominantWaterPlane(pending);
        _pendingActiveSections = null;
        _sectionGpuGeneration++;
        foreach(SectionRenderGeometry geometry in _activeSections)
            _sectionGpuLastUsed[geometry.Coordinate] = _sectionGpuGeneration;
        _shadowDirty = true;
        _shadowRefreshDeferred = false;
        TrimSectionGpuCache();
        _sectionGpuPublication?.TrySetResult(true);
    }

    private void TrimSectionGpuCache()
    {
        var activeCoordinates = _activeSections.Select(geometry => geometry.Coordinate).ToHashSet();
        foreach(SectionCoordinate coordinate in SectionGpuRetentionPolicy.SelectEvictions(
                     _sectionGpuLastUsed,
                     activeCoordinates,
                     _maximumActiveSections,
                     _sectionGpuCache.ToDictionary(pair => pair.Key, pair => (long)pair.Value.Source.Vertices.Length * VertexStride + (long)pair.Value.Source.Indices.Length * sizeof(uint))))
        {
            if(!_sectionGpuCache.Remove(coordinate, out GpuSectionGeometry? geometry)) continue;
            _sectionGpuLastUsed.Remove(coordinate);
            geometry.Dispose();
            RequestShadowRefresh(false);
        }
    }

    private float? FindDominantWaterPlane(IReadOnlyList<SectionRenderGeometry> sections)
    {
        if(!_enhancedLightingEnabled)
        {
            _reflectionPlaneHeights = [];
            return null;
        }
        _reflectionPlaneHeights = WaterPlaneSelection.Select(sections, _camera.NavigationPosition, 4);
        return _reflectionPlaneHeights.Length == 0 ? null : _reflectionPlaneHeights[0];
    }
    private void DrawGeometry(
        ID3D11DeviceContext context,
        ID3D11Buffer vertexBuffer,
        ID3D11Buffer indexBuffer,
        int indexCount,
        int startIndex)
    {
        Surface.Profiler?.Draw(indexCount);
        context.IASetVertexBuffer(0, vertexBuffer, (uint)VertexStride);
        context.IASetIndexBuffer(indexBuffer, Format.R32_UInt, 0);
        context.DrawIndexed((uint)indexCount, (uint)startIndex, 0);
    }

    private void SetRenderLayer(ID3D11DeviceContext context, BlockRenderLayer layer)
    {
        bool translucent = layer == BlockRenderLayer.Translucent;
        context.OMSetBlendState(_enhancedLightingEnabled
            ? translucent ? _alphaBlendState : _opaqueBlendState
            : translucent ? _standardAlphaBlendState : _standardOpaqueBlendState);
        context.OMSetDepthStencilState(translucent ? _depthReadState : _depthWriteState, 0);
    }

    private void ReplacePreviewGeometry(ID3D11Device device, (VoxelVertex[] Vertices, uint[] Indices) geometry)
    {
        var replacement = GpuGeometry.Create(device, geometry.Vertices, geometry.Indices);
        var previous = _previewGpuGeometry;
        _previewGpuGeometry = replacement;
        previous?.Dispose();
        _previewGeometryDirty = false;
        _shadowDirty = true;
    }

    private void DisposePreviewGeometry()
    {
        _previewGpuGeometry?.Dispose();
        _previewGpuGeometry = null;
    }

    private void ReplaceModelGpu(ID3D11DeviceContext context, ID3D11Device device, ImportedModel? model)
    {
        GpuImportedModel? replacement = model is null ? null : GpuImportedModel.Create(context, device, model);
        var previous = _modelGpu;
        _modelGpu = replacement;
        previous?.Dispose();
        _modelGpuDirty = false;
        _shadowDirty = true;
    }

    private void DisposeModelGpu()
    {
        _modelGpu?.Dispose();
        _modelGpu = null;
    }

    private void DisposeSectionGpuGeometry(SectionCoordinate coordinate)
    {
        if(_sectionGpuCache.Remove(coordinate, out GpuSectionGeometry? geometry))
        {
            _sectionGpuLastUsed.Remove(coordinate);
            geometry.Dispose();
            _shadowDirty = true;
        }
    }

    private void DisposeSectionGpuCache()
    {
        foreach(GpuSectionGeometry geometry in _sectionGpuCache.Values) geometry.Dispose();
        foreach(GpuSectionGeometry geometry in _stagedSectionGpuCache.Values) geometry.Dispose();
        _sectionGpuCache.Clear();
        _stagedSectionGpuCache.Clear();
        _sectionGpuLastUsed.Clear();
        _pendingSectionGpuRemovals.Clear();
        _pendingActiveSections = null;
        _activeSections = [];
        _reflectionPlaneHeight = null;
        _shadowDirty = true;
    }

    private void DisposeStagedSectionGpuCache()
    {
        foreach(GpuSectionGeometry geometry in _stagedSectionGpuCache.Values) geometry.Dispose();
        _stagedSectionGpuCache.Clear();
        _pendingActiveSections = null;
    }

    private void EnsureTerrainGeometry(ID3D11Device device)
    {
        if(_resourceTransition is not null) return;
        if(_environment.TerrainMode == ViewportTerrainMode.Transparent)
        {
            DisposeTerrainGeometry();
            _terrainGeometryDirty = false;
            return;
        }

        int radius = (int)Math.Clamp(Math.Ceiling(_sectionDrawDistance / 16f) * 16d, 64d, 384d);
        int anchorX = TerrainAnchor(_camera.NavigationPosition.X, radius);
        int anchorZ = TerrainAnchor(_camera.NavigationPosition.Z, radius);
        if(!_terrainGeometryDirty && anchorX == _terrainAnchorX && anchorZ == _terrainAnchorZ) return;

        IBlockFaceMaterialResolver? materials = _environment.TerrainMode == ViewportTerrainMode.Superflat &&
                                                _content is ViewportContent.Sections or ViewportContent.Preview
            ? _minecraftResources
            : null;
        var geometry = ViewportTerrainGeometryBuilder.Build(_environment.TerrainMode, anchorX, anchorZ, radius, materials);
        var replacement = GpuGeometry.Create(device, geometry.Vertices, geometry.Indices);
        var previous = _terrainGpuGeometry;
        _terrainGpuGeometry = replacement;
        _terrainAnchorX = anchorX;
        _terrainAnchorZ = anchorZ;
        _terrainGeometryDirty = false;
        previous?.Dispose();
    }

    private static int TerrainAnchor(float value, int radius)
    {
        double snapped = Math.Floor(value / 16d) * 16d;
        return (int)Math.Clamp(snapped, int.MinValue + (double)radius, int.MaxValue - (double)radius);
    }

    private void InvalidateTerrainGeometry() => _terrainGeometryDirty = true;

    private void DisposeTerrainGeometry()
    {
        _terrainGpuGeometry?.Dispose();
        _terrainGpuGeometry = null;
        _terrainAnchorX = int.MinValue;
        _terrainAnchorZ = int.MinValue;
    }

    private void EnsureGpuAtlas(ID3D11DeviceContext context, ID3D11Device device)
    {
        if(_resourceTransition is not null)
        {
            if(_resourceTransitionReady) EnsureStagedGpuAtlas(context, device);
            return;
        }

        bool useMinecraftAtlas = (_content is ViewportContent.Sections or ViewportContent.Preview) && _minecraftResources is not null;
        MinecraftTextureAtlasSnapshot snapshot;
        if(useMinecraftAtlas)
        {
            int publishedRevision = _gpuAtlas?.Revision ?? 0;
            if(!_minecraftResources!.Atlas.TrySnapshotAfterRevision(publishedRevision, out snapshot))
            {
                if(_gpuAtlas is not null) return;
                snapshot = WhiteAtlasSnapshot();
            }
        }
        else
        {
            if(_gpuAtlas?.Revision == 0) return;
            snapshot = WhiteAtlasSnapshot();
        }

        // Never overwrite a texture that the previous published frame can still be sampling. Upload a complete new
        // resource first, exchange the reference on the dispatcher, then let D3D retire the old resource safely.
        var replacement = GpuAtlas.Create(context, device, snapshot);
        var previous = _gpuAtlas;
        _gpuAtlas = replacement;
        previous?.Dispose();
    }

    private void EnsureStagedGpuAtlas(ID3D11DeviceContext context, ID3D11Device device)
    {
        bool useMinecraftAtlas = _minecraftResources is not null;
        MinecraftTextureAtlasSnapshot snapshot;
        if(useMinecraftAtlas)
        {
            int stagedRevision = _stagedGpuAtlas?.Revision ?? 0;
            if(!_minecraftResources!.Atlas.TrySnapshotAfterRevision(stagedRevision, out snapshot)) return;
        }
        else
        {
            if(_stagedGpuAtlas?.Revision == 0) return;
            snapshot = WhiteAtlasSnapshot();
        }
        GpuAtlas replacement = GpuAtlas.Create(context, device, snapshot);
        GpuAtlas? previous = _stagedGpuAtlas;
        _stagedGpuAtlas = replacement;
        previous?.Dispose();
    }

    private static MinecraftTextureAtlasSnapshot WhiteAtlasSnapshot() =>
        new(1, 1, 4, 0, [255, 255, 255, 255]);

    private void DisposeGpuAtlas()
    {
        _gpuAtlas?.Dispose();
        _gpuAtlas = null;
        DisposeStagedGpuAtlas();
    }

    private void DisposeStagedGpuAtlas()
    {
        _stagedGpuAtlas?.Dispose();
        _stagedGpuAtlas = null;
    }

    private void OnDispatcherShutdown(object? sender, EventArgs e)
    {
        Dispatcher.ShutdownStarted -= OnDispatcherShutdown;
        _changingNavigationMode = true;
        try
        {
            _camera.SetNavigationMode(CameraNavigationMode.Orbit);
            ResetCameraGesture();
            ReleaseObserverPointerLock(true);
        }
        finally
        {
            _changingNavigationMode = false;
        }
        Minecraft1122BlockRenderResources? retainedResources = _resourceTransition?.PreviousResources;
        InvalidateSectionMeshBuilds(_minecraftResources);
        if(retainedResources is not null && !ReferenceEquals(retainedResources, _minecraftResources))
            InvalidateSectionMeshBuilds(retainedResources);
        _minecraftResources = null;
        _resourceTransition = null;
        _resourceTransitionReady = false;
    }

    private CameraConstants CreateCameraConstants(
        ViewportEnvironmentFrame environment,
        ShadowCameraFrame shadow,
        bool shadowEnabled)
    {
        float width = Math.Max(Surface.TextureWidth, 1);
        float height = Math.Max(Surface.TextureHeight, 1);
        Matrix4x4 viewProjection = _camera.CreateViewProjection(width, height);
        if(!Matrix4x4.Invert(viewProjection, out var inverseViewProjection))
        {
            throw new InvalidOperationException("相机矩阵不可逆。");
        }
        ViewportSkyCameraBasis skyCamera = ViewportSkyMath.CreateCameraBasis(
            _camera.ViewDirection,
            width,
            height,
            _camera.VerticalFieldOfView);
        SunScreenProjection sunProjection = SunOpticsMath.Project(-environment.SunDirection, skyCamera, MathF.Atan(0.040f));
        float PlaneHeight(int index) => index < _reflectionPlaneHeights.Length ? _reflectionPlaneHeights[index] : -100000f;
        Matrix4x4 ReflectionProjection(int index) => index < _reflectionPlaneHeights.Length ? CreateReflectionViewProjection(_reflectionPlaneHeights[index], width, height) : Matrix4x4.Identity;
        float reflectionPlane = _reflectionPlaneHeight ?? 0f;
        bool reflectionAvailable = _enhancedLightingEnabled && _reflectionPlaneHeight.HasValue;
        Matrix4x4 reflectionViewProjection = reflectionAvailable
            ? CreateReflectionViewProjection(reflectionPlane, width, height)
            : Matrix4x4.Identity;
        return new CameraConstants
        {
            ViewProjection = viewProjection,
            InverseViewProjection = inverseViewProjection,
            SunViewProjection = shadow.ViewProjection,
            ReflectionViewProjection = reflectionViewProjection,
            ReflectionViewProjection1 = ReflectionProjection(1),
            ReflectionViewProjection2 = ReflectionProjection(2),
            ReflectionViewProjection3 = ReflectionProjection(3),
            ReflectionPlaneHeights = new Vector4(PlaneHeight(0), PlaneHeight(1), PlaneHeight(2), PlaneHeight(3)),
            CameraPosition = new Vector4(_camera.EyePosition, 1f),
            CameraRightAndHorizontalScale = new Vector4(skyCamera.Right, skyCamera.HorizontalProjectionScale),
            CameraUpAndVerticalScale = new Vector4(skyCamera.Up, skyCamera.VerticalProjectionScale),
            CameraForward = new Vector4(skyCamera.Forward, 0f),
            SunDirectionAndStrength = new Vector4(environment.SunDirection, environment.SunlightStrength),
            MoonDirectionAndStrength = new Vector4(environment.MoonDirection, environment.MoonlightStrength),
            SunlightColor = new Vector4(environment.SunlightColor, 1f),
            MoonlightColor = new Vector4(environment.MoonlightColor, 1f),
            AmbientColorAndStrength = new Vector4(environment.AmbientLightColor, environment.AmbientLightStrength),
            SkyZenithColor = new Vector4(environment.SkyZenithColor, 1f),
            SkyHorizonColor = new Vector4(environment.SkyHorizonColor, 1f),
            FogColorAndDensity = new Vector4(environment.FogColor, environment.FogDensity),
            EnvironmentOptions = new Vector4(
                (float)(_animationClock.Elapsed.TotalSeconds % 4096d),
                _environment.TimeOfDay,
                _environment.CloudsEnabled ? 1f : 0f,
                _environment.RainEnabled ? 1f : 0f),
            ViewportOptions = new Vector4(
                width,
                height,
                (float)_environment.TerrainMode,
                environment.StarVisibility),
            ShadowOptions = new Vector4(
                1f / (_shadowMap?.Texture.Description.Width ?? 2048),
                0.00030f,
                shadowEnabled ? 1f : 0f,
                environment.DaylightFactor),
            RainOptions = new Vector4(
                ViewportRainMath.FallSpeed,
                ViewportRainMath.VerticalSpan,
                ViewportRainMath.TopOffset,
                ViewportRainMath.HorizontalRadius),
            QualityOptions = new Vector4(
                _enhancedLightingEnabled ? 1f : 0f,
                1.16f,
                1.05f,
                0.08f),
            ReflectionOptions = new Vector4(
                reflectionPlane,
                0f,
                reflectionAvailable ? _reflectionPlaneHeights.Length : 0f,
                0f),
            SunOpticsProjection = new Vector4(sunProjection.ViewportUv, sunProjection.DiscRadiusUv.X, sunProjection.DiscRadiusUv.Y),
            SunOpticsOptions = new Vector4(sunProjection.VisibilityWeight, SunOpticsEnabledForValidation ? 1f : 0f, 0f, 0f),
        };
    }

    private Matrix4x4 CreateReflectionViewProjection(float planeY, float width, float height)
    {
        Vector3 eye = ReflectAcrossHorizontalPlane(_camera.EyePosition, planeY);
        Vector3 focus = ReflectAcrossHorizontalPlane(_camera.Focus, planeY);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, focus, -Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            _camera.VerticalFieldOfView,
            width / height,
            0.1f,
            2048f);
        return view * projection;
    }

    private CameraConstants CreateReflectionCameraConstants(CameraConstants mainCamera, float planeY)
    {
        float width = Math.Max(Surface.TextureWidth, 1);
        float height = Math.Max(Surface.TextureHeight, 1);
        Vector3 eye = ReflectAcrossHorizontalPlane(_camera.EyePosition, planeY);
        Vector3 focus = ReflectAcrossHorizontalPlane(_camera.Focus, planeY);
        Vector3 direction = Vector3.Normalize(focus - eye);
        Matrix4x4 viewProjection = CreateReflectionViewProjection(planeY, width, height);
        if(!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection))
            throw new InvalidOperationException("水面反射相机矩阵不可逆。");
        ViewportSkyCameraBasis skyCamera = ViewportSkyMath.CreateCameraBasis(
            direction,
            width,
            height,
            _camera.VerticalFieldOfView,
            -Vector3.UnitY);

        mainCamera.ViewProjection = viewProjection;
        mainCamera.InverseViewProjection = inverseViewProjection;
        mainCamera.CameraPosition = new Vector4(eye, 1f);
        mainCamera.CameraRightAndHorizontalScale = new Vector4(skyCamera.Right, skyCamera.HorizontalProjectionScale);
        mainCamera.CameraUpAndVerticalScale = new Vector4(skyCamera.Up, skyCamera.VerticalProjectionScale);
        mainCamera.CameraForward = new Vector4(skyCamera.Forward, 0f);
        mainCamera.ReflectionOptions = new Vector4(planeY, 1f, 0f, 0f);
        return mainCamera;
    }

    private static Vector3 ReflectAcrossHorizontalPlane(Vector3 position, float planeY) =>
        new(position.X, planeY * 2f - position.Y, position.Z);

    private void CreatePreparedShaderPipeline(ID3D11Device device)
    {
        if(_shaderPipelineCreated || !_shaderBytecodePreparation.IsCompletedSuccessfully) return;

        ShaderBytecodeBundle bytecode = _shaderBytecodePreparation.Result;
        InputElementDescription[] layout =
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("COLOR", 0, Format.R32G32B32A32_Float, 24, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
            new("TEXCOORD", 1, Format.R32G32B32A32_Float, 48, 0),
            new("TEXCOORD", 2, Format.R32_Float, 64, 0),
            new("TEXCOORD", 3, Format.R32G32B32A32_Float, 68, 0),
            new("TEXCOORD", 4, Format.R32_Float, 84, 0)
        ];

        _vertexShader = device.CreateVertexShader(bytecode.Vertex.Span);
        _pixelShader = device.CreatePixelShader(bytecode.Pixel.Span);
        _shadowVertexShader = device.CreateVertexShader(bytecode.ShadowVertex.Span);
        _shadowPixelShader = device.CreatePixelShader(bytecode.ShadowPixel.Span);
        _skyVertexShader = device.CreateVertexShader(bytecode.SkyVertex.Span);
        _skyPixelShader = device.CreatePixelShader(bytecode.SkyPixel.Span);
        _rainVertexShader = device.CreateVertexShader(bytecode.RainVertex.Span);
        _rainPixelShader = device.CreatePixelShader(bytecode.RainPixel.Span);
        _groundPixelShader = device.CreatePixelShader(bytecode.GroundPixel.Span);
        _postProcessPixelShader = device.CreatePixelShader(bytecode.PostProcessPixel.Span);
        _sunOcclusionPixelShader = device.CreatePixelShader(bytecode.SunOcclusionPixel.Span);
        _emissionCoverageCompute = device.CreateComputeShader(bytecode.EmissionCoverageCompute.Span);
        _shadowRangeCompute = device.CreateComputeShader(bytecode.ShadowRangeCompute.Span);
        _rangeReductionCompute = device.CreateComputeShader(bytecode.RangeReductionCompute.Span);

        _inputLayout = device.CreateInputLayout(layout, bytecode.Vertex.Span);
        _shaderPipelineCreated = true;
        CompleteRendererStartupOverlay();
    }

    private void CompleteRendererStartupOverlay()
    {
        if(RendererStartupOverlay.Visibility != Visibility.Visible) return;
        RendererStartupStageText.Text = "渲染器已就绪";
        RendererStartupProgressText.Text = "完成";
        RendererStartupProgressBar.IsIndeterminate = false;
        RendererStartupProgressBar.Value = 100d;
        DoubleAnimation fade = new(
            RendererStartupOverlay.Opacity,
            0d,
            TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            RendererStartupOverlay.Visibility = Visibility.Collapsed;
            RendererStartupOverlay.Opacity = 1d;
        };
        RendererStartupOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private static ShaderBytecodeBundle CompileShaderBundle(Action<int, int> reportProgress)
    {
        using Stream stream = typeof(VoxelViewport).Assembly.GetManifestResourceStream(ShaderResourceName)
            ?? throw new InvalidOperationException($"缺少内嵌着色器 {ShaderResourceName}。");
        using StreamReader reader = new(stream);
        string source = reader.ReadToEnd();
        (string EntryPoint, string Profile)[] entries =
        [
            ("VSMain", "vs_4_0"),
            ("PSMain", "ps_4_0"),
            ("ShadowVS", "vs_4_0"),
            ("ShadowPS", "ps_4_0"),
            ("SkyVS", "vs_4_0"),
            ("SkyPS", "ps_4_0"),
            ("RainVS", "vs_4_0"),
            ("RainPS", "ps_4_0"),
            ("GroundPS", "ps_4_0"),
            ("PostProcessPS", "ps_4_0"),
            ("SunOcclusionPS", "ps_4_0"),
            ("EmissionCoverageCS", "cs_5_0"),
            ("ShadowRangeCS", "cs_5_0"),
            ("RangeReductionCS", "cs_5_0")

        ];
        ReadOnlyMemory<byte>[] compiled = new ReadOnlyMemory<byte>[entries.Length];
        int completed = 0;
        reportProgress(0, entries.Length);
        Parallel.For(
            0,
            entries.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) },
            index =>
            {
                (string entryPoint, string profile) = entries[index];
                compiled[index] = Compiler.Compile(
                    source,
                    entryPoint,
                    "VoxelPreview.hlsl",
                    profile,
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None);
                reportProgress(Interlocked.Increment(ref completed), entries.Length);
            });
        return new ShaderBytecodeBundle(
            compiled[0],
            compiled[1],
            compiled[2],
            compiled[3],
            compiled[4],
            compiled[5],
            compiled[6],
            compiled[7],
            compiled[8],
            compiled[9], compiled[10], compiled[11], compiled[12], compiled[13]);
    }

    private sealed record ShaderBytecodeBundle(
        ReadOnlyMemory<byte> Vertex,
        ReadOnlyMemory<byte> Pixel,
        ReadOnlyMemory<byte> ShadowVertex,
        ReadOnlyMemory<byte> ShadowPixel,
        ReadOnlyMemory<byte> SkyVertex,
        ReadOnlyMemory<byte> SkyPixel,
        ReadOnlyMemory<byte> RainVertex,
        ReadOnlyMemory<byte> RainPixel,
        ReadOnlyMemory<byte> GroundPixel,
        ReadOnlyMemory<byte> PostProcessPixel,
        ReadOnlyMemory<byte> SunOcclusionPixel,
        ReadOnlyMemory<byte> EmissionCoverageCompute,
        ReadOnlyMemory<byte> ShadowRangeCompute,
        ReadOnlyMemory<byte> RangeReductionCompute);

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if(!IsPointerInsideSurface(e))
        {
            CancelCameraGesture();
            return;
        }
        if(NavigationMode == ViewportNavigationMode.Observer)
        {
            ActivateObserverPointerLock();
            e.Handled = true;
            return;
        }
        BeginCameraGesture(CameraGesture.Rotate, e);
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if(!IsPointerInsideSurface(e))
        {
            CancelCameraGesture();
            return;
        }
        if(NavigationMode == ViewportNavigationMode.Observer)
        {
            ActivateObserverPointerLock();
            e.Handled = true;
            return;
        }
        BeginCameraGesture(CameraGesture.Pan, e);
    }

    private void OnPreviewMouseButtonUp(object sender, MouseButtonEventArgs e)
    {
        CameraGesture gesture = e.ChangedButton switch
        {
            MouseButton.Left => CameraGesture.Rotate,
            MouseButton.Right => CameraGesture.Pan,
            _ => CameraGesture.None,
        };
        if(gesture == CameraGesture.None || !_cameraGestureStartedInsideSurface) return;
        if(NavigationMode == ViewportNavigationMode.Observer)
        {
            e.Handled = true;
            return;
        }
        if(FinishCameraGesture(gesture, e.Timestamp)) e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if(NavigationMode == ViewportNavigationMode.Observer)
        {
            HandleObserverMouseMove(e);
            return;
        }
        if(_lastPointer is not Point previous ||
           _cameraGesture == CameraGesture.None ||
           !_cameraGestureStartedInsideSurface)
        {
            return;
        }
        if((_cameraGesture == CameraGesture.Rotate && e.LeftButton != MouseButtonState.Pressed) ||
           (_cameraGesture == CameraGesture.Pan && e.RightButton != MouseButtonState.Pressed))
        {
            FinishCameraGesture(_cameraGesture, e.Timestamp);
            return;
        }

        Point current = e.GetPosition(Surface);
        Vector2 pointerDelta = new((float)(current.X - previous.X), (float)(current.Y - previous.Y));
        if(pointerDelta == Vector2.Zero) return;
        float elapsed = CalculatePointerSampleElapsedSeconds(e.Timestamp, _lastPointerTimestamp);
        if(_cameraGesture == CameraGesture.Rotate)
        {
            _camera.QueueRotationGesture(pointerDelta, elapsed);
        }
        else
        {
            _camera.QueuePan(pointerDelta, Math.Max((float)ActualHeight, 1f));
        }
        _lastPointer = current;
        _lastPointerTimestamp = e.Timestamp;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Surface.Focus();
        if(NavigationMode == ViewportNavigationMode.Observer)
        {
            e.Handled = true;
            return;
        }
        float previousZoomTarget = _camera.ZoomTargetDistance;
        _camera.QueueZoom(e.Delta);
        NotifyZoomTargetDistanceChanged(previousZoomTarget);
        Surface.InvalidateVisual();
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        // ReleaseMouseCapture raises a late LostMouseCapture synchronously. Ignore that callback before applying
        // observer-mode semantics, otherwise a completed orbit release can be mistaken for pointer-lock loss.
        if(_releasingGestureCapture || Surface.IsMouseCaptured) return;
        if(NavigationMode == ViewportNavigationMode.Observer && !_changingNavigationMode)
        {
            ExitObserverMode();
            return;
        }
        CameraGestureTermination termination = ResolveLostCaptureTermination(
            _releasingGestureCapture,
            Surface.IsMouseCaptured,
            _cameraGesture,
            e.LeftButton,
            e.RightButton);
        if(termination == CameraGestureTermination.Ignore) return;
        if(termination == CameraGestureTermination.Finish)
        {
            FinishCameraGesture(_cameraGesture, e.Timestamp);
            return;
        }
        CancelCameraGesture();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if(NavigationMode != ViewportNavigationMode.Observer || e.Key != Key.Escape) return;
        ExitObserverMode();
        e.Handled = true;
    }

    private void BeginCameraGesture(CameraGesture gesture, MouseButtonEventArgs e)
    {
        if(!IsPointerInsideSurface(e)) return;
        if(_cameraGesture != CameraGesture.None) CancelCameraGesture();
        _cameraGesture = gesture;
        _cameraGestureStartedInsideSurface = true;
        _lastPointer = e.GetPosition(Surface);
        _lastPointerTimestamp = e.Timestamp;
        if(gesture == CameraGesture.Rotate) _camera.BeginRotationGesture();
        else _camera.CancelRotationGesture();
        if(!Surface.CaptureMouse())
        {
            CancelCameraGesture();
            return;
        }
        Surface.Focus();
        e.Handled = true;
    }

    private bool FinishCameraGesture(CameraGesture gesture, int inputTimestamp)
    {
        if(_cameraGesture != gesture || !_cameraGestureStartedInsideSurface) return false;
        if(gesture == CameraGesture.Rotate) FinishRotationGesture(inputTimestamp);
        ResetCameraGesture();
        Surface.InvalidateVisual();
        return true;
    }

    private void FinishRotationGesture(int inputTimestamp)
    {
        float elapsed = CalculatePointerReleaseElapsedSeconds(inputTimestamp, _lastPointerTimestamp);
        _camera.EndRotationGesture(elapsed);
    }

    private float CalculateAdaptiveKeyboardMovementSpeed() => Math.Clamp(_camera.Distance * 0.8f, 6f, 96f);

    internal static float CalculatePointerSampleElapsedSeconds(int currentTimestamp, int previousTimestamp)
    {
        int elapsedMilliseconds = unchecked(currentTimestamp - previousTimestamp);
        if(elapsedMilliseconds < 0) return 0f;
        return Math.Max(elapsedMilliseconds, 1) / 1000f;
    }

    internal static float CalculatePointerReleaseElapsedSeconds(int currentTimestamp, int previousTimestamp)
    {
        int elapsedMilliseconds = unchecked(currentTimestamp - previousTimestamp);
        return elapsedMilliseconds <= 0 ? 0f : elapsedMilliseconds / 1000f;
    }

    internal static bool ShouldFinishCameraGesture(
        CameraGesture gesture,
        MouseButtonState leftButton,
        MouseButtonState rightButton) =>
        (gesture == CameraGesture.Rotate && leftButton == MouseButtonState.Released) ||
        (gesture == CameraGesture.Pan && rightButton == MouseButtonState.Released);

    internal static CameraGestureTermination ResolveLostCaptureTermination(
        bool releasingCapture,
        bool surfaceIsCaptured,
        CameraGesture gesture,
        MouseButtonState leftButton,
        MouseButtonState rightButton)
    {
        if(releasingCapture || surfaceIsCaptured || gesture == CameraGesture.None)
            return CameraGestureTermination.Ignore;
        return ShouldFinishCameraGesture(gesture, leftButton, rightButton)
            ? CameraGestureTermination.Finish
            : CameraGestureTermination.Cancel;
    }

    internal static float CalculateFrameElapsedSeconds(double nowSeconds, double previousSeconds)
    {
        double elapsed = nowSeconds - previousSeconds;
        if(!double.IsFinite(elapsed) || elapsed <= 0d) return 0f;
        return (float)Math.Min(elapsed, MaximumFrameElapsedSeconds);
    }

    private void CancelCameraGesture()
    {
        if(_cameraGesture == CameraGesture.Rotate) _camera.CancelRotationGesture();
        ResetCameraGesture();
    }

    private void ResetCameraGesture()
    {
        _cameraGesture = CameraGesture.None;
        _cameraGestureStartedInsideSurface = false;
        _lastPointer = null;
        _lastPointerTimestamp = 0;
        if(!Surface.IsMouseCaptured) return;
        _releasingGestureCapture = true;
        try
        {
            Surface.ReleaseMouseCapture();
        }
        finally
        {
            _releasingGestureCapture = false;
        }
    }

    private bool IsPointerInsideSurface(MouseEventArgs e)
    {
        if(!Surface.IsLoaded || !Surface.IsVisible || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
            return false;
        Point pointer = e.GetPosition(Surface);
        return double.IsFinite(pointer.X) &&
               double.IsFinite(pointer.Y) &&
               pointer.X >= 0 &&
               pointer.Y >= 0 &&
               pointer.X <= Surface.ActualWidth &&
               pointer.Y <= Surface.ActualHeight;
    }

    private void HandleObserverMouseMove(MouseEventArgs e)
    {
        if(!Surface.IsMouseCaptured) return;
        if(!TryGetObserverPointerAnchor(out Point center, out NativePoint screenCenter)) return;
        Point current = e.GetPosition(Surface);
        Vector2 pointerDelta = new((float)(current.X - center.X), (float)(current.Y - center.Y));
        if(pointerDelta.LengthSquared() >= 0.01f)
        {
            _camera.QueueFirstPersonLook(ScaleObserverPointerDelta(pointerDelta, _observerMouseSensitivity));
        }
        CenterObserverPointer(screenCenter);
        e.Handled = true;
    }

    internal static Vector2 ScaleObserverPointerDelta(Vector2 pointerDelta, float sensitivity)
    {
        if(!float.IsFinite(pointerDelta.X) || !float.IsFinite(pointerDelta.Y))
            throw new ArgumentOutOfRangeException(nameof(pointerDelta));
        ValidateObserverMouseSensitivity(sensitivity);
        return pointerDelta * sensitivity;
    }

    private static void ValidateObserverMouseSensitivity(float sensitivity)
    {
        if(!float.IsFinite(sensitivity) || sensitivity is < MinimumObserverMouseSensitivity or > MaximumObserverMouseSensitivity)
            throw new ArgumentOutOfRangeException(nameof(sensitivity));
    }

    private void ActivateObserverPointerLock()
    {
        if(NavigationMode != ViewportNavigationMode.Observer) return;
        if(Surface.IsMouseCaptured)
        {
            Surface.Focus();
            return;
        }

        if(!GetCursorPos(out NativePoint current)) return;
        _observerRestorePointer = current;
        _hasObserverRestorePointer = true;
        Surface.Focus();
        if(!Mouse.Capture(Surface, CaptureMode.Element))
        {
            _hasObserverRestorePointer = false;
            return;
        }
        Surface.Cursor = Cursors.None;
        CenterObserverPointer();
    }

    private void ReleaseObserverPointerLock(bool restorePointer)
    {
        Surface.Cursor = null;
        if(Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
        if(restorePointer && _hasObserverRestorePointer)
            SetCursorPos(_observerRestorePointer.X, _observerRestorePointer.Y);
        _hasObserverRestorePointer = false;
    }

    private void CenterObserverPointer()
    {
        if(TryGetObserverPointerAnchor(out _, out NativePoint screenCenter)) CenterObserverPointer(screenCenter);
    }

    private static void CenterObserverPointer(NativePoint screenCenter)
    {
        if(GetCursorPos(out NativePoint current) &&
           current.X == screenCenter.X && current.Y == screenCenter.Y) return;
        SetCursorPos(screenCenter.X, screenCenter.Y);
    }

    private bool TryGetObserverPointerAnchor(out Point localCenter, out NativePoint screenCenter)
    {
        localCenter = default;
        screenCenter = default;
        if(!Surface.IsLoaded || !Surface.IsVisible) return false;
        try
        {
            Point desiredScreenCenter = Surface.PointToScreen(ObserverPointerCenter());
            screenCenter.X = (int)Math.Round(desiredScreenCenter.X);
            screenCenter.Y = (int)Math.Round(desiredScreenCenter.Y);
            // SetCursorPos can only use integer physical pixels. Convert that exact snapped pixel back to DIPs,
            // otherwise its synthetic MouseMove carries a small one-way delta on fractional DPI/layout centers.
            localCenter = Surface.PointFromScreen(new Point(screenCenter.X, screenCenter.Y));
            return true;
        }
        catch(InvalidOperationException)
        {
            // The presentation source can disappear between a fullscreen transition and the mouse event.
            return false;
        }
    }

    private Point ObserverPointerCenter() => new(
        Math.Max(Surface.ActualWidth, 1d) * 0.5d,
        Math.Max(Surface.ActualHeight, 1d) * 0.5d);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CameraConstants
    {
        public Matrix4x4 ViewProjection;
        public Matrix4x4 InverseViewProjection;
        public Matrix4x4 SunViewProjection;
        public Matrix4x4 ReflectionViewProjection;
        public Matrix4x4 ReflectionViewProjection1;
        public Matrix4x4 ReflectionViewProjection2;
        public Matrix4x4 ReflectionViewProjection3;
        public Vector4 ReflectionPlaneHeights;
        public Vector4 CameraPosition;
        public Vector4 CameraRightAndHorizontalScale;
        public Vector4 CameraUpAndVerticalScale;
        public Vector4 CameraForward;
        public Vector4 SunDirectionAndStrength;
        public Vector4 MoonDirectionAndStrength;
        public Vector4 SunlightColor;
        public Vector4 MoonlightColor;
        public Vector4 AmbientColorAndStrength;
        public Vector4 SkyZenithColor;
        public Vector4 SkyHorizonColor;
        public Vector4 FogColorAndDensity;
        public Vector4 EnvironmentOptions;
        public Vector4 ViewportOptions;
        public Vector4 ShadowOptions;
        public Vector4 RainOptions;
        public Vector4 QualityOptions;
        public Vector4 ReflectionOptions;
        public Vector4 SunOpticsProjection;
        public Vector4 SunOpticsOptions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialConstants
    {
        public Vector4 Options;
    }

    private sealed record StagedSectionBuild(
        SectionRenderCache Cache,
        SectionRenderBuildRequest Request,
        string? Dimension);

    private enum ViewportContent
    {
        Preview,
        Sections,
        Model,
    }

    internal enum CameraGesture
    {
        None,
        Rotate,
        Pan,
    }

    internal enum CameraGestureTermination
    {
        Ignore,
        Finish,
        Cancel,
    }

    private sealed record ResourceTransitionState(
        Minecraft1122BlockRenderResources? PreviousResources,
        SectionRenderCache PreviousSectionCache,
        string? PreviousActiveDimension,
        ViewportContent PreviousContent);

    private sealed class GpuGeometry : IDisposable
    {
        private bool _disposed;

        private GpuGeometry(ID3D11Buffer vertexBuffer, ID3D11Buffer indexBuffer, int indexCount)
        {
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
            IndexCount = indexCount;
        }

        public ID3D11Buffer VertexBuffer { get; }

        public ID3D11Buffer IndexBuffer { get; }

        public int IndexCount { get; }

        public static GpuGeometry Create(ID3D11Device device, VoxelVertex[] vertices, uint[] indices)
        {
            if(vertices.Length == 0 || indices.Length == 0) throw new ArgumentException("GPU 网格不能为空。");
            ID3D11Buffer? vertexBuffer = null;
            ID3D11Buffer? indexBuffer = null;
            try
            {
                vertexBuffer = device.CreateBuffer(vertices.AsSpan(), BindFlags.VertexBuffer);
                indexBuffer = device.CreateBuffer(indices.AsSpan(), BindFlags.IndexBuffer);
                return new GpuGeometry(vertexBuffer, indexBuffer, indices.Length);
            }
            catch
            {
                vertexBuffer?.Dispose();
                indexBuffer?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if(_disposed) return;
            _disposed = true;
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }
    }

    private sealed class GpuSectionGeometry : IDisposable
    {
        public GpuSectionGeometry(SectionRenderGeometry source, GpuGeometry geometry)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        }

        public SectionRenderGeometry Source { get; }

        public GpuGeometry Geometry { get; }

        public void Dispose() => Geometry.Dispose();
    }

    private sealed class GpuShadowMap : IDisposable
    {
        private bool _disposed;

        private GpuShadowMap(ID3D11Texture2D texture, ID3D11DepthStencilView depthView, ID3D11ShaderResourceView view)
        {
            Texture = texture;
            DepthView = depthView;
            View = view;
        }

        public ID3D11Texture2D Texture { get; }

        public ID3D11DepthStencilView DepthView { get; }

        public ID3D11ShaderResourceView View { get; }

        public static GpuShadowMap Create(ID3D11Device device, int textureSize)
        {
            if(textureSize <= 0) throw new ArgumentOutOfRangeException(nameof(textureSize));
            ID3D11Texture2D? texture = null;
            ID3D11DepthStencilView? depthView = null;
            ID3D11ShaderResourceView? view = null;
            try
            {
                texture = device.CreateTexture2D(new Texture2DDescription(
                    Format.R32_Typeless,
                    (uint)textureSize,
                    (uint)textureSize,
                    1,
                    1,
                    BindFlags.DepthStencil | BindFlags.ShaderResource));
                depthView = device.CreateDepthStencilView(
                    texture,
                    new DepthStencilViewDescription(
                        DepthStencilViewDimension.Texture2D,
                        Format.D32_Float,
                        0,
                        0,
                        1,
                        DepthStencilViewFlags.None));
                view = device.CreateShaderResourceView(
                    texture,
                    new ShaderResourceViewDescription(
                        ShaderResourceViewDimension.Texture2D,
                        Format.R32_Float,
                        0,
                        1,
                        0,
                        1,
                        BufferExtendedShaderResourceViewFlags.None));
                return new GpuShadowMap(texture, depthView, view);
            }
            catch
            {
                view?.Dispose();
                depthView?.Dispose();
                texture?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if(_disposed) return;
            _disposed = true;
            View.Dispose();
            DepthView.Dispose();
            Texture.Dispose();
        }
    }

    private sealed class GpuPostProcessTarget : IDisposable
    {
        private bool _disposed;

        private GpuPostProcessTarget(
            ID3D11Texture2D texture,
            ID3D11RenderTargetView renderView,
            ID3D11ShaderResourceView view,
            ID3D11Texture2D surfaceTexture,
            ID3D11RenderTargetView surfaceRenderView,
            ID3D11ShaderResourceView surfaceView,
            ID3D11Texture2D materialTexture,
            ID3D11RenderTargetView materialRenderView,
            ID3D11ShaderResourceView materialView,
            ID3D11Texture2D opaqueTexture,
            ID3D11ShaderResourceView opaqueView,
            ID3D11Texture2D opaqueDepthTexture,
            ID3D11ShaderResourceView opaqueDepthView,
            ID3D11Texture2D depthTexture,
            ID3D11DepthStencilView depthView,
            ID3D11ShaderResourceView depthShaderView,
            ID3D11Texture2D reflectionTexture,
            ID3D11RenderTargetView[] reflectionRenderView,
            ID3D11ShaderResourceView reflectionView,
            ID3D11Texture2D reflectionDepthTexture,
            ID3D11DepthStencilView reflectionDepthView,
            int reflectionWidth,
            int reflectionHeight,
            int width,
            int height)
        {
            Texture = texture;
            RenderView = renderView;
            View = view;
            SurfaceTexture = surfaceTexture;
            SurfaceRenderView = surfaceRenderView;
            SurfaceView = surfaceView;
            MaterialTexture = materialTexture;
            MaterialRenderView = materialRenderView;
            MaterialView = materialView;
            OpaqueTexture = opaqueTexture;
            OpaqueView = opaqueView;
            OpaqueDepthTexture = opaqueDepthTexture;
            OpaqueDepthView = opaqueDepthView;
            DepthTexture = depthTexture;
            DepthView = depthView;
            DepthShaderView = depthShaderView;
            ReflectionTexture = reflectionTexture;
            ReflectionRenderView = reflectionRenderView;
            ReflectionView = reflectionView;
            ReflectionDepthTexture = reflectionDepthTexture;
            ReflectionDepthView = reflectionDepthView;
            ReflectionWidth = reflectionWidth;
            ReflectionHeight = reflectionHeight;
            Width = width;
            Height = height;
        }

        public ID3D11Texture2D Texture { get; }

        public ID3D11RenderTargetView RenderView { get; }

        public ID3D11ShaderResourceView View { get; }

        public ID3D11Texture2D SurfaceTexture { get; }

        public ID3D11RenderTargetView SurfaceRenderView { get; }

        public ID3D11ShaderResourceView SurfaceView { get; }

        public ID3D11Texture2D MaterialTexture { get; }

        public ID3D11RenderTargetView MaterialRenderView { get; }

        public ID3D11ShaderResourceView MaterialView { get; }

        public ID3D11Texture2D OpaqueTexture { get; }

        public ID3D11ShaderResourceView OpaqueView { get; }

        public ID3D11Texture2D OpaqueDepthTexture { get; }

        public ID3D11ShaderResourceView OpaqueDepthView { get; }

        public ID3D11Texture2D DepthTexture { get; }

        public ID3D11DepthStencilView DepthView { get; }

        public ID3D11ShaderResourceView DepthShaderView { get; }

        public ID3D11Texture2D ReflectionTexture { get; }

        public ID3D11RenderTargetView[] ReflectionRenderView { get; }

        public ID3D11ShaderResourceView ReflectionView { get; }

        public ID3D11Texture2D ReflectionDepthTexture { get; }

        public ID3D11DepthStencilView ReflectionDepthView { get; }

        public int ReflectionWidth { get; }

        public int ReflectionHeight { get; }

        public int Width { get; }

        public int Height { get; }

        public static GpuPostProcessTarget Create(ID3D11Device device, int width, int height)
        {
            if(width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if(height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            ID3D11Texture2D? texture = null;
            ID3D11RenderTargetView? renderView = null;
            ID3D11ShaderResourceView? view = null;
            ID3D11Texture2D? surfaceTexture = null;
            ID3D11RenderTargetView? surfaceRenderView = null;
            ID3D11ShaderResourceView? surfaceView = null;
            ID3D11Texture2D? materialTexture = null;
            ID3D11RenderTargetView? materialRenderView = null;
            ID3D11ShaderResourceView? materialView = null;
            ID3D11Texture2D? opaqueTexture = null;
            ID3D11ShaderResourceView? opaqueView = null;
            ID3D11Texture2D? opaqueDepthTexture = null;
            ID3D11ShaderResourceView? opaqueDepthView = null;
            ID3D11Texture2D? depthTexture = null;
            ID3D11DepthStencilView? depthView = null;
            ID3D11ShaderResourceView? depthShaderView = null;
            ID3D11Texture2D? reflectionTexture = null;
            ID3D11RenderTargetView[]? reflectionRenderView = null;
            ID3D11ShaderResourceView? reflectionView = null;
            ID3D11Texture2D? reflectionDepthTexture = null;
            ID3D11DepthStencilView? reflectionDepthView = null;
            try
            {
                texture = device.CreateTexture2D(new Texture2DDescription(
                    Format.R16G16B16A16_Float,
                    (uint)width,
                    (uint)height,
                    1,
                    1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource));
                renderView = device.CreateRenderTargetView(texture);
                view = device.CreateShaderResourceView(texture);
                surfaceTexture = device.CreateTexture2D(new Texture2DDescription(
                    Format.R16G16B16A16_Float,
                    (uint)width,
                    (uint)height,
                    1,
                    1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource));
                surfaceRenderView = device.CreateRenderTargetView(surfaceTexture);
                surfaceView = device.CreateShaderResourceView(surfaceTexture);
                materialTexture = device.CreateTexture2D(new Texture2DDescription(
                    Format.R16G16B16A16_Float,
                    (uint)width,
                    (uint)height,
                    1,
                    1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource));
                materialRenderView = device.CreateRenderTargetView(materialTexture);
                materialView = device.CreateShaderResourceView(materialTexture);
                depthTexture = device.CreateTexture2D(new Texture2DDescription(
                    Format.R32_Typeless,
                    (uint)width,
                    (uint)height,
                    1,
                    1,
                    BindFlags.DepthStencil | BindFlags.ShaderResource));
                depthView = device.CreateDepthStencilView(
                    depthTexture,
                    new DepthStencilViewDescription(
                        DepthStencilViewDimension.Texture2D,
                        Format.D32_Float,
                        0,
                        0,
                        1,
                        DepthStencilViewFlags.None));
                depthShaderView = device.CreateShaderResourceView(
                    depthTexture,
                    new ShaderResourceViewDescription(
                        ShaderResourceViewDimension.Texture2D,
                        Format.R32_Float,
                        0,
                        1,
                        0,
                        1,
                        BufferExtendedShaderResourceViewFlags.None));
                opaqueTexture = device.CreateTexture2D(new Texture2DDescription(
                    Format.R16G16B16A16_Float,
                    (uint)width,
                    (uint)height,
                    1,
                    1,
                    BindFlags.ShaderResource));
                opaqueView = device.CreateShaderResourceView(opaqueTexture);
                opaqueDepthTexture = device.CreateTexture2D(new Texture2DDescription(
                    Format.R32_Typeless,
                    (uint)width,
                    (uint)height,
                    1,
                    1,
                    BindFlags.ShaderResource));
                opaqueDepthView = device.CreateShaderResourceView(
                    opaqueDepthTexture,
                    new ShaderResourceViewDescription(
                        ShaderResourceViewDimension.Texture2D,
                        Format.R32_Float,
                        0,
                        1,
                        0,
                        1,
                        BufferExtendedShaderResourceViewFlags.None));
                int reflectionWidth = width;
                int reflectionHeight = height;
                reflectionTexture = device.CreateTexture2D(new Texture2DDescription(
                    Format.R16G16B16A16_Float,
                    (uint)reflectionWidth,
                    (uint)reflectionHeight,
                    4,
                    1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource));
                reflectionRenderView = Enumerable.Range(0, 4).Select(index => device.CreateRenderTargetView(reflectionTexture, new RenderTargetViewDescription(RenderTargetViewDimension.Texture2DArray, Format.R16G16B16A16_Float, 0, (uint)index, 1))).ToArray();
                reflectionView = device.CreateShaderResourceView(reflectionTexture);
                reflectionDepthTexture = device.CreateTexture2D(new Texture2DDescription(
                    Format.D32_Float,
                    (uint)reflectionWidth,
                    (uint)reflectionHeight,
                    1,
                    1,
                    BindFlags.DepthStencil));
                reflectionDepthView = device.CreateDepthStencilView(reflectionDepthTexture);
                return new GpuPostProcessTarget(
                    texture,
                    renderView,
                    view,
                    surfaceTexture,
                    surfaceRenderView,
                    surfaceView,
                    materialTexture,
                    materialRenderView,
                    materialView,
                    opaqueTexture,
                    opaqueView,
                    opaqueDepthTexture,
                    opaqueDepthView,
                    depthTexture,
                    depthView,
                    depthShaderView,
                    reflectionTexture,
                    reflectionRenderView,
                    reflectionView,
                    reflectionDepthTexture,
                    reflectionDepthView,
                    reflectionWidth,
                    reflectionHeight,
                    width,
                    height);
            }
            catch
            {
                reflectionDepthView?.Dispose();
                reflectionDepthTexture?.Dispose();
                reflectionView?.Dispose();
                if(reflectionRenderView is not null) foreach(var viewToDispose in reflectionRenderView) viewToDispose.Dispose();
                reflectionTexture?.Dispose();
                opaqueDepthView?.Dispose();
                opaqueDepthTexture?.Dispose();
                opaqueView?.Dispose();
                opaqueTexture?.Dispose();
                depthShaderView?.Dispose();
                depthView?.Dispose();
                depthTexture?.Dispose();
                materialView?.Dispose();
                materialRenderView?.Dispose();
                materialTexture?.Dispose();
                surfaceView?.Dispose();
                surfaceRenderView?.Dispose();
                surfaceTexture?.Dispose();
                view?.Dispose();
                renderView?.Dispose();
                texture?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if(_disposed) return;
            _disposed = true;
            ReflectionDepthView.Dispose();
            ReflectionDepthTexture.Dispose();
            ReflectionView.Dispose();
            foreach(var reflectionRenderView in ReflectionRenderView) reflectionRenderView.Dispose();
            ReflectionTexture.Dispose();
            OpaqueDepthView.Dispose();
            OpaqueDepthTexture.Dispose();
            OpaqueView.Dispose();
            OpaqueTexture.Dispose();
            DepthShaderView.Dispose();
            DepthView.Dispose();
            DepthTexture.Dispose();
            MaterialView.Dispose();
            MaterialRenderView.Dispose();
            MaterialTexture.Dispose();
            SurfaceView.Dispose();
            SurfaceRenderView.Dispose();
            SurfaceTexture.Dispose();
            View.Dispose();
            RenderView.Dispose();
            Texture.Dispose();
        }
    }

    private sealed class GpuAtlas : IDisposable
    {
        private bool _disposed;

        private GpuAtlas(ID3D11Texture2D texture, ID3D11ShaderResourceView view, int width, int height, int revision, int tileSize, int logicalTilesPerRow)
        {
            Texture = texture;
            View = view;
            Width = width;
            Height = height;
            Revision = revision;
            TileSize = tileSize;
            LogicalTilesPerRow = logicalTilesPerRow;
        }

        public ID3D11Texture2D Texture { get; }

        public ID3D11ShaderResourceView View { get; }

        public int Width { get; }

        public int Height { get; }

        public int Revision { get; private set; }
        public int TileSize { get; }
        public int LogicalTilesPerRow { get; }

        public static GpuAtlas Create(
            ID3D11DeviceContext context,
            ID3D11Device device,
            MinecraftTextureAtlasSnapshot snapshot)
        {
            ID3D11Texture2D? texture = null;
            ID3D11ShaderResourceView? view = null;
            try
            {
                texture = device.CreateTexture2D(new Texture2DDescription(
                    Format.B8G8R8A8_UNorm_SRgb,
                    (uint)snapshot.Width,
                    (uint)snapshot.Height,
                    1,
                    (uint)(Math.Log2(Math.Min(snapshot.TileSize, Math.Min(snapshot.Width, snapshot.Height))) + 1),
                    BindFlags.ShaderResource | BindFlags.RenderTarget)
                {
                    MiscFlags = ResourceOptionFlags.GenerateMips,
                });
                context.UpdateSubresource(
                    snapshot.BgraPixels.AsSpan(),
                    texture,
                    0,
                    (uint)snapshot.RowPitch,
                    0);
                view = device.CreateShaderResourceView(texture);
                context.GenerateMips(view);
                return new GpuAtlas(texture, view, snapshot.Width, snapshot.Height, snapshot.Revision, snapshot.TileSize, snapshot.LogicalTilesPerRow);
            }
            catch
            {
                view?.Dispose();
                texture?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if(_disposed) return;
            _disposed = true;
            View.Dispose();
            Texture.Dispose();
        }
    }

    private sealed class GpuImportedModel : IDisposable
    {
        private bool _disposed;

        private GpuImportedModel(IReadOnlyList<GpuModelPrimitive> primitives, IReadOnlyList<GpuModelTexture> textures)
        {
            Primitives = primitives;
            Textures = textures;
        }

        public IReadOnlyList<GpuModelPrimitive> Primitives { get; }

        public IReadOnlyList<GpuModelTexture> Textures { get; }

        public static GpuImportedModel Create(ID3D11DeviceContext context, ID3D11Device device, ImportedModel model)
        {
            var textures = new List<GpuModelTexture>(model.Textures.Count);
            var primitives = new List<GpuModelPrimitive>(model.Primitives.Count);
            try
            {
                foreach(ImportedModelTexture texture in model.Textures)
                {
                    textures.Add(GpuModelTexture.Create(context, device, texture));
                }
                foreach(ImportedModelPrimitive primitive in model.Primitives)
                {
                    (VoxelVertex[] vertices, uint[] indices) = BuildGeometry(primitive);
                    GpuGeometry geometry = GpuGeometry.Create(device, vertices, indices);
                    primitives.Add(new GpuModelPrimitive(
                        geometry,
                        primitive.Material.BaseColorTextureIndex,
                        primitive.Material.AlphaMode,
                        primitive.Material.AlphaCutoff));
                }
                return new GpuImportedModel(primitives, textures);
            }
            catch
            {
                foreach(GpuModelPrimitive primitive in primitives) primitive.Geometry.Dispose();
                foreach(GpuModelTexture texture in textures) texture.Dispose();
                throw;
            }
        }

        private static (VoxelVertex[] Vertices, uint[] Indices) BuildGeometry(ImportedModelPrimitive primitive)
        {
            var region = new Vector4(0f, 0f, 1f, 1f);
            Vector3 factorSrgb = ViewportColorSpace.LinearToSrgb(new Vector3(
                primitive.Material.BaseColorFactor.X,
                primitive.Material.BaseColorFactor.Y,
                primitive.Material.BaseColorFactor.Z));
            var factor = new Vector4(factorSrgb, primitive.Material.BaseColorFactor.W);
            VoxelVertex[] frontVertices = primitive.Vertices
                .Select(vertex => new VoxelVertex(
                    vertex.Position,
                    vertex.Normal,
                    factor,
                    vertex.TextureCoordinate,
                    region,
                    0f,
                    VoxelFaceLighting.FullBright.ToVertexData(0)))
                .ToArray();
            if(!primitive.Material.DoubleSided) return (frontVertices, primitive.Indices);

            var vertices = new VoxelVertex[checked(frontVertices.Length * 2)];
            frontVertices.CopyTo(vertices, 0);
            for(int index = 0; index < frontVertices.Length; index++)
            {
                VoxelVertex front = frontVertices[index];
                vertices[frontVertices.Length + index] = front with { Normal = -front.Normal };
            }
            var indices = new uint[checked(primitive.Indices.Length * 2)];
            primitive.Indices.CopyTo(indices, 0);
            uint backVertexOffset = checked((uint)frontVertices.Length);
            for(int index = 0; index < primitive.Indices.Length; index += 3)
            {
                int destination = primitive.Indices.Length + index;
                indices[destination] = checked(primitive.Indices[index] + backVertexOffset);
                indices[destination + 1] = checked(primitive.Indices[index + 2] + backVertexOffset);
                indices[destination + 2] = checked(primitive.Indices[index + 1] + backVertexOffset);
            }
            return (vertices, indices);
        }

        public void Dispose()
        {
            if(_disposed) return;
            _disposed = true;
            foreach(GpuModelPrimitive primitive in Primitives) primitive.Geometry.Dispose();
            foreach(GpuModelTexture texture in Textures) texture.Dispose();
        }
    }

    private sealed record GpuModelPrimitive(
        GpuGeometry Geometry,
        int? TextureIndex,
        ImportedModelAlphaMode AlphaMode,
        float AlphaCutoff);

    private sealed class GpuModelTexture : IDisposable
    {
        private bool _disposed;

        private GpuModelTexture(ID3D11Texture2D texture, ID3D11ShaderResourceView view)
        {
            Texture = texture;
            View = view;
        }

        public ID3D11Texture2D Texture { get; }

        public ID3D11ShaderResourceView View { get; }

        public static GpuModelTexture Create(
            ID3D11DeviceContext context,
            ID3D11Device device,
            ImportedModelTexture source)
        {
            ID3D11Texture2D? texture = null;
            ID3D11ShaderResourceView? view = null;
            try
            {
                texture = device.CreateTexture2D(new Texture2DDescription(
                    Format.B8G8R8A8_UNorm_SRgb,
                    (uint)source.Width,
                    (uint)source.Height,
                    1,
                    1,
                    BindFlags.ShaderResource));
                context.UpdateSubresource(source.BgraPixels.AsSpan(), texture, 0, (uint)source.RowPitch, 0);
                view = device.CreateShaderResourceView(texture);
                return new GpuModelTexture(texture, view);
            }
            catch
            {
                view?.Dispose();
                texture?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if(_disposed) return;
            _disposed = true;
            View.Dispose();
            Texture.Dispose();
        }
    }

    private TextureAtlasRegion PreviewWhiteTextureRegion => _minecraftResources?.Atlas.WhiteTile.Region ?? TextureAtlasRegion.EntireTexture;

    private static class PreviewGeometry
    {
        private static readonly Face[] Faces =
        [
            new(new Cell(1, 0, 0), Vector3.UnitX, [new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1)]),
            new(new Cell(-1, 0, 0), -Vector3.UnitX, [new(0, 0, 1), new(0, 1, 1), new(0, 1, 0), new(0, 0, 0)]),
            new(new Cell(0, 1, 0), Vector3.UnitY, [new(0, 1, 1), new(1, 1, 1), new(1, 1, 0), new(0, 1, 0)]),
            new(new Cell(0, -1, 0), -Vector3.UnitY, [new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)]),
            new(new Cell(0, 0, 1), Vector3.UnitZ, [new(1, 0, 1), new(1, 1, 1), new(0, 1, 1), new(0, 0, 1)]),
            new(new Cell(0, 0, -1), -Vector3.UnitZ, [new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0)])
        ];

        public static (VoxelVertex[] Vertices, uint[] Indices) Build(SceneDelta? delta, TextureAtlasRegion whiteTextureRegion)
        {
            Dictionary<Cell, Vector4> blocks = delta is null ? BuildDemoBlocks() : BuildDeltaBlocks(delta);
            if(blocks.Count == 0)
            {
                blocks[new Cell(0, 0, 0)] = new Vector4(0.74f, 0.43f, 0.96f, 1f);
            }
            List<VoxelVertex> vertices = new(blocks.Count * 8);
            List<uint> indices = new(blocks.Count * 12);

            foreach((Cell cell, Vector4 color) in blocks)
            {
                foreach(Face face in Faces)
                {
                    if(blocks.ContainsKey(cell + face.Neighbor))
                    {
                        continue;
                    }

                    uint start = (uint)vertices.Count;
                    Vector3 origin = new(cell.X, cell.Y, cell.Z);
                    foreach(Vector3 corner in face.Corners)
                    {
                        vertices.Add(new VoxelVertex(
                            origin + corner,
                            face.Normal,
                            color,
                            Vector2.Zero,
                            new Vector4(whiteTextureRegion.U, whiteTextureRegion.V, whiteTextureRegion.Width, whiteTextureRegion.Height),
                            1f,
                            VoxelFaceLighting.FullBright.ToVertexData(0)));
                    }

                    indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
                }
            }

            return (vertices.ToArray(), indices.ToArray());
        }

        private static Dictionary<Cell, Vector4> BuildDeltaBlocks(SceneDelta delta)
        {
            Dictionary<Cell, Vector4> blocks = [];
            foreach(SectionDelta section in delta.Sections)
            {
                foreach(VoxelChange change in section.Changes)
                {
                    BlockPosition position = section.Section.ToBlockPosition(change.LocalIndex);
                    Cell cell = new(position.X, position.Y, position.Z);
                    if(change.After.IsAir) blocks.Remove(cell);
                    else blocks[cell] = ColorFor(change.After);
                }
            }

            return blocks;
        }

        private static Vector4 ColorFor(BlockState state)
        {
            string name = state.Name;
            if(name.Contains("grass", StringComparison.Ordinal)) return new Vector4(0.48f, 0.69f, 0.44f, 1f);
            if(name.Contains("stone", StringComparison.Ordinal) || name.Contains("cobble", StringComparison.Ordinal)) return new Vector4(0.64f, 0.62f, 0.68f, 1f);
            if(name.Contains("wood", StringComparison.Ordinal) || name.Contains("log", StringComparison.Ordinal) || name.Contains("plank", StringComparison.Ordinal)) return new Vector4(0.40f, 0.25f, 0.30f, 1f);
            if(name.Contains("water", StringComparison.Ordinal)) return new Vector4(0.25f, 0.52f, 0.92f, 0.82f);
            if(name.Contains("light", StringComparison.Ordinal) || name.Contains("glow", StringComparison.Ordinal)) return new Vector4(1f, 0.75f, 0.36f, 1f);
            return new Vector4(0.74f, 0.43f, 0.96f, 1f);
        }

        private static Dictionary<Cell, Vector4> BuildDemoBlocks()
        {
            Dictionary<Cell, Vector4> blocks = [];
            Vector4 grass = new(0.48f, 0.69f, 0.44f, 1f);
            Vector4 stone = new(0.64f, 0.62f, 0.68f, 1f);
            Vector4 path = new(0.78f, 0.73f, 0.72f, 1f);
            Vector4 wood = new(0.40f, 0.25f, 0.30f, 1f);
            Vector4 crystal = new(0.74f, 0.43f, 0.96f, 1f);

            for(int x = -8; x <= 8; x++)
            {
                for(int z = -8; z <= 8; z++)
                {
                    blocks[new Cell(x, -1, z)] = stone;
                    bool isPath = Math.Abs(x) <= 1 || Math.Abs(z) <= 1;
                    blocks[new Cell(x, 0, z)] = isPath ? path : grass;
                }
            }

            foreach((int x, int z) in new[] { (-5, -5), (5, -5), (-5, 5), (5, 5) })
            {
                for(int y = 1; y <= 5; y++)
                {
                    blocks[new Cell(x, y, z)] = wood;
                }
                blocks[new Cell(x, 6, z)] = crystal;
            }

            for(int x = -3; x <= 3; x++)
            {
                for(int z = -3; z <= 3; z++)
                {
                    if(Math.Abs(x) == 3 || Math.Abs(z) == 3)
                    {
                        blocks[new Cell(x, 1, z)] = stone;
                    }
                }
            }

            return blocks;
        }

        private readonly record struct Cell(int X, int Y, int Z)
        {
            public static Cell operator +(Cell left, Cell right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        }

        private sealed record Face(Cell Neighbor, Vector3 Normal, Vector3[] Corners);
    }
}
