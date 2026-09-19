using System.Text.Json;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

public sealed record MinecraftReplacementBlock(BlockState State, long Count, LegacyBlockEncoding? LegacyEncoding, bool HasBlockEntity = false);

public sealed record MinecraftReplacementInventory(string SourceRevision, IReadOnlyList<MinecraftReplacementBlock> Blocks, int ChunkCount, int DimensionCount);

public sealed record MinecraftBlockReplacementRequest(MinecraftReplacementInventory Inventory, MinecraftReplacementBlock Source, MinecraftReplacementBlock Target)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MinecraftReplacementBlock> resolved = new(StringComparer.Ordinal);
    internal MinecraftReplacementBlock ResolveTarget(MinecraftReplacementBlock source) => resolved.GetOrAdd(
        source.State.CanonicalKey + ":" + source.LegacyEncoding, _ => MinecraftBlockReplacementRules.Resolve(source, Target));
    internal bool Changes(MinecraftReplacementBlock source) => MinecraftWorldBlockReplacement.Matches(source, this) && !MinecraftWorldBlockReplacement.Same(source, ResolveTarget(source));
}

/// <summary>Scans one chunk at a time and prepares a revision-checked, same-version rewrite for an in-place transaction.</summary>
public sealed class MinecraftWorldBlockReplacement
{
    public async Task<MinecraftReplacementInventory> ScanAsync(IReadOnlyMinecraftWorld world, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await EnsureStableAsync(world, cancellationToken).ConfigureAwait(false);
        Dictionary<string, MinecraftReplacementBlock> blocks = new(StringComparer.Ordinal);
        int count = 0;
        foreach(MinecraftDimensionDescriptor dimension in world.Descriptor.Dimensions)
        {
            await foreach(MinecraftChunkIndexEntry entry in world.ChunkIndex.EnumerateAsync(dimension.Id, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RawMinecraftChunk raw = await world.ChunkReader.ReadAsync(entry, new MinecraftChunkReadOptions(), cancellationToken);
                ReplacementChunk chunk = new(raw.NbtPayload);
                HashSet<string> entityNames = chunk.EntityBlockNames();
                foreach(ReplacementSection section in chunk.Sections)
                {
                    long[] occurrences = new long[section.Palette.Count];
                    foreach(ushort index in section.Indices) occurrences[index]++;
                    for(int i = 0; i < occurrences.Length; i++)
                    {
                        if(occurrences[i] == 0) continue;
                        MinecraftReplacementBlock block = section.Palette[i];
                        string key = Key(block);
                        blocks.TryGetValue(key, out var existing);
                        blocks[key] = block with { Count = occurrences[i] + (existing?.Count ?? 0), HasBlockEntity = entityNames.Contains(block.State.Name) || existing?.HasBlockEntity == true };
                    }
                }
                if(++count % 32 == 0) progress?.Report($"扫描全部维度：{count:N0} / {world.ChunkIndex.TotalChunkCount:N0} 个区块");
            }
        }
        await EnsureStableAsync(world, cancellationToken).ConfigureAwait(false);
        return new(world.Descriptor.SourceRevision, blocks.Values.OrderBy(b => b.State.CanonicalKey, StringComparer.Ordinal).ToArray(), count, world.Descriptor.Dimensions.Count);
    }

    public static long CountMatches(MinecraftBlockReplacementRequest request) => request.Inventory.Blocks
        .Where(request.Changes).Sum(b => b.Count);

    public async Task<PreparedMinecraftBlockReplacement> PrepareAsync(IReadOnlyMinecraftWorld world, MinecraftBlockReplacementRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if(world is not IReadOnlyMinecraftWorldFileSource fileSource || world.Descriptor.SourceRevisionStrength != MinecraftSourceRevisionStrength.ContentHash)
            throw new InvalidOperationException("原地替换需要完整校验的世界挂载，请重新打开地图。");
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(world.Descriptor.RootPath));
        string parent = Path.GetDirectoryName(source) ?? throw new InvalidDataException("地图必须有父目录。");
        string name = Path.GetFileName(source);
        string staging = Path.Combine(parent, $".{name}.zjj-replace-staging-{Guid.NewGuid():N}");
        string rollback = Path.Combine(parent, $".{name}.zjj-replace-rollback-{Guid.NewGuid():N}");
        Dictionary<string, MinecraftFileFingerprint> sourceFiles = new(StringComparer.OrdinalIgnoreCase);
        await foreach(var file in fileSource.EnumerateWorldFilesAsync(cancellationToken).ConfigureAwait(false))
            sourceFiles.Add(file.RelativePath, file);
        PreparedWorldDirectoryTransaction transaction = new(source, staging, rollback, sourceFiles, ".zjj-replace-staging-", ".zjj-replace-rollback-", "方块替换");
        try
        {
            await PrepareWorldFilesAsync(world, staging, request, progress, cancellationToken).ConfigureAwait(false);
            return new PreparedMinecraftBlockReplacement(transaction);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<MinecraftWorldCloneResult> PrepareWorldFilesAsync(IReadOnlyMinecraftWorld world, string destination, MinecraftBlockReplacementRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if(request.Inventory.SourceRevision != world.Descriptor.SourceRevision)
            throw new InvalidOperationException("地图已更换，请重新扫描方块。");
        if(request.Source.State.IsAir) throw new InvalidOperationException("不能把无限的空气区域作为全地图替换来源。");
        if(MinecraftBlockReplacementRules.StructuralError(request.Source, request.Target) is { } structureError)
            throw new InvalidOperationException(structureError);
        long expected = CountMatches(request);
        if(expected == 0) throw new InvalidOperationException("没有需要替换的方块。");
        bool legacy = request.Inventory.Blocks.All(b => b.LegacyEncoding is not null);
        if(legacy && request.Target.LegacyEncoding is null) throw new InvalidOperationException("旧版地图目标必须具有有效的 ID:子ID。");
        if(!legacy && request.Target.LegacyEncoding is not null) throw new InvalidOperationException("目标版本与地图不一致。");
        // Entity-bearing destinations require a semantic constructor, not fabricated empty NBT.
        if((request.Inventory.Blocks.Any(b => b.State.Name == request.Target.State.Name && b.HasBlockEntity) || RequiresBlockEntity(request.Target.State.Name) || request.Target.LegacyEncoding is { } targetEncoding && Minecraft1122BlockEntityCodec.SupportsBlockEntity(targetEncoding.NumericId)) && request.Target.State.Name != request.Source.State.Name)
            throw new InvalidOperationException("暂不支持新建带独立数据的目标（如箱子、告示牌或头颅）；可以将这些方块替换成普通方块。");

        HashSet<MinecraftChunkAddress> affected = [];
        int scanned = 0;
        foreach(var dimension in world.Descriptor.Dimensions)
        {
            await foreach(var entry in world.ChunkIndex.EnumerateAsync(dimension.Id, cancellationToken: cancellationToken))
            {
                var raw = await world.ChunkReader.ReadAsync(entry, new MinecraftChunkReadOptions(), cancellationToken).ConfigureAwait(false);
                var chunk = new ReplacementChunk(raw.NbtPayload);
                if(chunk.ContainsMatch(request))
                    for(int dz = -1; dz <= 1; dz++)
                    for(int dx = -1; dx <= 1; dx++)
                    {
                        long x = (long)entry.Address.X + dx, z = (long)entry.Address.Z + dz;
                        if(x is >= int.MinValue and <= int.MaxValue && z is >= int.MinValue and <= int.MaxValue)
                            affected.Add(new(dimension.Id, (int)x, (int)z));
                    }
                if(++scanned % 32 == 0) progress?.Report($"准备替换与邻区光照：{scanned:N0} / {world.ChunkIndex.TotalChunkCount:N0} 个区块");
            }
        }
        ReplacementLightingSource? lightingSource = legacy ? new(world, request) : null;
        Minecraft1122LightBaker? lightBaker = lightingSource is null ? null : new(lightingSource, ReplacementLightingSource.ResolveLightingEncoding);
        return await new MinecraftWorldCloneExporter().ExportTransformedAsync(
            new(destination, request.Inventory.SourceRevision, world), async (staging, token) =>
            {
                long changed = 0;
                foreach(MinecraftDimensionDescriptor dimension in world.Descriptor.Dimensions)
                {
                    foreach(string regionDirectory in dimension.RegionDirectories)
                    {
                        string directory = Path.GetFullPath(Path.Combine(staging, regionDirectory));
                        string relative = Path.GetRelativePath(staging, directory);
                        if(Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                            throw new InvalidDataException("维度目录越过导出范围。");
                        foreach(string region in Directory.EnumerateFiles(directory, "r.*.*.mca"))
                        {
                            token.ThrowIfCancellationRequested();
                            changed += await MinecraftWorldExportProtection.ReplaceRegionBlocksAsync(region, request, dimension.Id, affected, lightingSource, lightBaker, token);
                            progress?.Report($"替换全部维度：{changed:N0} / {expected:N0} 个方块");
                        }
                        // POI is a rebuildable cache. Invalidating its validity flags lets Minecraft rescan villagers' job sites.
                        string poi = Path.Combine(Path.GetDirectoryName(directory)!, "poi");
                        if(Directory.Exists(poi))
                            foreach(string region in Directory.EnumerateFiles(poi, "r.*.*.mca"))
                                await MinecraftWorldExportProtection.InvalidateReplacementPoiAsync(region, token);
                    }
                }
                if(changed != expected) throw new InvalidDataException($"替换数量与预览不一致：预览 {expected:N0}，实际 {changed:N0}。未修改原地图，请重新扫描。");
                await File.WriteAllTextAsync(Path.Combine(staging, "zjj-block-replacement.json"), JsonSerializer.Serialize(new
                {
                    SourceRevision = request.Inventory.SourceRevision,
                    Source = request.Source.State.CanonicalKey,
                    Target = request.Target.State.CanonicalKey,
                    StatePolicy = "同类方块自动保留兼容状态，跨类型使用目标默认状态",
                    ReplacedBlocks = changed,
                    Scope = "全部维度 / 全部已保存区块",
                    BlockEntityArchive = "被替换的方块实体原始 NBT 保存在对应区块 zjj_replaced_block_entities 标签。",
                }, new JsonSerializerOptions { WriteIndented = true }), token);
            }, new Progress<MinecraftWorldCloneProgress>(p => progress?.Report(p.Stage == MinecraftWorldCloneStage.CopyingFiles ? $"准备临时地图：{p.FilesCopied:N0} 个文件" : "正在校验地图与准备替换")), cancellationToken).ConfigureAwait(false);
    }

    internal static bool Matches(MinecraftReplacementBlock block, MinecraftBlockReplacementRequest request) =>
        MinecraftBlockReplacementRules.Identity(block) == MinecraftBlockReplacementRules.Identity(request.Source);

    internal static bool Same(MinecraftReplacementBlock a, MinecraftReplacementBlock b) => Key(a) == Key(b);
    private static string Key(MinecraftReplacementBlock b) => b.LegacyEncoding is { } l ? $"{l.NumericId}:{l.Metadata}" : b.State.CanonicalKey;

    private static async Task EnsureStableAsync(IReadOnlyMinecraftWorld world, CancellationToken token)
    {
        if(!(await world.ValidateSourceAsync(token).ConfigureAwait(false)).IsStable)
            throw new IOException("源地图已经变化，请关闭游戏或服务端并重新打开地图。");
    }

    private static bool RequiresBlockEntity(string name) => new[]
    {
        "chest", "barrel", "shulker_box", "furnace", "smoker", "hopper", "dispenser", "dropper", "sign", "hanging_sign", "head", "skull", "banner", "bed", "spawner", "beacon", "brewing_stand", "enchanting_table", "lectern", "campfire", "beehive", "bee_nest", "conduit", "jigsaw", "structure_block", "command_block", "end_gateway", "end_portal", "piston", "moving_piston", "comparator", "daylight_detector", "jukebox", "sculk_sensor", "sculk_shrieker", "sculk_catalyst", "decorated_pot", "brushable_block", "suspicious_sand", "suspicious_gravel", "crafter", "trial_spawner", "vault", "ender_chest"
    }.Any(suffix => name == "minecraft:" + suffix || name.EndsWith("_" + suffix, StringComparison.Ordinal));
}
