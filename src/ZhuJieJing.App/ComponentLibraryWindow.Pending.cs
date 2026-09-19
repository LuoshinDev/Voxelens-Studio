using System.Diagnostics;
using System.IO;
using System.Windows;
using ZhuJieJing.App.Components;

namespace ZhuJieJing.App;

public partial class ComponentLibraryWindow
{
    private bool pendingRefreshRunning;

    private void OnPendingFilesChanged(object sender, FileSystemEventArgs e)
    {
        if(closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if(closed) return;
            if(candidate is not null && (string.Equals(candidate.SourcePath, e.FullPath, StringComparison.OrdinalIgnoreCase) ||
               e is RenamedEventArgs renamed && string.Equals(candidate.SourcePath, renamed.OldFullPath, StringComparison.OrdinalIgnoreCase)))
            {
                previewReady = false;
                InspectedCheckBox.IsChecked = false;
                UiText.Bind(PreviewStateText, "Text", UiText.Text("候选文件已变化，请点击上方“刷新”后检查最新画面。"));
                UpdateActions();
            }
            pendingRefreshTimer.Stop();
            pendingRefreshTimer.Start();
        });
    }

    private async void OnPendingRefreshTick(object? sender, EventArgs e)
    {
        if(busy || pendingRefreshRunning) return;
        pendingRefreshTimer.Stop();
        // A directory update should not cover the current preview with a busy overlay.
        pendingRefreshRunning = true;
        try { await RefreshLibraryAsync(lifetime.Token); }
        catch(OperationCanceledException) { }
        catch(Exception exception) { if(!closed) UiText.Bind(LibraryStatusText, "Text", UiText.Message($"待批准列表刷新失败：{exception.Message}")); }
        finally { pendingRefreshRunning = false; }
        if(!closed && candidate is not null && !File.Exists(candidate.SourcePath)) ClearSelectionPreview();
    }

    private void ClearSelectionPreview()
    {
        candidate = null;
        approvedEntry = null;
        versionParent = null;
        inspectedPlan = null;
        inspectedDelta = null;
        previewReady = false;
        InspectedCheckBox.IsChecked = false;
        ComponentViewport.ClearSections();
        ApprovalPanel.Visibility = Visibility.Collapsed;
        PlacementPanel.Visibility = Visibility.Collapsed;
        PreviewEmptyOverlay.Visibility = Visibility.Visible;
        UiText.Bind(PreviewTitleText, "Text", UiText.Text("请选择一个组件"));
        UiText.Bind(PreviewStateText, "Text", UiText.Text("待批准文件会自动出现；关闭窗口也会保留。"));
        ProvenanceText.Text = "";
        UiText.Bind(PreviewDetailsText, "Text", UiText.Text("选择组件后显示尺寸和材质。"));
        SetMetadata(new("", "建筑", [], "", ""), editable: false);
        UpdateActions();
    }

    private void LibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        if(!uiReady || busy) return;
        ClearSelectionPreview();
        LibrarySearchBox.Clear();
        ApplyFilter();
    }

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            library.EnsureDirectories();
            Process.Start(new ProcessStartInfo(PendingTab.IsChecked == true ? library.PendingDirectory : library.ApprovedDirectory) { UseShellExecute = true });
        }
        catch(Exception exception) { UiText.Bind(LibraryStatusText, "Text", UiText.Message($"无法打开目录：{exception.Message}")); }
    }

    private async Task ShowPendingAsync(PendingComponentEntry entry, ComponentCandidate imported, CancellationToken cancellationToken)
    {
        if(closed) return;
        if(entry.Error is not null) throw new InvalidDataException($"候选信息文件损坏：{entry.Error}。请修复同名 .metadata.json，或移走信息文件后刷新。");
        candidate = imported;
        approvedEntry = null;
        versionParent = entry.Details.PreviousVersion;
        inspectedPlan = imported.Plan;
        inspectedDelta = imported.Compilation.Delta;
        SetMetadata(entry.Metadata, editable: true);
        UiText.Bind(PreviewTitleText, "Text", UiText.Message($"{entry.DisplayTitle} · 待批准"));
        UiText.Bind(PreviewStateText, "Text", UiText.Text("旋转检查各个方向，满意后勾选确认；候选已保存，稍后可继续审核。"));
        UiText.Bind(DetailsTitleText, "Text", UiText.Text("待批准组件"));
        UiText.Bind(ApprovalTargetText, "Text", versionParent is null ? UiText.Text("批准为一个新组件") : UiText.Message($"批准为“{versionParent.Metadata.Name}”的新版本"));
        ApprovalPanel.Visibility = Visibility.Visible;
        PlacementPanel.Visibility = Visibility.Collapsed;
        UiText.Bind(ProvenanceText, "Text", UiText.Message($"文件：{imported.SourcePath}\n源蓝图：{imported.Plan.PlanId}\nSHA-256：{imported.SourceSha256}"));
        InspectedCheckBox.IsChecked = false;
        await PreparePreviewAsync(0, cancellationToken);
        if(!closed) UiText.Bind(LibraryStatusText, "Text", UiText.Text("候选已显示，可批准、保存信息或移至回收站。"));
    }

    private async void SavePending_Click(object sender, RoutedEventArgs e)
    {
        if(candidate is not ComponentCandidate selected || busy) return;
        ComponentLibraryMetadata metadata = new(ComponentNameBox.Text, ComponentCategoryBox.Text,
            ComponentTagsBox.Text.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), ComponentNotesBox.Text, ComponentSourceBox.Text);
        await RunOperationAsync("正在保存待批准信息", async cancellationToken =>
        {
            await library.SavePendingDetailsAsync(selected.SourcePath, new(metadata, versionParent), cancellationToken);
            await RefreshLibraryAsync(cancellationToken);
            UiText.Bind(LibraryStatusText, "Text", UiText.Text("待批准信息已保存。"));
        }, invalidatePreview: false);
    }

    private async void RemovePending_Click(object sender, RoutedEventArgs e)
    {
        if(busy || LibraryList.SelectedItem is not PendingComponentEntry selected) return;
        await RunOperationAsync("正在移至回收站", async cancellationToken =>
        {
            library.RecyclePending(selected.Path);
            ClearSelectionPreview();
            await RefreshLibraryAsync(cancellationToken);
            UiText.Bind(LibraryStatusText, "Text", UiText.Message($"已将 {selected.DisplayTitle} 移至回收站，可在系统回收站恢复。"));
        });
    }
}
