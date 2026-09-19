using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Globalization;
using ZhuJieJing.App.Preferences;

namespace ZhuJieJing.App.Resources;

internal static class MinecraftBlockEnglishNames
{
    private static Lazy<IReadOnlyDictionary<string, string>> names = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Reload() => Interlocked.Exchange(ref names, new(Load, LazyThreadSafetyMode.ExecutionAndPublication));

    public static string GetName(string identifier)
    {
        string key = identifier switch
        {
            "minecraft:grass_path" => "minecraft:dirt_path",
            "minecraft:sign" => "minecraft:oak_sign",
            "minecraft:wall_sign" => "minecraft:oak_wall_sign",
            _ => identifier,
        };
        key = "block." + key.Replace(':', '.');
        var current = Volatile.Read(ref names).Value;
        if(current.TryGetValue(key, out string? name)) return name;
        if(key.Contains("_wall_", StringComparison.Ordinal) && current.TryGetValue(key.Replace("_wall_", "_"), out name)) return name;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(identifier[(identifier.IndexOf(':') + 1)..].Replace('_', ' '));
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        string? path = StudioSettingsStore.Load().ModernJar?.CachedPath;
        if(string.IsNullOrWhiteSpace(path)) return new Dictionary<string, string>();
        try
        {
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("assets/minecraft/lang/en_us.json")?.Open();
            if(stream is null) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch(IOException) { return new Dictionary<string, string>(); }
        catch(UnauthorizedAccessException) { return new Dictionary<string, string>(); }
        catch(JsonException) { return new Dictionary<string, string>(); }
    }
}
