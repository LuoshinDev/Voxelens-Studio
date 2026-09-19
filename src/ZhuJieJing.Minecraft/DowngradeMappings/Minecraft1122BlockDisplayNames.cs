using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Stable Simplified Chinese display names for Java block identifiers, including selectable 1.12.2 targets
/// and modern source blocks. The language snapshot is embedded so the editor never depends on launcher assets.
/// </summary>
public static class Minecraft1122BlockDisplayNames
{
    // Mojang Java Edition 1.21.10 asset-index 27 object; SHA-1 is its official content address.
    private const string ResourceName =
        "ZhuJieJing.Minecraft.Resources.minecraft-lang-zh_cn-1.21.10.json";
    private const string ExpectedResourceSha1 =
        "5E4F3E0B66B9DD484307F949AFC211CFF3C496E3";
    private const int ExpectedBlockTranslationCount = 1975;
    private const string MinecraftNamespacePrefix = "minecraft:";
    private const string BlockTranslationPrefix = "block.minecraft.";

    private static readonly IReadOnlyDictionary<string, string> LegacyTranslationKeys =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["minecraft:grass_path"] = "block.minecraft.dirt_path",
                ["minecraft:sign"] = "block.minecraft.oak_sign",
                ["minecraft:wall_sign"] = "block.minecraft.oak_wall_sign",
                ["minecraft:white_wall_banner"] = "block.minecraft.white_banner",
            });

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Translations = new(
        LoadAndValidate,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string GetDisplayName(string canonicalBlockName)
    {
        if(string.IsNullOrWhiteSpace(canonicalBlockName))
            throw new ArgumentException("Canonical block name cannot be empty.", nameof(canonicalBlockName));

        if(TryGetDisplayName(canonicalBlockName, out string displayName)) return displayName;

        throw new InvalidDataException(
            $"Embedded zh_cn snapshot has no display name for Java block {canonicalBlockName}.");
    }

    /// <summary>
    /// Resolves an exact vanilla Java block identifier through the bundled Simplified Chinese language snapshot.
    /// Unknown or modded identifiers deliberately return false instead of inventing a translated name.
    /// </summary>
    public static bool TryGetDisplayName(string canonicalBlockName, out string displayName)
    {
        if(string.IsNullOrWhiteSpace(canonicalBlockName))
        {
            displayName = string.Empty;
            return false;
        }

        int propertiesStart = canonicalBlockName.IndexOf('[', StringComparison.Ordinal);
        string blockName = propertiesStart < 0 ? canonicalBlockName : canonicalBlockName[..propertiesStart];

        string translationKey;
        if(!LegacyTranslationKeys.TryGetValue(blockName, out translationKey!))
        {
            if(!blockName.StartsWith(MinecraftNamespacePrefix, StringComparison.Ordinal) ||
               blockName.Length == MinecraftNamespacePrefix.Length ||
               blockName.Contains('*', StringComparison.Ordinal))
            {
                displayName = string.Empty;
                return false;
            }
            translationKey = BlockTranslationPrefix + blockName[MinecraftNamespacePrefix.Length..];
        }

        if(!Translations.Value.TryGetValue(translationKey, out string? translated) ||
           string.IsNullOrWhiteSpace(translated))
        {
            displayName = string.Empty;
            return false;
        }
        displayName = translated;
        return true;
    }

    private static IReadOnlyDictionary<string, string> LoadAndValidate()
    {
        Assembly assembly = typeof(Minecraft1122BlockDisplayNames).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName) ??
                              throw new InvalidOperationException(
                                  $"Embedded Minecraft zh_cn snapshot is missing: {ResourceName}");
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] bytes = copy.ToArray();
        string hash = Convert.ToHexString(SHA1.HashData(bytes));
        if(!hash.Equals(ExpectedResourceSha1, StringComparison.Ordinal))
            throw new InvalidDataException($"Embedded Minecraft zh_cn snapshot failed its integrity check: {hash}");

        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
        if(document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Embedded Minecraft zh_cn snapshot root must be an object.");

        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach(JsonProperty property in document.RootElement.EnumerateObject())
        {
            if(!property.Name.StartsWith(BlockTranslationPrefix, StringComparison.Ordinal)) continue;
            string displayName = property.Value.GetString() ??
                                 throw new InvalidDataException(
                                     $"Minecraft zh_cn entry {property.Name} is not a string.");
            if(string.IsNullOrWhiteSpace(displayName) || !names.TryAdd(property.Name, displayName))
                throw new InvalidDataException($"Invalid or duplicate Minecraft zh_cn entry: {property.Name}");
        }

        if(names.Count != ExpectedBlockTranslationCount)
        {
            throw new InvalidDataException(
                $"Embedded Minecraft zh_cn block entry count changed: {names.Count}.");
        }
        return new ReadOnlyDictionary<string, string>(names);
    }
}
