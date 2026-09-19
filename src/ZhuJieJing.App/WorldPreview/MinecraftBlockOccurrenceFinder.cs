using System.IO;
using System.Security.Cryptography;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.WorldPreview;

internal readonly record struct MinecraftBlockOccurrence(
    MinecraftChunkAddress Chunk,
    BlockPosition Position);

internal readonly record struct MinecraftBlockOccurrenceSearchProgress(int ChunksScanned, int ChunksTotal);

/// <summary>
/// Finds a real source-world occurrence without materializing the whole world. Chunk order is shuffled and read
/// in small parallel windows, so repeated requests produce useful random destinations while remaining cancellable.
/// </summary>
internal static class MinecraftBlockOccurrenceFinder
{
    public static async Task<MinecraftBlockOccurrence?> FindRandomAsync(
        INormalizedMinecraftChunkSource source,
        IReadOnlyList<MinecraftChunkAddress> addresses,
        string sourcePattern,
        MinecraftDimensionId preferredDimension,
        int loadConcurrency,
        IProgress<MinecraftBlockOccurrenceSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(addresses);
        if(string.IsNullOrWhiteSpace(sourcePattern))
            throw new ArgumentException("方块标识不能为空。", nameof(sourcePattern));
        if(loadConcurrency is < NormalizedMinecraftChunkBatcher.MinimumLoadConcurrency or
           > NormalizedMinecraftChunkBatcher.MaximumLoadConcurrency)
            throw new ArgumentOutOfRangeException(nameof(loadConcurrency));

        MinecraftChunkAddress[] preferred = addresses
            .Where(address => address.Dimension == preferredDimension)
            .ToArray();
        MinecraftChunkAddress[] remaining = addresses
            .Where(address => address.Dimension != preferredDimension)
            .ToArray();
        Shuffle(preferred);
        Shuffle(remaining);
        MinecraftChunkAddress[] randomized = preferred.Concat(remaining).ToArray();
        int processed = 0;

        while(processed < randomized.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(loadConcurrency, randomized.Length - processed);
            Task<NormalizedMinecraftChunk?>[] reads = new Task<NormalizedMinecraftChunk?>[count];
            for(int index = 0; index < count; index++)
            {
                MinecraftChunkAddress address = randomized[processed + index];
                reads[index] = source.FindAsync(address, cancellationToken).AsTask();
            }

            NormalizedMinecraftChunk?[] chunks = await Task.WhenAll(reads).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            MinecraftBlockOccurrence[] candidates = chunks
                .Where(static chunk => chunk is not null)
                .Select(chunk => FindRandomInChunk(chunk!, sourcePattern, cancellationToken))
                .Where(static occurrence => occurrence is not null)
                .Select(static occurrence => occurrence!.Value)
                .ToArray();
            processed += count;
            progress?.Report(new MinecraftBlockOccurrenceSearchProgress(processed, randomized.Length));
            if(candidates.Length > 0)
                return candidates[RandomNumberGenerator.GetInt32(candidates.Length)];
        }

        return null;
    }

    private static MinecraftBlockOccurrence? FindRandomInChunk(
        NormalizedMinecraftChunk chunk,
        string sourcePattern,
        CancellationToken cancellationToken)
    {
        MinecraftBlockOccurrence? selected = null;
        int matchCount = 0;
        foreach(NormalizedMinecraftSection section in chunk.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<BlockState> palette = section.Palette;
            bool[] matchingPalette = new bool[palette.Count];
            bool sectionCanMatch = false;
            for(int paletteIndex = 0; paletteIndex < palette.Count; paletteIndex++)
            {
                bool matches = MatchesSource(sourcePattern, palette[paletteIndex]);
                matchingPalette[paletteIndex] = matches;
                sectionCanMatch |= matches;
            }
            if(!sectionCanMatch) continue;

            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            if(indices.Length != 4096)
            {
                throw new InvalidDataException(
                    $"Chunk {chunk.Address} 的 Section {section.Coordinate} 包含 {indices.Length} 个方块索引，预期为 4096。");
            }
            for(int localIndex = 0; localIndex < indices.Length; localIndex++)
            {
                if((localIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                ushort paletteIndex = indices[localIndex];
                if(paletteIndex >= matchingPalette.Length)
                    throw new InvalidDataException($"Section {section.Coordinate} 的调色板索引 {paletteIndex} 越界。");
                if(!matchingPalette[paletteIndex]) continue;

                matchCount = checked(matchCount + 1);
                if(RandomNumberGenerator.GetInt32(matchCount) != 0) continue;
                int x = checked(section.Coordinate.X * 16 + (localIndex & 15));
                int y = checked(section.Coordinate.Y * 16 + (localIndex >> 8 & 15));
                int z = checked(section.Coordinate.Z * 16 + (localIndex >> 4 & 15));
                selected = new MinecraftBlockOccurrence(chunk.Address, new BlockPosition(x, y, z));
            }
        }
        return selected;
    }

    private static bool MatchesSource(string sourcePattern, BlockState state)
    {
        if(sourcePattern.Contains('[', StringComparison.Ordinal))
            return string.Equals(sourcePattern, state.CanonicalKey, StringComparison.Ordinal);
        int wildcard = sourcePattern.IndexOf('*');
        if(wildcard < 0) return string.Equals(sourcePattern, state.Name, StringComparison.Ordinal);
        return state.Name.StartsWith(sourcePattern[..wildcard], StringComparison.Ordinal) &&
               state.Name.EndsWith(sourcePattern[(wildcard + 1)..], StringComparison.Ordinal);
    }

    private static void Shuffle(Span<MinecraftChunkAddress> addresses)
    {
        for(int index = addresses.Length - 1; index > 0; index--)
        {
            int other = RandomNumberGenerator.GetInt32(index + 1);
            (addresses[index], addresses[other]) = (addresses[other], addresses[index]);
        }
    }
}
