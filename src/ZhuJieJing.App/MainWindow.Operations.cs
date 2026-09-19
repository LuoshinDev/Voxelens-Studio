using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private CancellationTokenSource? photographyCancellation;
    private CancellationTokenSource? comparisonCancellation;
    private CancellationTokenSource? schematicCancellation;
    private DispatcherTimer? operationDisplayTimer;
    private readonly Stopwatch operationElapsed = new();

    private void InitializeOperationDisplay()
    {
        operationDisplayTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => RefreshOperationDisplay(), Dispatcher);
        Closed += (_, _) => { operationDisplayTimer.Stop(); CancelForegroundOperations(); };
    }

    private IEnumerable<CancellationTokenSource> ForegroundOperations()
    {
        CancellationTokenSource?[] sources = [mountCancellation, worldPreviewCancellation,
            conversionPreviewCancellation, contentTransitionCancellation, worldExportCancellation,
            planCompilationCancellation, materialModeCancellation, worldCropCancellation,
            worldSlimmingCancellation, aiProjectReloadCancellation, photographyCancellation, comparisonCancellation, schematicCancellation];
        return sources.OfType<CancellationTokenSource>().Distinct();
    }

    private void RefreshOperationDisplay()
    {
        CancellationTokenSource[] operations = ForegroundOperations().ToArray();
        bool busy = operations.Length > 0;
        bool validating = busy && worldValidationOwnerGeneration is int validationGeneration &&
                          worldPreviewLoadingOwnerGeneration == validationGeneration;
        WorldValidationOverlay.Visibility = validating ? Visibility.Visible : Visibility.Collapsed;
        ActiveOperationPanel.Visibility = busy && !validating ? Visibility.Visible : Visibility.Collapsed;
        mapFunctionsWindow?.SetTaskCancellation(busy, !isBlockReplacementCommitCritical && !isWorldCropCommitCritical && !isWorldSlimmingCommitCritical && operations.Any(source => !source.IsCancellationRequested));
        if(!busy) { operationElapsed.Reset(); return; }
        if(!operationElapsed.IsRunning) operationElapsed.Start();
        bool cancelling = operations.All(source => source.IsCancellationRequested);
        bool committing = isBlockReplacementCommitCritical || isWorldCropCommitCritical || isWorldSlimmingCommitCritical;
        CancelOperationButton.IsEnabled = !cancelling && !committing;
        if(isWorldExporting) CancelExportButton.IsEnabled = !committing && worldExportCancellation is { IsCancellationRequested: false };
        if(validating)
        {
            WorldValidationStageText.Text = cancelling ? UiText.Get("正在取消打开…") : worldPreviewLoadingStage;
            UiText.Bind(WorldValidationProgressText, "Text", UiText.Message($"{worldPreviewLoadingProgressText} · 已用时 {operationElapsed.Elapsed:mm\\:ss}"));
            WorldValidationProgressBar.IsIndeterminate = worldPreviewLoadingIndeterminate;
            if(!worldPreviewLoadingIndeterminate) WorldValidationProgressBar.Value = worldPreviewLoadingProgress;
            CancelWorldValidationButton.IsEnabled = !cancelling && !committing;
        }
        UiText.Bind(ActiveOperationTimeText, "Text", committing ? UiText.Text("正在完成安全提交") : cancelling ? UiText.Text("正在安全取消…") : UiText.Message($"已用时 {operationElapsed.Elapsed:mm\\:ss}"));
        if(MaterialLoadingOverlay.Visibility == Visibility.Visible)
            ActiveOperationStageText.Text = MaterialLoadingStageText.Text;
        else if(worldPreviewLoadingOwnerGeneration is not null)
            ActiveOperationStageText.Text = $"{worldPreviewLoadingStage} · {worldPreviewLoadingProgressText}";
        else ActiveOperationStageText.Text = StatusText.Text;
        ActiveOperationProgress.IsIndeterminate = worldPreviewLoadingOwnerGeneration is null || worldPreviewLoadingIndeterminate;
        if(!ActiveOperationProgress.IsIndeterminate) ActiveOperationProgress.Value = worldPreviewLoadingProgress;
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if(isBlockReplacementCommitCritical || isWorldCropCommitCritical || isWorldSlimmingCommitCritical) return;
        CancelForegroundOperations();
        UiText.Bind(StatusText, "Text", UiText.Text("正在取消当前任务，已完成的施工版本会保留。"));
        RefreshOperationDisplay();
    }

    private void CancelForegroundOperations()
    {
        if(isBlockReplacementCommitCritical || isWorldCropCommitCritical || isWorldSlimmingCommitCritical) return;
        foreach(CancellationTokenSource cancellation in ForegroundOperations())
        {
            try { cancellation.Cancel(); } catch(ObjectDisposedException) { }
        }
    }
}
