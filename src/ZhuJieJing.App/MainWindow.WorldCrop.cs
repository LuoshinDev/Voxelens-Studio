using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Threading;
using ZhuJieJing.App.WorldPreview;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private readonly MinecraftWorldCropService worldCropService = new();
    private CancellationTokenSource? worldCropCancellation;
    private Task? activeWorldCropTask;
    private bool isWorldCropCommitCritical;

    private async void OpenWorldCrop_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(world is null)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("请先打开 Minecraft 世界。"));
            return;
        }
        if(activeWorldCropTask is not null || activeWorldSlimmingTask is not null) return;

        chunkLoadReloadTimer.Stop();
        WorldCropWindow dialog = new(world, displayedWorldDimension, CurrentChunkLoadConcurrency)
        {
            Owner = GetMapFunctionsDialogOwner(),
        };
        TrackTopLevelWindowRenderingSuspension(dialog);
        if(dialog.ShowDialog() != true ||
           dialog.SelectedBounds is not MinecraftChunkSquareBounds bounds ||
           dialog.SelectedFocusChunk is not WorldCropChunkPoint focusChunk)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("已取消地图裁剪；当前地图没有改动。"));
            return;
        }
        if(isClosing || !ReferenceEquals(world, mountedWorld)) return;

        CancellationTokenSource requestCancellation = new();
        worldCropCancellation = requestCancellation;
        BeginWorldCropOperation("正在核验裁剪范围");
        UiText.Bind(StatusText, "Text", UiText.Text("正在后台核验当前维度与裁剪边界……"));
        Task<WorldCropExecutionOutcome> task = ExecuteWorldCropAsync(
            world,
            displayedWorldDimension,
            bounds,
            focusChunk,
            requestCancellation);
        activeWorldCropTask = task;
        UpdateWorldCropControls();

        try
        {
            WorldCropExecutionOutcome outcome = await task;
            if(isClosing) return;
            SetWorldCropSummary(outcome.Message);
            StatusText.Text = outcome.WorldRewritten
                ? UiText.Format($"{outcome.Message} 当前场景、区块索引与 Region 列表已重新载入。")
                : outcome.Message;
            CompleteWorldCropProgress(outcome.WorldRewritten ? "裁剪完成" : "无需裁剪");
        }
        catch(OperationCanceledException)
        {
            if(!isClosing)
            {
                SetWorldCropSummary(UiText.Text("地图裁剪已在提交前取消；当前地图没有改动。"));
                UiText.Bind(StatusText, "Text", UiText.Text("地图裁剪已取消；准备区已安全清理。"));
                HideWorldCropProgress();
            }
        }
        catch(Exception exception)
        {
            if(!isClosing)
            {
                SetWorldCropSummary(exception.Message);
                StatusText.Text = exception.Message;
                HideWorldCropProgress();
            }
        }
        finally
        {
            CompleteWorldCropRequest(task, requestCancellation);
        }
    }

    private async Task<WorldCropExecutionOutcome> ExecuteWorldCropAsync(
        IReadOnlyMinecraftWorld sourceWorld,
        MinecraftDimensionId cropDimension,
        MinecraftChunkSquareBounds bounds,
        WorldCropChunkPoint focusChunk,
        CancellationTokenSource requestCancellation)
    {
        string sourceDirectory = sourceWorld.Descriptor.RootPath;
        string sourceRevision = sourceWorld.Descriptor.SourceRevision;
        MinecraftDimensionId retainedDimension = displayedWorldDimension;
        Vector3 retainedWorldCamera = KeepCameraInsideCrop(
            FromPreviewRenderPosition(Viewport.CameraTarget),
            bounds,
            focusChunk);
        Progress<MinecraftWorldCropProgress> progress = new(value =>
        {
            if(IsCurrentWorldCropRequest(requestCancellation)) ShowWorldCropProgress(value);
        });

        requestCancellation.Token.ThrowIfCancellationRequested();
        EnsureCurrentWorldCropSource(sourceWorld, sourceRevision, requestCancellation.Token);

        SetWorldCropIndeterminateProgress(UiText.Get("正在核验当前维度与裁剪边界"));
        await Dispatcher.Yield(DispatcherPriority.Render);
        MinecraftWorldCropAnalyzeRequest analyzeRequest = new(
            sourceWorld,
            sourceRevision,
            cropDimension,
            bounds);
        MinecraftWorldCropAnalysis analysis = await Task.Run(
            async () => await worldCropService.AnalyzeAsync(
                analyzeRequest,
                progress,
                requestCancellation.Token),
            requestCancellation.Token);
        requestCancellation.Token.ThrowIfCancellationRequested();
        EnsureCurrentWorldCropAnalysis(sourceWorld, analysis, cropDimension, bounds, requestCancellation.Token);

        if(!analysis.HasChanges)
            return new WorldCropExecutionOutcome(
                "地图裁剪分析完成：选区外没有可删除的 Chunk，地图未改动。",
                false);
        string estimate = WorldRegionNavigation.FormatFileSize(analysis.EstimatedReclaimableBytes);
        SetWorldCropSummary(() =>
            UiText.Format($"已核验当前维度：将保留 {analysis.RetainedTargetDimensionChunkCount:N0} 个 Chunk，删除 {analysis.RemovedChunkCount:N0} 个；") +
            UiText.Format($"其他维度 {analysis.OtherDimensionChunkCount:N0} 个 Chunk 原样保留，预计释放 {estimate}。"));

        SetWorldCropIndeterminateProgress(UiText.Get("正在停止当前场景读取"));
        await CancelWorldExportAsync();
        await CancelWorldPreviewAsync();
        await CancelConversionPreviewAsync();
        requestCancellation.Token.ThrowIfCancellationRequested();
        EnsureCurrentWorldCropAnalysis(sourceWorld, analysis, cropDimension, bounds, requestCancellation.Token);
        SetWorldCropIndeterminateProgress(UiText.Get("正在准备安全暂存区"));
        await Dispatcher.Yield(DispatcherPriority.Render);

        PreparedMinecraftWorldCrop prepared = await Task.Run(
            async () => await worldCropService.PrepareAsync(
                analysis,
                progress,
                requestCancellation.Token),
            requestCancellation.Token);
        await using(prepared)
        {
            requestCancellation.Token.ThrowIfCancellationRequested();
            EnsureCurrentWorldCropAnalysis(sourceWorld, analysis, cropDimension, bounds, requestCancellation.Token);

            bool sourceDetached = false;
            bool commitAttempted = false;
            bool remountValidated = false;
            isWorldCropCommitCritical = true;
            try
            {
                SetWorldCropIndeterminateProgress(UiText.Get("正在卸载只读地图"));
                BeginPreviewGeneration();
                mountedWorld = null;
                sourceDetached = true;
                InvalidateWorldSlimmingAnalysis();
                ClearWorldRegionNavigation();
                ClearDisplayedContent();
                Viewport.ClearSections();
                Viewport.ClearMinecraftResources();
                activeResourceSelection = null;
                originalWorldResourceSelection = null;
                UpdateRenderInfoText();
                UpdateWorldCropControls();
                await sourceWorld.DisposeAsync();

                SetWorldCropIndeterminateProgress(UiText.Get("正在原子提交裁剪后的 Region 文件"));
                commitAttempted = true;
                await Task.Run(prepared.Commit);

                SetWorldCropIndeterminateProgress(UiText.Get("正在重新挂载并重建当前场景"));
                Progress<MinecraftWorldMountProgress> mountProgress = new(value =>
                {
                    if(!isClosing && isWorldCropCommitCritical) ShowWorldCropMountProgress(value);
                });
                SlimmingMountOutcome outcome = await MountAndDisplaySlimmedWorldAsync(
                    sourceDirectory,
                    retainedDimension,
                    retainedWorldCamera,
                    mountProgress);
                remountValidated = true;

                SetWorldCropIndeterminateProgress(UiText.Get("正在确认提交并清理临时回滚数据"));
                string cleanupWarning = string.Empty;
                try
                {
                    await Task.Run(prepared.FinalizeCommit);
                }
                catch(Exception exception)
                {
                    cleanupWarning = $" 地图已经安全生效，但临时回滚目录未完全清理：{prepared.CleanupWarning ?? exception.Message}";
                }
                isWorldCropCommitCritical = false;

                MinecraftWorldCropResult result = prepared.Result;
                string freed = WorldRegionNavigation.FormatFileSize(Math.Max(0L, result.BytesFreed));
                string spawnSuffix = result.RelocatedSpawnLocation is { } relocatedSpawn
                    ? $" 出生点已迁移到选区中心 ({relocatedSpawn.X}, {relocatedSpawn.Y}, {relocatedSpawn.Z})。"
                    : string.Empty;
                string streamingSuffix = string.Empty;
                try
                {
                    StartPostSlimmingWorldStream(outcome);
                }
                catch(Exception exception)
                {
                    streamingSuffix = $" 首屏已经重建，但当前维度的后台补齐未能启动：{exception.Message}";
                }
                return new WorldCropExecutionOutcome(
                    $"地图裁剪完成：当前维度保留 {analysis.RetainedTargetDimensionChunkCount:N0} 个 Chunk、删除 {result.RemovedChunkCount:N0} 个，" +
                    $"重构 {result.RepackedRegionCount:N0} 个主 Region、删除 {result.RemovedRegionCount:N0} 个空主 Region，" +
                    $"同步删除 {result.RemovedSidecarRecordCount:N0} 条实体/POI 记录，释放 {freed}；其他维度原样保留。" +
                    spawnSuffix + cleanupWarning + streamingSuffix,
                    true);
            }
            catch(Exception operationException)
            {
                if(!sourceDetached) throw;
                if(remountValidated)
                    throw new InvalidOperationException(
                        $"地图裁剪已生效并通过重新挂载验证；后续清理未完成：{operationException.Message}",
                        operationException);

                Exception? recoveryFailure = null;
                string? recoveryCleanupWarning = null;
                try
                {
                    recoveryCleanupWarning = await RestoreOriginalWorldAfterCropFailureAsync(
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
                    isWorldCropCommitCritical = false;
                }

                if(recoveryFailure is null)
                {
                    string cleanupSuffix = string.IsNullOrWhiteSpace(recoveryCleanupWarning)
                        ? string.Empty
                        : $" {recoveryCleanupWarning}";
                    throw new InvalidOperationException(
                        $"地图裁剪未完成，原地图已安全回滚并重新载入：{operationException.Message}{cleanupSuffix}",
                        operationException);
                }
                throw new AggregateException(
                    $"地图裁剪失败，自动回滚或重新载入也未能完成：{operationException.Message}；恢复错误：{recoveryFailure.Message}",
                    operationException,
                    recoveryFailure);
            }
            finally
            {
                isWorldCropCommitCritical = false;
            }
        }
    }

    private async Task<string?> RestoreOriginalWorldAfterCropFailureAsync(
        PreparedMinecraftWorldCrop prepared,
        string sourceDirectory,
        MinecraftDimensionId retainedDimension,
        Vector3 retainedWorldCamera,
        bool commitAttempted)
    {
        await DetachMountedWorldForSlimmingRecoveryAsync();
        if(commitAttempted)
        {
            SetWorldCropIndeterminateProgress(UiText.Get("提交未完成，正在原子回滚原地图"));
            await Task.Run(prepared.Rollback);
        }

        SetWorldCropIndeterminateProgress(UiText.Get("正在重新载入已恢复的原地图"));
        Progress<MinecraftWorldMountProgress> mountProgress = new(value =>
        {
            if(!isClosing && isWorldCropCommitCritical) ShowWorldCropMountProgress(value);
        });
        SlimmingMountOutcome outcome = await MountAndDisplaySlimmedWorldAsync(
            sourceDirectory,
            retainedDimension,
            retainedWorldCamera,
            mountProgress);
        StartPostSlimmingWorldStream(outcome);
        return prepared.CleanupWarning;
    }

    private void EnsureCurrentWorldCropSource(
        IReadOnlyMinecraftWorld sourceWorld,
        string sourceRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(isClosing || !ReferenceEquals(sourceWorld, mountedWorld) ||
           !string.Equals(sourceWorld.Descriptor.SourceRevision, sourceRevision, StringComparison.Ordinal))
            throw new OperationCanceledException("当前地图已变化，地图裁剪请求失效。", cancellationToken);
    }

    private void EnsureCurrentWorldCropAnalysis(
        IReadOnlyMinecraftWorld sourceWorld,
        MinecraftWorldCropAnalysis analysis,
        MinecraftDimensionId dimension,
        MinecraftChunkSquareBounds bounds,
        CancellationToken cancellationToken)
    {
        EnsureCurrentWorldCropSource(sourceWorld, analysis.SourceRevision, cancellationToken);
        if(!string.Equals(
               Path.TrimEndingDirectorySeparator(sourceWorld.Descriptor.RootPath),
               Path.TrimEndingDirectorySeparator(analysis.SourceDirectory),
               StringComparison.OrdinalIgnoreCase) ||
           analysis.Dimension != dimension || analysis.Bounds != bounds)
            throw new OperationCanceledException("地图路径、维度或裁剪边界已经变化，请重新打开裁剪器。", cancellationToken);
    }

    private bool IsCurrentWorldCropRequest(CancellationTokenSource requestCancellation) =>
        !isClosing && !requestCancellation.IsCancellationRequested &&
        ReferenceEquals(worldCropCancellation, requestCancellation);

    private static Vector3 KeepCameraInsideCrop(
        Vector3 camera,
        MinecraftChunkSquareBounds bounds,
        WorldCropChunkPoint focusChunk)
    {
        int cameraChunkX = WorldCropSelectionMath.BlockToChunk((int)Math.Clamp(
            Math.Floor(camera.X),
            int.MinValue,
            int.MaxValue));
        int cameraChunkZ = WorldCropSelectionMath.BlockToChunk((int)Math.Clamp(
            Math.Floor(camera.Z),
            int.MinValue,
            int.MaxValue));
        if(bounds.Contains(cameraChunkX, cameraChunkZ)) return camera;

        float focusX = (float)((long)focusChunk.X * 16L + 8L);
        float focusZ = (float)((long)focusChunk.Z * 16L + 8L);
        return new Vector3(focusX, camera.Y, focusZ);
    }

    private void BeginWorldCropOperation(string stage)
    {
        chunkLoadReloadTimer.Stop();
        SetWorldCropIndeterminateProgress(stage);
        SetSourceOpenButtonsEnabled(false);
        SetCoordinateJumpButtonsEnabled(false);
        UpdateConversionControls();
    }

    private void CompleteWorldCropRequest(Task task, CancellationTokenSource requestCancellation)
    {
        if(ReferenceEquals(activeWorldCropTask, task)) activeWorldCropTask = null;
        if(ReferenceEquals(worldCropCancellation, requestCancellation))
        {
            worldCropCancellation = null;
            requestCancellation.Dispose();
        }
        if(!isClosing && !isWorldCropCommitCritical)
        {
            SetSourceOpenButtonsEnabled(true);
            SetCoordinateJumpButtonsEnabled(true);
            UpdateConversionControls();
            PrefillConversionYOffsetIfToolIsVisible();
        }
    }

    private void InvalidateWorldCropState()
    {
        SetWorldCropSummary(
            UiText.Text("原地删除选区外的主区块、实体与 POI 数据。不会自动创建备份；执行前请手动备份并关闭 Minecraft 与服务端。"));
        UpdateWorldCropControls();
    }

    private void UpdateWorldCropControls() => UpdateMapFunctionsControls();

    private void ShowWorldCropProgress(MinecraftWorldCropProgress progress)
    {
        string stage = string.IsNullOrWhiteSpace(progress.Message)
            ? UiText.Get("正在处理地图裁剪")
            : progress.Message;
        SetWorldCropProgress(stage, progress.CompletedItems, progress.TotalItems);
    }

    private void ShowWorldCropMountProgress(MinecraftWorldMountProgress progress)
    {
        SetWorldCropProgress(FormatWorldMountProgress(progress), progress.CompletedItems, progress.TotalItems);
    }

    private void SetWorldCropProgress(string stage, long completedItems, long totalItems)
    {
        if(totalItems <= 0)
        {
            SetWorldCropProgressPresentation(stage, true, 0, "处理中");
            return;
        }

        long completed = Math.Clamp(completedItems, 0L, totalItems);
        int percentage = (int)Math.Round(completed * 100d / totalItems, MidpointRounding.AwayFromZero);
        SetWorldCropProgressPresentation(stage, false, percentage, $"{percentage}%");
    }

    private void SetWorldCropIndeterminateProgress(string stage) =>
        SetWorldCropProgressPresentation(stage, true, 0, "处理中");

    private void CompleteWorldCropProgress(string stage) =>
        SetWorldCropProgressPresentation(stage, false, 100, "100%");

    private void HideWorldCropProgress() => HideWorldCropProgressPresentation();

    private sealed record WorldCropExecutionOutcome(string Message, bool WorldRewritten);
}
