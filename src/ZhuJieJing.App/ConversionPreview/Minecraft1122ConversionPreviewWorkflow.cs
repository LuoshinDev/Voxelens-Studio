using System.IO;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.ConversionPreview;

public sealed record ConversionYOffsetInput(bool IsValid, int? RequestedOffset, string? ErrorMessage);

public static class Minecraft1122ConversionPreviewWorkflow
{
    public static BlockState VisibleFallback { get; } =
        Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(159, 2).State;

    public static bool IsAlreadyTarget(MinecraftVersionDescriptor version) =>
        string.Equals(version.VersionName, MinecraftTargetProfile.Java1122.Version.VersionName, StringComparison.OrdinalIgnoreCase) ||
        version.DataVersion == MinecraftTargetProfile.Java1122.Version.DataVersion &&
        version.StorageFamily == MinecraftStorageFamily.LegacyNumericAnvil;

    public static bool IsHigherThanTarget(MinecraftVersionDescriptor version)
    {
        int targetDataVersion = MinecraftTargetProfile.Java1122.Version.DataVersion!.Value;
        if(version.DataVersion is int dataVersion) return dataVersion > targetDataVersion;
        return version.StorageFamily is MinecraftStorageFamily.FlattenedPalette or MinecraftStorageFamily.ModernSectionPalette;
    }

    public static bool CanUseVerifiedWorldClone(
        MinecraftWorldDescriptor descriptor,
        bool isOriginalWorldScene,
        int? requestedYOffset,
        int skippedCustomDimensionCount)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return isOriginalWorldScene &&
               descriptor.SourceRevisionStrength == MinecraftSourceRevisionStrength.ContentHash &&
               descriptor.Version.DataVersion == MinecraftTargetProfile.Java1122.Version.DataVersion &&
               descriptor.Version.StorageFamily == MinecraftStorageFamily.LegacyNumericAnvil &&
               requestedYOffset.GetValueOrDefault() == 0 &&
               skippedCustomDimensionCount == 0;
    }

    public static ConversionYOffsetInput ParseYOffset(string? text)
    {
        string value = text?.Trim() ?? string.Empty;
        if(value.Length == 0) return new ConversionYOffsetInput(true, null, null);
        if(!int.TryParse(value, out int offset) || offset < 0)
        {
            return new ConversionYOffsetInput(false, null, "Y 上移必须是零或正整数，留空则自动计算。");
        }
        return new ConversionYOffsetInput(true, offset, null);
    }

    public static MinecraftDowngradePreviewRequest CreateRequest(
        INormalizedMinecraftChunkSource source,
        int? requestedOffset,
        IReadOnlyList<MinecraftChunkAddress>? chunkAddresses = null,
        int maximumConcurrency = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new MinecraftDowngradePreviewRequest(
            source,
            MinecraftTargetProfile.Java1122,
            new MinecraftDowngradePolicy(
                VisibleFallback,
                allowVerticalClipping: false,
                preserveLighting: true),
            requestedOffset,
            ChunkAddresses: chunkAddresses,
            MaximumConcurrency: maximumConcurrency);
    }

    public static int SelectConversionConcurrency(int requestedConcurrency) => Math.Clamp(
        requestedConcurrency,
        1,
        Math.Max(2, Environment.ProcessorCount * 2));

    public static string GetMappingRevision(IMinecraftBlockDowngradeRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if(rules is IRevisionedMinecraftBlockDowngradeRules revisioned &&
           !string.IsNullOrWhiteSpace(revisioned.Revision)) return revisioned.Revision;
        return rules.GetType().AssemblyQualifiedName ?? rules.GetType().FullName ?? rules.GetType().Name;
    }

    public static Minecraft1122ConversionPreviewCacheKey CreateCacheKey(
        INormalizedMinecraftChunkSource source,
        int? requestedOffset,
        string mappingRevision,
        string resourceRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRevision);
        return new Minecraft1122ConversionPreviewCacheKey(
            source.Revision,
            requestedOffset,
            mappingRevision,
            resourceRevision);
    }

    public static bool CanRenderWithoutClipping(MinecraftDowngradePreviewSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.YTranslation.CanFitWithoutClipping &&
               summary.YTranslation.Allows(summary.AppliedYOffset) &&
               summary.VerticallyClippedBlockCount == 0;
    }

    public static string FormatHeightBlockReason(MinecraftDowngradePreviewSummary summary)
    {
        MinecraftYTranslationLimits limits = summary.YTranslation;
        if(!limits.CanFitWithoutClipping && limits.OccupiedSourceRange is { } range)
            return UiText.Format($"无法无损转换：地图实际方块高度为 Y {range.Minimum}～{range.Maximum}（{(long)range.Maximum - range.Minimum + 1} 格），") +
                   UiText.Get("超过 1.12.2 的 Y 0～255（256 格）。整体上移也放不下；需先确定保留的高度范围并另存裁剪副本，再进行预览或对照。源地图未修改。");
        return UiText.Format($"当前 Y 偏移 {summary.AppliedYOffset} 会导致方块超出 1.12.2 高度范围。") +
               UiText.Format($"请重新计算安全 Y 偏移（合法范围 {limits.MinimumOffset}～{limits.MaximumOffset}）。源地图未修改。");
    }

    public static bool CanExport(MinecraftDowngradePreviewSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.CanExport &&
               (CanRenderWithoutClipping(summary) || CanRenderConfirmedTopClipping(summary));
    }

    public static bool CanRenderConfirmedTopClipping(MinecraftDowngradePreviewSummary summary) =>
        summary.VerticalClippingAllowed && summary.YTranslation.MinimumOffset == summary.AppliedYOffset;

    public static string BuildSuggestedDestinationDirectory(
        string parentDirectory,
        MinecraftWorldDescriptor sourceDescriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        string parent = Path.GetFullPath(parentDirectory);
        string source = Path.GetFullPath(sourceDescriptor.RootPath);
        if(IsSameOrDescendant(parent, source))
        {
            throw new InvalidOperationException("导出位置不能是原世界文件夹或其子目录。");
        }

        string worldName = SanitizeFolderName(sourceDescriptor.LevelName);
        string destination = Path.GetFullPath(Path.Combine(parent, $"{worldName}-1.12.2"));
        if(string.Equals(destination, source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("导出位置不能覆盖原世界文件夹。");
        return destination;
    }

    public static MinecraftWorldExportRequest CreateExportRequest(
        string destinationDirectory,
        IMinecraftDowngradePreview preview,
        MinecraftOpaquePayload sourceLevelMetadata,
        MinecraftWorldDescriptor sourceDescriptor,
        IMinecraftAuxiliaryFileSource auxiliaryFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(sourceLevelMetadata);
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(auxiliaryFiles);
        if(!CanExport(preview.Summary))
            throw new InvalidOperationException("当前转换预览未通过无裁切导出检查。");
        BlockPosition spawn = sourceDescriptor.SpawnLocation ??
            throw new InvalidOperationException("源世界缺少出生点坐标，无法安全导出。");

        return new MinecraftWorldExportRequest(
            Path.GetFullPath(destinationDirectory),
            preview.ConvertedChunks.Revision,
            MinecraftTargetProfile.Java1122,
            preview.ConvertedChunks,
            sourceLevelMetadata,
            spawn,
            preview.Summary.AppliedYOffset,
            new MinecraftUnknownDataPolicy(),
            auxiliaryFiles,
            MinecraftExportDestinationPolicy.CreateUniqueSibling,
            new MinecraftAuxiliaryExportPolicy());
    }

    public static string FormatSummary(MinecraftDowngradePreviewSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        long fallback = checked(summary.VisibleFallbackBlockCount + summary.BlockedBlockCount);
        string offset = summary.AppliedYOffset >= 0
            ? $"+{summary.AppliedYOffset}"
            : summary.AppliedYOffset.ToString();
        return UiText.Format($"Y {offset} · 精确 {summary.ExactBlockCount:N0} · 相似 {summary.SimilarBlockCount:N0} · 替代 {fallback:N0} · 裁切 {summary.VerticallyClippedBlockCount:N0}");
    }

    private static string SanitizeFolderName(string? levelName)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        string sanitized = new((levelName ?? string.Empty)
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        sanitized = sanitized.TrimEnd('.', ' ');
        if(sanitized.Length == 0) sanitized = "Minecraft World";
        return sanitized.Length <= 96 ? sanitized : sanitized[..96].TrimEnd('.', ' ');
    }

    private static bool IsSameOrDescendant(string candidate, string ancestor)
    {
        string relative = Path.GetRelativePath(ancestor, candidate);
        return relative == "." ||
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
