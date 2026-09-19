
using System.IO;

using System.Windows;

using System.Windows.Media.Imaging;

using Microsoft.Win32;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private bool isApplyingPhotographyPreset;

    private void EnhancedLightingToggle_Click(object sender, RoutedEventArgs e)
    {
        if(!isUiReady) return;
        bool enabled = EnhancedLightingToggle.IsChecked == true;
        Viewport.SetEnhancedLightingEnabled(enabled);
        SchedulePreviewPreferencesSave();
        UiText.Bind(StatusText, "Text", enabled ? UiText.Text("已启用画质增强。") : UiText.Text("已关闭画质增强。"));
    }

    private void PhotographyMorningPreset_Checked(object sender, RoutedEventArgs e) =>
        ApplyPhotographyPreset(7.5d, "清晨");

    private void PhotographyGoldenPreset_Checked(object sender, RoutedEventArgs e) =>
        ApplyPhotographyPreset(17.25d, "暖阳");

    private void PhotographyDuskPreset_Checked(object sender, RoutedEventArgs e) =>
        ApplyPhotographyPreset(19.25d, "暮色");

    private void ApplyPhotographyPreset(double timeOfDay, string name)
    {
        isApplyingPhotographyPreset = true;
        try
        {
            TimeOfDaySlider.Value = timeOfDay;
            if(!isUiReady) return;

            Viewport.SetTimeOfDay((float)timeOfDay);
            SchedulePreviewPreferencesSave();
            UiText.Bind(StatusText, "Text", () => UiText.Format($"已应用{UiText.Get(name)}光线预设，天气设置保持不变。"));
        }
        finally
        {
            isApplyingPhotographyPreset = false;
        }
    }

    private void ClearPhotographyLightPreset()
    {
        if(isApplyingPhotographyPreset || PhotographyMorningPresetChoice is null) return;
        PhotographyMorningPresetChoice.IsChecked = false;
        PhotographyGoldenPresetChoice.IsChecked = false;
        PhotographyDuskPresetChoice.IsChecked = false;
    }

    private void PhotographyFieldOfViewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int degrees = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        if(PhotographyFieldOfViewText is not null) PhotographyFieldOfViewText.Text = $"{degrees}°";
        if(isUiReady)
        {
            Viewport.SetVerticalFieldOfViewDegrees(degrees);
        }
    }

    private async void CaptureViewport_Click(object sender, RoutedEventArgs e)
    {
        if(isClosing || photographyCancellation is not null) return;
        if(displayedContent == DisplayedContent.None)
        {
            UiText.Bind(StatusText, "Text", UiText.Text("请先打开地图、结构或蓝图，再保存画面截图。"));
            return;
        }

        string worldName = CreateScreenshotFileName(WorldNameText.Text);
        SaveFileDialog dialog = new()
        {
            Title = UiText.Get("保存摄影模式截图"),
            Filter = UiText.Get("PNG 图片 (*.png)|*.png"),
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"{worldName}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if(dialog.ShowDialog(this) != true) return;
        if(isClosing) return;

        using CancellationTokenSource cancellation = new();
        photographyCancellation = cancellation;
        int sceneGeneration = previewGeneration;
        CaptureViewportButton.IsEnabled = false;
        string temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            UiText.Bind(StatusText, "Text", UiText.Text("正在等待完整渲染画面…"));
            BitmapSource screenshot = await Viewport.CaptureSceneAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(isClosing || sceneGeneration != previewGeneration) throw new OperationCanceledException(cancellation.Token);
            UiText.Bind(StatusText, "Text", UiText.Text("正在保存无界面遮挡的 PNG…"));
            await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(screenshot));
                using(FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    encoder.Save(output);
                cancellation.Token.ThrowIfCancellationRequested();
                File.Move(temporary, dialog.FileName, overwrite: true);
            }, cancellation.Token);
            if(!isClosing && sceneGeneration == previewGeneration) UiText.Bind(StatusText, "Text", UiText.Message($"画面截图已保存：{dialog.FileName}"));
        }
        catch(OperationCanceledException) { if(!isClosing && sceneGeneration == previewGeneration) UiText.Bind(StatusText, "Text", UiText.Text("截图已取消。")); }
        catch(Exception exception)
        {
            if(!isClosing && sceneGeneration == previewGeneration)
            {
UiMessageBox.Show(this, exception.Message, UiText.Get("截图失败"), MessageBoxButton.OK, MessageBoxImage.Error);
                UiText.Bind(StatusText, "Text", UiText.Text("画面截图保存失败。"));
            }
        }
        finally
        {
            if(File.Exists(temporary)) { try { File.Delete(temporary); } catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { } }
            if(ReferenceEquals(photographyCancellation, cancellation)) photographyCancellation = null;
            if(!isClosing) CaptureViewportButton.IsEnabled = true;
        }
    }
    private static string CreateScreenshotFileName(string source)
    {
        string fallback = string.IsNullOrWhiteSpace(source) || source == "尚未选择世界" ? "筑界镜摄影" : source.Trim();
        foreach(char invalid in Path.GetInvalidFileNameChars()) fallback = fallback.Replace(invalid, '-');
        return fallback;
    }
}
