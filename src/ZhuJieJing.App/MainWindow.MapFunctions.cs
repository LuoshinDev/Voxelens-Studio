using System.Windows;
using ZhuJieJing.App.ConversionPreview;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private MapFunctionsWindow? mapFunctionsWindow;
    private string conversionYOffsetText = string.Empty;
    private string? conversionYOffsetStatus;
    private bool isCalculatingConversionYOffset;
    private bool conversionHeightCannotFit;

    private void MapFunctionsButton_Click(object sender, RoutedEventArgs e)
    {
        MapFunctionsWindow window = EnsureMapFunctionsWindow();
        UpdateMapFunctionsControls();
        window.ShowWindow();
    }

    private MapFunctionsWindow EnsureMapFunctionsWindow()
    {
        if(mapFunctionsWindow is not null) return mapFunctionsWindow;

        MapFunctionsWindow window = new()
        {
            Owner = this,
        };
        TrackTopLevelWindowRenderingSuspension(window);
        window.WorldCropRequested += MapFunctionsWindow_WorldCropRequested;
        window.WorldSlimmingAnalysisRequested += MapFunctionsWindow_WorldSlimmingAnalysisRequested;
        window.WorldSlimmingApplyRequested += MapFunctionsWindow_WorldSlimmingApplyRequested;
        window.ConversionExportRequested += MapFunctionsWindow_ConversionExportRequested;
        window.ConversionSectionOpened += MapFunctionsWindow_ConversionSectionOpened;
        window.ReplacementScanRequested += MapFunctionsWindow_ReplacementScanRequested;
        window.ReplacementApplyRequested += MapFunctionsWindow_ReplacementApplyRequested;
        window.TaskCancellationRequested += (_, _) => CancelOperation_Click(window, new RoutedEventArgs());
        window.ConversionYOffsetText = conversionYOffsetText;
        mapFunctionsWindow = window;
        return window;
    }

    private void MapFunctionsWindow_WorldCropRequested(object? sender, EventArgs e) =>
        OpenWorldCrop_Click(sender ?? this, new RoutedEventArgs());

    private void MapFunctionsWindow_WorldSlimmingAnalysisRequested(object? sender, EventArgs e) =>
        AnalyzeWorldSlimming_Click(sender ?? this, new RoutedEventArgs());

    private void MapFunctionsWindow_WorldSlimmingApplyRequested(object? sender, EventArgs e) =>
        ApplyWorldSlimming_Click(sender ?? this, new RoutedEventArgs());

    private async void MapFunctionsWindow_ConversionExportRequested(object? sender, EventArgs e)
    {
        await ExportCurrentAsWorldFolderAsync();
        if(!isClosing) mapFunctionsWindow?.SetWindowStatus(StatusText.Text);
    }

    private async void MapFunctionsWindow_ConversionSectionOpened(object? sender, EventArgs e)
    {
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(isClosing || isCalculatingConversionYOffset || world is null ||
           !sourceActionsEnabled || activeMountTask is not null || isWorldExporting ||
           displayedContent is not (DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections) ||
           !Minecraft1122ConversionPreviewWorkflow.IsHigherThanTarget(world.Descriptor.Version) ||
           !string.IsNullOrWhiteSpace(GetConversionYOffsetText()) ||
           activeConversionWorkflowTask is not null || activeConversionPreviewTask is not null)
            return;

        Task<IMinecraftDowngradePreview>? workflow = null;
        isCalculatingConversionYOffset = true;
        conversionYOffsetStatus = null;
        conversionHeightCannotFit = false;
        UpdateMapFunctionsControls();
        try
        {
            if(isClosing || !ReferenceEquals(world, mountedWorld)) return;
            IMinecraftBlockDowngradeRules rules = await GetBlockMappingRulesAsync();
            if(isClosing || !sourceActionsEnabled || activeMountTask is not null ||
               !ReferenceEquals(world, mountedWorld) || activeConversionWorkflowTask is not null ||
               activeConversionPreviewTask is not null) return;

            workflow = PrepareCurrentWorldMappingsAsync(world, rules, "计算安全 Y 上移");
            activeConversionWorkflowTask = workflow;
            UpdateConversionControls();
            IMinecraftDowngradePreview preview = await workflow;
            if(isClosing || !ReferenceEquals(world, mountedWorld)) return;

            MinecraftYTranslationLimits translation = preview.Summary.YTranslation;
            if(!translation.CanFitWithoutClipping)
            {
                conversionHeightCannotFit = true;
                conversionYOffsetStatus = translation.OccupiedSourceRange is BlockYRange occupied
                    ? $"地图实际高度为 Y {occupied.Minimum}…{occupied.Maximum}，跨度超过 1.12.2 的 0…255；任何 Y 上移值都无法做到无裁切转换。"
                    : "地图高度无法完整放入 1.12.2 的 Y 0…255，已阻止无损转换。";
                return;
            }

            int suggestedOffset = translation.SuggestedOffset;
            if(suggestedOffset < 0)
            {
                SetConversionYOffsetText(string.Empty);
                conversionYOffsetStatus = translation.OccupiedSourceRange is BlockYRange highRange
                    ? $"地图实际高度为 Y {highRange.Minimum}…{highRange.Maximum}，转换时需要自动下移 {-suggestedOffset} 格；Y 上移保持留空即可无裁切处理。"
                    : "转换时需要自动下移；Y 上移保持留空即可无裁切处理。";
                return;
            }
            SetConversionYOffsetText(suggestedOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            conversionYOffsetStatus = translation.OccupiedSourceRange is BlockYRange sourceRange && sourceRange.Minimum < 0
                ? $"检测到最低方块 Y={sourceRange.Minimum}，已自动填入安全上移 {suggestedOffset}，地下内容不会被 1.12.2 高度下限裁掉。"
                : "地图没有位于 Y=0 以下的方块，Y 上移已自动填入 0。";
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception exception)
        {
            if(!isClosing && ReferenceEquals(world, mountedWorld))
                conversionYOffsetStatus = $"安全 Y 上移暂未预填：{exception.Message}；保持留空仍会在正式转换时自动计算。";
        }
        finally
        {
            if(ReferenceEquals(activeConversionWorkflowTask, workflow)) activeConversionWorkflowTask = null;
            isCalculatingConversionYOffset = false;
            if(!isClosing) UpdateConversionControls();
        }
    }

    private Window GetMapFunctionsDialogOwner() =>
        mapFunctionsWindow is { IsVisible: true } window ? window : this;

    private void PrefillConversionYOffsetIfToolIsVisible()
    {
        if(mapFunctionsWindow is not { IsVisible: true, IsConversionSectionActive: true } window) return;
        MapFunctionsWindow_ConversionSectionOpened(window, EventArgs.Empty);
    }

    private void UpdateMapFunctionsControls()
    {
        bool cropRunning = activeWorldCropTask is not null || isWorldCropCommitCritical;
        bool slimmingRunning = activeWorldSlimmingTask is not null || isWorldSlimmingCommitCritical;
        bool conversionRunning = activeConversionWorkflowTask is not null || activeConversionPreviewTask is not null;
        bool operationRunning = cropRunning || slimmingRunning || conversionRunning ||
                                isCalculatingConversionYOffset || isWorldExporting;
        bool hasWorld = mountedWorld is not null;
        MapFunctionsButton.IsEnabled = !isClosing && (hasWorld || operationRunning);
        UiText.Bind(MapFunctionsButton, "ToolTip", operationRunning ? UiText.Text("打开地图工具并查看任务进度") : UiText.Text("打开地图裁剪、地图瘦身、方块替换与 1.12.2 转换"));

        MapFunctionsWindow? window = mapFunctionsWindow;
        if(window is null) return;

        string? dimensionName = hasWorld
            ? WorldPreview.WorldRegionNavigation.FormatDimensionName(displayedWorldDimension)
            : null;
        window.SetWorldContext(mountedWorld?.Descriptor.LevelName, dimensionName, operationRunning);

        bool showingWorld = displayedContent is DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections;
        bool commonAvailable = hasWorld && showingWorld && sourceActionsEnabled && !isClosing && !operationRunning &&
                               !isWorldExporting && !isChangingMaterialMode && !isNavigationJumping &&
                               activeMountTask is null && activeNavigationPreviewTask is null &&
                               activeConversionWorkflowTask is null;
        bool cropEnabled = commonAvailable && mountedWorld!.ChunkIndex.GetChunkCount(displayedWorldDimension) > 0;
        bool analyzeEnabled = commonAvailable && activeWorldPreviewTask is null;
        bool applyEnabled = analyzeEnabled &&
                            activeWorldSlimmingAnalysis is { RemovableChunkCount: > 0 } analysis &&
                            IsWorldSlimmingAnalysisCurrent(analysis);
        window.SetActionAvailability(cropEnabled, analyzeEnabled, applyEnabled);
        window.SetReplacementContext(mountedWorld?.Descriptor.SourceRevision, commonAvailable && displayedContent == DisplayedContent.WorldSections);
        if(commonAvailable && displayedContent == DisplayedContent.ConvertedWorldSections)
            window.SetReplacementStatus(UiText.Get("请先切回“原始地图”，再扫描和替换；此工具会直接替换原地图中的方块。"));

        bool showConversion = hasWorld && Minecraft1122ConversionPreviewWorkflow.IsHigherThanTarget(
            mountedWorld!.Descriptor.Version);
        bool conversionAvailable = sourceActionsEnabled && showConversion && showingWorld && !isWorldExporting &&
                                   !isChangingMaterialMode && !isNavigationJumping && !cropRunning && !slimmingRunning;
        string conversionStatus = !hasWorld
            ? "请先打开高于 1.12.2 的 Minecraft 世界。"
            : !showConversion
                ? "当前地图已经是 1.12.2 或更早版本，无需转换。"
                : isWorldExporting
                    ? "正在转换并导出 1.12.2 世界，请留意主窗口底部进度。"
                    : isCalculatingConversionYOffset
                        ? "正在扫描全部维度并计算安全 Y 上移；结果会自动填入。"
                    : conversionRunning
                        ? "低版本预览或方块映射正在处理，请稍候。"
                        : !string.IsNullOrWhiteSpace(conversionYOffsetStatus)
                            ? conversionYOffsetStatus
                        : displayedContent == DisplayedContent.ConvertedWorldSections
                            ? "当前正在显示低版本预览；可使用相同 Y 上移值转换并导出完整世界。"
                            : "设置 Y 上移后直接转换并导出 1.12.2 世界；留空时自动计算。";
        window.SetConversionControls(
            conversionStatus,
            conversionAvailable && !conversionRunning && !isCalculatingConversionYOffset,
            conversionAvailable && !conversionRunning && !isCalculatingConversionYOffset && !conversionHeightCannotFit);
    }

    private string GetConversionYOffsetText()
    {
        if(mapFunctionsWindow is not null)
            conversionYOffsetText = mapFunctionsWindow.ConversionYOffsetText;
        return conversionYOffsetText;
    }

    private void SetConversionYOffsetText(string value)
    {
        conversionYOffsetText = value;
        if(value.Length == 0)
        {
            conversionYOffsetStatus = null;
            conversionHeightCannotFit = false;
        }
        if(mapFunctionsWindow is not null)
            mapFunctionsWindow.ConversionYOffsetText = value;
    }

    private void DisableMapConversionControls() => mapFunctionsWindow?.SetConversionControls(
        "1.12.2 转换工具正在处理，请稍候。",
        yOffsetEnabled: false,
        exportEnabled: false);

    private void SetWorldCropSummary(string summary) => SetWorldCropSummary(UiText.Text(summary));

    private void SetWorldCropSummary(Func<string> summary) => mapFunctionsWindow?.SetCropSummary(summary);

    private void SetWorldSlimmingSummary(string summary) => SetWorldSlimmingSummary(UiText.Text(summary));

    private void SetWorldSlimmingSummary(Func<string> summary) => mapFunctionsWindow?.SetSlimmingSummary(summary);

    private void SetWorldCropProgressPresentation(
        string stage,
        bool indeterminate,
        int percentage,
        string progressText)
    {
        StatusText.Text = $"{stage} · {progressText}";
        mapFunctionsWindow?.SetCropProgress(stage, indeterminate, percentage, progressText);
    }

    private void SetWorldSlimmingProgressPresentation(
        string stage,
        bool indeterminate,
        int percentage,
        string progressText)
    {
        StatusText.Text = $"{stage} · {progressText}";
        mapFunctionsWindow?.SetSlimmingProgress(stage, indeterminate, percentage, progressText);
    }

    private void HideWorldCropProgressPresentation() => mapFunctionsWindow?.HideCropProgress();

    private void HideWorldSlimmingProgressPresentation() => mapFunctionsWindow?.HideSlimmingProgress();

    private void CloseMapFunctionsWindowForApplicationExit()
    {
        MapFunctionsWindow? window = mapFunctionsWindow;
        if(window is null) return;

        window.WorldCropRequested -= MapFunctionsWindow_WorldCropRequested;
        window.WorldSlimmingAnalysisRequested -= MapFunctionsWindow_WorldSlimmingAnalysisRequested;
        window.WorldSlimmingApplyRequested -= MapFunctionsWindow_WorldSlimmingApplyRequested;
        window.ConversionExportRequested -= MapFunctionsWindow_ConversionExportRequested;
        window.ConversionSectionOpened -= MapFunctionsWindow_ConversionSectionOpened;
        window.ReplacementScanRequested -= MapFunctionsWindow_ReplacementScanRequested;
        window.ReplacementApplyRequested -= MapFunctionsWindow_ReplacementApplyRequested;
        mapFunctionsWindow = null;
        window.CloseForOwnerShutdown();
    }
}
