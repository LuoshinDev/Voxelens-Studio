using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZhuJieJing.App.ConversionPreview;
using ZhuJieJing.App.Resources;
using ZhuJieJing.App.Preferences;
using ZhuJieJing.App.WorldPreview;
using ZhuJieJing.Assets;
using ZhuJieJing.Core;
using ZhuJieJing.Core.Collaboration;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Controls;
using ZhuJieJing.Renderer.Meshing;
using ZhuJieJing.Renderer.Minecraft;
using Microsoft.Win32;

namespace ZhuJieJing.App;

public partial class MainWindow : Window
{
    private const int WindowMessageEnterSizeMove = 0x0231;
    private const int WindowMessageExitSizeMove = 0x0232;
    private const int WindowMessageDestroy = 0x0002;
    private const int WindowMessageKeyDown = 0x0100;
    private const int WindowMessageSystemKeyDown = 0x0104;
    private const int VirtualKeyEscape = 0x001B;
    private const int MaximumRenderableSectionsPerChunk = 32;
    private const int ProgressiveWorldPreviewBatchSize = 4;
    private const float MinimumZoomDistance = 3f;
    private const float MaximumZoomDistance = 512f;

    private IReadOnlyMinecraftWorld? mountedWorld;
    private CancellationTokenSource? mountCancellation;
    private CancellationTokenSource? worldPreviewCancellation;
    private CancellationTokenSource? navigationPreviewCancellation;
    private CancellationTokenSource? conversionPreviewCancellation;
    private CancellationTokenSource? contentTransitionCancellation;
    private CancellationTokenSource? worldExportCancellation;
    private CancellationTokenSource? planCompilationCancellation;
    private CancellationTokenSource? materialModeCancellation;
    private Task<IReadOnlyMinecraftWorld>? activeMountTask;
    private Task? activeWorldPreviewTask;
    private Task? activeNavigationPreviewTask;
    private Task<IMinecraftDowngradePreview>? activeConversionPreviewTask;
    private Task? activeConversionWorkflowTask;
    private Task? activeWorldExportTask;
    private IMinecraftDowngradePreview? activeConversionPreview;
    private readonly Minecraft1122ConversionPreviewSessionCache conversionPreviewCache = new();
    private readonly Dictionary<SectionCoordinate, NormalizedMinecraftSection> displayedWorldSections = [];
    private readonly Dictionary<MinecraftChunkAddress, WorldPreviewStatistics> displayedWorldChunkStatistics = [];
    private MinecraftDimensionId displayedWorldDimension = MinecraftDimensionId.Overworld;
    private LegacySchematicImport? displayedSchematic;
    private SceneDelta? displayedPlanPreview;
    private IReadOnlyList<NormalizedMinecraftSection> displayedPlanSections = [];
    private MinecraftDimensionId? displayedPlanDimension;
    private string? displayedPlanSourcePath;
    private BlockPosition? displayedPlanSpawnPoint;
    private ZjjVisualProfile? displayedPlanVisualProfile;
    private MinecraftVersionDescriptor? displayedPlanVisualVersion;
    private bool displayedPlanVisualProfileInferred;
    private DisplayedContent displayedContent;
    private ResourceSelection? activeResourceSelection;
    private ResourceSelection? originalWorldResourceSelection;
    private readonly MinecraftResourceCacheService minecraftResourceCacheService = new();
    private readonly Stopwatch navigationPreviewClock = Stopwatch.StartNew();
    private readonly NavigationPreviewRequestDebouncer navigationPreviewDebouncer = new();
    private readonly DispatcherTimer navigationPreviewTimer;
    private readonly DispatcherTimer chunkLoadReloadTimer;
    private MinecraftChunkBounds? navigationPreviewCoverage;
    private object? activeWorldExportOwner;
    private bool isWorldExporting;
    private bool sourceActionsEnabled = true;
    private bool isSynchronizingZoomSlider;
    private bool isChangingMaterialMode;
    private bool isNavigationJumping;
    private int navigationRequestGeneration;
    private int previewGeneration;
    private bool isUiReady;
    private bool isClosing;
    private readonly HashSet<IntPtr> windowsInSizeMove = [];
    private readonly HashSet<Window> activeToolWindows = [];
    private readonly HashSet<Window> renderingSuspensionWindows = [];
    private readonly Dictionary<Window, (HwndSource Source, IntPtr Handle)> renderingSuspensionSources = [];
    private int? worldPreviewLoadingOwnerGeneration;
    private int? worldValidationOwnerGeneration;
    private string worldPreviewLoadingStage = "", worldPreviewLoadingProgressText = "";
    private bool worldPreviewLoadingIndeterminate;
    private double worldPreviewLoadingProgress;

    public MainWindow()
    {
        InitializeComponent();
        InitializeOperationDisplay();
        Viewport.CameraTargetChanged += OnCameraTargetChanged;
        Viewport.NavigationTargetChanged += OnNavigationTargetChanged;
        Viewport.NavigationModeChanged += OnNavigationModeChanged;
        Viewport.ZoomTargetDistanceChanged += OnZoomTargetDistanceChanged;
        Viewport.FrameRateUpdated += OnFrameRateUpdated;
        navigationPreviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        navigationPreviewTimer.Tick += NavigationPreviewTimer_Tick;
        chunkLoadReloadTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        chunkLoadReloadTimer.Tick += ChunkLoadReloadTimer_Tick;
        RestorePreviewPreferences();
        isUiReady = true;
        SynchronizeViewControlSlider(Viewport.NavigationMode);
        ApplyEnvironmentSettings();
        UpdateRenderInfoText();
        BeginPreviewPreferencesTracking();
        UpdateConversionControls();
        PropertyChangedEventManager.AddHandler(UiText.Notifications, InterfaceLanguageChanged, "Language");
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if(Application.Current is App app && app.TakePendingStudioCommand() is { } command)
        {
            HandleStudioCommand(command);
            if(command.Action == ZhujieStudioCommand.OpenProjectAction) return;
        }
        if(displayedContent == DisplayedContent.None) await RefreshMaterialModeAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TrackTopLevelWindowRenderingSuspension(this);
    }

    private IntPtr OnTrackedTopLevelWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if(message is WindowMessageKeyDown or WindowMessageSystemKeyDown &&
           wParam.ToInt32() == VirtualKeyEscape &&
           hwnd == new WindowInteropHelper(this).Handle &&
           Viewport.NavigationMode == ViewportNavigationMode.Observer)
        {
            Viewport.ExitObserverMode();
            handled = true;
            return IntPtr.Zero;
        }
        if(message == WindowMessageEnterSizeMove)
        {
            if(windowsInSizeMove.Add(hwnd)) UpdateHostViewportRenderingSuspension();
            return IntPtr.Zero;
        }
        if(message is WindowMessageExitSizeMove or WindowMessageDestroy &&
           windowsInSizeMove.Remove(hwnd))
            UpdateHostViewportRenderingSuspension();
        return IntPtr.Zero;
    }

    private void TrackTopLevelWindowRenderingSuspension(Window window)
    {
        if(!renderingSuspensionWindows.Add(window)) return;
        window.SourceInitialized += TrackedTopLevelWindow_SourceInitialized;
        window.Closed += TrackedTopLevelWindow_Closed;
        if(!ReferenceEquals(window, this))
        {
            window.Activated += TrackedToolWindow_Activated;
            window.Deactivated += TrackedToolWindow_Deactivated;
        }
        AttachTopLevelWindowRenderingSuspension(window);
    }

    private void TrackedTopLevelWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if(sender is Window window) AttachTopLevelWindowRenderingSuspension(window);
    }

    private void AttachTopLevelWindowRenderingSuspension(Window window)
    {
        if(renderingSuspensionSources.ContainsKey(window)) return;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if(handle == IntPtr.Zero) return;
        HwndSource? source = PresentationSource.FromVisual(window) as HwndSource ?? HwndSource.FromHwnd(handle);
        if(source is null) return;
        source.AddHook(OnTrackedTopLevelWindowMessage);
        renderingSuspensionSources.Add(window, (source, handle));
    }

    private void TrackedTopLevelWindow_Closed(object? sender, EventArgs e)
    {
        if(sender is Window window) UntrackTopLevelWindowRenderingSuspension(window);
    }

    private void TrackedToolWindow_Activated(object? sender, EventArgs e)
    {
        if(sender is Window window && activeToolWindows.Add(window))
            UpdateHostViewportRenderingSuspension();
    }

    private void TrackedToolWindow_Deactivated(object? sender, EventArgs e)
    {
        if(sender is Window window && activeToolWindows.Remove(window))
            UpdateHostViewportRenderingSuspension();
    }

    private void UntrackTopLevelWindowRenderingSuspension(Window window)
    {
        window.SourceInitialized -= TrackedTopLevelWindow_SourceInitialized;
        window.Closed -= TrackedTopLevelWindow_Closed;
        window.Activated -= TrackedToolWindow_Activated;
        window.Deactivated -= TrackedToolWindow_Deactivated;
        activeToolWindows.Remove(window);
        renderingSuspensionWindows.Remove(window);
        if(renderingSuspensionSources.Remove(window, out var tracked))
        {
            tracked.Source.RemoveHook(OnTrackedTopLevelWindowMessage);
            windowsInSizeMove.Remove(tracked.Handle);
        }
        UpdateHostViewportRenderingSuspension();
    }

    private void UntrackAllTopLevelWindowRenderingSuspension()
    {
        foreach(Window window in renderingSuspensionWindows.ToArray())
            UntrackTopLevelWindowRenderingSuspension(window);
        windowsInSizeMove.Clear();
        activeToolWindows.Clear();
        Viewport.SetHostWindowRenderingSuspended(false);
    }

    private void UpdateHostViewportRenderingSuspension() =>
        Viewport.SetHostWindowRenderingSuspended(windowsInSizeMove.Count > 0 || activeToolWindows.Count > 0);

    private void OnCameraTargetChanged(Vector3 target)
    {
        if(XCoordinate.IsKeyboardFocusWithin || YCoordinate.IsKeyboardFocusWithin || ZCoordinate.IsKeyboardFocusWithin) return;
        Vector3 worldTarget = FromPreviewRenderPosition(target);
        XCoordinate.Text = worldTarget.X.ToString("0.##");
        YCoordinate.Text = worldTarget.Y.ToString("0.##");
        ZCoordinate.Text = worldTarget.Z.ToString("0.##");
    }

    private void OnFrameRateUpdated(float framesPerSecond)
    {
        if(isClosing) return;
        int rounded = Math.Max(0, (int)Math.Round(framesPerSecond));
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                if(!isClosing) FpsText.Text = $"{rounded:N0} FPS";
            });
    }

    private void OnNavigationTargetChanged(Vector3 target)
    {
        if(mountCancellation is not null || activeMountTask is not null) return;
        Vector3 worldTarget = FromPreviewRenderPosition(target);
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(!CanStreamNavigationPreview(world))
        {
            StopNavigationPreviewScheduling();
            return;
        }
        Vector3 prefetchTarget = worldTarget;
        if(activeNavigationPreviewTask is null && navigationPreviewCoverage is MinecraftChunkBounds coverage &&
           WorldPreviewNavigation.ContainsInterior(coverage, prefetchTarget, 0))
        {
            navigationPreviewDebouncer.Clear();
            navigationPreviewTimer.Stop();
            return;
        }

        navigationPreviewDebouncer.Request(prefetchTarget, navigationPreviewClock.Elapsed);
        if(!navigationPreviewTimer.IsEnabled) navigationPreviewTimer.Start();
    }

    private void OnNavigationModeChanged(ViewportNavigationMode mode)
    {
        OrbitPreviewModeChoice.IsChecked = mode == ViewportNavigationMode.Orbit;
        ObserverPreviewModeChoice.IsChecked = mode == ViewportNavigationMode.Observer;
        SynchronizeViewControlSlider(mode);
    }

    private void OnZoomTargetDistanceChanged(float distance)
    {
        if(Viewport.NavigationMode == ViewportNavigationMode.Orbit) SynchronizeZoomSlider(distance);
    }

    private void NavigationPreviewTimer_Tick(object? sender, EventArgs e)
    {
        if(mountCancellation is not null || activeMountTask is not null || isNavigationJumping ||
           isChangingMaterialMode) return;
        if(activeNavigationPreviewTask is not null || activeWorldPreviewTask is not null) return;
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(!CanStreamNavigationPreview(world))
        {
            StopNavigationPreviewScheduling();
            return;
        }

        if(navigationPreviewDebouncer.PendingTarget is Vector3 pending &&
           navigationPreviewCoverage is MinecraftChunkBounds coverage &&
           WorldPreviewNavigation.ContainsInterior(coverage, pending, 0))
        {
            navigationPreviewDebouncer.Clear();
            navigationPreviewTimer.Stop();
            return;
        }
        if(!navigationPreviewDebouncer.TryTake(navigationPreviewClock.Elapsed, out Vector3 target)) return;
        if(!WorldPreviewNavigation.TryCreateBlockPosition(target, out BlockPosition focus))
        {
            navigationPreviewTimer.Stop();
            return;
        }

        navigationPreviewTimer.Stop();
        StartNavigationPreview(world!, displayedWorldDimension, focus);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if(isBlockReplacementCommitCritical || isWorldSlimmingCommitCritical || isWorldCropCommitCritical)
        {
            e.Cancel = true;
            UiText.Bind(StatusText, "Text", UiText.Text("地图重构正在原子提交或恢复，当前阶段不能中断；完成后即可关闭窗口。"));
            return;
        }
        if(debugRecordingPath is not null || debugRecordingStopping)
        {
            e.Cancel = true;
            FinishDebugRecordingAndClose();
            return;
        }
        SaveCurrentMapView();
        isClosing = true;
        CancelForegroundOperations();
        CloseMapFunctionsWindowForApplicationExit();
        CloseAiProjectSession();
        navigationPreviewTimer.Stop();
        chunkLoadReloadTimer.Stop();
        Viewport.ExitObserverMode();
        mountCancellation?.Cancel();
        worldPreviewCancellation?.Cancel();
        navigationPreviewCancellation?.Cancel();
        conversionPreviewCancellation?.Cancel();
        contentTransitionCancellation?.Cancel();
        worldExportCancellation?.Cancel();
        planCompilationCancellation?.Cancel();
        materialModeCancellation?.Cancel();
        worldSlimmingCancellation?.Cancel();
        worldCropCancellation?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        UntrackAllTopLevelWindowRenderingSuspension();
        navigationPreviewTimer.Stop();
        chunkLoadReloadTimer.Stop();
        Viewport.CameraTargetChanged -= OnCameraTargetChanged;
        Viewport.NavigationTargetChanged -= OnNavigationTargetChanged;
        Viewport.NavigationModeChanged -= OnNavigationModeChanged;
        Viewport.ZoomTargetDistanceChanged -= OnZoomTargetDistanceChanged;
        Viewport.FrameRateUpdated -= OnFrameRateUpdated;
        mountCancellation?.Cancel();
        worldPreviewCancellation?.Cancel();
        navigationPreviewCancellation?.Cancel();
        conversionPreviewCancellation?.Cancel();
        contentTransitionCancellation?.Cancel();
        worldExportCancellation?.Cancel();
        planCompilationCancellation?.Cancel();
        materialModeCancellation?.Cancel();
        worldSlimmingCancellation?.Cancel();
        worldCropCancellation?.Cancel();
        IReadOnlyMinecraftWorld? worldToDispose = mountedWorld;
        mountedWorld = null;
        _ = DisposeWorldAfterOperationsStopAsync(
            worldToDispose,
            activeWorldPreviewTask,
            activeNavigationPreviewTask,
            activeConversionWorkflowTask,
            activeConversionPreview,
            activeWorldExportTask,
            activeWorldSlimmingTask,
            activeWorldCropTask,
            activeComparisonTask);
        if(activeMountTask is Task<IReadOnlyMinecraftWorld> mountTask)
        {
            _ = mountTask.ContinueWith(
                static async task =>
                {
                    if(task.Status == TaskStatus.RanToCompletion)
                    {
                        await task.Result.DisposeAsync();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
        }

        base.OnClosed(e);
    }

    private static async Task DisposeWorldAfterOperationsStopAsync(
        IReadOnlyMinecraftWorld? world,
        Task? worldPreviewTask,
        Task? navigationPreviewTask,
        Task? conversionTask,
        IMinecraftDowngradePreview? conversionPreview,
        Task? exportTask,
        Task? worldSlimmingTask,
        Task? worldCropTask,
        Task? comparisonTask)
    {
        if(comparisonTask is not null)
        {
            try { await comparisonTask.ConfigureAwait(false); }
            catch { }
        }
        if(worldSlimmingTask is not null)
        {
            try
            {
                await worldSlimmingTask.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(worldCropTask is not null)
        {
            try
            {
                await worldCropTask.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(world is null) return;
        if(worldPreviewTask is not null)
        {
            try
            {
                await worldPreviewTask.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(navigationPreviewTask is not null)
        {
            try
            {
                await navigationPreviewTask.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(conversionTask is not null)
        {
            try
            {
                await conversionTask.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(exportTask is not null)
        {
            try
            {
                await exportTask.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(conversionPreview is not null) await conversionPreview.DisposeAsync().ConfigureAwait(false);
        await world.DisposeAsync().ConfigureAwait(false);
    }

    private async void OpenWorldFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = UiText.Get("选择 Minecraft 世界文件夹"),
            Multiselect = false
        };
        FileDialogLocations.Shared.Configure(dialog, MapFileKind.WorldFolder);

        if(dialog.ShowDialog(this) != true)
        {
            return;
        }

        FileDialogLocations.Shared.RememberDirectory(MapFileKind.WorldFolder, dialog.FolderName);
        CloseAiProjectSession();
        chunkLoadReloadTimer.Stop();
        SetSourceOpenButtonsEnabled(false);
        SetCoordinateJumpButtonsEnabled(false);
        SetConversionControlsEnabled(false);
        UiText.Bind(StatusText, "Text", UiText.Text("正在准备打开世界……"));
        StatusText.ToolTip = dialog.FolderName;
        mountCancellation?.Cancel();
        mountCancellation?.Dispose();
        await CancelWorldExportAsync();
        await CancelWorldPreviewAsync();
        await CancelConversionPreviewAsync();
        CancellationTokenSource requestCancellation = new();
        mountCancellation = requestCancellation;
        int openingGeneration = BeginPreviewGeneration();
        worldValidationOwnerGeneration = openingGeneration;
        BeginWorldPreviewLoading(openingGeneration, UiText.Get("正在校验世界文件"));
        bool isMountInProgress = true;
        IProgress<MinecraftWorldMountProgress> mountProgress = new Progress<MinecraftWorldMountProgress>(value =>
        {
            if(!isClosing && isMountInProgress && ReferenceEquals(mountCancellation, requestCancellation))
            {
                StatusText.Text = FormatWorldMountProgress(value);
                UpdateWorldMountLoading(openingGeneration, value);
            }
        });
        UiText.Bind(StatusText, "Text", UiText.Text("正在扫描世界文件……"));

        IReadOnlyMinecraftWorld? newWorld = null;
        try
        {
            ReadOnlyWorldMountRequest request = new(
                dialog.FolderName,
                ConcurrentWritePolicy: ConcurrentWorldWritePolicy.RejectActiveWriter,
                IncludeCustomDimensions: true,
                CaptureSourceFingerprints: true);
            activeMountTask = Task.Run(
                async () => await new AnvilWorldMounter().MountAsync(
                    request,
                    requestCancellation.Token,
                    mountProgress),
                requestCancellation.Token);
            newWorld = await activeMountTask;
            isMountInProgress = false;
            EndWorldValidation(openingGeneration);
            requestCancellation.Token.ThrowIfCancellationRequested();
            if(isClosing || !ReferenceEquals(mountCancellation, requestCancellation))
                throw new OperationCanceledException(requestCancellation.Token);
            activeMountTask = null;
            int chunkCount = newWorld.ChunkIndex.TotalChunkCount;
            int regionCount = newWorld.Descriptor.Dimensions.Sum(
                dimension => newWorld.ChunkIndex.GetRegionCount(dimension.Id));

            IReadOnlyMinecraftWorld? previousWorld = mountedWorld;
            mountedWorld = newWorld;
            newWorld = null;
            InvalidateWorldSlimmingAnalysis();
            InvalidateWorldCropState();
            UpdateWorldSlimmingControls();
            if(previousWorld is not null)
            {
                await previousWorld.DisposeAsync();
            }

            MinecraftWorldDescriptor descriptor = mountedWorld.Descriptor;
            originalWorldResourceSelection = null;
            SetConversionYOffsetText(string.Empty);
            ClearDisplayedContent(invalidatePreviewGeneration: false);
            Viewport.ClearSections();
            UiText.Bind(StatusText, "Text", UiText.Text("世界索引完成，正在准备 Minecraft 材质……"));
            UpdateWorldPreviewLoadingIndeterminate(openingGeneration, "正在准备 Minecraft 材质");
            BeginMaterialLoading(UiText.Get("正在查找匹配的 Minecraft 材质"));
            string resourceStatus;
            try
            {
                resourceStatus = await ConfigureAutomaticWorldResourcesAsync(
                    descriptor,
                    requestCancellation.Token);
            }
            finally
            {
                EndMaterialLoading();
            }
            WorldNameText.Text = descriptor.LevelName;
            UiText.Bind(WorldVersionText, "Text", UiText.Message($"版本： {FormatWorldVersion(descriptor.Version)}"));
            WorldVersionText.ToolTip = null;
            WorldPathText.Text = descriptor.RootPath;
            WorldPathText.ToolTip = descriptor.RootPath;
            if(descriptor.SpawnLocation is BlockPosition spawn)
            {
                XCoordinate.Text = spawn.X.ToString();
                YCoordinate.Text = spawn.Y.ToString();
                ZCoordinate.Text = spawn.Z.ToString();
                Viewport.FrameAt(new Vector3(spawn.X, spawn.Y, spawn.Z), 128f);
            }
            else
            {
                XCoordinate.Text = string.Empty;
                YCoordinate.Text = string.Empty;
                ZCoordinate.Text = string.Empty;
            }

            string version = descriptor.Version.VersionName
                ?? (descriptor.Version.DataVersion is int dataVersion ? $"DataVersion {dataVersion}" : "版本未知");
            string diagnostics = descriptor.Diagnostics.Count == 0
                ? string.Empty
                : $"，{descriptor.Diagnostics.Count:N0} 条诊断";
            SavedMapView? savedView = FindMapView(descriptor.RootPath);
            if(savedView is not null && !descriptor.Dimensions.Any(d => d.Id.Value == savedView.Dimension)) savedView = null;
            MinecraftDimensionId previewDimension = savedView is null
                ? SelectPreviewDimension(descriptor) : new MinecraftDimensionId(savedView.Dimension);
            displayedWorldDimension = previewDimension;
            RestoreMapView(descriptor.RootPath, savedView);
            BlockPosition initialFocus = descriptor.SpawnLocation ?? new BlockPosition(0, 64, 0);
            if(savedView is not null && WorldPreviewNavigation.TryCreateBlockPosition(Viewport.CameraTarget, out BlockPosition rememberedFocus))
                initialFocus = rememberedFocus;
            int dimensionChunkCount = mountedWorld.ChunkIndex.GetChunkCount(previewDimension);
            UiText.Bind(StatusText, "Text", UiText.Text("材质准备完成，正在读取首屏区块……"));
            Task<InitialWorldPreview> initialPreviewTask = LoadInitialWorldPreviewAsync(
                mountedWorld,
                previewDimension,
                initialFocus,
                requestCancellation.Token,
                loadingGeneration: openingGeneration);
            activeWorldPreviewTask = initialPreviewTask;
            InitialWorldPreview preview = await initialPreviewTask;
            if(ReferenceEquals(activeWorldPreviewTask, initialPreviewTask)) activeWorldPreviewTask = null;
            if(!isClosing)
            {
                bool preserveCoordinateFocus = CoordinatesHaveKeyboardFocus();
                ShowWorldNeighborhood(preview);
                if(!preserveCoordinateFocus) Viewport.FocusNavigation();
            }
            Task<IReadOnlyList<WorldDimensionRegionGroup>> regionGroupsTask = LoadWorldRegionGroupsAsync(
                mountedWorld,
                previewDimension,
                descriptor.SpawnLocation?.Y ?? 64,
                requestCancellation.Token);
            activeWorldPreviewTask = regionGroupsTask;
            IReadOnlyList<WorldDimensionRegionGroup> regionGroups = await regionGroupsTask;
            if(ReferenceEquals(activeWorldPreviewTask, regionGroupsTask)) activeWorldPreviewTask = null;
            if(!isClosing && ReferenceEquals(descriptor, mountedWorld.Descriptor))
                ShowWorldRegionNavigation(regionGroups);
            string previewStatus = $"首屏已读取 {preview.ChunkCount:N0} 个 Chunk / {preview.SectionCount:N0} 个 Section";
            string loadingStatus = dimensionChunkCount <= CurrentMaximumLoadedChunkCount
                ? $"，正在后台补齐当前维度 {dimensionChunkCount:N0} 个 Chunk"
                : "，当前维度较大，继续按坐标载入";
            UiText.Bind(StatusText, "Text", UiText.Message($"{version} · {previewStatus}{loadingStatus}。{resourceStatus}；世界共 {regionCount:N0} 个 Region、{chunkCount:N0} 个 Chunk{diagnostics}；原存档只读。"));
            if(!isClosing && dimensionChunkCount <= CurrentMaximumLoadedChunkCount)
            {
                StartWholeWorldPreview(mountedWorld, previewDimension, dimensionChunkCount, version);
            }
        }
        catch(OperationCanceledException)
        {
            // 新的挂载请求或窗口关闭会取消本次读取。
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"无法只读挂载世界：{exception.Message}"));
        }
        finally
        {
            EndWorldPreviewLoading(openingGeneration);
            isMountInProgress = false;
            activeMountTask = null;
            if(activeWorldPreviewTask?.IsCompleted == true) activeWorldPreviewTask = null;
            if(newWorld is not null)
            {
                await newWorld.DisposeAsync();
            }

            if(ReferenceEquals(mountCancellation, requestCancellation))
            {
                mountCancellation = null;
                if(!isClosing)
                {
                    SetSourceOpenButtonsEnabled(true);
                    SetCoordinateJumpButtonsEnabled(true);
                    UpdateConversionControls();
                    PrefillConversionYOffsetIfToolIsVisible();
                }
            }
            requestCancellation.Dispose();
        }
    }

    private static string FormatWorldMountProgress(MinecraftWorldMountProgress progress) => progress.Stage switch
    {
        MinecraftWorldMountStage.DiscoveringFiles => UiText.Get("正在扫描世界文件……"),
        MinecraftWorldMountStage.FingerprintingFiles when progress.TotalItems > 0 =>
            UiText.Format($"正在校验世界文件：{progress.CompletedItems:N0} / {progress.TotalItems:N0}"),
        MinecraftWorldMountStage.FingerprintingFiles => UiText.Get("正在准备校验世界文件……"),
        MinecraftWorldMountStage.ReadingLevelMetadata => UiText.Get("正在读取 level.dat……"),
        MinecraftWorldMountStage.DiscoveringRegions => UiText.Get("正在查找 Region 文件……"),
        MinecraftWorldMountStage.IndexingRegions when progress.TotalItems > 0 =>
            UiText.Format($"正在索引 Region：{progress.CompletedItems:N0} / {progress.TotalItems:N0}"),
        MinecraftWorldMountStage.IndexingRegions => UiText.Get("正在准备索引 Region……"),
        MinecraftWorldMountStage.Completed => UiText.Get("世界索引完成，正在准备首屏……"),
        _ => UiText.Get("正在打开世界……"),
    };

    private async Task<InitialWorldPreview> LoadInitialWorldPreviewAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftDimensionId dimension,
        BlockPosition focus,
        CancellationToken cancellationToken,
        int? radiusChunks = null,
        INormalizedMinecraftChunkSource? sourceOverride = null,
        bool converted = false,
        bool preserveExistingWindow = false,
        int? loadingGeneration = null)
    {
        Dispatcher.VerifyAccess();
        int generation = loadingGeneration ?? BeginPreviewGeneration();
        DisplayedContent requestedContent = converted
            ? DisplayedContent.ConvertedWorldSections
            : DisplayedContent.WorldSections;
        if(!preserveExistingWindow)
        {
            ResetWorldSectionWindow(requestedContent);
        }
        else if(displayedContent != requestedContent || dimension != displayedWorldDimension)
        {
            throw new InvalidOperationException("只能在同一地图视图和维度中保留旧区块窗口。");
        }
        int effectiveRadius = radiusChunks ?? CurrentLoadedChunkRadius;
        MinecraftChunkBounds bounds = WorldPreviewNavigation.CreateBounds(focus, effectiveRadius);
        MinecraftChunkBounds? retainedCoverage = preserveExistingWindow ? navigationPreviewCoverage : null;
        HashSet<MinecraftChunkAddress>? retainedAddresses = preserveExistingWindow
            ? [.. displayedWorldChunkStatistics.Keys]
            : null;
        bool showLoadingOverlay = !preserveExistingWindow;
        if(showLoadingOverlay)
        {
            string preparationStage = converted ? "正在准备转换预览首屏" : "正在准备首屏";
            if(loadingGeneration is null) BeginWorldPreviewLoading(generation, preparationStage);
            else UpdateWorldPreviewLoadingIndeterminate(generation, preparationStage);
        }
        try
        {
            IReadOnlyList<MinecraftChunkAddress> candidateAddresses = WorldPreviewNavigation.CreateCenterFirstAddresses(
                dimension,
                focus,
                effectiveRadius);
            var addresses = new List<MinecraftChunkAddress>(candidateAddresses.Count);
            foreach(MinecraftChunkAddress address in candidateAddresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(retainedAddresses?.Contains(address) == true ||
                   retainedCoverage is MinecraftChunkBounds existing && existing.Contains(address)) continue;
                if(await world.ChunkIndex.FindAsync(address, cancellationToken) is not null) addresses.Add(address);
            }
            // Interactive window updates share CPU and allocation bandwidth with mesh generation.
            // Retain the user's configured concurrency for initial/full loads and conversion scans.
            int loadConcurrency = preserveExistingWindow
                ? Math.Min(CurrentChunkLoadConcurrency, Math.Clamp(Environment.ProcessorCount / 4, 1, 4))
                : CurrentChunkLoadConcurrency;
            int totalChunkCount = world.ChunkIndex.GetChunkCount(dimension);
            return await Task.Run(
                async () =>
                {
                    INormalizedMinecraftChunkSource source = sourceOverride ?? NormalizedMinecraftChunkSourceFactory.Create(world);
                    int sectionCount = 0;
                    int chunkCount = 0;
                    WorldPreviewStatistics statistics = WorldPreviewStatistics.Empty;
                    int leadingAddressCount = 0;
                    int leadingChunkCount = 0;
                    WorldPreviewStatistics leadingStatistics = WorldPreviewStatistics.Empty;

                    if(addresses.Count > 0)
                    {
                        NormalizedMinecraftChunk? centerChunk = await source.FindAsync(addresses[0], cancellationToken)
                            .ConfigureAwait(false);
                        leadingAddressCount = 1;
                        if(centerChunk is not null)
                        {
                            leadingChunkCount = 1;
                            chunkCount = 1;
                            sectionCount = centerChunk.Sections.Count;
                            leadingStatistics = WorldPreviewStatisticsAnalyzer.AnalyzeChunk(centerChunk, cancellationToken);
                            statistics = leadingStatistics;
                            await PublishBatchAsync(
                                    centerChunk.Sections,
                                    new Dictionary<MinecraftChunkAddress, WorldPreviewStatistics>
                                    {
                                        [centerChunk.Address] = leadingStatistics,
                                    },
                                    leadingAddressCount)
                                .ConfigureAwait(false);
                        }
                    }

                    await foreach(NormalizedMinecraftChunkBatch batch in NormalizedMinecraftChunkBatcher.EnumerateAddressesAsync(
                                      source,
                                      addresses.Skip(leadingAddressCount),
                                      batchSize: ProgressiveWorldPreviewBatchSize,
                                      loadConcurrency: loadConcurrency,
                                      cancellationToken: cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        chunkCount = checked(leadingChunkCount + batch.ProcessedChunkCount);
                        sectionCount = checked(sectionCount + batch.Sections.Count);
                        statistics = leadingStatistics.Merge(batch.ProcessedStatistics);
                        int processedAddressCount = checked(leadingAddressCount + batch.ProcessedChunkCount);
                        await PublishBatchAsync(batch.Sections, batch.ChunkStatistics, processedAddressCount)
                            .ConfigureAwait(false);
                    }

                    if(chunkCount == 0)
                    {
                        await Dispatcher.InvokeAsync(
                            () =>
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if(!IsPreviewGenerationCurrent(generation) || isClosing ||
                                   !ReferenceEquals(world, mountedWorld) || dimension != displayedWorldDimension ||
                                   (converted && !ReferenceEquals(source, activeConversionPreview?.ConvertedChunks)))
                                    throw new OperationCanceledException(cancellationToken);
                                ShowWorldSceneStatistics(
                                    preserveExistingWindow
                                        ? SnapshotDisplayedWorldStatistics()
                                        : WorldPreviewStatistics.Empty,
                                    totalChunkCount,
                                    converted);
                            },
                            DispatcherPriority.Normal,
                            cancellationToken);
                    }

                    return new InitialWorldPreview(chunkCount, sectionCount, bounds, statistics);

                    async Task PublishBatchAsync(
                        IReadOnlyList<NormalizedMinecraftSection> sections,
                        IReadOnlyDictionary<MinecraftChunkAddress, WorldPreviewStatistics> chunkStatistics,
                        int processedAddressCount)
                    {
                        await Viewport.ReplaceSectionsAsync(sections, cancellationToken).ConfigureAwait(false);
                        await Dispatcher.InvokeAsync(
                            () =>
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if(!IsPreviewGenerationCurrent(generation) || isClosing ||
                                   !ReferenceEquals(world, mountedWorld) || dimension != displayedWorldDimension ||
                                   (converted && !ReferenceEquals(source, activeConversionPreview?.ConvertedChunks)))
                                    throw new OperationCanceledException(cancellationToken);
                                ReplaceWorldSections(sections);
                                ReplaceWorldChunkStatistics(chunkStatistics);
                                ShowWorldSceneStatistics(
                                    preserveExistingWindow ? SnapshotDisplayedWorldStatistics() : statistics,
                                    totalChunkCount,
                                    converted);
                                if(showLoadingOverlay)
                                {
                                    UpdateWorldPreviewLoading(
                                        generation,
                                        converted ? UiText.Get("正在读取转换预览区块") : UiText.Get("正在读取首屏区块"),
                                        processedAddressCount,
                                        addresses.Count);
                                }
                                UiText.Bind(StatusText, "Text", converted ? UiText.Message($"正在逐步载入转换预览：{chunkCount:N0} / 最多 {CurrentMaximumLoadedChunkCount:N0} 个 Chunk，已显示 {displayedWorldSections.Count:N0} 个 Section。") : UiText.Message($"正在逐步载入：{chunkCount:N0} / 最多 {CurrentMaximumLoadedChunkCount:N0} 个 Chunk，已显示 {displayedWorldSections.Count:N0} 个 Section；原存档只读。"));
                            },
                            preserveExistingWindow ? DispatcherPriority.Background : DispatcherPriority.Normal,
                            cancellationToken);
                    }
                },
                cancellationToken);
        }
        finally
        {
            if(showLoadingOverlay) EndWorldPreviewLoading(generation);
        }
    }

    private static MinecraftDimensionId SelectPreviewDimension(MinecraftWorldDescriptor descriptor)
    {
        MinecraftDimensionDescriptor? overworld = descriptor.Dimensions.FirstOrDefault(
            static dimension => dimension.Id == MinecraftDimensionId.Overworld);
        return overworld?.Id ?? descriptor.Dimensions.FirstOrDefault()?.Id ?? MinecraftDimensionId.Overworld;
    }

    private static async Task<IReadOnlyList<MinecraftChunkAddress>> CollectChunkAddressesAsync(
        IMinecraftChunkIndex index,
        MinecraftDimensionId dimension,
        CancellationToken cancellationToken)
    {
        var addresses = new List<MinecraftChunkAddress>(index.GetChunkCount(dimension));
        await foreach(MinecraftChunkIndexEntry entry in index
                           .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            addresses.Add(entry.Address);
        }
        return addresses;
    }

    private static async Task<IReadOnlyList<MinecraftChunkAddress>> CollectAllChunkAddressesAsync(
        IMinecraftChunkIndex index,
        IEnumerable<MinecraftDimensionId> dimensions,
        CancellationToken cancellationToken)
    {
        var addresses = new List<MinecraftChunkAddress>(index.TotalChunkCount);
        foreach(MinecraftDimensionId dimension in dimensions.Distinct())
        {
            await foreach(MinecraftChunkIndexEntry entry in index
                               .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                addresses.Add(entry.Address);
            }
        }
        return addresses;
    }

    private static Task<IReadOnlyList<WorldDimensionRegionGroup>> LoadWorldRegionGroupsAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftDimensionId initiallyExpandedDimension,
        int preferredY,
        CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            IReadOnlyList<WorldDimensionRegionSummary> summaries =
                WorldRegionNavigation.DescribeDimensions(world.Descriptor, world.ChunkIndex);
            var groups = new List<WorldDimensionRegionGroup>(summaries.Count);
            foreach(WorldDimensionRegionSummary summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var regions = new List<WorldRegionNavigationItem>(summary.RegionCount);
                await foreach(WorldRegionNavigationItem region in WorldRegionNavigation.EnumerateRegionsAsync(
                                  world.ChunkIndex,
                                  summary.Dimension,
                                  preferredY,
                                  cancellationToken))
                {
                    regions.Add(region);
                }

                groups.Add(new WorldDimensionRegionGroup(
                    summary,
                    WorldRegionNavigation.OrderByFileSizeDescending(regions),
                    summary.Dimension == initiallyExpandedDimension));
            }
            return (IReadOnlyList<WorldDimensionRegionGroup>)groups;
        },
        cancellationToken);

    private void ShowWorldRegionNavigation(IReadOnlyList<WorldDimensionRegionGroup> groups)
    {
        RegionDimensionsList.ItemsSource = groups;
        RegionFilesSection.Visibility = groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearWorldRegionNavigation()
    {
        RegionDimensionsList.ItemsSource = null;
        RegionFilesSection.Visibility = Visibility.Collapsed;
    }

    private static string FormatWorldVersion(MinecraftVersionDescriptor version)
    {
        return string.IsNullOrWhiteSpace(version.VersionName)
            ? UiText.Get("Minecraft 版本未知")
            : $"Minecraft {version.VersionName}";
    }

    private async void OpenSchematic_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = UiText.Get("导入 Legacy .schematic"),
            Filter = UiText.Get("Minecraft 结构 (*.schematic)|*.schematic"),
            Multiselect = false,
            CheckFileExists = true,
        };
        FileDialogLocations.Shared.Configure(dialog, MapFileKind.Schematic);
        if(dialog.ShowDialog(this) != true) return;
        FileDialogLocations.Shared.RememberFile(MapFileKind.Schematic, dialog.FileName);

        using CancellationTokenSource cancellation = new();
        schematicCancellation = cancellation;
        CloseAiProjectSession();
        chunkLoadReloadTimer.Stop();
        SetSourceOpenButtonsEnabled(false);
        SetCoordinateJumpButtonsEnabled(false);
        UiText.Bind(StatusText, "Text", UiText.Message($"正在读取 {Path.GetFileName(dialog.FileName)}……"));
        try
        {
            LegacySchematicImport imported = await Task.Run(() => LegacySchematicImporter.Import(dialog.FileName, cancellationToken: cancellation.Token));
            if(isClosing) return;

            await CancelWorldExportAsync();
            await CancelWorldPreviewAsync();
            await CancelConversionPreviewAsync();
            if(isClosing) return;
            IReadOnlyMinecraftWorld? previousWorld = mountedWorld;
            mountedWorld = null;
            InvalidateWorldSlimmingAnalysis();
            InvalidateWorldCropState();
            if(previousWorld is not null) await previousWorld.DisposeAsync();

            ClearWorldRegionNavigation();
            ClearDisplayedContent();
            Viewport.ClearSections();
            BeginMaterialLoading(UiText.Get("正在查找 Minecraft 1.12.2 材质"));
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            string resourceStatus;
            try
            {
                resourceStatus = await ConfigureSchematicResourcesAsync(
                    imported.SourcePath,
                    cancellation.Token);
                IProgress<SectionMeshBuildProgress> progress = CreateMaterialSectionProgress();
                await Viewport.ReplaceSectionsAsync(imported.Sections, cancellation.Token, progress);
            }
            finally
            {
                EndMaterialLoading();
            }
            foreach(NormalizedMinecraftSection section in imported.Sections)
                displayedWorldSections[section.Coordinate] = section;
            displayedSchematic = imported;
            displayedContent = DisplayedContent.SchematicSections;

            string title = Path.GetFileNameWithoutExtension(imported.SourcePath);
            WorldNameText.Text = title;
            UiText.Bind(WorldVersionText, "Text", UiText.Text("版本： Minecraft Java 1.12.2 · Legacy schematic"));
            WorldVersionText.ToolTip = null;
            WorldPathText.Text = imported.SourcePath;
            WorldPathText.ToolTip = imported.SourcePath;
            float centerX = imported.Origin.X + (imported.Width - 1) * 0.5f;
            float centerY = imported.Origin.Y + (imported.Height - 1) * 0.5f;
            float centerZ = imported.Origin.Z + (imported.Length - 1) * 0.5f;
            float span = Math.Max(imported.Width, Math.Max(imported.Height, imported.Length));
            var center = new Vector3(centerX, centerY, centerZ);
            Viewport.FrameAt(center, Math.Clamp(span * 1.8f, 18f, 512f));
            XCoordinate.Text = centerX.ToString("0.##");
            YCoordinate.Text = centerY.ToString("0.##");
            ZCoordinate.Text = centerZ.ToString("0.##");
            RestoreMapView(imported.SourcePath, FindMapView(imported.SourcePath));
            Viewport.FocusNavigation();
            ShowSceneStatistics(
                source: ".schematic",
                dataVersion: "1.12.2 Legacy",
                countLabel: "方块数量",
                count: imported.NonAirBlockCount.ToString("N0"),
                size: $"{imported.Width:N0} × {imported.Height:N0} × {imported.Length:N0}",
                chunks: $"{CountChunks(imported.Sections):N0} / {CountChunks(imported.Sections):N0}",
                blockEntities: imported.BlockEntityCount.ToString("N0"),
                origin: $"{imported.Origin.X:N0}, {imported.Origin.Y:N0}, {imported.Origin.Z:N0}");
            string diagnostics = imported.Diagnostics.Count == 0
                ? string.Empty
                : $"，{imported.Diagnostics.Count:N0} 条诊断";
            UiText.Bind(StatusText, "Text", UiText.Message($"已导入 {imported.NonAirBlockCount:N0} 个方块 / {imported.Sections.Count:N0} 个 Section{diagnostics}。{resourceStatus}。"));
            UpdateConversionControls();
        }
        catch(OperationCanceledException) { if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Text("结构导入已取消。")); }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                           NotSupportedException or ArgumentException or OverflowException)
        {
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Message($"无法导入 .schematic：{exception.Message}"));
        }
        finally
        {
            if(ReferenceEquals(schematicCancellation, cancellation)) schematicCancellation = null;
            if(!isClosing)
            {
                SetSourceOpenButtonsEnabled(true);
                SetCoordinateJumpButtonsEnabled(true);
            }
        }
    }

    private void StartWholeWorldPreview(
        IReadOnlyMinecraftWorld world,
        MinecraftDimensionId dimension,
        int totalChunkCount,
        string version)
    {
        if(isClosing || activeWorldPreviewTask is not null || activeNavigationPreviewTask is not null) return;
        int generation = BeginPreviewGeneration();
        CancellationTokenSource requestCancellation = new();
        worldPreviewCancellation = requestCancellation;
        Task<WholeWorldPreviewResult> task = StreamWholeWorldPreviewAsync(
            world,
            dimension,
            totalChunkCount,
            generation,
            requestCancellation.Token);
        activeWorldPreviewTask = task;
        UpdateConversionControls();
        _ = CompleteWholeWorldPreviewAsync(world, dimension, task, requestCancellation, version, generation);
    }

    private Task<WholeWorldPreviewResult> StreamWholeWorldPreviewAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftDimensionId dimension,
        int totalChunkCount,
        int generation,
        CancellationToken cancellationToken)
    {
        int loadConcurrency = CurrentChunkLoadConcurrency;
        return Task.Run(
            async () =>
            {
                INormalizedMinecraftChunkSource source = NormalizedMinecraftChunkSourceFactory.Create(world);
                IReadOnlyList<MinecraftChunkAddress> addresses = await CollectChunkAddressesAsync(
                    world.ChunkIndex,
                    dimension,
                    cancellationToken).ConfigureAwait(false);
                int processedChunkCount = 0;
                WorldPreviewStatistics statistics = WorldPreviewStatistics.Empty;
                await foreach(NormalizedMinecraftChunkBatch batch in NormalizedMinecraftChunkBatcher.EnumerateAddressesAsync(
                                  source,
                                  addresses,
                                  batchSize: ProgressiveWorldPreviewBatchSize,
                                  loadConcurrency: loadConcurrency,
                                  cancellationToken: cancellationToken))
                {
                    processedChunkCount = batch.ProcessedChunkCount;
                    statistics = batch.ProcessedStatistics;
                    await Viewport.ReplaceSectionsAsync(batch.Sections, cancellationToken).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(
                        () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if(!IsPreviewGenerationCurrent(generation) ||
                               !ReferenceEquals(world, mountedWorld) || dimension != displayedWorldDimension ||
                               displayedContent != DisplayedContent.WorldSections)
                                throw new OperationCanceledException(cancellationToken);
                            ReplaceWorldSections(batch.Sections);
                            ReplaceWorldChunkStatistics(batch.ChunkStatistics);
                            ShowWorldSceneStatistics(batch.ProcessedStatistics, totalChunkCount, converted: false);
                            UiText.Bind(StatusText, "Text", UiText.Message($"正在载入完整世界：{batch.ProcessedChunkCount:N0} / {totalChunkCount:N0} 个 Chunk，已显示 {displayedWorldSections.Count:N0} 个 Section；原存档只读。"));
                        },
                        System.Windows.Threading.DispatcherPriority.Normal,
                        cancellationToken);
                }

                return new WholeWorldPreviewResult(processedChunkCount, statistics);
            },
            cancellationToken);
    }

    private async Task CompleteWholeWorldPreviewAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftDimensionId dimension,
        Task<WholeWorldPreviewResult> task,
        CancellationTokenSource requestCancellation,
        string version,
        int generation)
    {
        try
        {
            WholeWorldPreviewResult result = await task;
            if(IsPreviewGenerationCurrent(generation) && !isClosing &&
               ReferenceEquals(world, mountedWorld) && dimension == displayedWorldDimension &&
               displayedContent == DisplayedContent.WorldSections)
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"{version} · 当前维度已完整载入 {result.ChunkCount:N0} 个 Chunk / {displayedWorldSections.Count:N0} 个 Section；原存档只读。"));
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception exception)
        {
            if(IsPreviewGenerationCurrent(generation) && !isClosing &&
               ReferenceEquals(world, mountedWorld) && dimension == displayedWorldDimension)
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"完整世界载入在已显示内容处停止：{exception.Message}"));
            }
        }
        finally
        {
            if(ReferenceEquals(activeWorldPreviewTask, task)) activeWorldPreviewTask = null;
            if(ReferenceEquals(worldPreviewCancellation, requestCancellation))
            {
                worldPreviewCancellation = null;
                requestCancellation.Dispose();
            }
            UpdateConversionControls();
        }
    }

    private bool CanStreamNavigationPreview(IReadOnlyMinecraftWorld? world) =>
        world is not null && sourceActionsEnabled && !isClosing && !isChangingMaterialMode &&
        (displayedContent == DisplayedContent.WorldSections ||
         displayedContent == DisplayedContent.ConvertedWorldSections && activeConversionPreview is not null) &&
        world.ChunkIndex.GetChunkCount(displayedWorldDimension) > CurrentMaximumLoadedChunkCount;

    private void StartNavigationPreview(
        IReadOnlyMinecraftWorld world,
        MinecraftDimensionId dimension,
        BlockPosition focus)
    {
        if(activeNavigationPreviewTask is not null || activeWorldPreviewTask is not null ||
           mountCancellation is not null || activeMountTask is not null || !CanStreamNavigationPreview(world) ||
           dimension != displayedWorldDimension) return;
        CancellationTokenSource requestCancellation = new();
        navigationPreviewCancellation = requestCancellation;
        activeNavigationPreviewTask = RunNavigationPreviewAsync(world, dimension, focus, requestCancellation);
    }

    private async Task RunNavigationPreviewAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftDimensionId dimension,
        BlockPosition focus,
        CancellationTokenSource requestCancellation)
    {
        try
        {
            DisplayedContent requestedContent = displayedContent;
            IMinecraftDowngradePreview? conversion = requestedContent == DisplayedContent.ConvertedWorldSections
                ? activeConversionPreview
                : null;
            if(requestedContent == DisplayedContent.ConvertedWorldSections && conversion is null) return;
            InitialWorldPreview preview = await LoadInitialWorldPreviewAsync(
                world,
                dimension,
                focus,
                requestCancellation.Token,
                sourceOverride: conversion?.ConvertedChunks,
                converted: conversion is not null,
                preserveExistingWindow: true);
            requestCancellation.Token.ThrowIfCancellationRequested();
            if(isClosing || isChangingMaterialMode || !ReferenceEquals(world, mountedWorld) ||
               dimension != displayedWorldDimension ||
               displayedContent != requestedContent ||
               (conversion is not null && !ReferenceEquals(conversion, activeConversionPreview)))
                return;

            Vector3 currentTarget = FromPreviewRenderPosition(Viewport.CameraTarget);
            if(!WorldPreviewNavigation.ContainsInterior(preview.Bounds, currentTarget, 0))
            {
                navigationPreviewDebouncer.Request(
                    currentTarget,
                    navigationPreviewClock.Elapsed);
                return;
            }

            PruneWorldSectionWindow(preview.Bounds, requestedContent, dimension);
            ShowWorldNeighborhood(preview);
            UiText.Bind(StatusText, "Text", conversion is null ? UiText.Message($"已按移动位置载入 {preview.ChunkCount:N0} 个 Chunk / {preview.SectionCount:N0} 个 Section；原存档只读。") : UiText.Message($"已按移动位置载入转换预览 {preview.ChunkCount:N0} 个 Chunk / {preview.SectionCount:N0} 个 Section。"));
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception exception)
        {
            if(!isClosing && ReferenceEquals(world, mountedWorld))
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"移动位置周围区块载入失败：{exception.Message}"));
            }
        }
        finally
        {
            if(ReferenceEquals(navigationPreviewCancellation, requestCancellation))
            {
                navigationPreviewCancellation = null;
                activeNavigationPreviewTask = null;
                requestCancellation.Dispose();
            }
            ReconcileNavigationPreviewScheduling();
        }
    }

    private void ReconcileNavigationPreviewScheduling()
    {
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(!CanStreamNavigationPreview(world))
        {
            StopNavigationPreviewScheduling();
            return;
        }
        if(navigationPreviewDebouncer.PendingTarget is not Vector3 pending)
        {
            navigationPreviewTimer.Stop();
            return;
        }
        if(navigationPreviewCoverage is MinecraftChunkBounds coverage &&
           WorldPreviewNavigation.ContainsInterior(coverage, pending, 0))
        {
            navigationPreviewDebouncer.Clear();
            navigationPreviewTimer.Stop();
            return;
        }
        if(!navigationPreviewTimer.IsEnabled) navigationPreviewTimer.Start();
    }

    private async Task CancelNavigationPreviewAsync()
    {
        navigationPreviewTimer.Stop();
        navigationPreviewDebouncer.Clear();
        navigationPreviewCoverage = null;

        CancellationTokenSource? cancellation = navigationPreviewCancellation;
        Task? task = activeNavigationPreviewTask;
        cancellation?.Cancel();
        if(task is not null)
        {
            try
            {
                await task;
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(ReferenceEquals(activeNavigationPreviewTask, task)) activeNavigationPreviewTask = null;
        if(ReferenceEquals(navigationPreviewCancellation, cancellation))
        {
            navigationPreviewCancellation = null;
            cancellation?.Dispose();
        }
    }

    private void StopNavigationPreviewScheduling()
    {
        navigationPreviewTimer.Stop();
        navigationPreviewDebouncer.Clear();
        navigationPreviewCoverage = null;

    }



    private async Task CancelWorldPreviewAsync()
    {
        await CancelNavigationPreviewAsync();
        CancellationTokenSource? cancellation = worldPreviewCancellation;
        Task? task = activeWorldPreviewTask;
        cancellation?.Cancel();
        if(task is not null)
        {
            try
            {
                await task;
            }
            catch(OperationCanceledException)
            {
            }
            catch(Exception)
            {
            }
        }

        if(ReferenceEquals(activeWorldPreviewTask, task)) activeWorldPreviewTask = null;
        if(ReferenceEquals(worldPreviewCancellation, cancellation))
        {
            worldPreviewCancellation = null;
            cancellation?.Dispose();
        }
    }

    private async void OpenPlan_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = UiText.Get("打开蓝图"),
            Filter = UiText.Get("筑界镜蓝图 (*.zz)|*.zz"),
            Multiselect = false
        };
        FileDialogLocations.Shared.Configure(dialog, MapFileKind.Blueprint);

        if(dialog.ShowDialog(this) != true)
        {
            return;
        }

        FileDialogLocations.Shared.RememberFile(MapFileKind.Blueprint, dialog.FileName);
        await LoadPlanFileAsync(dialog.FileName);
    }

    private async Task LoadPlanFileAsync(string planFilePath, CancellationToken cancellationToken = default, bool propagateErrors = false)
    {
        CloseAiProjectSession();
        chunkLoadReloadTimer.Stop();
        await CancelWorldExportAsync();
        await CancelWorldPreviewAsync();
        await CancelConversionPreviewAsync();
        planCompilationCancellation?.Cancel();
        planCompilationCancellation?.Dispose();
        CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        planCompilationCancellation = requestCancellation;
        SetSourceOpenButtonsEnabled(false);
        SetCoordinateJumpButtonsEnabled(false);

        try
        {
            UiText.Bind(StatusText, "Text", UiText.Text("正在读取和验证蓝图…"));
            var parsed = await Task.Run(async () =>
            {
                FileInfo info = new(planFilePath);
                if(info.Length > 32L * 1024 * 1024) throw new InvalidDataException("蓝图超过 32 MiB，请拆分成独立组件或增量批次。");
                string json = await File.ReadAllTextAsync(planFilePath, requestCancellation.Token);
                ZjjPlan value = ZjjPlanJson.Deserialize(json);
                return (Plan: value, Validation: ZjjPlanValidator.Validate(value));
            }, requestCancellation.Token);
            ZjjPlan plan = parsed.Plan;
            PlanValidationResult validation = parsed.Validation;
            if(!validation.IsValid)
            {
                ValidationIssue issue = validation.Issues[0];
                UiText.Bind(StatusText, "Text", UiText.Message($"蓝图未通过检查：{issue.Path} · {issue.Message}"));
                if(propagateErrors) throw new InvalidDataException(StatusText.Text);
                return;
            }

            if(plan.Base is not null)
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"蓝图 {plan.PlanId} 已通过检查；挂载基础场景 {plan.Base.Scene} 后即可生成预览。"));
                return;
            }

            UiText.Bind(StatusText, "Text", UiText.Message($"正在后台编译蓝图 {plan.PlanId}……"));
            PlanPreviewLoadResult preview = await Task.Run(
                () => CompilePlanPreviewAsync(plan, requestCancellation.Token),
                requestCancellation.Token);
            requestCancellation.Token.ThrowIfCancellationRequested();
            if(isClosing || !ReferenceEquals(planCompilationCancellation, requestCancellation)) return;

            IReadOnlyMinecraftWorld? previousWorld = mountedWorld;
            mountedWorld = null;
            InvalidateWorldSlimmingAnalysis();
            InvalidateWorldCropState();
            if(previousWorld is not null) await previousWorld.DisposeAsync();
            ClearWorldRegionNavigation();
            ClearDisplayedContent();
            Viewport.ClearSections();
            BeginMaterialLoading(UiText.Get("正在匹配蓝图的 Minecraft 材质"));
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            string resourceStatus;
            try
            {
                resourceStatus = await ConfigurePlanResourcesAsync(
                    planFilePath,
                    preview.VisualVersion,
                    preview.VisualProfileInferred,
                    requestCancellation.Token);
                IProgress<SectionMeshBuildProgress> progress = CreateMaterialSectionProgress(() =>
                    !isClosing && ReferenceEquals(planCompilationCancellation, requestCancellation));
                await Viewport.ReplaceSectionsAsync(preview.Sections, requestCancellation.Token, progress);
            }
            finally
            {
                EndMaterialLoading();
            }
            requestCancellation.Token.ThrowIfCancellationRequested();
            if(isClosing || !ReferenceEquals(planCompilationCancellation, requestCancellation)) return;

            displayedPlanPreview = preview.Compilation.Delta;
            displayedPlanSections = preview.Sections;
            displayedPlanDimension = new MinecraftDimensionId(plan.Dimension);
            displayedPlanSourcePath = Path.GetFullPath(planFilePath);
            displayedPlanSpawnPoint = plan.SpawnPoint;
            displayedPlanVisualProfile = preview.VisualProfile;
            displayedPlanVisualVersion = preview.VisualVersion;
            displayedPlanVisualProfileInferred = preview.VisualProfileInferred;
            displayedSchematic = null;
            displayedWorldSections.Clear();
            displayedWorldChunkStatistics.Clear();
            displayedContent = DisplayedContent.PlanPreview;
            UpdateRenderInfoText();
            WorldNameText.Text = plan.PlanId;
            UiText.Bind(WorldVersionText, "Text", UiText.Message($"版本： {FormatPlanMinecraftVersion(preview.VisualVersion)}"));
            WorldVersionText.ToolTip = FormatPlanVisualProfile(preview.VisualVersion, preview.VisualProfileInferred);
            PlanFormatCard.Visibility = Visibility.Visible;
            WorldPathText.Text = planFilePath;
            WorldPathText.ToolTip = planFilePath;
            ShowPlanSceneStatistics(preview.Compilation.Delta, FormatPlanDataVersion(preview.VisualVersion));
            UpdateConversionControls();
            UiText.Bind(StatusText, "Text", UiText.Message($"蓝图 {plan.PlanId} 已编译：{preview.Compilation.Delta.ChangedVoxelCount:N0} 个方块，分布于 {preview.Compilation.Delta.Sections.Count:N0} 个 Section。{resourceStatus}。"));

            BoxSelection? firstSelection = plan.Selections.Values.Cast<BoxSelection?>().FirstOrDefault();
            if(firstSelection is BoxSelection selection)
            {
                BlockPosition center = new(
                    (selection.Min.X + selection.MaxExclusive.X) / 2,
                    (selection.Min.Y + selection.MaxExclusive.Y) / 2,
                    (selection.Min.Z + selection.MaxExclusive.Z) / 2);
                float selectionSpan = Math.Max(
                    selection.MaxExclusive.X - selection.Min.X,
                    Math.Max(
                        selection.MaxExclusive.Y - selection.Min.Y,
                        selection.MaxExclusive.Z - selection.Min.Z));
                Viewport.FrameAt(new Vector3(center.X, center.Y, center.Z), Math.Clamp(selectionSpan * 1.8f, 18f, 256f));
                XCoordinate.Text = center.X.ToString();
                YCoordinate.Text = center.Y.ToString();
                ZCoordinate.Text = center.Z.ToString();
            }
            RestoreMapView(planFilePath, FindMapView(planFilePath));
        }
        catch(OperationCanceledException)
        {
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Text("蓝图预览已取消。"));
            if(propagateErrors) throw;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or PlanValidationException or PlanCompilationException or ArgumentException)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"无法打开蓝图：{exception.Message}"));
            if(propagateErrors) throw;
        }
        finally
        {
            if(ReferenceEquals(planCompilationCancellation, requestCancellation))
            {
                planCompilationCancellation = null;
                if(!isClosing)
                {
                    SetSourceOpenButtonsEnabled(true);
                    SetCoordinateJumpButtonsEnabled(true);
                }
            }
            requestCancellation.Dispose();
        }
    }

    private static async Task<PlanPreviewLoadResult> CompilePlanPreviewAsync(
        ZjjPlan plan,
        CancellationToken cancellationToken)
    {
        CompilationResult compilation = new ZjjPlanCompiler().Compile(
            plan,
            new EmptySceneSnapshot(),
            cancellationToken);
        PlanVisualProfileSelection visual = SelectPlanVisualProfile(plan);
        MinecraftDimensionId dimension = new(plan.Dimension);
        NormalizedMinecraftSceneChunkSource source = NormalizedMinecraftSceneChunkSource.FromSceneDelta(
            compilation.Delta,
            visual.Version,
            dimension);
        var sections = new List<NormalizedMinecraftSection>(compilation.Delta.Sections.Count);
        await foreach(NormalizedMinecraftChunk chunk in source
                          .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                          .ConfigureAwait(false))
        {
            sections.AddRange(chunk.Sections);
        }
        return new PlanPreviewLoadResult(
            compilation,
            sections,
            visual.Profile,
            visual.Version,
            visual.Inferred);
    }

    private static PlanVisualProfileSelection SelectPlanVisualProfile(ZjjPlan plan)
    {
        if(plan.VisualProfile is ZjjVisualProfile explicitProfile)
        {
            MinecraftStorageFamily family = explicitProfile.StorageFamily switch
            {
                ZjjVisualProfile.LegacyNumericAnvilStorageFamily => MinecraftStorageFamily.LegacyNumericAnvil,
                ZjjVisualProfile.FlattenedPaletteStorageFamily => MinecraftStorageFamily.FlattenedPalette,
                ZjjVisualProfile.ModernSectionPaletteStorageFamily => MinecraftStorageFamily.ModernSectionPalette,
                _ => IsLegacyVersion(explicitProfile.VersionName, explicitProfile.DataVersion)
                    ? MinecraftStorageFamily.LegacyNumericAnvil
                    : MinecraftStorageFamily.ModernSectionPalette,
            };
            return new PlanVisualProfileSelection(
                explicitProfile,
                new MinecraftVersionDescriptor(
                    explicitProfile.DataVersion,
                    explicitProfile.VersionName,
                    family),
                false);
        }

        bool modern = plan.Materials.Values
            .SelectMany(static material => material.ExactState is BlockState exact
                ? new[] { exact }.Concat(material.Candidates)
                : material.Candidates)
            .Any(IsModernPlanState);
        MinecraftVersionDescriptor inferredVersion = modern
            ? new MinecraftVersionDescriptor(3463, "1.20", MinecraftStorageFamily.ModernSectionPalette)
            : MinecraftTargetProfile.Java1122.Version;
        return new PlanVisualProfileSelection(
            CreatePlanVisualProfile(inferredVersion),
            inferredVersion,
            true);
    }

    private static bool IsLegacyVersion(string? versionName, int? dataVersion) =>
        dataVersion == 1343 || string.Equals(versionName?.Trim(), "1.12.2", StringComparison.OrdinalIgnoreCase);

    private static bool IsModernPlanState(BlockState state)
    {
        if(state.Properties.ContainsKey("waterlogged")) return true;
        string name = state.Name.StartsWith("minecraft:", StringComparison.Ordinal)
            ? state.Name["minecraft:".Length..]
            : state.Name;
        return name is "light" or "chain" or "conduit" or "decorated_pot" or "campfire" or "lantern" or
                   "barrel" or "smoker" or "blast_furnace" or "grindstone" or "stonecutter" or "loom" or
                   "cartography_table" or "smithing_table" or "fletching_table" or "target" or "lodestone" or
                   "respawn_anchor" or "ancient_debris" or "bee_nest" or "beehive" or "honey_block" or
                   "honeycomb_block" or "powder_snow" or "spore_blossom" or "reinforced_deepslate" ||
               name.EndsWith("_hanging_sign", StringComparison.Ordinal) ||
               name.Contains("cherry", StringComparison.Ordinal) ||
               name.Contains("mangrove", StringComparison.Ordinal) ||
               name.Contains("deepslate", StringComparison.Ordinal) ||
               name.Contains("sculk", StringComparison.Ordinal) ||
               name.Contains("copper", StringComparison.Ordinal) ||
               name.Contains("amethyst", StringComparison.Ordinal) ||
               name.Contains("dripstone", StringComparison.Ordinal) ||
               name.Contains("froglight", StringComparison.Ordinal) ||
               name.StartsWith("warped_", StringComparison.Ordinal) ||
               name.StartsWith("crimson_", StringComparison.Ordinal);
    }

    private static ZjjVisualProfile CreatePlanVisualProfile(MinecraftVersionDescriptor version) => new()
    {
        Edition = ZjjVisualProfile.JavaEdition,
        VersionName = version.VersionName,
        DataVersion = version.DataVersion,
        StorageFamily = version.StorageFamily switch
        {
            MinecraftStorageFamily.LegacyNumericAnvil => ZjjVisualProfile.LegacyNumericAnvilStorageFamily,
            MinecraftStorageFamily.FlattenedPalette => ZjjVisualProfile.FlattenedPaletteStorageFamily,
            MinecraftStorageFamily.ModernSectionPalette => ZjjVisualProfile.ModernSectionPaletteStorageFamily,
            _ => ZjjVisualProfile.UnknownStorageFamily,
        },
    };

    private static string FormatPlanMinecraftVersion(MinecraftVersionDescriptor version) =>
        string.IsNullOrWhiteSpace(version.VersionName)
            ? version.DataVersion is int dataVersion ? $"Minecraft DataVersion {dataVersion}" : UiText.Get("Minecraft 兼容材质")
            : $"Minecraft {version.VersionName}";

    private static string FormatPlanDataVersion(MinecraftVersionDescriptor version) =>
        version.DataVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";

    private static string FormatPlanVisualProfile(MinecraftVersionDescriptor version, bool inferred)
    {
        string label = FormatPlanMinecraftVersion(version);
        return inferred ? UiText.Format($"{label} 材质预览（自动推断）") : UiText.Format($"{label} 材质预览");
    }

    private async void JumpToCoordinate_Click(object sender, RoutedEventArgs e)
    {
        if(!WorldPreviewNavigation.TryParseCoordinate(XCoordinate.Text, out float x) ||
           !WorldPreviewNavigation.TryParseCoordinate(YCoordinate.Text, out float y) ||
           !WorldPreviewNavigation.TryParseCoordinate(ZCoordinate.Text, out float z))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("坐标必须是有限且可表示的有效数字。"));
            return;
        }

        var target = new Vector3(x, y, z);
        if(!WorldPreviewNavigation.TryCreateBlockPosition(target, out BlockPosition blockFocus))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("坐标超出可表示范围。"));
            return;
        }

        await JumpToPositionAsync(target, blockFocus);
    }

    private async void JumpToSpawn_Click(object sender, RoutedEventArgs e)
    {
        if(!TryGetDisplayedWorldSpawn(out BlockPosition spawn))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("当前视图没有可用的出生点信息。"));
            SetCoordinateJumpButtonsEnabled(true);
            return;
        }

        XCoordinate.Text = spawn.X.ToString();
        YCoordinate.Text = spawn.Y.ToString();
        ZCoordinate.Text = spawn.Z.ToString();
        await JumpToPositionAsync(
            new Vector3(spawn.X, spawn.Y, spawn.Z),
            spawn,
            displayedContent == DisplayedContent.PlanPreview
                ? displayedPlanDimension ?? MinecraftDimensionId.Overworld
                : MinecraftDimensionId.Overworld);
    }

    private async void RegionFile_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not Button { Tag: WorldRegionNavigationItem region }) return;
        if(mountedWorld is null ||
           displayedContent is not DisplayedContent.WorldSections and not DisplayedContent.ConvertedWorldSections)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("区域文件所属的世界已经关闭，请重新打开世界。"));
            return;
        }

        int preferredY = CurrentRegionNavigationY();
        BlockPosition? indexedTarget = region.Target is BlockPosition found
            ? found with { Y = preferredY }
            : null;
        if(indexedTarget is not BlockPosition target)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"{region.FileName} 的中心坐标超出可导航范围。"));
            return;
        }

        XCoordinate.Text = target.X.ToString();
        YCoordinate.Text = target.Y.ToString();
        ZCoordinate.Text = target.Z.ToString();
        UiText.Bind(StatusText, "Text", UiText.Message($"正在前往 {WorldRegionNavigation.FormatDimensionName(region.Address.Dimension)} · {region.FileName}……"));
        StatusText.ToolTip = region.FileName;
        await JumpToPositionAsync(
            new Vector3(target.X, target.Y, target.Z),
            target,
            region.Address.Dimension);
    }

    private int CurrentRegionNavigationY()
    {
        if(WorldPreviewNavigation.TryParseCoordinate(YCoordinate.Text, out float y) &&
           WorldPreviewNavigation.TryCreateBlockPosition(new Vector3(0f, y, 0f), out BlockPosition parsed))
        {
            return parsed.Y;
        }
        return TryGetDisplayedWorldSpawn(out BlockPosition spawn) ? spawn.Y : 64;
    }

    private async Task JumpToPositionAsync(
        Vector3 target,
        BlockPosition blockFocus,
        MinecraftDimensionId? requestedDimension = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        chunkLoadReloadTimer.Stop();
        int requestGeneration = unchecked(++navigationRequestGeneration);
        isNavigationJumping = true;
        SolidMaterialChoice.IsEnabled = false;
        MinecraftMaterialChoice.IsEnabled = false;
        SetSourceOpenButtonsEnabled(sourceActionsEnabled);
        SetCoordinateJumpButtonsEnabled(false);
        try
        {
            await JumpToPositionCoreAsync(
                target,
                blockFocus,
                requestedDimension,
                requestGeneration,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if(requestGeneration == navigationRequestGeneration)
            {
                isNavigationJumping = false;
                if(!isChangingMaterialMode)
                {
                    SolidMaterialChoice.IsEnabled = true;
                    MinecraftMaterialChoice.IsEnabled = true;
                }
                if(!isClosing)
                {
                    SetSourceOpenButtonsEnabled(sourceActionsEnabled);
                    SetCoordinateJumpButtonsEnabled(true);
                    UpdateConversionControls();
                }
            }
        }
    }

    private async Task JumpToPositionCoreAsync(
        Vector3 target,
        BlockPosition blockFocus,
        MinecraftDimensionId? requestedDimension,
        int requestGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        float x = target.X;
        float y = target.Y;
        float z = target.Z;
        IReadOnlyMinecraftWorld? world = mountedWorld;
        MinecraftDimensionId destinationDimension = requestedDimension ?? displayedWorldDimension;
        await CancelNavigationPreviewAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if(!IsCurrentNavigationRequest(requestGeneration, world)) return;
        bool dimensionChanged = destinationDimension != displayedWorldDimension;
        if(dimensionChanged) await CancelWorldPreviewAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if(!IsCurrentNavigationRequest(requestGeneration, world)) return;
        displayedWorldDimension = destinationDimension;
        if(dimensionChanged)
        {
            navigationPreviewCoverage = null;
            displayedWorldSections.Clear();
            displayedWorldChunkStatistics.Clear();
            Viewport.ClearSections();
            ClearSceneStatistics();
        }
        Viewport.JumpTo(ToPreviewRenderPosition(target));
        Viewport.FocusNavigation();
        if(world is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"视角已跳转到 X {x:0.##} / Y {y:0.##} / Z {z:0.##}。"));
            return;
        }

        if(displayedContent == DisplayedContent.ConvertedWorldSections)
        {
            IMinecraftDowngradePreview? conversion = activeConversionPreview;
            if(conversion is null)
            {
                UiText.Bind(StatusText, "Text", UiText.Text("转换预览已经失效，请重新生成转换预览。"));
                return;
            }
            int convertedChunkCount = world.ChunkIndex.GetChunkCount(displayedWorldDimension);
            if(dimensionChanged || convertedChunkCount > CurrentMaximumLoadedChunkCount)
            {
                if(!dimensionChanged) await CancelWorldPreviewAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if(!IsCurrentNavigationRequest(requestGeneration, world)) return;
                displayedWorldSections.Clear();
                displayedWorldChunkStatistics.Clear();
                Viewport.ClearSections();
                displayedContent = DisplayedContent.ConvertedWorldSections;
                string summary = Minecraft1122ConversionPreviewWorkflow.FormatSummary(conversion.Summary);
                StartConvertedWorldPreview(
                    world,
                    conversion,
                    displayedWorldDimension,
                    convertedChunkCount,
                    summary);
                UiText.Bind(StatusText, "Text", dimensionChanged ? UiText.Message($"已切换到 {WorldRegionNavigation.FormatDimensionName(displayedWorldDimension)}，正在逐步载入转换预览。") : UiText.Message($"已跳转到 X {x:0.##} / Y {y:0.##} / Z {z:0.##}，正在逐步载入转换预览。"));
                return;
            }
            string loading = activeWorldPreviewTask is null ? "转换预览已载入" : "转换预览仍在后台载入";
            UiText.Bind(StatusText, "Text", UiText.Message($"视角已跳转到 X {x:0.##} / Y {y:0.##} / Z {z:0.##}；{loading}。"));
            return;
        }

        int dimensionChunkCount = world.ChunkIndex.GetChunkCount(displayedWorldDimension);
        if(!dimensionChanged && dimensionChunkCount <= CurrentMaximumLoadedChunkCount &&
           displayedContent == DisplayedContent.WorldSections)
        {
            string loading = activeWorldPreviewTask is null ? "当前维度已完整载入" : "当前维度仍在后台载入";
            UiText.Bind(StatusText, "Text", UiText.Message($"视角已跳转到 X {x:0.##} / Y {y:0.##} / Z {z:0.##}；{loading}。"));
            return;
        }

        if(!dimensionChanged) await CancelWorldPreviewAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if(!IsCurrentNavigationRequest(requestGeneration, world) || destinationDimension != displayedWorldDimension)
            return;
        CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worldPreviewCancellation = requestCancellation;
        Task<InitialWorldPreview>? loadTask = null;
        bool loadedInitialNeighborhood = false;
        UiText.Bind(StatusText, "Text", UiText.Message($"正在按需载入 X {x:0.##} / Y {y:0.##} / Z {z:0.##} 周围的 Chunk……"));
        try
        {
            loadTask = LoadInitialWorldPreviewAsync(
                world,
                destinationDimension,
                blockFocus,
                requestCancellation.Token,
                preserveExistingWindow: !dimensionChanged && displayedContent == DisplayedContent.WorldSections);
            activeWorldPreviewTask = loadTask;
            InitialWorldPreview preview = await loadTask;
            if(IsCurrentNavigationRequest(requestGeneration, world) &&
               destinationDimension == displayedWorldDimension)
            {
                PruneWorldSectionWindow(preview.Bounds, DisplayedContent.WorldSections, destinationDimension);
                ShowWorldNeighborhood(preview);
                loadedInitialNeighborhood = true;
                UiText.Bind(StatusText, "Text", UiText.Message($"已跳转并载入 {preview.ChunkCount:N0} 个 Chunk / {preview.SectionCount:N0} 个 Section。"));
            }
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {
            // 下一次跳转或窗口关闭会取消本次视野读取。
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            if(!isClosing && requestGeneration == navigationRequestGeneration)
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"坐标已跳转，但周围区块载入失败：{exception.Message}"));
            }
        }
        finally
        {
            if(ReferenceEquals(activeWorldPreviewTask, loadTask)) activeWorldPreviewTask = null;
            if(ReferenceEquals(worldPreviewCancellation, requestCancellation))
            {
                worldPreviewCancellation = null;
            }
            requestCancellation.Dispose();
        }

        if(dimensionChanged && loadedInitialNeighborhood && dimensionChunkCount <= CurrentMaximumLoadedChunkCount &&
           !isClosing && ReferenceEquals(world, mountedWorld) &&
           requestGeneration == navigationRequestGeneration &&
           destinationDimension == displayedWorldDimension &&
           displayedContent == DisplayedContent.WorldSections && activeWorldPreviewTask is null)
        {
            string version = world.Descriptor.Version.VersionName ?? "原始版本";
            StartWholeWorldPreview(world, displayedWorldDimension, dimensionChunkCount, version);
        }
    }

    private bool IsCurrentNavigationRequest(int requestGeneration, IReadOnlyMinecraftWorld? world) =>
        !isClosing && requestGeneration == navigationRequestGeneration && ReferenceEquals(world, mountedWorld);

    private async void ConversionPreview_Click(object sender, RoutedEventArgs e)
    {
        if(isCalculatingConversionYOffset) return;
        if(ReferenceEquals(sender, ConversionPreviewButton) &&
           (displayedContent == DisplayedContent.ConvertedWorldSections || activeConversionWorkflowTask is not null)) return;
        chunkLoadReloadTimer.Stop();
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(world is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("请先打开 Minecraft 世界。"));
            return;
        }
        if(Minecraft1122ConversionPreviewWorkflow.IsAlreadyTarget(world.Descriptor.Version))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("当前世界已经是 1.12.2，无需创建降级预览。"));
            return;
        }

        ConversionYOffsetInput offsetInput = Minecraft1122ConversionPreviewWorkflow.ParseYOffset(
            GetConversionYOffsetText());
        if(!offsetInput.IsValid)
        {
            StatusText.Text = offsetInput.ErrorMessage!;
            return;
        }
        if(activeConversionWorkflowTask is not null) return;

        if(displayedContent != DisplayedContent.ConvertedWorldSections)
        {
            originalWorldResourceSelection = activeResourceSelection;
        }
        SetSourceOpenButtonsEnabled(false);
        SetCoordinateJumpButtonsEnabled(false);
        SetConversionControlsEnabled(false);
        Task? workflow = null;
        try
        {
            await CancelWorldExportAsync();
            await CancelWorldPreviewAsync();
            await CancelConversionPreviewAsync(invalidateCachedPreview: false);
            if(isClosing || !ReferenceEquals(world, mountedWorld)) return;

            workflow = RunConversionPreviewAsync(world, offsetInput);
            activeConversionWorkflowTask = workflow;
            await workflow;
        }
        finally
        {
            if(ReferenceEquals(activeConversionWorkflowTask, workflow)) activeConversionWorkflowTask = null;
            if(!isClosing)
            {
                SetSourceOpenButtonsEnabled(true);
                SetCoordinateJumpButtonsEnabled(true);
                UpdateConversionControls();
            }
        }
    }

    private async Task RunConversionPreviewAsync(
        IReadOnlyMinecraftWorld world,
        ConversionYOffsetInput offsetInput)
    {
        CancellationTokenSource requestCancellation = new();
        conversionPreviewCancellation = requestCancellation;
        Task<IMinecraftDowngradePreview>? task = null;
        IMinecraftDowngradePreview? pendingPreview = null;
        IReadOnlyList<NormalizedMinecraftSection> retainedSections = displayedWorldSections.Values.ToArray();
        ResourceSelection? retainedResources = activeResourceSelection;
        DisplayedContent retainedContent = displayedContent;
        bool resourceTransitionStarted = false;
        int totalChunks = world.ChunkIndex.TotalChunkCount;
        UiText.Bind(StatusText, "Text", UiText.Message($"正在分析 1.12.2 转换：0 / {totalChunks:N0} 个 Chunk……"));

        try
        {
            INormalizedMinecraftChunkSource source = NormalizedMinecraftChunkSourceFactory.Create(world);
            IMinecraftBlockDowngradeRules rules = await GetBlockMappingRulesAsync();
            EnsureCurrentConversionRequest(world, requestCancellation);
            string resourceRevision = await ResolveConversionResourceRevisionAsync(
                world.Descriptor,
                requestCancellation.Token);
            EnsureCurrentConversionRequest(world, requestCancellation);
            Minecraft1122ConversionPreviewCacheKey cacheKey =
                Minecraft1122ConversionPreviewWorkflow.CreateCacheKey(
                    source,
                    offsetInput.RequestedOffset,
                    Minecraft1122ConversionPreviewWorkflow.GetMappingRevision(rules),
                    resourceRevision);
            IMinecraftDowngradePreview selectedPreview;
            bool reusedPreview = conversionPreviewCache.TryGet(cacheKey, out selectedPreview!);
            if(!reusedPreview)
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"正在准备并行分析 1.12.2 转换：0 / {totalChunks:N0} 个 Chunk……"));
                IReadOnlyList<MinecraftChunkAddress> addresses = await Task.Run(
                    async () => await CollectAllChunkAddressesAsync(
                        world.ChunkIndex,
                        source.Dimensions,
                        requestCancellation.Token).ConfigureAwait(false),
                    requestCancellation.Token);
                EnsureCurrentConversionRequest(world, requestCancellation);
                int conversionConcurrency = Minecraft1122ConversionPreviewWorkflow.SelectConversionConcurrency(
                    CurrentChunkLoadConcurrency);
                MinecraftDowngradePreviewRequest request =
                    Minecraft1122ConversionPreviewWorkflow.CreateRequest(
                        source,
                        offsetInput.RequestedOffset,
                        addresses,
                        conversionConcurrency);
                Progress<MinecraftConversionProgress> progress = new(value =>
                {
                    if(IsCurrentConversionRequest(world, requestCancellation))
                        UiText.Bind(StatusText, "Text", UiText.Message($"正在并行分析 1.12.2 转换：{value.ChunksScanned:N0} / {totalChunks:N0} 个 Chunk · {conversionConcurrency:N0} 路……"));
                });
                MinecraftDowngradePreviewService service = new(rules);
                task = Task.Run(
                    async () => await service.CreateAsync(request, progress, requestCancellation.Token),
                    requestCancellation.Token);
                activeConversionPreviewTask = task;
                pendingPreview = await task;
                selectedPreview = pendingPreview;
                EnsureCurrentConversionRequest(world, requestCancellation);
            }

            MinecraftDowngradePreviewSummary summary = selectedPreview.Summary;
            SetConversionYOffsetText(summary.AppliedYOffset >= 0
                ? summary.AppliedYOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty);
            string summaryText = Minecraft1122ConversionPreviewWorkflow.FormatSummary(summary);
            if(!Minecraft1122ConversionPreviewWorkflow.CanRenderWithoutClipping(summary))
            {
                IMinecraftDowngradePreview? clipped = await ConfirmTopClippingAsync(world, summary, requestCancellation.Token);
                if(clipped is null) return;
                if(pendingPreview is not null) await pendingPreview.DisposeAsync();
                pendingPreview = clipped;
                selectedPreview = clipped;
                reusedPreview = false;
                summary = clipped.Summary;
                summaryText = Minecraft1122ConversionPreviewWorkflow.FormatSummary(summary);
                cacheKey = cacheKey with { MappingRevision = cacheKey.MappingRevision + ":confirmed-top-clipping", RequestedYOffset = summary.AppliedYOffset };
                SetConversionYOffsetText(summary.AppliedYOffset >= 0 ? summary.AppliedYOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
                EnsureCurrentConversionRequest(world, requestCancellation);
            }

            resourceTransitionStarted = true;
            string resourceStatus = await ConfigureConversionResourcesAsync(
                world.Descriptor,
                requestCancellation.Token);
            EnsureCurrentConversionRequest(world, requestCancellation);

            if(!reusedPreview)
            {
                await conversionPreviewCache.StoreAsync(cacheKey, selectedPreview);
                pendingPreview = null;
            }
            Vector3 retainedWorldCamera = FromPreviewRenderPosition(Viewport.CameraTarget);
            activeConversionPreview = selectedPreview;
            ClearDisplayedContent();
            displayedContent = DisplayedContent.ConvertedWorldSections;
            Viewport.JumpTo(ToPreviewRenderPosition(retainedWorldCamera, summary.AppliedYOffset));
            int dimensionChunkCount = world.ChunkIndex.GetChunkCount(displayedWorldDimension);
            string preparationStatus = reusedPreview ? "已复用转换缓存" : "并行转换分析完成";
            UiText.Bind(StatusText, "Text", UiText.Message($"{preparationStatus}，正在载入预览。{summaryText}；{resourceStatus}。"));
            StartConvertedWorldPreview(
                world,
                activeConversionPreview,
                displayedWorldDimension,
                dimensionChunkCount,
                summaryText);
        }
        catch(OperationCanceledException)
        {
            if(resourceTransitionStarted && IsCurrentConversionRequest(world, requestCancellation) &&
               displayedContent == retainedContent)
                RestoreRetainedSectionSnapshot(retainedSections, retainedResources);
        }
        catch(Exception exception)
        {
            if(IsCurrentConversionRequest(world, requestCancellation))
            {
                if(resourceTransitionStarted && displayedContent == retainedContent)
                    RestoreRetainedSectionSnapshot(retainedSections, retainedResources);
                UiText.Bind(StatusText, "Text", UiText.Message($"无法创建 1.12.2 转换预览：{exception.Message}"));
            }
        }
        finally
        {
            if(pendingPreview is not null) await pendingPreview.DisposeAsync();
            if(ReferenceEquals(activeConversionPreviewTask, task)) activeConversionPreviewTask = null;
            if(ReferenceEquals(conversionPreviewCancellation, requestCancellation))
            {
                conversionPreviewCancellation = null;
                requestCancellation.Dispose();
            }
            UpdateConversionControls();
        }
    }

    private bool IsCurrentConversionRequest(
        IReadOnlyMinecraftWorld world,
        CancellationTokenSource requestCancellation) =>
        !isClosing && !requestCancellation.IsCancellationRequested &&
        ReferenceEquals(world, mountedWorld) &&
        ReferenceEquals(conversionPreviewCancellation, requestCancellation);

    private void EnsureCurrentConversionRequest(
        IReadOnlyMinecraftWorld world,
        CancellationTokenSource requestCancellation)
    {
        requestCancellation.Token.ThrowIfCancellationRequested();
        if(!IsCurrentConversionRequest(world, requestCancellation))
            throw new OperationCanceledException(requestCancellation.Token);
    }

    private async void OriginalWorld_Click(object sender, RoutedEventArgs e)
    {
        chunkLoadReloadTimer.Stop();
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(world is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("请先打开 Minecraft 世界。"));
            return;
        }
        if(displayedContent != DisplayedContent.ConvertedWorldSections &&
           activeConversionPreviewTask is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("当前显示的就是原始地图。"));
            return;
        }

        CancellationTokenSource transitionCancellation = new();
        contentTransitionCancellation = transitionCancellation;
        SetSourceOpenButtonsEnabled(false);
        SetCoordinateJumpButtonsEnabled(false);
        SetConversionControlsEnabled(false);
        CancellationTokenSource? requestCancellation = null;
        Task<InitialWorldPreview>? loadTask = null;
        try
        {
            await CancelWorldExportAsync();
            await CancelWorldPreviewAsync();
            await CancelConversionPreviewAsync(invalidateCachedPreview: false);
            EnsureCurrentContentTransition(world, transitionCancellation);

            string resourceStatus = await RestoreOriginalWorldResourcesAsync(
                world.Descriptor,
                transitionCancellation.Token);
            EnsureCurrentContentTransition(world, transitionCancellation);
            Vector3 retainedWorldCamera = FromPreviewRenderPosition(Viewport.CameraTarget);
            originalWorldResourceSelection = null;
            ClearDisplayedContent();
            Viewport.JumpTo(retainedWorldCamera);
            requestCancellation = new CancellationTokenSource();
            worldPreviewCancellation = requestCancellation;
            BlockPosition focus = WorldPreviewNavigation.TryCreateBlockPosition(retainedWorldCamera, out BlockPosition current)
                ? current
                : CurrentFocus(world.Descriptor);
            loadTask = LoadInitialWorldPreviewAsync(
                world,
                displayedWorldDimension,
                focus,
                requestCancellation.Token);
            activeWorldPreviewTask = loadTask;
            InitialWorldPreview preview = await loadTask;
            requestCancellation.Token.ThrowIfCancellationRequested();
            if(isClosing || !ReferenceEquals(world, mountedWorld)) return;

            ShowWorldNeighborhood(preview);
            int dimensionChunkCount = world.ChunkIndex.GetChunkCount(displayedWorldDimension);
            string version = world.Descriptor.Version.VersionName ?? "原始版本";
            UiText.Bind(StatusText, "Text", UiText.Message($"原始地图已恢复：{preview.ChunkCount:N0} 个 Chunk / {preview.SectionCount:N0} 个 Section；{resourceStatus}。"));

            if(ReferenceEquals(activeWorldPreviewTask, loadTask)) activeWorldPreviewTask = null;
            if(ReferenceEquals(worldPreviewCancellation, requestCancellation)) worldPreviewCancellation = null;
            requestCancellation.Dispose();
            requestCancellation = null;
            loadTask = null;
            if(dimensionChunkCount <= CurrentMaximumLoadedChunkCount)
            {
                StartWholeWorldPreview(world, displayedWorldDimension, dimensionChunkCount, version);
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception exception)
        {
            if(!isClosing && ReferenceEquals(world, mountedWorld))
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"原始地图恢复失败：{exception.Message}"));
            }
        }
        finally
        {
            if(ReferenceEquals(activeWorldPreviewTask, loadTask)) activeWorldPreviewTask = null;
            if(requestCancellation is not null && ReferenceEquals(worldPreviewCancellation, requestCancellation))
            {
                worldPreviewCancellation = null;
                requestCancellation.Dispose();
            }
            if(ReferenceEquals(contentTransitionCancellation, transitionCancellation))
            {
                contentTransitionCancellation = null;
                transitionCancellation.Dispose();
            }
            if(!isClosing)
            {
                SetSourceOpenButtonsEnabled(true);
                SetCoordinateJumpButtonsEnabled(true);
                UpdateConversionControls();
            }
        }
    }

    private void EnsureCurrentContentTransition(
        IReadOnlyMinecraftWorld world,
        CancellationTokenSource requestCancellation)
    {
        requestCancellation.Token.ThrowIfCancellationRequested();
        if(isClosing || !ReferenceEquals(world, mountedWorld) ||
           !ReferenceEquals(contentTransitionCancellation, requestCancellation))
            throw new OperationCanceledException(requestCancellation.Token);
    }

    private void UnifiedOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if(!UnifiedOpenButton.IsEnabled) return;
        UnifiedOpenMenu.PlacementTarget = UnifiedOpenButton;
        UnifiedOpenMenu.IsOpen = true;
    }

    private void UnifiedExportButton_Click(object sender, RoutedEventArgs e)
    {
        if(!UnifiedExportButton.IsEnabled) return;
        UpdateUnifiedExportMenu();
        UnifiedExportMenu.PlacementTarget = UnifiedExportButton;
        UnifiedExportMenu.IsOpen = true;
    }

    private async void ExportCurrentOriginalVersionWorldFolder_Click(object sender, RoutedEventArgs e)
    {
        await ExportCurrentAsWorldFolderAsync(preserveSourceVersion: true);
    }

    private async void ExportCurrentWorldFolder_Click(object sender, RoutedEventArgs e)
    {
        await ExportCurrentAsWorldFolderAsync();
    }

    private async void ExportCurrentSchematic_Click(object sender, RoutedEventArgs e)
    {
        await ExportCurrentAsSchematicAsync();
    }

    private async void ExportCurrentPlan_Click(object sender, RoutedEventArgs e)
    {
        await ExportCurrentAsPlanAsync();
    }

    private async Task ExportCurrentAsWorldFolderAsync(bool preserveSourceVersion = false)
    {
        ExportSourceContext? context = CreateCurrentExportSource();
        if(context is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("当前内容不能导出为 Minecraft 世界。请先打开世界、.schematic 或筑界镜蓝图。"));
            return;
        }
        if(preserveSourceVersion && (context.World is null || !context.IsOriginalWorldScene))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("只有当前打开的原始世界文件夹可以导出原版本副本。"));
            return;
        }
        ConversionYOffsetInput offsetInput = preserveSourceVersion
            ? new ConversionYOffsetInput(true, null, null)
            : Minecraft1122ConversionPreviewWorkflow.ParseYOffset(GetConversionYOffsetText());
        if(!offsetInput.IsValid)
        {
            StatusText.Text = offsetInput.ErrorMessage!;
            return;
        }
        if(context.AlreadyConvertedYOffset is int previewOffset &&
           offsetInput.RequestedOffset is int requestedOffset &&
           requestedOffset != previewOffset)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("Y 上移值已改变，请先重新生成转换预览再导出。"));
            return;
        }

        OpenFolderDialog dialog = new()
        {
            Title = UiText.Get("选择新世界所在文件夹"),
            Multiselect = false,
        };
        if(dialog.ShowDialog(GetMapFunctionsDialogOwner()) != true) return;

        string requestedDestination;
        try
        {
            string exportName = SanitizeExportName(context.DisplayName);
            requestedDestination = preserveSourceVersion
                ? Path.Combine(
                    Path.GetFullPath(dialog.FolderName),
                    $"{exportName}-原版本副本")
                : context.World is not null
                ? Minecraft1122ConversionPreviewWorkflow.BuildSuggestedDestinationDirectory(
                    dialog.FolderName,
                    context.World.Descriptor)
                : Path.Combine(
                    Path.GetFullPath(dialog.FolderName),
                    context.IsAiProjectScene ? exportName : $"{exportName}-1.12.2");
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"无法使用该导出位置：{exception.Message}"));
            return;
        }

        object exportOwner = new();
        CancellationTokenSource requestCancellation = new();
        CancellationToken cancellationToken = requestCancellation.Token;
        if(!TryBeginWorldExport(exportOwner, requestCancellation))
        {
            requestCancellation.Dispose();
            UiText.Bind(StatusText, "Text", UiText.Text("已有导出任务正在运行。"));
            return;
        }
        Task<WorldFolderExportOutcome>? task = null;
        string exportFinishStage = "导出已取消";
        UiText.Bind(StatusText, "Text", UiText.Text("正在停止场景流送并检查导出条件……"));
        StatusText.ToolTip = requestedDestination;
        BeginWorldExportLoading(UiText.Get("正在准备导出"), UiText.Get("正在停止场景流送并检查导出条件"));
        await Dispatcher.Yield(DispatcherPriority.Render);
        try
        {
            await CancelWorldPreviewAsync();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWorldExportOwner(exportOwner, cancellationToken);
            if(isClosing) return;
            UiText.Bind(StatusText, "Text", preserveSourceVersion ? UiText.Text("正在验证原版本世界完整性……") : UiText.Text("正在检查高度、方块替换与源地图完整性……"));
            Progress<MinecraftConversionProgress> conversionProgress = new(value =>
            {
                if(!isClosing && ReferenceEquals(worldExportCancellation, requestCancellation))
                {
                    string status = UiText.Format($"正在准备 1.12.2 世界：已检查 {value.ChunksScanned:N0} 个 Chunk / {value.BlocksScanned:N0} 个方块……");
                    StatusText.Text = status;
                    UpdateWorldExportLoading(
                        UiText.Get("正在检查并转换世界内容"),
                        value.ChunksScanned,
                        value.ChunksTotal ?? 0,
                        UiText.Format($"已检查 {value.BlocksScanned:N0} 个方块"));
                }
            });
            Progress<MinecraftWorldExportProgress> exportProgress = new(value =>
            {
                if(!isClosing && ReferenceEquals(worldExportCancellation, requestCancellation))
                {
                    StatusText.Text = FormatWorldExportProgress(value, context.TotalChunkCount);
                    UpdateWorldExportLoading(value, context.TotalChunkCount);
                }
            });
            Progress<MinecraftWorldCloneProgress> cloneProgress = new(value =>
            {
                if(!isClosing && ReferenceEquals(worldExportCancellation, requestCancellation))
                {
                    StatusText.Text = FormatWorldCloneProgress(value);
                    UpdateWorldExportLoadingIndeterminate(
                        value.Message,
                        $"已复制 {value.FilesCopied:N0} 个文件 / {value.BytesCopied:N0} 字节");
                }
            });
            IMinecraftBlockDowngradeRules? mappingRules = preserveSourceVersion
                ? null
                : await GetBlockMappingRulesAsync();
            int conversionConcurrency = Minecraft1122ConversionPreviewWorkflow.SelectConversionConcurrency(
                CurrentChunkLoadConcurrency);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWorldExportOwner(exportOwner, cancellationToken);
            task = preserveSourceVersion
                ? Task.Run(
                    () => RunOriginalVersionWorldCloneAsync(
                        context,
                        requestedDestination,
                        cloneProgress,
                        cancellationToken),
                    cancellationToken)
                : Task.Run(
                    () => RunWorldFolderExportAsync(
                         context,
                         requestedDestination,
                         offsetInput.RequestedOffset,
                         mappingRules!,
                         conversionConcurrency,
                         conversionProgress,
                        exportProgress,
                        cloneProgress,
                        cancellationToken,
                        context.World is null ? null : summary => Dispatcher.InvokeAsync(async () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            EnsureWorldExportOwner(exportOwner, cancellationToken);
                            return await ConfirmTopClippingAsync(context.World, summary, cancellationToken);
                        }).Task.Unwrap()),
                    cancellationToken);
            activeWorldExportTask = task;
            WorldFolderExportOutcome outcome = await task;
            EnsureWorldExportOwner(exportOwner, cancellationToken);
            if(isClosing) return;

            ShowWorldFolderExportResult(context, outcome);
            exportFinishStage = "世界导出完成";
        }
        catch(OperationCanceledException)
        {
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Text("导出已取消；源地图和未完成目标均未改动。"));
            exportFinishStage = "导出已取消";
        }
        catch(Exception exception)
        {
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Message($"世界导出失败：{exception.Message}"));
            exportFinishStage = "世界导出失败";
        }
        finally
        {
            if(ReferenceEquals(activeWorldExportTask, task)) activeWorldExportTask = null;
            if(ReferenceEquals(worldExportCancellation, requestCancellation)) worldExportCancellation = null;
            requestCancellation.Dispose();
            EndWorldExport(exportOwner);
            EndWorldExportLoading(exportFinishStage, exportFinishStage == UiText.Get("世界导出完成"));
        }
    }

    private static async Task<WorldFolderExportOutcome> RunWorldFolderExportAsync(
        ExportSourceContext context,
        string destination,
        int? requestedOffset,
        IMinecraftBlockDowngradeRules mappingRules,
        int conversionConcurrency,
        IProgress<MinecraftConversionProgress> conversionProgress,
        IProgress<MinecraftWorldExportProgress> exportProgress,
        IProgress<MinecraftWorldCloneProgress> cloneProgress,
        CancellationToken cancellationToken,
        Func<MinecraftDowngradePreviewSummary, Task<IMinecraftDowngradePreview?>>? confirmTopClipping = null)
    {
        ArgumentNullException.ThrowIfNull(mappingRules);
        await EnsureWorldSourceStableAsync(context.World, cancellationToken).ConfigureAwait(false);

        if(CanCloneOriginalWorld(context, requestedOffset))
        {
            string cloneDestination = AllocateUniqueWorldDirectory(destination);
            return await CloneOriginalWorldAsync(
                context.World!,
                cloneDestination,
                cloneProgress,
                cancellationToken).ConfigureAwait(false);
        }

        IMinecraftDowngradePreview? ownedPreview = null;
        try
        {
            INormalizedMinecraftChunkSource targetChunks;
            int appliedOffset;
            if(context.AlreadyConvertedYOffset is int convertedOffset)
            {
                targetChunks = context.Source;
                appliedOffset = convertedOffset;
            }
            else
            {
                IReadOnlyList<MinecraftChunkAddress>? addresses = context.World is null
                    ? null
                    : await CollectAllChunkAddressesAsync(
                        context.World.ChunkIndex,
                        context.Source.Dimensions,
                        cancellationToken).ConfigureAwait(false);
                MinecraftDowngradePreviewService service = new(mappingRules);
                ownedPreview = await service.CreateAsync(
                    Minecraft1122ConversionPreviewWorkflow.CreateRequest(
                        context.Source,
                        requestedOffset,
                        addresses,
                        conversionConcurrency),
                    conversionProgress,
                    cancellationToken).ConfigureAwait(false);
                if(!ownedPreview.Summary.YTranslation.CanFitWithoutClipping && confirmTopClipping is not null)
                {
                    IMinecraftDowngradePreview? clipped = await confirmTopClipping(ownedPreview.Summary).ConfigureAwait(false);
                    if(clipped is null) throw new OperationCanceledException("已取消顶部裁切导出。", cancellationToken);
                    IMinecraftDowngradePreview previous = ownedPreview;
                    ownedPreview = clipped;
                    await previous.DisposeAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if(!Minecraft1122ConversionPreviewWorkflow.CanExport(ownedPreview.Summary))
                {
                    string summary = Minecraft1122ConversionPreviewWorkflow.FormatSummary(ownedPreview.Summary);
                    throw new InvalidDataException($"当前内容无法无裁切导出到 Y 0–255：{summary}。");
                }
                targetChunks = ownedPreview.ConvertedChunks;
                appliedOffset = ownedPreview.Summary.AppliedYOffset;
            }

            MinecraftOpaquePayload levelMetadata = context.World is not null
                ? await context.World.ReadLevelMetadataAsync(cancellationToken).ConfigureAwait(false)
                : NormalizedMinecraftSceneChunkSource.CreateGeneratedLevelMetadata(context.SourcePath ?? context.DisplayName);
            BlockPosition spawn = FitSpawnToTarget(context.SpawnLocation, appliedOffset);
            MinecraftWorldExportRequest request = new(
                destination,
                targetChunks.Revision,
                MinecraftTargetProfile.Java1122,
                targetChunks,
                levelMetadata,
                spawn,
                appliedOffset,
                new MinecraftUnknownDataPolicy(),
                context.World,
                MinecraftExportDestinationPolicy.CreateUniqueSibling,
                context.World is null ? null : new MinecraftAuxiliaryExportPolicy());
            Minecraft1122WorldExporter exporter = new(mappingRules);
            MinecraftWorldExportResult result = await exporter.ExportAsync(
                request,
                exportProgress,
                cancellationToken).ConfigureAwait(false);
            return WorldFolderExportOutcome.FromReencoded(result, appliedOffset);
        }
        finally
        {
            if(ownedPreview is not null) await ownedPreview.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<WorldFolderExportOutcome> RunOriginalVersionWorldCloneAsync(
        ExportSourceContext context,
        string destination,
        IProgress<MinecraftWorldCloneProgress> cloneProgress,
        CancellationToken cancellationToken)
    {
        IReadOnlyMinecraftWorld world = context.World ??
            throw new InvalidOperationException("原版本副本导出需要打开一个世界文件夹。");
        if(!context.IsOriginalWorldScene)
            throw new InvalidOperationException("请先切换回原始地图，再导出原版本副本。");
        await EnsureWorldSourceStableAsync(world, cancellationToken).ConfigureAwait(false);
        return await CloneOriginalWorldAsync(
            world,
            AllocateUniqueWorldDirectory(destination),
            cloneProgress,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorldFolderExportOutcome> CloneOriginalWorldAsync(
        IReadOnlyMinecraftWorld world,
        string destination,
        IProgress<MinecraftWorldCloneProgress> cloneProgress,
        CancellationToken cancellationToken)
    {
        MinecraftWorldCloneResult cloneResult = await new MinecraftWorldCloneExporter().ExportAsync(
            new MinecraftWorldCloneRequest(destination, world.Descriptor.SourceRevision, world),
            cloneProgress,
            cancellationToken).ConfigureAwait(false);
        List<MinecraftDiagnostic> diagnostics =
        [
            new MinecraftDiagnostic(
                "export.clone.building_protection",
                MinecraftDiagnosticSeverity.Information,
                $"已在导出副本写入建筑保护规则，并从 {cloneResult.Protection.ChunkPayloadsRewritten:N0} 个 Chunk 清理 {cloneResult.Protection.ScheduledTickContainersCleared:N0} 个非空方块/流体调度表。"),
        ];
        if(cloneResult.SkippedSessionLockCount > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "export.clone.session_lock_skipped",
                MinecraftDiagnosticSeverity.Information,
                $"导出副本时跳过 {cloneResult.SkippedSessionLockCount:N0} 个 session.lock；Minecraft 会在打开副本时重新创建。"));
        }
        return WorldFolderExportOutcome.FromClone(cloneResult, diagnostics);
    }

    private async Task ExportCurrentAsSchematicAsync()
    {
        ExportSourceContext? context = CreateCurrentExportSource();
        if(context is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("当前内容不能导出为 .schematic。请先打开世界、.schematic 或筑界镜蓝图。"));
            return;
        }
        SaveFileDialog dialog = new()
        {
            Title = UiText.Get("导出 Legacy .schematic"),
            Filter = UiText.Get("Minecraft 结构 (*.schematic)|*.schematic"),
            DefaultExt = ".schematic",
            AddExtension = true,
            FileName = $"{SanitizeExportName(context.DisplayName)}.schematic",
            OverwritePrompt = false,
        };
        if(dialog.ShowDialog(this) != true) return;
        if(IsProtectedSourceFile(dialog.FileName, context.SourcePath))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("导出目标不能覆盖当前打开的源文件，请选择新文件名。"));
            return;
        }
        if(File.Exists(dialog.FileName) || Directory.Exists(dialog.FileName))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("导出目标已存在；为保护既有文件，请选择新文件名。"));
            return;
        }

        object exportOwner = new();
        CancellationTokenSource requestCancellation = new();
        CancellationToken cancellationToken = requestCancellation.Token;
        if(!TryBeginWorldExport(exportOwner, requestCancellation))
        {
            requestCancellation.Dispose();
            UiText.Bind(StatusText, "Text", UiText.Text("已有导出任务正在运行。"));
            return;
        }
        Task<SchematicExportOutcome>? task = null;
        UiText.Bind(StatusText, "Text", UiText.Text("正在停止场景流送并准备 Legacy .schematic……"));
        StatusText.ToolTip = dialog.FileName;
        try
        {
            await CancelWorldPreviewAsync();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWorldExportOwner(exportOwner, cancellationToken);
            if(isClosing) return;
            UiText.Bind(StatusText, "Text", UiText.Text("正在收集当前维度并转换为 Legacy .schematic……"));
            Progress<MinecraftConversionProgress> progress = new(value =>
            {
                if(!isClosing && ReferenceEquals(worldExportCancellation, requestCancellation))
                    UiText.Bind(StatusText, "Text", UiText.Message($"正在生成 .schematic：{value.ChunksScanned:N0} 个 Chunk / {value.BlocksScanned:N0} 个方块……"));
            });
            IMinecraftBlockDowngradeRules mappingRules = await GetBlockMappingRulesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWorldExportOwner(exportOwner, cancellationToken);
            task = Task.Run(
                () => RunSchematicExportAsync(context, dialog.FileName, mappingRules, progress, cancellationToken),
                cancellationToken);
            activeWorldExportTask = task;
            SchematicExportOutcome outcome = await task;
            EnsureWorldExportOwner(exportOwner, cancellationToken);
            if(isClosing) return;

            long knownEntities = Math.Max(outcome.CollectedBlockEntityCount, context.KnownBlockEntityCount);
            long omittedEntities = Math.Max(0, knownEntities - outcome.Result.BlockEntityCount);
            string entities = outcome.Result.BlockEntityCount == 0
                ? string.Empty
                : $"；已保留 {outcome.Result.BlockEntityCount:N0} 个方块实体";
            if(omittedEntities > 0) entities += $"；{omittedEntities:N0} 个方块实体位于导出范围外";
            ExportDiagnosticPresentation diagnostics = PresentExportDiagnostics(outcome.Result.Diagnostics);
            UiText.Bind(StatusText, "Text", UiText.Message($".schematic 导出完成：{outcome.Result.NonAirBlockCount:N0} 个方块，{outcome.Result.Bounds.MaxExclusive.X - outcome.Result.Bounds.Min.X} × {outcome.Result.Bounds.MaxExclusive.Y - outcome.Result.Bounds.Min.Y} × {outcome.Result.Bounds.MaxExclusive.Z - outcome.Result.Bounds.Min.Z}{entities}{diagnostics.StatusSuffix}。"));
            StatusText.ToolTip = BuildExportToolTip(outcome.Result.DestinationPath, diagnostics);
        }
        catch(OperationCanceledException)
        {
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Text("导出已取消；源文件未改动。"));
        }
        catch(Exception exception)
        {
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Message($".schematic 导出失败：{exception.Message}"));
        }
        finally
        {
            if(ReferenceEquals(activeWorldExportTask, task)) activeWorldExportTask = null;
            if(ReferenceEquals(worldExportCancellation, requestCancellation)) worldExportCancellation = null;
            requestCancellation.Dispose();
            EndWorldExport(exportOwner);
        }
    }

    private static async Task<SchematicExportOutcome> RunSchematicExportAsync(
        ExportSourceContext context,
        string destination,
        IMinecraftBlockDowngradeRules mappingRules,
        IProgress<MinecraftConversionProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mappingRules);
        await EnsureWorldSourceStableAsync(context.World, cancellationToken).ConfigureAwait(false);
        if(context.SchematicBounds is BoxSelection exactBounds)
            EnsureSchematicVolumeWithinLimit(exactBounds.Min, exactBounds.MaxExclusive.Offset(-1, -1, -1));
        CollectedDimensionSections collected = await CollectDimensionSectionsAsync(
            context.Source,
            context.MainDimension,
            progress,
            "正在收集 .schematic 方块",
            cancellationToken).ConfigureAwait(false);
        await EnsureWorldSourceStableAsync(context.World, cancellationToken).ConfigureAwait(false);
        string fullDestination = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidDataException(".schematic 导出目标缺少父目录。");
        string staging = Path.Combine(
            parent,
            $".{Path.GetFileNameWithoutExtension(fullDestination)}.zjj-staging-{Guid.NewGuid():N}.schematic");
        try
        {
            LegacySchematicExportResult staged = new LegacySchematicExporter(mappingRules)
                .Export(new LegacySchematicExportRequest(
                    staging,
                    collected.Sections,
                    context.SchematicBounds,
                    context.SchematicClipboardOrigin,
                    collected.BlockEntities), cancellationToken);
            await EnsureWorldSourceStableAsync(context.World, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if(File.Exists(fullDestination) || Directory.Exists(fullDestination))
                throw new IOException(".schematic 导出目标在提交前已存在；既有文件未被覆盖。");
            File.Move(staging, fullDestination, overwrite: false);
            return new SchematicExportOutcome(
                staged with { DestinationPath = fullDestination },
                collected.BlockEntityCount);
        }
        finally
        {
            if(File.Exists(staging))
            {
                try
                {
                    File.Delete(staging);
                }
                catch(IOException)
                {
                }
                catch(UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static async ValueTask EnsureWorldSourceStableAsync(
        IReadOnlyMinecraftWorld? world,
        CancellationToken cancellationToken)
    {
        if(world is null) return;
        WorldSourceValidationResult validation = await world.ValidateSourceAsync(cancellationToken).ConfigureAwait(false);
        if(!validation.IsStable)
            throw new IOException("原世界在导出期间发生了变化，请关闭 Minecraft 并重新打开世界。");
    }

    private async Task ExportCurrentAsPlanAsync()
    {
        ExportSourceContext? context = CreateCurrentExportSource();
        if(context is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("当前内容不能导出为筑界镜蓝图。请先打开世界、.schematic 或筑界镜蓝图。"));
            return;
        }
        SaveFileDialog dialog = new()
        {
            Title = UiText.Get("导出筑界镜蓝图"),
            Filter = UiText.Get("筑界镜蓝图 (*.zz)|*.zz"),
            DefaultExt = ".zz",
            AddExtension = true,
            FileName = $"{SanitizeExportName(context.DisplayName)}.zz",
            OverwritePrompt = false,
        };
        if(dialog.ShowDialog(this) != true) return;
        if(IsProtectedSourceFile(dialog.FileName, context.SourcePath))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("导出目标不能覆盖当前打开的源文件，请选择新文件名。"));
            return;
        }
        if(File.Exists(dialog.FileName) || Directory.Exists(dialog.FileName))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("导出目标已存在；为保护既有文件，请选择新文件名。"));
            return;
        }

        object exportOwner = new();
        CancellationTokenSource requestCancellation = new();
        CancellationToken cancellationToken = requestCancellation.Token;
        if(!TryBeginWorldExport(exportOwner, requestCancellation))
        {
            requestCancellation.Dispose();
            UiText.Bind(StatusText, "Text", UiText.Text("已有导出任务正在运行。"));
            return;
        }
        Task<MinecraftZjjPlanExportResult>? task = null;
        UiText.Bind(StatusText, "Text", UiText.Text("正在停止场景流送并准备筑界镜蓝图……"));
        StatusText.ToolTip = dialog.FileName;
        try
        {
            await CancelWorldPreviewAsync();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWorldExportOwner(exportOwner, cancellationToken);
            if(isClosing) return;
            UiText.Bind(StatusText, "Text", UiText.Text("正在压缩方块列并生成可编辑筑界镜蓝图……"));
            Progress<MinecraftConversionProgress> progress = new(value =>
            {
                if(!isClosing && ReferenceEquals(worldExportCancellation, requestCancellation))
                    UiText.Bind(StatusText, "Text", UiText.Message($"正在生成筑界镜蓝图：{value.ChunksScanned:N0} 个 Chunk / {value.BlocksScanned:N0} 个方块……"));
            });
            MinecraftZjjPlanExportRequest request = new(
                dialog.FileName,
                context.Source,
                context.MainDimension,
                SanitizeExportName(context.DisplayName),
                context.SourcePath is not null && File.Exists(context.SourcePath) ? context.SourcePath : null,
                context.World,
                context.VisualProfile,
                Math.Min(CurrentChunkLoadConcurrency, MinecraftZjjPlanExporter.MaximumReadConcurrency),
                context.SpawnLocation);
            task = Task.Run(
                () => new MinecraftZjjPlanExporter().ExportAsync(request, progress, cancellationToken),
                cancellationToken);
            activeWorldExportTask = task;
            MinecraftZjjPlanExportResult result = await task;
            EnsureWorldExportOwner(exportOwner, cancellationToken);
            if(isClosing) return;

            long omittedEntities = Math.Max(result.OmittedBlockEntityCount, context.KnownBlockEntityCount);
            string entities = omittedEntities == 0
                ? string.Empty
                : $"；{omittedEntities:N0} 个方块实体不属于 .zz 方块操作";
            UiText.Bind(StatusText, "Text", UiText.Message($"筑界镜蓝图导出完成：{result.NonAirBlockCount:N0} 个方块 / {result.Plan.Operations.Count:N0} 条可编辑操作{entities}。"));
            StatusText.ToolTip = result.OutputPath;
        }
        catch(OperationCanceledException)
        {
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Text("导出已取消；源文件未改动。"));
        }
        catch(Exception exception)
        {
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Message($"筑界镜蓝图导出失败：{exception.Message}"));
        }
        finally
        {
            if(ReferenceEquals(activeWorldExportTask, task)) activeWorldExportTask = null;
            if(ReferenceEquals(worldExportCancellation, requestCancellation)) worldExportCancellation = null;
            requestCancellation.Dispose();
            EndWorldExport(exportOwner);
        }
    }

    private ExportSourceContext? CreateCurrentExportSource()
    {
        switch(displayedContent)
        {
            case DisplayedContent.WorldSections when mountedWorld is IReadOnlyMinecraftWorld world:
                return CreateWorldExportSource(
                    world,
                    NormalizedMinecraftChunkSourceFactory.Create(world),
                    null,
                    isOriginalWorldScene: true);
            case DisplayedContent.ConvertedWorldSections when mountedWorld is IReadOnlyMinecraftWorld world &&
                                                              activeConversionPreview is IMinecraftDowngradePreview preview &&
                                                              Minecraft1122ConversionPreviewWorkflow.CanExport(preview.Summary):
                return CreateWorldExportSource(
                    world,
                    preview.ConvertedChunks,
                    preview.Summary.AppliedYOffset,
                    isOriginalWorldScene: false);
            case DisplayedContent.SchematicSections when displayedSchematic is LegacySchematicImport schematic:
            {
                NormalizedMinecraftSceneChunkSource source = new(
                    schematic.Sections,
                    MinecraftTargetProfile.Java1122.Version,
                    MinecraftDimensionId.Overworld,
                    schematic.BlockEntities);
                return new ExportSourceContext(
                    source,
                    MinecraftDimensionId.Overworld,
                    Path.GetFileNameWithoutExtension(schematic.SourcePath),
                    schematic.SourcePath,
                    FindSceneSpawn(schematic.Sections),
                    null,
                    null,
                    CountChunks(schematic.Sections),
                    schematic.BlockEntityCount,
                    0,
                    false,
                    BoxSelection.FromMinAndSize(
                        schematic.Origin,
                        schematic.Width,
                        schematic.Height,
                        schematic.Length),
                    schematic.ClipboardOrigin);
            }
            case DisplayedContent.PlanPreview when displayedPlanSections.Count > 0:
            {
                MinecraftDimensionId planDimension = displayedPlanDimension ??
                                                     throw new InvalidOperationException("当前蓝图缺少维度信息。");
                MinecraftVersionDescriptor visualVersion = displayedPlanVisualVersion ?? MinecraftTargetProfile.Java1122.Version;
                NormalizedMinecraftSceneChunkSource source = new(
                    displayedPlanSections,
                    visualVersion,
                    planDimension);
                return new ExportSourceContext(
                    source,
                    planDimension,
                    aiProjectSession?.Project.DisplayName ??
                    Path.GetFileNameWithoutExtension(displayedPlanSourcePath ?? "scene"),
                    displayedPlanSourcePath,
                    displayedPlanSpawnPoint ?? FindSceneSpawn(displayedPlanSections),
                    null,
                    null,
                    CountChunks(displayedPlanSections),
                    0,
                    0,
                    false,
                    VisualProfile: displayedPlanVisualProfile ?? CreatePlanVisualProfile(visualVersion),
                    IsAiProjectScene: aiProjectSession is not null);
            }
            default:
                return null;
        }
    }

    private ExportSourceContext? CreateWorldExportSource(
        IReadOnlyMinecraftWorld world,
        INormalizedMinecraftChunkSource source,
        int? alreadyConvertedYOffset,
        bool isOriginalWorldScene)
    {
        MinecraftDimensionId[] supported = source.Dimensions
            .Where(static dimension => dimension == MinecraftDimensionId.Overworld ||
                                       dimension == MinecraftDimensionId.Nether ||
                                       dimension == MinecraftDimensionId.End)
            .ToArray();
        if(supported.Length == 0) return null;
        int skipped = source.Dimensions.Count - supported.Length;
        INormalizedMinecraftChunkSource exportSource = skipped == 0
            ? source
            : new NormalizedMinecraftDimensionSubsetSource(source, supported);
        MinecraftDimensionId mainDimension = supported.Contains(displayedWorldDimension)
            ? displayedWorldDimension
            : supported[0];
        int chunks = supported.Sum(dimension => world.ChunkIndex.GetChunkCount(dimension));
        return new ExportSourceContext(
            exportSource,
            mainDimension,
            world.Descriptor.LevelName,
            world.Descriptor.RootPath,
            world.Descriptor.SpawnLocation ?? CurrentFocus(world.Descriptor),
            world,
            alreadyConvertedYOffset,
            chunks,
            0,
            skipped,
            isOriginalWorldScene);
    }

    private static bool CanCloneOriginalWorld(ExportSourceContext context, int? requestedOffset)
    {
        return context.World is not null &&
               Minecraft1122ConversionPreviewWorkflow.CanUseVerifiedWorldClone(
                   context.World.Descriptor,
                   context.IsOriginalWorldScene,
                   requestedOffset,
                   context.SkippedCustomDimensionCount);
    }

    private static string AllocateUniqueWorldDirectory(string requestedPath)
    {
        string requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        string? parent = Path.GetDirectoryName(requested);
        string name = Path.GetFileName(requested);
        if(parent is null || name.Length == 0)
            throw new InvalidDataException("导出世界路径无效。");
        if(!Directory.Exists(requested) && !File.Exists(requested)) return requested;
        for(int suffix = 1; suffix <= 10_000; suffix++)
        {
            string candidate = Path.Combine(parent, $"{name} ({suffix})");
            if(!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException($"无法在 {requested} 旁分配新的世界目录。");
    }

    private static async Task<CollectedDimensionSections> CollectDimensionSectionsAsync(
        INormalizedMinecraftChunkSource source,
        MinecraftDimensionId dimension,
        IProgress<MinecraftConversionProgress>? progress,
        string message,
        CancellationToken cancellationToken)
    {
        string revision = source.Revision;
        List<NormalizedMinecraftSection> sections = [];
        List<NormalizedMinecraftBlockEntity> blockEntityItems = [];
        BlockPosition? occupiedMinimum = null;
        BlockPosition? occupiedMaximum = null;
        long chunks = 0;
        long blockEntities = 0;
        long blocksScanned = 0;
        await foreach(NormalizedMinecraftChunk chunk in source
                           .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(chunk.Sections.Count > LegacySchematicExporter.MaximumInputSections - sections.Count)
            {
                throw new InvalidDataException(
                    $"当前维度的 Section 数量超过 .schematic 安全上限 {LegacySchematicExporter.MaximumInputSections:N0}；请缩小导出范围。");
            }
            if(chunk.BlockEntities.Count > LegacySchematicExporter.MaximumBlockEntities - blockEntityItems.Count)
            {
                throw new InvalidDataException(
                    $"当前维度的方块实体数量超过 .schematic 安全上限 {LegacySchematicExporter.MaximumBlockEntities:N0}；请缩小导出范围。");
            }
            sections.AddRange(chunk.Sections);
            blockEntityItems.AddRange(chunk.BlockEntities);
            blockEntities = checked(blockEntities + chunk.BlockEntities.Count);
            blocksScanned = checked(blocksScanned + chunk.Sections.Sum(static section => (long)section.PaletteIndices.Length));
            foreach(NormalizedMinecraftSection section in chunk.Sections)
            {
                ExpandOccupiedBounds(section, ref occupiedMinimum, ref occupiedMaximum);
            }
            if(occupiedMinimum is BlockPosition min && occupiedMaximum is BlockPosition max)
                EnsureSchematicVolumeWithinLimit(min, max);
            chunks = checked(chunks + 1);
            progress?.Report(new MinecraftConversionProgress(chunks, null, blocksScanned, chunk.Address, message));
        }
        if(!string.Equals(revision, source.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("导出期间场景 revision 发生变化。");
        return new CollectedDimensionSections(sections, blockEntityItems, chunks, blockEntities);
    }

    private static void ExpandOccupiedBounds(
        NormalizedMinecraftSection section,
        ref BlockPosition? occupiedMinimum,
        ref BlockPosition? occupiedMaximum)
    {
        ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
        for(int localIndex = 0; localIndex < indices.Length; localIndex++)
        {
            ushort paletteIndex = indices[localIndex];
            if(paletteIndex >= section.Palette.Count)
                throw new InvalidDataException($"Section {section.Coordinate} 包含越界的方块索引。");
            if(section.Palette[paletteIndex].IsAir) continue;
            BlockPosition position = section.Coordinate.ToBlockPosition(localIndex);
            occupiedMinimum = occupiedMinimum is BlockPosition minimum
                ? new BlockPosition(
                    Math.Min(minimum.X, position.X),
                    Math.Min(minimum.Y, position.Y),
                    Math.Min(minimum.Z, position.Z))
                : position;
            occupiedMaximum = occupiedMaximum is BlockPosition maximum
                ? new BlockPosition(
                    Math.Max(maximum.X, position.X),
                    Math.Max(maximum.Y, position.Y),
                    Math.Max(maximum.Z, position.Z))
                : position;
        }
    }

    private static void EnsureSchematicVolumeWithinLimit(BlockPosition minimum, BlockPosition maximumInclusive)
    {
        try
        {
            long width = checked((long)maximumInclusive.X - minimum.X + 1);
            long height = checked((long)maximumInclusive.Y - minimum.Y + 1);
            long length = checked((long)maximumInclusive.Z - minimum.Z + 1);
            long volume = checked(checked(width * height) * length);
            if(width <= 0 || height <= 0 || length <= 0 || volume > LegacySchematicExporter.MaximumVolume)
            {
                throw new InvalidDataException(
                    $"当前内容的 .schematic 包围体积 {volume:N0} 超过安全上限 {LegacySchematicExporter.MaximumVolume:N0} 方块；请缩小导出范围。");
            }
        }
        catch(OverflowException exception)
        {
            throw new InvalidDataException("当前内容的 .schematic 包围范围过大；请缩小导出范围。", exception);
        }
    }

    private static BlockPosition FindSceneSpawn(IEnumerable<NormalizedMinecraftSection> sections)
    {
        BlockPosition? minimum = null;
        BlockPosition? maximum = null;
        foreach(NormalizedMinecraftSection section in sections)
        {
            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            for(int localIndex = 0; localIndex < indices.Length; localIndex++)
            {
                ushort paletteIndex = indices[localIndex];
                if(paletteIndex >= section.Palette.Count || section.Palette[paletteIndex].IsAir) continue;
                BlockPosition position = section.Coordinate.ToBlockPosition(localIndex);
                minimum = minimum is BlockPosition min
                    ? new BlockPosition(Math.Min(min.X, position.X), Math.Min(min.Y, position.Y), Math.Min(min.Z, position.Z))
                    : position;
                maximum = maximum is BlockPosition max
                    ? new BlockPosition(Math.Max(max.X, position.X), Math.Max(max.Y, position.Y), Math.Max(max.Z, position.Z))
                    : position;
            }
        }
        if(minimum is not BlockPosition lower || maximum is not BlockPosition upper)
            return new BlockPosition(0, 64, 0);
        int x = checked((int)(((long)lower.X + upper.X) / 2));
        int z = checked((int)(((long)lower.Z + upper.Z) / 2));
        int y = upper.Y == int.MaxValue ? upper.Y : upper.Y + 1;
        return new BlockPosition(x, y, z);
    }

    private static BlockPosition FindSceneSpawn(SceneDelta delta)
    {
        BlockPosition? minimum = null;
        BlockPosition? maximum = null;
        foreach(SectionDelta section in delta.Sections)
        {
            foreach(VoxelChange change in section.Changes)
            {
                if(change.After.IsAir) continue;
                BlockPosition position = section.Section.ToBlockPosition(change.LocalIndex);
                minimum = minimum is BlockPosition min
                    ? new BlockPosition(Math.Min(min.X, position.X), Math.Min(min.Y, position.Y), Math.Min(min.Z, position.Z))
                    : position;
                maximum = maximum is BlockPosition max
                    ? new BlockPosition(Math.Max(max.X, position.X), Math.Max(max.Y, position.Y), Math.Max(max.Z, position.Z))
                    : position;
            }
        }
        if(minimum is not BlockPosition lower || maximum is not BlockPosition upper)
            return new BlockPosition(0, 64, 0);
        int x = checked((int)(((long)lower.X + upper.X) / 2));
        int z = checked((int)(((long)lower.Z + upper.Z) / 2));
        int y = upper.Y == int.MaxValue ? upper.Y : upper.Y + 1;
        return new BlockPosition(x, y, z);
    }

    private static BlockPosition FitSpawnToTarget(BlockPosition sourceSpawn, int yOffset)
    {
        long translated = (long)sourceSpawn.Y + yOffset;
        int targetY = (int)Math.Clamp(translated, MinecraftTargetProfile.Java1122.BuildRange.Minimum, MinecraftTargetProfile.Java1122.BuildRange.Maximum);
        return sourceSpawn with { Y = checked(targetY - yOffset) };
    }

    private static int CountChunks(IEnumerable<NormalizedMinecraftSection> sections) => sections
        .Select(static section => (section.Coordinate.X, section.Coordinate.Z))
        .Distinct()
        .Count();

    private static int CountChunks(SceneDelta delta) => delta.Sections
        .Select(static section => (section.Section.X, section.Section.Z))
        .Distinct()
        .Count();

    private static bool IsProtectedSourceFile(string candidate, string? sourcePath) =>
        sourcePath is not null && File.Exists(sourcePath) &&
        string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase);

    private static string SanitizeExportName(string? value)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        string result = new((value ?? string.Empty)
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        result = result.TrimEnd('.', ' ');
        if(result.Length == 0) result = "Minecraft-Scene";
        return result.Length <= 80 ? result : result[..80].TrimEnd('.', ' ');
    }

    private void StartConvertedWorldPreview(
        IReadOnlyMinecraftWorld world,
        IMinecraftDowngradePreview preview,
        MinecraftDimensionId dimension,
        int totalChunkCount,
        string summaryText)
    {
        if(isClosing || activeWorldPreviewTask is not null || activeNavigationPreviewTask is not null) return;
        IReadOnlyList<MinecraftChunkAddress>? addresses = null;
        MinecraftChunkBounds? bounds = null;
        if(totalChunkCount > CurrentMaximumLoadedChunkCount)
        {
            Vector3 worldTarget = FromPreviewRenderPosition(Viewport.CameraTarget);
            BlockPosition focus = WorldPreviewNavigation.TryCreateBlockPosition(worldTarget, out BlockPosition current)
                ? current
                : CurrentFocus(world.Descriptor);
            bounds = WorldPreviewNavigation.CreateBounds(focus, CurrentLoadedChunkRadius);
            addresses = WorldPreviewNavigation.CreateCenterFirstAddresses(
                dimension,
                focus,
                CurrentLoadedChunkRadius);
        }
        int generation = BeginPreviewGeneration();
        ResetWorldSectionWindow(DisplayedContent.ConvertedWorldSections);
        CancellationTokenSource requestCancellation = new();
        worldPreviewCancellation = requestCancellation;
        Task<WholeWorldPreviewResult> task = StreamConvertedWorldPreviewAsync(
            world,
            preview,
            dimension,
            totalChunkCount,
            summaryText,
            addresses,
            generation,
            requestCancellation.Token);
        activeWorldPreviewTask = task;
        UpdateConversionControls();
        _ = CompleteConvertedWorldPreviewAsync(
            world,
            preview,
            dimension,
            task,
            requestCancellation,
            summaryText,
            bounds,
            generation);
    }

    private Task<WholeWorldPreviewResult> StreamConvertedWorldPreviewAsync(
        IReadOnlyMinecraftWorld world,
        IMinecraftDowngradePreview preview,
        MinecraftDimensionId dimension,
        int totalChunkCount,
        string summaryText,
        IReadOnlyList<MinecraftChunkAddress>? addresses,
        int generation,
        CancellationToken cancellationToken)
    {
        int loadConcurrency = CurrentChunkLoadConcurrency;
        return Task.Run(
            async () =>
            {
                IReadOnlyList<MinecraftChunkAddress> requestedAddresses = addresses ??
                    await CollectChunkAddressesAsync(
                        world.ChunkIndex,
                        dimension,
                        cancellationToken).ConfigureAwait(false);
                int expectedChunkCount = requestedAddresses.Count;
                int processedChunkCount = 0;
                WorldPreviewStatistics statistics = WorldPreviewStatistics.Empty;
                IAsyncEnumerable<NormalizedMinecraftChunkBatch> batches =
                    NormalizedMinecraftChunkBatcher.EnumerateAddressesAsync(
                        preview.ConvertedChunks,
                        requestedAddresses,
                        batchSize: ProgressiveWorldPreviewBatchSize,
                        loadConcurrency: loadConcurrency,
                        cancellationToken: cancellationToken);
                await foreach(NormalizedMinecraftChunkBatch batch in batches.WithCancellation(cancellationToken))
                {
                    processedChunkCount = batch.ProcessedChunkCount;
                    statistics = batch.ProcessedStatistics;
                    await Viewport.ReplaceSectionsAsync(batch.Sections, cancellationToken).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(
                        () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if(!IsPreviewGenerationCurrent(generation) ||
                               !ReferenceEquals(world, mountedWorld) ||
                               !ReferenceEquals(preview, activeConversionPreview) ||
                               dimension != displayedWorldDimension ||
                               displayedContent != DisplayedContent.ConvertedWorldSections)
                                throw new OperationCanceledException(cancellationToken);
                            ReplaceWorldSections(batch.Sections);
                            ReplaceWorldChunkStatistics(batch.ChunkStatistics);
                            ShowWorldSceneStatistics(batch.ProcessedStatistics, totalChunkCount, converted: true);
                            UiText.Bind(StatusText, "Text", UiText.Message($"正在载入转换预览：{batch.ProcessedChunkCount:N0} / {expectedChunkCount:N0} 个 Chunk。{summaryText}。"));
                        },
                        System.Windows.Threading.DispatcherPriority.Normal,
                        cancellationToken);
                }
                return new WholeWorldPreviewResult(processedChunkCount, statistics);
            },
            cancellationToken);
    }

    private async Task CompleteConvertedWorldPreviewAsync(
        IReadOnlyMinecraftWorld world,
        IMinecraftDowngradePreview preview,
        MinecraftDimensionId dimension,
        Task<WholeWorldPreviewResult> task,
        CancellationTokenSource requestCancellation,
        string summaryText,
        MinecraftChunkBounds? bounds,
        int generation)
    {
        try
        {
            WholeWorldPreviewResult result = await task;
            if(IsPreviewGenerationCurrent(generation) && !isClosing && ReferenceEquals(world, mountedWorld) &&
               ReferenceEquals(preview, activeConversionPreview) &&
               dimension == displayedWorldDimension &&
               displayedContent == DisplayedContent.ConvertedWorldSections)
            {
                navigationPreviewCoverage = bounds;
                UiText.Bind(StatusText, "Text", UiText.Message($"1.12.2 转换预览已载入 {result.ChunkCount:N0} 个 Chunk / {displayedWorldSections.Count:N0} 个 Section。{summaryText}。"));
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception exception)
        {
            if(IsPreviewGenerationCurrent(generation) && !isClosing && ReferenceEquals(world, mountedWorld) &&
               ReferenceEquals(preview, activeConversionPreview) &&
               dimension == displayedWorldDimension)
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"转换预览载入在已显示内容处停止：{exception.Message}"));
            }
        }
        finally
        {
            if(ReferenceEquals(activeWorldPreviewTask, task)) activeWorldPreviewTask = null;
            if(ReferenceEquals(worldPreviewCancellation, requestCancellation))
            {
                worldPreviewCancellation = null;
                requestCancellation.Dispose();
            }
            UpdateConversionControls();
            if(IsPreviewGenerationCurrent(generation)) ReconcileNavigationPreviewScheduling();
        }
    }

    private async Task CancelConversionPreviewAsync(bool invalidateCachedPreview = true)
    {
        await CancelComparisonAsync();
        CancellationTokenSource? cancellation = conversionPreviewCancellation;
        Task? workflow = activeConversionWorkflowTask;
        Task<IMinecraftDowngradePreview>? scanTask = activeConversionPreviewTask;
        cancellation?.Cancel();
        Task? taskToAwait = workflow ?? scanTask;
        if(taskToAwait is not null)
        {
            try
            {
                await taskToAwait;
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(invalidateCachedPreview)
        {
            activeConversionPreview = null;
            await conversionPreviewCache.InvalidateAsync();
        }
        if(ReferenceEquals(activeConversionWorkflowTask, workflow)) activeConversionWorkflowTask = null;
        if(ReferenceEquals(activeConversionPreviewTask, scanTask)) activeConversionPreviewTask = null;
        if(workflow is null && ReferenceEquals(conversionPreviewCancellation, cancellation))
        {
            conversionPreviewCancellation = null;
            cancellation?.Dispose();
        }
        UpdateConversionControls();
    }

    private void InvalidateConversionPreviewCache() => conversionPreviewCache.MarkStale();

    private async Task CancelWorldExportAsync()
    {
        object? exportOwner = activeWorldExportOwner;
        CancellationTokenSource? cancellation = worldExportCancellation;
        Task? task = activeWorldExportTask;
        cancellation?.Cancel();
        if(task is not null)
        {
            try
            {
                await task;
            }
            catch(OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if(ReferenceEquals(activeWorldExportTask, task)) activeWorldExportTask = null;
        if(ReferenceEquals(worldExportCancellation, cancellation))
        {
            worldExportCancellation = null;
            cancellation?.Dispose();
        }
        if(exportOwner is not null) EndWorldExport(exportOwner);
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        if(!isWorldExporting || isBlockReplacementCommitCritical) return;
        CancelExportButton.IsEnabled = false;
        UiText.Bind(StatusText, "Text", UiText.Text("正在取消导出；临时目标会自动清理……"));
        UpdateWorldExportLoadingIndeterminate("正在取消导出", "正在清理未完成的临时目标");
        worldExportCancellation?.Cancel();
    }

    private void BeginWorldExportLoading(string stage, string detail)
    {
        WorldExportLoadingOverlay.BeginAnimation(OpacityProperty, null);
        WorldExportLoadingOverlay.Opacity = 1d;
        WorldExportLoadingOverlay.IsHitTestVisible = true;
        WorldExportLoadingOverlay.Visibility = Visibility.Visible;
        UpdateWorldExportLoadingIndeterminate(stage, detail);
    }

    private void UpdateWorldExportLoadingIndeterminate(string stage, string detail)
    {
        if(!isWorldExporting) return;
        WorldExportLoadingStageText.Text = stage;
        UiText.Bind(WorldExportLoadingProgressText, "Text", UiText.Text("处理中"));
        WorldExportLoadingDetailText.Text = detail;
        WorldExportLoadingProgressBar.IsIndeterminate = true;
        WorldExportLoadingProgressBar.Value = 0d;
    }

    private void UpdateWorldExportLoading(string stage, long completedItems, long totalItems, string detail)
    {
        if(!isWorldExporting) return;
        long total = Math.Max(0, totalItems);
        long completed = Math.Clamp(completedItems, 0, total);
        WorldExportLoadingStageText.Text = stage;
        WorldExportLoadingProgressText.Text = total == 0
            ? $"{Math.Max(0, completedItems):N0}"
            : $"{completed:N0} / {total:N0}";
        WorldExportLoadingDetailText.Text = detail;
        WorldExportLoadingProgressBar.IsIndeterminate = total == 0;
        WorldExportLoadingProgressBar.Value = total == 0 ? 0d : completed * 100d / total;
    }

    private void UpdateWorldExportLoading(MinecraftWorldExportProgress progress, int totalChunks)
    {
        if(progress.Stage == MinecraftWorldExportStage.Complete)
        {
            UpdateWorldExportLoading(
                UiText.Get("正在完成世界导出"),
                totalChunks,
                totalChunks,
                UiText.Format($"已写入 {progress.ChunksWritten:N0} 个 Chunk / {progress.RegionsWritten:N0} 个 Region"));
            return;
        }
        if(progress.Stage == MinecraftWorldExportStage.WritingRegions)
        {
            UpdateWorldExportLoading(
                UiText.Get("正在写入世界区块"),
                progress.ChunksWritten,
                totalChunks,
                UiText.Format($"已完成 {progress.RegionsWritten:N0} 个 Region"));
            return;
        }
        UpdateWorldExportLoadingIndeterminate(
            progress.Message,
            $"已写入 {progress.ChunksWritten:N0} 个 Chunk / {progress.RegionsWritten:N0} 个 Region");
    }

    private void EndWorldExportLoading(string finalStage, bool completed)
    {
        WorldExportLoadingOverlay.IsHitTestVisible = false;
        WorldExportLoadingStageText.Text = finalStage;
        WorldExportLoadingProgressText.Text = completed ? "100%" : finalStage.Replace(UiText.Get("世界"), string.Empty);
        UiText.Bind(WorldExportLoadingDetailText, "Text", completed ? UiText.Text("目标世界文件夹已生成") : UiText.Text("请查看窗口左下角的详细信息"));
        WorldExportLoadingProgressBar.IsIndeterminate = false;
        if(completed) WorldExportLoadingProgressBar.Value = 100d;
        DoubleAnimation fade = new(
            WorldExportLoadingOverlay.Opacity,
            0d,
            TimeSpan.FromMilliseconds(220))
        {
            BeginTime = TimeSpan.FromMilliseconds(completed ? 480 : 220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            if(isWorldExporting) return;
            WorldExportLoadingOverlay.Visibility = Visibility.Collapsed;
            WorldExportLoadingOverlay.Opacity = 1d;
        };
        WorldExportLoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private static string FormatWorldExportProgress(MinecraftWorldExportProgress progress, int totalChunks)
    {
        if(progress.Stage == MinecraftWorldExportStage.WritingRegions)
        {
            return UiText.Format($"正在导出世界：{progress.ChunksWritten:N0} / {totalChunks:N0} 个 Chunk · {progress.RegionsWritten:N0} 个 Region。");
        }
        return UiText.Format($"{progress.Message} · 已写入 {progress.ChunksWritten:N0} 个 Chunk / {progress.RegionsWritten:N0} 个 Region。");
    }

    private static string FormatWorldCloneProgress(MinecraftWorldCloneProgress progress) =>
        progress.Stage == MinecraftWorldCloneStage.CopyingFiles
            ? UiText.Format($"正在复制原版本世界：{progress.FilesCopied:N0} 个文件 / {progress.BytesCopied:N0} 字节……")
            : $"{progress.Message}……";

    private void ShowWorldFolderExportResult(ExportSourceContext context, WorldFolderExportOutcome outcome)
    {
        ExportDiagnosticPresentation diagnostics = PresentExportDiagnostics(outcome.Diagnostics);
        if(outcome.CloneResult is MinecraftWorldCloneResult clone)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"原版本受保护副本导出完成：{clone.FileCount:N0} 个文件 / {clone.ByteCount:N0} 字节；仅重写保护规则与区块调度刻，原挂载世界未改动{diagnostics.StatusSuffix}。"));
            string version = context.World is null
                ? "原版本"
                : FormatWorldVersion(context.World.Descriptor.Version);
            StatusText.ToolTip = BuildExportToolTip(
                UiText.Format($"{clone.OutputDirectory}{Environment.NewLine}导出方式：验证后的 {version} 受保护副本（session.lock 不复制）"),
                diagnostics);
            return;
        }

        MinecraftWorldExportResult result = outcome.ReencodedResult ??
                                             throw new InvalidOperationException("世界导出结果缺少重编码报告。");
        string offset = outcome.AppliedYOffset >= 0
            ? $"+{outcome.AppliedYOffset}"
            : outcome.AppliedYOffset.ToString();
        string customDimensions = context.SkippedCustomDimensionCount == 0
            ? string.Empty
            : $"；跳过 {context.SkippedCustomDimensionCount:N0} 个 1.12.2 不支持的自定义维度";
        string entityNotice = result.Diagnostics.Any(static item =>
            item.Code.Equals("export.entities.empty", StringComparison.Ordinal))
            ? "；普通实体事实层不可用"
            : string.Empty;
        UiText.Bind(StatusText, "Text", UiText.Message($"世界重编码导出完成：{result.ChunkCount:N0} 个 Chunk / {result.RegionCount:N0} 个 Region，Y {offset}{customDimensions}{entityNotice}{diagnostics.StatusSuffix}。"));
        StatusText.ToolTip = BuildExportToolTip(
            UiText.Format($"{result.OutputDirectory}{Environment.NewLine}导出方式：规范化后重编码为 Java 1.12.2"),
            diagnostics);
    }

    private static ExportDiagnosticPresentation PresentExportDiagnostics(
        IReadOnlyList<MinecraftDiagnostic> diagnostics)
    {
        if(diagnostics.Count == 0) return new ExportDiagnosticPresentation(string.Empty, string.Empty);
        var groups = diagnostics
            .GroupBy(static item => (item.Code, item.Severity))
            .OrderByDescending(static group => group.Key.Severity)
            .ThenBy(static group => group.Key.Code, StringComparer.Ordinal)
            .ToArray();
        int warningKinds = groups.Count(static group => group.Key.Severity != MinecraftDiagnosticSeverity.Information);
        int informationKinds = groups.Length - warningKinds;
        string suffix = warningKinds > 0
            ? $"；诊断 {warningKinds:N0} 类警告 / {informationKinds:N0} 类提示"
            : $"；诊断 {informationKinds:N0} 类提示";
        string details = string.Join(
            Environment.NewLine,
            groups.Select(group =>
                $"[{group.Key.Severity}] {group.First().Message}" +
                (group.Count() > 1 ? $"（{group.Count():N0} 处）" : string.Empty)));
        return new ExportDiagnosticPresentation(suffix, details);
    }

    private static string BuildExportToolTip(string outputPath, ExportDiagnosticPresentation diagnostics) =>
        diagnostics.Details.Length == 0
            ? outputPath
            : $"{outputPath}{Environment.NewLine}{Environment.NewLine}{diagnostics.Details}";

    private async Task<string> ResolveConversionResourceRevisionAsync(
        MinecraftWorldDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if(IsSolidMaterialMode()) return "solid-performance/v1";
        MinecraftResourceSelectionResult located = await minecraftResourceCacheService.ResolveAsync(
            descriptor.RootPath,
            MinecraftTargetProfile.Java1122.Version,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if(!located.IsAvailable || string.IsNullOrWhiteSpace(located.ClientJarPath))
            return "minecraft-1.12.2/unavailable";

        string path = Path.GetFullPath(located.ClientJarPath);
        FileInfo file = new(path);
        return $"minecraft-1.12.2:{path.ToUpperInvariant()}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }

    private async Task<string> ConfigureConversionResourcesAsync(
        MinecraftWorldDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        // Keep the app-level Section snapshot intact until the converted stream commits. The viewport cache must be
        // empty before swapping resolvers; otherwise VoxelViewport deliberately rejects the resource transition.
        Viewport.ClearSections();
        activeResourceSelection = null;
        if(IsSolidMaterialMode())
        {
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            return UiText.Get("纯色性能模式已跳过 1.12.2 材质解析");
        }

        MinecraftResourceSelectionResult located = await minecraftResourceCacheService.ResolveAsync(
            descriptor.RootPath,
            MinecraftTargetProfile.Java1122.Version,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if(!located.IsAvailable)
        {
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            return located.Status.TrimEnd('。');
        }

        ResourceSelection selection = new(located.ClientJarPath!, []);
        try
        {
            SetMaterialLoadingStage(UiText.Get("正在后台索引 Minecraft 1.12.2 材质"));
            await Viewport.ConfigureMinecraft1122ResourcesAsync(
                CreateRendererConfiguration(selection),
                cancellationToken);
            activeResourceSelection = selection;
            UpdateRenderInfoText();
            return located.Status.TrimEnd('。');
        }
        catch(Exception exception) when(IsRecoverableResourceException(exception))
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            UpdateRenderInfoText();
            return UiText.Get("1.12.2 资源不可用，已回退纯色性能模式");
        }
    }

    private void RestoreRetainedSectionSnapshot(
        IReadOnlyList<NormalizedMinecraftSection> sections,
        ResourceSelection? resources)
    {
        Viewport.ClearSections();
        try
        {
            if(IsSolidMaterialMode())
            {
                Viewport.ClearMinecraftResources();
                activeResourceSelection = resources;
            }
            else if(resources is not null)
            {
                Viewport.ConfigureMinecraft1122Resources(CreateRendererConfiguration(resources));
                activeResourceSelection = resources;
            }
            else
            {
                Viewport.ClearMinecraftResources();
                activeResourceSelection = null;
            }
        }
        catch(Exception exception) when(IsRecoverableResourceException(exception))
        {
            Viewport.ClearSections();
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
        }

        if(sections.Count > 0) Viewport.LoadSections(sections);
        UpdateRenderInfoText();
    }

    private async Task<string> RestoreOriginalWorldResourcesAsync(
        MinecraftWorldDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        Viewport.ClearSections();
        ResourceSelection? selection = originalWorldResourceSelection;
        if(IsSolidMaterialMode())
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = selection;
            UpdateRenderInfoText();
            return UiText.Get("已恢复原始地图的纯色性能预览");
        }

        if(selection is null)
        {
            MinecraftResourceSelectionResult located = await minecraftResourceCacheService.ResolveAsync(
                descriptor.RootPath,
                descriptor.Version,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if(located.IsAvailable)
            {
                selection = new ResourceSelection(
                    located.ClientJarPath!,
                    FindAutomaticWorldResourcePacks(descriptor.RootPath));
            }
        }
        if(selection is null)
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            UpdateRenderInfoText();
            return UiText.Get("原始版本材质不可用，已回退纯色方块");
        }

        try
        {
            SetMaterialLoadingStage(UiText.Get("正在后台索引原始地图材质"));
            await Viewport.ConfigureMinecraft1122ResourcesAsync(
                CreateRendererConfiguration(selection),
                cancellationToken);
            activeResourceSelection = selection;
            UpdateRenderInfoText();
            return UiText.Get("已恢复原始地图的 Minecraft 材质");
        }
        catch(Exception exception) when(IsRecoverableResourceException(exception))
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            UpdateRenderInfoText();
            return UiText.Get("原始资源不可用，已回退纯色方块");
        }
    }

    private BlockPosition CurrentFocus(MinecraftWorldDescriptor descriptor)
    {
        if(int.TryParse(XCoordinate.Text, out int x) &&
           int.TryParse(YCoordinate.Text, out int y) &&
           int.TryParse(ZCoordinate.Text, out int z))
        {
            return new BlockPosition(x, y, z);
        }
        return descriptor.SpawnLocation ?? new BlockPosition(0, 64, 0);
    }

    private void TimeOfDaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ClearPhotographyLightPreset();
        double normalizedHours = e.NewValue >= 24d ? 0d : e.NewValue;
        int totalMinutes = (int)Math.Round(normalizedHours * 60d, MidpointRounding.AwayFromZero) % (24 * 60);
        if(TimeOfDayText is not null) TimeOfDayText.Text = $"{totalMinutes / 60:00}:{totalMinutes % 60:00}";
        if(isUiReady) Viewport.SetTimeOfDay((float)e.NewValue);
    }

    private void CloudsToggle_Click(object sender, RoutedEventArgs e)
    {
        if(!isUiReady) return;
        bool enabled = CloudsToggle.IsChecked == true;
        Viewport.SetCloudsEnabled(enabled);
        UiText.Bind(StatusText, "Text", !enabled ? UiText.Text("云层已关闭，点击“云层”可重新显示。") : Viewport.EnhancedLightingEnabled ? UiText.Text("云层已开启：静态云景。") : UiText.Text("云层已开启：经典方块云。"));
    }

    private void RainToggle_Click(object sender, RoutedEventArgs e)
    {
        if(isUiReady) Viewport.SetRainEnabled(RainToggle.IsChecked == true);
    }

    private void FogToggle_Click(object sender, RoutedEventArgs e)
    {
        if(isUiReady) Viewport.SetFogEnabled(FogToggle.IsChecked == true);
    }

    private void KeyboardMoveSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int speed = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        if(KeyboardMoveSpeedText is not null) UiText.Bind(KeyboardMoveSpeedText, "Text", UiText.Message($"{speed:N0} 方块/秒"));
        if(isUiReady) Viewport.KeyboardMovementSpeed = speed;
    }

    private int CurrentLoadedChunkRadius => Math.Clamp(
        (int)Math.Round(LoadedChunkRadiusSlider.Value, MidpointRounding.AwayFromZero),
        WorldPreviewNavigation.MinimumRadiusChunks,
        WorldPreviewNavigation.MaximumRadiusChunks);

    private int CurrentMaximumLoadedChunkCount => WorldPreviewNavigation.ChunkCountForRadius(CurrentLoadedChunkRadius);

    private int CurrentChunkLoadConcurrency => Math.Clamp(
        (int)Math.Round(ChunkLoadConcurrencySlider.Value, MidpointRounding.AwayFromZero),
        NormalizedMinecraftChunkBatcher.MinimumLoadConcurrency,
        NormalizedMinecraftChunkBatcher.MaximumLoadConcurrency);

    private void ChunkLoadConcurrencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int concurrency = Math.Clamp(
            (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero),
            NormalizedMinecraftChunkBatcher.MinimumLoadConcurrency,
            NormalizedMinecraftChunkBatcher.MaximumLoadConcurrency);
        if(ChunkLoadConcurrencyText is not null) ChunkLoadConcurrencyText.Text = concurrency.ToString("N0");
    }

    private void LoadedChunkRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int radius = Math.Clamp(
            (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero),
            WorldPreviewNavigation.MinimumRadiusChunks,
            WorldPreviewNavigation.MaximumRadiusChunks);
        int chunkCount = WorldPreviewNavigation.ChunkCountForRadius(radius);
        if(LoadedChunkCountText is not null) LoadedChunkCountText.Text = chunkCount.ToString("N0");
        if(Viewport is not null)
        {
            Viewport.MaximumActiveSections = checked(chunkCount * MaximumRenderableSectionsPerChunk);
            Viewport.SectionDrawDistance = Math.Max(384f, radius * 24f);
        }
        if(!isUiReady || isClosing || mountedWorld is null) return;
        if(mountCancellation is null && activeMountTask is null &&
           displayedContent is DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections)
        {
            BeginPreviewGeneration();
            worldPreviewCancellation?.Cancel();
            navigationPreviewCancellation?.Cancel();
            if(CountChunks(displayedWorldSections.Values) > chunkCount)
                ResetWorldSectionWindow(displayedContent);
        }
        chunkLoadReloadTimer.Stop();
        chunkLoadReloadTimer.Start();
    }

    private async void ChunkLoadReloadTimer_Tick(object? sender, EventArgs e)
    {
        chunkLoadReloadTimer.Stop();
        if(!sourceActionsEnabled || mountCancellation is not null || activeMountTask is not null ||
           isChangingMaterialMode || isNavigationJumping)
        {
            if(!isClosing) chunkLoadReloadTimer.Start();
            return;
        }
        IReadOnlyMinecraftWorld? world = mountedWorld;
        DisplayedContent content = displayedContent;
        IMinecraftDowngradePreview? conversion = activeConversionPreview;
        MinecraftDimensionId dimension = displayedWorldDimension;
        if(world is null || content is not (DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections)) return;

        int generation = BeginPreviewGeneration();
        worldPreviewCancellation?.Cancel();
        navigationPreviewCancellation?.Cancel();
        await CancelWorldPreviewAsync();
        if(!IsPreviewGenerationCurrent(generation) || isClosing || !ReferenceEquals(world, mountedWorld) ||
           content != displayedContent || dimension != displayedWorldDimension) return;
        int totalChunkCount = world.ChunkIndex.GetChunkCount(dimension);
        if(content == DisplayedContent.WorldSections)
        {
            if(totalChunkCount <= CurrentMaximumLoadedChunkCount)
            {
                string version = world.Descriptor.Version.VersionName ?? "原始版本";
                StartWholeWorldPreview(world, dimension, totalChunkCount, version);
                return;
            }
            if(WorldPreviewNavigation.TryCreateBlockPosition(Viewport.CameraTarget, out BlockPosition focus))
                StartNavigationPreview(world, dimension, focus);
            return;
        }

        if(conversion is null || !ReferenceEquals(conversion, activeConversionPreview)) return;
        string summary = Minecraft1122ConversionPreviewWorkflow.FormatSummary(conversion.Summary);
        StartConvertedWorldPreview(world, conversion, dimension, totalChunkCount, summary);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int percentage = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        if(ZoomLevelText is not null) ZoomLevelText.Text = $"{percentage}%";
        if(!isUiReady || isSynchronizingZoomSlider) return;
        if(Viewport.NavigationMode == ViewportNavigationMode.Observer)
        {
            Viewport.ObserverMouseSensitivity = (float)(e.NewValue / 100d);
        }
        else
        {
            Viewport.SetZoomTargetDistance(ZoomPercentageToDistance(e.NewValue));
        }
    }

    private void SynchronizeZoomSlider(float distance)
    {
        if(ZoomSlider is null || ZoomLevelText is null || ViewControlTitleText is null) return;
        isSynchronizingZoomSlider = true;
        try
        {
            UiText.Bind(ViewControlTitleText, "Text", UiText.Text("缩放"));
            ZoomSlider.Minimum = 0d;
            ZoomSlider.Maximum = 100d;
            ZoomSlider.TickFrequency = 1d;
            ZoomSlider.IsSnapToTickEnabled = false;
            double percentage = ZoomDistanceToPercentage(distance);
            ZoomSlider.Value = percentage;
            ZoomLevelText.Text = $"{(int)Math.Round(percentage, MidpointRounding.AwayFromZero)}%";
        }
        finally
        {
            isSynchronizingZoomSlider = false;
        }
    }

    private void SynchronizeViewControlSlider(ViewportNavigationMode mode)
    {
        if(mode == ViewportNavigationMode.Orbit)
        {
            SynchronizeZoomSlider(Viewport.ZoomTargetDistance);
            return;
        }
        if(ZoomSlider is null || ZoomLevelText is null || ViewControlTitleText is null) return;

        isSynchronizingZoomSlider = true;
        try
        {
            UiText.Bind(ViewControlTitleText, "Text", UiText.Text("鼠标速度"));
            ZoomSlider.Minimum = VoxelViewport.MinimumObserverMouseSensitivity * 100d;
            ZoomSlider.Maximum = VoxelViewport.MaximumObserverMouseSensitivity * 100d;
            ZoomSlider.TickFrequency = 5d;
            ZoomSlider.IsSnapToTickEnabled = false;
            double percentage = Viewport.ObserverMouseSensitivity * 100d;
            ZoomSlider.Value = percentage;
            ZoomLevelText.Text = $"{(int)Math.Round(percentage, MidpointRounding.AwayFromZero)}%";
        }
        finally
        {
            isSynchronizingZoomSlider = false;
        }
    }

    private static float ZoomPercentageToDistance(double percentage)
    {
        double normalized = Math.Clamp(percentage / 100d, 0d, 1d);
        double range = MaximumZoomDistance / MinimumZoomDistance;
        return (float)(MaximumZoomDistance / Math.Pow(range, normalized));
    }

    private static double ZoomDistanceToPercentage(float distance)
    {
        double clamped = Math.Clamp(distance, MinimumZoomDistance, MaximumZoomDistance);
        double range = MaximumZoomDistance / MinimumZoomDistance;
        return Math.Log(MaximumZoomDistance / clamped) / Math.Log(range) * 100d;
    }

    private void NavigationMode_Checked(object sender, RoutedEventArgs e)
    {
        if(!isUiReady) return;
        Viewport.SetNavigationMode(ObserverPreviewModeChoice.IsChecked == true
            ? ViewportNavigationMode.Observer
            : ViewportNavigationMode.Orbit);
    }

    private void TerrainMode_Checked(object sender, RoutedEventArgs e)
    {
        if(!isUiReady) return;
        Viewport.SetTerrainMode(SelectedTerrainMode());
    }

    private void BeginWorldPreviewLoading(int generation, string stage)
    {
        worldPreviewLoadingOwnerGeneration = generation;
        worldPreviewLoadingStage = stage;
        worldPreviewLoadingProgressText = "准备中";
        worldPreviewLoadingIndeterminate = true;
        worldPreviewLoadingProgress = 0d;
        RefreshOperationDisplay();
    }

    private void UpdateWorldPreviewLoading(int generation, string stage, int completedItems, int totalItems)
    {
        if(worldPreviewLoadingOwnerGeneration != generation) return;
        int total = Math.Max(0, totalItems);
        int completed = Math.Clamp(completedItems, 0, total);
        int percentage = total == 0
            ? 100
            : (int)Math.Round(completed * 100d / total, MidpointRounding.AwayFromZero);
        worldPreviewLoadingStage = stage;
        worldPreviewLoadingProgressText = total == 0
            ? "无需读取"
            : $"{completed:N0} / {total:N0}";
        worldPreviewLoadingIndeterminate = false;
        worldPreviewLoadingProgress = percentage;
    }

    private void UpdateWorldPreviewLoadingIndeterminate(int generation, string stage)
    {
        if(worldPreviewLoadingOwnerGeneration != generation) return;
        worldPreviewLoadingStage = stage;
        worldPreviewLoadingProgressText = "准备中";
        worldPreviewLoadingIndeterminate = true;
        worldPreviewLoadingProgress = 0d;
    }

    private void UpdateWorldMountLoading(int generation, MinecraftWorldMountProgress progress)
    {
        string stage = progress.Stage switch
        {
            MinecraftWorldMountStage.DiscoveringFiles => UiText.Get("正在扫描世界文件"),
            MinecraftWorldMountStage.FingerprintingFiles => UiText.Get("正在校验世界文件"),
            MinecraftWorldMountStage.ReadingLevelMetadata => UiText.Get("正在读取世界信息"),
            MinecraftWorldMountStage.DiscoveringRegions => UiText.Get("正在查找 Region 文件"),
            MinecraftWorldMountStage.IndexingRegions => UiText.Get("正在建立区块索引"),
            MinecraftWorldMountStage.Completed => UiText.Get("正在准备首屏"),
            _ => UiText.Get("正在打开世界"),
        };
        if(progress.TotalItems > 0 &&
           progress.Stage is MinecraftWorldMountStage.FingerprintingFiles or MinecraftWorldMountStage.IndexingRegions)
        {
            UpdateWorldPreviewLoading(generation, stage, progress.CompletedItems, progress.TotalItems);
            return;
        }
        UpdateWorldPreviewLoadingIndeterminate(generation, stage);
    }

    private void EndWorldPreviewLoading(int generation)
    {
        if(worldPreviewLoadingOwnerGeneration != generation) return;
        EndWorldValidation(generation);
        worldPreviewLoadingOwnerGeneration = null;
        worldPreviewLoadingIndeterminate = false;
        RefreshOperationDisplay();
    }

    private void EndWorldValidation(int generation)
    {
        if(worldValidationOwnerGeneration != generation) return;
        worldValidationOwnerGeneration = null;
        RefreshOperationDisplay();
    }

    private void BeginMaterialLoading(string stage)
    {
        MaterialLoadingOverlay.Visibility = Visibility.Visible;
        SetMaterialLoadingStage(stage);
    }

    private void SetMaterialLoadingStage(string stage)
    {
        UiText.Bind(MaterialLoadingStageText, "Text", UiText.Text(stage));
        UiText.Bind(MaterialLoadingPercentText, "Text", UiText.Text("准备中"));
        MaterialLoadingProgressBar.IsIndeterminate = true;
        MaterialLoadingProgressBar.Value = 0d;
    }

    private IProgress<SectionMeshBuildProgress> CreateMaterialSectionProgress(Func<bool>? isCurrent = null) =>
        new Progress<SectionMeshBuildProgress>(value =>
        {
            if(isClosing || isCurrent is not null && !isCurrent()) return;
            int total = Math.Max(0, value.TotalSections);
            int completed = Math.Clamp(value.CompletedSections, 0, total);
            int percentage = total == 0
                ? 100
                : (int)Math.Round(completed * 100d / total, MidpointRounding.AwayFromZero);
            UiText.Bind(MaterialLoadingStageText, "Text", total == 0 ? UiText.Text("无需重建 Section 网格") : UiText.Message($"正在重建 Section 网格 · {completed:N0} / {total:N0}"));
            MaterialLoadingPercentText.Text = $"{percentage}%";
            MaterialLoadingProgressBar.IsIndeterminate = false;
            MaterialLoadingProgressBar.Value = percentage;
            UpdateAiBatchLoadingProgress("正在重建施工批次 Section", completed, total);
        });

    private void EndMaterialLoading()
    {
        MaterialLoadingProgressBar.IsIndeterminate = false;
        MaterialLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private async void MaterialMode_Checked(object sender, RoutedEventArgs e)
        => await RefreshMaterialModeAsync();

    private async Task RefreshMaterialModeAsync()
    {
        UpdateRenderInfoText();
        if(!isUiReady || isChangingMaterialMode || isNavigationJumping) return;
        chunkLoadReloadTimer.Stop();

        DisplayedContent content = displayedContent;
        IReadOnlyMinecraftWorld? world = mountedWorld;
        IMinecraftDowngradePreview? conversion = activeConversionPreview;
        LegacySchematicImport? schematic = displayedSchematic;
        IReadOnlyList<NormalizedMinecraftSection> planSections = displayedPlanSections;
        MinecraftVersionDescriptor? planVersion = displayedPlanVisualVersion;
        string? planSourcePath = displayedPlanSourcePath;
        bool planProfileInferred = displayedPlanVisualProfileInferred;
        MinecraftDimensionId dimension = displayedWorldDimension;
        bool resumeStreaming = activeWorldPreviewTask is not null;
        CancellationTokenSource requestCancellation = new();
        materialModeCancellation = requestCancellation;
        isChangingMaterialMode = true;
        bool resourceTransitionStarted = false;
        SolidMaterialChoice.IsEnabled = false;
        MinecraftMaterialChoice.IsEnabled = false;
        SetSourceOpenButtonsEnabled(sourceActionsEnabled);
        SetCoordinateJumpButtonsEnabled(false);
        BeginMaterialLoading(UiText.Get("正在准备切换材质"));

        try
        {
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            await CancelWorldPreviewAsync();
            if(isClosing || content != displayedContent || !ReferenceEquals(world, mountedWorld) ||
               !ReferenceEquals(schematic, displayedSchematic) ||
               content == DisplayedContent.PlanPreview && !ReferenceEquals(planSections, displayedPlanSections))
                return;

            // The home scene is renderer-owned geometry, not an empty world Section snapshot.
            if(content == DisplayedContent.None)
            {
                string homeResourceStatus = await ConfigureHomeResourcesAsync(requestCancellation.Token);
                UiText.Bind(StatusText, "Text", UiText.Text(homeResourceStatus));
                return;
            }

            IReadOnlyList<NormalizedMinecraftSection> sections = content switch
            {
                DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections =>
                    displayedWorldSections.Values.ToArray(),
                DisplayedContent.SchematicSections when schematic is not null => schematic.Sections,
                DisplayedContent.PlanPreview => planSections,
                _ => [],
            };

            Viewport.BeginResourceTransition();
            resourceTransitionStarted = true;
            Viewport.ClearSections();
            string resourceStatus;
            if(IsSolidMaterialMode())
            {
                SetMaterialLoadingStage("正在切换到纯色性能材质");
                Viewport.ClearMinecraftResources();
                resourceStatus = "纯色性能模式已启用：已跳过纹理图集、材质包和复杂方块模型解析";
            }
            else
            {
                SetMaterialLoadingStage("正在查找匹配的 Minecraft 材质");
                resourceStatus = content switch
                {
                    DisplayedContent.WorldSections when world is not null =>
                        await ConfigureAutomaticWorldResourcesAsync(world.Descriptor, requestCancellation.Token),
                    DisplayedContent.ConvertedWorldSections when world is not null =>
                        await ConfigureConversionResourcesAsync(world.Descriptor, requestCancellation.Token),
                    DisplayedContent.SchematicSections when schematic is not null =>
                        await ConfigureSchematicResourcesAsync(schematic.SourcePath, requestCancellation.Token),
                    DisplayedContent.PlanPreview when planVersion is not null && planSourcePath is not null =>
                        await ConfigurePlanResourcesAsync(
                            planSourcePath,
                            planVersion,
                            planProfileInferred,
                            requestCancellation.Token),
                    _ => "Minecraft 材质将在打开地图后自动匹配",
                };
            }

            if(isClosing || content != displayedContent || !ReferenceEquals(world, mountedWorld) ||
               !ReferenceEquals(schematic, displayedSchematic) ||
               content == DisplayedContent.PlanPreview && !ReferenceEquals(planSections, displayedPlanSections))
                return;
            IProgress<SectionMeshBuildProgress> progress = CreateMaterialSectionProgress(() =>
                !isClosing && ReferenceEquals(materialModeCancellation, requestCancellation));
            await Viewport.ReplaceSectionsAsync(sections, requestCancellation.Token, progress);
            requestCancellation.Token.ThrowIfCancellationRequested();
            SetMaterialLoadingStage("正在原子发布 Section 显存批次");
            Viewport.PrepareResourceTransitionForPublication();
            await Viewport.WaitForSectionGpuPublicationAsync(requestCancellation.Token);
            UpdateRenderInfoText();
            UiText.Bind(StatusText, "Text", UiText.Message($"{UiText.Get(resourceStatus)}；已重建 {sections.Count:N0} 个 Section。"));

            if(resumeStreaming && world is not null && ReferenceEquals(world, mountedWorld))
            {
                int totalChunkCount = world.ChunkIndex.GetChunkCount(dimension);
                if(content == DisplayedContent.WorldSections)
                {
                    string version = world.Descriptor.Version.VersionName ?? "原始版本";
                    StartWholeWorldPreview(world, dimension, totalChunkCount, version);
                }
                else if(content == DisplayedContent.ConvertedWorldSections &&
                        conversion is not null && ReferenceEquals(conversion, activeConversionPreview))
                {
                    string summary = Minecraft1122ConversionPreviewWorkflow.FormatSummary(conversion.Summary);
                    StartConvertedWorldPreview(world, conversion, dimension, totalChunkCount, summary);
                }
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception exception) when(IsRecoverableResourceException(exception))
        {
            if(!isClosing)
            {
                if(content == DisplayedContent.None)
                {
                    Viewport.ClearMinecraftResources();
                    activeResourceSelection = null;
                    UiText.Bind(StatusText, "Text", UiText.Message($"材质切换失败，已保持纯色方块：{exception.Message}"));
                    return;
                }
                Viewport.ClearSections();
                Viewport.ClearMinecraftResources();
                IReadOnlyList<NormalizedMinecraftSection> fallback = content switch
                {
                    DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections =>
                        displayedWorldSections.Values.ToArray(),
                    DisplayedContent.SchematicSections when schematic is not null => schematic.Sections,
                    DisplayedContent.PlanPreview => planSections,
                    _ => [],
                };
                IProgress<SectionMeshBuildProgress> fallbackProgress = CreateMaterialSectionProgress(() =>
                    !isClosing && ReferenceEquals(materialModeCancellation, requestCancellation));
                await Viewport.ReplaceSectionsAsync(fallback, requestCancellation.Token, fallbackProgress);
                SetMaterialLoadingStage("正在原子发布纯色 Section 显存批次");
                Viewport.PrepareResourceTransitionForPublication();
                await Viewport.WaitForSectionGpuPublicationAsync(requestCancellation.Token);
                activeResourceSelection = null;
                UiText.Bind(StatusText, "Text", UiText.Message($"材质切换失败，已保持纯色方块：{exception.Message}"));
            }
        }
        finally
        {
            if(resourceTransitionStarted) Viewport.CancelResourceTransition();
            if(ReferenceEquals(materialModeCancellation, requestCancellation))
                materialModeCancellation = null;
            requestCancellation.Dispose();
            isChangingMaterialMode = false;
            EndMaterialLoading();
            SolidMaterialChoice.IsEnabled = true;
            MinecraftMaterialChoice.IsEnabled = true;
            UpdateRenderInfoText();
            if(!isClosing)
            {
                SetSourceOpenButtonsEnabled(sourceActionsEnabled);
                SetCoordinateJumpButtonsEnabled(true);
                UpdateConversionControls();
                if(CanStreamNavigationPreview(mountedWorld)) OnNavigationTargetChanged(Viewport.CameraTarget);
            }
        }
    }

    private bool IsSolidMaterialMode() => SolidMaterialChoice.IsChecked == true;

    private void UpdateRenderInfoText()
    {
        if(RenderMeshInfoText is null || RenderMaterialInfoText is null) return;
        UiText.Bind(RenderMeshInfoText, "Text", IsSolidMaterialMode() ? UiText.Text("轻量合并网格") : UiText.Text("Section 网格"));
        UiText.Bind(RenderMaterialInfoText, "Text", IsSolidMaterialMode() ? UiText.Text("纯色性能") : activeResourceSelection is null && displayedContent != DisplayedContent.None ? UiText.Text("Minecraft 材质不可用") : UiText.Text("Minecraft 材质"));
    }

    private void ApplyEnvironmentSettings()
    {
        Viewport.SetTimeOfDay((float)TimeOfDaySlider.Value);
        Viewport.SetCloudsEnabled(CloudsToggle.IsChecked == true);
        Viewport.SetRainEnabled(RainToggle.IsChecked == true);
        Viewport.SetFogEnabled(FogToggle.IsChecked == true);
        Viewport.SetEnhancedLightingEnabled(EnhancedLightingToggle.IsChecked == true);
        Viewport.SetVerticalFieldOfViewDegrees((float)PhotographyFieldOfViewSlider.Value);
        Viewport.SetTerrainMode(SelectedTerrainMode());
        Viewport.KeyboardMovementSpeed = (float)KeyboardMoveSpeedSlider.Value;
        Viewport.MaximumActiveSections = checked(CurrentMaximumLoadedChunkCount * MaximumRenderableSectionsPerChunk);
        Viewport.SectionDrawDistance = Math.Max(384f, CurrentLoadedChunkRadius * 24f);
        Viewport.SetNavigationMode(ObserverPreviewModeChoice.IsChecked == true
            ? ViewportNavigationMode.Observer
            : ViewportNavigationMode.Orbit);
    }

    private ViewportTerrainMode SelectedTerrainMode()
    {
        if(SuperflatTerrainChoice.IsChecked == true) return ViewportTerrainMode.Superflat;
        if(TransparentTerrainChoice.IsChecked == true) return ViewportTerrainMode.Transparent;
        return ViewportTerrainMode.Chunks;
    }

    private async Task<string> ConfigureAutomaticWorldResourcesAsync(
        MinecraftWorldDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        string versionLabel = descriptor.Version.VersionName ?? UiText.Get("当前版本");
        activeResourceSelection = null;
        if(IsSolidMaterialMode())
        {
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            return UiText.Format($"纯色性能模式已跳过 {versionLabel} 材质与复杂方块模型解析");
        }

        MinecraftResourceSelectionResult location = await minecraftResourceCacheService.ResolveAsync(
            descriptor.RootPath,
            descriptor.Version,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if(!location.IsAvailable)
        {
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            return location.Status.TrimEnd('。');
        }

        IReadOnlyList<string> automaticResourcePacks = FindAutomaticWorldResourcePacks(descriptor.RootPath);
        ResourceSelection selection = new(location.ClientJarPath!, automaticResourcePacks);
        try
        {
            SetMaterialLoadingStage(UiText.Get("正在后台索引 Minecraft 材质资源"));
            await Viewport.ConfigureMinecraft1122ResourcesAsync(
                CreateRendererConfiguration(selection),
                cancellationToken);
            activeResourceSelection = selection;
            UpdateRenderInfoText();
            string status = location.Status.TrimEnd('。');
            return automaticResourcePacks.Count == 0 ? status : UiText.Format($"{status}，并叠加地图资源包");
        }
        catch(Exception exception) when(IsRecoverableResourceException(exception))
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            UpdateRenderInfoText();
            return UiText.Get("自动材质无法读取，已回退纯色性能模式");
        }
    }

    private async Task<string> ConfigureSchematicResourcesAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        string searchRoot = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        activeResourceSelection = null;
        originalWorldResourceSelection = null;
        if(IsSolidMaterialMode())
        {
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            return UiText.Get("纯色性能模式已跳过 1.12.2 材质与复杂方块模型解析");
        }

        MinecraftResourceSelectionResult location = await minecraftResourceCacheService.ResolveAsync(
            searchRoot,
            MinecraftTargetProfile.Java1122.Version,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if(!location.IsAvailable)
        {
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            return location.Status.TrimEnd('。');
        }

        ResourceSelection selection = new(location.ClientJarPath!, []);
        try
        {
            SetMaterialLoadingStage(UiText.Get("正在后台索引 Minecraft 1.12.2 材质"));
            await Viewport.ConfigureMinecraft1122ResourcesAsync(
                CreateRendererConfiguration(selection),
                cancellationToken);
            activeResourceSelection = selection;
            UpdateRenderInfoText();
            return location.Status.TrimEnd('。');
        }
        catch(Exception exception) when(IsRecoverableResourceException(exception))
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            UpdateRenderInfoText();
            return UiText.Get("1.12.2 资源不可用，已回退纯色性能模式");
        }
    }

    private async Task<string> ConfigurePlanResourcesAsync(
        string sourcePath,
        MinecraftVersionDescriptor version,
        bool inferred,
        CancellationToken cancellationToken = default)
    {
        string searchRoot = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        activeResourceSelection = null;
        originalWorldResourceSelection = null;
        if(IsSolidMaterialMode())
        {
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            return UiText.Get("纯色性能模式已跳过蓝图材质与复杂方块模型解析");
        }

        MinecraftResourceSelectionResult location = await minecraftResourceCacheService.ResolveAsync(
            searchRoot,
            version,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if(!location.IsAvailable)
        {
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            return location.Status.TrimEnd('。');
        }

        ResourceSelection selection = new(location.ClientJarPath!, []);
        try
        {
            SetMaterialLoadingStage(UiText.Get("正在后台索引蓝图的 Minecraft 材质"));
            await Viewport.ConfigureMinecraft1122ResourcesAsync(
                CreateRendererConfiguration(selection),
                cancellationToken);
            activeResourceSelection = selection;
            UpdateRenderInfoText();
            string inference = inferred ? UiText.Get("；蓝图未记录视觉版本，已根据方块状态自动推断") : string.Empty;
            return $"{location.Status.TrimEnd('。')}{inference}";
        }
        catch(Exception exception) when(IsRecoverableResourceException(exception))
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            UpdateRenderInfoText();
            return UiText.Get("蓝图材质无法读取，已回退纯色性能模式");
        }
    }

    private static IReadOnlyList<string> FindAutomaticWorldResourcePacks(string worldRootPath)
    {
        string path = Path.Combine(worldRootPath, "resources.zip");
        return File.Exists(path) ? [Path.GetFullPath(path)] : [];
    }

    private static Minecraft1122ResourceConfiguration CreateRendererConfiguration(ResourceSelection selection)
    {
        MinecraftResourceOverlay[] overlays = selection.ResourcePackPaths
            .Select(path => MinecraftResourceOverlay.ResourcePack(path, Path.GetFileNameWithoutExtension(path)))
            .ToArray();
        return new Minecraft1122ResourceConfiguration(selection.ClientJarPath, overlays);
    }

    private int BeginPreviewGeneration() => Interlocked.Increment(ref previewGeneration);

    private bool IsPreviewGenerationCurrent(int generation) =>
        generation == Volatile.Read(ref previewGeneration);

    private void ResetWorldSectionWindow(DisplayedContent content)
    {
        Dispatcher.VerifyAccess();
        Viewport.ClearSections();
        displayedWorldSections.Clear();
        displayedWorldChunkStatistics.Clear();
        displayedPlanPreview = null;
        displayedPlanSections = [];
        displayedPlanDimension = null;
        displayedSchematic = null;
        displayedPlanSourcePath = null;
        displayedPlanSpawnPoint = null;
        displayedPlanVisualProfile = null;
        displayedPlanVisualVersion = null;
        displayedPlanVisualProfileInferred = false;
        displayedContent = content;
        navigationPreviewCoverage = null;

    }

    private void PruneWorldSectionWindow(
        MinecraftChunkBounds bounds,
        DisplayedContent content,
        MinecraftDimensionId dimension)
    {
        Dispatcher.VerifyAccess();
        if(displayedContent != content || displayedWorldDimension != dimension) return;
        SectionCoordinate[] obsolete = displayedWorldSections.Keys
            .Where(coordinate =>
                !string.Equals(coordinate.Dimension, dimension.Value, StringComparison.Ordinal) ||
                !bounds.Contains(new MinecraftChunkAddress(dimension, coordinate.X, coordinate.Z)))
            .ToArray();
        if(obsolete.Length > 0)
        {
            Viewport.RemoveSections(obsolete);
            foreach(SectionCoordinate coordinate in obsolete) displayedWorldSections.Remove(coordinate);
        }
        MinecraftChunkAddress[] obsoleteStatistics = displayedWorldChunkStatistics.Keys
            .Where(address => address.Dimension != dimension || !bounds.Contains(address))
            .ToArray();
        foreach(MinecraftChunkAddress address in obsoleteStatistics) displayedWorldChunkStatistics.Remove(address);
    }

    private void ShowWorldNeighborhood(InitialWorldPreview preview)
    {
        navigationPreviewCoverage = preview.Bounds;
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(world is not null)
        {
            int totalChunkCount = world.ChunkIndex.GetChunkCount(displayedWorldDimension);
            ShowWorldSceneStatistics(
                SnapshotDisplayedWorldStatistics(),
                totalChunkCount,
                converted: displayedContent == DisplayedContent.ConvertedWorldSections);
        }
    }

    private void ReplaceWorldChunkStatistics(
        IReadOnlyDictionary<MinecraftChunkAddress, WorldPreviewStatistics> statistics)
    {
        Dispatcher.VerifyAccess();
        foreach((MinecraftChunkAddress address, WorldPreviewStatistics value) in statistics)
            displayedWorldChunkStatistics[address] = value;
    }

    private WorldPreviewStatistics SnapshotDisplayedWorldStatistics()
    {
        Dispatcher.VerifyAccess();
        var accumulator = new WorldPreviewStatisticsAccumulator();
        foreach((MinecraftChunkAddress address, WorldPreviewStatistics value) in displayedWorldChunkStatistics)
        {
            if(address.Dimension == displayedWorldDimension) accumulator.Add(value);
        }
        return accumulator.Snapshot();
    }

    private void ReplaceWorldSections(IReadOnlyList<NormalizedMinecraftSection> sections)
    {
        if(sections.Count == 0) return;
        Dispatcher.VerifyAccess();
        foreach(NormalizedMinecraftSection section in sections)
        {
            displayedWorldSections[section.Coordinate] = section;
        }
    }

    private void ClearDisplayedContent(bool invalidatePreviewGeneration = true)
    {
        SaveCurrentMapView();
        if(invalidatePreviewGeneration) BeginPreviewGeneration();
        StopNavigationPreviewScheduling();
        displayedWorldSections.Clear();
        displayedWorldChunkStatistics.Clear();
        displayedSchematic = null;
        displayedPlanPreview = null;
        displayedPlanSections = [];
        displayedPlanDimension = null;
        displayedPlanSourcePath = null;
        displayedPlanSpawnPoint = null;
        displayedPlanVisualProfile = null;
        displayedPlanVisualVersion = null;
        displayedPlanVisualProfileInferred = false;
        PlanFormatCard.Visibility = Visibility.Collapsed;
        displayedContent = DisplayedContent.None;
        ClearSceneStatistics();
    }

    private void ShowSceneStatistics(
        string source,
        string dataVersion,
        string countLabel,
        string count,
        string size,
        string chunks,
        string blockEntities,
        string origin)
    {
        UiText.Bind(SceneSourceText, "Text", UiText.Text(source));
        UiText.Bind(SceneSourceText, "ToolTip", UiText.Text(source));
        SceneDataVersionText.Text = dataVersion;
        UiText.Bind(SceneCountLabel, "Text", UiText.Text(countLabel));
        SceneCountText.Text = count;
        SceneSizeText.Text = size;
        SceneChunksText.Text = chunks;
        SceneBlockEntitiesText.Text = blockEntities;
        SceneOriginText.Text = origin;
    }

    private void ClearSceneStatistics() =>
        ShowSceneStatistics("—", "—", "方块数量", "—", "—", "—", "—", "—");

    private void ShowWorldSceneStatistics(
        WorldPreviewStatistics statistics,
        int availableChunkCount,
        bool converted)
    {
        string dataVersion = converted
            ? "1.12.2 Legacy"
            : mountedWorld?.Descriptor.Version.DataVersion?.ToString() ?? "—";
        string size = "—";
        string origin = "—";
        if(statistics.OccupiedBounds is WorldPreviewOccupiedBounds bounds)
        {
            WorldPreviewBlockSize blockSize = bounds.Size;
            WorldPreviewBlockCoordinate blockOrigin = bounds.Origin;
            size = $"{blockSize.Width:N0} × {blockSize.Height:N0} × {blockSize.Depth:N0}";
            origin = $"{blockOrigin.X:N0}, {blockOrigin.Y:N0}, {blockOrigin.Z:N0}";
        }
        ShowSceneStatistics(
            converted ? "1.12.2 转换预览" : "世界文件夹",
            dataVersion,
            "方块数量",
            statistics.NonAirBlockCount.ToString("N0"),
            size,
            $"{statistics.ChunkCount:N0} / {availableChunkCount:N0}",
            statistics.BlockEntityCount.ToString("N0"),
            origin);
    }

    private void ShowPlanSceneStatistics(SceneDelta delta, string dataVersion)
    {
        long blockCount = 0;
        BlockPosition? minimum = null;
        BlockPosition? maximum = null;
        foreach(SectionDelta section in delta.Sections)
        {
            foreach(VoxelChange change in section.Changes)
            {
                if(change.After.IsAir) continue;
                BlockPosition position = section.Section.ToBlockPosition(change.LocalIndex);
                blockCount++;
                minimum = minimum is BlockPosition min
                    ? new BlockPosition(Math.Min(min.X, position.X), Math.Min(min.Y, position.Y), Math.Min(min.Z, position.Z))
                    : position;
                maximum = maximum is BlockPosition max
                    ? new BlockPosition(Math.Max(max.X, position.X), Math.Max(max.Y, position.Y), Math.Max(max.Z, position.Z))
                    : position;
            }
        }

        string size = "—";
        string origin = "—";
        if(minimum is BlockPosition minPosition && maximum is BlockPosition maxPosition)
        {
            size = $"{(long)maxPosition.X - minPosition.X + 1:N0} × " +
                   $"{(long)maxPosition.Y - minPosition.Y + 1:N0} × " +
                   $"{(long)maxPosition.Z - minPosition.Z + 1:N0}";
            origin = $"{minPosition.X:N0}, {minPosition.Y:N0}, {minPosition.Z:N0}";
        }
        ShowSceneStatistics(
            ".zz",
            dataVersion,
            "方块数量",
            blockCount.ToString("N0"),
            size,
            $"{CountChunks(delta):N0} / {CountChunks(delta):N0}",
            "0",
            origin);
    }

    private bool CoordinatesHaveKeyboardFocus() =>
        XCoordinate.IsKeyboardFocusWithin || YCoordinate.IsKeyboardFocusWithin || ZCoordinate.IsKeyboardFocusWithin;

    private void SetConversionControlsEnabled(bool enabled)
    {
        bool showConversion = mountedWorld is not null &&
                              Minecraft1122ConversionPreviewWorkflow.IsHigherThanTarget(
                                  mountedWorld.Descriptor.Version);
        ConversionCard.Visibility = showConversion ? Visibility.Visible : Visibility.Collapsed;
        bool available = enabled && sourceActionsEnabled && showConversion && !isWorldExporting &&
                         !isCalculatingConversionYOffset && !isChangingMaterialMode && !isNavigationJumping;
        ConversionPreviewButton.IsEnabled = available && displayedContent == DisplayedContent.WorldSections &&
            activeConversionPreviewTask is null && activeConversionWorkflowTask is null && activeWorldPreviewTask is null;
        BlockMappingTableButton.IsEnabled = available;
        OriginalWorldButton.IsEnabled = available && displayedContent == DisplayedContent.ConvertedWorldSections &&
            activeConversionPreviewTask is null && activeConversionWorkflowTask is null && activeWorldPreviewTask is null;
        if(!enabled) DisableMapConversionControls();
    }

    private void UpdateConversionControls()
    {
        bool hasWorld = mountedWorld is not null && !isClosing;
        bool showConversion = hasWorld && Minecraft1122ConversionPreviewWorkflow.IsHigherThanTarget(
            mountedWorld!.Descriptor.Version);
        ConversionCard.Visibility = showConversion ? Visibility.Visible : Visibility.Collapsed;
        bool isScanning = activeConversionPreviewTask is not null;
        bool isStreaming = activeWorldPreviewTask is not null;
        bool showingWorld = displayedContent is DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections;
        bool available = sourceActionsEnabled && showConversion && showingWorld && !isWorldExporting &&
                         !isCalculatingConversionYOffset && !isChangingMaterialMode && !isNavigationJumping;
        ConversionPreviewButton.IsEnabled = available && !isScanning && !isStreaming &&
            activeConversionWorkflowTask is null && displayedContent == DisplayedContent.WorldSections;
        BlockMappingTableButton.IsEnabled = available && !isScanning && !isStreaming;
        OriginalWorldButton.IsEnabled = available && !isScanning && !isStreaming &&
            activeConversionWorkflowTask is null && displayedContent == DisplayedContent.ConvertedWorldSections;
        UpdateWorldSlimmingControls();
        UpdateWorldCropControls();
        UpdateUnifiedExportControl();
    }

    private void SetWorldExportBusy(bool busy)
    {
        isWorldExporting = busy;
        CancelExportButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelExportButton.IsEnabled = busy;
        if(busy)
        {
            SetSourceOpenButtonsEnabled(false);
            SetCoordinateJumpButtonsEnabled(false);
        }
        else if(!isClosing)
        {
            SetSourceOpenButtonsEnabled(true);
            SetCoordinateJumpButtonsEnabled(true);
        }
        UpdateConversionControls();
    }

    private bool TryBeginWorldExport(object owner, CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(cancellation);
        if(activeWorldExportOwner is not null || isWorldExporting) return false;
        activeWorldExportOwner = owner;
        worldExportCancellation = cancellation;
        SetWorldExportBusy(true);
        return true;
    }

    private void EnsureWorldExportOwner(object owner, CancellationToken cancellationToken)
    {
        if(!ReferenceEquals(activeWorldExportOwner, owner))
            throw new OperationCanceledException("导出任务已被其他操作取代。", cancellationToken);
    }

    private void EndWorldExport(object owner)
    {
        if(!ReferenceEquals(activeWorldExportOwner, owner)) return;
        activeWorldExportOwner = null;
        SetWorldExportBusy(false);
    }

    private void SetSourceOpenButtonsEnabled(bool enabled)
    {
        sourceActionsEnabled = enabled;
        bool available = enabled && !isClosing && !isWorldExporting && !isChangingMaterialMode &&
                         !isNavigationJumping;
        UnifiedOpenButton.IsEnabled = available;
        RegionFilesSection.IsEnabled = available;
        SolidMaterialChoice.IsEnabled = available;
        MinecraftMaterialChoice.IsEnabled = available;
        LoadedChunkRadiusSlider.IsEnabled = available;
        UpdateWorldSlimmingControls();
        UpdateWorldCropControls();
        UpdateUnifiedExportControl();
    }

    private void SetCoordinateJumpButtonsEnabled(bool enabled)
    {
        bool available = enabled && sourceActionsEnabled && !isClosing && !isWorldExporting &&
                         !isChangingMaterialMode && !isNavigationJumping;
        CoordinateJumpButton.IsEnabled = available;
        SpawnJumpButton.IsEnabled = available && TryGetDisplayedWorldSpawn(out _);
    }

    private bool TryGetDisplayedWorldSpawn(out BlockPosition spawn)
    {
        if(displayedContent is DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections)
            return WorldPreviewNavigation.TryGetSpawn(mountedWorld?.Descriptor, out spawn);

        if(displayedContent == DisplayedContent.PlanPreview && displayedPlanSpawnPoint is BlockPosition planSpawn)
        {
            spawn = planSpawn;
            return true;
        }

        spawn = default;
        return false;
    }

    private void UpdateUnifiedExportControl()
    {
        bool exportable = displayedContent is DisplayedContent.WorldSections or
            DisplayedContent.ConvertedWorldSections or
            DisplayedContent.SchematicSections or
            DisplayedContent.PlanPreview;
        UnifiedExportButton.IsEnabled = sourceActionsEnabled && !isClosing && !isWorldExporting &&
                                         !isChangingMaterialMode && !isNavigationJumping && exportable &&
                                         activeMountTask is null && planCompilationCancellation is null;
        UpdateUnifiedExportMenu();
    }

    private void UpdateUnifiedExportMenu()
    {
        bool originalModernWorld = mountedWorld is not null &&
                                   displayedContent == DisplayedContent.WorldSections &&
                                   Minecraft1122ConversionPreviewWorkflow.IsHigherThanTarget(
                                       mountedWorld.Descriptor.Version);
        OriginalVersionWorldExportMenuItem.Visibility = originalModernWorld
            ? Visibility.Visible
            : Visibility.Collapsed;
        UiText.Bind(WorldFolderExportTitleText, "Text", originalModernWorld ? UiText.Text("转换为 Java 1.12.2 世界文件夹") : displayedContent == DisplayedContent.ConvertedWorldSections ? UiText.Text("世界文件夹（当前 1.12.2 转换预览）") : mountedWorld is not null &&
                  Minecraft1122ConversionPreviewWorkflow.IsAlreadyTarget(mountedWorld.Descriptor.Version) ? UiText.Text("世界文件夹（原样副本）") : UiText.Text("世界文件夹（Java 1.12.2）"));
    }

    private static bool IsRecoverableResourceException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or
            ArgumentException or InvalidOperationException or System.Text.Json.JsonException;

    private sealed record WorldDimensionRegionGroup(
        WorldDimensionRegionSummary Summary,
        IReadOnlyList<WorldRegionNavigationItem> Regions,
        bool IsInitiallyExpanded)
    {
        public string DirectoryToolTip => Summary.RegionDirectories.Count == 0
            ? Summary.Dimension.Value
            : string.Join(Environment.NewLine, Summary.RegionDirectories);
    }

    private sealed record InitialWorldPreview(
        int ChunkCount,
        int SectionCount,
        MinecraftChunkBounds Bounds,
        WorldPreviewStatistics Statistics);

    private sealed record PlanVisualProfileSelection(
        ZjjVisualProfile Profile,
        MinecraftVersionDescriptor Version,
        bool Inferred);

    private sealed record PlanPreviewLoadResult(
        CompilationResult Compilation,
        IReadOnlyList<NormalizedMinecraftSection> Sections,
        ZjjVisualProfile VisualProfile,
        MinecraftVersionDescriptor VisualVersion,
        bool VisualProfileInferred);

    private sealed record WholeWorldPreviewResult(int ChunkCount, WorldPreviewStatistics Statistics);

    private sealed record ExportSourceContext(
        INormalizedMinecraftChunkSource Source,
        MinecraftDimensionId MainDimension,
        string DisplayName,
        string? SourcePath,
        BlockPosition SpawnLocation,
        IReadOnlyMinecraftWorld? World,
        int? AlreadyConvertedYOffset,
        int TotalChunkCount,
        long KnownBlockEntityCount,
        int SkippedCustomDimensionCount,
        bool IsOriginalWorldScene,
        BoxSelection? SchematicBounds = null,
        BlockPosition? SchematicClipboardOrigin = null,
        ZjjVisualProfile? VisualProfile = null,
        bool IsAiProjectScene = false);

    private sealed record WorldFolderExportOutcome(
        MinecraftWorldExportResult? ReencodedResult,
        MinecraftWorldCloneResult? CloneResult,
        int AppliedYOffset,
        IReadOnlyList<MinecraftDiagnostic> Diagnostics)
    {
        public static WorldFolderExportOutcome FromReencoded(MinecraftWorldExportResult result, int appliedYOffset) =>
            new(result, null, appliedYOffset, result.Diagnostics);

        public static WorldFolderExportOutcome FromClone(
            MinecraftWorldCloneResult result,
            IReadOnlyList<MinecraftDiagnostic> diagnostics) => new(null, result, 0, diagnostics);
    }

    private sealed record CollectedDimensionSections(
        IReadOnlyList<NormalizedMinecraftSection> Sections,
        IReadOnlyList<NormalizedMinecraftBlockEntity> BlockEntities,
        long ChunkCount,
        long BlockEntityCount);

    private sealed record SchematicExportOutcome(
        LegacySchematicExportResult Result,
        long CollectedBlockEntityCount);

    private sealed record ExportDiagnosticPresentation(string StatusSuffix, string Details);

    private sealed record ResourceSelection(
        string ClientJarPath,
        IReadOnlyList<string> ResourcePackPaths);

    private enum DisplayedContent
    {
        None,
        WorldSections,
        ConvertedWorldSections,
        SchematicSections,
        PlanPreview,
    }
}
