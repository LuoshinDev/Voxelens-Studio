using System.ComponentModel;
using System.Windows;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

internal enum MapFunctionSection
{
    Crop,
    Slimming,
    Conversion,
    Replacement,
}

public partial class MapFunctionsWindow : Window
{
    private bool ownerShutdown;
    private string? currentWorldName;
    private string? currentDimensionName;
    private MapFunctionSection selectedSection;

    public MapFunctionsWindow()
    {
        InitializeComponent();
        RestoreWindowSize();
        UpdateWorldSlimmingConcurrencyText();
        SelectSection(MapFunctionSection.Crop);
    }

    internal event EventHandler? WorldCropRequested;

    internal event EventHandler? WorldSlimmingAnalysisRequested;

    internal event EventHandler? WorldSlimmingApplyRequested;

    internal event EventHandler? ConversionExportRequested;

    internal event EventHandler? ConversionSectionOpened;

    internal event EventHandler? TaskCancellationRequested;

    internal void SetTaskCancellation(bool busy, bool canCancel)
    {
        CancelTaskButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelTaskButton.IsEnabled = canCancel;
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e) => TaskCancellationRequested?.Invoke(this, EventArgs.Empty);

    internal bool IsConversionSectionActive => selectedSection == MapFunctionSection.Conversion;

    internal int WorldSlimmingConcurrency => Math.Clamp(
        (int)Math.Round(WorldSlimmingConcurrencySlider.Value, MidpointRounding.AwayFromZero),
        MinecraftWorldSlimmingService.MinimumAnalysisConcurrency,
        MinecraftWorldSlimmingService.MaximumAnalysisConcurrency);

    internal string ConversionYOffsetText
    {
        get => ConversionYOffsetTextBox.Text;
        set => ConversionYOffsetTextBox.Text = value;
    }

    internal void ShowWindow()
    {
        if(!IsVisible) Show();
        if(WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if(selectedSection == MapFunctionSection.Conversion)
            ConversionSectionOpened?.Invoke(this, EventArgs.Empty);
    }

    internal void ShowSection(MapFunctionSection section)
    {
        SelectSection(section);
        ShowWindow();
    }

    internal void SetWorldContext(string? worldName, string? dimensionName, bool operationRunning)
    {
        if(!string.IsNullOrWhiteSpace(worldName))
        {
            currentWorldName = worldName;
            currentDimensionName = dimensionName;
        }
        else if(!operationRunning)
        {
            currentWorldName = null;
            currentDimensionName = null;
        }

        UiText.Bind(WorldContextStateText, "Text", operationRunning ? UiText.Text("地图处理中") : currentWorldName is null ? UiText.Text("未挂载地图") : UiText.Text("地图已挂载"));
        WorldContextText.Text = currentWorldName is null
            ? UiText.Get("请先打开 Minecraft 世界")
            : string.IsNullOrWhiteSpace(currentDimensionName)
                ? currentWorldName
                : $"{currentWorldName} · {currentDimensionName}";
        WorldContextText.ToolTip = WorldContextText.Text;
        UiText.Bind(WindowStatusText, "Text", operationRunning ? UiText.Text("地图任务正在执行；可以隐藏此窗口，任务不会中断。") : currentWorldName is null ? UiText.Text("打开 Minecraft 世界后即可使用地图工具。") : UiText.Text("选择左侧工具后，在此窗口中完成额外操作。"));
    }

    internal void SetActionAvailability(bool cropEnabled, bool analyzeEnabled, bool applyEnabled)
    {
        OpenWorldCropButton.IsEnabled = cropEnabled;
        AnalyzeWorldSlimmingButton.IsEnabled = analyzeEnabled;
        ApplyWorldSlimmingButton.IsEnabled = applyEnabled;
        WorldSlimmingConcurrencySlider.IsEnabled = analyzeEnabled;
    }

    internal void SetConversionControls(
        string status,
        bool yOffsetEnabled,
        bool exportEnabled)
    {
        ConversionAvailabilityText.Text = status;
        ConversionYOffsetTextBox.IsEnabled = yOffsetEnabled;
        ConversionExportButton.IsEnabled = exportEnabled;
    }

    internal void SetCropSummary(Func<string> summary) => UiText.Bind(WorldCropSummaryText, "Text", summary);

    internal void SetSlimmingSummary(Func<string> summary) => UiText.Bind(WorldSlimmingSummaryText, "Text", summary);

    internal void SetWindowStatus(string status) => WindowStatusText.Text = status;

    internal void SetCropProgress(string stage, bool indeterminate, int percentage, string progressText)
    {
        WorldCropProgressPanel.Visibility = Visibility.Visible;
        WorldCropProgressStageText.Text = stage;
        WorldCropProgressBar.IsIndeterminate = indeterminate;
        WorldCropProgressBar.Value = Math.Clamp(percentage, 0, 100);
        WorldCropProgressText.Text = progressText;
    }

    internal void SetSlimmingProgress(string stage, bool indeterminate, int percentage, string progressText)
    {
        WorldSlimmingProgressPanel.Visibility = Visibility.Visible;
        WorldSlimmingProgressStageText.Text = stage;
        WorldSlimmingProgressBar.IsIndeterminate = indeterminate;
        WorldSlimmingProgressBar.Value = Math.Clamp(percentage, 0, 100);
        WorldSlimmingProgressText.Text = progressText;
    }

    internal void HideCropProgress()
    {
        WorldCropProgressBar.IsIndeterminate = false;
        WorldCropProgressBar.Value = 0d;
        WorldCropProgressText.Text = "0%";
        WorldCropProgressPanel.Visibility = Visibility.Collapsed;
    }

    internal void HideSlimmingProgress()
    {
        WorldSlimmingProgressBar.IsIndeterminate = false;
        WorldSlimmingProgressBar.Value = 0d;
        WorldSlimmingProgressText.Text = "0%";
        WorldSlimmingProgressPanel.Visibility = Visibility.Collapsed;
    }

    internal void CloseForOwnerShutdown()
    {
        ownerShutdown = true;
        CancelReplacementThumbnails();
        Close();
    }

    private void CropNavigation_Click(object sender, RoutedEventArgs e) => SelectSection(MapFunctionSection.Crop);

    private void SlimmingNavigation_Click(object sender, RoutedEventArgs e) => SelectSection(MapFunctionSection.Slimming);

    private void ConversionNavigation_Click(object sender, RoutedEventArgs e)
    {
        SelectSection(MapFunctionSection.Conversion);
        ConversionSectionOpened?.Invoke(this, EventArgs.Empty);
    }

    private void OpenWorldCrop_Click(object sender, RoutedEventArgs e) => WorldCropRequested?.Invoke(this, EventArgs.Empty);

    private void AnalyzeWorldSlimming_Click(object sender, RoutedEventArgs e) => WorldSlimmingAnalysisRequested?.Invoke(this, EventArgs.Empty);

    private void ApplyWorldSlimming_Click(object sender, RoutedEventArgs e) => WorldSlimmingApplyRequested?.Invoke(this, EventArgs.Empty);

    private void WorldSlimmingConcurrencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateWorldSlimmingConcurrencyText();

    private void ConversionExport_Click(object sender, RoutedEventArgs e) => ConversionExportRequested?.Invoke(this, EventArgs.Empty);

    private void SelectSection(MapFunctionSection section)
    {
        selectedSection = section;
        bool crop = section == MapFunctionSection.Crop;
        bool slimming = section == MapFunctionSection.Slimming;
        bool conversion = section == MapFunctionSection.Conversion;
        bool replacement = section == MapFunctionSection.Replacement;
        ReplacementNavigationButton.IsChecked = replacement;
        ReplacementDetailPanel.Visibility = replacement ? Visibility.Visible : Visibility.Collapsed;
        CropNavigationButton.IsChecked = crop;
        SlimmingNavigationButton.IsChecked = slimming;
        ConversionNavigationButton.IsChecked = conversion;
        CropDetailPanel.Visibility = crop ? Visibility.Visible : Visibility.Collapsed;
        SlimmingDetailPanel.Visibility = slimming ? Visibility.Visible : Visibility.Collapsed;
        ConversionDetailPanel.Visibility = conversion ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateWorldSlimmingConcurrencyText()
    {
        if(WorldSlimmingConcurrencyText is not null)
            UiText.Bind(WorldSlimmingConcurrencyText, "Text", UiText.Message($"{WorldSlimmingConcurrency:N0} 路"));
    }

    private void MapFunctionsWindow_Closing(object? sender, CancelEventArgs e)
    {
        SaveWindowSize();
        if(ownerShutdown) return;
        e.Cancel = true;
        Hide();
    }
}
