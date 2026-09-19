using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZhuJieJing.App.BlockReplacement;
using ZhuJieJing.App.Resources;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

internal sealed class ReplacementBlockCard(MinecraftReplacementBlock block) : INotifyPropertyChanged
{
    internal MinecraftReplacementBlock Block { get; } = block;
    internal ReplacementBlockRelevance Relevance { get; } = ReplacementBlockRelevance.Describe(block.State.Name);
    internal Color? TextureColor { get; private set; }
    public string Name => UiText.BlockName(Block.State.Name);
    public string Id => Block.State.Name;
    public string Quantity => Block.Count > 0 ? UiText.Format($"{Block.Count:N0} 个") : "";
    public string Tooltip => $"{Name}\n{Id}" + (Block.Count > 0 ? UiText.Format($"\n地图内共 {Block.Count:N0} 个") : "");
    public ImageSource? Thumbnail { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetThumbnail(ImageSource image, bool isTexture = false)
    {
        Thumbnail = image;
        TextureColor = isTexture ? ReplacementBlockRelevance.TextureColor(image) : null;
        PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
    }
    internal bool MatchesSearch(string text) => Name.Contains(text, StringComparison.OrdinalIgnoreCase) || Id.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        (Block.LegacyEncoding is { } code && code.NumericId.ToString().Contains(text, StringComparison.Ordinal));
}

public partial class MapFunctionsWindow
{
    internal event EventHandler? ReplacementScanRequested;
    internal event EventHandler? ReplacementApplyRequested;
    private MinecraftReplacementInventory? replacementInventory;
    private ReplacementBlockCard[] replacementSources = [], replacementTargets = [];
    private string? replacementRevision;
    private bool replacementAvailable;
    private CancellationTokenSource? replacementThumbnailCancellation;
    private bool updatingReplacementLists;

    internal void SetReplacementContext(string? revision, bool available)
    {
        if(replacementRevision != revision)
        {
            CancelReplacementThumbnails();
            replacementRevision = revision;
            replacementInventory = null;
            replacementSources = replacementTargets = [];
            ReplacementSourceList.ItemsSource = null;
            ReplacementTargetList.ItemsSource = null;
            UiText.Bind(ReplacementStatusText, "Text", UiText.Text("地图已更换，请重新扫描。"));
        }
        replacementAvailable = available;
        ScanReplacementButton.IsEnabled = available;
        ReplacementInputs.IsEnabled = available && replacementInventory is not null;
        UpdateReplacementSummary();
    }

    internal void SetReplacementInventory(MinecraftReplacementInventory inventory)
    {
        CancelReplacementThumbnails();
        replacementInventory = inventory;
        bool legacy = inventory.Blocks.All(b => b.LegacyEncoding is not null);
        IEnumerable<MinecraftReplacementBlock> targets = legacy
            ? Minecraft1122BlockMappingCatalog.LegacyTargets.Select(t => new MinecraftReplacementBlock(t.State, 0, t.LegacyEncoding))
            : inventory.Blocks.Concat(new[] { "air", "stone", "dirt", "cobblestone", "bedrock", "glass", "sand", "gravel", "obsidian", "gold_block", "iron_block", "diamond_block" }
                .Select(n => new MinecraftReplacementBlock(new BlockState("minecraft:" + n), 0, null)));
        replacementSources = CreateReplacementCards(inventory.Blocks.Where(b => !b.State.IsAir))
            .OrderByDescending(card => card.Block.Count).ToArray();
        replacementTargets = CreateReplacementCards(targets);
        UiText.Bind(ReplacementTargetHelp, "Text", UiText.Text("同类方块自动保留兼容的朝向与结构，替换为其他类型时自动使用合适状态。"));
        FilterReplacementLists();
        UiText.Bind(ReplacementStatusText, "Text", UiText.Message($"扫描完成：{inventory.DimensionCount:N0} 个维度，{inventory.ChunkCount:N0} 个区块，{replacementSources.Length:N0} 种方块。"));
        PopulateReplacementThumbnails();
    }

    private static ReplacementBlockCard[] CreateReplacementCards(IEnumerable<MinecraftReplacementBlock> blocks) => blocks
        .GroupBy(MinecraftBlockReplacementRules.Identity, StringComparer.Ordinal)
        .Select(g => new ReplacementBlockCard(MinecraftBlockReplacementRules.Representative(g)))
        .OrderBy(c => c.Name, StringComparer.CurrentCulture).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();

    internal MinecraftBlockReplacementRequest? GetReplacementRequest() => replacementInventory is not null &&
        ReplacementSourceList.SelectedItem is ReplacementBlockCard source && ReplacementTargetList.SelectedItem is ReplacementBlockCard target
        ? new(replacementInventory, source.Block, target.Block) : null;

    internal void SetReplacementStatus(string text) => ReplacementStatusText.Text = text;
    private void ReplacementNavigation_Click(object sender, RoutedEventArgs e) => SelectSection(MapFunctionSection.Replacement);
    private void ScanReplacement_Click(object sender, RoutedEventArgs e) => ReplacementScanRequested?.Invoke(this, EventArgs.Empty);
    private void ApplyReplacement_Click(object sender, RoutedEventArgs e) => ReplacementApplyRequested?.Invoke(this, EventArgs.Empty);
    private void ReplacementSearch_Changed(object sender, TextChangedEventArgs e) => FilterReplacementLists();
    private void ReplacementSelection_Changed(object sender, RoutedEventArgs e)
    {
        if(updatingReplacementLists) return;
        if(ReferenceEquals(sender, ReplacementSourceList)) RefreshReplacementTargets(scrollToTop: true);
        UpdateReplacementSummary();
    }

    private void FilterReplacementLists()
    {
        if(ReplacementSourceList is null || ReplacementTargetList is null || replacementInventory is null) return;
        object? source = ReplacementSourceList.SelectedItem;
        updatingReplacementLists = true;
        try
        {
            ReplacementSourceList.ItemsSource = replacementSources.Where(x => x.MatchesSearch(ReplacementSourceSearch.Text.Trim())).ToArray();
            ReplacementSourceList.SelectedItem = source;
        }
        finally { updatingReplacementLists = false; }
        RefreshReplacementTargets(scrollToTop: true);
        UpdateReplacementSummary();
    }

    private void RefreshReplacementTargets(bool scrollToTop)
    {
        if(ReplacementTargetList is null || ReplacementTargetSearch is null) return;
        var source = ReplacementSourceList.SelectedItem as ReplacementBlockCard;
        var selected = ReplacementTargetList.SelectedItem;
        IEnumerable<ReplacementBlockCard> targets = replacementTargets.Where(card => card.MatchesSearch(ReplacementTargetSearch.Text.Trim()));
        if(source is not null)
            targets = targets.OrderByDescending(card => MinecraftBlockReplacementRules.Identity(source.Block) == MinecraftBlockReplacementRules.Identity(card.Block) ? -1000 :
                card.Block.State.IsAir || MinecraftBlockReplacementRules.StructuralError(source.Block, card.Block) is not null ? -500 :
                source.Relevance.Score(card.Relevance, source.TextureColor, card.TextureColor));
        updatingReplacementLists = true;
        try
        {
            ReplacementTargetList.ItemsSource = targets.ToArray();
            ReplacementTargetList.SelectedItem = selected;
            UiText.Bind(ReplacementTargetHeading, "Text", source is null ? UiText.Text("B · 替换成") : UiText.Text("B · 替换成（相关方块优先）"));
            ReplacementTargetHeading.ToolTip = source is null ? null : UiText.Format($"根据“{source.Name}”的类型、材质和颜色排序");
            if(scrollToTop && ReplacementTargetList.Items.Count > 0)
                ReplacementTargetList.ScrollIntoView(ReplacementTargetList.Items[0]);
        }
        finally { updatingReplacementLists = false; }
    }

    private void UpdateReplacementSummary()
    {
        if(ApplyReplacementButton is null || ReplacementSummaryText is null) return;
        var request = GetReplacementRequest();
        ApplyReplacementButton.IsEnabled = false;
        if(request is null) { UiText.Bind(ReplacementSummaryText, "Text", UiText.Text("分别点选要替换的方块和目标方块。")); return; }
        try
        {
            string? error = MinecraftBlockReplacementRules.StructuralError(request.Source, request.Target);
            if(error is not null) { ReplacementSummaryText.Text = error; return; }
            long count = MinecraftWorldBlockReplacement.CountMatches(request);
            string a = UiText.BlockName(request.Source.State.Name), b = UiText.BlockName(request.Target.State.Name);
            UiText.Bind(ReplacementSummaryText, "Text", count == 0 ? UiText.Text("来源和目标相同，无需替换。") : UiText.Message($"{a} → {b} · 共 {count:N0} 个方块 · 全部 {request.Inventory.DimensionCount:N0} 个维度"));
            ApplyReplacementButton.IsEnabled = replacementAvailable && count > 0;
        }
        catch(Exception exception) when(exception is System.IO.InvalidDataException or InvalidOperationException)
        {
            ReplacementSummaryText.Text = exception.Message;
        }
    }

    private void CancelReplacementThumbnails() => replacementThumbnailCancellation?.Cancel();

    internal void ReloadReplacementThumbnails()
    {
        CancelReplacementThumbnails();
        if(replacementInventory is not null) PopulateReplacementThumbnails();
    }

    private async void PopulateReplacementThumbnails()
    {
        CancellationTokenSource cancellation = new();
        replacementThumbnailCancellation = cancellation;
        try
        {
            using MinecraftBlockThumbnailProvider provider = await MinecraftBlockThumbnailProvider.CreateConfiguredAsync(thumbnailPixelSize: 48, cancellationToken: cancellation.Token);
            ReplacementBlockCard[] cards = replacementSources.Concat(replacementTargets).ToArray();
            foreach(var card in cards) card.SetThumbnail(card.Block.State.IsAir ? provider.EmptyThumbnail : provider.MissingThumbnail);
            foreach(var batch in cards.Chunk(6))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var images = await Task.Run(() => batch.Select(card => card.Block.LegacyEncoding is { } code
                    ? provider.GetLegacyThumbnail(code) : provider.GetModernThumbnail(card.Block.State.CanonicalKey)).ToArray(), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                for(int i = 0; i < batch.Length; i++) batch[i].SetThumbnail(images[i], !ReferenceEquals(images[i], provider.MissingThumbnail) && !ReferenceEquals(images[i], provider.EmptyThumbnail));
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
            // Do not reshuffle each thumbnail batch while the user is choosing a target.
            if(ReplacementSourceList.SelectedItem is not null && ReplacementTargetList.SelectedItem is null)
                RefreshReplacementTargets(scrollToTop: true);
        }
        catch(OperationCanceledException) { }
        catch(Exception)
        {
            if(!ownerShutdown && ReferenceEquals(replacementThumbnailCancellation, cancellation))
                UiText.Bind(ReplacementTargetHelp, "Text", UiText.Text("部分贴图未能载入；仍可按方块名称选择。兼容状态由替换机制自动处理。"));
        }
        finally
        {
            if(ReferenceEquals(replacementThumbnailCancellation, cancellation)) replacementThumbnailCancellation = null;
            cancellation.Dispose();
        }
    }
}
