namespace ZhuJieJing.Renderer.Controls;

/// <summary>
/// Tracks where an opaque-scene snapshot is older than the color target, in render-pixel tiles.
/// Recreate for each planned draw sequence; it deliberately owns no GPU state or frame history.
/// </summary>
internal sealed class RefractionSnapshotTracker
{
    private readonly bool[] _dirty;
    private readonly int _columns;

    internal RefractionSnapshotTracker(int width, int height, int tileSize = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
        Width = width;
        Height = height;
        TileSize = tileSize;
        _columns = (width - 1) / tileSize + 1;
        int rows = (height - 1) / tileSize + 1;
        _dirty = new bool[checked(_columns * rows)];
        Array.Fill(_dirty, true);
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int TileSize { get; }

    internal void Reset() => Array.Fill(_dirty, true);

    internal bool RequiresCopy(RefractionCopyBounds readRect)
    {
        RefractionCopyBounds clipped = Clip(readRect);
        if(clipped.IsEmpty) return false;
        int firstX = clipped.Left / TileSize, lastX = (clipped.Right - 1) / TileSize;
        int firstY = clipped.Top / TileSize, lastY = (clipped.Bottom - 1) / TileSize;
        for(int y = firstY; y <= lastY; y++)
            for(int x = firstX; x <= lastX; x++)
                if(_dirty[y * _columns + x]) return true;
        return false;
    }

    /// <summary>Return the actual copy rectangle needed to make all intersecting tiles clean.</summary>
    internal RefractionCopyBounds AlignCopyBounds(RefractionCopyBounds rect)
    {
        RefractionCopyBounds clipped = Clip(rect);
        if(clipped.IsEmpty) return default;
        return new(clipped.Left / TileSize * TileSize, clipped.Top / TileSize * TileSize,
            (int)Math.Min(((long)(clipped.Right - 1) / TileSize + 1) * TileSize, Width),
            (int)Math.Min(((long)(clipped.Bottom - 1) / TileSize + 1) * TileSize, Height));
    }

    /// <summary>
    /// Record an actual completed copy. Partial tile copies must remain dirty: another pixel in that
    /// same tile can still contain an older snapshot even though this rectangle itself was refreshed.
    /// </summary>
    internal void MarkCopied(RefractionCopyBounds rect)
    {
        RefractionCopyBounds clipped = Clip(rect);
        if(clipped.IsEmpty) return;
        int firstX = clipped.Left / TileSize, lastX = (clipped.Right - 1) / TileSize;
        int firstY = clipped.Top / TileSize, lastY = (clipped.Bottom - 1) / TileSize;
        for(int y = firstY; y <= lastY; y++)
        {
            int top = y * TileSize;
            int bottom = (int)Math.Min((long)top + TileSize, Height);
            if(clipped.Top > top || clipped.Bottom < bottom) continue;
            for(int x = firstX; x <= lastX; x++)
            {
                int left = x * TileSize;
                int right = (int)Math.Min((long)left + TileSize, Width);
                if(clipped.Left <= left && clipped.Right >= right) _dirty[y * _columns + x] = false;
            }
        }
    }

    internal void MarkDrawn(RefractionCopyBounds writeRect)
    {
        RefractionCopyBounds clipped = Clip(writeRect);
        if(clipped.IsEmpty) return;
        int firstX = clipped.Left / TileSize, lastX = (clipped.Right - 1) / TileSize;
        int firstY = clipped.Top / TileSize, lastY = (clipped.Bottom - 1) / TileSize;
        for(int y = firstY; y <= lastY; y++)
            _dirty.AsSpan(y * _columns + firstX, lastX - firstX + 1).Fill(true);
    }

    private RefractionCopyBounds Clip(RefractionCopyBounds rect)
    {
        if(rect.IsEmpty) return default;
        int left = Math.Clamp(rect.Left, 0, Width), top = Math.Clamp(rect.Top, 0, Height);
        int right = Math.Clamp(rect.Right, 0, Width), bottom = Math.Clamp(rect.Bottom, 0, Height);
        return right <= left || bottom <= top ? default : new(left, top, right, bottom);
    }
}
