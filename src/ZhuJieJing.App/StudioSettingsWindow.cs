using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ZhuJieJing.App.Preferences;
using ZhuJieJing.App.Resources;

namespace ZhuJieJing.App;

public partial class StudioSettingsWindow : Window
{
    private readonly MinecraftResourceCacheService resources = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly string worldRootPath;
    private bool importing;
    private bool updatingLanguages;
    private string languageSignature = "";
    public StudioSettings Settings { get; private set; } = StudioSettingsStore.Load();

    public StudioSettingsWindow(string? worldRootPath = null)
    {
        this.worldRootPath = worldRootPath ?? AppContext.BaseDirectory;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // The owner is assigned after construction; inherit its actual UI typeface.
            if(Owner is not { } owner) return;
            FontFamily = owner.FontFamily;
            FontSize = owner.FontSize;
        };
        RefreshLanguages();
        RefreshPath(LegacyPath, Settings.LegacyJar);
        RefreshPath(ModernPath, Settings.ModernJar);
        Activated += (_, _) => RefreshLanguages();
        Closing += (_, _) => lifetime.Cancel();
        Closed += (_, _) => { if(!importing) lifetime.Dispose(); };
    }

    private void RefreshLanguages()
    {
        var choices = UiText.AvailableLanguages();
        string signature = string.Join("\n", choices.Select(c => c.Id + ":" + c.DisplayName)) + UiText.Language;
        if(signature == languageSignature) return;
        updatingLanguages = true;
        try
        {
            LanguageChoices.Children.Clear();
            foreach(var choice in choices)
            {
                RadioButton button = new() { Content = choice.DisplayName, Tag = choice.Id, GroupName = "Language", IsChecked = choice.Id == UiText.Language, Style = (Style)FindResource("LanguageCard") };
                button.Checked += Language_Checked;
                LanguageChoices.Children.Add(button);
            }
            languageSignature = signature;
        }
        finally { updatingLanguages = false; }
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if(updatingLanguages || sender is not RadioButton { Tag: string id } || id == UiText.Language) return;
        try
        {
            // Language is immediate; resource edits remain a separate, explicit save.
            StudioSettings saved = StudioSettingsStore.Load();
            StudioSettingsStore.Save(saved with { Language = id });
            Settings = Settings with { Language = id };
            UiText.SetLanguage(id);
            languageSignature = "";
            UiText.Bind(Status, nameof(TextBlock.Text), UiText.Text("语言已保存"));
        }
        catch(Exception exception)
        {
            languageSignature = "";
            RefreshLanguages();
            Status.Text = exception.Message;
        }
    }

    private void OpenLanguages_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(UiText.LanguageDirectory);
            Process.Start(new ProcessStartInfo(UiText.LanguageDirectory) { UseShellExecute = true });
        }
        catch(Exception exception) { Status.Text = exception.Message; }
    }

    private static void RefreshPath(TextBox path, ResourceJarPreference? preference)
    {
        if(preference is null) UiText.Bind(path, nameof(TextBox.Text), UiText.Text("未设置"));
        else path.Text = preference.SourcePath;
        path.ToolTip = preference?.SourcePath;
    }

    private void ChooseLegacy_Click(object sender, RoutedEventArgs e) => Choose(MinecraftResourceCompatibilityFamily.Legacy1122);
    private void ChooseModern_Click(object sender, RoutedEventArgs e) => Choose(MinecraftResourceCompatibilityFamily.Modern);
    private async void FindLegacy_Click(object sender, RoutedEventArgs e) => await LocateAsync(MinecraftResourceCompatibilityFamily.Legacy1122);
    private async void FindModern_Click(object sender, RoutedEventArgs e) => await LocateAsync(MinecraftResourceCompatibilityFamily.Modern);

    private async void Choose(MinecraftResourceCompatibilityFamily family)
    {
        OpenFileDialog dialog = new() { Title = UiText.Get("选择 JAR"), Filter = "Minecraft JAR (*.jar)|*.jar", CheckFileExists = true };
        if(dialog.ShowDialog(this) != true) return;
        await ImportAsync(family, dialog.FileName);
    }

    private async Task LocateAsync(MinecraftResourceCompatibilityFamily family)
    {
        if(importing) return;
        await RunResourceActionAsync(async () =>
        {
            UiText.Bind(Status, nameof(TextBlock.Text), UiText.Text("正在查找本机资源…"));
            var found = await resources.DiscoverAsync(worldRootPath, family, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if(found.ClientJarPath is null)
            {
                UiText.Bind(Status, nameof(TextBlock.Text), UiText.Text("未找到可用 JAR，请手动选择。原设置保留。"));
                return;
            }
            // The resolved cache is itself a usable, stable path. Show it immediately.
            var preference = new ResourceJarPreference(found.ClientJarPath, found.ClientJarPath, found.SelectedVersion ?? (family == MinecraftResourceCompatibilityFamily.Legacy1122 ? "1.12.2" : "1.13+"), null);
            SetResource(family, preference);
            UiText.Bind(Status, nameof(TextBlock.Text), UiText.Text("已找到资源，点击保存资源应用"));
        });
    }

    private Task ImportAsync(MinecraftResourceCompatibilityFamily family, string path) => RunResourceActionAsync(async () =>
    {
        UiText.Bind(Status, nameof(TextBlock.Text), UiText.Text("正在校验并缓存资源 JAR…"));
        var preference = await resources.ImportConfiguredJarAsync(path, family, lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        SetResource(family, preference);
        UiText.Bind(Status, nameof(TextBlock.Text), UiText.Text("资源已就绪，点击保存资源应用"));
    });

    private void SetResource(MinecraftResourceCompatibilityFamily family, ResourceJarPreference preference)
    {
        Settings = family == MinecraftResourceCompatibilityFamily.Legacy1122 ? Settings with { LegacyJar = preference } : Settings with { ModernJar = preference };
        RefreshPath(family == MinecraftResourceCompatibilityFamily.Legacy1122 ? LegacyPath : ModernPath, preference);
    }

    private async Task RunResourceActionAsync(Func<Task> action)
    {
        if(importing) return;
        importing = true; Body.IsEnabled = false; SaveButton.IsEnabled = false;
        try { await action(); }
        catch(OperationCanceledException) { }
        catch(Exception exception) { if(!lifetime.IsCancellationRequested) Status.Text = UiText.Get(exception.Message); }
        finally
        {
            importing = false;
            if(lifetime.IsCancellationRequested) lifetime.Dispose();
            else { Body.IsEnabled = true; SaveButton.IsEnabled = true; }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if(importing) return;
        try
        {
            Settings = Settings with { Language = UiText.Language };
            StudioSettingsStore.Save(Settings);
            DialogResult = true;
        }
        catch(Exception exception) { Status.Text = UiText.Format($"设置保存失败：{exception.Message}"); }
    }
}
