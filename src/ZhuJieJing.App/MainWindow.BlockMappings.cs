using System.Numerics;
using System.Windows;
using ZhuJieJing.App.ConversionPreview;
using ZhuJieJing.App.WorldPreview;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private readonly Minecraft1122BlockMappingOverrideStore blockMappingOverrideStore =
        Minecraft1122BlockMappingOverrideStore.CreateDefault();
    private Minecraft1122BlockMappingOverrideSet? blockMappingOverrides;
    private Task<Minecraft1122BlockMappingOverrideSet>? blockMappingOverridesLoadTask;

    private async Task<Minecraft1122BlockMappingOverrideSet> GetBlockMappingOverridesAsync()
    {
        if(blockMappingOverrides is not null) return blockMappingOverrides;
        blockMappingOverridesLoadTask ??= blockMappingOverrideStore.LoadAsync().AsTask();
        try
        {
            blockMappingOverrides = await blockMappingOverridesLoadTask;
            return blockMappingOverrides;
        }
        catch
        {
            blockMappingOverridesLoadTask = null;
            throw;
        }
    }

    private async Task<IMinecraftBlockDowngradeRules> GetBlockMappingRulesAsync()
    {
        Minecraft1122BlockMappingOverrideSet overrides = await GetBlockMappingOverridesAsync();
        return Minecraft1122BlockDowngradeRules.WithOverrides(overrides);
    }

    private async void BlockMappingTable_Click(object sender, RoutedEventArgs e)
    {
        if(isClosing || isCalculatingConversionYOffset || activeConversionWorkflowTask is not null ||
           activeConversionPreviewTask is not null ||
           activeWorldPreviewTask is not null) return;
        bool refreshConvertedPreview = false;
        Task<bool>? workflow = null;
        SetSourceOpenButtonsEnabled(false);
        SetCoordinateJumpButtonsEnabled(false);
        SetConversionControlsEnabled(false);
        try
        {
            workflow = RunBlockMappingTableAsync();
            activeConversionWorkflowTask = workflow;
            refreshConvertedPreview = await workflow;
        }
        catch(OperationCanceledException) when(isClosing)
        {
        }
        catch(Exception exception)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"方块映射表操作失败：{exception.Message}"));
        }
        finally
        {
            if(ReferenceEquals(activeConversionWorkflowTask, workflow)) activeConversionWorkflowTask = null;
            if(!isClosing)
            {
                SetSourceOpenButtonsEnabled(true);
                SetCoordinateJumpButtonsEnabled(true);
                UpdateConversionControls();
            }
        }

        if(refreshConvertedPreview && !isClosing)
            ConversionPreview_Click(BlockMappingTableButton, new RoutedEventArgs());
    }

    private async Task<bool> RunBlockMappingTableAsync()
    {
        await Task.Yield();
        Minecraft1122BlockMappingOverrideSet overrides = await GetBlockMappingOverridesAsync();
        if(isClosing) return false;

        IReadOnlyMinecraftWorld? world = mountedWorld;
        IMinecraftDowngradePreview? mappingPreview = activeConversionPreview;
        if(mappingPreview is null && world is not null &&
           Minecraft1122ConversionPreviewWorkflow.IsHigherThanTarget(world.Descriptor.Version))
        {
            IMinecraftBlockDowngradeRules rules = Minecraft1122BlockDowngradeRules.WithOverrides(overrides);
            mappingPreview = await PrepareCurrentWorldMappingsAsync(world, rules);
        }
        if(isClosing || !ReferenceEquals(world, mountedWorld)) return false;

        IReadOnlyList<Minecraft1122LegacyMappingTarget> targets = Minecraft1122BlockMappingCatalog.LegacyTargets;
        IReadOnlyList<BlockMappingEditorEntry> entries = CreateBlockMappingEditorEntries(
            overrides,
            targets,
            mappingPreview?.Summary.Mappings);
        var dialog = new BlockMappingTableWindow(
            entries,
            targets,
            randomJumpHandler: JumpToRandomBlockMappingOccurrenceAsync)
        {
            Owner = GetMapFunctionsDialogOwner(),
        };
        TrackTopLevelWindowRenderingSuspension(dialog);
        if(dialog.ShowDialog() != true) return false;

        Minecraft1122BlockMappingOverrideSet updated = new(
            dialog.SavedOverrides.Select(static item => new Minecraft1122BlockMappingOverride(
                item.SourceKey,
                item.Target.NumericId,
                item.Target.Metadata,
                "筑界镜用户设定")));
        if(string.Equals(updated.Revision, overrides.Revision, StringComparison.Ordinal))
        {
            UiText.Bind(StatusText, "Text", UiText.Text("方块映射表没有变化。"));
            return false;
        }

        await blockMappingOverrideStore.SaveAsync(updated);
        blockMappingOverrides = updated;
        blockMappingOverridesLoadTask = Task.FromResult(updated);
        InvalidateConversionPreviewCache();
        bool refreshConvertedPreview = displayedContent == DisplayedContent.ConvertedWorldSections &&
                                       mountedWorld is not null;
        UiText.Bind(StatusText, "Text", refreshConvertedPreview ? UiText.Message($"已保存 {updated.Count:N0} 条用户方块映射，正在重新生成转换预览……") : UiText.Message($"已保存 {updated.Count:N0} 条用户方块映射；下次转换预览将自动采用。"));
        return refreshConvertedPreview;
    }

    private async Task<IMinecraftDowngradePreview> PrepareCurrentWorldMappingsAsync(
        IReadOnlyMinecraftWorld world,
        IMinecraftBlockDowngradeRules rules,
        string operationName = "统计当前地图会被替代的方块")
    {
        CancellationTokenSource requestCancellation = new();
        conversionPreviewCancellation = requestCancellation;
        Task<IMinecraftDowngradePreview>? task = null;
        IMinecraftDowngradePreview? pendingPreview = null;
        int totalChunks = world.ChunkIndex.TotalChunkCount;
        UiText.Bind(StatusText, "Text", UiText.Message($"正在{operationName}：0 / {totalChunks:N0} 个 Chunk……"));

        try
        {
            INormalizedMinecraftChunkSource source = NormalizedMinecraftChunkSourceFactory.Create(world);
            string resourceRevision = await ResolveConversionResourceRevisionAsync(
                world.Descriptor,
                requestCancellation.Token);
            EnsureCurrentConversionRequest(world, requestCancellation);
            Minecraft1122ConversionPreviewCacheKey cacheKey =
                Minecraft1122ConversionPreviewWorkflow.CreateCacheKey(
                    source,
                    requestedOffset: null,
                    Minecraft1122ConversionPreviewWorkflow.GetMappingRevision(rules),
                    resourceRevision);
            if(conversionPreviewCache.TryGet(cacheKey, out IMinecraftDowngradePreview cachedPreview))
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"{operationName}已复用当前地图转换缓存。"));
                return cachedPreview;
            }

            IReadOnlyList<MinecraftChunkAddress> addresses = await Task.Run(
                async () => await CollectAllChunkAddressesAsync(
                    world.ChunkIndex,
                    source.Dimensions,
                    requestCancellation.Token).ConfigureAwait(false),
                requestCancellation.Token);
            EnsureCurrentConversionRequest(world, requestCancellation);
            int concurrency = Minecraft1122ConversionPreviewWorkflow.SelectConversionConcurrency(
                CurrentChunkLoadConcurrency);
            MinecraftDowngradePreviewRequest request = Minecraft1122ConversionPreviewWorkflow.CreateRequest(
                source,
                requestedOffset: null,
                addresses,
                concurrency);
            Progress<MinecraftConversionProgress> progress = new(value =>
            {
                if(IsCurrentConversionRequest(world, requestCancellation))
                {
                    UiText.Bind(StatusText, "Text", UiText.Message($"正在{operationName}：{value.ChunksScanned:N0} / {totalChunks:N0} 个 Chunk · {concurrency:N0} 路……"));
                }
            });
            MinecraftDowngradePreviewService service = new(rules);
            task = Task.Run(
                async () => await service.CreateAsync(request, progress, requestCancellation.Token),
                requestCancellation.Token);
            activeConversionPreviewTask = task;
            pendingPreview = await task;
            EnsureCurrentConversionRequest(world, requestCancellation);
            await conversionPreviewCache.StoreAsync(cacheKey, pendingPreview);
            IMinecraftDowngradePreview selectedPreview = pendingPreview;
            pendingPreview = null;
            UiText.Bind(StatusText, "Text", UiText.Message($"{operationName}完成。"));
            return selectedPreview;
        }
        finally
        {
            if(pendingPreview is not null) await pendingPreview.DisposeAsync();
            if(ReferenceEquals(activeConversionPreviewTask, task)) activeConversionPreviewTask = null;
            if(ReferenceEquals(conversionPreviewCancellation, requestCancellation))
            {
                conversionPreviewCancellation = null;
                requestCancellation.Dispose();
            }
        }
    }

    private async Task<BlockMappingRandomJumpResult> JumpToRandomBlockMappingOccurrenceAsync(
        BlockMappingEditorEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        IReadOnlyMinecraftWorld? world = mountedWorld;
        if(world is null)
            return new BlockMappingRandomJumpResult(false, "请先打开 Minecraft 地图。");

        cancellationToken.ThrowIfCancellationRequested();
        INormalizedMinecraftChunkSource source = NormalizedMinecraftChunkSourceFactory.Create(world);
        string sourceRevision = source.Revision;
        MinecraftDimensionId preferredDimension = displayedWorldDimension;
        int concurrency = Math.Clamp(
            CurrentChunkLoadConcurrency,
            NormalizedMinecraftChunkBatcher.MinimumLoadConcurrency,
            NormalizedMinecraftChunkBatcher.MaximumLoadConcurrency);
        UiText.Bind(StatusText, "Text", UiText.Message($"正在当前地图中查找“{entry.SourceTitle}”…"));

        IReadOnlyList<MinecraftChunkAddress> addresses = await Task.Run(
            async () => await CollectAllChunkAddressesAsync(
                world.ChunkIndex,
                source.Dimensions,
                cancellationToken).ConfigureAwait(false),
            cancellationToken);
        if(isClosing || !ReferenceEquals(world, mountedWorld))
            return new BlockMappingRandomJumpResult(false, "地图已切换，本次查找已取消。");

        Progress<MinecraftBlockOccurrenceSearchProgress> progress = new(value =>
        {
            if(!isClosing && !cancellationToken.IsCancellationRequested && ReferenceEquals(world, mountedWorld))
            {
                UiText.Bind(StatusText, "Text", UiText.Message($"正在当前地图中查找“{entry.SourceTitle}”：{value.ChunksScanned:N0} / {value.ChunksTotal:N0} 个 Chunk……"));
            }
        });
        MinecraftBlockOccurrence? occurrence = await Task.Run(
            async () => await MinecraftBlockOccurrenceFinder.FindRandomAsync(
                source,
                addresses,
                entry.SourceKey,
                preferredDimension,
                concurrency,
                progress,
                cancellationToken).ConfigureAwait(false),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if(isClosing || !ReferenceEquals(world, mountedWorld) ||
           !string.Equals(sourceRevision, source.Revision, StringComparison.Ordinal))
        {
            return new BlockMappingRandomJumpResult(false, "地图已变化，本次查找结果已丢弃。");
        }
        if(occurrence is not MinecraftBlockOccurrence found)
        {
            string notFound = $"当前地图中没有“{entry.SourceTitle}”的实际方块。";
            StatusText.Text = notFound;
            return new BlockMappingRandomJumpResult(false, notFound);
        }

        BlockPosition position = found.Position;
        XCoordinate.Text = position.X.ToString();
        YCoordinate.Text = position.Y.ToString();
        ZCoordinate.Text = position.Z.ToString();
        await JumpToPositionAsync(
            new Vector3(position.X, position.Y, position.Z),
            position,
            found.Chunk.Dimension,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string dimensionName = WorldRegionNavigation.FormatDimensionName(found.Chunk.Dimension);
        string message =
            UiText.Format($"已跳转到“{entry.SourceTitle}”：{dimensionName} X {position.X:N0} / Y {position.Y:N0} / Z {position.Z:N0}。");
        StatusText.Text = message;
        return new BlockMappingRandomJumpResult(true, message);
    }

    private IReadOnlyList<BlockMappingEditorEntry> CreateBlockMappingEditorEntries(
        Minecraft1122BlockMappingOverrideSet overrides,
        IReadOnlyList<Minecraft1122LegacyMappingTarget> targets,
        IReadOnlyList<MinecraftBlockMappingSummary>? currentWorldMappings)
    {
        var targetByEncoding = targets
            .GroupBy(static target => target.LegacyEncoding)
            .ToDictionary(static group => group.Key, static group => group.First());
        Minecraft1122BlockDowngradeRules builtInRules = Minecraft1122BlockDowngradeRules.Instance;
        Minecraft1122BlockDowngradeRules effectiveRules = Minecraft1122BlockDowngradeRules.WithOverrides(overrides);
        var rows = new Dictionary<string, BlockMappingEditorEntry>(StringComparer.Ordinal);

        if(currentWorldMappings is not null)
        {
            var currentSubstitutions = currentWorldMappings
                .Where(static summary => !summary.Mapping.Source.IsAir)
                .Select(summary => new
                {
                    Summary = summary,
                    Effective = effectiveRules.Resolve(summary.Mapping.Source, MinecraftTargetProfile.Java1122),
                })
                .Where(static item => item.Effective.Quality != MinecraftBlockMappingQuality.Exact);
            foreach(var group in currentSubstitutions
                        .GroupBy(static item => item.Summary.Mapping.Source.Name, StringComparer.Ordinal)
                        .OrderByDescending(static group => group.Sum(static item => item.Summary.BlockCount)))
            {
                var sample = group.MaxBy(static item => item.Summary.BlockCount)!;
                BlockState source = sample.Summary.Mapping.Source;
                MinecraftBlockMapping builtIn = builtInRules.Resolve(source, MinecraftTargetProfile.Java1122);
                MinecraftBlockMapping effective = sample.Effective;
                if(!TryCreateEditorTargets(builtIn, effective, targetByEncoding, out Minecraft1122LegacyMappingTarget defaultTarget, out Minecraft1122LegacyMappingTarget effectiveTarget))
                    continue;
                bool hasNameOverride = overrides.TryResolve(source, out Minecraft1122BlockMappingOverride applied) &&
                                       string.Equals(applied.SourceKey, source.Name, StringComparison.Ordinal);
                if(hasNameOverride) effectiveTarget = FindEditorTarget(applied.LegacyEncoding, targetByEncoding);
                Minecraft1122BlockMappingDefinition? catalogDefinition = FindBuiltInDefinition(source.Name);
                string sourceDisplay = CreateSourceDisplay(source, source.Name, catalogDefinition);
                rows[source.Name] = new BlockMappingEditorEntry(
                    source,
                    source.Name,
                    sourceDisplay,
                    defaultTarget,
                    effectiveTarget,
                    effective.Quality,
                    effective.RuleId,
                    effective.Explanation,
                    group.Sum(static item => item.Summary.BlockCount),
                    canOverride: true,
                    hasNameOverride,
                    thumbnailSourceKey: source.CanonicalKey);
            }
        }

        foreach(Minecraft1122BlockMappingDefinition definition in Minecraft1122BlockMappingCatalog.BuiltInMappings)
        {
            if(rows.ContainsKey(definition.SourcePattern)) continue;
            Minecraft1122LegacyMappingTarget defaultTarget = FindEditorTarget(
                definition.LegacyEncoding,
                definition.TargetState,
                targetByEncoding);
            bool canOverride = !definition.SourcePattern.Contains('*', StringComparison.Ordinal);
            Minecraft1122LegacyMappingTarget effectiveTarget = defaultTarget;
            bool hasOverride = false;
            if(canOverride)
            {
                BlockState source = new(definition.SourcePattern);
                MinecraftBlockMapping effective = effectiveRules.Resolve(source, MinecraftTargetProfile.Java1122);
                if(effective.LegacyEncoding is LegacyBlockEncoding encoding && effective.Target is BlockState targetState)
                    effectiveTarget = FindEditorTarget(encoding, targetState, targetByEncoding);
                hasOverride = overrides.TryResolve(source, out Minecraft1122BlockMappingOverride applied) &&
                              string.Equals(applied.SourceKey, definition.SourcePattern, StringComparison.Ordinal);
                if(hasOverride) effectiveTarget = FindEditorTarget(applied.LegacyEncoding, targetByEncoding);
            }
            rows[definition.SourcePattern] = new BlockMappingEditorEntry(
                new BlockState(definition.SourcePattern),
                definition.SourcePattern,
                CreateSourceDisplay(new BlockState(definition.SourcePattern), definition.SourcePattern, definition),
                defaultTarget,
                effectiveTarget,
                definition.Quality,
                definition.RuleId,
                definition.Explanation,
                blockCount: 0,
                canOverride,
                hasOverride,
                thumbnailSourceKey: definition.SourcePattern);
        }

        foreach(Minecraft1122BlockMappingOverride mappingOverride in overrides.Entries)
        {
            if(rows.ContainsKey(mappingOverride.SourceKey)) continue;
            BlockState source = ParseBlockMappingSource(mappingOverride.SourceKey);
            MinecraftBlockMapping builtIn = builtInRules.Resolve(source, MinecraftTargetProfile.Java1122);
            MinecraftBlockMapping effective = effectiveRules.Resolve(source, MinecraftTargetProfile.Java1122);
            if(!TryCreateEditorTargets(builtIn, effective, targetByEncoding, out Minecraft1122LegacyMappingTarget defaultTarget, out Minecraft1122LegacyMappingTarget effectiveTarget))
                continue;
            effectiveTarget = FindEditorTarget(mappingOverride.LegacyEncoding, targetByEncoding);
            rows[mappingOverride.SourceKey] = new BlockMappingEditorEntry(
                source,
                mappingOverride.SourceKey,
                CreateSourceDisplay(source, mappingOverride.SourceKey, FindBuiltInDefinition(source.Name)),
                defaultTarget,
                effectiveTarget,
                builtIn.Quality,
                builtIn.RuleId,
                builtIn.Explanation,
                blockCount: 0,
                canOverride: true,
                hasUserOverride: true,
                thumbnailSourceKey: mappingOverride.SourceKey);
        }

        return rows.Values
            .OrderByDescending(static row => row.BlockCount > 0)
            .ThenByDescending(static row => row.BlockCount)
            .ThenByDescending(static row => row.HasUserOverride)
            .ThenBy(static row => row.SourceDisplay, StringComparer.Ordinal)
            .ToArray();
    }

    private static Minecraft1122BlockMappingDefinition? FindBuiltInDefinition(string sourceName) =>
        Minecraft1122BlockMappingCatalog.BuiltInMappings.FirstOrDefault(definition =>
            MappingPatternMatches(definition.SourcePattern, sourceName));

    private static string CreateSourceDisplay(
        BlockState source,
        string sourceKey,
        Minecraft1122BlockMappingDefinition? definition)
    {
        string chineseName;
        if(Minecraft1122BlockDisplayNames.TryGetDisplayName(source.Name, out string translated))
        {
            chineseName = translated;
        }
        else if(!string.IsNullOrWhiteSpace(definition?.SourceDisplay))
        {
            chineseName = definition.SourceDisplay;
        }
        else
        {
            chineseName = source.Name.StartsWith("minecraft:", StringComparison.Ordinal)
                ? "未收录中文名称"
                : "模组方块（未提供中文名称）";
        }
        return $"{chineseName} · {sourceKey}";
    }

    private static bool MappingPatternMatches(string pattern, string value)
    {
        int wildcard = pattern.IndexOf('*');
        if(wildcard < 0) return string.Equals(pattern, value, StringComparison.Ordinal);
        return value.StartsWith(pattern[..wildcard], StringComparison.Ordinal) &&
               value.EndsWith(pattern[(wildcard + 1)..], StringComparison.Ordinal);
    }

    private static bool TryCreateEditorTargets(
        MinecraftBlockMapping builtIn,
        MinecraftBlockMapping effective,
        IReadOnlyDictionary<LegacyBlockEncoding, Minecraft1122LegacyMappingTarget> targetByEncoding,
        out Minecraft1122LegacyMappingTarget defaultTarget,
        out Minecraft1122LegacyMappingTarget effectiveTarget)
    {
        if(builtIn.LegacyEncoding is not LegacyBlockEncoding defaultEncoding || builtIn.Target is not BlockState defaultState ||
           effective.LegacyEncoding is not LegacyBlockEncoding currentEncoding || effective.Target is not BlockState currentState)
        {
            defaultTarget = null!;
            effectiveTarget = null!;
            return false;
        }
        defaultTarget = FindEditorTarget(defaultEncoding, defaultState, targetByEncoding);
        effectiveTarget = FindEditorTarget(currentEncoding, currentState, targetByEncoding);
        return true;
    }

    private static Minecraft1122LegacyMappingTarget FindEditorTarget(
        LegacyBlockEncoding encoding,
        BlockState state,
        IReadOnlyDictionary<LegacyBlockEncoding, Minecraft1122LegacyMappingTarget> targetByEncoding) =>
        targetByEncoding.TryGetValue(encoding, out Minecraft1122LegacyMappingTarget? target)
            ? target
            : Minecraft1122BlockMappingCatalog.CreateLegacyTarget(encoding, state);

    private static Minecraft1122LegacyMappingTarget FindEditorTarget(
        LegacyBlockEncoding encoding,
        IReadOnlyDictionary<LegacyBlockEncoding, Minecraft1122LegacyMappingTarget> targetByEncoding)
    {
        if(targetByEncoding.TryGetValue(encoding, out Minecraft1122LegacyMappingTarget? target)) return target;
        MinecraftRegistryResolution resolution = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(
            encoding.NumericId,
            encoding.Metadata);
        return Minecraft1122BlockMappingCatalog.CreateLegacyTarget(encoding, resolution.State);
    }

    private static BlockState ParseBlockMappingSource(string sourceKey)
    {
        int propertiesStart = sourceKey.IndexOf('[', StringComparison.Ordinal);
        if(propertiesStart < 0) return new BlockState(sourceKey);
        string name = sourceKey[..propertiesStart];
        string propertyText = sourceKey[(propertiesStart + 1)..^1];
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach(string pair in propertyText.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if(separator <= 0 || separator == pair.Length - 1) continue;
            properties[pair[..separator]] = pair[(separator + 1)..];
        }
        return new BlockState(name, properties);
    }
}
