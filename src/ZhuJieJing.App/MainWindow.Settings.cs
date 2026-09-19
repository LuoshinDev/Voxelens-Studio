using System.Windows;
using ZhuJieJing.App.Localization;
using ZhuJieJing.App.Preferences;
using ZhuJieJing.App.Resources;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private void InterfaceLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(isClosing || !isUiReady) return;
        UpdateRenderInfoText();
        UpdateMapFunctionsControls();
        UpdateConversionControls();
        UpdateUnifiedExportMenu();
    }

    private async void StudioSettings_Click(object sender, RoutedEventArgs e)
    {
        if(isClosing || isChangingMaterialMode || ForegroundOperations().Any())
        {
            UiText.Bind(StatusText, "Text", UiText.Text("请先完成或取消当前任务，再修改设置。"));
            return;
        }
        StudioSettings previous = StudioSettingsStore.Load();
        StudioSettingsWindow window = new(mountedWorld?.Descriptor.RootPath) { Owner = this };
        TrackTopLevelWindowRenderingSuspension(window);
        if(window.ShowDialog() != true) return;
        if(previous.LegacyJar != window.Settings.LegacyJar || previous.ModernJar != window.Settings.ModernJar)
        {
            inspectionGeneration++;
            MinecraftBlockEnglishNames.Reload();
            inspectionThumbnails?.Dispose();
            inspectionThumbnails = null;
            mapFunctionsWindow?.ReloadReplacementThumbnails();
            await RefreshMaterialModeAsync();
        }
        UiText.Bind(StatusText, "Text", UiText.Text("设置已保存。"));
    }
}
