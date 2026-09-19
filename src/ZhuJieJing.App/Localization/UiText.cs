using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Data;
using ZhuJieJing.App.Preferences;
using ZhuJieJing.App.Resources;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.Localization;

/// <summary>Presentation-only strings. Never translate block IDs, paths, persisted data or protocol fields.</summary>
public static class UiText
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() =>
    {
        using Stream stream = typeof(UiText).Assembly.GetManifestResourceStream("ZhuJieJing.App.Localization.en-US.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    });
    public static string LanguageDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "languages");
    public static string Language { get; private set; } = StudioSettingsStore.Load().Language;
    private static Dictionary<string, string> selected = LoadLanguage(Language) ?? new(StringComparer.Ordinal);
    internal static LanguageNotifications Notifications { get; } = new();

    public static void SetLanguage(string language)
    {
        if(!StudioSettingsStore.IsLanguageId(language)) return;
        Language = language;
        selected = LoadLanguage(language) ?? new(StringComparer.Ordinal);
        Notifications.Refresh();
        if(Application.Current is { } app)
            foreach(Window window in app.Windows) RefreshDisplayBindings(window);
    }

    private static void RefreshDisplayBindings(DependencyObject element)
    {
        // Refresh visible display-name getters, never editable text or item collections.
        // This preserves selections, expansion, scroll position and unsaved input.
        if(element is System.Windows.Controls.TextBlock)
            BindingOperations.GetBindingExpression(element, System.Windows.Controls.TextBlock.TextProperty)?.UpdateTarget();
        if(element is System.Windows.Controls.HeaderedContentControl)
            BindingOperations.GetBindingExpression(element, System.Windows.Controls.HeaderedContentControl.HeaderProperty)?.UpdateTarget();
        if(element is System.Windows.Controls.HeaderedItemsControl)
            BindingOperations.GetBindingExpression(element, System.Windows.Controls.HeaderedItemsControl.HeaderProperty)?.UpdateTarget();
        if(element is not System.Windows.Media.Visual) return;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(element);
        for(int i = 0; i < count; i++) RefreshDisplayBindings(System.Windows.Media.VisualTreeHelper.GetChild(element, i));
    }

    public static void Bind(DependencyObject target, string property, Func<string> text)
    {
        DependencyProperty? dependencyProperty = DependencyPropertyDescriptor.FromName(property, target.GetType(), target.GetType())?.DependencyProperty;
        if(dependencyProperty is null) throw new ArgumentException($"Unknown text property {property}.", nameof(property));
        BindingOperations.SetBinding(target, dependencyProperty, LiveBinding(text));
    }

    public static Func<string> Text(string key) => () => Get(key);
    public static Func<string> Message(FormattableString text) => () => Format(text);

    internal static Binding LiveBinding(Func<string> text) => new(nameof(LiveText.Value)) { Source = new LiveText(text), Mode = BindingMode.OneWay };
    public static bool IsEnglish => Language == "en-US";
    public static string Get(string text)
    {
        if(selected.TryGetValue(text, out string? translated)) return translated;
        return Language != "zh-CN" && English.Value.TryGetValue(text, out translated) ? translated : text;
    }
    public static string Format(FormattableString text)
    {
        try { return string.Format(CultureInfo.CurrentCulture, Get(text.Format), text.GetArguments()); }
        catch(FormatException)
        {
            try { return string.Format(CultureInfo.CurrentCulture, Language != "zh-CN" ? English.Value.GetValueOrDefault(text.Format, text.Format) : text.Format, text.GetArguments()); }
            catch(FormatException) { return text.ToString(CultureInfo.CurrentCulture); }
        }
    }
    public static string BlockName(string id) => selected.TryGetValue("block." + id, out string? name) ? name : Language != "zh-CN"
        ? MinecraftBlockEnglishNames.GetName(id)
        : Minecraft1122BlockDisplayNames.GetDisplayName(id);

    public static void EnsureLanguageFiles()
    {
        try
        {
            Directory.CreateDirectory(LanguageDirectory);
            foreach(string id in new[] { "zh-CN", "en-US" })
            {
                string path = Path.Combine(LanguageDirectory, id + ".json");
                if(File.Exists(path)) continue;
                var texts = English.Value.ToDictionary(pair => pair.Key, pair => id == "zh-CN" ? pair.Key : pair.Value, StringComparer.Ordinal);
                texts["$name"] = id == "zh-CN" ? "简体中文" : "English";
                using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, texts, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { }
    }

    public static IReadOnlyList<LanguageChoice> AvailableLanguages()
    {
        EnsureLanguageFiles();
        var result = new Dictionary<string, LanguageChoice>(StringComparer.Ordinal) { ["zh-CN"] = new("zh-CN", "简体中文"), ["en-US"] = new("en-US", "English") };
        try
        {
            foreach(string file in Directory.EnumerateFiles(LanguageDirectory, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if(!StudioSettingsStore.IsLanguageId(id) || LoadLanguage(id) is not { } strings) continue;
                result[id] = new(id, strings.GetValueOrDefault("$name", id));
            }
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { }
        return result.Values.OrderBy(value => value.Id is "zh-CN" ? 0 : value.Id is "en-US" ? 1 : 2).ThenBy(value => value.DisplayName, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, string>? LoadLanguage(string id)
    {
        if(!StudioSettingsStore.IsLanguageId(id)) return null;
        try
        {
            string path = Path.Combine(LanguageDirectory, id + ".json");
            if(!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), new JsonSerializerOptions { MaxDepth = 8 });
            return values?.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

public sealed record LanguageChoice(string Id, string DisplayName);

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.LiveBinding(() => UiText.Get(text)).ProvideValue(serviceProvider);
}

internal sealed class LanguageNotifications : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Language"));
}

internal sealed class LiveText : INotifyPropertyChanged
{
    private readonly Func<string> text;
    public string Value { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal LiveText(Func<string> text)
    {
        this.text = text;
        Value = text();
        PropertyChangedEventManager.AddHandler(UiText.Notifications, Refresh, "Language");
    }
    private void Refresh(object? sender, PropertyChangedEventArgs e)
    {
        Value = text();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }
}

internal static class UiMessageBox
{
    internal static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        MessageBox.Show(UiText.Get(message), UiText.Get(caption), buttons, image, defaultResult);
    internal static MessageBoxResult Show(Window owner, string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        MessageBox.Show(owner, UiText.Get(message), UiText.Get(caption), buttons, image, defaultResult);
}
