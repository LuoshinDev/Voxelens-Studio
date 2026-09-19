using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ZhuJieJing.App.Resources;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

internal sealed record BlockMappingEditorOverride(string SourceKey, LegacyBlockEncoding Target);

internal sealed record BlockMappingRandomJumpResult(bool Found, string Message);

internal sealed class BlockMappingEditorEntry : INotifyPropertyChanged
{
    private Minecraft1122LegacyMappingTarget effectiveTarget;
    private ImageSource? sourceThumbnail;
    private ImageSource? defaultTargetThumbnail;
    private ImageSource? effectiveTargetThumbnail;
    private bool hasUserOverride;

    public BlockMappingEditorEntry(
        BlockState sourceState,
        string sourceKey,
        string sourceDisplay,
        Minecraft1122LegacyMappingTarget defaultTarget,
        Minecraft1122LegacyMappingTarget effectiveTarget,
        MinecraftBlockMappingQuality quality,
        string ruleId,
        string explanation,
        long blockCount,
        bool canOverride,
        bool hasUserOverride,
        string? thumbnailSourceKey = null)
    {
        ArgumentNullException.ThrowIfNull(sourceState);
        SourceState = sourceState;
        SourceKey = sourceKey;
        SourceDisplay = sourceDisplay;
        ThumbnailSourceKey = string.IsNullOrWhiteSpace(thumbnailSourceKey) ? sourceKey : thumbnailSourceKey;
        DefaultTarget = defaultTarget;
        this.effectiveTarget = effectiveTarget;
        Quality = quality;
        RuleId = ruleId;
        Explanation = explanation;
        BlockCount = blockCount;
        CanOverride = canOverride;
        this.hasUserOverride = hasUserOverride && effectiveTarget.LegacyEncoding != defaultTarget.LegacyEncoding;
    }

    public BlockState SourceState { get; }

    public string SourceKey { get; }

    public string SourceDisplay { get; }

    public string ThumbnailSourceKey { get; }

    public string SourceTitle => UiText.Language != "zh-CN" ? UiText.BlockName(SourceState.Name) : SourceDisplay.EndsWith($" · {SourceKey}", StringComparison.Ordinal)
        ? SourceDisplay[..^(SourceKey.Length + 3)]
        : SourceDisplay;

    public Minecraft1122LegacyMappingTarget DefaultTarget { get; }

    public Minecraft1122LegacyMappingTarget EffectiveTarget => effectiveTarget;

    public MinecraftBlockMappingQuality Quality { get; }

    public string RuleId { get; }

    public string Explanation { get; }

    public long BlockCount { get; }

    public bool CanOverride { get; }

    public bool HasUserOverride => hasUserOverride;

    public string DefaultTargetDisplay => FormatTarget(DefaultTarget);

    public string EffectiveTargetDisplay => FormatTarget(EffectiveTarget);

    public string DefaultTargetName => UiText.BlockName(DefaultTarget.State.Name);

    public string DefaultTargetIdentifier => DefaultTarget.State.CanonicalKey;

    public string DefaultTargetEncodingText => FormatEncoding(DefaultTarget.LegacyEncoding);

    public string DefaultTargetDetail => $"{DefaultTargetIdentifier}  {DefaultTargetEncodingText}";

    public string EffectiveTargetName => UiText.BlockName(EffectiveTarget.State.Name);

    public string EffectiveTargetIdentifier => EffectiveTarget.State.CanonicalKey;

    public string EffectiveTargetEncodingText => FormatEncoding(EffectiveTarget.LegacyEncoding);

    public string EffectiveTargetDetail => $"{EffectiveTargetIdentifier}  {EffectiveTargetEncodingText}";

    public ImageSource? SourceThumbnail => sourceThumbnail;

    public ImageSource? DefaultTargetThumbnail => defaultTargetThumbnail;

    public ImageSource? EffectiveTargetThumbnail => effectiveTargetThumbnail;

    public string UsageText => UiText.Format($"当前地图 {BlockCount:N0}");

    public Visibility UsageVisibility => BlockCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string OriginText => !CanOverride ? UiText.Get("规则族") : HasUserOverride ? UiText.Get("用户设定") : UiText.Get("内置规则");

    public Brush OriginBrush => HasUserOverride ? Brushes.MediumVioletRed : new SolidColorBrush(Color.FromRgb(139, 122, 157));

    public string SearchText => string.Join(
        '\n',
        SourceKey,
        SourceDisplay,
        SourceTitle,
        DefaultTargetName,
        EffectiveTargetName,
        DefaultTarget.DisplayName,
        DefaultTarget.State.CanonicalKey,
        EffectiveTarget.DisplayName,
        EffectiveTarget.State.CanonicalKey,
        $"{EffectiveTarget.LegacyEncoding.NumericId}:{EffectiveTarget.LegacyEncoding.Metadata}",
        RuleId,
        Explanation);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateSourceThumbnail(ImageSource? thumbnail)
    {
        if(ReferenceEquals(sourceThumbnail, thumbnail)) return;
        sourceThumbnail = thumbnail;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceThumbnail)));
    }

    public void UpdateTargetThumbnail(Minecraft1122LegacyMappingTarget target, ImageSource? thumbnail)
    {
        string visualIdentity = BlockMappingTargetEntry.CreateVisualIdentityKey(target);
        if(string.Equals(
               BlockMappingTargetEntry.CreateVisualIdentityKey(DefaultTarget),
               visualIdentity,
               StringComparison.Ordinal) &&
           !ReferenceEquals(defaultTargetThumbnail, thumbnail))
        {
            defaultTargetThumbnail = thumbnail;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DefaultTargetThumbnail)));
        }
        if(string.Equals(
               BlockMappingTargetEntry.CreateVisualIdentityKey(EffectiveTarget),
               visualIdentity,
               StringComparison.Ordinal) &&
           !ReferenceEquals(effectiveTargetThumbnail, thumbnail))
        {
            effectiveTargetThumbnail = thumbnail;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTargetThumbnail)));
        }
    }

    public void ApplyOverride(Minecraft1122LegacyMappingTarget target, ImageSource? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if(!CanOverride) return;
        effectiveTarget = target;
        effectiveTargetThumbnail = thumbnail;
        hasUserOverride = effectiveTarget.LegacyEncoding != DefaultTarget.LegacyEncoding;
        NotifyMappingChanged();
    }

    public void RestoreDefault()
    {
        if(!CanOverride) return;
        effectiveTarget = DefaultTarget;
        effectiveTargetThumbnail = defaultTargetThumbnail;
        hasUserOverride = false;
        NotifyMappingChanged();
    }

    private void NotifyMappingChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTarget)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTargetDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTargetName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTargetIdentifier)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTargetEncodingText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTargetDetail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveTargetThumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUserOverride)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OriginText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OriginBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchText)));
    }

    private static string FormatTarget(Minecraft1122LegacyMappingTarget target) =>
        $"{UiText.BlockName(target.State.Name)} · {FormatEncoding(target.LegacyEncoding)}";

    private static string FormatEncoding(LegacyBlockEncoding encoding) =>
        string.Create(CultureInfo.InvariantCulture, $"{encoding.NumericId}:{encoding.Metadata}");
}

internal sealed class BlockMappingTargetEntry : INotifyPropertyChanged
{
    private ImageSource? thumbnail;
    private bool isSelected;

    public BlockMappingTargetEntry(Minecraft1122LegacyMappingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
    }

    public Minecraft1122LegacyMappingTarget Target { get; }

    public string VisualIdentityKey => CreateVisualIdentityKey(Target);

    public string DisplayName => UiText.BlockName(Target.State.Name);

    public string MaterialIdentifier => Target.State.Name;

    public string CanonicalKey => Target.State.CanonicalKey;

    public string EncodingText => string.Create(
        CultureInfo.InvariantCulture,
        $"{Target.LegacyEncoding.NumericId}:{Target.LegacyEncoding.Metadata}");

    public ImageSource? Thumbnail => thumbnail;

    public bool IsSelected => isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateThumbnail(ImageSource? value)
    {
        if(ReferenceEquals(thumbnail, value)) return;
        thumbnail = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }

    public void SetSelected(bool value)
    {
        if(isSelected == value) return;
        isSelected = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }

    public bool Represents(Minecraft1122LegacyMappingTarget target) =>
        string.Equals(VisualIdentityKey, CreateVisualIdentityKey(target), StringComparison.Ordinal);

    public static string CreateVisualIdentityKey(Minecraft1122LegacyMappingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        BlockState state = target.State;
        string name = state.Name;
        string path = BlockMappingTargetRelevance.GetPath(name);
        if(path == "door" || path.EndsWith("_door", StringComparison.Ordinal))
            return name;
        if(target.LegacyEncoding.NumericId is 8 or 10)
            return $"{name}|fluid=flowing";
        if(target.LegacyEncoding.NumericId is 9 or 11)
            return $"{name}|fluid=still";
        // The picker selects a material; conversion restores bottom/top/double from the source.
        if(path.EndsWith("_slab", StringComparison.Ordinal)) return name;
        if(path is "piston_head" or "moving_piston" &&
           state.Properties.TryGetValue("type", out string? pistonType))
            return $"{name}|type={pistonType}";
        if(path == "structure_block" && state.Properties.TryGetValue("mode", out string? mode))
            return $"{name}|mode={mode}";

        string[] meaningfulProperties =
        [
            "lit",
            "powered",
            "inverted",
            "conditional",
            "extended",
            "triggered",
            "mode",
        ];
        string[] states = meaningfulProperties
            .Where(state.Properties.ContainsKey)
            .Select(key => $"{key}={state.Properties[key]}")
            .ToArray();
        return states.Length == 0 ? name : $"{name}|{string.Join('|', states)}";
    }

    public static Minecraft1122LegacyMappingTarget SelectRepresentative(
        IEnumerable<Minecraft1122LegacyMappingTarget> candidates) => candidates
        .OrderBy(static candidate => RepresentativePenalty(candidate.State))
        .ThenBy(static candidate => candidate.LegacyEncoding.NumericId)
        .ThenBy(static candidate => candidate.LegacyEncoding.Metadata)
        .First();

    private static int RepresentativePenalty(BlockState state)
    {
        int penalty = 0;
        foreach((string key, string value) in state.Properties)
        {
            penalty += (key, value) switch
            {
                ("open" or "powered" or "extended" or "triggered" or "occupied" or "lit", "false") => 0,
                ("facing", "north") => 0,
                ("axis", "y") => 0,
                ("half", "bottom" or "lower") => 0,
                ("hinge", "left") => 0,
                ("shape", "straight" or "north_south") => 0,
                ("rotation" or "age" or "level" or "power" or "stage", "0") => 0,
                ("type", "bottom" or "single" or "normal") => 0,
                _ => 1,
            };
        }
        return penalty;
    }
}

internal sealed record BlockMappingTargetRow(IReadOnlyList<BlockMappingTargetEntry> Items);

public partial class BlockMappingTableWindow : Window
{
    private const int ThumbnailBatchSize = 6;
    private const int TargetGridColumns = 6;
    private readonly IReadOnlyList<BlockMappingEditorEntry> entries;
    private readonly IReadOnlyList<BlockMappingTargetEntry> targetEntries;
    private readonly ICollectionView mappingView;
    private readonly ICollectionView targetView;
    private readonly Func<BlockMappingEditorEntry, CancellationToken, Task<BlockMappingRandomJumpResult>>? randomJumpHandler;
    private MinecraftBlockThumbnailProvider? thumbnailProvider;
    private readonly bool ownsThumbnailProvider;
    private readonly CancellationTokenSource windowCancellation = new();
    private Task? thumbnailLoadTask;
    private Task<BlockMappingRandomJumpResult>? randomJumpTask;
    private IReadOnlyList<BlockMappingTargetRow> targetRows = [];
    private BlockMappingTargetEntry? selectedTarget;
    private bool randomJumpRunning;
    private bool isUiReady;

    internal BlockMappingTableWindow(
        IReadOnlyList<BlockMappingEditorEntry> entries,
        IReadOnlyList<Minecraft1122LegacyMappingTarget> targets,
        MinecraftBlockThumbnailProvider? thumbnailProvider = null,
        Func<BlockMappingEditorEntry, CancellationToken, Task<BlockMappingRandomJumpResult>>? randomJumpHandler = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(targets);
        this.entries = entries;
        targetEntries = targets
            .Concat(entries.SelectMany(static entry => new[] { entry.DefaultTarget, entry.EffectiveTarget }))
            .GroupBy(static target => BlockMappingTargetEntry.CreateVisualIdentityKey(target), StringComparer.Ordinal)
            .Select(static group => new BlockMappingTargetEntry(BlockMappingTargetEntry.SelectRepresentative(group)))
            .ToArray();
        this.thumbnailProvider = thumbnailProvider;
        ownsThumbnailProvider = thumbnailProvider is null;
        this.randomJumpHandler = randomJumpHandler;
        InitializeComponent();
        RandomJumpButton.Visibility = randomJumpHandler is null ? Visibility.Collapsed : Visibility.Visible;

        mappingView = new ListCollectionView(entries.ToList());
        mappingView.Filter = FilterMapping;
        MappingList.ItemsSource = mappingView;

        targetView = new ListCollectionView(targetEntries.ToList());
        targetView.Filter = FilterTarget;
        RefreshTargetRows();

        Loaded += BlockMappingTableWindow_Loaded;
        Closed += BlockMappingTableWindow_Closed;

        UiText.Bind(MappingCountText, "Text", UiText.Message($"{entries.Count:N0} 条映射"));
        isUiReady = true;
        MappingList.SelectedItem = entries.FirstOrDefault(static entry => entry.BlockCount > 0) ?? entries.FirstOrDefault();
        UpdateEditorState();
        UpdateChangeSummary();
    }

    internal IReadOnlyList<BlockMappingEditorOverride> SavedOverrides { get; private set; } = [];

    internal void UpdateSourceThumbnail(string sourceKey, ImageSource? thumbnail)
    {
        foreach(BlockMappingEditorEntry entry in entries.Where(entry => string.Equals(entry.SourceKey, sourceKey, StringComparison.Ordinal)))
            entry.UpdateSourceThumbnail(thumbnail);
    }

    internal void UpdateLegacyThumbnail(Minecraft1122LegacyMappingTarget target, ImageSource? thumbnail)
    {
        foreach(BlockMappingTargetEntry targetEntry in targetEntries.Where(entry => entry.Represents(target)))
            targetEntry.UpdateThumbnail(thumbnail);
        foreach(BlockMappingEditorEntry entry in entries)
            entry.UpdateTargetThumbnail(target, thumbnail);
    }

    private async void BlockMappingTableWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= BlockMappingTableWindow_Loaded;
        CancellationToken cancellationToken = windowCancellation.Token;
        ThumbnailLoadText.Visibility = Visibility.Visible;
        thumbnailLoadTask = PopulateThumbnailsAsync(cancellationToken);
        try
        {
            await thumbnailLoadTask;
            if(!cancellationToken.IsCancellationRequested)
                ThumbnailLoadText.Visibility = Visibility.Collapsed;
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if(!cancellationToken.IsCancellationRequested)
                UiText.Bind(ThumbnailLoadText, "Text", UiText.Text("部分方块图载入失败"));
        }
    }

    private async Task PopulateThumbnailsAsync(CancellationToken cancellationToken)
    {
        MinecraftBlockThumbnailProvider provider = thumbnailProvider ?? await MinecraftBlockThumbnailProvider.CreateConfiguredAsync(thumbnailPixelSize: 48, cancellationToken: cancellationToken);
        thumbnailProvider = provider;
        cancellationToken.ThrowIfCancellationRequested();

        int total = targetEntries.Count + entries.Count;
        int completed = targetEntries.Count(static entry => entry.Thumbnail is not null) +
                        entries.Count(static entry => entry.SourceThumbnail is not null);
        while(completed < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlockMappingTargetEntry[] targetBatch = GetNextTargetThumbnailBatch();
            BlockMappingEditorEntry[] sourceBatch = GetNextSourceThumbnailBatch();
            if(targetBatch.Length == 0 && sourceBatch.Length == 0) break;

            if(targetBatch.Length > 0)
            {
                (BlockMappingTargetEntry Entry, ImageSource Thumbnail)[] thumbnails = await Task.Run(
                    () => targetBatch.Select(entry => (entry, provider.GetLegacyThumbnail(entry.Target))).ToArray(),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach((BlockMappingTargetEntry entry, ImageSource thumbnail) in thumbnails)
                    UpdateLegacyThumbnail(entry.Target, thumbnail);
                completed += thumbnails.Length;
                UiText.Bind(ThumbnailLoadText, "Text", UiText.Message($"正在载入方块图… {completed:N0}/{total:N0}"));
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            if(sourceBatch.Length > 0)
            {
                (BlockMappingEditorEntry Entry, ImageSource Thumbnail)[] thumbnails = await Task.Run(
                    () => sourceBatch.Select(entry => (entry, provider.GetModernThumbnail(entry.ThumbnailSourceKey))).ToArray(),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach((BlockMappingEditorEntry entry, ImageSource thumbnail) in thumbnails)
                    entry.UpdateSourceThumbnail(thumbnail);
                completed += thumbnails.Length;
                UiText.Bind(ThumbnailLoadText, "Text", UiText.Message($"正在载入方块图… {completed:N0}/{total:N0}"));
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
    }

    private BlockMappingTargetEntry[] GetNextTargetThumbnailBatch()
    {
        BlockMappingEditorEntry? selectedMapping = MappingList.SelectedItem as BlockMappingEditorEntry;
        IEnumerable<BlockMappingTargetEntry> selectedMappingTargets = selectedMapping is null
            ? Enumerable.Empty<BlockMappingTargetEntry>()
            : targetEntries.Where(entry =>
                entry.Represents(selectedMapping.DefaultTarget) ||
                entry.Represents(selectedMapping.EffectiveTarget));
        IEnumerable<BlockMappingTargetEntry> selectedTargets = selectedTarget is null
            ? Enumerable.Empty<BlockMappingTargetEntry>()
            : new[] { selectedTarget };
        return selectedMappingTargets
            .Concat(selectedTargets)
            .Concat(GetVisibleTargetEntries())
            .Concat(targetEntries)
            .Where(static entry => entry.Thumbnail is null)
            .Distinct()
            .Take(ThumbnailBatchSize)
            .ToArray();
    }

    private IEnumerable<BlockMappingTargetEntry> GetVisibleTargetEntries()
    {
        foreach(BlockMappingTargetRow row in GetVisibleItems<BlockMappingTargetRow>(TargetList))
        {
            foreach(BlockMappingTargetEntry entry in row.Items)
                yield return entry;
        }
    }

    private BlockMappingEditorEntry[] GetNextSourceThumbnailBatch()
    {
        BlockMappingEditorEntry? selectedSource = MappingList.SelectedItem as BlockMappingEditorEntry;
        IEnumerable<BlockMappingEditorEntry> selectedSources = selectedSource is null
            ? Enumerable.Empty<BlockMappingEditorEntry>()
            : new[] { selectedSource };
        return selectedSources
            .Concat(GetVisibleItems<BlockMappingEditorEntry>(MappingList))
            .Concat(entries)
            .Where(static entry => entry.SourceThumbnail is null)
            .Distinct()
            .Take(ThumbnailBatchSize)
            .ToArray();
    }

    private static IEnumerable<T> GetVisibleItems<T>(ListBox listBox)
    {
        for(var index = 0; index < listBox.Items.Count; index++)
        {
            if(listBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem { IsVisible: true } &&
               listBox.Items[index] is T item)
                yield return item;
        }
    }

    private void BlockMappingTableWindow_Closed(object? sender, EventArgs e)
    {
        Closed -= BlockMappingTableWindow_Closed;
        windowCancellation.Cancel();
        _ = DisposeWindowResourcesAsync();
    }

    private async Task DisposeWindowResourcesAsync()
    {
        try
        {
            if(thumbnailLoadTask is not null) await thumbnailLoadTask.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            if(randomJumpTask is not null) await randomJumpTask.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            if(ownsThumbnailProvider) thumbnailProvider?.Dispose();
        }
        catch
        {
        }
        finally
        {
            windowCancellation.Dispose();
        }
    }

    private void MappingSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if(!isUiReady) return;
        MappingSearchHint.Visibility = string.IsNullOrEmpty(MappingSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        mappingView.Refresh();
        EnsureVisibleMappingSelection();
        UpdateVisibleMappingCount();
    }

    private void MappingFilter_Changed(object sender, RoutedEventArgs e)
    {
        if(!isUiReady) return;
        mappingView.Refresh();
        EnsureVisibleMappingSelection();
        UpdateVisibleMappingCount();
    }

    private void EnsureVisibleMappingSelection()
    {
        if(MappingList.SelectedItem is BlockMappingEditorEntry selected && mappingView.Contains(selected)) return;
        MappingList.SelectedItem = mappingView.Cast<BlockMappingEditorEntry>().FirstOrDefault();
    }

    private bool FilterMapping(object value)
    {
        if(value is not BlockMappingEditorEntry entry) return false;
        if(CurrentWorldOnlyCheckBox.IsChecked == true && entry.BlockCount <= 0) return false;
        string query = MappingSearchBox.Text.Trim();
        return query.Length == 0 || entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateVisibleMappingCount()
    {
        int count = mappingView?.Cast<object>().Count() ?? entries.Count;
        UiText.Bind(MappingCountText, "Text", count == entries.Count ? UiText.Message($"{entries.Count:N0} 条映射") : UiText.Message($"显示 {count:N0} / {entries.Count:N0}"));
    }

    private void MappingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if(!isUiReady) return;
        UpdateEditorState();
    }

    private void UpdateEditorState()
    {
        BlockMappingEditorEntry? entry = MappingList.SelectedItem as BlockMappingEditorEntry;
        EditorPanel.DataContext = entry;
        bool editable = entry?.CanOverride == true;
        TargetList.IsEnabled = editable;
        TargetSearchBox.IsEnabled = editable;
        RestoreDefaultButton.IsEnabled = editable && entry!.HasUserOverride;
        UseSelectedTargetButton.IsEnabled = editable && selectedTarget is not null;
        RandomJumpButton.IsEnabled = entry is not null && randomJumpHandler is not null && !randomJumpRunning;

        if(entry is null)
        {
            targetView.Refresh();
            RefreshTargetRows();
            SelectTarget(null);
            return;
        }
        targetView.Refresh();
        RefreshTargetRows();
        BlockMappingTargetEntry? selected = targetView.Cast<BlockMappingTargetEntry>()
            .FirstOrDefault(target => target.Represents(entry.EffectiveTarget));
        SelectTarget(selected);
        UseSelectedTargetButton.IsEnabled = editable && selected is not null;
        if(selected is not null) ScrollTargetIntoView(selected);
    }

    private void TargetSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if(!isUiReady) return;
        TargetSearchHint.Visibility = string.IsNullOrEmpty(TargetSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        targetView.Refresh();
        RefreshTargetRows();
        if(selectedTarget is not null && !targetView.Contains(selectedTarget)) SelectTarget(null);
        UseSelectedTargetButton.IsEnabled = MappingList.SelectedItem is BlockMappingEditorEntry { CanOverride: true } &&
                                            selectedTarget is not null;
    }

    private bool FilterTarget(object value)
    {
        if(value is not BlockMappingTargetEntry targetEntry) return false;
        Minecraft1122LegacyMappingTarget target = targetEntry.Target;
        if(target.LegacyEncoding.NumericId == 0 &&
           !AllowsAirTarget(MappingList.SelectedItem as BlockMappingEditorEntry))
            return false;
        string query = TargetSearchBox.Text.Trim();
        if(query.Length == 0) return true;
        string encoding = string.Create(
            CultureInfo.InvariantCulture,
            $"{target.LegacyEncoding.NumericId}:{target.LegacyEncoding.Metadata}");
        return UiText.BlockName(target.State.Name).Contains(query, StringComparison.OrdinalIgnoreCase) || target.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               target.State.CanonicalKey.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               encoding.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsAirTarget(BlockMappingEditorEntry? entry) =>
        entry is not null &&
        (string.Equals(entry.SourceKey, "minecraft:light", StringComparison.Ordinal) ||
         entry.SourceKey.StartsWith("minecraft:light[", StringComparison.Ordinal));

    private void UseSelectedTargetButton_Click(object sender, RoutedEventArgs e) => ApplySelectedTarget();

    private void TargetTile_Click(object sender, RoutedEventArgs e)
    {
        if(!isUiReady || sender is not Button { DataContext: BlockMappingTargetEntry entry }) return;
        SelectTarget(entry);
        ApplySelectedTarget();
    }

    private void SelectTarget(BlockMappingTargetEntry? entry)
    {
        if(ReferenceEquals(selectedTarget, entry)) return;
        selectedTarget?.SetSelected(false);
        selectedTarget = entry;
        selectedTarget?.SetSelected(true);
    }

    private void RefreshTargetRows()
    {
        BlockMappingEditorEntry? source = MappingList.SelectedItem as BlockMappingEditorEntry;
        BlockMappingTargetEntry[] visible = targetView
            .Cast<BlockMappingTargetEntry>()
            .OrderByDescending(candidate => BlockMappingTargetRelevance.Score(source, candidate))
            .ThenBy(static candidate => candidate.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(static candidate => candidate.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        targetRows = visible
            .Chunk(TargetGridColumns)
            .Select(static items => new BlockMappingTargetRow(items))
            .ToArray();
        TargetList.ItemsSource = targetRows;
    }

    private void ScrollTargetIntoView(BlockMappingTargetEntry entry)
    {
        BlockMappingTargetRow? row = targetRows.FirstOrDefault(candidate => candidate.Items.Contains(entry));
        if(row is not null) TargetList.ScrollIntoView(row);
    }

    private void ApplySelectedTarget()
    {
        if(MappingList.SelectedItem is not BlockMappingEditorEntry { CanOverride: true } entry ||
           selectedTarget is not BlockMappingTargetEntry targetEntry)
            return;
        entry.ApplyOverride(targetEntry.Target, targetEntry.Thumbnail);
        UpdateEditorState();
        UpdateChangeSummary();
        mappingView.Refresh();
        MappingList.ScrollIntoView(entry);
    }

    private async void RandomJumpButton_Click(object sender, RoutedEventArgs e)
    {
        if(randomJumpRunning || randomJumpHandler is null ||
           MappingList.SelectedItem is not BlockMappingEditorEntry entry)
            return;

        randomJumpRunning = true;
        RandomJumpButton.IsEnabled = false;
        UiText.Bind(RandomJumpButton, "Content", UiText.Text("正在查找…"));
        UiText.Bind(RandomJumpStatusText, "Text", UiText.Message($"正在当前地图查找“{entry.SourceTitle}”…"));
        CancellationToken cancellationToken = windowCancellation.Token;
        try
        {
            Task<BlockMappingRandomJumpResult> task = randomJumpHandler(entry, cancellationToken);
            randomJumpTask = task;
            BlockMappingRandomJumpResult result = await task;
            if(!cancellationToken.IsCancellationRequested)
                RandomJumpStatusText.Text = result.Message;
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
        {
        }
        catch(Exception exception)
        {
            if(!cancellationToken.IsCancellationRequested)
                UiText.Bind(RandomJumpStatusText, "Text", UiText.Message($"无法查找方块位置：{exception.Message}"));
        }
        finally
        {
            randomJumpTask = null;
            randomJumpRunning = false;
            if(!cancellationToken.IsCancellationRequested)
            {
                UiText.Bind(RandomJumpButton, "Content", UiText.Text("随机查看位置"));
                UpdateEditorState();
            }
        }
    }

    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        if(MappingList.SelectedItem is not BlockMappingEditorEntry entry) return;
        entry.RestoreDefault();
        UpdateEditorState();
        UpdateChangeSummary();
        mappingView.Refresh();
    }

    private void UpdateChangeSummary()
    {
        int count = entries.Count(static entry => entry.HasUserOverride);
        UiText.Bind(ChangeSummaryText, "Text", count == 0 ? UiText.Text("当前全部使用内置规则") : UiText.Message($"已设置 {count:N0} 条用户替代规则"));
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SavedOverrides = entries
            .Where(static entry => entry.CanOverride && entry.HasUserOverride)
            .GroupBy(static entry => entry.SourceKey, StringComparer.Ordinal)
            .Select(static group => group.First())
            .Select(static entry => new BlockMappingEditorOverride(entry.SourceKey, entry.EffectiveTarget.LegacyEncoding))
            .ToArray();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

internal static class BlockMappingTargetRelevance
{
    private static readonly HashSet<string> ColorTokens = new(StringComparer.Ordinal)
    {
        "white", "orange", "magenta", "light", "blue", "yellow", "lime", "pink",
        "gray", "grey", "cyan", "purple", "brown", "green", "red", "black",
    };

    private static readonly HashSet<string> MaterialTokens = new(StringComparer.Ordinal)
    {
        "oak", "spruce", "birch", "jungle", "acacia", "dark", "mangrove", "cherry",
        "bamboo", "crimson", "warped", "stone", "cobblestone", "brick", "sandstone",
        "quartz", "purpur", "prismarine", "deepslate", "blackstone", "tuff", "calcite",
        "granite", "diorite", "andesite", "copper", "iron", "gold", "diamond", "emerald",
        "lapis", "redstone", "coal", "nether", "end", "mud", "terracotta", "concrete",
        "wool", "glass", "ice", "snow", "slime", "honey", "coral",
    };

    public static int Score(BlockMappingEditorEntry? source, BlockMappingTargetEntry candidate)
    {
        if(source is null) return 0;
        Minecraft1122LegacyMappingTarget target = candidate.Target;
        if(target.LegacyEncoding == source.EffectiveTarget.LegacyEncoding) return 2_000_000;
        if(target.LegacyEncoding == source.DefaultTarget.LegacyEncoding) return 1_900_000;

        string sourcePath = GetPath(source.SourceState.Name);
        string targetPath = GetPath(target.State.Name);
        string defaultPath = GetPath(source.DefaultTarget.State.Name);
        int score = 0;
        if(string.Equals(sourcePath, targetPath, StringComparison.Ordinal)) score += 500_000;

        string sourceShape = GetShape(sourcePath);
        string targetShape = GetShape(targetPath);
        string defaultShape = GetShape(defaultPath);
        if(sourceShape != "block" && string.Equals(sourceShape, targetShape, StringComparison.Ordinal)) score += 80_000;
        if(defaultShape != "block" && string.Equals(defaultShape, targetShape, StringComparison.Ordinal)) score += 45_000;
        if(sourceShape == "block" && targetShape == "block") score += 2_000;

        HashSet<string> sourceTokens = Tokenize(sourcePath);
        HashSet<string> targetTokens = Tokenize(targetPath);
        HashSet<string> defaultTokens = Tokenize(defaultPath);
        score += CountMatches(sourceTokens, targetTokens) * 5_000;
        score += CountMatches(defaultTokens, targetTokens) * 3_000;
        score += CountCategoryMatches(sourceTokens, targetTokens, MaterialTokens) * 12_000;
        score += CountCategoryMatches(defaultTokens, targetTokens, MaterialTokens) * 7_000;
        score += CountCategoryMatches(sourceTokens, targetTokens, ColorTokens) * 16_000;
        score += CountCategoryMatches(defaultTokens, targetTokens, ColorTokens) * 8_000;

        foreach((string key, string value) in source.SourceState.Properties)
        {
            if(!target.State.Properties.TryGetValue(key, out string? candidateValue)) continue;
            score += string.Equals(value, candidateValue, StringComparison.Ordinal) ? 1_500 : -500;
        }
        if(targetPath.StartsWith(defaultPath, StringComparison.Ordinal) ||
           defaultPath.StartsWith(targetPath, StringComparison.Ordinal)) score += 4_000;
        return score;
    }

    private static int CountMatches(IReadOnlySet<string> left, IReadOnlySet<string> right) =>
        left.Count(right.Contains);

    private static int CountCategoryMatches(
        IReadOnlySet<string> left,
        IReadOnlySet<string> right,
        IReadOnlySet<string> category) => left.Count(token => category.Contains(token) && right.Contains(token));

    private static HashSet<string> Tokenize(string path) => path
        .Split(new[] { '_', '*' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.Ordinal);

    internal static string GetPath(string name)
    {
        int namespaceSeparator = name.IndexOf(':');
        int propertiesStart = name.IndexOf('[');
        int start = namespaceSeparator < 0 ? 0 : namespaceSeparator + 1;
        int end = propertiesStart < 0 ? name.Length : propertiesStart;
        return name[start..end];
    }

    private static string GetShape(string path)
    {
        if(path.EndsWith("_stairs", StringComparison.Ordinal)) return "stairs";
        if(path.EndsWith("_slab", StringComparison.Ordinal)) return "slab";
        if(path.EndsWith("_wall", StringComparison.Ordinal)) return "wall";
        if(path.EndsWith("_fence_gate", StringComparison.Ordinal)) return "fence_gate";
        if(path.EndsWith("_fence", StringComparison.Ordinal)) return "fence";
        if(path.EndsWith("_trapdoor", StringComparison.Ordinal)) return "trapdoor";
        if(path.EndsWith("_door", StringComparison.Ordinal)) return "door";
        if(path.EndsWith("_button", StringComparison.Ordinal)) return "button";
        if(path.EndsWith("_pressure_plate", StringComparison.Ordinal)) return "pressure_plate";
        if(path.EndsWith("_leaves", StringComparison.Ordinal)) return "leaves";
        if(path.EndsWith("_sapling", StringComparison.Ordinal)) return "sapling";
        if(path.EndsWith("_log", StringComparison.Ordinal) || path.EndsWith("_stem", StringComparison.Ordinal)) return "log";
        if(path.EndsWith("_wood", StringComparison.Ordinal) || path.EndsWith("_hyphae", StringComparison.Ordinal)) return "wood";
        if(path.EndsWith("_planks", StringComparison.Ordinal)) return "planks";
        if(path.EndsWith("_wool", StringComparison.Ordinal)) return "wool";
        if(path.EndsWith("_carpet", StringComparison.Ordinal)) return "carpet";
        if(path.EndsWith("_pane", StringComparison.Ordinal) || path == "iron_bars") return "pane";
        if(path.EndsWith("_sign", StringComparison.Ordinal)) return "sign";
        if(path.EndsWith("_banner", StringComparison.Ordinal)) return "banner";
        if(path.EndsWith("_ore", StringComparison.Ordinal)) return "ore";
        if(path.Contains("water", StringComparison.Ordinal) || path.Contains("lava", StringComparison.Ordinal)) return "fluid";
        return "block";
    }
}
