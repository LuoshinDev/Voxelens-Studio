using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Controls;
using ZhuJieJing.Renderer.Minecraft;

namespace ZhuJieJing.App;

/// <summary>Two independent scenes sharing a logical source-world camera.</summary>
public sealed class ConversionComparisonWindow : Window
{
    private readonly VoxelViewport original = new();
    private readonly VoxelViewport converted = new();
    private readonly TextBlock status = new() { Margin = new Thickness(14, 8, 14, 8) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly DispatcherTimer cameraTimer;
    private readonly int yOffset;
    private VoxelViewport driver;
    private ViewportCameraPose? lastPose;
    private bool closed;

    public ConversionComparisonWindow(int yOffset)
    {
        this.yOffset = yOffset;
        lifetimeToken = lifetime.Token;
        driver = original;
        Title = UiText.Get("转换前后 · 同镜头对照");
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/ZhuJieJing.ico"));
        Width = Math.Min(1400, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(850, SystemParameters.WorkArea.Height - 32);
        MinWidth = 900; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(247, 245, 252));
        var layout = new DockPanel();
        var header = new Grid { Margin = new Thickness(18, 12, 18, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var originalLabel = new TextBlock { Text = UiText.Get("原始世界"), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var convertedLabel = new TextBlock { Text = UiText.Get("1.12.2 转换结果"), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(convertedLabel, 1);
        header.Children.Add(originalLabel); header.Children.Add(convertedLabel);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var footer = new DockPanel();
        var cancel = new Button { Content = UiText.Get("取消加载 / 关闭"), Margin = new Thickness(8), Padding = new Thickness(12, 5, 12, 5) };
        cancel.Click += (_, _) => Close(); DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
        status.TextWrapping = TextWrapping.Wrap;
        status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(status); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var views = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        views.ColumnDefinitions.Add(new ColumnDefinition()); views.ColumnDefinitions.Add(new ColumnDefinition());
        original.Margin = new Thickness(0, 0, 5, 0); converted.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(converted, 1); views.Children.Add(original); views.Children.Add(converted); layout.Children.Add(views);
        Content = layout;
        foreach(VoxelViewport viewport in new[] { original, converted })
        {
            viewport.PreviewMouseDown += (_, _) => driver = viewport;
            viewport.PreviewMouseWheel += (_, _) => driver = viewport;
            viewport.GotKeyboardFocus += (_, _) => driver = viewport;
        }
        cameraTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => SynchronizeCamera(), Dispatcher);
        cameraTimer.Stop();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); cameraTimer.Stop(); original.Dispose(); converted.Dispose(); Content = null; lifetime.Dispose(); };
    }

    public async Task InitializeAsync(IReadOnlyList<NormalizedMinecraftSection> sourceSections,
        IReadOnlyList<NormalizedMinecraftSection> targetSections, Minecraft1122ResourceConfiguration? sourceResources,
        Minecraft1122ResourceConfiguration? targetResources, ViewportCameraPose logicalPose,
        bool enhanced, float time, bool clouds, bool rain, bool fog, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();
        UiText.Bind(status, "Text", UiText.Text("正在匹配两侧材质…"));
        if(sourceResources is not null) await original.ConfigureMinecraft1122ResourcesAsync(sourceResources, token);
        if(targetResources is not null) await converted.ConfigureMinecraft1122ResourcesAsync(targetResources, token);
        token.ThrowIfCancellationRequested();
        foreach(VoxelViewport viewport in new[] { original, converted })
        {
            viewport.SetEnhancedLightingEnabled(enhanced); viewport.SetTimeOfDay(time);
            viewport.SetCloudsEnabled(clouds); viewport.SetRainEnabled(rain); viewport.SetFogEnabled(fog);
            viewport.MaximumActiveSections = Math.Max(sourceSections.Count, targetSections.Count);
            viewport.SectionDrawDistance = 1024;
        }
        UiText.Bind(status, "Text", UiText.Text("正在构建同一片区域的两侧画面…"));
        await original.ReplaceSectionsAsync(sourceSections, token);
        await converted.ReplaceSectionsAsync(targetSections, token);
        original.ApplyCameraPose(logicalPose);
        converted.ApplyCameraPose(Translate(logicalPose, yOffset));
        await original.WaitForSectionGpuPublicationAsync(token);
        await converted.WaitForSectionGpuPublicationAsync(token);
        token.ThrowIfCancellationRequested();
        status.Text = UiText.Format($"两侧镜头同步 · 在任意一侧操作 · 转换高度偏移 {yOffset:+0;-0;0} 已对齐 · 对照当前加载区域") +
            (sourceResources is null || targetResources is null ? UiText.Get(" · 部分材质缺失，当前为纯色预览") : "");
        cameraTimer.Start();
    }

    private void SynchronizeCamera()
    {
        if(closed) return;
        ViewportCameraPose pose = driver.CaptureCameraPose();
        if(ReferenceEquals(driver, converted)) pose = Translate(pose, -yOffset);
        if(lastPose == pose) return;
        lastPose = pose;
        if(ReferenceEquals(driver, original)) converted.ApplyCameraPose(Translate(pose, yOffset));
        else original.ApplyCameraPose(pose);
    }

    private static ViewportCameraPose Translate(ViewportCameraPose pose, int y) =>
        new(pose.Camera with { Focus = pose.Camera.Focus + new Vector3(0, y, 0) });
}
