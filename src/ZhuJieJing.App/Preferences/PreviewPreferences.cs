using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZhuJieJing.Renderer.Controls;

namespace ZhuJieJing.App.Preferences;

public enum PreviewMaterialMode
{
    Solid,
    Minecraft,
}

/// <summary>Durable preview controls. Transient navigation and pointer-lock state intentionally do not belong here.</summary>
public sealed record PreviewPreferences
{
    public const int CurrentSchemaVersion = 9;
    public const float DefaultZoomDistance = 27f;
    public const float DefaultObserverMouseSensitivity = 1f;
    public const float DefaultKeyboardMovementSpeed = 24f;
    public const float DefaultTimeOfDay = 14f;
    public const float MinimumKeyboardMovementSpeed = 4f;
    public const float MaximumKeyboardMovementSpeed = 128f;
    public const double DefaultWindowWidth = 1480d;
    public const double DefaultWindowHeight = 900d;
    public const double MinimumWindowWidth = 1120d;
    public const double MinimumWindowHeight = 720d;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public float ZoomDistance { get; init; } = DefaultZoomDistance;

    public float ObserverMouseSensitivity { get; init; } = DefaultObserverMouseSensitivity;

    public float KeyboardMovementSpeed { get; init; } = DefaultKeyboardMovementSpeed;

    public float TimeOfDay { get; init; } = DefaultTimeOfDay;

    public int MaximumLoadedChunkCount { get; init; } = WorldPreview.WorldPreviewNavigation.DefaultChunkCount;

    public int ChunkLoadConcurrency { get; init; } = WorldPreview.NormalizedMinecraftChunkBatcher.DefaultLoadConcurrency;

    public bool CloudsEnabled { get; init; } = true;

    public bool RainEnabled { get; init; }

    public bool FogEnabled { get; init; } = true;

    public bool EnhancedLightingEnabled { get; init; }


    public ViewportTerrainMode TerrainMode { get; init; } = ViewportTerrainMode.Chunks;

    public PreviewMaterialMode MaterialMode { get; init; } = PreviewMaterialMode.Minecraft;

    public double WindowWidth { get; init; } = DefaultWindowWidth;

    public double WindowHeight { get; init; } = DefaultWindowHeight;

    public double? WindowLeft { get; init; }

    public double? WindowTop { get; init; }

    public bool WindowMaximized { get; init; }

    public static PreviewPreferences Default { get; } = new();

    public PreviewPreferences Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        ZoomDistance = InRange(ZoomDistance, 3f, 512f) ? ZoomDistance : DefaultZoomDistance,
        ObserverMouseSensitivity = float.IsFinite(ObserverMouseSensitivity)
            ? Math.Clamp(
                ObserverMouseSensitivity,
                VoxelViewport.MinimumObserverMouseSensitivity,
                VoxelViewport.MaximumObserverMouseSensitivity)
            : DefaultObserverMouseSensitivity,
        KeyboardMovementSpeed = InRange(
            KeyboardMovementSpeed,
            MinimumKeyboardMovementSpeed,
            MaximumKeyboardMovementSpeed)
            ? KeyboardMovementSpeed
            : DefaultKeyboardMovementSpeed,
        TimeOfDay = InRange(TimeOfDay, 0f, 24f) ? TimeOfDay : DefaultTimeOfDay,
        MaximumLoadedChunkCount = WorldPreview.WorldPreviewNavigation.NormalizeChunkCount(MaximumLoadedChunkCount),
        ChunkLoadConcurrency = (int)Math.Clamp(
            SchemaVersion switch
            {
                4 => (long)ChunkLoadConcurrency * 6L,
                5 => (long)ChunkLoadConcurrency * 2L,
                _ => ChunkLoadConcurrency,
            },
            WorldPreview.NormalizedMinecraftChunkBatcher.MinimumLoadConcurrency,
            WorldPreview.NormalizedMinecraftChunkBatcher.MaximumLoadConcurrency),
        TerrainMode = Enum.IsDefined(TerrainMode) ? TerrainMode : ViewportTerrainMode.Chunks,
        MaterialMode = Enum.IsDefined(MaterialMode) ? MaterialMode : PreviewMaterialMode.Minecraft,
        WindowWidth = NormalizeWindowLength(WindowWidth, MinimumWindowWidth, DefaultWindowWidth),
        WindowHeight = NormalizeWindowLength(WindowHeight, MinimumWindowHeight, DefaultWindowHeight),
        WindowLeft = NormalizeCoordinate(WindowLeft),
        WindowTop = NormalizeCoordinate(WindowTop),
    };

    private static bool InRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static double NormalizeWindowLength(double value, double minimum, double fallback) =>
        double.IsFinite(value) && value >= minimum ? value : fallback;

    private static double? NormalizeCoordinate(double? value) =>
        value is double coordinate && double.IsFinite(coordinate) ? coordinate : null;
}

/// <summary>Loads and atomically replaces the per-user preview preference file.</summary>
public sealed class PreviewPreferencesStore
{
    private const string SettingsDirectoryName = "筑界镜 Studio";
    private const string SettingsFileName = "preview-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string filePath;

    public PreviewPreferencesStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => filePath;

    public static PreviewPreferencesStore CreateDefault()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new PreviewPreferencesStore(
            Path.Combine(localApplicationData, SettingsDirectoryName, SettingsFileName));
    }

    public PreviewPreferences Load()
    {
        if(TryLoad(filePath, out PreviewPreferences preferences)) return preferences;
        return PreviewPreferences.Default;
    }

    public bool TrySave(PreviewPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string? directory = Path.GetDirectoryName(filePath);
        if(string.IsNullOrEmpty(directory)) return false;
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            using(FileStream stream = new(
                      temporaryPath,
                      FileMode.CreateNew,
                      FileAccess.Write,
                      FileShare.None,
                      4096,
                      FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, preferences.Normalize(), SerializerOptions);
                stream.Flush(true);
            }
            File.Move(temporaryPath, filePath, true);
            return true;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if(File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static bool TryLoad(string path, out PreviewPreferences preferences)
    {
        preferences = PreviewPreferences.Default;
        try
        {
            if(!File.Exists(path)) return false;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            PreviewPreferences? loaded = JsonSerializer.Deserialize<PreviewPreferences>(stream, SerializerOptions);
            if(loaded is null) return false;
            preferences = loaded.Normalize();
            return true;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }
}
