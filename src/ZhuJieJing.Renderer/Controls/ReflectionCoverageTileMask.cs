namespace ZhuJieJing.Renderer.Controls;

/// <summary>A conservative work mask. Reflection colors are still shaded at the original full resolution.</summary>
internal sealed class ReflectionCoverageTileMask
{
    internal const int TileSize = 16;
    internal int Width { get; }
    internal int Height { get; }
    internal int Columns { get; }
    internal int Rows { get; }
    internal byte[] Pixels { get; }
    internal RefractionCopyBounds PixelBounds { get; private set; }
    internal bool HasUncoveredTiles => Pixels.AsSpan().Contains((byte)0);

    internal ReflectionCoverageTileMask(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width; Height = height;
        Columns = checked((width - 1) / TileSize + 1);
        Rows = checked((height - 1) / TileSize + 1);
        Pixels = new byte[checked(Columns * Rows)];
    }

    internal void Include(RefractionCopyBounds sampledPixels)
    {
        if(sampledPixels.IsEmpty) return;
        var clipped = new RefractionCopyBounds(Math.Clamp(sampledPixels.Left, 0, Width),
            Math.Clamp(sampledPixels.Top, 0, Height), Math.Clamp(sampledPixels.Right, 0, Width), Math.Clamp(sampledPixels.Bottom, 0, Height));
        if(clipped.IsEmpty) return;
        PixelBounds = RefractionCopyBounds.Union(PixelBounds, clipped);
        int left = clipped.Left / TileSize, right = (clipped.Right - 1) / TileSize;
        int top = clipped.Top / TileSize, bottom = (clipped.Bottom - 1) / TileSize;
        for(int row = top; row <= bottom; row++) Pixels.AsSpan(row * Columns + left, right - left + 1).Fill(byte.MaxValue);
    }

    internal void Clear()
    {
        Pixels.AsSpan().Clear();
        PixelBounds = default;
    }
}
