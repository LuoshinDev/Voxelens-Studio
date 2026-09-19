using System.Windows;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private bool isBlockReplacementCommitCritical;

    private async void MapFunctionsWindow_ReplacementScanRequested(object? sender, EventArgs e)
        => await RunBlockReplacementAsync(apply: false);

    private async void MapFunctionsWindow_ReplacementApplyRequested(object? sender, EventArgs e)
        => await RunBlockReplacementAsync(apply: true);

    private async Task RunBlockReplacementAsync(bool apply)
    {
        IReadOnlyMinecraftWorld? world = mountedWorld;
        MapFunctionsWindow? window = mapFunctionsWindow;
        if(world is null || window is null || isClosing || isWorldExporting || !sourceActionsEnabled || displayedContent != DisplayedContent.WorldSections) return;
        MinecraftBlockReplacementRequest? request = apply ? window.GetReplacementRequest() : null;
        if(apply)
        {
            if(request is null) return;
            long count = MinecraftWorldBlockReplacement.CountMatches(request);
            if(count == 0) return;
            string message = UiText.Format($"来源：{UiText.BlockName(request.Source.State.Name)}\n目标：{UiText.BlockName(request.Target.State.Name)}\n\n将替换 {count:N0} 个方块，包含全部 {request.Inventory.DimensionCount:N0} 个维度、全部已保存区块。\n兼容的朝向与结构将自动保留。\n") +
                UiText.Get("被替换的箱子等方块实体不会留在新方块上，其原始数据会归档在地图中。\n\n将直接修改原地图，不另存副本。请先自行备份，并关闭 Minecraft 与服务端。确认替换？");
            if(UiMessageBox.Show(window, message, UiText.Get("确认替换原地图"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        object owner = new();
        CancellationTokenSource cancellation = new();
        if(!TryBeginWorldExport(owner, cancellation)) { cancellation.Dispose(); return; }
        Task? operation = null;
        string finish = UiText.Get("方块替换已结束");
        bool completed = false;
        BeginWorldExportLoading(apply ? UiText.Get("正在替换原地图") : UiText.Get("正在扫描地图方块"), UiText.Get("全部维度 / 全部已保存区块"));
        try
        {
            // Track preparation, commit, recovery and remount together so closing cannot dispose their source early.
            Task<string> task = ExecuteBlockReplacementAsync(world, window, request, cancellation);
            activeWorldExportTask = operation = task;
            finish = await task;
            completed = true;
        }
        catch(OperationCanceledException)
        {
            finish = "已取消；原地图保持不变";
        }
        catch(Exception exception)
        {
            finish = $"方块替换未完成：{exception.Message}";
        }
        finally
        {
            if(ReferenceEquals(activeWorldExportTask, operation)) activeWorldExportTask = null;
            if(ReferenceEquals(worldExportCancellation, cancellation)) worldExportCancellation = null;
            cancellation.Dispose();
            EndWorldExport(owner);
            EndWorldExportLoading(finish, completed);
            if(!isClosing)
            {
                window.SetReplacementStatus(finish);
                StatusText.Text = finish;
            }
        }
    }

    private async Task<string> ExecuteBlockReplacementAsync(IReadOnlyMinecraftWorld world, MapFunctionsWindow window, MinecraftBlockReplacementRequest? request, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        await CancelWorldPreviewAsync();
        await CancelConversionPreviewAsync();
        cancellation.Token.ThrowIfCancellationRequested();
        bool preparing = true;
        void Report(string text)
        {
            if(isClosing || !ReferenceEquals(worldExportCancellation, cancellation)) return;
            window.SetReplacementStatus(text);
            StatusText.Text = text;
            UpdateWorldExportLoadingIndeterminate(request is null ? "扫描地图方块" : "正在替换原地图", text);
        }
        Progress<string> progress = new(text => { if(preparing) Report(text); });
        var service = new MinecraftWorldBlockReplacement();
        if(request is null)
        {
            MinecraftReplacementInventory inventory = await Task.Run(() => service.ScanAsync(world, progress, cancellation.Token));
            preparing = false;
            cancellation.Token.ThrowIfCancellationRequested();
            if(!isClosing && ReferenceEquals(world, mountedWorld)) window.SetReplacementInventory(inventory);
            return $"扫描完成：{inventory.DimensionCount:N0} 个维度，{inventory.ChunkCount:N0} 个区块；来源方块已按数量从多到少排列。";
        }

        string sourceDirectory = world.Descriptor.RootPath;
        MinecraftDimensionId dimension = displayedWorldDimension;
        var camera = FromPreviewRenderPosition(Viewport.CameraTarget);
        await using PreparedMinecraftBlockReplacement prepared = await Task.Run(() => service.PrepareAsync(world, request, progress, cancellation.Token));
        preparing = false;
        cancellation.Token.ThrowIfCancellationRequested();
        if(isClosing || !ReferenceEquals(world, mountedWorld)) throw new OperationCanceledException("当前地图已变化。", cancellation.Token);
        bool detached = false, commitAttempted = false, remountValidated = false;
        isBlockReplacementCommitCritical = true;
        RefreshOperationDisplay();
        try
        {
            Report(UiText.Get("正在卸载地图并提交替换结果"));
            activeWorldSlimmingAnalysis = null;
            InvalidateWorldCropState();
            detached = true;
            await DetachMountedWorldForSlimmingRecoveryAsync();
            commitAttempted = true;
            await Task.Run(prepared.Commit);
            Report(UiText.Get("原地图已写入，正在刷新场景"));
            var outcome = await MountAndDisplaySlimmedWorldAsync(sourceDirectory, dimension, camera,
                new Progress<MinecraftWorldMountProgress>(p => Report(FormatWorldMountProgress(p))));
            remountValidated = true;
            string warning = string.Empty;
            try { await Task.Run(prepared.FinalizeCommit); }
            catch(Exception exception) { warning = $" 临时回滚目录清理未完成：{exception.Message}"; }
            try { StartPostSlimmingWorldStream(outcome); }
            catch(Exception exception) { warning += $" 后台区块加载未启动：{exception.Message}"; }
            return $"已在原地图替换 {MinecraftWorldBlockReplacement.CountMatches(request):N0} 个方块，当前场景已刷新；可重新扫描继续替换。{warning}";
        }
        catch(Exception operationException)
        {
            if(!detached || remountValidated) throw;
            try
            {
                Report(UiText.Get("替换提交未完成，正在恢复原地图"));
                await DetachMountedWorldForSlimmingRecoveryAsync();
                if(commitAttempted) await Task.Run(prepared.Rollback);
                var restored = await MountAndDisplaySlimmedWorldAsync(sourceDirectory, dimension, camera);
                StartPostSlimmingWorldStream(restored);
            }
            catch(Exception recoveryException)
            {
                throw new AggregateException($"{operationException.Message}；自动恢复未完成：{recoveryException.Message}", operationException, recoveryException);
            }
            throw new InvalidOperationException($"原地图已恢复并重新载入：{operationException.Message} {prepared.CleanupWarning}", operationException);
        }
        finally
        {
            isBlockReplacementCommitCritical = false;
            RefreshOperationDisplay();
        }
    }
}
