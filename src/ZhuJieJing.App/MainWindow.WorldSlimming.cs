using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Threading;
using ZhuJieJing.App.WorldPreview;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private const int WorldSlimmingSpawnRetentionRadiusChunks = 1;

    private readonly MinecraftWorldSlimmingService worldSlimmingService = new();
    private CancellationTokenSource? worldSlimmingCancellation;
    private Task? activeWorldSlimmingTask;
    private MinecraftWorldSlimmingAnalysis? activeWorldSlimmingAnalysis;
    private bool isWorldSlimmingCommitCritical;

    private async void AnalyzeWorldSlimming_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(world is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("请先打开 Minecraft 世界。"));
            return;
        }
        if(activeWorldSlimmingTask is not null) return;

        CancellationTokenSource requestCancellation = new();
        worldSlimmingCancellation = requestCancellation;
        activeWorldSlimmingAnalysis = null;
        Progress<MinecraftWorldSlimmingProgress> progress = new(value =>
        {
            if(IsCurrentWorldSlimmingRequest(requestCancellation)) ShowWorldSlimmingProgress(value);
        });
        MinecraftWorldSlimmingAnalyzeRequest request = new(
            world,
            world.Descriptor.SourceRevision,
            WorldSlimmingSpawnRetentionRadiusChunks,
            mapFunctionsWindow?.WorldSlimmingConcurrency ?? MinecraftWorldSlimmingService.DefaultAnalysisConcurrency);
        BeginWorldSlimmingOperation("正在分析完全空白区块");
        UiText.Bind(StatusText, "Text", UiText.Text("正在后台分析可安全清理的完全空白区块；自然地形一律保留……"));
        Task<MinecraftWorldSlimmingAnalysis>? task = null;

        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            requestCancellation.Token.ThrowIfCancellationRequested();
            task = Task.Run(
                async () => await worldSlimmingService.AnalyzeAsync(
                    request,
                    progress,
                    requestCancellation.Token),
                requestCancellation.Token);
            activeWorldSlimmingTask = task;
            UpdateWorldSlimmingControls();
            MinecraftWorldSlimmingAnalysis analysis = await task;
            requestCancellation.Token.ThrowIfCancellationRequested();
            if(!IsCurrentWorldSlimmingRequest(requestCancellation) ||
               !ReferenceEquals(world, mountedWorld) ||
               !IsWorldSlimmingAnalysisCurrent(analysis))
                return;

            activeWorldSlimmingAnalysis = analysis;
            string reclaimable = WorldRegionNavigation.FormatFileSize(analysis.EstimatedReclaimableBytes);
            SetWorldSlimmingSummary(analysis.RemovableChunkCount == 0
                ? UiText.Message($"已分析 {analysis.TotalChunkCount:N0} 个 Chunk：没有符合安全标准的完全空白区块；自然地形和有活动数据的区块均已保留。")
                : UiText.Message($"已分析 {analysis.TotalChunkCount:N0} 个 Chunk：可安全清理 {analysis.RemovableChunkCount:N0} 个，保留 {analysis.RetainedChunkCount:N0} 个，预计释放 {reclaimable}。"));
            CompleteWorldSlimmingProgress("分析完成");
            UiText.Bind(StatusText, "Text", analysis.RemovableChunkCount == 0 ? UiText.Text("地图瘦身分析完成：没有可安全清理的区块。") : UiText.Message($"地图瘦身分析完成：{analysis.RemovableChunkCount:N0} 个完全空白 Chunk 可安全清理；执行前请手动备份地图。"));
        }
        catch(OperationCanceledException)
        {
            if(!isClosing)
            {
                SetWorldSlimmingSummary(UiText.Text("地图瘦身分析已取消。"));
                HideWorldSlimmingProgress();
            }
        }
        catch(Exception exception)
        {
            if(!isClosing)
            {
                SetWorldSlimmingSummary(UiText.Message($"分析失败：{exception.Message}"));
                UiText.Bind(StatusText, "Text", UiText.Message($"无法分析地图瘦身：{exception.Message}"));
                HideWorldSlimmingProgress();
            }
        }
        finally
        {
            CompleteWorldSlimmingRequest(task, requestCancellation);
        }
    }

    private async void ApplyWorldSlimming_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyMinecraftWorld? world = mountedWorld;
        MinecraftWorldSlimmingAnalysis? analysis = activeWorldSlimmingAnalysis;
        if(world is null || analysis is null || !IsWorldSlimmingAnalysisCurrent(analysis))
        {
            activeWorldSlimmingAnalysis = null;
            SetWorldSlimmingSummary(UiText.Text("当前地图或文件修订已经变化，请重新分析后再执行。"));
            UpdateWorldSlimmingControls();
            return;
        }
        if(analysis.RemovableChunkCount == 0 || activeWorldSlimmingTask is not null) return;

        string reclaimable = WorldRegionNavigation.FormatFileSize(analysis.EstimatedReclaimableBytes);
        string confirmation =
            $"地图：{world.Descriptor.LevelName}\n" +
            $"位置：{world.Descriptor.RootPath}\n\n" +
            $"将清理 {analysis.RemovableChunkCount:N0} 个完全空白 Chunk，预计释放 {reclaimable}。\n" +
            "仅清理完全空白、无方块实体或活动数据的区块；自然地形一律保留。\n\n" +
            "本操作会原地重构当前地图，筑界镜不会自动创建备份。\n" +
            "继续前请确认你已经手动备份地图，并已关闭 Minecraft 与服务端。\n\n" +
            "确定继续吗？";
        MessageBoxResult answer = UiMessageBox.Show(
            GetMapFunctionsDialogOwner(),
            confirmation,
            UiText.Get("确认地图瘦身"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if(answer != MessageBoxResult.Yes)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("已取消地图瘦身；当前地图没有改动。"));
            return;
        }

        CancellationTokenSource requestCancellation = new();
        worldSlimmingCancellation = requestCancellation;
        BeginWorldSlimmingOperation("正在停止当前场景读取");
        UiText.Bind(StatusText, "Text", UiText.Text("正在停止场景流送并准备安全重构地图……"));
        Task<string> task = ExecuteWorldSlimmingAsync(world, analysis, requestCancellation);
        activeWorldSlimmingTask = task;
        UpdateWorldSlimmingControls();

        try
        {
            string completion = await task;
            if(isClosing) return;
            SetWorldSlimmingSummary(completion);
            UiText.Bind(StatusText, "Text", UiText.Message($"{completion} 当前场景、区块索引与 Region 列表已重新载入。"));
            CompleteWorldSlimmingProgress("瘦身完成");
        }
        catch(OperationCanceledException)
        {
            if(!isClosing)
            {
                activeWorldSlimmingAnalysis = null;
                SetWorldSlimmingSummary(UiText.Text("地图瘦身已在提交前取消；当前地图没有改动。"));
                UiText.Bind(StatusText, "Text", UiText.Text("地图瘦身已取消；准备区已安全清理。"));
                HideWorldSlimmingProgress();
            }
        }
        catch(Exception exception)
        {
            if(!isClosing)
            {
                activeWorldSlimmingAnalysis = null;
                SetWorldSlimmingSummary(exception.Message);
                StatusText.Text = exception.Message;
                HideWorldSlimmingProgress();
            }
        }
        finally
        {
            CompleteWorldSlimmingRequest(task, requestCancellation);
        }
    }

    private async Task<string> ExecuteWorldSlimmingAsync(
        IReadOnlyMinecraftWorld sourceWorld,
        MinecraftWorldSlimmingAnalysis analysis,
        CancellationTokenSource requestCancellation)
    {
        string sourceDirectory = sourceWorld.Descriptor.RootPath;
        MinecraftDimensionId retainedDimension = displayedWorldDimension;
        Vector3 retainedWorldCamera = FromPreviewRenderPosition(Viewport.CameraTarget);
        Progress<MinecraftWorldSlimmingProgress> progress = new(value =>
        {
            if(IsCurrentWorldSlimmingRequest(requestCancellation)) ShowWorldSlimmingProgress(value);
        });

        await CancelWorldExportAsync();
        await CancelWorldPreviewAsync();
        await CancelConversionPreviewAsync();
        requestCancellation.Token.ThrowIfCancellationRequested();
        EnsureCurrentWorldSlimmingSource(sourceWorld, analysis, requestCancellation.Token);
        await Dispatcher.Yield(DispatcherPriority.Render);

        PreparedMinecraftWorldSlimming prepared = await Task.Run(
            async () => await worldSlimmingService.PrepareAsync(
                analysis,
                progress,
                requestCancellation.Token),
            requestCancellation.Token);
        await using(prepared)
        {
            requestCancellation.Token.ThrowIfCancellationRequested();
            EnsureCurrentWorldSlimmingSource(sourceWorld, analysis, requestCancellation.Token);

            bool sourceDetached = false;
            bool commitAttempted = false;
            bool remountValidated = false;
            isWorldSlimmingCommitCritical = true;
            try
            {
                SetWorldSlimmingIndeterminateProgress(UiText.Get("正在卸载只读地图"));
                BeginPreviewGeneration();
                mountedWorld = null;
                sourceDetached = true;
                activeWorldSlimmingAnalysis = null;
                InvalidateWorldCropState();
                ClearWorldRegionNavigation();
                ClearDisplayedContent();
                Viewport.ClearSections();
                Viewport.ClearMinecraftResources();
                activeResourceSelection = null;
                originalWorldResourceSelection = null;
                UpdateRenderInfoText();
                UpdateWorldSlimmingControls();
                await sourceWorld.DisposeAsync();

                SetWorldSlimmingIndeterminateProgress(UiText.Get("正在原子提交 Region 文件"));
                commitAttempted = true;
                await Task.Run(prepared.Commit);

                SetWorldSlimmingIndeterminateProgress(UiText.Get("正在重新挂载并重建当前场景"));
                SlimmingMountOutcome outcome = await MountAndDisplaySlimmedWorldAsync(
                    sourceDirectory,
                    retainedDimension,
                    retainedWorldCamera);
                remountValidated = true;

                SetWorldSlimmingIndeterminateProgress(UiText.Get("正在确认提交并清理临时回滚数据"));
                string cleanupWarning = string.Empty;
                try
                {
                    await Task.Run(prepared.FinalizeCommit);
                }
                catch(Exception exception)
                {
                    cleanupWarning = $" 地图已经安全生效，但临时回滚目录未完全清理：{exception.Message}";
                }
                isWorldSlimmingCommitCritical = false;

                MinecraftWorldSlimmingResult result = prepared.Result;
                string freed = WorldRegionNavigation.FormatFileSize(Math.Max(0L, result.BytesFreed));
                string streamingSuffix = string.Empty;
                try
                {
                    StartPostSlimmingWorldStream(outcome);
                }
                catch(Exception exception)
                {
                    streamingSuffix = $" 首屏已经重建，但当前维度的后台补齐未能启动：{exception.Message}";
                }
                return $"地图瘦身完成：清理 {result.RemovedChunkCount:N0} 个 Chunk，重构 {result.RepackedRegionCount:N0} 个 Region，删除 {result.RemovedRegionCount:N0} 个空 Region 文件，释放 {freed}。{cleanupWarning}{streamingSuffix}";
            }
            catch(Exception operationException)
            {
                if(!sourceDetached) throw;
                if(remountValidated)
                    throw new InvalidOperationException($"地图瘦身已生效并通过重新挂载验证；后续清理未完成：{operationException.Message}", operationException);

                Exception? recoveryFailure = null;
                string? recoveryCleanupWarning = null;
                try
                {
                    recoveryCleanupWarning = await RestoreOriginalWorldAfterSlimmingFailureAsync(
                        prepared,
                        sourceDirectory,
                        retainedDimension,
                        retainedWorldCamera,
                        commitAttempted);
                }
                catch(Exception exception)
                {
                    recoveryFailure = exception;
                }
                finally
                {
                    isWorldSlimmingCommitCritical = false;
                }

                if(recoveryFailure is null)
                {
                    string cleanupSuffix = string.IsNullOrWhiteSpace(recoveryCleanupWarning)
                        ? string.Empty
                        : $" {recoveryCleanupWarning}";
                    throw new InvalidOperationException(
                        $"地图瘦身未完成，原地图已安全回滚并重新载入：{operationException.Message}{cleanupSuffix}",
                        operationException);
                }
                throw new AggregateException(
                    $"地图瘦身失败，自动回滚或重新载入也未能完成：{operationException.Message}；恢复错误：{recoveryFailure.Message}",
                    operationException,
                    recoveryFailure);
            }
            finally
            {
                isWorldSlimmingCommitCritical = false;
            }
        }
    }

    private async Task<string?> RestoreOriginalWorldAfterSlimmingFailureAsync(
        PreparedMinecraftWorldSlimming prepared,
        string sourceDirectory,
        MinecraftDimensionId retainedDimension,
        Vector3 retainedWorldCamera,
        bool commitAttempted)
    {
        await DetachMountedWorldForSlimmingRecoveryAsync();
        if(commitAttempted)
        {
            SetWorldSlimmingIndeterminateProgress(UiText.Get("提交未完成，正在原子回滚原地图"));
            await Task.Run(prepared.Rollback);
        }

        SetWorldSlimmingIndeterminateProgress(UiText.Get("正在重新载入已恢复的原地图"));
        SlimmingMountOutcome outcome = await MountAndDisplaySlimmedWorldAsync(
            sourceDirectory,
            retainedDimension,
            retainedWorldCamera);
        StartPostSlimmingWorldStream(outcome);
        return prepared.CleanupWarning;
    }

    private async Task DetachMountedWorldForSlimmingRecoveryAsync()
    {
        BeginPreviewGeneration();
        await CancelWorldPreviewAsync();
        IReadOnlyMinecraftWorld? world = mountedWorld;
        mountedWorld = null;
        ClearWorldRegionNavigation();
        ClearDisplayedContent();
        Viewport.ClearSections();
        Viewport.ClearMinecraftResources();
        activeResourceSelection = null;
        originalWorldResourceSelection = null;
        UpdateRenderInfoText();
        if(world is not null) await world.DisposeAsync();
    }

    private async Task<SlimmingMountOutcome> MountAndDisplaySlimmedWorldAsync(
        string sourceDirectory,
        MinecraftDimensionId retainedDimension,
        Vector3 retainedWorldCamera,
        IProgress<MinecraftWorldMountProgress>? mountProgress = null)
    {
        IReadOnlyMinecraftWorld world = await MountWorldForSlimmingAsync(sourceDirectory, mountProgress);
        try
        {
            mountedWorld = world;
            MinecraftWorldDescriptor descriptor = world.Descriptor;
            displayedWorldDimension = descriptor.Dimensions.Any(dimension => dimension.Id == retainedDimension)
                ? retainedDimension
                : SelectPreviewDimension(descriptor);
            originalWorldResourceSelection = null;
            SetConversionYOffsetText(string.Empty);
            ClearDisplayedContent();
            Viewport.ClearSections();
            WorldNameText.Text = descriptor.LevelName;
            UiText.Bind(WorldVersionText, "Text", UiText.Message($"版本： {FormatWorldVersion(descriptor.Version)}"));
            WorldVersionText.ToolTip = null;
            WorldPathText.Text = descriptor.RootPath;
            WorldPathText.ToolTip = descriptor.RootPath;
            XCoordinate.Text = retainedWorldCamera.X.ToString("0.##");
            YCoordinate.Text = retainedWorldCamera.Y.ToString("0.##");
            ZCoordinate.Text = retainedWorldCamera.Z.ToString("0.##");
            Viewport.JumpTo(retainedWorldCamera);

            BeginMaterialLoading(UiText.Get("正在重新匹配 Minecraft 材质"));
            string resourceStatus;
            try
            {
                resourceStatus = await ConfigureAutomaticWorldResourcesAsync(descriptor, CancellationToken.None);
            }
            finally
            {
                EndMaterialLoading();
            }

            BlockPosition focus = WorldPreviewNavigation.TryCreateBlockPosition(retainedWorldCamera, out BlockPosition current)
                ? current
                : descriptor.SpawnLocation ?? new BlockPosition(0, 64, 0);
            Task<InitialWorldPreview> initialPreviewTask = LoadInitialWorldPreviewAsync(
                world,
                displayedWorldDimension,
                focus,
                CancellationToken.None);
            activeWorldPreviewTask = initialPreviewTask;
            InitialWorldPreview preview = await initialPreviewTask;
            if(ReferenceEquals(activeWorldPreviewTask, initialPreviewTask)) activeWorldPreviewTask = null;
            ShowWorldNeighborhood(preview);

            Task<IReadOnlyList<WorldDimensionRegionGroup>> regionGroupsTask = LoadWorldRegionGroupsAsync(
                world,
                displayedWorldDimension,
                focus.Y,
                CancellationToken.None);
            activeWorldPreviewTask = regionGroupsTask;
            IReadOnlyList<WorldDimensionRegionGroup> regionGroups = await regionGroupsTask;
            if(ReferenceEquals(activeWorldPreviewTask, regionGroupsTask)) activeWorldPreviewTask = null;
            ShowWorldRegionNavigation(regionGroups);
            UpdateRenderInfoText();
            UpdateConversionControls();

            int dimensionChunkCount = world.ChunkIndex.GetChunkCount(displayedWorldDimension);
            string version = descriptor.Version.VersionName ?? "原始版本";
            return new SlimmingMountOutcome(world, displayedWorldDimension, dimensionChunkCount, version, resourceStatus, preview);
        }
        catch
        {
            if(ReferenceEquals(world, mountedWorld)) mountedWorld = null;
            activeWorldPreviewTask = null;
            ClearWorldRegionNavigation();
            ClearDisplayedContent();
            Viewport.ClearSections();
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            originalWorldResourceSelection = null;
            UpdateRenderInfoText();
            await world.DisposeAsync();
            throw;
        }
    }

    private async Task<IReadOnlyMinecraftWorld> MountWorldForSlimmingAsync(
        string sourceDirectory,
        IProgress<MinecraftWorldMountProgress>? mountProgress = null)
    {
        IProgress<MinecraftWorldMountProgress> progress = mountProgress ??
            new Progress<MinecraftWorldMountProgress>(value =>
            {
                if(!isClosing && isWorldSlimmingCommitCritical)
                    ShowWorldSlimmingMountProgress(value);
            });
        ReadOnlyWorldMountRequest request = new(
            sourceDirectory,
            ConcurrentWritePolicy: ConcurrentWorldWritePolicy.RejectActiveWriter,
            IncludeCustomDimensions: true,
            CaptureSourceFingerprints: true);
        Task<IReadOnlyMinecraftWorld> task = Task.Run(
            async () => await new AnvilWorldMounter().MountAsync(request, CancellationToken.None, progress));
        activeMountTask = task;
        try
        {
            return await task;
        }
        finally
        {
            if(ReferenceEquals(activeMountTask, task)) activeMountTask = null;
        }
    }

    private void StartPostSlimmingWorldStream(SlimmingMountOutcome outcome)
    {
        if(!ReferenceEquals(outcome.World, mountedWorld)) return;
        if(outcome.DimensionChunkCount <= CurrentMaximumLoadedChunkCount)
            StartWholeWorldPreview(outcome.World, outcome.Dimension, outcome.DimensionChunkCount, outcome.Version);
    }

    private void EnsureCurrentWorldSlimmingSource(
        IReadOnlyMinecraftWorld sourceWorld,
        MinecraftWorldSlimmingAnalysis analysis,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(isClosing || !ReferenceEquals(sourceWorld, mountedWorld) || !IsWorldSlimmingAnalysisCurrent(analysis))
            throw new OperationCanceledException("当前地图已变化，地图瘦身请求失效。", cancellationToken);
    }

    private bool IsWorldSlimmingAnalysisCurrent(MinecraftWorldSlimmingAnalysis analysis)
    {
        MinecraftWorldDescriptor? descriptor = mountedWorld?.Descriptor;
        return descriptor is not null &&
               string.Equals(
                   Path.TrimEndingDirectorySeparator(descriptor.RootPath),
                   Path.TrimEndingDirectorySeparator(analysis.SourceDirectory),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(descriptor.SourceRevision, analysis.SourceRevision, StringComparison.Ordinal);
    }

    private bool IsCurrentWorldSlimmingRequest(CancellationTokenSource requestCancellation) =>
        !isClosing && !requestCancellation.IsCancellationRequested &&
        ReferenceEquals(worldSlimmingCancellation, requestCancellation);

    private void BeginWorldSlimmingOperation(string stage)
    {
        chunkLoadReloadTimer.Stop();
        SetWorldSlimmingIndeterminateProgress(stage);
        SetSourceOpenButtonsEnabled(false);
        SetCoordinateJumpButtonsEnabled(false);
        UpdateConversionControls();
    }

    private void CompleteWorldSlimmingRequest(Task? task, CancellationTokenSource requestCancellation)
    {
        if(task is not null && ReferenceEquals(activeWorldSlimmingTask, task)) activeWorldSlimmingTask = null;
        if(ReferenceEquals(worldSlimmingCancellation, requestCancellation))
        {
            worldSlimmingCancellation = null;
            requestCancellation.Dispose();
        }
        if(!isClosing && !isWorldSlimmingCommitCritical)
        {
            SetSourceOpenButtonsEnabled(true);
            SetCoordinateJumpButtonsEnabled(true);
            UpdateConversionControls();
            PrefillConversionYOffsetIfToolIsVisible();
        }
    }

    private void InvalidateWorldSlimmingAnalysis()
    {
        activeWorldSlimmingAnalysis = null;
        SetWorldSlimmingSummary(UiText.Text("请先分析当前地图。筑界镜不会自动创建备份；执行前请手动备份，并关闭 Minecraft 与服务端。"));
        UpdateWorldSlimmingControls();
    }

    private void UpdateWorldSlimmingControls() => UpdateMapFunctionsControls();

    private void ShowWorldSlimmingProgress(MinecraftWorldSlimmingProgress progress)
    {
        string stage = string.IsNullOrWhiteSpace(progress.Message)
            ? FormatWorldSlimmingStage(progress.Stage)
            : progress.Message;
        SetWorldSlimmingProgress(stage, progress.CompletedItems, progress.TotalItems);
    }

    private void ShowWorldSlimmingMountProgress(MinecraftWorldMountProgress progress)
    {
        SetWorldSlimmingProgress(FormatWorldMountProgress(progress), progress.CompletedItems, progress.TotalItems);
    }

    private void SetWorldSlimmingProgress(string stage, long completedItems, long totalItems)
    {
        if(totalItems <= 0)
        {
            SetWorldSlimmingProgressPresentation(stage, true, 0, "处理中");
            return;
        }

        long completed = Math.Clamp(completedItems, 0L, totalItems);
        int percentage = (int)Math.Round(completed * 100d / totalItems, MidpointRounding.AwayFromZero);
        SetWorldSlimmingProgressPresentation(stage, false, percentage, $"{percentage}%");
    }

    private void SetWorldSlimmingIndeterminateProgress(string stage) =>
        SetWorldSlimmingProgressPresentation(stage, true, 0, "处理中");

    private void CompleteWorldSlimmingProgress(string stage) =>
        SetWorldSlimmingProgressPresentation(stage, false, 100, "100%");

    private void HideWorldSlimmingProgress() => HideWorldSlimmingProgressPresentation();

    private static string FormatWorldSlimmingStage(MinecraftWorldSlimmingStage stage) => stage switch
    {
        MinecraftWorldSlimmingStage.ValidatingSource => UiText.Get("正在核验地图修订"),
        MinecraftWorldSlimmingStage.AnalyzingChunks => UiText.Get("正在分析区块内容"),
        MinecraftWorldSlimmingStage.AnalyzingCompanionData => UiText.Get("正在检查方块实体与活动数据"),
        MinecraftWorldSlimmingStage.PreparingStaging => UiText.Get("正在准备安全暂存区"),
        MinecraftWorldSlimmingStage.CopyingFiles => UiText.Get("正在复制未修改文件"),
        MinecraftWorldSlimmingStage.RepackingRegions => UiText.Get("正在重构 Region 文件"),
        MinecraftWorldSlimmingStage.ValidatingStaging => UiText.Get("正在验证重构结果"),
        MinecraftWorldSlimmingStage.ReadyToCommit => UiText.Get("安全暂存区已就绪"),
        _ => UiText.Get("正在处理地图瘦身"),
    };

    private sealed record SlimmingMountOutcome(
        IReadOnlyMinecraftWorld World,
        MinecraftDimensionId Dimension,
        int DimensionChunkCount,
        string Version,
        string ResourceStatus,
        InitialWorldPreview Preview);

}
