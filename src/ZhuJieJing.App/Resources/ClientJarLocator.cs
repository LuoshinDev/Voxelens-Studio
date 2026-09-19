using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.Resources;

public enum ClientJarLocationSource
{
    WorldInstallation,
    RoamingProfile,
}

public enum ClientJarVersionMatch
{
    ExactVersion,
    ProfileMetadata,
    EmbeddedVersion,
    DataVersion,
}

public sealed record ClientJarLocation(
    string Path,
    ClientJarLocationSource Source,
    string MatchedVersion,
    ClientJarVersionMatch VersionMatch);

/// <summary>Finds a resource-bearing client JAR matching the opened world's own version evidence.</summary>
public static class ClientJarLocator
{
    private const int MaximumVersionNameLength = 128;
    private const long MaximumProfileMetadataBytes = 8 * 1024 * 1024;
    private const long MaximumEmbeddedVersionBytes = 64 * 1024;
    private const string StoneBlockState = "assets/minecraft/blockstates/stone.json";
    private const string LegacyStoneTexture = "assets/minecraft/textures/blocks/stone.png";
    private const string ModernStoneTexture = "assets/minecraft/textures/block/stone.png";

    /// <param name="worldRootPath">Minecraft 世界文件夹。</param>
    /// <param name="versionName"><c>level.dat</c> 中的 <c>Data.Version.Name</c>。</param>
    /// <param name="roamingApplicationDataDirectory">回退用的 <c>%APPDATA%</c> 目录；方法会自行追加 <c>.minecraft</c>。</param>
    public static ClientJarLocation? TryLocate(
        string worldRootPath,
        string? versionName,
        string? roamingApplicationDataDirectory = null)
    {
        if(!TryNormalizeVersionName(versionName, out string? normalizedVersion)) return null;
        return TryLocateCore(worldRootPath, new VersionEvidence(normalizedVersion, null), roamingApplicationDataDirectory);
    }

    /// <summary>Uses both the human-readable version and DataVersion, allowing modern renamed profiles to match safely.</summary>
    public static ClientJarLocation? TryLocate(
        string worldRootPath,
        MinecraftVersionDescriptor worldVersion,
        string? roamingApplicationDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(worldVersion);
        TryNormalizeVersionName(worldVersion.VersionName, out string? normalizedVersion);
        if(normalizedVersion is null && worldVersion.DataVersion is null) return null;
        return TryLocateCore(
            worldRootPath,
            new VersionEvidence(normalizedVersion, worldVersion.DataVersion),
            roamingApplicationDataDirectory);
    }

    private static ClientJarLocation? TryLocateCore(
        string worldRootPath,
        VersionEvidence version,
        string? roamingApplicationDataDirectory)
    {
        if(string.IsNullOrWhiteSpace(worldRootPath)) throw new ArgumentException("世界路径不能为空。", nameof(worldRootPath));
        string fullWorldPath = Path.GetFullPath(worldRootPath);
        foreach(InstallationCandidate installation in InstallationCandidates(fullWorldPath, roamingApplicationDataDirectory))
        {
            ClientJarLocation? result = TryLocateInInstallation(installation, fullWorldPath, version);
            if(result is not null) return result;
        }
        return null;
    }

    private static ClientJarLocation? TryLocateInInstallation(
        InstallationCandidate installation,
        string fullWorldPath,
        VersionEvidence version)
    {
        string versionsRoot = Path.GetFullPath(Path.Combine(installation.Root, "versions"));
        if(!Directory.Exists(versionsRoot)) return null;

        if(version.VersionName is not null)
        {
            string exactDirectory = Path.GetFullPath(Path.Combine(versionsRoot, version.VersionName));
            if(IsDescendant(versionsRoot, exactDirectory))
            {
                string exactJar = Path.Combine(exactDirectory, $"{version.VersionName}.jar");
                if(TryInspectResourceJar(exactJar, out _))
                {
                    return Location(exactJar, installation.Source, version.VersionName, ClientJarVersionMatch.ExactVersion);
                }
            }
        }

        string? owningProfile = TryGetOwningProfileId(versionsRoot, fullWorldPath);
        DirectoryInfo[] directories = EnumerateVersionDirectories(versionsRoot)
            .OrderBy(directory => string.Equals(directory.Name, owningProfile, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach(DirectoryInfo directory in directories)
        {
            VersionProfile? profile = TryReadProfile(directory);
            if(profile is null || !ProfileMatches(profile, version)) continue;
            ClientJarLocation? located = TryResolveProfileJar(
                versionsRoot,
                directory,
                profile,
                installation.Source,
                ClientJarVersionMatch.ProfileMetadata);
            if(located is not null) return located;
        }

        foreach(DirectoryInfo directory in directories)
        {
            string jarPath = Path.Combine(directory.FullName, $"{directory.Name}.jar");
            if(!TryInspectResourceJar(jarPath, out EmbeddedVersion? embedded) || embedded is null) continue;
            ClientJarVersionMatch? match = EmbeddedMatch(embedded, version);
            if(match is null) continue;
            string matchedVersion = embedded.Id ?? embedded.Name ?? directory.Name;
            return Location(jarPath, installation.Source, matchedVersion, match.Value);
        }
        return null;
    }

    private static ClientJarLocation? TryResolveProfileJar(
        string versionsRoot,
        DirectoryInfo profileDirectory,
        VersionProfile profile,
        ClientJarLocationSource source,
        ClientJarVersionMatch match)
    {
        string ownJar = Path.Combine(profileDirectory.FullName, $"{profileDirectory.Name}.jar");
        if(TryInspectResourceJar(ownJar, out _))
        {
            return Location(ownJar, source, profile.Id ?? profileDirectory.Name, match);
        }

        foreach(string referencedVersion in new[] { profile.Jar, profile.InheritsFrom }.OfType<string>())
        {
            if(!TryNormalizeVersionName(referencedVersion, out string? safeReference)) continue;
            string directory = Path.GetFullPath(Path.Combine(versionsRoot, safeReference!));
            if(!IsDescendant(versionsRoot, directory)) continue;
            string referencedJar = Path.Combine(directory, $"{safeReference}.jar");
            if(TryInspectResourceJar(referencedJar, out _))
            {
                return Location(referencedJar, source, safeReference!, match);
            }
        }
        return null;
    }

    private static ClientJarLocation Location(
        string path,
        ClientJarLocationSource source,
        string matchedVersion,
        ClientJarVersionMatch match) =>
        new(Path.GetFullPath(path), source, matchedVersion, match);

    private static IEnumerable<InstallationCandidate> InstallationCandidates(
        string fullWorldPath,
        string? roamingApplicationDataDirectory)
    {
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo? current = new(fullWorldPath);
        while(current is not null)
        {
            if(string.Equals(current.Name, ".minecraft", StringComparison.OrdinalIgnoreCase) && seenRoots.Add(current.FullName))
            {
                yield return new InstallationCandidate(current.FullName, ClientJarLocationSource.WorldInstallation);
                break;
            }
            current = current.Parent;
        }

        string? roamingPath = roamingApplicationDataDirectory ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if(string.IsNullOrWhiteSpace(roamingPath)) yield break;
        string roamingMinecraft = Path.GetFullPath(Path.Combine(roamingPath, ".minecraft"));
        if(seenRoots.Add(roamingMinecraft))
        {
            yield return new InstallationCandidate(roamingMinecraft, ClientJarLocationSource.RoamingProfile);
        }
    }

    private static IEnumerable<DirectoryInfo> EnumerateVersionDirectories(string versionsRoot)
    {
        try
        {
            return new DirectoryInfo(versionsRoot).EnumerateDirectories().ToArray();
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryGetOwningProfileId(string versionsRoot, string worldPath)
    {
        string relative = Path.GetRelativePath(versionsRoot, worldPath);
        if(Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return null;
        }
        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        return separator <= 0 ? null : relative[..separator];
    }

    private static VersionProfile? TryReadProfile(DirectoryInfo directory)
    {
        string path = Path.Combine(directory.FullName, $"{directory.Name}.json");
        try
        {
            var info = new FileInfo(path);
            if(!info.Exists || info.Length <= 0 || info.Length > MaximumProfileMetadataBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            return new VersionProfile(
                GetString(root, "id"),
                GetString(root, "inheritsFrom"),
                GetString(root, "jar"),
                GetNestedString(root, "downloads", "client", "url"));
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool ProfileMatches(VersionProfile profile, VersionEvidence version)
    {
        if(version.VersionName is null) return false;
        if(EqualsVersion(profile.Id, version.VersionName) ||
           EqualsVersion(profile.InheritsFrom, version.VersionName) ||
           EqualsVersion(profile.Jar, version.VersionName))
        {
            return true;
        }
        if(!Uri.TryCreate(profile.ClientDownloadUrl, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Segments.Any(segment => EqualsVersion(Uri.UnescapeDataString(segment.Trim('/')), version.VersionName));
    }

    private static ClientJarVersionMatch? EmbeddedMatch(EmbeddedVersion embedded, VersionEvidence version)
    {
        if(version.VersionName is not null &&
           (EqualsVersion(embedded.Id, version.VersionName) || EqualsVersion(embedded.Name, version.VersionName)))
        {
            return ClientJarVersionMatch.EmbeddedVersion;
        }
        if(version.DataVersion is not null && embedded.DataVersion == version.DataVersion)
        {
            return ClientJarVersionMatch.DataVersion;
        }
        return null;
    }

    private static bool TryInspectResourceJar(string path, out EmbeddedVersion? embedded)
    {
        embedded = null;
        try
        {
            if(!File.Exists(path)) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            if(archive.GetEntry(StoneBlockState) is null ||
               archive.GetEntry(LegacyStoneTexture) is null && archive.GetEntry(ModernStoneTexture) is null)
            {
                return false;
            }

            ZipArchiveEntry? versionEntry = archive.GetEntry("version.json");
            if(versionEntry is null || versionEntry.Length <= 0 || versionEntry.Length > MaximumEmbeddedVersionBytes) return true;
            try
            {
                using Stream stream = versionEntry.Open();
                using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = document.RootElement;
                embedded = new EmbeddedVersion(
                    GetString(root, "id"),
                    GetString(root, "name"),
                    GetInt32(root, "world_version"));
            }
            catch(JsonException)
            {
                embedded = null;
            }
            return true;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
        {
            embedded = null;
            return false;
        }
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetNestedString(JsonElement root, string first, string second, string third)
    {
        if(root.ValueKind != JsonValueKind.Object ||
           !root.TryGetProperty(first, out JsonElement firstValue) || firstValue.ValueKind != JsonValueKind.Object ||
           !firstValue.TryGetProperty(second, out JsonElement secondValue) || secondValue.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return GetString(secondValue, third);
    }

    private static int? GetInt32(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out int value)
            ? value
            : null;

    private static bool EqualsVersion(string? candidate, string expected) =>
        string.Equals(candidate?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeVersionName(string? versionName, out string? normalized)
    {
        normalized = versionName?.Trim();
        if(string.IsNullOrEmpty(normalized) || normalized.Length > MaximumVersionNameLength || normalized is "." or "..")
        {
            normalized = null;
            return false;
        }
        if(normalized.EndsWith(".", StringComparison.Ordinal) ||
           !string.Equals(Path.GetFileName(normalized), normalized, StringComparison.Ordinal) ||
           normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            normalized = null;
            return false;
        }
        return true;
    }

    private static bool IsDescendant(string root, string candidate)
    {
        string rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct VersionEvidence(string? VersionName, int? DataVersion);

    private readonly record struct InstallationCandidate(string Root, ClientJarLocationSource Source);

    private sealed record VersionProfile(string? Id, string? InheritsFrom, string? Jar, string? ClientDownloadUrl);

    private sealed record EmbeddedVersion(string? Id, string? Name, int? DataVersion);
}
