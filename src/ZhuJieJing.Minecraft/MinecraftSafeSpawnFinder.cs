using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

internal static class MinecraftSafeSpawnFinder
{
    internal static async Task<BlockPosition> FindAsync(IReadOnlyMinecraftWorld world,
        IReadOnlyList<MinecraftChunkIndexEntry> retainedEntries, MinecraftChunkSquareBounds bounds,
        CancellationToken cancellationToken)
    {
        BlockPosition center = MinecraftWorldCropSpawnPolicy.MoveToCenter(bounds, 0);
        IMinecraftChunkNormalizer normalizer = world.Descriptor.Version.StorageFamily switch
        {
            MinecraftStorageFamily.LegacyNumericAnvil => new LegacyAnvilChunkNormalizer(),
            MinecraftStorageFamily.FlattenedPalette or MinecraftStorageFamily.ModernSectionPalette => new ModernAnvilChunkNormalizer(),
            _ => throw new InvalidOperationException("无法判断当前区块格式，不能验证裁剪后的安全出生点。"),
        };
        foreach(MinecraftChunkIndexEntry entry in retainedEntries.OrderBy(entry =>
                    Math.Pow((double)entry.Address.X * 16 + 8 - center.X, 2) +
                    Math.Pow((double)entry.Address.Z * 16 + 8 - center.Z, 2)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawMinecraftChunk raw = await world.ChunkReader.ReadAsync(entry,
                new MinecraftChunkReadOptions(MaximumDecompressedBytes: ModernAnvilChunkNormalizer.MaximumChunkNbtBytes),
                cancellationToken).ConfigureAwait(false);
            NormalizedMinecraftChunk chunk = await normalizer.NormalizeAsync(new MinecraftNormalizationRequest(
                raw, world.Descriptor.Version, Minecraft1122VanillaBlockRegistry.Instance, new MinecraftUnknownDataPolicy()),
                cancellationToken).ConfigureAwait(false);
            Dictionary<int, NormalizedMinecraftSection> sections = chunk.Sections.ToDictionary(static section => section.Coordinate.Y);
            if(sections.Count == 0) continue;
            int minFloorY = world.Descriptor.Version.StorageFamily == MinecraftStorageFamily.ModernSectionPalette ? -64 : 0;
            int maxFloorY = world.Descriptor.Version.StorageFamily == MinecraftStorageFamily.ModernSectionPalette ? 317 : 253;
            int[] topNonAir = Enumerable.Repeat(int.MinValue, 256).ToArray();
            foreach(NormalizedMinecraftSection section in sections.Values)
            {
                for(int local = 0; local < 4096; local++)
                {
                    if((local & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int paletteIndex = section.PaletteIndices.Span[local];
                    if(paletteIndex >= section.Palette.Count) throw new InvalidDataException("出生点检查遇到越界调色板索引。");
                    if(section.Palette[paletteIndex].IsAir) continue;
                    int y = checked(section.Coordinate.Y * 16 + (local >> 8));
                    topNonAir[local & 255] = Math.Max(topNonAir[local & 255], y);
                }
            }
            var columns = from x in Enumerable.Range(1, 14)
                          from z in Enumerable.Range(1, 14)
                          orderby Math.Pow((double)entry.Address.X * 16 + x - center.X, 2) +
                                  Math.Pow((double)entry.Address.Z * 16 + z - center.Z, 2)
                          select (X: x, Z: z);
            foreach((int x, int z) in columns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Java 1.12.2 EntityPlayer.getSpawnPoint calls World.q even at spawnRadius=0.
                // A safe room under a hazardous roof is therefore NOT a safe initial spawn.
                // Require the highest non-air surface and an entirely clear 3 x 3 column above it.
                int floorY = topNonAir[z * 16 + x];
                if(floorY < minFloorY || floorY > maxFloorY || !IsSafeFloor(StateAt(x, floorY, z))) continue;
                bool safe = true;
                for(int dx = -1; dx <= 1 && safe; dx++)
                for(int dz = -1; dz <= 1 && safe; dz++)
                {
                    safe = topNonAir[(z + dz) * 16 + x + dx] == floorY &&
                           IsSafeFloor(StateAt(x + dx, floorY, z + dz));
                }
                if(safe)
                {
                    return new BlockPosition(checked(entry.Address.X * 16 + x), checked(floorY + 1),
                        checked(entry.Address.Z * 16 + z));
                }
            }

            BlockState StateAt(int x, int y, int z)
            {
                int sy = (int)Math.Floor(y / 16.0);
                if(!sections.TryGetValue(sy, out NormalizedMinecraftSection? section)) return BlockState.Air;
                int index = ((y & 15) << 8) | z << 4 | x;
                int paletteIndex = section.PaletteIndices.Span[index];
                if(paletteIndex >= section.Palette.Count) throw new InvalidDataException("出生点检查遇到越界调色板索引。");
                return section.Palette[paletteIndex];
            }
        }
        throw new InvalidOperationException("保留区域内未找到可验证的安全出生点：需要稳定的 3×3 最高落脚面且上方贯通空气；旧版首次出生会重新寻找列顶，不能选择危险屋顶下的室内地板。请调整选区或先建立安全出生平台。");
    }

    private static bool IsSafeFloor(BlockState state)
    {
        if(state.IsAir || !state.Name.StartsWith("minecraft:", StringComparison.Ordinal)) return false;
        MinecraftBlockMapping mapping = Minecraft1122BlockDowngradeRules.Instance.Resolve(state, MinecraftTargetProfile.Java1122);
        if(mapping.Quality == MinecraftBlockMappingQuality.Exact && mapping.LegacyEncoding is { } encoding)
        {
            // Falling blocks, magma and slippery blocks cannot be the basis of an automatically safe spawn.
            return Minecraft1122RuntimeRegistry.IsFullCube(encoding.NumericId) &&
                   encoding.NumericId is not (12 or 13 or 79 or 165 or 174 or 212 or 213 or 252);
        }
        // Conservative full-cube families whose geometry is unchanged by their newer material name.
        string name = state.Name[10..];
        return name is "deepslate" or "cobbled_deepslate" or "calcite" or "tuff" or "polished_tuff" ||
               name.EndsWith("_planks", StringComparison.Ordinal) || name.EndsWith("_log", StringComparison.Ordinal) ||
               name.EndsWith("_wood", StringComparison.Ordinal) || name.EndsWith("_bricks", StringComparison.Ordinal) ||
               name.EndsWith("_terracotta", StringComparison.Ordinal) || name.EndsWith("_wool", StringComparison.Ordinal) ||
               name.EndsWith("_concrete", StringComparison.Ordinal);
    }

}
