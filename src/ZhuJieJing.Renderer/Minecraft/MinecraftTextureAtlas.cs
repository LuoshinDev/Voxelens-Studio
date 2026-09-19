using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZhuJieJing.Renderer.Meshing;

namespace ZhuJieJing.Renderer.Minecraft;

public readonly record struct MinecraftTextureTile(
    TextureAtlasRegion Region,
    bool HasTransparentPixels,
    bool HasTranslucentPixels,
    bool IsFallback);

public sealed record MinecraftTextureAtlasSnapshot(
    int Width,
    int Height,
    int RowPitch,
    int Revision,
    byte[] BgraPixels,
    int TileSize = 32,
    int LogicalTilesPerRow = 0);

public sealed class MinecraftTextureAtlasCapacityException(string message) : InvalidOperationException(message);

/// <summary>A compact copy of one decoded atlas tile for UI previews and other CPU-side consumers.</summary>
public sealed record MinecraftTextureTileSnapshot(
    int Width,
    int Height,
    int RowPitch,
    byte[] BgraPixels);

/// <summary>
/// On-demand physical atlas with stable logical slots. Shaders and thumbnails map the logical slot to the
/// current physical layout, so packing more rows or preserving a larger source never invalidates mesh UVs.
/// </summary>
public sealed class MinecraftTextureAtlas
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MinecraftTextureTile> _tiles = new(StringComparer.Ordinal);
    private byte[] _pixels;
    private readonly Dictionary<int, DecodedTexture> _decodedTiles = [];
    private readonly Dictionary<int, TextureAnimation> _animations = [];
    private int _nextSlot = 2;
    private int _revision = 1;
    private int _updateBatchDepth;
    private bool _updateBatchChanged;
    public const int MaximumTextureDimension = 16384;
    public const long MaximumBaseLevelBytes = 1024L * 1024 * 1024;

    public MinecraftTextureAtlas(int tileSize = 32, int tilesPerRow = 64)
    {
        if(tileSize is < 8 or > 8192 || (tileSize & (tileSize - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
        if(tilesPerRow is < 2 or > 128) throw new ArgumentOutOfRangeException(nameof(tilesPerRow));

        TileSize = tileSize;
        TilesPerRow = tilesPerRow;
        (Width, Height) = CalculateLayout(tileSize, tilesPerRow, 2);
        _pixels = new byte[checked(Width * Height * 4)];
        FillChecker(0, 255, 0, 220, 16, 16, 16);
        FillSolid(1, 255, 255, 255, 255);
        MissingTile = CreateTile(0, false, false, true);
        WhiteTile = CreateTile(1, false, false, false);
    }

    public int TileSize { get; private set; }

    public int TilesPerRow { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Capacity => Math.Min(checked(TilesPerRow * TilesPerRow),
        checked((Width / TileSize) * Math.Min(MaximumTextureDimension / TileSize, (int)(MaximumBaseLevelBytes / (Width * (long)TileSize * 4))))) - 2;

    public int LoadedTextureCount
    {
        get
        {
            lock(_gate) return _tiles.Count(pair => !pair.Value.IsFallback);
        }
    }

    public int Revision
    {
        get
        {
            lock(_gate) return _revision;
        }
    }

    public MinecraftTextureTile MissingTile { get; }

    public MinecraftTextureTile WhiteTile { get; }

    /// <summary>
    /// Coalesces all successful tile additions in this scope into one externally visible revision. Pixel writes
    /// remain immediate so mesh generation can use stable UV slots, while the renderer keeps the last complete
    /// GPU atlas until the outermost scope finishes.
    /// </summary>
    public IDisposable BeginBatchUpdate()
    {
        lock(_gate) _updateBatchDepth = checked(_updateBatchDepth + 1);
        return new BatchUpdateScope(this);
    }

    public MinecraftTextureTile GetOrAddPng(string resourceKey, Func<Stream> openRead)
    {
        return GetOrAdd(resourceKey, openRead, null);
    }

    public MinecraftTextureTile GetOrAddPng(string resourceKey, Func<Stream> openRead, Func<Stream>? openAnimationMetadata)
    {
        MinecraftTextureTile tile = GetOrAddPng(resourceKey, openRead);
        if(openAnimationMetadata is null) return tile;
        lock(_gate)
        {
            int slot = (int)MathF.Round(tile.Region.V * TilesPerRow) * TilesPerRow + (int)MathF.Round(tile.Region.U * TilesPerRow);
            if(!tile.IsFallback && !_animations.ContainsKey(slot))
            {
                using Stream stream = openRead();
                TextureAnimation? animation = TextureAnimation.Load(stream, openAnimationMetadata);
                if(animation is not null) _animations[slot] = animation;
            }
        }
        return tile;
    }

    public MinecraftTextureTile GetOrAddPngRegion(
        string resourceKey,
        Func<Stream> openRead,
        int x,
        int y,
        int width,
        int height)
    {
        return GetOrAddPngRegion(resourceKey, openRead, x, y, width, height, 64, 64);
    }

    public MinecraftTextureTile GetOrAddPngRegion(
        string resourceKey,
        Func<Stream> openRead,
        int x,
        int y,
        int width,
        int height,
        int logicalTextureWidth,
        int logicalTextureHeight)
    {
        if(x < 0) throw new ArgumentOutOfRangeException(nameof(x));
        if(y < 0) throw new ArgumentOutOfRangeException(nameof(y));
        if(width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if(height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if(logicalTextureWidth <= 0) throw new ArgumentOutOfRangeException(nameof(logicalTextureWidth));
        if(logicalTextureHeight <= 0) throw new ArgumentOutOfRangeException(nameof(logicalTextureHeight));
        if(width > logicalTextureWidth || x > logicalTextureWidth - width) throw new ArgumentOutOfRangeException(nameof(width));
        if(height > logicalTextureHeight || y > logicalTextureHeight - height) throw new ArgumentOutOfRangeException(nameof(height));
        return GetOrAdd(
            $"{resourceKey}#region={x},{y},{width},{height}@{logicalTextureWidth}x{logicalTextureHeight}",
            openRead,
            new TextureCrop(x, y, width, height, logicalTextureWidth, logicalTextureHeight));
    }

    private MinecraftTextureTile GetOrAdd(string cacheKey, Func<Stream> openRead, TextureCrop? crop)
    {
        if(string.IsNullOrWhiteSpace(cacheKey)) throw new ArgumentException("资源键不能为空。", nameof(cacheKey));
        ArgumentNullException.ThrowIfNull(openRead);
        lock(_gate)
        {
            if(_tiles.TryGetValue(cacheKey, out var cached)) return cached;
            if(_nextSlot >= TilesPerRow * TilesPerRow)
            {
                throw new MinecraftTextureAtlasCapacityException($"当前材质需要超过 {Capacity} 个图集槽位，已停止加载以保留原始纹理质量；请减少同时使用的材质或拆分场景。");
            }

            try
            {
                using var stream = openRead();
                ArgumentNullException.ThrowIfNull(stream);
                var decoded = Decode(stream, crop);
                EnsureTileResolution(Math.Max(decoded.Width, decoded.Height), _nextSlot + 1);
                var slot = _nextSlot++;
                _decodedTiles[slot] = decoded;
                CopyScaled(decoded, slot);
                var tile = CreateTile(slot, decoded.HasTransparentPixels, decoded.HasTranslucentPixels, false);
                _tiles.Add(cacheKey, tile);
                MarkPixelsChanged();
                return tile;
            }
            catch(Exception exception) when(exception is IOException or InvalidDataException or NotSupportedException or FileFormatException)
            {
                _tiles.Add(cacheKey, MissingTile);
                return MissingTile;
            }
        }
    }

    public MinecraftTextureAtlasSnapshot Snapshot()
    {
        lock(_gate)
        {
            return new MinecraftTextureAtlasSnapshot(Width, Height, Width * 4, _revision, (byte[])_pixels.Clone(), TileSize, TilesPerRow);
        }
    }

    /// <summary>
    /// Copies only the tile addressed by an atlas region. This avoids cloning the complete GPU atlas when a
    /// lightweight UI thumbnail needs the already decoded Minecraft texture.
    /// </summary>
    public MinecraftTextureTileSnapshot SnapshotTile(TextureAtlasRegion region)
    {
        lock(_gate)
        {
            int slot = LogicalSlot(region);
            int sourceX = slot % (Width / TileSize) * TileSize;
            int sourceY = slot / (Width / TileSize) * TileSize;
            if(sourceX < 0 || sourceX > Width - TileSize ||
               sourceY < 0 || sourceY > Height - TileSize ||
               sourceX % TileSize != 0 || sourceY % TileSize != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(region), "纹理区域没有指向完整的图集槽位。");
            }

            int rowPitch = checked(TileSize * 4);
            byte[] tilePixels = GC.AllocateUninitializedArray<byte>(checked(rowPitch * TileSize));
            for(var y = 0; y < TileSize; y++)
            {
                int sourceOffset = checked(((sourceY + y) * Width + sourceX) * 4);
                Buffer.BlockCopy(_pixels, sourceOffset, tilePixels, y * rowPitch, rowPitch);
            }
            return new MinecraftTextureTileSnapshot(TileSize, TileSize, rowPitch, tilePixels);
        }
    }

    /// <summary>
    /// Captures a complete atlas generation only after the outermost render batch has finished. A renderer must keep
    /// its previously published GPU texture while this returns false; otherwise it can upload a half-written batch
    /// under the previous revision and expose that partial texture to an already visible Section mesh.
    /// </summary>
    public bool TrySnapshotAfterRevision(int publishedRevision, out MinecraftTextureAtlasSnapshot snapshot)
    {
        lock(_gate)
        {
            if(_updateBatchDepth != 0 || _revision == publishedRevision)
            {
                snapshot = null!;
                return false;
            }
            snapshot = new MinecraftTextureAtlasSnapshot(Width, Height, Width * 4, _revision, (byte[])_pixels.Clone(), TileSize, TilesPerRow);
            return true;
        }
    }

    private void MarkPixelsChanged()
    {
        if(_updateBatchDepth > 0)
        {
            _updateBatchChanged = true;
            return;
        }
        _revision = checked(_revision + 1);
    }

    private void EndBatchUpdate()
    {
        lock(_gate)
        {
            if(_updateBatchDepth <= 0) throw new InvalidOperationException("Minecraft 纹理图集批处理作用域失配。");
            _updateBatchDepth--;
            if(_updateBatchDepth != 0 || !_updateBatchChanged) return;
            _updateBatchChanged = false;
            _revision = checked(_revision + 1);
        }
    }

    private static DecodedTexture Decode(Stream stream, TextureCrop? crop)
    {
        // ZipArchiveEntry streams are non-seekable; WPF's decoder can silently return its 1x1 placeholder
        // for them, so buffer this one requested PNG before decoding it.
        using var seekable = new MemoryStream();
        stream.CopyTo(seekable);
        seekable.Position = 0;
        var decoder = BitmapDecoder.Create(seekable, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if(decoder.Frames.Count == 0) throw new InvalidDataException("PNG 不包含图像帧。");
        BitmapSource source = decoder.Frames[0];
        if(source.PixelWidth <= 0 || source.PixelHeight <= 0) throw new InvalidDataException("PNG 尺寸无效。");
        if(source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            source = converted;
        }

        // Block animation strips are vertical, while entity textures use explicit rectangular regions.
        int sourceX = crop is null ? 0 : ScaleLogicalBoundary(crop.Value.X, source.PixelWidth, crop.Value.LogicalTextureWidth);
        int sourceY = crop is null ? 0 : ScaleLogicalBoundary(crop.Value.Y, source.PixelHeight, crop.Value.LogicalTextureHeight);
        int sourceRight = crop is null
            ? source.PixelWidth
            : ScaleLogicalBoundary(crop.Value.X + crop.Value.Width, source.PixelWidth, crop.Value.LogicalTextureWidth);
        int sourceBottom = crop is null
            ? Math.Min(source.PixelWidth, source.PixelHeight)
            : ScaleLogicalBoundary(crop.Value.Y + crop.Value.Height, source.PixelHeight, crop.Value.LogicalTextureHeight);
        int sourceWidth = Math.Max(sourceRight - sourceX, 1);
        int sourceHeight = Math.Max(sourceBottom - sourceY, 1);
        sourceWidth = Math.Min(sourceWidth, source.PixelWidth - sourceX);
        sourceHeight = Math.Min(sourceHeight, source.PixelHeight - sourceY);
        var stride = checked(source.PixelWidth * 4);
        var allPixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(allPixels, stride, 0);
        var croppedStride = checked(sourceWidth * 4);
        var pixels = new byte[checked(croppedStride * sourceHeight)];
        bool transparent = false;
        bool translucent = false;
        for(var y = 0; y < sourceHeight; y++)
        {
            int sourceOffset = checked((sourceY + y) * stride + sourceX * 4);
            int destinationOffset = y * croppedStride;
            Array.Copy(allPixels, sourceOffset, pixels, destinationOffset, croppedStride);
            for(var x = 0; x < sourceWidth; x++)
            {
                byte alpha = pixels[destinationOffset + x * 4 + 3];
                transparent |= alpha == 0;
                translucent |= alpha is > 0 and < 255;
            }
        }
        return new DecodedTexture(sourceWidth, sourceHeight, croppedStride, pixels, transparent, translucent);
    }

    private static int ScaleLogicalBoundary(int position, int physicalSize, int logicalSize) =>
        checked((int)((long)position * physicalSize / logicalSize));

    private void CopyScaled(DecodedTexture source, int slot)
    {
        var destinationX = slot % (Width / TileSize) * TileSize;
        var destinationY = slot / (Width / TileSize) * TileSize;
        for(var y = 0; y < TileSize; y++)
        {
            int sourceY = Math.Min(source.Height - 1, y * source.Height / TileSize);
            for(var x = 0; x < TileSize; x++)
            {
                int sourceX = Math.Min(source.Width - 1, x * source.Width / TileSize);
                int from = sourceY * source.Stride + sourceX * 4;
                int to = ((destinationY + y) * Width + destinationX + x) * 4;
                _pixels[to] = source.Pixels[from];
                _pixels[to + 1] = source.Pixels[from + 1];
                _pixels[to + 2] = source.Pixels[from + 2];
                _pixels[to + 3] = source.Pixels[from + 3];
            }
        }
    }

    internal static (int Width, int Height) CalculateLayout(int tileSize, int logicalTilesPerRow, int usedSlots)
    {
        int columns = Math.Min(logicalTilesPerRow, MaximumTextureDimension / tileSize);
        if(columns < 1) throw CapacityError(tileSize, usedSlots);
        int rows = 1;
        while(rows < (usedSlots + columns - 1) / columns) rows *= 2;
        long width = (long)columns * tileSize, height = (long)rows * tileSize;
        if(width > MaximumTextureDimension || height > MaximumTextureDimension || width * height * 4 > MaximumBaseLevelBytes)
            throw CapacityError(tileSize, usedSlots);
        return ((int)width, (int)height);
    }

    private static MinecraftTextureAtlasCapacityException CapacityError(int tileSize, int usedSlots) => new(
        $"保留 {tileSize}×{tileSize} 原始材质的 {usedSlots} 个槽位将超过图集上限（边长 16384、基础像素 1 GiB）。已停止加载，没有降低纹理分辨率；请减少同时使用的材质或拆分场景。");

    private int LogicalSlot(TextureAtlasRegion region) => (int)MathF.Round(region.V * TilesPerRow) * TilesPerRow + (int)MathF.Round(region.U * TilesPerRow);

    private void EnsureTileResolution(int sourceSize, int usedSlots)
    {
        int required = TileSize;
        while(required < sourceSize) required = checked(required * 2);
        (int width, int height) = CalculateLayout(required, TilesPerRow, usedSlots);
        if(required == TileSize && width == Width && height == Height) return;
        byte[] replacement = new byte[checked(width * height * 4)];
        TileSize = required;
        Width = width;
        Height = height;
        _pixels = replacement;
        FillChecker(0, 255, 0, 220, 16, 16, 16);
        FillSolid(1, 255, 255, 255, 255);
        foreach((int slot, DecodedTexture source) in _decodedTiles) CopyScaled(source, slot);
    }

    public IReadOnlyList<MinecraftTextureAnimationUpdate> AdvanceAnimations(double seconds, int? expectedRevision = null)
    {
        lock(_gate)
        {
            if(_updateBatchDepth != 0 || expectedRevision.HasValue && expectedRevision.Value != _revision) return [];
            var updates = new List<MinecraftTextureAnimationUpdate>();
            foreach((int slot, TextureAnimation animation) in _animations)
            {
                byte[]? pixels = animation.FrameAt(seconds);
                if(pixels is null) continue;
                var source = new DecodedTexture(animation.Size, animation.Size, animation.Size * 4, pixels, true, true);
                _decodedTiles[slot] = source;
                CopyScaled(source, slot);
                int x = slot % (Width / TileSize) * TileSize, y = slot / (Width / TileSize) * TileSize;
                byte[] scaled = new byte[TileSize * TileSize * 4];
                for(int row = 0; row < TileSize; row++) Buffer.BlockCopy(_pixels, ((y + row) * Width + x) * 4, scaled, row * TileSize * 4, TileSize * 4);
                updates.Add(new MinecraftTextureAnimationUpdate(x, y, TileSize, scaled));
            }
            return updates;
        }
    }

    private MinecraftTextureTile CreateTile(int slot, bool transparent, bool translucent, bool fallback)
    {
        float size = 1f / TilesPerRow;
        float u = slot % TilesPerRow * size;
        float v = slot / TilesPerRow * size;
        return new MinecraftTextureTile(
            new TextureAtlasRegion(u, v, size, size),
            transparent,
            translucent,
            fallback);
    }

    private void FillSolid(int slot, byte red, byte green, byte blue, byte alpha) =>
        FillChecker(slot, red, green, blue, red, green, blue, alpha);

    private void FillChecker(
        int slot,
        byte redA,
        byte greenA,
        byte blueA,
        byte redB,
        byte greenB,
        byte blueB,
        byte alpha = 255)
    {
        var startX = slot % (Width / TileSize) * TileSize;
        var startY = slot / (Width / TileSize) * TileSize;
        var cell = Math.Max(TileSize / 4, 1);
        for(var y = 0; y < TileSize; y++)
        {
            for(var x = 0; x < TileSize; x++)
            {
                bool first = ((x / cell) + (y / cell)) % 2 == 0;
                int target = ((startY + y) * Width + startX + x) * 4;
                _pixels[target] = first ? blueA : blueB;
                _pixels[target + 1] = first ? greenA : greenB;
                _pixels[target + 2] = first ? redA : redB;
                _pixels[target + 3] = alpha;
            }
        }
    }

    private sealed record DecodedTexture(
        int Width,
        int Height,
        int Stride,
        byte[] Pixels,
        bool HasTransparentPixels,
        bool HasTranslucentPixels);

    private readonly record struct TextureCrop(
        int X,
        int Y,
        int Width,
        int Height,
        int LogicalTextureWidth,
        int LogicalTextureHeight);

    private sealed class BatchUpdateScope(MinecraftTextureAtlas owner) : IDisposable
    {
        private MinecraftTextureAtlas? _owner = owner;

        public void Dispose()
        {
            MinecraftTextureAtlas? ownerToRelease = Interlocked.Exchange(ref _owner, null);
            ownerToRelease?.EndBatchUpdate();
        }
    }
}
