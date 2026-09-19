using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.Resources;

public enum MinecraftResourceCompatibilityFamily
{
    Legacy1122,
    Modern,
}

public enum MinecraftResourceSelectionMatch
{
    Exact,
    Compatible,
    Unavailable,
}

public enum MinecraftResourceSelectionSource
{
    WorldInstallation,
    RoamingProfile,
    Cache,
    Bundled,
    Manual,
}

public sealed record MinecraftResourceSelectionResult(
    string? ClientJarPath,
    MinecraftResourceSelectionMatch Match,
    MinecraftResourceCompatibilityFamily? Family,
    string? SelectedVersion,
    MinecraftResourceSelectionSource? Source,
    bool ReusedCachedFile,
    string Status)
{
    public bool IsAvailable => ClientJarPath is not null;
}

/// <summary>
/// Resolves a local vanilla client JAR and copies it into an application-owned, content-addressed cache.
/// Source installations are opened read-only and this service never downloads resources.
/// </summary>
public sealed partial class MinecraftResourceCacheService
{
    private const string CacheOwnerDirectoryName = "筑界镜 Studio";
    private const string BundledResourceDirectoryName = "resources";
    private const string BundledMinecraftDirectoryName = "minecraft";
    private const int CacheMetadataVersion = 1;
    private const long MaximumMetadataBytes = 64 * 1024;
    private const long Legacy1122DataVersion = 1343;
    private const string StoneBlockState = "assets/minecraft/blockstates/stone.json";
    private const string LegacyStoneTexture = "assets/minecraft/textures/blocks/stone.png";
    private const string ModernStoneTexture = "assets/minecraft/textures/block/stone.png";
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private readonly bool allowExplicitBundledResources;

    private static readonly BundledResourceDefinition[] BundledResources =
    [
        new("legacy-1.12.2.jar", "1.12.2", 1343),
        new("modern-1.21.10.jar", "1.21.10", 4556),
    ];

    public MinecraftResourceCacheService(string? localApplicationDataDirectory = null, string? bundledResourceDirectory = null)
    {
        allowExplicitBundledResources = bundledResourceDirectory is not null;
        string localData = localApplicationDataDirectory ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if(string.IsNullOrWhiteSpace(localData))
            throw new InvalidOperationException("无法定位本地应用数据目录。");
        CacheDirectory = Path.GetFullPath(Path.Combine(localData, CacheOwnerDirectoryName, "resource-cache"));
        BundledResourceDirectory = Path.GetFullPath(bundledResourceDirectory ?? Path.Combine(
            AppContext.BaseDirectory,
            BundledResourceDirectoryName,
            BundledMinecraftDirectoryName));
    }

    public string CacheDirectory { get; }

    public string BundledResourceDirectory { get; }

    public async ValueTask<MinecraftResourceSelectionResult> ResolveAsync(
        string worldRootPath,
        MinecraftVersionDescriptor worldVersion,
        string? roamingApplicationDataDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldRootPath);
        ArgumentNullException.ThrowIfNull(worldVersion);
        cancellationToken.ThrowIfCancellationRequested();

        if(!TryDescribeTarget(worldVersion, out ResourceTarget target))
        {
            return Unavailable(null, $"无法判断 {FormatWorldVersion(worldVersion)} 的材质兼容族；已使用纯色方块。");
        }

        // A cold cache makes the first async file read complete synchronously. Keep the following installation
        // enumeration and ZIP inspection behind an explicit worker boundary so a WPF caller never inherits them.
        return await Task.Run(
                () => ResolveCoreAsync(
                    worldRootPath,
                    worldVersion,
                    roamingApplicationDataDirectory,
                    target,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MinecraftResourceSelectionResult> ResolveCoreAsync(
        string worldRootPath,
        MinecraftVersionDescriptor worldVersion,
        string? roamingApplicationDataDirectory,
        ResourceTarget target,
        CancellationToken cancellationToken,
        bool useConfigured = true)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MinecraftResourceSelectionResult? configured = useConfigured ? await ResolveConfiguredAsync(target, cancellationToken).ConfigureAwait(false) : null;
        if(configured is not null) return configured;

        IReadOnlyList<CachedCandidate> cached = await ReadCachedCandidatesAsync(target.Family, cancellationToken)
            .ConfigureAwait(false);
        CachedCandidate? cachedExact = cached.FirstOrDefault(candidate => IsExact(candidate.Version, candidate.DataVersion, target));
        if(cachedExact is not null)
        {
            return new MinecraftResourceSelectionResult(
                cachedExact.Path,
                MinecraftResourceSelectionMatch.Exact,
                target.Family,
                cachedExact.Version,
                MinecraftResourceSelectionSource.Cache,
                true,
                UiText.Format($"已自动使用缓存的 Minecraft {cachedExact.Version} 原版材质。"));
        }

        ClientJarLocation? exact = ClientJarLocator.TryLocate(
            worldRootPath,
            worldVersion,
            roamingApplicationDataDirectory);
        if(exact is not null)
        {
            string selectedVersion = target.Version?.ToString() ?? exact.MatchedVersion;
            CacheWriteResult stored = await CacheAsync(
                exact.Path,
                target.Family,
                selectedVersion,
                worldVersion.DataVersion,
                MapSource(exact.Source),
                cancellationToken).ConfigureAwait(false);
            return new MinecraftResourceSelectionResult(
                stored.Path,
                MinecraftResourceSelectionMatch.Exact,
                target.Family,
                selectedVersion,
                MapSource(exact.Source),
                stored.Reused,
                stored.Reused
                    ? UiText.Format($"已自动使用缓存的 Minecraft {selectedVersion} 原版材质。")
                    : UiText.Format($"已自动缓存并使用 Minecraft {selectedVersion} 原版材质。"));
        }

        ResourceCandidate[] bundled = FindBundledCandidates()
            .Where(candidate => IsCompatible(candidate.Release, candidate.DataVersion, target.Family))
            .ToArray();
        ResourceCandidate? bundledExact = bundled.FirstOrDefault(candidate =>
            IsExact(candidate.Version, candidate.DataVersion, target));
        if(bundledExact is not null)
        {
            CacheWriteResult stored = await CacheAsync(
                bundledExact.Path,
                target.Family,
                bundledExact.Version,
                bundledExact.DataVersion,
                MinecraftResourceSelectionSource.Bundled,
                cancellationToken).ConfigureAwait(false);
            return new MinecraftResourceSelectionResult(
                stored.Path,
                MinecraftResourceSelectionMatch.Exact,
                target.Family,
                bundledExact.Version,
                MinecraftResourceSelectionSource.Bundled,
                stored.Reused,
                UiText.Format($"已自动使用内置 Minecraft {bundledExact.Version} 原版材质。"));
        }

        List<ResourceCandidate> compatible = cached
            .Select(candidate => new ResourceCandidate(
                candidate.Path,
                candidate.Version,
                candidate.Release,
                candidate.DataVersion,
                MinecraftResourceSelectionSource.Cache))
            .Concat(FindInstalledCandidates(worldRootPath, roamingApplicationDataDirectory))
            .Where(candidate => IsCompatible(candidate.Release, candidate.DataVersion, target.Family))
            .ToList();
        ResourceCandidate? nearest = SelectNearest(compatible, target);
        nearest ??= SelectNearest(bundled, target);
        if(nearest is null)
        {
            return Unavailable(
                target.Family,
                $"未找到与 {FormatWorldVersion(worldVersion)} 同兼容族的本地客户端 JAR；已使用纯色方块。");
        }

        if(nearest.Source == MinecraftResourceSelectionSource.Cache)
        {
            return new MinecraftResourceSelectionResult(
                nearest.Path,
                MinecraftResourceSelectionMatch.Compatible,
                target.Family,
                nearest.Version,
                nearest.Source,
                true,
                UiText.Format($"未找到 {FormatWorldVersion(worldVersion)} 精确材质；已自动使用缓存的同兼容族 Minecraft {nearest.Version} 原版材质。"));
        }

        CacheWriteResult compatibleStored = await CacheAsync(
            nearest.Path,
            target.Family,
            nearest.Version,
            nearest.DataVersion,
            nearest.Source,
            cancellationToken).ConfigureAwait(false);
        return new MinecraftResourceSelectionResult(
            compatibleStored.Path,
            MinecraftResourceSelectionMatch.Compatible,
            target.Family,
            nearest.Version,
            nearest.Source,
            compatibleStored.Reused,
            nearest.Source == MinecraftResourceSelectionSource.Bundled
                ? UiText.Format($"未找到 {FormatWorldVersion(worldVersion)} 精确材质；已自动使用内置 Minecraft {nearest.Version} 同族兼容材质。")
                : UiText.Format($"未找到 {FormatWorldVersion(worldVersion)} 精确材质；已自动选择同兼容族中最接近的 Minecraft {nearest.Version} 原版材质。"));
    }

    private async ValueTask<CacheWriteResult> CacheAsync(
        string sourcePath,
        MinecraftResourceCompatibilityFamily family,
        string version,
        int? dataVersion,
        MinecraftResourceSelectionSource source,
        CancellationToken cancellationToken)
    {
        await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryJar = null;
        string? temporaryMetadata = null;
        try
        {
            string familyDirectory = Path.Combine(CacheDirectory, family.ToString());
            Directory.CreateDirectory(familyDirectory);
            temporaryJar = Path.Combine(familyDirectory, $".{Guid.NewGuid():N}.jar.tmp");
            string hash;
            await using(FileStream input = new(
                            Path.GetFullPath(sourcePath),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            128 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using(FileStream output = new(
                            temporaryJar,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            128 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan))
            using(IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
                int read;
                while((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if(!TryInspectResourceJar(temporaryJar, out _, out _))
                throw new InvalidDataException("复制后的客户端 JAR 不包含可用的 Minecraft 方块材质。");

            string destinationJar = Path.Combine(familyDirectory, $"{hash}.jar");
            bool reused = File.Exists(destinationJar) && await HashMatchesAsync(destinationJar, hash, cancellationToken)
                .ConfigureAwait(false);
            if(reused)
            {
                File.Delete(temporaryJar);
                temporaryJar = null;
            }
            else
            {
                File.Move(temporaryJar, destinationJar, overwrite: true);
                temporaryJar = null;
            }

            CacheMetadata metadata = new(
                CacheMetadataVersion,
                hash,
                family,
                version,
                dataVersion,
                source);
            string metadataPath = Path.Combine(familyDirectory, $"{hash}.json");
            temporaryMetadata = Path.Combine(familyDirectory, $".{Guid.NewGuid():N}.json.tmp");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(metadata);
            await using(FileStream stream = new(
                            temporaryMetadata,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            4096,
                            FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryMetadata, metadataPath, overwrite: true);
            temporaryMetadata = null;
            return new CacheWriteResult(destinationJar, reused);
        }
        finally
        {
            if(temporaryJar is not null) TryDelete(temporaryJar);
            if(temporaryMetadata is not null) TryDelete(temporaryMetadata);
            cacheGate.Release();
        }
    }

    private async Task<IReadOnlyList<CachedCandidate>> ReadCachedCandidatesAsync(
        MinecraftResourceCompatibilityFamily family,
        CancellationToken cancellationToken)
    {
        var results = new List<CachedCandidate>();
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string directory = Path.Combine(CacheDirectory, family.ToString());
        if(!Directory.Exists(directory)) return results;
        string[] metadataPaths;
        try
        {
            metadataPaths = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
            return results;
        }
        foreach(string metadataPath in metadataPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CacheMetadata? metadata = TryReadMetadata(metadataPath);
            if(metadata is null || metadata.FormatVersion != CacheMetadataVersion || metadata.Family != family ||
               !IsSha256(metadata.Sha256) || seenHashes.Contains(metadata.Sha256)) continue;
            string jarPath = Path.Combine(directory, $"{metadata.Sha256}.jar");
            if(!File.Exists(jarPath) || !TryInspectResourceJar(jarPath, out _, out _) ||
               !await HashMatchesAsync(jarPath, metadata.Sha256, cancellationToken).ConfigureAwait(false)) continue;
            seenHashes.Add(metadata.Sha256);
            TryParseRelease(metadata.Version, out JavaRelease? release);
            results.Add(new CachedCandidate(jarPath, metadata.Version, release, metadata.DataVersion));
        }
        return results;
    }

    private static IEnumerable<ResourceCandidate> FindInstalledCandidates(
        string worldRootPath,
        string? roamingApplicationDataDirectory)
    {
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenJars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo? current = new(Path.GetFullPath(worldRootPath));
        while(current is not null)
        {
            if(current.Name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            {
                seenRoots.Add(current.FullName);
                foreach(ResourceCandidate candidate in EnumerateInstallation(current.FullName, MinecraftResourceSelectionSource.WorldInstallation, seenJars))
                    yield return candidate;
                break;
            }
            current = current.Parent;
        }

        string roaming = roamingApplicationDataDirectory ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if(string.IsNullOrWhiteSpace(roaming)) yield break;
        string roamingMinecraft = Path.GetFullPath(Path.Combine(roaming, ".minecraft"));
        if(!seenRoots.Add(roamingMinecraft)) yield break;
        foreach(ResourceCandidate candidate in EnumerateInstallation(roamingMinecraft, MinecraftResourceSelectionSource.RoamingProfile, seenJars))
            yield return candidate;
    }

    private IEnumerable<ResourceCandidate> FindBundledCandidates()
    {
        if(!allowExplicitBundledResources) yield break;
        foreach(BundledResourceDefinition resource in BundledResources)
        {
            string path = Path.Combine(BundledResourceDirectory, resource.FileName);
            if(!TryInspectResourceJar(path, out string? embeddedVersion, out int? embeddedDataVersion)) continue;
            string version = FirstReleaseVersion(embeddedVersion, resource.Version) ?? resource.Version;
            int? dataVersion = embeddedDataVersion ?? resource.DataVersion;
            TryParseRelease(version, out JavaRelease? release);
            yield return new ResourceCandidate(
                Path.GetFullPath(path),
                version,
                release,
                dataVersion,
                MinecraftResourceSelectionSource.Bundled);
        }
    }

    private static IEnumerable<ResourceCandidate> EnumerateInstallation(
        string minecraftRoot,
        MinecraftResourceSelectionSource source,
        ISet<string> seenJars)
    {
        string versionsRoot = Path.Combine(minecraftRoot, "versions");
        DirectoryInfo[] directories;
        try
        {
            directories = Directory.Exists(versionsRoot)
                ? new DirectoryInfo(versionsRoot).EnumerateDirectories().ToArray()
                : [];
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach(DirectoryInfo directory in directories)
        {
            string jarPath = Path.Combine(directory.FullName, $"{directory.Name}.jar");
            string fullJarPath = Path.GetFullPath(jarPath);
            if(!seenJars.Add(fullJarPath) || !TryInspectResourceJar(fullJarPath, out string? embeddedVersion, out int? dataVersion))
                continue;
            string version = FirstReleaseVersion(
                embeddedVersion,
                TryReadProfileBaseVersion(directory),
                directory.Name) ?? embeddedVersion ?? directory.Name;
            TryParseRelease(version, out JavaRelease? release);
            yield return new ResourceCandidate(fullJarPath, version, release, dataVersion, source);
        }
    }

    private static ResourceCandidate? SelectNearest(IEnumerable<ResourceCandidate> candidates, ResourceTarget target)
    {
        return candidates
            .Select(candidate => new { Candidate = candidate, Distance = Distance(candidate, target) })
            .Where(item => item.Distance is not null)
            .OrderBy(item => item.Distance)
            .ThenByDescending(item => item.Candidate.Release)
            .ThenBy(item => item.Candidate.Source == MinecraftResourceSelectionSource.Cache ? 0 : 1)
            .Select(item => item.Candidate)
            .FirstOrDefault();
    }

    private static long? Distance(ResourceCandidate candidate, ResourceTarget target)
    {
        if(target.DataVersion is int requestedData && candidate.DataVersion is int availableData)
            return Math.Abs((long)requestedData - availableData);
        if(target.Version is JavaRelease requested && candidate.Release is JavaRelease available)
            return Math.Abs(requested.SortKey - available.SortKey);
        return null;
    }

    private static bool TryDescribeTarget(MinecraftVersionDescriptor version, out ResourceTarget target)
    {
        TryParseRelease(version.VersionName, out JavaRelease? release);
        if((release is { Major: 1, Minor: 12, Patch: 2 }) || version.DataVersion == Legacy1122DataVersion)
        {
            target = new ResourceTarget(MinecraftResourceCompatibilityFamily.Legacy1122, release, version.DataVersion);
            return true;
        }
        if(version.StorageFamily is MinecraftStorageFamily.FlattenedPalette or MinecraftStorageFamily.ModernSectionPalette ||
           release is not null && IsModern(release.Value) || version.DataVersion > Legacy1122DataVersion)
        {
            target = new ResourceTarget(MinecraftResourceCompatibilityFamily.Modern, release, version.DataVersion);
            return release is not null || version.DataVersion is not null;
        }
        target = default;
        return false;
    }

    private static bool IsCompatible(JavaRelease? release, int? dataVersion, MinecraftResourceCompatibilityFamily family) => family switch
    {
        MinecraftResourceCompatibilityFamily.Legacy1122 =>
            release is { Major: 1, Minor: 12, Patch: 2 } || dataVersion == Legacy1122DataVersion,
        MinecraftResourceCompatibilityFamily.Modern =>
            release is JavaRelease found && IsModern(found) || dataVersion > Legacy1122DataVersion,
        _ => false,
    };

    private static bool IsExact(string version, int? dataVersion, ResourceTarget target) =>
        target.Version is JavaRelease requested && TryParseRelease(version, out JavaRelease? available) && requested == available ||
        target.DataVersion is int requestedData && dataVersion == requestedData;

    private static bool IsModern(JavaRelease release) => release.Major > 1 || release is { Major: 1, Minor: >= 13 };

    private static string? FirstReleaseVersion(params string?[] candidates)
    {
        foreach(string? candidate in candidates)
            if(TryParseRelease(candidate, out JavaRelease? release) && release is JavaRelease found) return found.ToString();
        return null;
    }

    private static bool TryParseRelease(string? value, out JavaRelease? release)
    {
        Match match = ReleaseVersionRegex().Match(value?.Trim() ?? string.Empty);
        if(!match.Success || !int.TryParse(match.Groups[1].Value, out int major) ||
           !int.TryParse(match.Groups[2].Value, out int minor))
        {
            release = null;
            return false;
        }
        int patch = 0;
        if(match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out patch))
        {
            release = null;
            return false;
        }
        release = new JavaRelease(major, minor, patch);
        return true;
    }

    private static bool TryInspectResourceJar(string path, out string? version, out int? dataVersion)
    {
        version = null;
        dataVersion = null;
        try
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            if(archive.GetEntry(StoneBlockState) is null ||
               archive.GetEntry(LegacyStoneTexture) is null && archive.GetEntry(ModernStoneTexture) is null) return false;
            ZipArchiveEntry? entry = archive.GetEntry("version.json");
            if(entry is null || entry.Length <= 0 || entry.Length > MaximumMetadataBytes) return true;
            using Stream stream = entry.Open();
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            version = GetString(root, "id") ?? GetString(root, "name");
            if(root.TryGetProperty("world_version", out JsonElement worldVersion) && worldVersion.TryGetInt32(out int found))
                dataVersion = found;
            return true;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
        {
            version = null;
            dataVersion = null;
            return false;
        }
    }

    private static string? TryReadProfileBaseVersion(DirectoryInfo directory)
    {
        string path = Path.Combine(directory.FullName, $"{directory.Name}.json");
        try
        {
            FileInfo info = new(path);
            if(!info.Exists || info.Length <= 0 || info.Length > 8 * 1024 * 1024) return null;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            return GetString(document.RootElement, "inheritsFrom") ?? GetString(document.RootElement, "jar");
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static CacheMetadata? TryReadMetadata(string path)
    {
        try
        {
            FileInfo info = new(path);
            if(!info.Exists || info.Length <= 0 || info.Length > MaximumMetadataBytes) return null;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<CacheMetadata>(stream);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormatWorldVersion(MinecraftVersionDescriptor version) => version.VersionName is { Length: > 0 } name
        ? $"Minecraft {name}"
        : version.DataVersion is int dataVersion
            ? $"DataVersion {dataVersion}"
            : UiText.Get("当前地图");

    private static MinecraftResourceSelectionResult Unavailable(
        MinecraftResourceCompatibilityFamily? family,
        string status) => new(
            null,
            MinecraftResourceSelectionMatch.Unavailable,
            family,
            null,
            null,
            false,
            status);

    private static MinecraftResourceSelectionSource MapSource(ClientJarLocationSource source) => source switch
    {
        ClientJarLocationSource.WorldInstallation => MinecraftResourceSelectionSource.WorldInstallation,
        _ => MinecraftResourceSelectionSource.RoamingProfile,
    };

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?(?:-(?:pre|rc)\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionRegex();

    private readonly record struct ResourceTarget(
        MinecraftResourceCompatibilityFamily Family,
        JavaRelease? Version,
        int? DataVersion);

    private readonly record struct BundledResourceDefinition(string FileName, string Version, int DataVersion);

    private sealed record ResourceCandidate(
        string Path,
        string Version,
        JavaRelease? Release,
        int? DataVersion,
        MinecraftResourceSelectionSource Source);

    private sealed record CachedCandidate(
        string Path,
        string Version,
        JavaRelease? Release,
        int? DataVersion);

    private sealed record CacheMetadata(
        int FormatVersion,
        string Sha256,
        MinecraftResourceCompatibilityFamily Family,
        string Version,
        int? DataVersion,
        MinecraftResourceSelectionSource Source);

    private readonly record struct CacheWriteResult(string Path, bool Reused);

    private readonly record struct JavaRelease(int Major, int Minor, int Patch) : IComparable<JavaRelease>
    {
        public long SortKey => (long)Major * 1_000_000_000 + (long)Minor * 1_000_000 + Patch;

        public int CompareTo(JavaRelease other) => SortKey.CompareTo(other.SortKey);

        public override string ToString() => Patch == 0 ? $"{Major}.{Minor}" : $"{Major}.{Minor}.{Patch}";
    }
}
