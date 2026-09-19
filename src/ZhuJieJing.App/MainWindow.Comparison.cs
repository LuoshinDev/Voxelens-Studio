using System.Numerics;
using System.Windows;
using ZhuJieJing.App.Resources;
using ZhuJieJing.App.WorldPreview;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Controls;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private Task? activeComparisonTask;

    private async void ConversionComparison_Click(object sender, RoutedEventArgs e)
    {
        if(mountedWorld is not { } world || displayedWorldSections.Count == 0)
        { UiText.Bind(StatusText, "Text", UiText.Text("请先打开世界并等待首屏载入，再进行转换对照。")); return; }
        if(isClosing || activeComparisonTask is not null || ForegroundOperations().Any())
        { UiText.Bind(StatusText, "Text", UiText.Text("请先完成或取消当前任务，再打开转换对照。")); return; }
        Task task = OpenConversionComparisonAsync(world);
        activeComparisonTask = task;
        try { await task; }
        finally { if(ReferenceEquals(activeComparisonTask, task)) activeComparisonTask = null; }
    }

    private async Task OpenConversionComparisonAsync(IReadOnlyMinecraftWorld world)
    {
        using CancellationTokenSource cancellation = new();
        comparisonCancellation = cancellation;
        SetSourceOpenButtonsEnabled(false);
        ConversionComparisonWindow? window = null;
        IMinecraftDowngradePreview? ownedClippedPreview = null;
        try
        {
            ViewportCameraPose pose = Viewport.CaptureCameraPose();
            pose = new(pose.Camera with { Focus = FromPreviewRenderPosition(pose.Camera.Focus) });
            MinecraftChunkAddress[] addresses = displayedWorldSections.Keys.Select(section =>
                new MinecraftChunkAddress(new MinecraftDimensionId(section.Dimension), section.X, section.Z)).Distinct().ToArray();
            IMinecraftDowngradePreview preview = activeConversionPreview ??
                await PrepareCurrentWorldMappingsAsync(world, await GetBlockMappingRulesAsync(), "准备转换对照");
            cancellation.Token.ThrowIfCancellationRequested();
            if(!ConversionPreview.Minecraft1122ConversionPreviewWorkflow.CanRenderWithoutClipping(preview.Summary) &&
               !ConversionPreview.Minecraft1122ConversionPreviewWorkflow.CanRenderConfirmedTopClipping(preview.Summary))
            {
                ownedClippedPreview = await ConfirmTopClippingAsync(world, preview.Summary, cancellation.Token);
                if(ownedClippedPreview is null) return;
                preview = ownedClippedPreview;
            }
            INormalizedMinecraftChunkSource source = NormalizedMinecraftChunkSourceFactory.Create(world);
            var original = new List<NormalizedMinecraftSection>();
            var converted = new List<NormalizedMinecraftSection>();
            var progress = new Progress<int>(count =>
            {
                if(!isClosing && !cancellation.IsCancellationRequested && ReferenceEquals(world, mountedWorld))
                    UiText.Bind(StatusText, "Text", UiText.Message($"正在准备对照区域：{count} / {addresses.Length} 个 Chunk…"));
            });
            await Task.Run(async () =>
            {
                for(int index = 0; index < addresses.Length; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if(await source.FindAsync(addresses[index], cancellation.Token) is { } before) original.AddRange(before.Sections);
                    if(await preview.ConvertedChunks.FindAsync(addresses[index], cancellation.Token) is { } after) converted.AddRange(after.Sections);
                    ((IProgress<int>)progress).Report(index + 1);
                }
            }, cancellation.Token);
            string root = world.Descriptor.RootPath;
            MinecraftResourceSelectionResult beforeResource = await minecraftResourceCacheService.ResolveAsync(root, world.Descriptor.Version, cancellationToken: cancellation.Token);
            MinecraftResourceSelectionResult afterResource = await minecraftResourceCacheService.ResolveAsync(root, MinecraftTargetProfile.Java1122.Version, cancellationToken: cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(isClosing || !ReferenceEquals(world, mountedWorld)) throw new OperationCanceledException(cancellation.Token);
            window = new ConversionComparisonWindow(preview.Summary.AppliedYOffset) { Owner = this };
            window.Show();
            await window.InitializeAsync(original, converted,
                beforeResource.ClientJarPath is { } beforeJar ? CreateRendererConfiguration(new ResourceSelection(beforeJar, originalWorldResourceSelection?.ResourcePackPaths ?? [])) : null,
                afterResource.ClientJarPath is { } afterJar ? CreateRendererConfiguration(new ResourceSelection(afterJar, [])) : null,
                pose, EnhancedLightingToggle.IsChecked == true, (float)TimeOfDaySlider.Value,
                CloudsToggle.IsChecked == true, RainToggle.IsChecked == true, FogToggle.IsChecked == true, cancellation.Token);
            if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Text("同镜头转换对照已就绪。"));
        }
        catch(OperationCanceledException) { if(window?.IsLoaded == true) window.Close(); if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Text("转换对照加载已取消。")); }
        catch(Exception exception) { if(window?.IsLoaded == true) window.Close(); if(!isClosing) UiText.Bind(StatusText, "Text", UiText.Message($"无法打开转换对照：{exception.Message}")); }
        finally
        {
            if(ownedClippedPreview is not null) await ownedClippedPreview.DisposeAsync();
            if(ReferenceEquals(comparisonCancellation, cancellation)) comparisonCancellation = null;
            if(!isClosing && !ForegroundOperations().Any()) SetSourceOpenButtonsEnabled(true);
        }
    }

    private async Task CancelComparisonAsync()
    {
        Task? task = activeComparisonTask;
        comparisonCancellation?.Cancel();
        if(task is null) return;
        // The initial mapping scan owns its own token; cancel it before awaiting
        // the comparison that consumes its result and the mounted world.
        conversionPreviewCancellation?.Cancel();
        try { await task; }
        catch(OperationCanceledException) { }
    }
}
