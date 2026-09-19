using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Rebuilds target lighting in a fixed 3 x 3 chunk neighbourhood. A light value travels at most fourteen
/// horizontal blocks, so a sixteen-block halo is sufficient even at region seams. No world-sized volume
/// or unbounded chunk cache is needed. Explicit modern light sources are baked without visible blocks.
/// </summary>
internal sealed class Minecraft1122LightBaker(INormalizedMinecraftChunkSource source, Func<BlockState, LegacyBlockEncoding> resolve)
{
    private const int Width = 48;
    private const int Plane = Width * Width;
    private const int Height = 256;
    private const int Volume = Plane * Height;
    private readonly MinecraftChunkMemoryCache neighbours = new(32L * 1024 * 1024);

    internal async ValueTask<BakedLegacyChunkLighting> BakeAsync(NormalizedMinecraftChunk center, CancellationToken cancellationToken)
    {
        byte[] opacity = new byte[Volume];
        byte[] blockLight = new byte[Volume];
        byte[] skyLight = new byte[Volume];
        bool staticLightPresent = false;
        neighbours.Add(center.Address, center);
        for(int dz = -1; dz <= 1; dz++)
        for(int dx = -1; dx <= 1; dx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long x = (long)center.Address.X + dx;
            long z = (long)center.Address.Z + dz;
            if(x < int.MinValue || x > int.MaxValue || z < int.MinValue || z > int.MaxValue) continue;
            MinecraftChunkAddress address = new(center.Address.Dimension, (int)x, (int)z);
            NormalizedMinecraftChunk? chunk = center;
            if(dx != 0 || dz != 0)
            {
                if(!neighbours.TryGet(address, out chunk))
                {
                    chunk = source is IUncachedNormalizedMinecraftChunkSource uncached
                        ? await uncached.FindUncachedAsync(address, cancellationToken).ConfigureAwait(false)
                        : await source.FindAsync(address, cancellationToken).ConfigureAwait(false);
                    neighbours.Add(address, chunk);
                }
            }
            if(chunk is null) continue;
            if(chunk.Address != address) throw new InvalidDataException($"光照邻域返回了错误区块：{chunk.Address}，期望 {address}。");
            foreach(NormalizedMinecraftSection section in chunk.Sections)
            {
                if(section.Coordinate.Y is < 0 or > 15 || section.PaletteIndices.Length != 4096)
                    throw new InvalidDataException($"光照邻域 Section 超出 1.12.2 格式：{section.Coordinate}。");
                byte[] paletteOpacity = new byte[section.Palette.Count];
                byte[] paletteEmission = new byte[section.Palette.Count];
                for(int p = 0; p < section.Palette.Count; p++)
                {
                    BlockState state = section.Palette[p];
                    LegacyBlockEncoding encoding = state.IsAir ? new(0, 0) : resolve(state);
                    paletteOpacity[p] = Minecraft1122RuntimeRegistry.GetOpacity(encoding.NumericId);
                    paletteEmission[p] = Minecraft1122RuntimeRegistry.GetEmission(encoding.NumericId);
                    if(state.Name == "minecraft:light")
                    {
                        paletteEmission[p] = (byte)(state.Properties.TryGetValue("level", out string? value) && int.TryParse(value, out int level)
                            ? Math.Clamp(level, 0, 15) : 15);
                        staticLightPresent |= paletteEmission[p] > 0;
                    }
                }
                ReadOnlyMemory<byte> staticSources = section.Lighting?.StaticLightSourceNibbles ?? default;
                if(staticSources.Length is not 0 and not 2048)
                    throw new InvalidDataException($"静态光源数组长度无效：{section.Coordinate}。");
                for(int local = 0; local < 4096; local++)
                {
                    if((local & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int p = section.PaletteIndices.Span[local];
                    if(p >= paletteOpacity.Length) throw new InvalidDataException($"光照 Section 调色板索引越界：{section.Coordinate}。");
                    int px = (dx + 1) * 16 + (local & 15);
                    int pz = (dz + 1) * 16 + ((local >> 4) & 15);
                    int py = section.Coordinate.Y * 16 + (local >> 8);
                    int index = py * Plane + pz * Width + px;
                    opacity[index] = paletteOpacity[p];
                    byte bakedSource = staticSources.IsEmpty ? (byte)0 : Nibble(staticSources.Span, local);
                    staticLightPresent |= bakedSource > 0;
                    blockLight[index] = Math.Max(paletteEmission[p], bakedSource);
                }
            }
        }

        bool hasSky = center.Address.Dimension == MinecraftDimensionId.Overworld;
        if(hasSky)
        {
            for(int column = 0; column < Plane; column++)
            {
                int level = 15;
                for(int y = Height - 1; y >= 0; y--)
                {
                    int index = y * Plane + column;
                    int attenuation = opacity[index];
                    if(attenuation == 0 && level != 15) attenuation = 1;
                    level = Math.Max(0, level - attenuation);
                    skyLight[index] = (byte)level;
                }
            }
            Propagate(skyLight, opacity, blockLight: false, cancellationToken);
        }
        Propagate(blockLight, opacity, blockLight: true, cancellationToken);

        int[] heightMap = new int[256];
        Dictionary<int, MinecraftSectionLighting> sections = [];
        HashSet<int> presentSections = center.Sections.Select(static section => section.Coordinate.Y).ToHashSet();
        for(int sy = 0; sy < 16; sy++)
        {
            byte[] sky = new byte[2048];
            byte[] block = new byte[2048];
            bool differsFromMissingSection = false;
            for(int local = 0; local < 4096; local++)
            {
                int x = local & 15;
                int z = local >> 4 & 15;
                int y = sy * 16 + (local >> 8);
                int index = y * Plane + (z + 16) * Width + x + 16;
                SetNibble(sky, local, skyLight[index]);
                SetNibble(block, local, blockLight[index]);
                differsFromMissingSection |= blockLight[index] != 0 || skyLight[index] != (hasSky ? 15 : 0);
                if(opacity[index] != 0) heightMap[z * 16 + x] = y + 1;
            }
            if(presentSections.Contains(sy) || differsFromMissingSection)
                sections.Add(sy, new MinecraftSectionLighting(sky, block));
        }
        return new BakedLegacyChunkLighting(heightMap, sections, staticLightPresent);
    }

    private static void Propagate(byte[] light, byte[] opacity, bool blockLight, CancellationToken cancellationToken)
    {
        Queue<int>[] levels = Enumerable.Range(0, 16).Select(static _ => new Queue<int>()).ToArray();
        for(int i = 0; i < Volume; i++)
        {
            if((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            byte level = light[i];
            if(level < 2) continue;
            // Interior cells of a uniformly sunlit volume never need to enter the work queue.
            if(!blockLight && !HasDarkerNeighbour(i, level - 1, light)) continue;
            levels[level].Enqueue(i);
        }
        for(int level = 15; level >= 2; level--)
        {
            Queue<int> queue = levels[level];
            int work = 0;
            while(queue.TryDequeue(out int index))
            {
                if((work++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if(light[index] != level) continue;
                int x = index % Width;
                int z = index / Width % Width;
                if(x > 0) Visit(index - 1);
                if(x + 1 < Width) Visit(index + 1);
                if(z > 0) Visit(index - Width);
                if(z + 1 < Width) Visit(index + Width);
                if(index >= Plane) Visit(index - Plane);
                if(index + Plane < Volume) Visit(index + Plane);

                void Visit(int target)
                {
                    int attenuation = Math.Max(1, (int)opacity[target]);
                    int candidate = level - attenuation;
                    if(candidate <= light[target]) return;
                    light[target] = (byte)candidate;
                    if(candidate > 1) levels[candidate].Enqueue(target);
                }
            }
        }
    }

    private static bool HasDarkerNeighbour(int index, int minimum, byte[] values)
    {
        int x = index % Width;
        int z = index / Width % Width;
        return x > 0 && values[index - 1] < minimum || x + 1 < Width && values[index + 1] < minimum ||
               z > 0 && values[index - Width] < minimum || z + 1 < Width && values[index + Width] < minimum ||
               index >= Plane && values[index - Plane] < minimum || index + Plane < Volume && values[index + Plane] < minimum;
    }

    private static byte Nibble(ReadOnlySpan<byte> data, int index) => (byte)((data[index >> 1] >> ((index & 1) * 4)) & 15);
    private static void SetNibble(Span<byte> data, int index, byte value) =>
        data[index >> 1] = (byte)((data[index >> 1] & (index % 2 == 0 ? 0xf0 : 0x0f)) | value << ((index & 1) * 4));

}

internal sealed record BakedLegacyChunkLighting(int[] HeightMap, IReadOnlyDictionary<int, MinecraftSectionLighting> Sections, bool HasStaticSources);
