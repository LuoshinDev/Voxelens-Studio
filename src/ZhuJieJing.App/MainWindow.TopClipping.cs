using System.Windows;
using ZhuJieJing.App.ConversionPreview;
using ZhuJieJing.App.WorldPreview;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private async Task<IMinecraftDowngradePreview?> ConfirmTopClippingAsync(
        IReadOnlyMinecraftWorld world, MinecraftDowngradePreviewSummary summary, CancellationToken cancellation)
    {
        var limits = summary.YTranslation;
        if(limits.CanFitWithoutClipping || limits.OccupiedSourceRange is not { } range || limits.MinimumOffset is not int offset)
        {
            StatusText.Text = Minecraft1122ConversionPreviewWorkflow.FormatHeightBlockReason(summary);
            return null;
        }
        long keptTop = (long)range.Minimum + 255;
        string message = UiText.Format($"地图实际高度：Y {range.Minimum}～{range.Maximum}。\n\n") +
            UiText.Format($"保留底部 Y {range.Minimum}～{keptTop}，整体偏移 {offset:+0;-0;0} 格，放入 1.12.2 的 Y 0～255。\n") +
            UiText.Format($"顶部 Y {keptTop + 1}～{range.Maximum}，共 {(long)range.Maximum - keptTop} 层，将从转换结果中删除，包含其中的方块和方块实体。\n\n") +
            UiText.Get("此方案应用于本次转换的所有维度。原始地图不会修改。\n\n是否允许裁掉顶部并继续？");
        if(UiMessageBox.Show(this, message, UiText.Get("允许裁掉顶部？"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("已取消顶部裁切，源地图未修改。"));
            return null;
        }
        cancellation.ThrowIfCancellationRequested();
        if(isClosing || !ReferenceEquals(world, mountedWorld)) throw new OperationCanceledException(cancellation);
        UiText.Bind(StatusText, "Text", UiText.Text("正在生成已确认的顶部裁切预览…"));
        var source = NormalizedMinecraftChunkSourceFactory.Create(world);
        var rules = await GetBlockMappingRulesAsync();
        int concurrency = Minecraft1122ConversionPreviewWorkflow.SelectConversionConcurrency(CurrentChunkLoadConcurrency);
        var progress = new Progress<MinecraftConversionProgress>(value =>
        {
            if(!isClosing && !cancellation.IsCancellationRequested)
                UiText.Bind(StatusText, "Text", UiText.Message($"正在分析顶部裁切：{value.ChunksScanned:N0} 个区块…"));
        });
        return await Task.Run(async () =>
        {
            var addresses = await CollectAllChunkAddressesAsync(world.ChunkIndex, source.Dimensions, cancellation);
            var request = Minecraft1122ConversionPreviewWorkflow.CreateRequest(source, offset, addresses, concurrency) with
            {
                Policy = new MinecraftDowngradePolicy(Minecraft1122ConversionPreviewWorkflow.VisibleFallback,
                    allowVerticalClipping: true, preserveLighting: true),
            };
            return await new MinecraftDowngradePreviewService(rules).CreateAsync(request, progress, cancellation);
        }, cancellation);
    }
}
