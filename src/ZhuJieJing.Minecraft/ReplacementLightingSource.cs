using System.Globalization;
using System.Runtime.CompilerServices;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Lighting-only view of the replacement transaction, including unmodified neighbour chunks.</summary>
internal sealed class ReplacementLightingSource : INormalizedMinecraftChunkSource
{
    private readonly LegacyNormalizedMinecraftChunkSource source;
    private readonly MinecraftBlockReplacementRequest request;
    private readonly Dictionary<ushort, BlockState> paletteCache = [];

    internal ReplacementLightingSource(IReadOnlyMinecraftWorld world, MinecraftBlockReplacementRequest request)
    {
        source = new(world);
        this.request = request;
    }

    public string Revision => source.Revision + ":replacement-lighting:" + request.Source.State.CanonicalKey + ":" + request.Target.State.CanonicalKey;
    public IReadOnlyList<MinecraftDimensionId> Dimensions => source.Dimensions;

    public async ValueTask<NormalizedMinecraftChunk?> FindAsync(MinecraftChunkAddress address, CancellationToken cancellationToken = default)
    {
        var chunk = await source.FindAsync(address, cancellationToken).ConfigureAwait(false);
        return chunk is null ? null : Transform(chunk);
    }

    public async IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateAsync(MinecraftDimensionId dimension, MinecraftChunkBounds? bounds = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach(var chunk in source.EnumerateAsync(dimension, bounds, cancellationToken).ConfigureAwait(false)) yield return Transform(chunk);
    }

    public async IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateRegionAsync(MinecraftRegionAddress region, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach(var chunk in source.EnumerateRegionAsync(region, cancellationToken).ConfigureAwait(false)) yield return Transform(chunk);
    }

    internal static LegacyBlockEncoding ResolveLightingEncoding(BlockState state) => state.IsAir ? new(0, 0)
        : new(ushort.Parse(state.Properties["numeric_id"], CultureInfo.InvariantCulture), 0);

    private NormalizedMinecraftChunk Transform(NormalizedMinecraftChunk chunk)
    {
        List<NormalizedMinecraftSection> sections = [];
        foreach(var section in chunk.Sections)
        {
            if(section.SourceLegacyStates.Length != 4096) throw new InvalidDataException("光照重建缺少原始旧版 ID 数据。");
            List<BlockState> palette = [];
            Dictionary<BlockState, ushort> lookup = [];
            ushort[] indices = new ushort[4096];
            for(int i = 0; i < indices.Length; i++)
            {
                ushort code = section.SourceLegacyStates.Span[i];
                if(!paletteCache.TryGetValue(code, out BlockState? state))
                {
                    LegacyBlockEncoding encoding = new((ushort)(code >> 4), (byte)(code & 15));
                    BlockState canonical = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(encoding.NumericId, encoding.Metadata).State;
                    var block = new MinecraftReplacementBlock(canonical, 0, encoding);
                    if(MinecraftWorldBlockReplacement.Matches(block, request)) encoding = request.ResolveTarget(block).LegacyEncoding!.Value;
                    state = encoding.NumericId == 0 ? BlockState.Air : new BlockState("zhujiejing:lighting_block", new Dictionary<string, string> { ["numeric_id"] = encoding.NumericId.ToString(CultureInfo.InvariantCulture) });
                    paletteCache.Add(code, state);
                }
                if(!lookup.TryGetValue(state, out ushort index)) { index = (ushort)palette.Count; palette.Add(state); lookup.Add(state, index); }
                indices[i] = index;
            }
            sections.Add(section with { Palette = palette, PaletteIndices = indices, Lighting = null });
        }
        return chunk with { Sections = sections };
    }
}
