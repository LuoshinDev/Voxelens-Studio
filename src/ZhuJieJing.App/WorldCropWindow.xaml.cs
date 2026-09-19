using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ZhuJieJing.App.WorldPreview;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

public partial class WorldCropWindow : Window
{
    private readonly IReadOnlyMinecraftWorld sourceWorld;
    private readonly MinecraftDimensionId dimension;
    private readonly BlockPosition? spawnLocation;
    private readonly WorldCropChunkPoint? spawnChunk;
    private readonly bool overworldSpawnUnavailable;
    private int loadConcurrency;
    private CancellationTokenSource? activeLoadCancellation;
    private Task? activeLoadTask;
    private int loadGeneration;
    private WorldCropChunkPlaneSnapshot? snapshot;
    private WorldCropChunkPlaneSnapshot? contentSnapshot;
    private bool terrainReady;
    private bool isClosed;
    private string? planeLoadFailure;
    private bool closeRequestedDuringLoad;
    private bool permitClose;
    private bool updatingCoordinateFields;
    private bool selectionPresentationPending;
    private bool loadRestartInProgress;
    private MinecraftChunkSquareBounds? pendingSelectionPresentation;

    internal WorldCropWindow(
        IReadOnlyMinecraftWorld sourceWorld,
        MinecraftDimensionId dimension,
        int loadConcurrency = NormalizedMinecraftChunkBatcher.DefaultLoadConcurrency)
    {
        ArgumentNullException.ThrowIfNull(sourceWorld);
        this.sourceWorld = sourceWorld;
        this.dimension = dimension;
        this.loadConcurrency = WorldCropSatelliteMapLoader.ClampLoadConcurrency(loadConcurrency);
        if(dimension == MinecraftDimensionId.Overworld && sourceWorld.Descriptor.SpawnLocation is { } spawn)
        {
            spawnLocation = spawn;
            spawnChunk = new WorldCropChunkPoint(
                WorldCropSelectionMath.BlockToChunk(spawn.X),
                WorldCropSelectionMath.BlockToChunk(spawn.Z));
        }
        else if(dimension == MinecraftDimensionId.Overworld)
        {
            overworldSpawnUnavailable = true;
        }

        InitializeComponent();
        CropLoadConcurrencySlider.Value = this.loadConcurrency;
        UpdateLoadConcurrencyControls();
        UiText.Bind(CropDimensionPillText, "Text", UiText.Message($"当前维度 · {WorldRegionNavigation.FormatDimensionName(dimension)}"));
        SpawnConstraintPanel.Visibility = dimension == MinecraftDimensionId.Overworld
            ? Visibility.Visible
            : Visibility.Collapsed;
        if(spawnChunk is WorldCropChunkPoint required)
            UiText.Bind(SpawnConstraintText, "Text", UiText.Message($"原出生点位于 Chunk（{required.X}, {required.Z}）。若选区不包含它，裁剪时会自动迁移到选区中心并保留 Y 高度。"));
        else if(overworldSpawnUnavailable)
            UiText.Bind(SpawnConstraintText, "Text", UiText.Text("level.dat 未提供世界出生点，无法安全裁剪主世界。"));
        Loaded += WorldCropWindow_Loaded;
        Closing += WorldCropWindow_Closing;
        Closed += WorldCropWindow_Closed;
    }

    public MinecraftChunkSquareBounds? SelectedBounds { get; private set; }

    internal WorldCropChunkPoint? SelectedFocusChunk { get; private set; }

    private void WorldCropWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WorldCropWindow_Loaded;
        StartPlaneLoad(loadConcurrency);
    }

    private void StartPlaneLoad(int requestedLoadConcurrency)
    {
        Dispatcher.VerifyAccess();
        loadConcurrency = WorldCropSatelliteMapLoader.ClampLoadConcurrency(requestedLoadConcurrency);
        int generation = ++loadGeneration;
        CancellationTokenSource cancellation = new();
        activeLoadCancellation = cancellation;
        terrainReady = false;
        planeLoadFailure = null;
        contentSnapshot = null;
        StartCropButton.IsEnabled = false;
        BeginPlaneLoadPresentation(loadConcurrency);
        activeLoadTask = RunPlaneLoadAsync(generation, loadConcurrency, cancellation);
        UpdateLoadConcurrencyControls();
    }

    private async Task RunPlaneLoadAsync(
        int generation,
        int configuredLoadConcurrency,
        CancellationTokenSource cancellation)
    {
        await Task.Yield();
        CancellationToken loadToken = cancellation.Token;
        Progress<WorldCropPlaneLoadProgress> progress = new(value =>
        {
            if(!IsLoadCurrent(generation, cancellation, loadToken) || terrainReady || planeLoadFailure is not null)
                return;
            CropPlaneLoadingText.Text = value.TotalItems > 0
                ? $"{value.Message} · {Math.Clamp(value.CompletedItems, 0, value.TotalItems):N0} / {value.TotalItems:N0}"
                : value.Message;
            CropPlaneLoadingProgressBar.IsIndeterminate = value.TotalItems <= 0;
            if(value.TotalItems > 0)
                CropPlaneLoadingProgressBar.Value = Math.Clamp(
                    value.CompletedItems * 100d / value.TotalItems,
                    0d,
                    100d);
            CropFooterStatusText.Text = value.Stage switch
            {
                WorldCropPlaneLoadStage.ReadingChunkIndex => UiText.Get("正在读取当前维度全部 Chunk 索引…"),
                WorldCropPlaneLoadStage.ResolvingMinecraftResources => UiText.Get("正在准备 Minecraft 顶面材质…"),
                WorldCropPlaneLoadStage.ReadingTerrain => value.TotalItems > 0
                    ? UiText.Format($"边界已可操作；后台以 {configuredLoadConcurrency:N0} 路铺设卫星图 ") +
                      $"{value.CompletedItems:N0} / {value.TotalItems:N0} Chunk…"
                    : UiText.Format($"边界已可操作；正在以 {configuredLoadConcurrency:N0} 路并行铺设卫星图…"),
                _ => UiText.Get("正在生成限宽总览与稀疏卫星图分块…"),
            };
        });
        Task<WorldCropPlaneLoadResult> task = Task.Run(
            () => WorldCropSatelliteMapLoader.LoadAsync(
                sourceWorld,
                dimension,
                progress,
                loadToken,
                configuredLoadConcurrency,
                (update, token) => PublishPlaneUpdateAsync(update, generation, cancellation, token),
                spawnChunk),
            loadToken);
        try
        {
            WorldCropPlaneLoadResult loadResult = await task;
            if(!IsLoadCurrent(generation, cancellation, loadToken)) return;
            WorldCropChunkPlaneSnapshot loaded = loadResult.Snapshot;
            EnsureIndexReady(loaded, loadResult.Raster);
            contentSnapshot = loadResult.ContentSnapshot;
            if(loaded.ChunkCount == 0)
            {
                CropPlaneLoadingOverlay.Visibility = Visibility.Collapsed;
                UiText.Bind(CropCoordinateValidationText, "Text", UiText.Text("当前维度没有已存在的 Chunk，无法创建裁剪选区。"));
                UiText.Bind(CropFooterStatusText, "Text", UiText.Text("当前维度为空，无需裁剪。"));
                return;
            }

            WorldCropChunkPlaneSnapshot displayed = loadResult.ContentSnapshot.ChunkCount > 0
                ? loadResult.ContentSnapshot
                : loaded;
            ChunkPlane.SetSnapshot(
                displayed,
                spawnChunk,
                loadResult.Raster,
                loadResult.SatelliteMap,
                fit: false);
            terrainReady = true;
            CropPlaneLoadingOverlay.Visibility = Visibility.Collapsed;
            ApplyCropCoordinatesButton.IsEnabled = true;
            if(ChunkPlane.Selection is MinecraftChunkSquareBounds selected)
                UpdateSelectionPresentation(selected, updateCoordinateFields: false);
            int excludedEmptyChunks = loaded.ChunkCount - loadResult.ContentSnapshot.ChunkCount;
            UiText.Bind(CropFooterStatusText, "Text", loadResult.ContentSnapshot.ChunkCount > 0 ? UiText.Message($"地形已加载 · {loadResult.ContentSnapshot.ChunkCount:N0} 个有效 Chunk · {excludedEmptyChunks:N0} 个空 Chunk") : UiText.Text("未发现有效地形"));
        }
        catch(OperationCanceledException)
        {
            if(IsLoadCurrent(generation, cancellation, loadToken))
            {
                UiText.Bind(CropPlaneLoadingText, "Text", UiText.Text("区块平面载入已取消。"));
                UiText.Bind(CropFooterStatusText, "Text", UiText.Text("区块平面载入已取消。"));
            }
        }
        catch(Exception exception)
        {
            if(!IsLoadCurrent(generation, cancellation, loadToken)) return;
            planeLoadFailure = exception.Message;
            terrainReady = false;
            CropPlaneLoadingOverlay.Visibility = Visibility.Collapsed;
            UiText.Bind(CropPlaneLoadingText, "Text", UiText.Text("无法建立区块平面"));
            CropCoordinateValidationText.Text = exception.Message;
            UiText.Bind(CropFooterStatusText, "Text", UiText.Message($"载入失败：{exception.Message}"));
            StartCropButton.IsEnabled = false;
        }
        finally
        {
            bool ownsActiveLoad = ReferenceEquals(activeLoadCancellation, cancellation);
            if(ownsActiveLoad)
            {
                activeLoadCancellation = null;
                activeLoadTask = null;
            }
            cancellation.Dispose();
            if(!isClosed) UpdateLoadConcurrencyControls();
            if(closeRequestedDuringLoad && ownsActiveLoad && !isClosed)
            {
                permitClose = true;
                Close();
            }
        }
    }

    private void BeginPlaneLoadPresentation(int configuredLoadConcurrency)
    {
        CropPlaneLoadingOverlay.Visibility = Visibility.Visible;
        CropPlaneLoadingProgressBar.IsIndeterminate = true;
        CropPlaneLoadingProgressBar.Value = 0d;
        UiText.Bind(CropPlaneLoadingText, "Text", UiText.Message($"正在以 {configuredLoadConcurrency:N0} 路准备 Chunk 边界与顶层地形…"));
        UiText.Bind(CropFooterStatusText, "Text", UiText.Message($"正在以 {configuredLoadConcurrency:N0} 路解析当前维度…"));
        if(snapshot is not null) return;

        CropPlaneLoadingOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
        CropPlaneLoadingOverlay.VerticalAlignment = VerticalAlignment.Stretch;
        CropPlaneLoadingOverlay.Margin = new Thickness(0);
        CropPlaneLoadingOverlay.Padding = new Thickness(0);
        CropPlaneLoadingOverlay.CornerRadius = new CornerRadius(0);
        CropPlaneLoadingOverlay.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(230, 31, 27, 39));
    }

    private bool IsLoadCurrent(
        int generation,
        CancellationTokenSource cancellation,
        CancellationToken loadToken) =>
        !isClosed &&
        !closeRequestedDuringLoad &&
        generation == loadGeneration &&
        ReferenceEquals(activeLoadCancellation, cancellation) &&
        !loadToken.IsCancellationRequested;

    private async Task CancelActiveLoadAsync()
    {
        Task? task = activeLoadTask;
        CancellationTokenSource? cancellation = activeLoadCancellation;
        if(task is null || task.IsCompleted) return;
        loadGeneration++;
        cancellation?.Cancel();
        try
        {
            await task;
        }
        catch(OperationCanceledException)
        {
            // 每轮读取会自行处理取消；这里只负责等待它彻底退出。
        }
    }

    private int SelectedLoadConcurrency => WorldCropSatelliteMapLoader.ClampLoadConcurrency(
        (int)Math.Round(CropLoadConcurrencySlider.Value, MidpointRounding.AwayFromZero));

    private void CropLoadConcurrencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateLoadConcurrencyControls();

    private async void ApplyCropLoadConcurrency_Click(object sender, RoutedEventArgs e)
    {
        if(loadRestartInProgress || isClosed || closeRequestedDuringLoad) return;
        int requestedLoadConcurrency = SelectedLoadConcurrency;
        if(requestedLoadConcurrency == loadConcurrency && planeLoadFailure is null) return;
        loadRestartInProgress = true;
        UpdateLoadConcurrencyControls();
        UiText.Bind(CropFooterStatusText, "Text", UiText.Text("正在安全停止上一轮地形解析…"));
        try
        {
            await CancelActiveLoadAsync();
            if(isClosed || closeRequestedDuringLoad) return;
            StartPlaneLoad(requestedLoadConcurrency);
        }
        finally
        {
            loadRestartInProgress = false;
            if(!isClosed) UpdateLoadConcurrencyControls();
        }
    }

    private void UpdateLoadConcurrencyControls()
    {
        if(CropLoadConcurrencySlider is null || CropLoadConcurrencyText is null ||
           ApplyCropLoadConcurrencyButton is null)
            return;
        int selected = SelectedLoadConcurrency;
        UiText.Bind(CropLoadConcurrencyText, "Text", UiText.Message($"{selected:N0} 路"));
        CropLoadConcurrencySlider.IsEnabled = !isClosed && !closeRequestedDuringLoad && !loadRestartInProgress;
        ApplyCropLoadConcurrencyButton.IsEnabled = IsLoaded &&
                                                   !isClosed &&
                                                   !closeRequestedDuringLoad &&
                                                   !loadRestartInProgress &&
                                                   (selected != loadConcurrency || planeLoadFailure is not null);
    }

    private void WorldCropWindow_Closing(object? sender, CancelEventArgs e)
    {
        Task? task = activeLoadTask;
        if(permitClose || task is null || task.IsCompleted) return;
        e.Cancel = true;
        RequestCloseDuringLoad();
    }

    private void WorldCropWindow_Closed(object? sender, EventArgs e)
    {
        Closing -= WorldCropWindow_Closing;
        Closed -= WorldCropWindow_Closed;
        isClosed = true;
        loadGeneration++;
        CancellationTokenSource? cancellation = activeLoadCancellation;
        cancellation?.Cancel();
        if(activeLoadTask is null)
        {
            activeLoadCancellation = null;
            cancellation?.Dispose();
        }
    }

    private void ChunkPlane_SelectionChanged(object sender, EventArgs e)
    {
        if(ChunkPlane.Selection is not MinecraftChunkSquareBounds bounds || snapshot is null) return;
        pendingSelectionPresentation = bounds;
        if(selectionPresentationPending) return;
        selectionPresentationPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            selectionPresentationPending = false;
            if(isClosed || closeRequestedDuringLoad ||
               pendingSelectionPresentation is not MinecraftChunkSquareBounds pending || snapshot is null)
                return;
            pendingSelectionPresentation = null;
            UpdateSelectionPresentation(pending, updateCoordinateFields: true);
        });
    }

    private void ApplyCropCoordinates_Click(object sender, RoutedEventArgs e)
    {
        if(snapshot is null) return;
        if(!int.TryParse(MinimumChunkXTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minimumX) ||
           !int.TryParse(MinimumChunkZTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minimumZ) ||
           !long.TryParse(CropWidthTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long width) ||
           !long.TryParse(CropDepthTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long depth) ||
           !WorldCropSelectionMath.TryCreateFromMinimumAndSize(
               minimumX,
               minimumZ,
               width,
               depth,
               out MinecraftChunkSquareBounds bounds))
        {
            UiText.Bind(CropCoordinateValidationText, "Text", UiText.Text("请输入有效整数；宽度和深度至少为 1，坐标不能超出 32 位整数范围。"));
            return;
        }

        CropCoordinateValidationText.Text = string.Empty;
        ChunkPlane.SetSelection(bounds, fit: true);
    }

    private void FitCropWorld_Click(object sender, RoutedEventArgs e) => ChunkPlane.FitWorld();

    private void FitCropSelection_Click(object sender, RoutedEventArgs e) => ChunkPlane.FitSelection();

    private void CancelCrop_Click(object sender, RoutedEventArgs e)
    {
        if(activeLoadTask is { IsCompleted: false })
        {
            RequestCloseDuringLoad();
            return;
        }
        permitClose = true;
        DialogResult = false;
    }

    private void RequestCloseDuringLoad()
    {
        if(closeRequestedDuringLoad) return;
        closeRequestedDuringLoad = true;
        loadGeneration++;
        activeLoadCancellation?.Cancel();
        UpdateLoadConcurrencyControls();
        UiText.Bind(CropPlaneLoadingText, "Text", UiText.Text("正在停止区块与地形读取…"));
        UiText.Bind(CropFooterStatusText, "Text", UiText.Text("正在安全停止卫星图生成…"));
    }

    private void StartCrop_Click(object sender, RoutedEventArgs e)
    {
        if(!terrainReady || planeLoadFailure is not null)
        {
UiMessageBox.Show(
                this,
                planeLoadFailure is null
                    ? UiText.Get("卫星图仍在后台扫描地形。边界和选区现在可以编辑，完整判空结束后才能安全执行裁剪。")
                    : UiText.Format($"地形扫描失败，不能执行裁剪：{planeLoadFailure}"),
                UiText.Get("尚不能开始裁剪"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if(snapshot is null || ChunkPlane.Selection is not MinecraftChunkSquareBounds bounds) return;
        int retained = snapshot.CountWithin(bounds);
        int removed = snapshot.ChunkCount - retained;
        if(retained <= 0)
        {
UiMessageBox.Show(
                this,
                UiText.Get("选区内没有已存在的 Chunk。请先选择至少一个要保留的 Chunk。"),
                UiText.Get("无法开始裁剪"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if(removed <= 0)
        {
UiMessageBox.Show(
                this,
                UiText.Get("选区外没有可删除的 Chunk。请缩小矩形保留区后再执行。"),
                UiText.Get("当前无需裁剪"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if(overworldSpawnUnavailable)
        {
UiMessageBox.Show(
                this,
                UiText.Get("level.dat 未提供世界出生点，无法安全裁剪主世界。"),
                UiText.Get("无法验证出生点"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        string dimensionName = WorldRegionNavigation.FormatDimensionName(dimension);
        long width = WorldCropSelectionMath.GetWidth(bounds);
        long depth = WorldCropSelectionMath.GetDepth(bounds);
        string spawnConfirmation = string.Empty;
        if(spawnLocation is BlockPosition originalSpawn &&
           !MinecraftWorldCropSpawnPolicy.IsInside(originalSpawn, bounds))
        {
            BlockPosition relocated = MinecraftWorldCropSpawnPolicy.MoveToCenter(bounds, originalSpawn.Y);
            spawnConfirmation =
                $"出生点：将从 ({originalSpawn.X}, {originalSpawn.Y}, {originalSpawn.Z}) 迁移到选区中心 " +
                $"({relocated.X}, {relocated.Y}, {relocated.Z})；Y 高度保持不变。\n";
        }
        string confirmation =
            $"地图：{sourceWorld.Descriptor.LevelName}\n" +
            $"当前维度：{dimensionName}\n" +
            $"Inclusive 边界：X {bounds.MinChunkX}…{bounds.MaxChunkX}，Z {bounds.MinChunkZ}…{bounds.MaxChunkZ}\n" +
            $"矩形尺寸：{width:N0} × {depth:N0} Chunk（{width * 16L:N0} × {depth * 16L:N0} 方块）\n" +
            $"将保留 {retained:N0} 个已存在 Chunk，删除 {removed:N0} 个。\n\n" +
            spawnConfirmation +
            "本操作会原地删除当前维度选区外的 Region 主区块、实体与 POI 数据；其他维度原样保留。\n" +
            "筑界镜不会自动创建备份。继续前请确认你已经手动备份地图，并已关闭 Minecraft 与服务端。\n\n" +
            "确定开始裁剪吗？";
        MessageBoxResult answer = UiMessageBox.Show(
            this,
            confirmation,
            UiText.Get("确认地图裁剪"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if(answer != MessageBoxResult.Yes) return;

        SelectedBounds = bounds;
        double centerX = bounds.MinChunkX + bounds.Width / 2d;
        double centerZ = bounds.MinChunkZ + bounds.Depth / 2d;
        WorldCropChunkPlaneSnapshot focusSnapshot = contentSnapshot is { ChunkCount: > 0 } content
            ? content
            : snapshot;
        if(focusSnapshot.TryFindClosestWithin(bounds, centerX, centerZ, out WorldCropChunkPoint focus))
            SelectedFocusChunk = focus;
        permitClose = true;
        DialogResult = true;
    }

    private void UpdateSelectionPresentation(MinecraftChunkSquareBounds bounds, bool updateCoordinateFields)
    {
        if(snapshot is null) return;
        if(updateCoordinateFields && !updatingCoordinateFields)
        {
            updatingCoordinateFields = true;
            MinimumChunkXTextBox.Text = bounds.MinChunkX.ToString(CultureInfo.InvariantCulture);
            MinimumChunkZTextBox.Text = bounds.MinChunkZ.ToString(CultureInfo.InvariantCulture);
            CropWidthTextBox.Text = WorldCropSelectionMath.GetWidth(bounds).ToString(CultureInfo.InvariantCulture);
            CropDepthTextBox.Text = WorldCropSelectionMath.GetDepth(bounds).ToString(CultureInfo.InvariantCulture);
            updatingCoordinateFields = false;
        }

        int retained = snapshot.CountWithin(bounds);
        int removed = snapshot.ChunkCount - retained;
        long width = WorldCropSelectionMath.GetWidth(bounds);
        long depth = WorldCropSelectionMath.GetDepth(bounds);
        CropInclusiveBoundsText.Text =
            $"X {bounds.MinChunkX} … {bounds.MaxChunkX}\n" +
            $"Z {bounds.MinChunkZ} … {bounds.MaxChunkZ}\n" +
            UiText.Format($"{width:N0} × {depth:N0} Chunk / {width * 16L:N0} × {depth * 16L:N0} 方块");
        RetainedCropChunkCountText.Text = $"{retained:N0}";
        RemovedCropChunkCountText.Text = $"{removed:N0}";

        bool containsSpawn = !overworldSpawnUnavailable &&
                             (spawnChunk is not WorldCropChunkPoint requiredSpawn ||
                              WorldCropSelectionMath.Contains(bounds, requiredSpawn));
        if(spawnChunk is WorldCropChunkPoint required)
        {
            SpawnConstraintPanel.Background = containsSpawn
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 250, 239))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 245, 255));
            SpawnConstraintText.Foreground = containsSpawn
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(67, 119, 51))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(105, 65, 198));
            SpawnConstraintText.Text = containsSpawn
                ? UiText.Format($"已包含原出生点 Chunk（{required.X}, {required.Z}），出生点保持不变。")
                : FormatRelocatedSpawnMessage(bounds, required);
        }

        CropCoordinateValidationText.Text = planeLoadFailure is not null
            ? planeLoadFailure
            : overworldSpawnUnavailable
            ? UiText.Get("level.dat 缺少出生点，不能安全裁剪主世界。")
            : removed == 0
                ? UiText.Get("选区外没有可删除的 Chunk；请缩小保留区后再执行。")
                : string.Empty;
        StartCropButton.IsEnabled = terrainReady && planeLoadFailure is null &&
                                    retained > 0 && removed > 0 && !overworldSpawnUnavailable;
        UiText.Bind(CropFooterStatusText, "Text", planeLoadFailure is not null ? UiText.Message($"载入失败：{planeLoadFailure}") : !terrainReady ? UiText.Text("地图边界与选区已可操作；真实地表和空 Chunk 判定仍在后台逐步载入。") : removed == 0 ? UiText.Message($"矩形选区 · 保留全部 {retained:N0} 个 Chunk") : UiText.Message($"矩形选区 · 保留 {retained:N0} · 删除 {removed:N0}"));
    }

    private string FormatRelocatedSpawnMessage(
        MinecraftChunkSquareBounds bounds,
        WorldCropChunkPoint originalChunk)
    {
        if(spawnLocation is not BlockPosition originalSpawn)
            return UiText.Format($"原出生点 Chunk（{originalChunk.X}, {originalChunk.Z}）位于选区外。");
        BlockPosition relocated = MinecraftWorldCropSpawnPolicy.MoveToCenter(bounds, originalSpawn.Y);
        return UiText.Format($"原出生点 Chunk（{originalChunk.X}, {originalChunk.Z}）位于选区外；裁剪时将迁移到选区中心 ") +
               UiText.Format($"({relocated.X}, {relocated.Y}, {relocated.Z})，Y 高度保持不变。");
    }

    private ValueTask PublishPlaneUpdateAsync(
        WorldCropPlaneUpdate update,
        int generation,
        CancellationTokenSource cancellation,
        CancellationToken cancellationToken)
    {
        if(!IsLoadCurrent(generation, cancellation, cancellationToken))
            return ValueTask.CompletedTask;
        Task dispatched = Dispatcher.InvokeAsync(
            () => ApplyPlaneUpdate(update, generation, cancellation, cancellationToken),
            DispatcherPriority.Background,
            cancellationToken).Task;
        return new ValueTask(dispatched);
    }

    private void ApplyPlaneUpdate(
        WorldCropPlaneUpdate update,
        int generation,
        CancellationTokenSource cancellation,
        CancellationToken cancellationToken)
    {
        if(!IsLoadCurrent(generation, cancellation, cancellationToken)) return;
        switch(update)
        {
            case WorldCropIndexReadyUpdate indexReady:
                EnsureIndexReady(indexReady.Snapshot, indexReady.PlaceholderRaster);
                break;
            case WorldCropSatelliteBatchUpdate satellite when !terrainReady:
                ChunkPlane.ApplySatelliteUpdate(satellite.Tiles, satellite.OverviewPatch);
                break;
        }
    }

    private void EnsureIndexReady(
        WorldCropChunkPlaneSnapshot loaded,
        WorldCropChunkRaster placeholderRaster)
    {
        if(snapshot is not null) return;
        snapshot = loaded;
        if(loaded.ChunkCount == 0) return;

        ChunkPlane.SetSnapshot(loaded, spawnChunk, placeholderRaster, new WorldCropSatelliteMap([]));
        ChunkPlane.SetSelection(WorldCropSelectionMath.CreateInitial(loaded, spawnChunk));
        ApplyCropCoordinatesButton.IsEnabled = true;
        CropPlaneLoadingOverlay.HorizontalAlignment = HorizontalAlignment.Center;
        CropPlaneLoadingOverlay.VerticalAlignment = VerticalAlignment.Bottom;
        CropPlaneLoadingOverlay.Margin = new Thickness(16);
        CropPlaneLoadingOverlay.Padding = new Thickness(14, 10, 14, 10);
        CropPlaneLoadingOverlay.CornerRadius = new CornerRadius(10);
        CropPlaneLoadingOverlay.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(224, 31, 27, 39));
        UiText.Bind(CropFooterStatusText, "Text", UiText.Message($"边界已就绪 · 正在加载 {loaded.ChunkCount:N0} 个 Chunk"));
    }
}
