using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Meshing;
using ZhuJieJing.Renderer.Minecraft;

namespace ZhuJieJing.App.Resources;

/// <summary>
/// Lazily creates small, immutable WPF previews from the same blockstate, model, texture and tint resolution
/// used by the viewport. Each resource family is opened only when its first thumbnail is requested.
/// </summary>
public sealed class MinecraftBlockThumbnailProvider : IDisposable
{
    private const string LegacyJarName = "legacy-1.12.2.jar";
    private const string ModernJarName = "modern-1.21.10.jar";
    private const int MaximumTexturePixelSize = 16;
    private const int AtlasTilesPerRow = 32;
    private const int MaximumStatesPerRendererSession = 192;
    private const int StateCacheCapacity = 256;
    private const int VisualCacheCapacity = 192;
    private static readonly BlockFace[] PreferredFaces =
    [
        BlockFace.North,
        BlockFace.South,
        BlockFace.East,
        BlockFace.West,
        BlockFace.Up,
        BlockFace.Down,
    ];

    private readonly object gate = new();
    private readonly ResourceFamilyContext legacy;
    private readonly ResourceFamilyContext modern;
    private bool disposed;

    private MinecraftBlockThumbnailProvider(
        ResourceFamilyContext legacy,
        ResourceFamilyContext modern,
        int thumbnailPixelSize)
    {
        this.legacy = legacy;
        this.modern = modern;
        ThumbnailPixelSize = thumbnailPixelSize;
        MissingThumbnail = CreateCheckerThumbnail(thumbnailPixelSize, (255, 0, 220), (28, 28, 28), 255);
        EmptyThumbnail = CreateCheckerThumbnail(thumbnailPixelSize, (238, 242, 249), (207, 216, 229), 176);
    }

    public int ThumbnailPixelSize { get; }

    public bool HasLegacyResources
    {
        get
        {
            lock(gate) return legacy.CanOpen;
        }
    }

    public bool HasModernResources
    {
        get
        {
            lock(gate) return modern.CanOpen;
        }
    }

    public string? LegacyResourceError
    {
        get
        {
            lock(gate) return legacy.Error;
        }
    }

    public string? ModernResourceError
    {
        get
        {
            lock(gate) return modern.Error;
        }
    }

    public ImageSource MissingThumbnail { get; }

    public ImageSource EmptyThumbnail { get; }

    public static async Task<MinecraftBlockThumbnailProvider> CreateConfiguredAsync(int thumbnailPixelSize = 32, CancellationToken cancellationToken = default)
    {
        var service = new MinecraftResourceCacheService();
        var legacy = await service.ResolveAsync(AppContext.BaseDirectory, MinecraftTargetProfile.Java1122.Version, cancellationToken: cancellationToken).ConfigureAwait(false);
        var modern = await service.ResolveAsync(AppContext.BaseDirectory, new MinecraftVersionDescriptor(4556, "1.21.10", MinecraftStorageFamily.ModernSectionPalette), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new MinecraftBlockThumbnailProvider(
            legacy.ClientJarPath is { } oldPath ? ResourceFamilyContext.Create(oldPath) : ResourceFamilyContext.Unavailable(legacy.Status),
            modern.ClientJarPath is { } newPath ? ResourceFamilyContext.Create(newPath) : ResourceFamilyContext.Unavailable(modern.Status), thumbnailPixelSize);
    }

    /// <summary>
    /// Records the two bundled client paths without opening or indexing either JAR. A missing or invalid family
    /// independently falls back to the frozen checker placeholder when a thumbnail is requested.
    /// </summary>
    public static MinecraftBlockThumbnailProvider CreateBundled(
        string? bundledResourceDirectory = null,
        int thumbnailPixelSize = 32)
    {
        if(thumbnailPixelSize is < 8 or > 64) throw new ArgumentOutOfRangeException(nameof(thumbnailPixelSize));
        string directory;
        try
        {
            directory = Path.GetFullPath(
                bundledResourceDirectory ?? Path.Combine(AppContext.BaseDirectory, "resources", "minecraft"));
        }
        catch(Exception exception)
        {
            string error = SafeErrorMessage(exception);
            return new MinecraftBlockThumbnailProvider(
                ResourceFamilyContext.Unavailable(error),
                ResourceFamilyContext.Unavailable(error),
                thumbnailPixelSize);
        }

        return new MinecraftBlockThumbnailProvider(
            ResourceFamilyContext.Create(Path.Combine(directory, LegacyJarName)),
            ResourceFamilyContext.Create(Path.Combine(directory, ModernJarName)),
            thumbnailPixelSize);
    }

    public ImageSource GetLegacyThumbnail(Minecraft1122LegacyMappingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return GetLegacyThumbnail(target.LegacyEncoding);
    }

    public ImageSource GetLegacyThumbnail(LegacyBlockEncoding encoding)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                MinecraftRegistryResolution resolution = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(
                    encoding.NumericId,
                    encoding.Metadata);
                if(resolution.Source == MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder)
                    return MissingThumbnail;
                return GetThumbnail(legacy, resolution.State);
            }
            catch
            {
                return MissingThumbnail;
            }
        }
    }

    public ImageSource GetModernThumbnail(BlockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return GetThumbnail(modern, state);
        }
    }

    public ImageSource GetModernThumbnail(string canonicalSourceKey)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if(TryParseCanonicalState(canonicalSourceKey, out BlockState? state))
                return GetThumbnail(modern, state!);
            BlockState? representative = modern.GetRepresentativeState(canonicalSourceKey);
            return representative is null ? MissingThumbnail : GetThumbnail(modern, representative);
        }
    }

    public void Dispose()
    {
        lock(gate)
        {
            if(disposed) return;
            disposed = true;
            legacy.Dispose();
            modern.Dispose();
        }
    }

    private ImageSource GetThumbnail(ResourceFamilyContext context, BlockState state)
    {
        if(context.StateCache.TryGet(state.CanonicalKey, out ImageSource cached)) return cached;

        ImageSource thumbnail;
        if(state.IsAir)
        {
            thumbnail = EmptyThumbnail;
        }
        else
        {
            try
            {
                Minecraft1122BlockRenderResources? resources = context.GetResourcesFor(
                    state.CanonicalKey,
                    Math.Min(ThumbnailPixelSize, MaximumTexturePixelSize));
                if(resources is null)
                {
                    thumbnail = MissingThumbnail;
                }
                else
                {
                    thumbnail = CreateThumbnail(context, resources, state);
                    if(ReferenceEquals(thumbnail, MissingThumbnail) && state.Properties.Count == 0)
                    {
                        BlockState? representative = context.GetRepresentativeState(state.Name);
                        if(representative is not null && !representative.Equals(state))
                            thumbnail = CreateThumbnail(context, resources, representative);
                        if(ReferenceEquals(thumbnail, MissingThumbnail) && representative is not null)
                            thumbnail = CreateDirectTextureThumbnail(context, resources, representative);
                    }
                    if(ReferenceEquals(thumbnail, MissingThumbnail))
                        thumbnail = CreateDirectTextureThumbnail(context, resources, state);
                }
            }
            catch
            {
                thumbnail = MissingThumbnail;
            }
        }

        context.StateCache.Set(state.CanonicalKey, thumbnail);
        return thumbnail;
    }

    private ImageSource CreateThumbnail(
        ResourceFamilyContext context,
        Minecraft1122BlockRenderResources resources,
        BlockState state)
    {
        BlockRenderDefinition? definition = resources.Resolve(state);
        if(definition is null) return MissingThumbnail;
        if(definition.GeometryKind is BlockGeometryKind.Empty or BlockGeometryKind.Invisible)
            return EmptyThumbnail;

        foreach(BlockFace face in PreferredFaces)
        {
            BlockFaceMaterial[] materials = resources.ResolveFaceLayers(state, face)
                .Where(static material => !material.IsFallback)
                .ToArray();
            if(materials.Length == 0) continue;

            string visualKey = BuildVisualKey(materials);
            if(context.VisualCache.TryGet(visualKey, out ImageSource cached)) return cached;
            ImageSource thumbnail = ComposeThumbnail(resources.Atlas, materials);
            context.VisualCache.Set(visualKey, thumbnail);
            return thumbnail;
        }
        return MissingThumbnail;
    }

    private ImageSource CreateDirectTextureThumbnail(
        ResourceFamilyContext context,
        Minecraft1122BlockRenderResources resources,
        BlockState state)
    {
        DirectTextureCandidate? candidate = context.GetDirectTexture(state);
        if(candidate is null) return MissingThumbnail;
        MinecraftTextureTile tile = candidate.Crop is DirectTextureCrop crop
            ? resources.Atlas.GetOrAddPngRegion(
                candidate.ResourcePath,
                () => context.OpenResource(candidate.ResourcePath),
                crop.X,
                crop.Y,
                crop.Width,
                crop.Height,
                crop.LogicalWidth,
                crop.LogicalHeight)
            : resources.Atlas.GetOrAddPng(
                candidate.ResourcePath,
                () => context.OpenResource(candidate.ResourcePath));
        if(tile.IsFallback) return MissingThumbnail;
        return ComposeThumbnail(
            resources.Atlas,
            [new BlockFaceMaterial(tile.Region, System.Numerics.Vector4.One, candidate.ResourcePath)]);
    }

    private ImageSource ComposeThumbnail(MinecraftTextureAtlas atlas, IReadOnlyList<BlockFaceMaterial> materials)
    {
        int length = checked(ThumbnailPixelSize * ThumbnailPixelSize * 4);
        byte[] composed = new byte[length];
        foreach(BlockFaceMaterial material in materials)
        {
            MinecraftTextureTileSnapshot tile = atlas.SnapshotTile(material.AtlasRegion);
            CompositeScaled(composed, ThumbnailPixelSize, tile, material.Tint);
        }

        BitmapSource bitmap = BitmapSource.Create(
            ThumbnailPixelSize,
            ThumbnailPixelSize,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            composed,
            checked(ThumbnailPixelSize * 4));
        RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.NearestNeighbor);
        bitmap.Freeze();
        return bitmap;
    }

    private static void CompositeScaled(
        byte[] destination,
        int destinationSize,
        MinecraftTextureTileSnapshot source,
        System.Numerics.Vector4 tint)
    {
        float tintRed = Math.Clamp(tint.X, 0f, 1f);
        float tintGreen = Math.Clamp(tint.Y, 0f, 1f);
        float tintBlue = Math.Clamp(tint.Z, 0f, 1f);
        float tintAlpha = Math.Clamp(tint.W, 0f, 1f);
        for(var y = 0; y < destinationSize; y++)
        {
            int sourceY = y * source.Height / destinationSize;
            for(var x = 0; x < destinationSize; x++)
            {
                int sourceX = x * source.Width / destinationSize;
                int sourceOffset = sourceY * source.RowPitch + sourceX * 4;
                int destinationOffset = (y * destinationSize + x) * 4;
                float sourceAlpha = source.BgraPixels[sourceOffset + 3] / 255f * tintAlpha;
                if(sourceAlpha <= 0f) continue;
                float destinationAlpha = destination[destinationOffset + 3] / 255f;
                float outputAlpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);

                float sourceBlue = source.BgraPixels[sourceOffset] * tintBlue;
                float sourceGreen = source.BgraPixels[sourceOffset + 1] * tintGreen;
                float sourceRed = source.BgraPixels[sourceOffset + 2] * tintRed;
                destination[destinationOffset] = Blend(
                    sourceBlue,
                    destination[destinationOffset],
                    sourceAlpha,
                    destinationAlpha,
                    outputAlpha);
                destination[destinationOffset + 1] = Blend(
                    sourceGreen,
                    destination[destinationOffset + 1],
                    sourceAlpha,
                    destinationAlpha,
                    outputAlpha);
                destination[destinationOffset + 2] = Blend(
                    sourceRed,
                    destination[destinationOffset + 2],
                    sourceAlpha,
                    destinationAlpha,
                    outputAlpha);
                destination[destinationOffset + 3] = ToByte(outputAlpha * 255f);
            }
        }
    }

    private static byte Blend(
        float source,
        byte destination,
        float sourceAlpha,
        float destinationAlpha,
        float outputAlpha)
    {
        if(outputAlpha <= 0f) return 0;
        return ToByte((source * sourceAlpha + destination * destinationAlpha * (1f - sourceAlpha)) / outputAlpha);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static string BuildVisualKey(IReadOnlyList<BlockFaceMaterial> materials)
    {
        var builder = new StringBuilder(materials.Count * 48);
        foreach(BlockFaceMaterial material in materials)
        {
            builder.Append(material.TextureKey ?? "@solid").Append(';')
                .Append(BitConverter.SingleToInt32Bits(material.Tint.X)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(material.Tint.Y)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(material.Tint.Z)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(material.Tint.W)).Append('|');
        }
        return builder.ToString();
    }

    private static ImageSource CreateCheckerThumbnail(
        int pixelSize,
        (byte Red, byte Green, byte Blue) first,
        (byte Red, byte Green, byte Blue) second,
        byte alpha)
    {
        int stride = checked(pixelSize * 4);
        byte[] pixels = new byte[checked(stride * pixelSize)];
        int cellSize = Math.Max(pixelSize / 4, 1);
        for(var y = 0; y < pixelSize; y++)
        {
            for(var x = 0; x < pixelSize; x++)
            {
                var color = ((x / cellSize) + (y / cellSize)) % 2 == 0 ? first : second;
                int offset = y * stride + x * 4;
                pixels[offset] = color.Blue;
                pixels[offset + 1] = color.Green;
                pixels[offset + 2] = color.Red;
                pixels[offset + 3] = alpha;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            pixelSize,
            pixelSize,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.NearestNeighbor);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool TryParseCanonicalState(string? value, out BlockState? state)
    {
        state = null;
        if(string.IsNullOrWhiteSpace(value)) return false;
        string canonical = value.Trim();
        if(canonical.Contains('*', StringComparison.Ordinal) || canonical.Any(char.IsWhiteSpace)) return false;

        int propertiesStart = canonical.IndexOf('[', StringComparison.Ordinal);
        string name = propertiesStart < 0 ? canonical : canonical[..propertiesStart];
        int namespaceSeparator = name.IndexOf(':', StringComparison.Ordinal);
        if(namespaceSeparator <= 0 || namespaceSeparator != name.LastIndexOf(':') || namespaceSeparator == name.Length - 1)
            return false;
        try
        {
            if(propertiesStart < 0)
            {
                state = new BlockState(name);
                return true;
            }
            if(!canonical.EndsWith(']') || propertiesStart == canonical.Length - 2) return false;

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach(string pair in canonical[(propertiesStart + 1)..^1].Split(','))
            {
                int separator = pair.IndexOf('=', StringComparison.Ordinal);
                if(separator <= 0 || separator == pair.Length - 1 || separator != pair.LastIndexOf('=')) return false;
                if(!properties.TryAdd(pair[..separator], pair[(separator + 1)..])) return false;
            }
            state = new BlockState(name, properties);
            return true;
        }
        catch(ArgumentException)
        {
            state = null;
            return false;
        }
    }

    private static string SafeErrorMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

    private sealed record DirectTextureCandidate(string ResourcePath, DirectTextureCrop? Crop = null);

    private readonly record struct DirectTextureCrop(
        int X,
        int Y,
        int Width,
        int Height,
        int LogicalWidth,
        int LogicalHeight);

    private sealed class ResourceFamilyContext : IDisposable
    {
        private readonly string? jarPath;
        private readonly HashSet<string> rendererStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BlockState?> representativeStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DirectTextureCandidate?> directTextures = new(StringComparer.Ordinal);
        private Minecraft1122BlockRenderResources? resources;
        private IReadOnlyList<string>? blockStatePaths;
        private IReadOnlyList<string>? texturePaths;
        private HashSet<string>? texturePathSet;

        private ResourceFamilyContext(string? jarPath, string? error)
        {
            this.jarPath = jarPath;
            Error = error;
            StateCache = new BoundedImageCache(StateCacheCapacity);
            VisualCache = new BoundedImageCache(VisualCacheCapacity);
        }

        public bool CanOpen => jarPath is not null && Error is null;

        public string? Error { get; private set; }

        public BoundedImageCache StateCache { get; }

        public BoundedImageCache VisualCache { get; }

        public static ResourceFamilyContext Create(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                return File.Exists(fullPath)
                    ? new ResourceFamilyContext(fullPath, null)
                    : Unavailable($"Minecraft 资源不存在：{fullPath}");
            }
            catch(Exception exception)
            {
                return Unavailable(SafeErrorMessage(exception));
            }
        }

        public static ResourceFamilyContext Unavailable(string error) => new(null, error);

        public BlockState? GetRepresentativeState(string sourcePattern)
        {
            if(representativeStates.TryGetValue(sourcePattern, out BlockState? cached)) return cached;
            BlockState? result = null;
            try
            {
                EnsureResourceIndex();
                if(TryGetMinecraftPath(sourcePattern, out string requestedPath))
                {
                    string? selectedPath = requestedPath.Contains('*', StringComparison.Ordinal)
                        ? blockStatePaths!.FirstOrDefault(candidate => PatternMatches(requestedPath, candidate))
                        : blockStatePaths!.Contains(requestedPath, StringComparer.Ordinal)
                            ? requestedPath
                            : null;
                    if(selectedPath is not null)
                    {
                        IReadOnlyDictionary<string, string> properties = ReadRepresentativeProperties(selectedPath);
                        result = new BlockState($"minecraft:{selectedPath}", properties);
                    }
                }
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException or JsonException or ArgumentException or
                                             NotSupportedException)
            {
            }
            representativeStates[sourcePattern] = result;
            return result;
        }

        public DirectTextureCandidate? GetDirectTexture(BlockState state)
        {
            if(directTextures.TryGetValue(state.CanonicalKey, out DirectTextureCandidate? cached)) return cached;
            DirectTextureCandidate? result = null;
            try
            {
                EnsureResourceIndex();
                if(TryGetMinecraftPath(state.Name, out string path))
                    result = FindDirectTexture(path);
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException or ArgumentException or NotSupportedException)
            {
            }
            directTextures[state.CanonicalKey] = result;
            return result;
        }

        public Stream OpenResource(string resourcePath)
        {
            if(jarPath is null) throw new InvalidOperationException("Minecraft 资源不可用。");
            using FileStream file = new(
                jarPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry entry = archive.GetEntry(resourcePath) ??
                throw new FileNotFoundException($"Minecraft 资源中不存在 {resourcePath}。", resourcePath);
            using Stream input = entry.Open();
            var copy = new MemoryStream(checked((int)Math.Min(entry.Length, int.MaxValue)));
            input.CopyTo(copy);
            copy.Position = 0;
            return copy;
        }

        public Minecraft1122BlockRenderResources? GetResourcesFor(string stateKey, int texturePixelSize)
        {
            if(!CanOpen) return null;
            if(resources is not null &&
               !rendererStates.Contains(stateKey) &&
               rendererStates.Count >= MaximumStatesPerRendererSession)
            {
                DropRenderer();
            }

            if(resources is null)
            {
                try
                {
                    resources = Minecraft1122BlockRenderResources.Open(
                        new Minecraft1122ResourceConfiguration(
                            jarPath!,
                            AtlasTileSize: texturePixelSize,
                            AtlasTilesPerRow: AtlasTilesPerRow));
                }
                catch(Exception exception)
                {
                    Error = SafeErrorMessage(exception);
                    DropRenderer();
                    return null;
                }
            }

            rendererStates.Add(stateKey);
            return resources;
        }

        public void Dispose()
        {
            DropRenderer();
            StateCache.Clear();
            VisualCache.Clear();
            representativeStates.Clear();
            directTextures.Clear();
            blockStatePaths = null;
            texturePaths = null;
            texturePathSet = null;
        }

        private void EnsureResourceIndex()
        {
            if(blockStatePaths is not null && texturePaths is not null && texturePathSet is not null) return;
            if(jarPath is null)
            {
                blockStatePaths = [];
                texturePaths = [];
                texturePathSet = new HashSet<string>(StringComparer.Ordinal);
                return;
            }

            using FileStream file = new(
                jarPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            blockStatePaths = archive.Entries
                .Select(static entry => entry.FullName)
                .Where(static path => path.StartsWith("assets/minecraft/blockstates/", StringComparison.Ordinal) &&
                                      path.EndsWith(".json", StringComparison.Ordinal))
                .Select(static path => path[29..^5])
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            texturePaths = archive.Entries
                .Select(static entry => entry.FullName)
                .Where(static path => path.StartsWith("assets/minecraft/textures/", StringComparison.Ordinal) &&
                                      path.EndsWith(".png", StringComparison.Ordinal))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            texturePathSet = new HashSet<string>(texturePaths, StringComparer.Ordinal);
        }

        private IReadOnlyDictionary<string, string> ReadRepresentativeProperties(string blockStatePath)
        {
            using Stream stream = OpenResource($"assets/minecraft/blockstates/{blockStatePath}.json");
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if(!document.RootElement.TryGetProperty("variants", out JsonElement variants) ||
               variants.ValueKind != JsonValueKind.Object) return properties;

            string? variantKey = null;
            foreach(JsonProperty variant in variants.EnumerateObject())
            {
                variantKey = variant.Name;
                break;
            }
            if(string.IsNullOrEmpty(variantKey) || string.Equals(variantKey, "normal", StringComparison.Ordinal))
                return properties;
            foreach(string pair in variantKey.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = pair.IndexOf('=', StringComparison.Ordinal);
                if(separator <= 0 || separator == pair.Length - 1) continue;
                string value = pair[(separator + 1)..];
                int alternative = value.IndexOf('|', StringComparison.Ordinal);
                properties[pair[..separator]] = alternative < 0 ? value : value[..alternative];
            }
            return properties;
        }

        private DirectTextureCandidate? FindDirectTexture(string path)
        {
            if(TryGetHeadTexture(path, out DirectTextureCandidate? head) && head is not null &&
               texturePathSet!.Contains(head.ResourcePath)) return head;
            if(path.EndsWith("_shulker_box", StringComparison.Ordinal))
            {
                string color = path[..^12];
                if(string.Equals(color, "light_gray", StringComparison.Ordinal)) color = "silver";
                var shulker = new DirectTextureCandidate(
                    $"assets/minecraft/textures/entity/shulker/shulker_{color}.png",
                    new DirectTextureCrop(16, 0, 16, 16, 64, 64));
                if(texturePathSet!.Contains(shulker.ResourcePath)) return shulker;
            }
            if(path.EndsWith("_bed", StringComparison.Ordinal))
            {
                string color = path[..^4];
                var bed = new DirectTextureCandidate($"assets/minecraft/textures/entity/bed/{color}.png");
                if(texturePathSet!.Contains(bed.ResourcePath)) return bed;
            }
            if(TryGetCopperOxidation(path, "copper_chest", out string chestOxidation))
            {
                string texture = chestOxidation.Length == 0 ? "copper" : $"copper_{chestOxidation}";
                var chest = new DirectTextureCandidate(
                    $"assets/minecraft/textures/entity/chest/{texture}.png",
                    new DirectTextureCrop(14, 0, 14, 14, 64, 64));
                if(texturePathSet!.Contains(chest.ResourcePath)) return chest;
            }
            if(TryGetCopperOxidation(path, "copper_golem_statue", out string statueOxidation))
            {
                string texture = statueOxidation.Length == 0 ? "copper_block" : $"{statueOxidation}_copper";
                var statue = new DirectTextureCandidate($"assets/minecraft/textures/block/{texture}.png");
                if(texturePathSet!.Contains(statue.ResourcePath)) return statue;
            }

            string? specialPath = path switch
            {
                "moving_piston" => "assets/minecraft/textures/blocks/piston_side.png",
                "purpur_slab" => "assets/minecraft/textures/blocks/purpur_block.png",
                "end_portal" => "assets/minecraft/textures/entity/end_portal.png",
                "end_gateway" => "assets/minecraft/textures/entity/end_gateway_beam.png",
                _ => null,
            };
            if(specialPath is not null && texturePathSet!.Contains(specialPath))
                return new DirectTextureCandidate(specialPath);

            var aliases = new List<string> { path };
            if(string.Equals(path, "chain", StringComparison.Ordinal)) aliases.Add("iron_chain");
            if(string.Equals(path, "bubble_column", StringComparison.Ordinal)) aliases.Add("water_still");
            if(path.Contains("_wall_", StringComparison.Ordinal))
                aliases.Add(path.Replace("_wall_", "_", StringComparison.Ordinal));
            if(path.StartsWith("attached_", StringComparison.Ordinal)) aliases.Add(path[9..]);
            if(path.EndsWith("_plant", StringComparison.Ordinal)) aliases.Add(path[..^6]);

            string[] blockRoots =
            [
                "assets/minecraft/textures/block/",
                "assets/minecraft/textures/blocks/",
            ];
            foreach(string alias in aliases.Distinct(StringComparer.Ordinal))
            {
                foreach(string root in blockRoots)
                {
                    string exact = $"{root}{alias}.png";
                    if(texturePathSet!.Contains(exact)) return new DirectTextureCandidate(exact);
                }
            }

            foreach(string alias in aliases.Distinct(StringComparer.Ordinal))
            {
                foreach(string root in blockRoots)
                {
                    string prefix = $"{root}{alias}_";
                    string? match = texturePaths!
                        .Where(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal))
                        .OrderBy(TexturePreference)
                        .ThenBy(static candidate => candidate, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if(match is not null) return new DirectTextureCandidate(match);
                }
            }

            string[] itemRoots =
            [
                "assets/minecraft/textures/item/",
                "assets/minecraft/textures/items/",
            ];
            foreach(string alias in aliases.Distinct(StringComparer.Ordinal))
            {
                foreach(string root in itemRoots)
                {
                    string exact = $"{root}{alias}.png";
                    if(texturePathSet!.Contains(exact)) return new DirectTextureCandidate(exact);
                }
            }
            return null;
        }

        private static bool TryGetHeadTexture(string path, out DirectTextureCandidate? candidate)
        {
            string? kind = path.EndsWith("_wall_head", StringComparison.Ordinal)
                ? path[..^10]
                : path.EndsWith("_wall_skull", StringComparison.Ordinal)
                    ? path[..^11]
                    : path.EndsWith("_head", StringComparison.Ordinal)
                        ? path[..^5]
                        : path.EndsWith("_skull", StringComparison.Ordinal)
                            ? path[..^6]
                            : null;
            candidate = kind switch
            {
                "creeper" => Head("assets/minecraft/textures/entity/creeper/creeper.png", 64, 32),
                "skeleton" => Head("assets/minecraft/textures/entity/skeleton/skeleton.png", 64, 32),
                "wither_skeleton" => Head("assets/minecraft/textures/entity/skeleton/wither_skeleton.png", 64, 32),
                "zombie" => Head("assets/minecraft/textures/entity/zombie/zombie.png", 64, 64),
                "piglin" => Head("assets/minecraft/textures/entity/piglin/piglin.png", 64, 64),
                "player" => Head("assets/minecraft/textures/entity/player/wide/steve.png", 64, 64),
                "dragon" => new DirectTextureCandidate("assets/minecraft/textures/entity/enderdragon/dragon.png"),
                _ => null,
            };
            return candidate is not null;

            static DirectTextureCandidate Head(string resourcePath, int logicalWidth, int logicalHeight) =>
                new(resourcePath, new DirectTextureCrop(8, 8, 8, 8, logicalWidth, logicalHeight));
        }

        private static int TexturePreference(string path)
        {
            if(path.EndsWith("_side.png", StringComparison.Ordinal)) return 0;
            if(path.EndsWith("_top.png", StringComparison.Ordinal)) return 1;
            if(path.EndsWith("_front.png", StringComparison.Ordinal)) return 2;
            if(path.EndsWith("_empty.png", StringComparison.Ordinal)) return 3;
            return 4;
        }

        private static bool TryGetCopperOxidation(string path, string baseName, out string oxidation)
        {
            string candidate = path.StartsWith("waxed_", StringComparison.Ordinal) ? path[6..] : path;
            if(string.Equals(candidate, baseName, StringComparison.Ordinal))
            {
                oxidation = string.Empty;
                return true;
            }
            foreach(string stage in new[] { "exposed", "weathered", "oxidized" })
            {
                if(string.Equals(candidate, $"{stage}_{baseName}", StringComparison.Ordinal))
                {
                    oxidation = stage;
                    return true;
                }
            }
            oxidation = string.Empty;
            return false;
        }

        private static bool TryGetMinecraftPath(string source, out string path)
        {
            const string Namespace = "minecraft:";
            if(!source.StartsWith(Namespace, StringComparison.Ordinal))
            {
                path = string.Empty;
                return false;
            }
            int properties = source.IndexOf('[', Namespace.Length);
            path = properties < 0 ? source[Namespace.Length..] : source[Namespace.Length..properties];
            return path.Length > 0;
        }

        private static bool PatternMatches(string pattern, string value)
        {
            int wildcard = pattern.IndexOf('*');
            if(wildcard < 0) return string.Equals(pattern, value, StringComparison.Ordinal);
            return value.StartsWith(pattern[..wildcard], StringComparison.Ordinal) &&
                   value.EndsWith(pattern[(wildcard + 1)..], StringComparison.Ordinal);
        }

        private void DropRenderer()
        {
            Minecraft1122BlockRenderResources? toDispose = resources;
            resources = null;
            rendererStates.Clear();
            try
            {
                toDispose?.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed class BoundedImageCache
    {
        private readonly int capacity;
        private readonly Dictionary<string, CacheEntry> entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> recency = new();

        public BoundedImageCache(int capacity)
        {
            this.capacity = capacity;
        }

        public bool TryGet(string key, out ImageSource image)
        {
            if(!entries.TryGetValue(key, out CacheEntry? entry))
            {
                image = null!;
                return false;
            }
            recency.Remove(entry.Node);
            recency.AddFirst(entry.Node);
            image = entry.Image;
            return true;
        }

        public void Set(string key, ImageSource image)
        {
            if(entries.TryGetValue(key, out CacheEntry? existing))
            {
                recency.Remove(existing.Node);
                recency.AddFirst(existing.Node);
                entries[key] = new CacheEntry(image, existing.Node);
                return;
            }

            var node = recency.AddFirst(key);
            entries.Add(key, new CacheEntry(image, node));
            if(entries.Count <= capacity) return;
            LinkedListNode<string>? oldest = recency.Last;
            if(oldest is null) return;
            recency.RemoveLast();
            entries.Remove(oldest.Value);
        }

        public void Clear()
        {
            entries.Clear();
            recency.Clear();
        }

        private sealed record CacheEntry(ImageSource Image, LinkedListNode<string> Node);
    }
}
