using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ZhuJieJing.App.Preferences;

internal enum MapFileKind { WorldFolder, Schematic, Blueprint, AiProject, ComponentCandidate }

/// <summary>Separate last-open locations per format, shared by all Studio windows and saved immediately.</summary>
internal sealed class FileDialogLocations
{
    public static FileDialogLocations Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "筑界镜 Studio", "open-locations.json"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string filePath;
    private readonly object gate = new();
    private Dictionary<MapFileKind, string>? locations;

    internal FileDialogLocations(string filePath) => this.filePath = Path.GetFullPath(filePath);

    public void Configure(CommonItemDialog dialog, MapFileKind kind, string? fallback = null)
    {
        // Windows also keeps dialog history by GUID; never share the shell's default bucket across formats.
        dialog.ClientGuid = kind switch
        {
            MapFileKind.WorldFolder => new("287f0861-f178-44bc-841a-e139f2b59b31"),
            MapFileKind.Schematic => new("989be0b9-11ed-46e6-9316-e79d910b5bd0"),
            MapFileKind.Blueprint => new("a4d3466e-f535-45ae-b7bf-34832dddad25"),
            MapFileKind.AiProject => new("afaf0bc6-97ea-42b7-ac27-ebc8f2ff7567"),
            MapFileKind.ComponentCandidate => new("2cf9057b-c53b-48e3-862b-511534617dd3"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        string defaultDirectory = ExistingDirectory(fallback) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        dialog.DefaultDirectory = defaultDirectory;
        lock(gate)
        {
            EnsureLoaded();
            if(locations!.TryGetValue(kind, out string? previous))
                dialog.InitialDirectory = ExistingDirectory(previous) ?? defaultDirectory;
            else if(fallback is not null)
                dialog.InitialDirectory = defaultDirectory;
        }
    }

    public void RememberFile(MapFileKind kind, string file) => RememberDirectory(kind, Path.GetDirectoryName(Path.GetFullPath(file))!);

    public void RememberDirectory(MapFileKind kind, string directory)
    {
        string? existing = ExistingDirectory(directory);
        if(existing is null) return;
        lock(gate)
        {
            EnsureLoaded();
            locations![kind] = existing;
            string temporary = filePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                using(var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, locations, JsonOptions);
                    stream.Flush(true);
                }
                File.Move(temporary, filePath, overwrite: true);
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { }
            finally
            {
                try { if(File.Exists(temporary)) File.Delete(temporary); }
                catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void EnsureLoaded()
    {
        if(locations is not null) return;
        try
        {
            if(File.Exists(filePath) && new FileInfo(filePath).Length <= 64 * 1024)
            {
                using var stream = File.OpenRead(filePath);
                locations = JsonSerializer.Deserialize<Dictionary<MapFileKind, string>>(stream, JsonOptions);
            }
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException) { }
        locations ??= [];
    }

    private static string? ExistingDirectory(string? path)
    {
        if(string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            // A deleted last-used folder falls back to its closest surviving parent.
            for(string? directory = Path.GetFullPath(path); directory is not null; directory = Path.GetDirectoryName(directory))
                if(Directory.Exists(directory)) return directory;
        }
        catch(Exception exception) when(exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { }
        return null;
    }
}
