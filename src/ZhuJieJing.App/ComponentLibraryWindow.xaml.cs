using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ZhuJieJing.App.Components;
using ZhuJieJing.App.Resources;
using ZhuJieJing.App.Preferences;
using ZhuJieJing.Core;
using ZhuJieJing.Core.Components;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Controls;
using ZhuJieJing.Renderer.Meshing;
using ZhuJieJing.Renderer.Minecraft;

namespace ZhuJieJing.App;

public partial class ComponentLibraryWindow : Window
{
    private readonly ComponentLibraryStore library = ComponentLibraryStore.CreateDefault();
    private readonly MinecraftResourceCacheService resourceCache = new();
    private readonly Func<ComponentPlacementRequest, CancellationToken, Task>? useComponentAsync;
    private readonly string? targetDimension;
    private readonly ZjjVisualProfile? preferredVisualProfile;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operationCancellation;
    private TaskCompletionSource<bool>? visibleFrame;
    private IReadOnlyList<ComponentLibraryEntry> entries = [];
    private IReadOnlyList<PendingComponentEntry> pendingEntries = [];
    private FileSystemWatcher? pendingWatcher;
    private readonly DispatcherTimer pendingRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private ComponentCandidate? candidate;
    private ComponentLibraryEntry? approvedEntry;
    private ComponentLibraryEntry? versionParent;
    private ZjjPlan? inspectedPlan;
    private SceneDelta? inspectedDelta;
    private BoxSelection? previewBounds;
    private string? activeResourceVersion;
    private bool previewReady;
    private bool busy;
    private bool uiReady;
    private bool closed;
    private bool synchronizingSelection;
    private string resourceStatus = string.Empty;

    public ComponentLibraryWindow(Func<ComponentPlacementRequest, CancellationToken, Task>? useComponentAsync = null,
        BlockPosition? initialAnchor = null, string? targetDimension = null, ZjjVisualProfile? preferredVisualProfile = null)
    {
        this.useComponentAsync = useComponentAsync;
        this.targetDimension = targetDimension;
        this.preferredVisualProfile = preferredVisualProfile;
        InitializeComponent();
        // WPF work-area units already account for desktop scaling. Keep the initial
        // window usable on smaller 125% desktops; detail fields scroll independently.
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24));
        BlockPosition anchor = initialAnchor ?? new(0, 32, 0);
        AnchorXBox.Text = anchor.X.ToString(CultureInfo.InvariantCulture);
        AnchorYBox.Text = anchor.Y.ToString(CultureInfo.InvariantCulture);
        AnchorZBox.Text = anchor.Z.ToString(CultureInfo.InvariantCulture);
        if(useComponentAsync is not null) UiText.Bind(PlacementHintText, "Text", UiText.Text("交给当前工作区处理：生成放置草稿并由 AI 按 revision 施工，或作为独立蓝图打开。"));
        ComponentViewport.SetNavigationMode(ViewportNavigationMode.Orbit);
        ComponentViewport.SetTerrainMode(ViewportTerrainMode.Transparent);
        ComponentViewport.SetFogEnabled(false);
        ComponentViewport.SetCloudsEnabled(false);
        ComponentViewport.SetTimeOfDay(14f);
        ComponentViewport.FrameRateUpdated += OnFrameRateUpdated;
        pendingRefreshTimer.Tick += OnPendingRefreshTick;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        uiReady = true;
        UpdateActions();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("正在读取组件库", RefreshLibraryAsync);
        if(closed || !Directory.Exists(library.PendingDirectory)) return;
        try
        {
            pendingWatcher = new FileSystemWatcher(library.PendingDirectory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            pendingWatcher.Created += OnPendingFilesChanged;
            pendingWatcher.Changed += OnPendingFilesChanged;
            pendingWatcher.Deleted += OnPendingFilesChanged;
            pendingWatcher.Renamed += OnPendingFilesChanged;
            pendingWatcher.Error += (_, _) => Dispatcher.BeginInvoke(() => { if(!closed) pendingRefreshTimer.Start(); });
            pendingWatcher.EnableRaisingEvents = true;
        }
        catch(IOException exception) { UiText.Bind(LibraryStatusText, "Text", UiText.Message($"自动刷新暂不可用，请点击刷新：{exception.Message}")); }
    }
    private async void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    {
        string? selectedPath = (LibraryList.SelectedItem as PendingComponentEntry)?.Path;
        await RunOperationAsync("正在刷新组件库", async cancellationToken =>
        {
            await RefreshLibraryAsync(cancellationToken);
            if(PendingTab.IsChecked == true && selectedPath is not null && pendingEntries.FirstOrDefault(entry => entry.Path == selectedPath) is { } selected)
            {
                ClearSelectionPreview();
                ComponentCandidate imported = await library.ImportCandidateAsync(selected.Path, cancellationToken);
                await ShowPendingAsync(selected, imported, cancellationToken);
            }
        }, invalidatePreview: false);
    }
    private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e) { if(uiReady) ApplyFilter(); }
    private void LibraryFilter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void CopyAuthoringPrompt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ZjjVisualProfile profile = preferredVisualProfile ?? inspectedPlan?.VisualProfile ?? ComponentAuthoringPrompt.DefaultVisualProfile;
            library.EnsureDirectories();
            Clipboard.SetText(ComponentAuthoringPrompt.Create(profile, targetDimension, library.PendingDirectory));
            UiText.Bind(LibraryStatusText, "Text", UiText.Message($"制作要求已复制到本机剪贴板 · Java {profile.VersionName} · 请补充想制作的素材后交给 AI。"));
        }
        catch(System.Runtime.InteropServices.ExternalException)
        {
            UiText.Bind(LibraryStatusText, "Text", UiText.Text("剪贴板正被其他程序占用，请稍后再点“复制 AI 制作要求”。"));
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
            UiText.Bind(LibraryStatusText, "Text", UiText.Message($"无法准备待批准目录：{exception.Message}"));
        }
    }

    private async Task RefreshLibraryAsync(CancellationToken cancellationToken)
    {
        ComponentLibraryListing listing = await library.ListAsync(cancellationToken);
        IReadOnlyList<PendingComponentEntry> pending = await library.ListPendingAsync(cancellationToken);
        if(closed) return;
        entries = listing.Entries;
        pendingEntries = pending;
        ApplyFilter();
        UiText.Bind(LibraryStatusText, "Text", listing.Errors.Count == 0 ? UiText.Message($"待批准 {pendingEntries.Count:N0} 项 · 已批准 {entries.Count:N0} 个版本 · 新文件自动刷新") : UiText.Message($"已读取 {entries.Count:N0} 个版本；{listing.Errors.Count:N0} 条损坏记录已隔离。请查看状态提示。"));
        LibraryStatusText.ToolTip = listing.Errors.Count == 0 ? library.RootDirectory : string.Join(Environment.NewLine, listing.Errors);
    }

    private void ApplyFilter()
    {
        if(!uiReady) return;
        string query = LibrarySearchBox.Text.Trim();
        PendingCountText.Text = pendingEntries.Count.ToString("N0");
        ApprovedCountText.Text = entries.Select(entry => entry.ComponentId).Distinct().Count().ToString("N0");
        System.Windows.Automation.AutomationProperties.SetName(PendingTab, $"待批准 {PendingCountText.Text} 项");
        System.Windows.Automation.AutomationProperties.SetName(ApprovedTab, $"已批准 {ApprovedCountText.Text} 项");
        LatestOnlyToggle.Visibility = PendingTab.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        if(PendingTab.IsChecked == true)
        {
            var pending = pendingEntries.Where(entry => query.Length == 0 || entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            string? selectedPath = (LibraryList.SelectedItem as PendingComponentEntry)?.Path ?? candidate?.SourcePath;
            synchronizingSelection = true;
            try
            {
                LibraryList.ItemsSource = pending;
                LibraryList.SelectedItem = pending.FirstOrDefault(entry => entry.Path == selectedPath);
            }
            finally { synchronizingSelection = false; }
            UiText.Bind(LibraryCountText, "Text", UiText.Message($"显示 {pending.Length:N0} 项 · 共 {pendingEntries.Count:N0} 个待批准文件"));
            UpdateActions();
            return;
        }
        IEnumerable<ComponentLibraryEntry> visible = entries;
        if(LatestOnlyToggle.IsChecked == true)
            visible = visible.GroupBy(entry => entry.ComponentId).Select(group => group.MaxBy(entry => entry.Version)!);
        ComponentLibraryEntry[] filtered = visible.Where(entry => query.Length == 0 || entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.ApprovedAtUtc).ToArray();
        synchronizingSelection = true;
        try
        {
            LibraryList.ItemsSource = filtered;
            if(approvedEntry is not null) LibraryList.SelectedItem = filtered.FirstOrDefault(entry => entry.ComponentId == approvedEntry.ComponentId && entry.Version == approvedEntry.Version);
        }
        finally { synchronizingSelection = false; }
        UiText.Bind(LibraryCountText, "Text", entries.Count == 0 ? UiText.Text("暂无批准组件。导入候选并确认保存后，将显示在这里。") : UiText.Message($"显示 {filtered.Length:N0} 项 · 共 {entries.Count:N0} 个不可变版本"));
    }

    private async void ImportCandidate_Click(object sender, RoutedEventArgs e) => await ImportAsync(null);
    private async void ImportVersion_Click(object sender, RoutedEventArgs e) => await ImportAsync(approvedEntry);

    private async Task ImportAsync(ComponentLibraryEntry? parent)
    {
        if(busy) return;
        OpenFileDialog dialog = new() { Title = parent is null ? UiText.Get("导入 AI 建筑组件候选") : UiText.Format($"为 {parent.Metadata.Name} 导入新版候选"),
            Filter = UiText.Get("筑界镜独立蓝图 (*.zz)|*.zz"), CheckFileExists = true, Multiselect = false };
        FileDialogLocations.Shared.Configure(dialog, MapFileKind.ComponentCandidate, library.PendingDirectory);
        if(dialog.ShowDialog(this) != true) return;
        FileDialogLocations.Shared.RememberFile(MapFileKind.ComponentCandidate, dialog.FileName);
        await RunOperationAsync("正在校验并编译候选", async cancellationToken =>
        {
            ComponentCandidate imported = await library.StageCandidateAsync(dialog.FileName, parent, cancellationToken);
            if(closed) return;
            PendingTab.IsChecked = true;
            candidate = imported;
            LibrarySearchBox.Clear();
            await RefreshLibraryAsync(cancellationToken);
            PendingComponentEntry entry = pendingEntries.First(entry => entry.Path == imported.SourcePath);
            await ShowPendingAsync(entry, imported, cancellationToken);
        });
    }

    private async void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if(!uiReady || synchronizingSelection || busy) return;
        if(LibraryList.SelectedItem is PendingComponentEntry pending)
        {
            ClearSelectionPreview();
            ApprovalPanel.Visibility = Visibility.Visible;
            await RunOperationAsync("正在读取待批准组件", async cancellationToken =>
            {
                ComponentCandidate imported = await library.ImportCandidateAsync(pending.Path, cancellationToken);
                await ShowPendingAsync(pending, imported, cancellationToken);
            });
            return;
        }
        if(LibraryList.SelectedItem is not ComponentLibraryEntry selected) return;
        await RunOperationAsync("正在校验批准版本", async cancellationToken =>
        {
            ZjjPlan plan = await library.LoadApprovedPlanAsync(selected, cancellationToken);
            CompilationResult compilation = await Task.Run(() => ComponentPlanBuilder.CompileCandidate(plan, cancellationToken), cancellationToken);
            if(closed) return;
            candidate = null;
            versionParent = null;
            approvedEntry = selected;
            inspectedPlan = plan;
            inspectedDelta = compilation.Delta;
            synchronizingSelection = true;
            try { RotationBox.SelectedIndex = 0; }
            finally { synchronizingSelection = false; }
            ShowApprovedDetails(selected);
            await PreparePreviewAsync(0, cancellationToken);
            if(!closed) UiText.Bind(LibraryStatusText, "Text", UiText.Message($"已校验并显示 {selected.DisplayTitle}。修改组件请使用“导入新版候选”，已有版本将一直保留。"));
        });
    }

    private void SetMetadata(ComponentLibraryMetadata metadata, bool editable)
    {
        ComponentNameBox.Text = metadata.Name;
        ComponentCategoryBox.Text = metadata.Category;
        ComponentTagsBox.Text = string.Join(", ", metadata.Tags);
        ComponentNotesBox.Text = metadata.Notes;
        ComponentSourceBox.Text = metadata.SourceDescription;
        foreach(TextBox textBox in new[] { ComponentNameBox, ComponentCategoryBox, ComponentTagsBox, ComponentNotesBox, ComponentSourceBox }) textBox.IsReadOnly = !editable;
    }

    private void ShowApprovedDetails(ComponentLibraryEntry entry)
    {
        SetMetadata(entry.Metadata, editable: false);
        PreviewTitleText.Text = entry.DisplayTitle;
        UiText.Bind(PreviewStateText, "Text", UiText.Text("已批准版本 · 文件内容与来源均通过 SHA-256 校验"));
        UiText.Bind(DetailsTitleText, "Text", UiText.Text("已批准组件"));
        ApprovalPanel.Visibility = Visibility.Collapsed;
        PlacementPanel.Visibility = Visibility.Visible;
        UiText.Bind(ProvenanceText, "Text", UiText.Message($"批准时间：{entry.ApprovedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n来源：{entry.SourcePath}\n版本 SHA-256：{entry.PlanSha256}"));
    }

    private async Task PreparePreviewAsync(int quarterTurns, CancellationToken cancellationToken)
    {
        if(inspectedPlan is not ZjjPlan source || inspectedDelta is not SceneDelta delta) return;
        previewReady = false;
        InspectedCheckBox.IsChecked = false;
        UpdateActions();
        ZjjPlan previewPlan = await Task.Run(() => ComponentPlanBuilder.CreatePlacement(source, delta, new(0, 0, 0), quarterTurns,
            cancellationToken: cancellationToken), cancellationToken);
        MinecraftVersionDescriptor version = ResolveVisualVersion(source);
        string versionKey = $"{version.VersionName}:{version.DataVersion}:{version.StorageFamily}";
        ComponentViewport.ClearSections();
        if(activeResourceVersion != versionKey)
        {
            UiText.Bind(PreviewBusyText, "Text", UiText.Text("正在匹配 Minecraft 材质"));
            string sourcePath = candidate?.SourcePath ?? (approvedEntry is null ? library.RootDirectory : library.GetVersionDirectory(approvedEntry));
            MinecraftResourceSelectionResult resources = await resourceCache.ResolveAsync(Path.GetDirectoryName(sourcePath) ?? library.RootDirectory,
                version, cancellationToken: cancellationToken);
            if(!resources.IsAvailable) throw new InvalidOperationException($"无法准备可审核的 Minecraft 材质：{resources.Status}。修复资源后请重新选择或导入候选。");
            await ComponentViewport.ConfigureMinecraft1122ResourcesAsync(new Minecraft1122ResourceConfiguration(resources.ClientJarPath!), cancellationToken);
            activeResourceVersion = versionKey;
            resourceStatus = resources.Status;
        }
        UiText.Bind(PreviewBusyText, "Text", UiText.Text("正在构建组件 3D 预览"));
        var sections = await Task.Run(async () =>
        {
            CompilationResult compilation = ComponentPlanBuilder.CompileCandidate(previewPlan, cancellationToken);
            MinecraftDimensionId dimension = new(previewPlan.Dimension);
            NormalizedMinecraftSceneChunkSource chunkSource = NormalizedMinecraftSceneChunkSource.FromSceneDelta(compilation.Delta, version, dimension);
            var result = new List<NormalizedMinecraftSection>();
            await foreach(NormalizedMinecraftChunk chunk in chunkSource.EnumerateAsync(dimension, cancellationToken: cancellationToken).ConfigureAwait(false)) result.AddRange(chunk.Sections);
            return result;
        }, cancellationToken);
        if(closed) return;
        ComponentViewport.SetActiveDimension(previewPlan.Dimension);
        var progress = new Progress<SectionMeshBuildProgress>(value =>
        {
            if(!closed && busy) UiText.Bind(PreviewBusyText, "Text", UiText.Message($"正在构建组件：{value.CompletedSections:N0} / {value.TotalSections:N0} 个 Section"));
        });
        await ComponentViewport.SynchronizeSparseSectionsAsync(sections, cancellationToken, progress);
        previewBounds = previewPlan.Selections["component"];
        ResetCamera();
        PreviewEmptyOverlay.Visibility = Visibility.Collapsed;
        UiText.Bind(PreviewBusyText, "Text", UiText.Text("正在等待可见画面"));
        using CancellationTokenSource publicationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        publicationTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await ComponentViewport.WaitForSectionGpuPublicationAsync(publicationTimeout.Token);
            visibleFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await visibleFrame.Task.WaitAsync(publicationTimeout.Token);
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("3D 预览尚未成功显示，已禁止批准。请保持窗口可见并重新选择该组件，或检查显卡状态。");
        }
        finally { visibleFrame = null; }
        cancellationToken.ThrowIfCancellationRequested();
        if(closed || !ComponentViewport.IsVisible || ComponentViewport.LoadedSectionCount == 0) return;
        previewReady = true;
        BoxSelection bounds = previewBounds.Value;
        PreviewDetailsText.Text = UiText.Format($"{bounds.MaxExclusive.X:N0} × {bounds.MaxExclusive.Y:N0} × {bounds.MaxExclusive.Z:N0} 方块 · {delta.ChangedVoxelCount:N0} 个方块 · 朝向 {quarterTurns * 90}°\n{resourceStatus}") +
            (source.VisualProfile is null ? UiText.Get("\n候选未指定视觉版本，当前使用 Minecraft 1.21.10 资源预览。") : "");
    }

    private static MinecraftVersionDescriptor ResolveVisualVersion(ZjjPlan plan)
    {
        ZjjVisualProfile? profile = plan.VisualProfile;
        if(profile is null) return new(4556, "1.21.10", MinecraftStorageFamily.ModernSectionPalette);
        MinecraftStorageFamily family = profile.StorageFamily switch
        {
            ZjjVisualProfile.LegacyNumericAnvilStorageFamily => MinecraftStorageFamily.LegacyNumericAnvil,
            ZjjVisualProfile.FlattenedPaletteStorageFamily => MinecraftStorageFamily.FlattenedPalette,
            ZjjVisualProfile.ModernSectionPaletteStorageFamily => MinecraftStorageFamily.ModernSectionPalette,
            _ => profile.DataVersion == 1343 || profile.VersionName == "1.12.2" ? MinecraftStorageFamily.LegacyNumericAnvil : MinecraftStorageFamily.ModernSectionPalette,
        };
        return new(profile.DataVersion, profile.VersionName, family);
    }

    private void OnFrameRateUpdated(float framesPerSecond)
    {
        if(framesPerSecond > 0 && !closed) visibleFrame?.TrySetResult(true);
    }

    private void ResetView_Click(object sender, RoutedEventArgs e) => ResetCamera();
    private void ResetCamera()
    {
        if(previewBounds is not BoxSelection bounds) return;
        float width = bounds.MaxExclusive.X - bounds.Min.X;
        float height = bounds.MaxExclusive.Y - bounds.Min.Y;
        float depth = bounds.MaxExclusive.Z - bounds.Min.Z;
        ComponentViewport.FrameAt(new Vector3((width - 1) * 0.5f, (height - 1) * 0.5f, (depth - 1) * 0.5f), Math.Clamp(Math.Max(width, Math.Max(height, depth)) * 1.8f, 12f, 512f));
    }

    private async void Rotation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if(!uiReady || synchronizingSelection || busy || approvedEntry is null) return;
        int rotation = RotationBox.SelectedIndex;
        await RunOperationAsync("正在预览放置方向", cancellationToken => PreparePreviewAsync(rotation, cancellationToken));
    }

    private void Metadata_TextChanged(object sender, TextChangedEventArgs e) { if(uiReady) UpdateActions(); }
    private void Inspection_Changed(object sender, RoutedEventArgs e) { if(uiReady) UpdateActions(); }

    private async void ApproveComponent_Click(object sender, RoutedEventArgs e)
    {
        if(busy || !previewReady || candidate is not ComponentCandidate checkedCandidate || InspectedCheckBox.IsChecked != true) return;
        ComponentLibraryMetadata metadata = new(ComponentNameBox.Text, ComponentCategoryBox.Text,
            ComponentTagsBox.Text.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), ComponentNotesBox.Text, ComponentSourceBox.Text);
        await RunOperationAsync("正在保存您批准的组件", async cancellationToken =>
        {
            ComponentLibraryEntry saved = await library.ApproveAsync(checkedCandidate, metadata, userConfirmed: true, previousVersion: versionParent, cancellationToken);
            if(closed) return;
            candidate = null;
            versionParent = null;
            approvedEntry = saved;
            previewReady = true;
            ApprovedTab.IsChecked = true;
            LibrarySearchBox.Clear();
            ShowApprovedDetails(saved);
            await RefreshLibraryAsync(cancellationToken);
            UiText.Bind(LibraryStatusText, "Text", UiText.Message($"已确认保存：{saved.DisplayTitle}。批准版本已锁定，可在以后地图中重复使用。"));
        }, invalidatePreview: false);
    }

    private async void UseComponent_Click(object sender, RoutedEventArgs e)
    {
        if(busy || !previewReady || approvedEntry is not ComponentLibraryEntry selected) return;
        if(!int.TryParse(AnchorXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(AnchorYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(AnchorZBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
        {
            UiText.Bind(LibraryStatusText, "Text", UiText.Text("放置坐标必须是有效整数。"));
            return;
        }
        int quarterTurns = RotationBox.SelectedIndex;
        await RunOperationAsync("正在生成批准版本的放置草稿", async cancellationToken =>
        {
            ComponentPlacementRequest request = await library.CreatePlacementDraftAsync(selected, new(x, y, z), quarterTurns, targetDimension, cancellationToken);
            if(useComponentAsync is not null) await useComponentAsync(request, cancellationToken);
            else Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{request.DraftPath}\"", UseShellExecute = true });
            if(!closed)
            {
                UiText.Bind(LibraryStatusText, "Text", useComponentAsync is null ? UiText.Text("放置草稿已生成并在文件夹中选中，可通过筑界镜打开。") : UiText.Text("批准组件的放置草稿已交给工作区处理。"));
                LibraryStatusText.ToolTip = request.DraftPath;
            }
        }, invalidatePreview: false);
    }

    private async Task RunOperationAsync(string stage, Func<CancellationToken, Task> operation, bool invalidatePreview = true)
    {
        if(busy || closed) return;
        busy = true;
        if(invalidatePreview) previewReady = false;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operationCancellation = cancellation;
        CancelOperationButton.IsEnabled = true;
        PreviewBusyText.Text = stage;
        PreviewBusyOverlay.Visibility = Visibility.Visible;
        UpdateActions();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            await operation(cancellation.Token);
        }
        catch(OperationCanceledException)
        {
            if(!closed) UiText.Bind(LibraryStatusText, "Text", UiText.Text("本次操作已取消；已批准组件保持不变。"));
        }
        catch(Exception exception)
        {
            if(!closed)
            {
                previewReady = invalidatePreview ? false : previewReady;
                UiText.Bind(LibraryStatusText, "Text", UiText.Message($"{stage}未完成：{exception.Message}"));
                LibraryStatusText.ToolTip = exception.Message;
            }
        }
        finally
        {
            if(ReferenceEquals(operationCancellation, cancellation)) operationCancellation = null;
            busy = false;
            if(!closed)
            {
                PreviewBusyOverlay.Visibility = Visibility.Collapsed;
                UpdateActions();
            }
        }
    }

    private void UpdateActions()
    {
        if(!uiReady) return;
        ImportCandidateButton.IsEnabled = !busy;
        ImportVersionButton.IsEnabled = !busy && approvedEntry is not null;
        RefreshLibraryButton.IsEnabled = !busy;
        LibraryList.IsEnabled = !busy;
        LibrarySearchBox.IsEnabled = !busy;
        LatestOnlyToggle.IsEnabled = !busy;
        PendingTab.IsEnabled = !busy;
        ApprovedTab.IsEnabled = !busy;
        SavePendingButton.IsEnabled = !busy && candidate is not null;
        RemovePendingButton.IsEnabled = !busy && PendingTab.IsChecked == true && LibraryList.SelectedItem is PendingComponentEntry;
        RotationBox.IsEnabled = !busy && approvedEntry is not null;
        ResetViewButton.IsEnabled = !busy && previewReady;
        InspectedCheckBox.IsEnabled = !busy && previewReady && candidate is not null;
        ApproveComponentButton.IsEnabled = !busy && previewReady && candidate is not null && InspectedCheckBox.IsChecked == true &&
            !string.IsNullOrWhiteSpace(ComponentNameBox.Text) && !string.IsNullOrWhiteSpace(ComponentCategoryBox.Text);
        UseComponentButton.IsEnabled = !busy && previewReady && approvedEntry is not null;
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
        CancelOperationButton.IsEnabled = false;
        UiText.Bind(PreviewBusyText, "Text", UiText.Text("正在取消…"));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        closed = true;
        pendingRefreshTimer.Stop();
        pendingWatcher?.Dispose();
        lifetime.Cancel();
        operationCancellation?.Cancel();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ComponentViewport.FrameRateUpdated -= OnFrameRateUpdated;
        visibleFrame?.TrySetCanceled();
        ComponentViewport.Dispose();
        lifetime.Dispose();
    }
}
