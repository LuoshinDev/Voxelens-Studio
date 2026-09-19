using System.IO;
using System.IO.Compression;
using ZhuJieJing.App.Preferences;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.Resources;

public sealed partial class MinecraftResourceCacheService
{
    public Task<MinecraftResourceSelectionResult> DiscoverAsync(string worldRootPath, MinecraftResourceCompatibilityFamily family, CancellationToken cancellationToken = default)
    {
        MinecraftVersionDescriptor version = family == MinecraftResourceCompatibilityFamily.Legacy1122
            ? MinecraftTargetProfile.Java1122.Version
            : new(4556, "1.21.10", MinecraftStorageFamily.ModernSectionPalette);
        if(!TryDescribeTarget(version, out ResourceTarget target)) throw new InvalidOperationException("Unknown resource family.");
        return Task.Run(() => ResolveCoreAsync(worldRootPath, version, null, target, cancellationToken, useConfigured: false), cancellationToken);
    }

    public Task<ResourceJarPreference> ImportConfiguredJarAsync(string path, MinecraftResourceCompatibilityFamily family, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        string fullPath = Path.GetFullPath(path);
        InspectConfiguredJar(fullPath, family, out string version, out int? dataVersion);
        CacheWriteResult stored = await CacheAsync(fullPath, family, version, dataVersion, MinecraftResourceSelectionSource.Manual, cancellationToken).ConfigureAwait(false);
        InspectConfiguredJar(stored.Path, family, out _, out _);
        return new ResourceJarPreference(fullPath, stored.Path, version, dataVersion);
    }, cancellationToken);

    private static void InspectConfiguredJar(string path, MinecraftResourceCompatibilityFamily family, out string version, out int? dataVersion)
    {
        if(!TryInspectResourceJar(path, out string? embeddedVersion, out dataVersion))
            throw new InvalidDataException("所选 JAR 缺少 Minecraft 方块模型或贴图，请选择客户端资源 JAR。");
        using var archive = ZipFile.OpenRead(path);
        string? detectedVersion = embeddedVersion;
        if(detectedVersion is null && TryParseRelease(Path.GetFileNameWithoutExtension(path), out _))
            detectedVersion = Path.GetFileNameWithoutExtension(path);
        if(TryParseRelease(detectedVersion, out var release) && !IsCompatible(release, dataVersion, family))
            throw new InvalidDataException("资源版本不匹配：1.12.2 和高版本 JAR 需要分别设置。");
        bool legacy = archive.GetEntry(LegacyStoneTexture) is not null;
        bool modern = archive.GetEntry(ModernStoneTexture) is not null;
        if(family == MinecraftResourceCompatibilityFamily.Legacy1122 ? !legacy || modern : !modern || legacy)
            throw new InvalidDataException("资源版本不匹配：1.12.2 和高版本 JAR 需要分别设置。");
        if(dataVersion is not null && (family == MinecraftResourceCompatibilityFamily.Legacy1122 ? dataVersion != Legacy1122DataVersion : dataVersion <= Legacy1122DataVersion))
            throw new InvalidDataException("资源版本不匹配：1.12.2 和高版本 JAR 需要分别设置。");
        version = detectedVersion ?? (family == MinecraftResourceCompatibilityFamily.Legacy1122 ? "1.12.2" : "1.13+");
    }

    private async Task<MinecraftResourceSelectionResult?> ResolveConfiguredAsync(ResourceTarget target, CancellationToken cancellationToken)
    {
        StudioSettings settings = StudioSettingsStore.Load();
        ResourceJarPreference? configured = target.Family == MinecraftResourceCompatibilityFamily.Legacy1122 ? settings.LegacyJar : settings.ModernJar;
        if(configured is null) return null;
        try
        {
            // The selected snapshot remains usable even if the original download is moved or removed.
            string hash = Path.GetFileNameWithoutExtension(configured.CachedPath);
            if(!File.Exists(configured.CachedPath) || !IsSha256(hash) || !await HashMatchesAsync(configured.CachedPath, hash, cancellationToken).ConfigureAwait(false))
                configured = await ImportConfiguredJarAsync(configured.SourcePath, target.Family, cancellationToken).ConfigureAwait(false);
            InspectConfiguredJar(configured.CachedPath, target.Family, out _, out _);
            return new(configured.CachedPath, IsExact(configured.Version, configured.DataVersion, target) ? MinecraftResourceSelectionMatch.Exact : MinecraftResourceSelectionMatch.Compatible,
                target.Family, configured.Version, MinecraftResourceSelectionSource.Manual, true, UiText.Format($"已使用手动设置的 Minecraft {configured.Version} 材质。"));
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unavailable(target.Family, UiText.Format($"手动资源 JAR 无法读取，请在设置中重新选择：{exception.Message}"));
        }
    }
}
