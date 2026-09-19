using System.IO;
using System.Text.Json;

namespace ZhuJieJing.App.Preferences;

public sealed record ResourceJarPreference(string SourcePath, string CachedPath, string Version, int? DataVersion);

public sealed record StudioSettings
{
    public string Language { get; init; } = "zh-CN";
    public ResourceJarPreference? LegacyJar { get; init; }
    public ResourceJarPreference? ModernJar { get; init; }
}

/// <summary>Application settings are separate from frequently saved camera/window preferences.</summary>
public static class StudioSettingsStore
{
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "筑界镜 Studio", "studio-settings.json");

    public static StudioSettings Load()
    {
        try
        {
            if(!File.Exists(FilePath)) return new();
            var settings = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(FilePath)) ?? new();
            return settings with { Language = IsLanguageId(settings.Language) ? settings.Language : "zh-CN" };
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public static void Save(StudioSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using(var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { if(File.Exists(temporary)) File.Delete(temporary); }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static bool IsLanguageId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
