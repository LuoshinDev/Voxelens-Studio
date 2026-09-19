using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZhuJieJing.App.Projects;
using ZhuJieJing.App.Preferences;
using ZhuJieJing.Core;
using ZhuJieJing.Core.Collaboration;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Controls;
using ZhuJieJing.Renderer.Meshing;
using Microsoft.Win32;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private static readonly TimeSpan AiStatusStaleAfter = TimeSpan.FromMinutes(2);
    private AiProjectSession? aiProjectSession;
    private CancellationTokenSource? aiProjectReloadCancellation;
    private CancellationTokenSource? aiProjectResourceTransitionOwner;
    private ResourceSelection? aiProjectPreviousResourceSelection;
    private ResourceSelection? aiProjectPreviousOriginalWorldResourceSelection;
    private readonly SemaphoreSlim aiProjectOpenGate = new(1, 1);
    private readonly SemaphoreSlim aiProjectReloadGate = new(1, 1);
    private int aiProjectReloadGeneration;
    private string? aiProjectObservedRevision;
    private string? aiProjectPreviewedPlanSha256;
    private string? aiProjectPreviewedRevision;
    private IReadOnlyList<ZhujieBatchRecord> aiProjectBatches = [];
    private int? aiProjectBrowseBatchIndex;
    private string? aiProjectPreviewError;
    private bool aiProjectHasPreview;
    private readonly Dictionary<string, ZhujiePreviewReceipt> aiProjectPendingReceipts = new(StringComparer.Ordinal);
    private AiProjectRefreshPump? aiProjectRefreshPump;
    private AiProjectSession? aiProjectDashboardReadOwner;
    private Task<AiProjectDashboard>? aiProjectDashboardReadTask;
    private bool isAiBatchTransitionActive;

    private async void OpenAiProject_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AiProjectSession.DefaultWorkspaceRoot);
        OpenFolderDialog dialog = new()
        {
            Title = UiText.Get("选择 AI 筑界工程"),
            Multiselect = false,
        };
        FileDialogLocations.Shared.Configure(dialog, MapFileKind.AiProject, AiProjectSession.DefaultWorkspaceRoot);
        if(dialog.ShowDialog(this) != true) return;
        FileDialogLocations.Shared.RememberDirectory(MapFileKind.AiProject, dialog.FolderName);
        await OpenAiProjectPathAsync(dialog.FolderName);
    }

    internal void HandleStudioCommand(ZhujieStudioCommand command)
    {
        ActivateStudioWindow();
        if(string.Equals(command.Action, ZhujieStudioCommand.OpenProjectAction, StringComparison.Ordinal) &&
           !string.IsNullOrWhiteSpace(command.ProjectDirectory))
            _ = OpenAiProjectPathAsync(command.ProjectDirectory);
    }

    internal async Task OpenAiProjectPathAsync(string projectDirectory)
    {
        await aiProjectOpenGate.WaitAsync();
        AiProjectSession? candidate = null;
        bool projectCheckOverlayActive = false;
        try
        {
            string normalizedProjectDirectory = Path.GetFullPath(projectDirectory);
            if(aiProjectSession is not null && string.Equals(
                   aiProjectSession.Project.ProjectDirectory,
                   normalizedProjectDirectory,
                   StringComparison.OrdinalIgnoreCase))
            {
                ActivateStudioWindow();
                await RefreshAiProjectAsync(aiProjectSession, forcePlanReload: true, frameFirstPreview: false);
                return;
            }

            UiText.Bind(StatusText, "Text", UiText.Text("正在检查 AI 筑界工程……"));
            BeginAiBatchLoading(UiText.Get("正在检查 AI 筑界工程"));
            projectCheckOverlayActive = true;
            UpdateAiBatchLoadingProgress("正在校验工程目录与协议文件", 1, 4);
            await Dispatcher.Yield(DispatcherPriority.Render);
            candidate = await AiProjectSession.OpenAsync(normalizedProjectDirectory);
            UpdateAiBatchLoadingProgress("工程协议有效，正在连接增量场景存储", 2, 4);
            if(isClosing)
            {
                candidate.Dispose();
                return;
            }
            UpdateAiBatchLoadingProgress("工程检查完成，正在切换预览上下文", 3, 4);

            chunkLoadReloadTimer.Stop();
            SetSourceOpenButtonsEnabled(false);
            SetCoordinateJumpButtonsEnabled(false);
            mountCancellation?.Cancel();
            mountCancellation?.Dispose();
            mountCancellation = null;
            planCompilationCancellation?.Cancel();
            planCompilationCancellation?.Dispose();
            planCompilationCancellation = null;
            worldPreviewCancellation?.Cancel();
            navigationPreviewCancellation?.Cancel();
            conversionPreviewCancellation?.Cancel();
            contentTransitionCancellation?.Cancel();
            worldExportCancellation?.Cancel();
            materialModeCancellation?.Cancel();

            IReadOnlyMinecraftWorld? previousWorld = mountedWorld;
            mountedWorld = null;
            InvalidateWorldSlimmingAnalysis();
            InvalidateWorldCropState();
            ClearWorldRegionNavigation();

            CloseAiProjectSession();
            ClearDisplayedContent();
            Viewport.ClearSections();
            activeResourceSelection = null;
            originalWorldResourceSelection = null;
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            await Dispatcher.Yield(DispatcherPriority.Render);
            await CancelWorldExportAsync();
            await CancelWorldPreviewAsync();
            await CancelConversionPreviewAsync();
            if(previousWorld is not null) await previousWorld.DisposeAsync();
            ClearDisplayedContent();
            Viewport.ClearSections();
            activeResourceSelection = null;
            originalWorldResourceSelection = null;
            Viewport.ClearMinecraftResources();
            UpdateRenderInfoText();
            aiProjectSession = candidate;
            aiProjectRefreshPump = new AiProjectRefreshPump(
                batch => RefreshAiProjectPassAsync(candidate, batch),
                SupersedeActiveAiPlanWork);
            candidate.RefreshRequested += AiProjectSession_RefreshRequested;
            aiProjectObservedRevision = null;
            aiProjectPreviewedPlanSha256 = null;
            aiProjectPreviewedRevision = null;
            aiProjectBatches = [];
            aiProjectBrowseBatchIndex = null;
            aiProjectPreviewError = null;
            aiProjectHasPreview = false;
            AiCollaborationCard.Visibility = Visibility.Visible;
            SetAiProjectControlsEnabled(true);
            UiText.Bind(AiStateText, "Text", UiText.Text("正在连接"));
            AiStatusDot.Fill = CreateStatusBrush(0x76, 0x51, 0xD2);
            UiText.Bind(AiStageText, "Text", UiText.Text("正在读取增量场景 revision……"));
            WorldNameText.Text = candidate.Project.DisplayName;
            UiText.Bind(WorldVersionText, "Text", UiText.Text("版本： 等待 AI 蓝图"));
            WorldVersionText.ToolTip = null;
            WorldPathText.Text = candidate.Project.ProjectDirectory;
            WorldPathText.ToolTip = candidate.Project.SceneStorePath;
            UpdateConversionControls();
            UpdateAiBatchLoadingProgress("正在读取 AI 施工 revision", 4, 4);
            await RefreshAiProjectAsync(candidate, forcePlanReload: true, frameFirstPreview: true);
            UpdateAiBatchLoadingProgress("AI 筑界工程已载入", 1, 1);
            EndAiBatchLoading();
            projectCheckOverlayActive = false;
            ActivateStudioWindow();
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException or
                                               InvalidDataException or NotSupportedException or ArgumentException or
                                               InvalidOperationException or ZhujieProtocolValidationException)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"无法打开 AI 筑界工程：{exception.Message}"));
        }
        finally
        {
            if(projectCheckOverlayActive) EndAiBatchLoading();
            if(candidate is not null && !ReferenceEquals(aiProjectSession, candidate)) candidate.Dispose();
            if(!isClosing)
            {
                SetSourceOpenButtonsEnabled(true);
                SetCoordinateJumpButtonsEnabled(true);
            }
            aiProjectOpenGate.Release();
        }
    }

    private void ActivateStudioWindow()
    {
        if(WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if(!IsVisible) Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void AiProjectSession_RefreshRequested(
        object? sender,
        AiProjectRefreshRequestedEventArgs e)
    {
        if(sender is not AiProjectSession session || isClosing) return;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => _ = QueueAiProjectRefreshAsync(
                session,
                e.Kind,
                forcePlanReload: false,
                frameFirstPreview: false));
    }

    private Task RefreshAiProjectAsync(
        AiProjectSession session,
        bool forcePlanReload,
        bool frameFirstPreview)
    {
        AiProjectRefreshKind kind = AiProjectRefreshKind.Dashboard | AiProjectRefreshKind.RevisionIntegrityProbe;
        if(forcePlanReload) kind |= AiProjectRefreshKind.RevisionWriteHint;
        return QueueAiProjectRefreshAsync(session, kind, forcePlanReload, frameFirstPreview);
    }

    private Task QueueAiProjectRefreshAsync(
        AiProjectSession session,
        AiProjectRefreshKind kind,
        bool forcePlanReload,
        bool frameFirstPreview)
    {
        if(isClosing || !ReferenceEquals(aiProjectSession, session) || aiProjectRefreshPump is null)
            return Task.CompletedTask;
        return aiProjectRefreshPump.EnqueueAsync(kind, forcePlanReload, frameFirstPreview);
    }

    private async Task RefreshAiProjectPassAsync(
        AiProjectSession session,
        AiProjectRefreshBatch batch)
    {
        if(isClosing || !ReferenceEquals(aiProjectSession, session)) return;
        int generationAtEntry = Volatile.Read(ref aiProjectReloadGeneration);
        try
        {
            AiProjectDashboard dashboard = await ReadAiProjectDashboardAsync(session, batch.Kind);
            if(!ReferenceEquals(aiProjectSession, session) || isClosing) return;
            if(generationAtEntry != Volatile.Read(ref aiProjectReloadGeneration)) return;
            UpdateAiProjectDashboard(dashboard);
            await RetryPendingAiPreviewReceiptsAsync(session);
            if(!ReferenceEquals(aiProjectSession, session) || isClosing) return;
            if(isWorldExporting || isChangingMaterialMode || isNavigationJumping) return;

            int generation = Interlocked.Increment(ref aiProjectReloadGeneration);
            aiProjectReloadCancellation?.Cancel();
            CancellationTokenSource requestCancellation = new();
            aiProjectReloadCancellation = requestCancellation;
            CancellationToken cancellationToken = requestCancellation.Token;
            UpdateAiBatchLoadingStage("正在定位施工批次 revision");

            bool reloadGateEntered = false;
            AiRevisionSnapshot? snapshot = null;
            try
            {
                await aiProjectReloadGate.WaitAsync(cancellationToken);
                reloadGateEntered = true;
                EnsureCurrentAiReload(session, generation, requestCancellation);
                string? requestedRevision = aiProjectBrowseBatchIndex is int browseIndex &&
                                            browseIndex >= 0 && browseIndex < aiProjectBatches.Count
                    ? aiProjectBatches[browseIndex].Revision
                    : null;
                IProgress<AiRevisionLoadProgress>? revisionProgress = CreateAiRevisionLoadingProgress();
                AiRevisionLoadResult revisionLoad = await session.LoadRevisionAsync(
                    aiProjectObservedRevision,
                    requestedRevision,
                    batch.ForceRevisionReload,
                    cancellationToken,
                    revisionProgress);
                EnsureCurrentAiReload(session, generation, requestCancellation);
                if(revisionLoad.Kind == AiRevisionLoadKind.Unchanged) return;
                snapshot = revisionLoad.Snapshot;
                if(snapshot is null || revisionLoad.Kind == AiRevisionLoadKind.Empty || snapshot.Batch is null)
                {
                    aiProjectObservedRevision = "root";
                    aiProjectBatches = snapshot?.Batches ?? [];
                    aiProjectPreviewError = aiProjectHasPreview ? null : "等待 AI 发布第一批增量施工内容";
                    UpdateAiBatchControls();
                    UpdateAiProjectDashboard(dashboard);
                    UiText.Bind(StatusText, "Text", aiProjectHasPreview ? UiText.Text("当前工程尚无新 revision。") : UiText.Text("AI 筑界工程已打开，正在等待第一批施工补丁。"));
                    return;
                }
                UiText.Bind(AiStateText, "Text", UiText.Text("正在载入"));
                AiStatusDot.Fill = CreateStatusBrush(0x76, 0x51, 0xD2);
                UiText.Bind(AiStageText, "Text", UiText.Text("发现新的施工 revision，正在解析并编译……"));
                AiProgressBar.IsIndeterminate = true;
                UiText.Bind(AiProgressText, "Text", UiText.Text("编译中"));
                aiProjectObservedRevision = snapshot.Revision;
                aiProjectBatches = snapshot.Batches;
                UpdateAiBatchControls();
                IProgress<SectionMeshBuildProgress>? conversionProgress = CreateAiBatchSectionProgress(
                    $"正在解析第 {snapshot.Batch.Sequence:N0} 批场景");
                ZjjPlan plan = snapshot.Plan ?? CreateDisplayPlan(snapshot.Batch);
                PlanVisualProfileSelection visual = SelectPlanVisualProfile(plan);
                IReadOnlyList<NormalizedMinecraftSection> sections = await Task.Run(
                    () => ConvertStoredSections(snapshot.Scene, cancellationToken, conversionProgress),
                    cancellationToken);
                EnsureCurrentAiReload(session, generation, requestCancellation);

                bool firstPreview = !aiProjectHasPreview;
                bool resourcesChanged = firstPreview ||
                                        displayedPlanVisualVersion != visual.Version ||
                                        displayedPlanVisualProfileInferred != visual.Inferred;
                if(resourcesChanged && snapshot.IsIncremental)
                {
                    AiRevisionLoadResult fullLoad = await session.LoadRevisionAsync(
                        observedRevision: null,
                        requestedRevision: snapshot.Revision,
                        force: true,
                        cancellationToken);
                    snapshot = fullLoad.Snapshot ?? throw new InvalidDataException("无法读取资源切换所需的完整 revision。");
                    conversionProgress = CreateAiBatchSectionProgress($"正在还原第 {snapshot.Batch!.Sequence:N0} 批完整场景");
                    sections = await Task.Run(
                        () => ConvertStoredSections(snapshot.Scene, cancellationToken, conversionProgress),
                        cancellationToken);
                }
                string resourceStatus = "沿用当前 Minecraft 材质";
                if(resourcesChanged)
                {
                    BeginMaterialLoading(UiText.Get("正在匹配 AI 蓝图的 Minecraft 材质"));
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    try
                    {
                        Viewport.BeginResourceTransition();
                        aiProjectResourceTransitionOwner = requestCancellation;
                        aiProjectPreviousResourceSelection = activeResourceSelection;
                        aiProjectPreviousOriginalWorldResourceSelection = originalWorldResourceSelection;
                        Viewport.ClearSections();
                        resourceStatus = await ConfigurePlanResourcesAsync(
                            snapshot.PlanPath ?? session.Project.SceneStorePath,
                            visual.Version,
                            visual.Inferred,
                            cancellationToken);
                        IProgress<SectionMeshBuildProgress> progress = CreateMaterialSectionProgress(() =>
                            IsCurrentAiReload(session, generation, requestCancellation));
                        await Viewport.SynchronizeSparseSectionsAsync(sections, cancellationToken, progress);
                        EnsureCurrentAiReload(session, generation, requestCancellation);
                        UpdateAiBatchLoadingStage("正在发布施工批次到显存");
                        Viewport.PrepareResourceTransitionForPublication();
                        await Viewport.WaitForSectionGpuPublicationAsync(cancellationToken);
                        EnsureAiSessionStillActiveAfterPublication(session);
                        CompleteAiResourceTransition(requestCancellation);
                    }
                    catch
                    {
                        CancelAiResourceTransition(requestCancellation);
                        throw;
                    }
                    finally
                    {
                        EndMaterialLoading();
                    }
                }
                else
                {
                    IProgress<SectionMeshBuildProgress> progress = new Progress<SectionMeshBuildProgress>(value =>
                    {
                        if(!IsCurrentAiReload(session, generation, requestCancellation)) return;
                        int total = Math.Max(0, value.TotalSections);
                        int completed = Math.Clamp(value.CompletedSections, 0, total);
                        AiProgressBar.IsIndeterminate = total == 0;
                        AiProgressBar.Value = total == 0 ? 0 : completed * 100d / total;
                        AiProgressText.Text = total == 0 ? UiText.Get("发布中") : $"{completed:N0}/{total:N0}";
                        UpdateAiBatchLoadingProgress("正在重建施工批次 Section", completed, total);
                    });
                    if(snapshot.IsIncremental)
                    {
                        await Viewport.ReplaceSparseSectionsAsync(sections, cancellationToken, progress);
                        HashSet<SectionCoordinate> present = sections.Select(static section => section.Coordinate).ToHashSet();
                        SectionCoordinate[] removed = snapshot.Delta!.Sections.Select(static section => section.Section)
                            .Where(section => !present.Contains(section))
                            .ToArray();
                        if(removed.Length > 0) Viewport.RemoveSections(removed);
                    }
                    else
                        await Viewport.SynchronizeSparseSectionsAsync(sections, cancellationToken, progress);
                    UpdateAiBatchLoadingStage("正在发布施工批次到显存");
                    await Viewport.WaitForSectionGpuPublicationAsync(cancellationToken);
                }
                displayedPlanPreview = snapshot.Delta;
                displayedPlanSections = snapshot.IsIncremental
                    ? MergeDisplayedSections(displayedPlanSections, sections, snapshot.Delta!)
                    : sections;
                displayedPlanDimension = new MinecraftDimensionId(plan.Dimension);
                displayedPlanSourcePath = session.Project.SceneStorePath;
                displayedPlanSpawnPoint = snapshot.Batch!.SpawnPoint;
                displayedPlanVisualProfile = visual.Profile;
                displayedPlanVisualVersion = visual.Version;
                displayedPlanVisualProfileInferred = visual.Inferred;
                displayedSchematic = null;
                displayedWorldSections.Clear();
                displayedWorldChunkStatistics.Clear();
                displayedContent = DisplayedContent.PlanPreview;
                aiProjectHasPreview = true;
                aiProjectPreviewedPlanSha256 = snapshot.Batch.PlanSha256;
                aiProjectPreviewedRevision = snapshot.Revision;
                aiProjectPreviewError = null;
                UpdateRenderInfoText();
                WorldNameText.Text = session.Project.DisplayName;
                UiText.Bind(WorldVersionText, "Text", UiText.Message($"版本： {FormatPlanMinecraftVersion(visual.Version)}"));
                WorldVersionText.ToolTip = FormatPlanVisualProfile(visual.Version, visual.Inferred);
                PlanFormatCard.Visibility = Visibility.Visible;
                WorldPathText.Text = session.Project.ProjectDirectory;
                WorldPathText.ToolTip = session.Project.SceneStorePath;
                ShowAiSceneStatistics(displayedPlanSections, FormatPlanDataVersion(visual.Version));
                UpdateConversionControls();
                SetCoordinateJumpButtonsEnabled(true);

                if(firstPreview && batch.FrameFirstPreview) FrameFirstAiPlanAtSpawn(plan);
                UiText.Bind(AiStateText, "Text", UiText.Text("预览已同步"));
                AiStatusDot.Fill = CreateStatusBrush(0x55, 0xB5, 0x88);
                AiStageText.Text = UiText.Format($"批次 {snapshot.Batch.Sequence:N0}/{snapshot.Batches.Count:N0} · ") +
                                   UiText.Format($"{snapshot.Batch.Module.DisplayName} · {snapshot.Batch.ChangedVoxelCount:N0} 个变化方块");
                AiProgressBar.IsIndeterminate = false;
                AiProgressBar.Value = 100;
                UiText.Bind(AiProgressText, "Text", UiText.Text("已显示"));
                UiText.Bind(StatusText, "Text", UiText.Message($"AI 增量批次已实时更新：{plan.PlanId}。{resourceStatus}。"));
                bool isLatest = aiProjectBrowseBatchIndex is null && snapshot.Batch == snapshot.Batches.LastOrDefault();
                if(isLatest && snapshot.Batch.Kind == ZhujieBatchKind.Construction)
                    await TryWriteAiPreviewReceiptAsync(session, new ZhujiePreviewReceipt
                {
                    ProjectId = session.Project.ProjectId,
                    PlanSha256 = snapshot.Batch.PlanSha256,
                    Revision = snapshot.Revision,
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    Status = ZhujiePreviewReceiptState.Previewed,
                    PlanId = plan.PlanId,
                    SceneDeltaHash = snapshot.Batch.SceneDeltaHash,
                    ChangedVoxelCount = snapshot.Batch.ChangedVoxelCount,
                    SectionCount = snapshot.Batch.SectionCount,
                    Errors = [],
                }, "预览已显示");
            }
            catch(IOException) when(snapshot is null)
            {
                if(ReferenceEquals(aiProjectSession, session) && !isClosing)
                {
                    UiText.Bind(AiStageText, "Text", UiText.Text("场景 revision 暂时不可读取，等待稳定后重试……"));
                    UiText.Bind(StatusText, "Text", aiProjectHasPreview ? UiText.Text("蓝图文件暂时不可读取；继续显示上一张有效预览。") : UiText.Text("蓝图文件暂时不可读取；稳定后会自动载入。"));
                }
            }
            catch(OperationCanceledException)
            {
                if(snapshot is not null && ReferenceEquals(aiProjectSession, session) &&
                   generation != Volatile.Read(ref aiProjectReloadGeneration))
                {
                    if(string.Equals(aiProjectObservedRevision, snapshot.Revision, StringComparison.Ordinal))
                        aiProjectObservedRevision = null;
                    if(snapshot.Batch?.Kind == ZhujieBatchKind.Construction)
                        await TryWriteAiPreviewReceiptAsync(session, new ZhujiePreviewReceipt
                    {
                        ProjectId = session.Project.ProjectId,
                        PlanSha256 = snapshot.Batch.PlanSha256,
                        Revision = snapshot.Revision,
                        ObservedAtUtc = DateTimeOffset.UtcNow,
                        Status = ZhujiePreviewReceiptState.Superseded,
                        Errors = [],
                    }, "旧预览已被新版本替代");
                }
            }
            catch(Exception exception) when(snapshot is not null && exception is
                (IOException or UnauthorizedAccessException or JsonException or PlanValidationException or
                 ZhujieProtocolValidationException or ArgumentException or InvalidDataException))
            {
                await RejectAiPlanAsync(
                    session,
                    snapshot,
                    [new ZhujiePreviewReceiptError
                    {
                        Code = "plan.load",
                        Path = "$",
                        Message = FormatPlanLoadException(exception),
                    }],
                    generation,
                    requestCancellation);
            }
            finally
            {
                if(reloadGateEntered) aiProjectReloadGate.Release();
                if(ReferenceEquals(aiProjectReloadCancellation, requestCancellation))
                    aiProjectReloadCancellation = null;
                requestCancellation.Dispose();
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException or
                                               InvalidDataException or NotSupportedException or ArgumentException or
                                               InvalidOperationException)
        {
            if(!ReferenceEquals(aiProjectSession, session) || isClosing) return;
            aiProjectPreviewError = exception.Message;
            UiText.Bind(AiStateText, "Text", UiText.Text("协作读取失败"));
            AiStatusDot.Fill = CreateStatusBrush(0xD5, 0x68, 0x68);
            AiStageText.Text = exception.Message;
            UiText.Bind(StatusText, "Text", UiText.Message($"AI 筑界工程读取失败：{exception.Message}"));
        }
    }

    private async Task RejectAiPlanAsync(
        AiProjectSession session,
        AiRevisionSnapshot snapshot,
        IReadOnlyList<ZhujiePreviewReceiptError> errors,
        int generation,
        CancellationTokenSource requestCancellation)
    {
        if(!IsCurrentAiReload(session, generation, requestCancellation)) return;
        string message = errors.FirstOrDefault()?.Message ?? UiText.Get("蓝图无效。");
        aiProjectPreviewError = aiProjectHasPreview
            ? $"蓝图无效，已保留当前工程上一张有效预览：{message}"
            : $"蓝图无效，当前工程尚无有效预览：{message}";
        UiText.Bind(AiStateText, "Text", UiText.Text("蓝图无效"));
        AiStatusDot.Fill = CreateStatusBrush(0xD5, 0x68, 0x68);
        AiStageText.Text = aiProjectPreviewError;
        AiProgressBar.IsIndeterminate = false;
        AiProgressBar.Value = 0;
        UiText.Bind(AiProgressText, "Text", UiText.Text("未采用"));
        StatusText.Text = aiProjectPreviewError;
        if(snapshot.Batch?.Kind != ZhujieBatchKind.Construction) return;
        await TryWriteAiPreviewReceiptAsync(session, new ZhujiePreviewReceipt
        {
            ProjectId = session.Project.ProjectId,
            PlanSha256 = snapshot.Batch.PlanSha256,
            Revision = snapshot.Revision,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Status = ZhujiePreviewReceiptState.Invalid,
            Errors = errors,
        }, "蓝图未采用");
    }

    private static ZjjPlan CreateDisplayPlan(ZhujieBatchRecord batch) => new()
    {
        PlanId = batch.PlanId,
        Dimension = batch.Dimension,
        VisualProfile = batch.VisualProfile,
        Base = new ZjjSceneReference
        {
            Scene = ZhujieProjectLayout.SceneStoreRelativePath,
            Revision = batch.BaseRevision,
        },
        Module = batch.Module,
        SpawnPoint = batch.SpawnPoint,
    };

    private static IReadOnlyList<NormalizedMinecraftSection> ConvertStoredSections(
        ZhuJieJing.SceneStore.StoredSceneSnapshot snapshot,
        CancellationToken cancellationToken,
        IProgress<SectionMeshBuildProgress>? progress)
    {
        var result = new List<NormalizedMinecraftSection>(snapshot.Sections.Count);
        int total = snapshot.Sections.Count;
        int reportStride = Math.Max(1, (total + 199) / 200);
        progress?.Report(new SectionMeshBuildProgress(0, total, null));
        foreach(ZhuJieJing.SceneStore.StoredSceneSection stored in snapshot.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var palette = new List<BlockState> { BlockState.Air };
            var lookup = new Dictionary<BlockState, ushort> { [BlockState.Air] = 0 };
            var indices = new ushort[4096];
            foreach((int localIndex, BlockState state) in stored.Blocks)
            {
                if(state.IsAir) continue;
                if(!lookup.TryGetValue(state, out ushort paletteIndex))
                {
                    paletteIndex = checked((ushort)palette.Count);
                    palette.Add(state);
                    lookup.Add(state, paletteIndex);
                }
                indices[localIndex] = paletteIndex;
            }
            result.Add(new NormalizedMinecraftSection(stored.Coordinate, palette, indices, null, []));
            int completed = result.Count;
            if(completed == total || completed % reportStride == 0)
                progress?.Report(new SectionMeshBuildProgress(completed, total, stored.Coordinate));
        }
        return result;
    }

    private static IReadOnlyList<NormalizedMinecraftSection> MergeDisplayedSections(
        IReadOnlyList<NormalizedMinecraftSection> current,
        IReadOnlyList<NormalizedMinecraftSection> replacements,
        SceneDelta delta)
    {
        Dictionary<SectionCoordinate, NormalizedMinecraftSection> merged = current
            .ToDictionary(static section => section.Coordinate);
        foreach(SectionDelta changed in delta.Sections) merged.Remove(changed.Section);
        foreach(NormalizedMinecraftSection replacement in replacements) merged[replacement.Coordinate] = replacement;
        return merged.Values.OrderBy(static section => section.Coordinate).ToArray();
    }

    private void ShowAiSceneStatistics(IReadOnlyList<NormalizedMinecraftSection> sections, string dataVersion)
    {
        long blockCount = 0;
        BlockPosition? minimum = null;
        BlockPosition? maximum = null;
        foreach(NormalizedMinecraftSection section in sections)
        {
            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            for(int localIndex = 0; localIndex < indices.Length; localIndex++)
            {
                if(section.Palette[indices[localIndex]].IsAir) continue;
                BlockPosition position = section.Coordinate.ToBlockPosition(localIndex);
                blockCount++;
                minimum = minimum is BlockPosition min
                    ? new BlockPosition(Math.Min(min.X, position.X), Math.Min(min.Y, position.Y), Math.Min(min.Z, position.Z))
                    : position;
                maximum = maximum is BlockPosition max
                    ? new BlockPosition(Math.Max(max.X, position.X), Math.Max(max.Y, position.Y), Math.Max(max.Z, position.Z))
                    : position;
            }
        }
        string size = minimum is BlockPosition minPosition && maximum is BlockPosition maxPosition
            ? $"{(long)maxPosition.X - minPosition.X + 1:N0} × {(long)maxPosition.Y - minPosition.Y + 1:N0} × {(long)maxPosition.Z - minPosition.Z + 1:N0}"
            : "—";
        string origin = minimum is BlockPosition originPosition
            ? $"{originPosition.X:N0}, {originPosition.Y:N0}, {originPosition.Z:N0}"
            : "—";
        ShowSceneStatistics(
            ".zjjscene",
            dataVersion,
            "方块数量",
            blockCount.ToString("N0"),
            size,
            $"{CountChunks(sections):N0} / {CountChunks(sections):N0}",
            "0",
            origin);
    }

    private async Task RetryPendingAiPreviewReceiptsAsync(AiProjectSession session)
    {
        foreach(ZhujiePreviewReceipt receipt in aiProjectPendingReceipts.Values.ToArray())
        {
            if(!ReferenceEquals(aiProjectSession, session) || isClosing) return;
            await TryWriteAiPreviewReceiptAsync(session, receipt, "预览结果已保留");
        }
    }

    private async Task<bool> TryWriteAiPreviewReceiptAsync(
        AiProjectSession session,
        ZhujiePreviewReceipt receipt,
        string resultDescription)
    {
        try
        {
            bool written = await session.WritePreviewReceiptAsync(receipt);
            if(!written)
            {
                RemovePendingAiPreviewReceipt(receipt);
                if(ReferenceEquals(aiProjectSession, session) && !isClosing)
                {
                    UiText.Bind(StatusText, "Text", UiText.Message($"{resultDescription}，但同一蓝图已有更强状态或冲突的预览回执，筑界镜未覆盖原回执。"));
                }
                return false;
            }
            RemovePendingAiPreviewReceipt(receipt);
            return true;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or ArgumentException or
                                              ZhujieProtocolValidationException)
        {
            if(!ReferenceEquals(aiProjectSession, session) || isClosing) return false;
            RememberPendingAiPreviewReceipt(receipt);
            UiText.Bind(StatusText, "Text", UiText.Message($"{resultDescription}，但预览回执写入失败：{exception.Message}；将自动重试。"));
            return false;
        }
    }

    private void RememberPendingAiPreviewReceipt(ZhujiePreviewReceipt receipt)
    {
        if(!aiProjectPendingReceipts.TryGetValue(receipt.PlanSha256, out ZhujiePreviewReceipt? pending) ||
           PreviewReceiptStrength(receipt.Status) >= PreviewReceiptStrength(pending.Status))
        {
            aiProjectPendingReceipts[receipt.PlanSha256] = receipt;
        }
    }

    private void RemovePendingAiPreviewReceipt(ZhujiePreviewReceipt receipt)
    {
        if(aiProjectPendingReceipts.TryGetValue(receipt.PlanSha256, out ZhujiePreviewReceipt? pending) &&
           ReferenceEquals(pending, receipt))
        {
            aiProjectPendingReceipts.Remove(receipt.PlanSha256);
        }
    }

    private static int PreviewReceiptStrength(ZhujiePreviewReceiptState state) => state switch
    {
        ZhujiePreviewReceiptState.Previewed => 3,
        ZhujiePreviewReceiptState.Invalid => 2,
        _ => 1,
    };

    private void UpdateAiProjectDashboard(AiProjectDashboard dashboard)
    {
        if(aiProjectSession is null) return;
        if(AiInstructionHistoryItems.ItemsSource is not IEnumerable<AiInstructionHistoryItem> previous || !previous.SequenceEqual(dashboard.Instructions))
            AiInstructionHistoryItems.ItemsSource = dashboard.Instructions;
        aiProjectBatches = dashboard.Batches;
        if(aiProjectBrowseBatchIndex is int browseIndex && browseIndex >= aiProjectBatches.Count)
            aiProjectBrowseBatchIndex = null;
        UpdateAiBatchControls();
        if(!string.IsNullOrWhiteSpace(aiProjectPreviewError))
        {
            UiText.Bind(AiStateText, "Text", aiProjectPreviewError.StartsWith(UiText.Get("等待"), StringComparison.Ordinal) ? UiText.Text("等待蓝图") : UiText.Text("蓝图无效"));
            AiStatusDot.Fill = aiProjectPreviewError.StartsWith("等待", StringComparison.Ordinal)
                ? CreateStatusBrush(0xAF, 0xA6, 0xBC)
                : CreateStatusBrush(0xD5, 0x68, 0x68);
            AiStageText.Text = aiProjectPreviewError;
        }

        ZhujieArchitectStatus? status = dashboard.ArchitectStatus;
        if(status is null)
        {
            AiStatusAgeText.Text = dashboard.ReadError ?? UiText.Get("尚未收到 AI 状态；指令会保留在队列中");
            if(string.IsNullOrWhiteSpace(aiProjectPreviewError))
            {
                UiText.Bind(AiStateText, "Text", UiText.Text("等待 AI"));
                AiStatusDot.Fill = CreateStatusBrush(0xAF, 0xA6, 0xBC);
                UiText.Bind(AiStageText, "Text", UiText.Text("工程已连接，等待建筑师写入状态。"));
            }
            AiProgressBar.IsIndeterminate = false;
            AiProgressBar.Value = 0;
            AiProgressText.Text = "—";
            return;
        }

        TimeSpan age = DateTimeOffset.UtcNow - status.UpdatedAtUtc;
        bool stale = age > AiStatusStaleAfter || status.UpdatedAtUtc == DateTimeOffset.MinValue;
        string statusAge = stale
            ? $"AI 状态可能过期 · {FormatStatusAge(age)}"
            : $"AI 更新于 {FormatStatusAge(age)}前{FormatAgentSuffix(status.Agent)}";
        AiStatusAgeText.Text = string.IsNullOrWhiteSpace(dashboard.ReadError)
            ? statusAge
            : $"{statusAge} · {dashboard.ReadError}";
        if(string.IsNullOrWhiteSpace(aiProjectPreviewError))
        {
            AiStateText.Text = stale ? UiText.Get("状态可能过期") : FormatArchitectState(status.State);
            AiStatusDot.Fill = stale
                ? CreateStatusBrush(0xD3, 0x9A, 0x4C)
                : CreateArchitectStateBrush(status.State);
            AiStageText.Text = JoinStatusMessage(status.Phase, status.Message);
        }

        if(status.Progress is ZhujieArchitectProgress progress && progress.Total > 0)
        {
            double percentage = Math.Clamp(progress.Completed / (double)progress.Total * 100d, 0d, 100d);
            AiProgressBar.IsIndeterminate = false;
            AiProgressBar.Value = percentage;
            AiProgressText.Text = $"{progress.Completed:N0}/{progress.Total:N0}{FormatUnit(progress.Unit)}";
        }
        else
        {
            bool active = status.State is ZhujieArchitectState.Planning or
                ZhujieArchitectState.Building or ZhujieArchitectState.Validating;
            AiProgressBar.IsIndeterminate = active;
            AiProgressBar.Value = 0;
            UiText.Bind(AiProgressText, "Text", active ? UiText.Text("进行中") : UiText.Text("—"));
        }
    }

    private async void QueueAiInstruction_Click(object sender, RoutedEventArgs e) =>
        await QueueAiInstructionAsync(ZhujieInstructionKind.Instruction, AiInstructionTextBox.Text);

    private async void PauseAi_Click(object sender, RoutedEventArgs e) =>
        await QueueAiInstructionAsync(ZhujieInstructionKind.Pause, "请在当前安全检查点暂停施工，并回写暂停状态。");

    private async void ResumeAi_Click(object sender, RoutedEventArgs e) =>
        await QueueAiInstructionAsync(ZhujieInstructionKind.Resume, "请从最近安全检查点继续施工。");

    private async void CancelAiCurrent_Click(object sender, RoutedEventArgs e) =>
        await QueueAiInstructionAsync(ZhujieInstructionKind.CancelCurrent, "请取消当前施工批次，保留最后一张有效蓝图。");

    private async Task QueueAiInstructionAsync(ZhujieInstructionKind kind, string text)
    {
        AiProjectSession? session = aiProjectSession;
        if(session is null || string.IsNullOrWhiteSpace(text)) return;
        SetAiInstructionControlsEnabled(false);
        try
        {
            BlockPosition? camera = null;
            if(aiProjectHasPreview)
            {
                Vector3 cameraTarget = FromPreviewRenderPosition(Viewport.CameraTarget);
                camera = new BlockPosition(
                    ClampCoordinate(cameraTarget.X),
                    ClampCoordinate(cameraTarget.Y),
                    ClampCoordinate(cameraTarget.Z));
            }
            string dimension = displayedPlanDimension?.Value ?? session.Project.TargetDimension;
            await session.QueueInstructionAsync(
                kind,
                text,
                new AiInstructionContextSnapshot(dimension, camera, aiProjectPreviewedRevision));
            if(kind == ZhujieInstructionKind.Instruction) AiInstructionTextBox.Clear();
            UiText.Bind(StatusText, "Text", UiText.Text("施工指令已排队；收到 acknowledgement 前不会显示为 AI 已读取。"));
            await QueueAiProjectRefreshAsync(
                session,
                AiProjectRefreshKind.Dashboard,
                forcePlanReload: false,
                frameFirstPreview: false);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or ArgumentException or
                                              InvalidDataException or ZhujieProtocolValidationException)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"施工指令排队失败：{exception.Message}"));
        }
        finally
        {
            if(ReferenceEquals(aiProjectSession, session) && !isClosing) SetAiInstructionControlsEnabled(true);
        }
    }

    private async void DeleteQueuedAiInstruction_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not FrameworkElement { DataContext: AiInstructionHistoryItem instruction } ||
           !instruction.CanDelete || aiProjectSession is not AiProjectSession session)
            return;
        if(UiMessageBox.Show(
               this,
               UiText.Format($"删除这条尚未被 AI 读取的排队指令？\n{instruction.DisplayText}"),
               UiText.Get("删除排队指令"),
               MessageBoxButton.YesNo,
               MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            bool deleted = await session.DeleteQueuedInstructionAsync(instruction.InstructionId);
            UiText.Bind(StatusText, "Text", deleted ? UiText.Text("排队指令已删除，AI 将不会再读取它。") : UiText.Text("这条指令已被 AI 读取，不能再从排队列表删除。"));
            await QueueAiProjectRefreshAsync(
                session,
                AiProjectRefreshKind.Dashboard,
                forcePlanReload: false,
                frameFirstPreview: false);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"删除排队指令失败：{exception.Message}"));
        }
    }

    private void AiInstructionTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if(e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        _ = QueueAiInstructionAsync(ZhujieInstructionKind.Instruction, AiInstructionTextBox.Text);
    }

    private void OpenAiProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        AiProjectSession? session = aiProjectSession;
        if(session is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(session.Project.ProjectDirectory) { UseShellExecute = true });
        }
        catch(Exception exception) when(exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"无法打开工程目录：{exception.Message}"));
        }
    }

    private void ReloadAiProject_Click(object sender, RoutedEventArgs e)
    {
        AiProjectSession? session = aiProjectSession;
        if(session is null) return;
        _ = RefreshAiProjectAsync(session, forcePlanReload: true, frameFirstPreview: false);
    }

    private async void PreviousAiBatch_Click(object sender, RoutedEventArgs e)
    {
        if(aiProjectSession is not AiProjectSession session || aiProjectBatches.Count == 0) return;
        aiProjectBrowseBatchIndex = aiProjectBrowseBatchIndex is int current
            ? Math.Max(0, current - 1)
            : Math.Max(0, aiProjectBatches.Count - 2);
        aiProjectObservedRevision = null;
        UpdateAiBatchControls();
        int target = aiProjectBrowseBatchIndex.Value;
        await RunAiBatchTransitionAsync(
            session,
            $"正在切换到第 {target + 1:N0} / {aiProjectBatches.Count:N0} 批",
            () => RefreshAiProjectAsync(session, forcePlanReload: true, frameFirstPreview: false),
            "切换上一批失败");
    }

    private async void NextAiBatch_Click(object sender, RoutedEventArgs e)
    {
        if(aiProjectSession is not AiProjectSession session || aiProjectBrowseBatchIndex is not int current) return;
        aiProjectBrowseBatchIndex = current >= aiProjectBatches.Count - 1 ? null : current + 1;
        aiProjectObservedRevision = null;
        UpdateAiBatchControls();
        int target = aiProjectBrowseBatchIndex ?? aiProjectBatches.Count - 1;
        await RunAiBatchTransitionAsync(
            session,
            $"正在切换到第 {target + 1:N0} / {aiProjectBatches.Count:N0} 批",
            () => RefreshAiProjectAsync(session, forcePlanReload: true, frameFirstPreview: false),
            "切换下一批失败");
    }

    private async void DeleteAiBatchContent_Click(object sender, RoutedEventArgs e)
    {
        if(aiProjectSession is not AiProjectSession session || aiProjectBatches.Count == 0) return;
        int index = aiProjectBrowseBatchIndex ?? aiProjectBatches.Count - 1;
        ZhujieBatchRecord selected = aiProjectBatches[index];
        if(selected.Kind == ZhujieBatchKind.Revert) return;
        if(UiMessageBox.Show(
               this,
               UiText.Format($"删除“{selected.Module.DisplayName}”的第 {selected.Sequence:N0} 批内容？\n筑界镜会生成一个可继续前进/回退的新 revision，不会破坏历史。"),
               UiText.Get("删除当前批次内容"),
               MessageBoxButton.YesNo,
               MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunAiBatchTransitionAsync(
            session,
            $"正在删除第 {selected.Sequence:N0} 批内容",
            async () =>
            {
                await session.RevertBatchAsync(selected.Revision);
                aiProjectBrowseBatchIndex = null;
                aiProjectObservedRevision = null;
                UpdateAiBatchLoadingStage("正在载入删除后的最新批次");
                await RefreshAiProjectAsync(session, forcePlanReload: true, frameFirstPreview: false);
            },
            "删除批次内容失败");
    }

    private async Task RunAiBatchTransitionAsync(
        AiProjectSession session,
        string initialStage,
        Func<Task> operation,
        string failurePrefix)
    {
        BeginAiBatchLoading(initialStage);
        await Dispatcher.Yield(DispatcherPriority.Render);
        try
        {
            await operation();
            if(!ReferenceEquals(aiProjectSession, session) || isClosing) return;
            UpdateAiBatchLoadingProgress("施工批次已显示", 1, 1);
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                               InvalidOperationException or ArgumentException or ZhujieProtocolValidationException)
        {
            if(ReferenceEquals(aiProjectSession, session) && !isClosing)
                StatusText.Text = $"{failurePrefix}：{exception.Message}";
        }
        finally
        {
            EndAiBatchLoading();
        }
    }

    private void BeginAiBatchLoading(string stage)
    {
        isAiBatchTransitionActive = true;
        AiBatchLoadingOverlay.BeginAnimation(OpacityProperty, null);
        AiBatchLoadingOverlay.Opacity = 1d;
        AiBatchLoadingOverlay.IsHitTestVisible = true;
        AiBatchLoadingOverlay.Visibility = Visibility.Visible;
        AiBatchLoadingStageText.Text = stage;
        UiText.Bind(AiBatchLoadingProgressText, "Text", UiText.Text("准备中"));
        AiBatchLoadingProgressBar.IsIndeterminate = true;
        AiBatchLoadingProgressBar.Value = 0d;
        UpdateAiBatchControls();
    }

    private void UpdateAiBatchLoadingStage(string stage)
    {
        if(!isAiBatchTransitionActive) return;
        AiBatchLoadingStageText.Text = stage;
        UiText.Bind(AiBatchLoadingProgressText, "Text", UiText.Text("准备中"));
        AiBatchLoadingProgressBar.IsIndeterminate = true;
        AiBatchLoadingProgressBar.Value = 0d;
    }

    private IProgress<AiRevisionLoadProgress>? CreateAiRevisionLoadingProgress()
    {
        if(!isAiBatchTransitionActive) return null;
        return new Progress<AiRevisionLoadProgress>(value =>
            UpdateAiBatchLoadingProgress(value.Stage, value.CompletedSteps, value.TotalSteps));
    }

    private IProgress<SectionMeshBuildProgress>? CreateAiBatchSectionProgress(string stage)
    {
        if(!isAiBatchTransitionActive) return null;
        return new Progress<SectionMeshBuildProgress>(value =>
            UpdateAiBatchLoadingProgress(stage, value.CompletedSections, value.TotalSections));
    }

    private void UpdateAiBatchLoadingProgress(string stage, int completedItems, int totalItems)
    {
        if(!isAiBatchTransitionActive) return;
        int total = Math.Max(0, totalItems);
        int completed = Math.Clamp(completedItems, 0, total);
        AiBatchLoadingStageText.Text = stage;
        AiBatchLoadingProgressText.Text = total == 0 ? UiText.Get("发布中") : $"{completed:N0} / {total:N0}";
        AiBatchLoadingProgressBar.IsIndeterminate = total == 0;
        AiBatchLoadingProgressBar.Value = total == 0 ? 0d : completed * 100d / total;
    }

    private void EndAiBatchLoading()
    {
        isAiBatchTransitionActive = false;
        AiBatchLoadingOverlay.IsHitTestVisible = false;
        AiBatchLoadingProgressBar.IsIndeterminate = false;
        UpdateAiBatchControls();
        DoubleAnimation fade = new(
            AiBatchLoadingOverlay.Opacity,
            0d,
            TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            if(isAiBatchTransitionActive) return;
            AiBatchLoadingOverlay.Visibility = Visibility.Collapsed;
            AiBatchLoadingOverlay.Opacity = 1d;
        };
        AiBatchLoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateAiBatchControls()
    {
        int count = aiProjectBatches.Count;
        int index = count == 0 ? -1 : aiProjectBrowseBatchIndex ?? count - 1;
        bool canChangeBatch = aiProjectSession is not null && !isAiBatchTransitionActive;
        UiText.Bind(AiBatchPositionText, "Text", count == 0 ? UiText.Text("施工批次 0 / 0") : UiText.Message($"施工批次 {index + 1:N0} / {count:N0} · {aiProjectBatches[index].Module.DisplayName}"));
        PreviousAiBatchButton.IsEnabled = canChangeBatch && index > 0;
        NextAiBatchButton.IsEnabled = canChangeBatch && aiProjectBrowseBatchIndex is not null;
        DeleteAiBatchContentButton.IsEnabled = canChangeBatch && index >= 0 &&
                                                     aiProjectBatches[index].Kind == ZhujieBatchKind.Construction;
    }

    private void CloseAiProjectSession()
    {
        AiProjectRefreshPump? refreshPump = aiProjectRefreshPump;
        aiProjectRefreshPump = null;
        if(refreshPump is null) SupersedeActiveAiPlanWork();
        else refreshPump.Stop();
        aiProjectReloadCancellation = null;
        CancelAiResourceTransition();
        AiProjectSession? session = aiProjectSession;
        aiProjectSession = null;
        if(session is not null)
        {
            session.RefreshRequested -= AiProjectSession_RefreshRequested;
            session.Dispose();
        }
        aiProjectObservedRevision = null;
        aiProjectPreviewedPlanSha256 = null;
        aiProjectPreviewedRevision = null;
        aiProjectBatches = [];
        aiProjectBrowseBatchIndex = null;
        aiProjectPreviewError = null;
        aiProjectHasPreview = false;
        aiProjectPendingReceipts.Clear();
        aiProjectDashboardReadOwner = null;
        aiProjectDashboardReadTask = null;
        if(isClosing) return;
        AiCollaborationCard.Visibility = Visibility.Collapsed;
        SetAiProjectControlsEnabled(false);
        UiText.Bind(AiStateText, "Text", UiText.Text("未打开工程"));
        AiStatusDot.Fill = CreateStatusBrush(0xAF, 0xA6, 0xBC);
        UiText.Bind(AiStageText, "Text", UiText.Text("工程打开后会自动跟踪增量 revision"));
        UiText.Bind(AiStatusAgeText, "Text", UiText.Text("尚未收到 AI 状态"));
        AiProgressBar.IsIndeterminate = false;
        AiProgressBar.Value = 0;
        AiProgressText.Text = "—";
        AiInstructionHistoryItems.ItemsSource = null;
        UpdateAiBatchControls();
    }

    private void SupersedeActiveAiPlanWork()
    {
        Interlocked.Increment(ref aiProjectReloadGeneration);
        aiProjectReloadCancellation?.Cancel();
    }

    private void CompleteAiResourceTransition(CancellationTokenSource owner)
    {
        if(!ReferenceEquals(aiProjectResourceTransitionOwner, owner)) return;
        aiProjectResourceTransitionOwner = null;
        aiProjectPreviousResourceSelection = null;
        aiProjectPreviousOriginalWorldResourceSelection = null;
    }

    private void CancelAiResourceTransition(CancellationTokenSource? owner = null)
    {
        if(aiProjectResourceTransitionOwner is null ||
           (owner is not null && !ReferenceEquals(aiProjectResourceTransitionOwner, owner))) return;
        Viewport.CancelResourceTransition();
        activeResourceSelection = aiProjectPreviousResourceSelection;
        originalWorldResourceSelection = aiProjectPreviousOriginalWorldResourceSelection;
        aiProjectResourceTransitionOwner = null;
        aiProjectPreviousResourceSelection = null;
        aiProjectPreviousOriginalWorldResourceSelection = null;
        UpdateRenderInfoText();
    }

    private void SetAiProjectControlsEnabled(bool enabled)
    {
        AiInstructionTextBox.IsEnabled = enabled;
        QueueAiInstructionButton.IsEnabled = enabled;
        PauseAiButton.IsEnabled = enabled;
        ResumeAiButton.IsEnabled = enabled;
        CancelAiCurrentButton.IsEnabled = enabled;
        OpenAiProjectFolderButton.IsEnabled = enabled;
        ReloadAiProjectButton.IsEnabled = enabled;
        if(enabled) UpdateAiBatchControls();
        else
        {
            PreviousAiBatchButton.IsEnabled = false;
            NextAiBatchButton.IsEnabled = false;
            DeleteAiBatchContentButton.IsEnabled = false;
        }
    }

    private Task<AiProjectDashboard> ReadAiProjectDashboardAsync(
        AiProjectSession session,
        AiProjectRefreshKind refreshKind)
    {
        if(!ReferenceEquals(aiProjectDashboardReadOwner, session) ||
           aiProjectDashboardReadTask is null || aiProjectDashboardReadTask.IsCompleted)
        {
            aiProjectDashboardReadOwner = session;
            aiProjectDashboardReadTask = session.ReadDashboardAsync(refreshKind);
        }
        return aiProjectDashboardReadTask;
    }

    private void SetAiInstructionControlsEnabled(bool enabled)
    {
        AiInstructionTextBox.IsEnabled = enabled;
        QueueAiInstructionButton.IsEnabled = enabled;
        PauseAiButton.IsEnabled = enabled;
        ResumeAiButton.IsEnabled = enabled;
        CancelAiCurrentButton.IsEnabled = enabled;
    }

    private void EnsureCurrentAiReload(
        AiProjectSession session,
        int generation,
        CancellationTokenSource requestCancellation)
    {
        requestCancellation.Token.ThrowIfCancellationRequested();
        if(!IsCurrentAiReload(session, generation, requestCancellation))
            throw new OperationCanceledException(requestCancellation.Token);
    }

    private void EnsureAiSessionStillActiveAfterPublication(AiProjectSession session)
    {
        // GPU publication is the transition's point of no return: the previous atlas and buffers have been
        // released. A newer plan in this same project should queue behind this pass, while a source switch must
        // still prevent the old pass from restoring its displayed state after the new source cleared the viewport.
        if(isClosing || !ReferenceEquals(aiProjectSession, session))
            throw new OperationCanceledException();
    }

    private bool IsCurrentAiReload(
        AiProjectSession session,
        int generation,
        CancellationTokenSource requestCancellation) =>
        !isClosing && !requestCancellation.IsCancellationRequested && ReferenceEquals(aiProjectSession, session) &&
        ReferenceEquals(aiProjectReloadCancellation, requestCancellation) &&
        generation == Volatile.Read(ref aiProjectReloadGeneration);

    private void FrameFirstAiPlanAtSpawn(ZjjPlan plan)
    {
        BoxSelection? firstSelection = plan.Selections.Values.Cast<BoxSelection?>().FirstOrDefault();
        float initialDistance = 48f;
        if(firstSelection is BoxSelection selection)
        {
            float selectionSpan = Math.Max(
                selection.MaxExclusive.X - selection.Min.X,
                Math.Max(
                    selection.MaxExclusive.Y - selection.Min.Y,
                    selection.MaxExclusive.Z - selection.Min.Z));
            initialDistance = Math.Clamp(selectionSpan * 1.2f, 24f, 96f);
        }

        BlockPosition spawn = plan.SpawnPoint ?? new BlockPosition(0, 32, 0);
        Viewport.SetNavigationMode(ViewportNavigationMode.Orbit);
        Viewport.FrameAt(new Vector3(spawn.X, spawn.Y, spawn.Z), initialDistance);
        XCoordinate.Text = spawn.X.ToString();
        YCoordinate.Text = spawn.Y.ToString();
        ZCoordinate.Text = spawn.Z.ToString();
    }

    private static string FormatPlanLoadException(Exception exception) => exception switch
    {
        JsonException json => UiText.Format($"JSON 无效（行 {json.LineNumber ?? 0}，字节 {json.BytePositionInLine ?? 0}）：{json.Message}"),
        DecoderFallbackException => UiText.Get("蓝图必须是有效的 UTF-8 文本。"),
        _ => exception.Message,
    };

    private static int ClampCoordinate(float value)
    {
        if(!float.IsFinite(value)) return 0;
        return (int)Math.Clamp(MathF.Floor(value), int.MinValue, int.MaxValue);
    }

    private static ZhujiePreviewReceiptError ToReceiptError(ValidationIssue issue) => new()
    {
        Code = issue.Code,
        Path = issue.Path,
        Message = issue.Message,
    };

    private static string FormatArchitectState(ZhujieArchitectState state) => state switch
    {
        ZhujieArchitectState.Idle => UiText.Get("空闲"),
        ZhujieArchitectState.Planning => UiText.Get("规划中"),
        ZhujieArchitectState.Building => UiText.Get("施工中"),
        ZhujieArchitectState.Validating => UiText.Get("校验中"),
        ZhujieArchitectState.WaitingForUser => UiText.Get("等待指挥"),
        ZhujieArchitectState.Paused => UiText.Get("已暂停"),
        ZhujieArchitectState.Completed => UiText.Get("已完成"),
        ZhujieArchitectState.Error => UiText.Get("AI 异常"),
        _ => state.ToString(),
    };

    private static Brush CreateArchitectStateBrush(ZhujieArchitectState state) => state switch
    {
        ZhujieArchitectState.Completed => CreateStatusBrush(0x55, 0xB5, 0x88),
        ZhujieArchitectState.Error => CreateStatusBrush(0xD5, 0x68, 0x68),
        ZhujieArchitectState.WaitingForUser or ZhujieArchitectState.Paused => CreateStatusBrush(0xD3, 0x9A, 0x4C),
        ZhujieArchitectState.Planning or ZhujieArchitectState.Building or ZhujieArchitectState.Validating =>
            CreateStatusBrush(0x76, 0x51, 0xD2),
        _ => CreateStatusBrush(0xAF, 0xA6, 0xBC),
    };

    private static SolidColorBrush CreateStatusBrush(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));

    private static string JoinStatusMessage(string? phase, string? message)
    {
        if(string.IsNullOrWhiteSpace(phase)) return string.IsNullOrWhiteSpace(message) ? "AI 未提供当前阶段说明" : message;
        return string.IsNullOrWhiteSpace(message) ? phase : $"{phase} · {message}";
    }

    private static string FormatStatusAge(TimeSpan age)
    {
        if(age < TimeSpan.Zero) return UiText.Get("刚刚");
        if(age < TimeSpan.FromSeconds(5)) return UiText.Get("刚刚");
        if(age < TimeSpan.FromMinutes(1)) return UiText.Format($"{Math.Max(1, (int)age.TotalSeconds)} 秒");
        if(age < TimeSpan.FromHours(1)) return UiText.Format($"{Math.Max(1, (int)age.TotalMinutes)} 分钟");
        return UiText.Format($"{Math.Max(1, (int)age.TotalHours)} 小时");
    }

    private static string FormatAgentSuffix(string? agent) =>
        string.IsNullOrWhiteSpace(agent) ? string.Empty : $" · {agent}";

    private static string FormatUnit(string? unit) => string.IsNullOrWhiteSpace(unit) ? string.Empty : $" {unit}";
}
