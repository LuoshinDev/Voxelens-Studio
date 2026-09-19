using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

internal readonly record struct WorldCropChunkPoint(int X, int Z);

internal sealed record WorldCropChunkRaster(
    int Width,
    int Height,
    int Stride,
    byte[] Pixels,
    int MinimumChunkX,
    int MinimumChunkZ,
    int MaximumChunkX,
    int MaximumChunkZ);

internal sealed record WorldCropChunkRasterPatch(
    int X,
    int Y,
    int Width,
    int Height,
    int Stride,
    byte[] Pixels);

internal sealed record WorldCropPlaneLoadResult(
    WorldCropChunkPlaneSnapshot Snapshot,
    WorldCropChunkPlaneSnapshot ContentSnapshot,
    WorldCropChunkRaster Raster,
    WorldCropSatelliteMap? SatelliteMap = null,
    long SurfaceColumnCount = 0,
    string ResourceStatus = "");

internal sealed class WorldCropChunkPlaneSnapshot
{
    private const int ChunksPerRegion = MinecraftRegionAddress.ChunksPerAxis;

    private readonly IReadOnlyDictionary<long, WorldCropRegionOccupancy> regions;
    private readonly WorldCropChunkRow[] rows;

    private WorldCropChunkPlaneSnapshot(
        MinecraftDimensionId dimension,
        WorldCropChunkRow[] rows,
        IReadOnlyDictionary<long, WorldCropRegionOccupancy> regions,
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ,
        int chunkCount)
    {
        Dimension = dimension;
        this.rows = rows;
        this.regions = regions;
        MinimumX = minimumX;
        MinimumZ = minimumZ;
        MaximumX = maximumX;
        MaximumZ = maximumZ;
        ChunkCount = chunkCount;
    }

    public MinecraftDimensionId Dimension { get; }

    public int MinimumX { get; }

    public int MinimumZ { get; }

    public int MaximumX { get; }

    public int MaximumZ { get; }

    public int ChunkCount { get; }

    public static async Task<WorldCropChunkPlaneSnapshot> LoadAsync(
        IMinecraftChunkIndex chunkIndex,
        MinecraftDimensionId dimension,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIndex);
        List<WorldCropChunkPoint> chunks = new(chunkIndex.GetChunkCount(dimension));
        await foreach(MinecraftChunkIndexEntry entry in chunkIndex
                          .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                          .WithCancellation(cancellationToken)
                          .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunks.Add(new WorldCropChunkPoint(entry.Address.X, entry.Address.Z));
            if((chunks.Count & 4095) == 0) progress?.Report(chunks.Count);
        }

        progress?.Report(chunks.Count);
        return Create(dimension, chunks);
    }

    public static WorldCropChunkPlaneSnapshot Create(
        MinecraftDimensionId dimension,
        IEnumerable<WorldCropChunkPoint> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        Dictionary<int, List<int>> rowBuilders = new();
        foreach(WorldCropChunkPoint chunk in chunks)
        {
            if(!rowBuilders.TryGetValue(chunk.Z, out List<int>? row))
            {
                row = new List<int>();
                rowBuilders.Add(chunk.Z, row);
            }
            row.Add(chunk.X);
        }

        if(rowBuilders.Count == 0)
            return new WorldCropChunkPlaneSnapshot(
                dimension,
                [],
                new Dictionary<long, WorldCropRegionOccupancy>(),
                0,
                0,
                0,
                0,
                0);

        WorldCropChunkRow[] rows = new WorldCropChunkRow[rowBuilders.Count];
        Dictionary<long, WorldCropRegionOccupancy> regions = new();
        int rowIndex = 0;
        int minimumX = int.MaxValue;
        int maximumX = int.MinValue;
        int chunkCount = 0;
        foreach((int z, List<int> rowBuilder) in rowBuilders.OrderBy(static pair => pair.Key))
        {
            rowBuilder.Sort();
            int uniqueCount = CompactDistinct(rowBuilder);
            int[] rowX = rowBuilder.Take(uniqueCount).ToArray();
            rows[rowIndex++] = new WorldCropChunkRow(z, rowX);
            minimumX = Math.Min(minimumX, rowX[0]);
            maximumX = Math.Max(maximumX, rowX[^1]);
            chunkCount = checked(chunkCount + rowX.Length);

            foreach(int x in rowX)
            {
                int regionX = FloorDiv(x, ChunksPerRegion);
                int regionZ = FloorDiv(z, ChunksPerRegion);
                long key = PackCoordinates(regionX, regionZ);
                if(!regions.TryGetValue(key, out WorldCropRegionOccupancy? region))
                {
                    region = new WorldCropRegionOccupancy();
                    regions.Add(key, region);
                }
                region.Add(x - regionX * ChunksPerRegion, z - regionZ * ChunksPerRegion);
            }
        }

        return new WorldCropChunkPlaneSnapshot(
            dimension,
            rows,
            regions,
            minimumX,
            rows[0].Z,
            maximumX,
            rows[^1].Z,
            chunkCount);
    }

    public bool ContainsChunk(int x, int z)
    {
        int regionX = FloorDiv(x, ChunksPerRegion);
        int regionZ = FloorDiv(z, ChunksPerRegion);
        return regions.TryGetValue(PackCoordinates(regionX, regionZ), out WorldCropRegionOccupancy? region) &&
               region.Contains(x - regionX * ChunksPerRegion, z - regionZ * ChunksPerRegion);
    }

    public int CountWithin(MinecraftChunkSquareBounds bounds)
    {
        if(rows.Length == 0) return 0;
        int firstRow = LowerBoundRows(bounds.MinChunkZ);
        int lastRowExclusive = UpperBoundRows(bounds.MaxChunkZ);
        int count = 0;
        for(int index = firstRow; index < lastRowExclusive; index++)
        {
            int[] xCoordinates = rows[index].XCoordinates;
            int firstX = LowerBound(xCoordinates, bounds.MinChunkX);
            int lastXExclusive = UpperBound(xCoordinates, bounds.MaxChunkX);
            count = checked(count + lastXExclusive - firstX);
        }
        return count;
    }

    public bool TryFindClosestWithin(
        MinecraftChunkSquareBounds bounds,
        double targetX,
        double targetZ,
        out WorldCropChunkPoint closest)
    {
        int firstRow = LowerBoundRows(bounds.MinChunkZ);
        int lastRowExclusive = UpperBoundRows(bounds.MaxChunkZ);
        double closestDistanceSquared = double.PositiveInfinity;
        WorldCropChunkPoint best = default;
        bool found = false;
        for(int rowIndex = firstRow; rowIndex < lastRowExclusive; rowIndex++)
        {
            WorldCropChunkRow row = rows[rowIndex];
            int firstX = LowerBound(row.XCoordinates, bounds.MinChunkX);
            int lastXExclusive = UpperBound(row.XCoordinates, bounds.MaxChunkX);
            if(firstX >= lastXExclusive) continue;
            int insertion = LowerBound(row.XCoordinates, ClampRoundToInt(targetX));
            Evaluate(Math.Clamp(insertion, firstX, lastXExclusive - 1));
            Evaluate(Math.Clamp(insertion - 1, firstX, lastXExclusive - 1));

            void Evaluate(int xIndex)
            {
                int x = row.XCoordinates[xIndex];
                double deltaX = x - targetX;
                double deltaZ = row.Z - targetZ;
                double distanceSquared = deltaX * deltaX + deltaZ * deltaZ;
                if(distanceSquared >= closestDistanceSquared) return;
                closestDistanceSquared = distanceSquared;
                best = new WorldCropChunkPoint(x, row.Z);
                found = true;
            }
        }
        closest = best;
        return found;
    }

    public WorldCropChunkRaster CreateRaster(int maximumDimension = 2048)
    {
        if(maximumDimension < 1) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        long chunkWidth = (long)MaximumX - MinimumX + 1L;
        long chunkHeight = (long)MaximumZ - MinimumZ + 1L;
        double ratio = Math.Min(1d, maximumDimension / (double)Math.Max(chunkWidth, chunkHeight));
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(chunkWidth * ratio));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(chunkHeight * ratio));
        int stride = checked(pixelWidth * 4);
        byte[] pixels = new byte[checked(stride * pixelHeight)];

        foreach(WorldCropChunkRow row in rows)
        {
            int pixelZ = ScaleCoordinate(row.Z, MinimumZ, chunkHeight, pixelHeight);
            foreach(int x in row.XCoordinates)
            {
                int pixelX = ScaleCoordinate(x, MinimumX, chunkWidth, pixelWidth);
                int offset = checked(pixelZ * stride + pixelX * 4);
                pixels[offset] = 0xC8;
                pixels[offset + 1] = 0xAF;
                pixels[offset + 2] = 0x96;
                pixels[offset + 3] = 0xFF;
            }
        }

        return new WorldCropChunkRaster(
            pixelWidth,
            pixelHeight,
            stride,
            pixels,
            MinimumX,
            MinimumZ,
            MaximumX,
            MaximumZ);
    }

    private int LowerBoundRows(int z)
    {
        int low = 0;
        int high = rows.Length;
        while(low < high)
        {
            int middle = low + (high - low) / 2;
            if(rows[middle].Z < z) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private int UpperBoundRows(int z)
    {
        int low = 0;
        int high = rows.Length;
        while(low < high)
        {
            int middle = low + (high - low) / 2;
            if(rows[middle].Z <= z) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int LowerBound(int[] values, int target)
    {
        int low = 0;
        int high = values.Length;
        while(low < high)
        {
            int middle = low + (high - low) / 2;
            if(values[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(int[] values, int target)
    {
        int low = 0;
        int high = values.Length;
        while(low < high)
        {
            int middle = low + (high - low) / 2;
            if(values[middle] <= target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int CompactDistinct(List<int> sorted)
    {
        if(sorted.Count == 0) return 0;
        int target = 1;
        for(int source = 1; source < sorted.Count; source++)
        {
            if(sorted[source] == sorted[target - 1]) continue;
            sorted[target++] = sorted[source];
        }
        return target;
    }

    private static int ScaleCoordinate(int value, int minimum, long sourceSize, int targetSize)
    {
        double normalized = ((long)value - minimum) / (double)sourceSize;
        return Math.Clamp((int)Math.Floor(normalized * targetSize), 0, targetSize - 1);
    }

    internal static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static long PackCoordinates(int x, int z) => ((long)x << 32) | (uint)z;

    private static int ClampRoundToInt(double value)
    {
        if(value <= int.MinValue) return int.MinValue;
        if(value >= int.MaxValue) return int.MaxValue;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private sealed record WorldCropChunkRow(int Z, int[] XCoordinates);

    private sealed class WorldCropRegionOccupancy
    {
        private readonly ulong[] words = new ulong[16];

        public void Add(int localX, int localZ)
        {
            int bitIndex = checked(localZ * ChunksPerRegion + localX);
            words[bitIndex >> 6] |= 1UL << (bitIndex & 63);
        }

        public bool Contains(int localX, int localZ)
        {
            int bitIndex = checked(localZ * ChunksPerRegion + localX);
            return (words[bitIndex >> 6] & (1UL << (bitIndex & 63))) != 0UL;
        }
    }
}

internal static class WorldCropSelectionMath
{
    public static MinecraftChunkSquareBounds CreateFromDrag(int anchorX, int anchorZ, int currentX, int currentZ)
        => new(
            Math.Min(anchorX, currentX),
            Math.Min(anchorZ, currentZ),
            Math.Max(anchorX, currentX),
            Math.Max(anchorZ, currentZ));

    public static MinecraftChunkSquareBounds CreateInitial(
        WorldCropChunkPlaneSnapshot snapshot,
        WorldCropChunkPoint? requiredChunk = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int minimumX = snapshot.MinimumX;
        int minimumZ = snapshot.MinimumZ;
        int maximumX = snapshot.MaximumX;
        int maximumZ = snapshot.MaximumZ;
        if(requiredChunk is WorldCropChunkPoint required)
        {
            minimumX = Math.Min(minimumX, required.X);
            minimumZ = Math.Min(minimumZ, required.Z);
            maximumX = Math.Max(maximumX, required.X);
            maximumZ = Math.Max(maximumZ, required.Z);
        }

        return new MinecraftChunkSquareBounds(minimumX, minimumZ, maximumX, maximumZ);
    }

    public static bool TryCreateFromMinimumAndSize(
        int minimumX,
        int minimumZ,
        long width,
        long depth,
        out MinecraftChunkSquareBounds bounds)
    {
        long maximumAllowedWidth = (long)int.MaxValue - minimumX + 1L;
        long maximumAllowedDepth = (long)int.MaxValue - minimumZ + 1L;
        if(width < 1L || depth < 1L || width > maximumAllowedWidth || depth > maximumAllowedDepth)
        {
            bounds = default;
            return false;
        }

        long maximumX = (long)minimumX + width - 1L;
        long maximumZ = (long)minimumZ + depth - 1L;
        bounds = new MinecraftChunkSquareBounds(minimumX, minimumZ, (int)maximumX, (int)maximumZ);
        return true;
    }

    public static bool TryCreateFromMinimumAndSide(
        int minimumX,
        int minimumZ,
        long sideLength,
        out MinecraftChunkSquareBounds bounds) =>
        TryCreateFromMinimumAndSize(minimumX, minimumZ, sideLength, sideLength, out bounds);

    public static long GetWidth(MinecraftChunkSquareBounds bounds) => bounds.Width;

    public static long GetDepth(MinecraftChunkSquareBounds bounds) => bounds.Depth;

    public static bool Contains(MinecraftChunkSquareBounds bounds, WorldCropChunkPoint chunk) =>
        chunk.X >= bounds.MinChunkX && chunk.X <= bounds.MaxChunkX &&
        chunk.Z >= bounds.MinChunkZ && chunk.Z <= bounds.MaxChunkZ;

    public static int BlockToChunk(int blockCoordinate) => WorldCropChunkPlaneSnapshot.FloorDiv(blockCoordinate, 16);

}

internal sealed class WorldCropChunkPlane : FrameworkElement
{
    private const double AbsoluteMinimumScale = 0.000000001d;
    private const double BlocksPerChunkAxis = 16d;
    private const double MaximumPixelsPerBlock = 2d;
    private const double MaximumDetailScale = BlocksPerChunkAxis * MaximumPixelsPerBlock;
    private const double FitPadding = 34d;
    private const double SelectionHandleRadius = 6d;
    private const double SelectionHandleHitRadius = 12d;
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(31, 27, 39)));
    private static readonly Brush OutsideSelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(96, 10, 8, 14)));
    private static readonly Pen SelectionPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(223, 203, 255)), 2d));
    private static readonly Brush SelectionHandleFillBrush = Freeze(new SolidColorBrush(Color.FromRgb(252, 250, 255)));
    private static readonly Pen SelectionHandlePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(139, 92, 246)), 2d));
    private static readonly Pen RegionGridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(125, 173, 153, 196)), 1d));
    private static readonly Pen ChunkGridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(48, 211, 199, 225)), 1d));
    private static readonly Pen AxisPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(180, 238, 150, 208)), 1.5d));
    private static readonly Pen SpawnPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(255, 194, 86)), 2d));

    private WorldCropChunkPlaneSnapshot? snapshot;
    private WorldCropChunkRaster? overviewRaster;
    private WriteableBitmap? overviewBitmap;
    private WorldCropSatelliteMap? satelliteMap;
    private MinecraftChunkSquareBounds? selection;
    private WorldCropChunkPoint? spawnChunk;
    private double scale = 1d;
    private double centerX = 0.5d;
    private double centerZ = 0.5d;
    private Point dragStartScreen;
    private double dragStartCenterX;
    private double dragStartCenterZ;
    private int selectionAnchorX;
    private int selectionAnchorZ;
    private MinecraftChunkSquareBounds resizeStartSelection;
    private SelectionHandle resizeHandle;
    private double resizePointerOffsetX;
    private double resizePointerOffsetZ;
    private DragMode dragMode;
    private bool fitOnNextArrange;
    private bool userHasInteracted;
    private bool firstSatellitePixelBatchHandled;

    public WorldCropChunkPlane()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SizeChanged += (_, _) =>
        {
            if(fitOnNextArrange) FitWorldCore();
            else if(snapshot is not null)
            {
                (double minimumScale, double maximumScale) = GetScaleLimits();
                if(scale < minimumScale || scale > maximumScale) FitWorldCore();
                else
                {
                    ClampCenter();
                    InvalidateVisual();
                }
            }
            else InvalidateVisual();
        };
    }

    public event EventHandler? SelectionChanged;

    public MinecraftChunkSquareBounds? Selection => selection;

    public void SetSnapshot(
        WorldCropChunkPlaneSnapshot value,
        WorldCropChunkPoint? requiredSpawnChunk,
        WorldCropChunkRaster? prebuiltRaster = null,
        WorldCropSatelliteMap? prebuiltSatelliteMap = null,
        bool fit = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        snapshot = value;
        spawnChunk = requiredSpawnChunk;
        WorldCropChunkRaster raster = prebuiltRaster ?? value.CreateRaster();
        overviewRaster = raster;
        satelliteMap = prebuiltSatelliteMap;
        overviewBitmap = new WriteableBitmap(
            raster.Width,
            raster.Height,
            96d,
            96d,
            PixelFormats.Bgra32,
            null);
        overviewBitmap.WritePixels(
            new Int32Rect(0, 0, raster.Width, raster.Height),
            raster.Pixels,
            raster.Stride,
            0);
        if(fit)
        {
            userHasInteracted = false;
            firstSatellitePixelBatchHandled = prebuiltSatelliteMap is { TileCount: > 0 };
            fitOnNextArrange = true;
            if(ActualWidth > 0d && ActualHeight > 0d) FitWorldCore();
            else InvalidateVisual();
            return;
        }

        fitOnNextArrange = false;
        if(ActualWidth > 0d && ActualHeight > 0d)
        {
            (double minimumScale, double maximumScale) = GetScaleLimits();
            scale = Math.Clamp(scale, minimumScale, maximumScale);
            ClampCenter();
        }
        InvalidateVisual();
    }

    public void ApplySatelliteUpdate(
        IReadOnlyList<WorldCropSatelliteTile> tiles,
        WorldCropChunkRasterPatch? overviewPatch)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(tiles);
        if(overviewPatch is not null && overviewBitmap is not null && overviewRaster is not null)
        {
            if(overviewPatch.X < 0 || overviewPatch.Y < 0 || overviewPatch.Width <= 0 || overviewPatch.Height <= 0 ||
               overviewPatch.X > overviewRaster.Width - overviewPatch.Width ||
               overviewPatch.Y > overviewRaster.Height - overviewPatch.Height ||
               overviewPatch.Stride < overviewPatch.Width * 4 ||
               overviewPatch.Pixels.Length < overviewPatch.Stride * overviewPatch.Height)
                throw new InvalidDataException("卫星图增量总览像素范围无效。");
            overviewBitmap.WritePixels(
                new Int32Rect(
                    overviewPatch.X,
                    overviewPatch.Y,
                    overviewPatch.Width,
                    overviewPatch.Height),
                overviewPatch.Pixels,
                overviewPatch.Stride,
                0);
        }

        bool viewChanged = false;
        if(tiles.Count > 0)
        {
            satelliteMap ??= new WorldCropSatelliteMap([]);
            satelliteMap.Upsert(tiles);
            if(!firstSatellitePixelBatchHandled)
            {
                if(userHasInteracted)
                {
                    firstSatellitePixelBatchHandled = true;
                }
                else if(TryFitSatelliteBatch(tiles))
                {
                    firstSatellitePixelBatchHandled = true;
                    viewChanged = true;
                }
            }
        }
        if(!viewChanged && (overviewPatch is not null || tiles.Count > 0)) InvalidateVisual();
    }

    public void SetSelection(MinecraftChunkSquareBounds bounds, bool fit = false)
    {
        selection = bounds;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if(fit) FitSelection();
        else InvalidateVisual();
    }

    public void FitWorld()
    {
        userHasInteracted = true;
        FitWorldCore();
    }

    private void FitWorldCore()
    {
        if(snapshot is null || ActualWidth <= 0d || ActualHeight <= 0d) return;
        fitOnNextArrange = false;
        (int minimumX, int minimumZ, int maximumX, int maximumZ) = GetViewBounds();
        FitBounds(minimumX, minimumZ, maximumX, maximumZ);
    }

    public void FitSelection()
    {
        userHasInteracted = true;
        FitSelectionCore();
    }

    private void FitSelectionCore()
    {
        if(selection is not MinecraftChunkSquareBounds bounds || ActualWidth <= 0d || ActualHeight <= 0d) return;
        FitBounds(bounds.MinChunkX, bounds.MinChunkZ, bounds.MaxChunkX, bounds.MaxChunkZ);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect viewport = new(0d, 0d, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(BackgroundBrush, null, viewport);
        if(snapshot is null || overviewBitmap is null || overviewRaster is null ||
           ActualWidth <= 0d || ActualHeight <= 0d) return;

        Rect worldRect = ChunkBoundsToScreen(
            overviewRaster.MinimumChunkX,
            overviewRaster.MinimumChunkZ,
            overviewRaster.MaximumChunkX,
            overviewRaster.MaximumChunkZ);
        drawingContext.DrawImage(overviewBitmap, worldRect);
        if(satelliteMap is not null) DrawSatelliteTiles(drawingContext, satelliteMap);
        DrawSelectionMask(drawingContext, viewport);
        DrawCoordinateGrid(drawingContext);
        DrawSelection(drawingContext);
        DrawSpawn(drawingContext);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        userHasInteracted = true;
        Point pointer = e.GetPosition(this);
        (double worldX, double worldZ) = ScreenToWorld(pointer);
        double factor = Math.Pow(1.0015d, e.Delta);
        (double minimumScale, double maximumScale) = GetScaleLimits();
        double nextScale = Math.Clamp(scale * factor, minimumScale, maximumScale);
        if(factor < 1d && nextScale <= minimumScale)
        {
            FitWorldCore();
            e.Handled = true;
            return;
        }
        if(nextScale == scale)
        {
            e.Handled = true;
            return;
        }
        scale = nextScale;
        centerX = worldX - (pointer.X - ActualWidth / 2d) / scale;
        centerZ = worldZ - (pointer.Y - ActualHeight / 2d) / scale;
        ClampCenter();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Point pointer = e.GetPosition(this);
        if(e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            userHasInteracted = true;
            dragMode = DragMode.Pan;
            dragStartScreen = pointer;
            dragStartCenterX = centerX;
            dragStartCenterZ = centerZ;
            Cursor = Cursors.SizeAll;
        }
        else if(e.ChangedButton == MouseButton.Left)
        {
            userHasInteracted = true;
            if(selection is MinecraftChunkSquareBounds bounds &&
               TryHitSelectionHandle(pointer, bounds, out SelectionHandle handle))
            {
                dragMode = DragMode.Resize;
                resizeHandle = handle;
                resizeStartSelection = bounds;
                (double pointerWorldX, double pointerWorldZ) = ScreenToWorld(pointer);
                (double boundaryX, double boundaryZ) = GetSelectionHandleBoundary(bounds, handle);
                resizePointerOffsetX = pointerWorldX - boundaryX;
                resizePointerOffsetZ = pointerWorldZ - boundaryZ;
                Cursor = CursorForSelectionHandle(handle);
            }
            else
            {
                dragMode = DragMode.Select;
                (selectionAnchorX, selectionAnchorZ) = ScreenToChunk(pointer);
                selection = WorldCropSelectionMath.CreateFromDrag(
                    selectionAnchorX,
                    selectionAnchorZ,
                    selectionAnchorX,
                    selectionAnchorZ);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }
        else
        {
            return;
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pointer = e.GetPosition(this);
        if(dragMode == DragMode.None || !IsMouseCaptured)
        {
            UpdatePointerCursor(pointer);
            return;
        }
        if(dragMode == DragMode.Pan)
        {
            centerX = dragStartCenterX - (pointer.X - dragStartScreen.X) / scale;
            centerZ = dragStartCenterZ - (pointer.Y - dragStartScreen.Y) / scale;
            ClampCenter();
        }
        else if(dragMode == DragMode.Resize)
        {
            selection = ResizeSelection(pointer);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            (int currentX, int currentZ) = ScreenToChunk(pointer);
            selection = WorldCropSelectionMath.CreateFromDrag(
                selectionAnchorX,
                selectionAnchorZ,
                currentX,
                currentZ);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if(dragMode == DragMode.None) return;
        dragMode = DragMode.None;
        ReleaseMouseCapture();
        UpdatePointerCursor(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragMode = DragMode.None;
        UpdatePointerCursor(e.GetPosition(this));
    }

    private MinecraftChunkSquareBounds ResizeSelection(Point pointer)
    {
        (double pointerWorldX, double pointerWorldZ) = ScreenToWorld(pointer);
        long boundaryX = ClampRoundToBoundary(pointerWorldX - resizePointerOffsetX);
        long boundaryZ = ClampRoundToBoundary(pointerWorldZ - resizePointerOffsetZ);
        MinecraftChunkSquareBounds start = resizeStartSelection;
        return resizeHandle switch
        {
            SelectionHandle.TopLeft => new MinecraftChunkSquareBounds(
                (int)Math.Min(boundaryX, start.MaxChunkX),
                (int)Math.Min(boundaryZ, start.MaxChunkZ),
                start.MaxChunkX,
                start.MaxChunkZ),
            SelectionHandle.TopRight => new MinecraftChunkSquareBounds(
                start.MinChunkX,
                (int)Math.Min(boundaryZ, start.MaxChunkZ),
                (int)Math.Max(start.MinChunkX, boundaryX - 1L),
                start.MaxChunkZ),
            SelectionHandle.BottomLeft => new MinecraftChunkSquareBounds(
                (int)Math.Min(boundaryX, start.MaxChunkX),
                start.MinChunkZ,
                start.MaxChunkX,
                (int)Math.Max(start.MinChunkZ, boundaryZ - 1L)),
            SelectionHandle.BottomRight => new MinecraftChunkSquareBounds(
                start.MinChunkX,
                start.MinChunkZ,
                (int)Math.Max(start.MinChunkX, boundaryX - 1L),
                (int)Math.Max(start.MinChunkZ, boundaryZ - 1L)),
            _ => start,
        };
    }

    private void UpdatePointerCursor(Point pointer)
    {
        if(selection is MinecraftChunkSquareBounds bounds &&
           TryHitSelectionHandle(pointer, bounds, out SelectionHandle handle))
            Cursor = CursorForSelectionHandle(handle);
        else Cursor = Cursors.Cross;
    }

    private bool TryHitSelectionHandle(
        Point pointer,
        MinecraftChunkSquareBounds bounds,
        out SelectionHandle handle)
    {
        Rect rectangle = ChunkBoundsToScreen(
            bounds.MinChunkX,
            bounds.MinChunkZ,
            bounds.MaxChunkX,
            bounds.MaxChunkZ);
        double bestDistanceSquared = SelectionHandleHitRadius * SelectionHandleHitRadius;
        SelectionHandle bestHandle = SelectionHandle.None;
        Evaluate(SelectionHandle.TopLeft, rectangle.TopLeft);
        Evaluate(SelectionHandle.TopRight, rectangle.TopRight);
        Evaluate(SelectionHandle.BottomLeft, rectangle.BottomLeft);
        Evaluate(SelectionHandle.BottomRight, rectangle.BottomRight);
        handle = bestHandle;
        return bestHandle != SelectionHandle.None;

        void Evaluate(SelectionHandle candidate, Point corner)
        {
            double deltaX = pointer.X - corner.X;
            double deltaY = pointer.Y - corner.Y;
            double distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if(distanceSquared > bestDistanceSquared) return;
            bestDistanceSquared = distanceSquared;
            bestHandle = candidate;
        }
    }

    private static (double X, double Z) GetSelectionHandleBoundary(
        MinecraftChunkSquareBounds bounds,
        SelectionHandle handle) => handle switch
    {
        SelectionHandle.TopLeft => (bounds.MinChunkX, bounds.MinChunkZ),
        SelectionHandle.TopRight => ((long)bounds.MaxChunkX + 1d, bounds.MinChunkZ),
        SelectionHandle.BottomLeft => (bounds.MinChunkX, (long)bounds.MaxChunkZ + 1d),
        SelectionHandle.BottomRight => ((long)bounds.MaxChunkX + 1d, (long)bounds.MaxChunkZ + 1d),
        _ => (bounds.MinChunkX, bounds.MinChunkZ),
    };

    private static Cursor CursorForSelectionHandle(SelectionHandle handle) => handle switch
    {
        SelectionHandle.TopLeft or SelectionHandle.BottomRight => Cursors.SizeNWSE,
        SelectionHandle.TopRight or SelectionHandle.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Cross,
    };

    private static long ClampRoundToBoundary(double value)
    {
        const long maximumExclusiveBoundary = (long)int.MaxValue + 1L;
        if(value <= int.MinValue) return int.MinValue;
        if(value >= maximumExclusiveBoundary) return maximumExclusiveBoundary;
        return (long)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private void FitBounds(int minimumX, int minimumZ, int maximumX, int maximumZ)
    {
        double width = (long)maximumX - minimumX + 1d;
        double height = (long)maximumZ - minimumZ + 1d;
        (double minimumScale, double maximumScale) = GetScaleLimits();
        scale = Math.Clamp(CalculateFitScale(width, height), minimumScale, maximumScale);
        centerX = minimumX + width / 2d;
        centerZ = minimumZ + height / 2d;
        ClampCenter();
        InvalidateVisual();
    }

    private bool TryFitSatelliteBatch(IReadOnlyList<WorldCropSatelliteTile> tiles)
    {
        if(tiles.Count == 0 || ActualWidth <= 0d || ActualHeight <= 0d) return false;
        long minimumChunkX = long.MaxValue;
        long minimumChunkZ = long.MaxValue;
        long maximumChunkX = long.MinValue;
        long maximumChunkZ = long.MinValue;
        foreach(WorldCropSatelliteTile tile in tiles)
        {
            long tileOriginX = (long)tile.TileX * WorldCropSatelliteMap.ChunksPerTile;
            long tileOriginZ = (long)tile.TileZ * WorldCropSatelliteMap.ChunksPerTile;
            long tileMinimumChunkX = tileOriginX + tile.PixelOffsetX / (int)BlocksPerChunkAxis;
            long tileMinimumChunkZ = tileOriginZ + tile.PixelOffsetZ / (int)BlocksPerChunkAxis;
            long tileMaximumChunkX = tileOriginX +
                                     (tile.PixelOffsetX + tile.Bitmap.PixelWidth - 1L) / (int)BlocksPerChunkAxis;
            long tileMaximumChunkZ = tileOriginZ +
                                     (tile.PixelOffsetZ + tile.Bitmap.PixelHeight - 1L) / (int)BlocksPerChunkAxis;
            minimumChunkX = Math.Min(minimumChunkX, tileMinimumChunkX);
            minimumChunkZ = Math.Min(minimumChunkZ, tileMinimumChunkZ);
            maximumChunkX = Math.Max(maximumChunkX, tileMaximumChunkX);
            maximumChunkZ = Math.Max(maximumChunkZ, tileMaximumChunkZ);
        }

        FitBounds(
            (int)Math.Clamp(minimumChunkX, int.MinValue, int.MaxValue),
            (int)Math.Clamp(minimumChunkZ, int.MinValue, int.MaxValue),
            (int)Math.Clamp(maximumChunkX, int.MinValue, int.MaxValue),
            (int)Math.Clamp(maximumChunkZ, int.MinValue, int.MaxValue));
        return true;
    }

    private (double Minimum, double Maximum) GetScaleLimits()
    {
        if(snapshot is null || ActualWidth <= 0d || ActualHeight <= 0d)
            return (AbsoluteMinimumScale, MaximumDetailScale);
        (int minimumX, int minimumZ, int maximumX, int maximumZ) = GetViewBounds();
        double width = (long)maximumX - minimumX + 1d;
        double height = (long)maximumZ - minimumZ + 1d;
        double fitScale = Math.Max(AbsoluteMinimumScale, CalculateFitScale(width, height));
        return (fitScale, Math.Max(MaximumDetailScale, fitScale));
    }

    private double CalculateFitScale(double width, double height)
    {
        double availableWidth = Math.Max(1d, ActualWidth - FitPadding * 2d);
        double availableHeight = Math.Max(1d, ActualHeight - FitPadding * 2d);
        return Math.Min(availableWidth / width, availableHeight / height);
    }

    private (int MinimumX, int MinimumZ, int MaximumX, int MaximumZ) GetViewBounds()
    {
        if(snapshot is null) return (0, 0, 0, 0);
        MinecraftChunkSquareBounds bounds = WorldCropSelectionMath.CreateInitial(snapshot, spawnChunk);
        return (bounds.MinChunkX, bounds.MinChunkZ, bounds.MaxChunkX, bounds.MaxChunkZ);
    }

    private void ClampCenter()
    {
        if(snapshot is null || ActualWidth <= 0d || ActualHeight <= 0d || scale <= 0d) return;
        (int minimumX, int minimumZ, int maximumX, int maximumZ) = GetViewBounds();
        centerX = ClampCenterAxis(centerX, minimumX, (long)maximumX + 1d, ActualWidth);
        centerZ = ClampCenterAxis(centerZ, minimumZ, (long)maximumZ + 1d, ActualHeight);
    }

    private double ClampCenterAxis(double value, double minimum, double maximumExclusive, double viewportSize)
    {
        double contentSize = maximumExclusive - minimum;
        double usableViewportSize = Math.Max(1d, viewportSize - FitPadding * 2d);
        double visibleSize = usableViewportSize / scale;
        double contentCenter = minimum + contentSize / 2d;
        if(contentSize <= visibleSize) return contentCenter;
        double halfVisibleSize = visibleSize / 2d;
        return Math.Clamp(value, minimum + halfVisibleSize, maximumExclusive - halfVisibleSize);
    }

    private void DrawSatelliteTiles(DrawingContext drawingContext, WorldCropSatelliteMap map)
    {
        (double worldMinimumX, double worldMinimumZ) = ScreenToWorld(new Point(0d, 0d));
        (double worldMaximumX, double worldMaximumZ) = ScreenToWorld(new Point(ActualWidth, ActualHeight));
        int minimumTileX = ClampFloorToInt(worldMinimumX / WorldCropSatelliteMap.ChunksPerTile);
        int maximumTileX = ClampFloorToInt(worldMaximumX / WorldCropSatelliteMap.ChunksPerTile);
        int minimumTileZ = ClampFloorToInt(worldMinimumZ / WorldCropSatelliteMap.ChunksPerTile);
        int maximumTileZ = ClampFloorToInt(worldMaximumZ / WorldCropSatelliteMap.ChunksPerTile);
        foreach(WorldCropSatelliteTile tile in map.EnumerateVisible(
                    minimumTileX,
                    minimumTileZ,
                    maximumTileX,
                    maximumTileZ))
        {
            double minimumChunkX = (long)tile.TileX * WorldCropSatelliteMap.ChunksPerTile +
                                   tile.PixelOffsetX / BlocksPerChunkAxis;
            double minimumChunkZ = (long)tile.TileZ * WorldCropSatelliteMap.ChunksPerTile +
                                   tile.PixelOffsetZ / BlocksPerChunkAxis;
            double maximumChunkX = minimumChunkX + tile.Bitmap.PixelWidth / BlocksPerChunkAxis;
            double maximumChunkZ = minimumChunkZ + tile.Bitmap.PixelHeight / BlocksPerChunkAxis;
            Rect tileRect = new(
                WorldToScreenX(minimumChunkX),
                WorldToScreenZ(minimumChunkZ),
                Math.Max(0d, WorldToScreenX(maximumChunkX) - WorldToScreenX(minimumChunkX)),
                Math.Max(0d, WorldToScreenZ(maximumChunkZ) - WorldToScreenZ(minimumChunkZ)));
            drawingContext.DrawImage(tile.Bitmap, tileRect);
        }
    }

    private void DrawSelectionMask(DrawingContext drawingContext, Rect viewport)
    {
        if(selection is not MinecraftChunkSquareBounds bounds) return;
        Rect selectionRect = ChunkBoundsToScreen(
            bounds.MinChunkX,
            bounds.MinChunkZ,
            bounds.MaxChunkX,
            bounds.MaxChunkZ);
        RectangleGeometry viewportGeometry = new(viewport);
        RectangleGeometry selectionGeometry = new(selectionRect);
        CombinedGeometry outside = new(GeometryCombineMode.Exclude, viewportGeometry, selectionGeometry);
        drawingContext.DrawGeometry(OutsideSelectionBrush, null, outside);
    }

    private void DrawCoordinateGrid(DrawingContext drawingContext)
    {
        if(snapshot is null) return;
        (int viewMinimumX, int viewMinimumZ, int viewMaximumX, int viewMaximumZ) = GetViewBounds();
        Rect gridBounds = ChunkBoundsToScreen(viewMinimumX, viewMinimumZ, viewMaximumX, viewMaximumZ);
        Rect clippedGridBounds = Rect.Intersect(new Rect(0d, 0d, ActualWidth, ActualHeight), gridBounds);
        if(clippedGridBounds.IsEmpty) return;
        drawingContext.PushClip(new RectangleGeometry(clippedGridBounds));
        (double worldMinimumX, double worldMinimumZ) = ScreenToWorld(new Point(0d, 0d));
        (double worldMaximumX, double worldMaximumZ) = ScreenToWorld(new Point(ActualWidth, ActualHeight));
        worldMinimumX = Math.Max(worldMinimumX, viewMinimumX);
        worldMinimumZ = Math.Max(worldMinimumZ, viewMinimumZ);
        worldMaximumX = Math.Min(worldMaximumX, (long)viewMaximumX + 1d);
        worldMaximumZ = Math.Min(worldMaximumZ, (long)viewMaximumZ + 1d);
        if(32d * scale >= 12d)
        {
            long firstRegionX = (long)Math.Floor(worldMinimumX / 32d);
            long lastRegionX = (long)Math.Ceiling(worldMaximumX / 32d);
            long firstRegionZ = (long)Math.Floor(worldMinimumZ / 32d);
            long lastRegionZ = (long)Math.Ceiling(worldMaximumZ / 32d);
            for(long regionX = firstRegionX; regionX <= lastRegionX && regionX - firstRegionX < 400L; regionX++)
            {
                double x = WorldToScreenX(regionX * 32d);
                drawingContext.DrawLine(RegionGridPen, new Point(x, 0d), new Point(x, ActualHeight));
            }
            for(long regionZ = firstRegionZ; regionZ <= lastRegionZ && regionZ - firstRegionZ < 400L; regionZ++)
            {
                double y = WorldToScreenZ(regionZ * 32d);
                drawingContext.DrawLine(RegionGridPen, new Point(0d, y), new Point(ActualWidth, y));
            }

            if(32d * scale >= 96d)
                DrawRegionLabels(drawingContext, firstRegionX, lastRegionX, firstRegionZ, lastRegionZ);
        }

        if(scale >= 12d)
        {
            int firstX = ClampFloorToInt(worldMinimumX);
            int lastX = ClampCeilingToInt(worldMaximumX);
            int firstZ = ClampFloorToInt(worldMinimumZ);
            int lastZ = ClampCeilingToInt(worldMaximumZ);
            for(int x = firstX; ; x++)
            {
                double screenX = WorldToScreenX(x);
                drawingContext.DrawLine(ChunkGridPen, new Point(screenX, 0d), new Point(screenX, ActualHeight));
                if(x == lastX) break;
            }
            for(int z = firstZ; ; z++)
            {
                double screenZ = WorldToScreenZ(z);
                drawingContext.DrawLine(ChunkGridPen, new Point(0d, screenZ), new Point(ActualWidth, screenZ));
                if(z == lastZ) break;
            }
        }

        double zeroX = WorldToScreenX(0d);
        if(zeroX >= 0d && zeroX <= ActualWidth)
            drawingContext.DrawLine(AxisPen, new Point(zeroX, 0d), new Point(zeroX, ActualHeight));
        double zeroZ = WorldToScreenZ(0d);
        if(zeroZ >= 0d && zeroZ <= ActualHeight)
            drawingContext.DrawLine(AxisPen, new Point(0d, zeroZ), new Point(ActualWidth, zeroZ));
        drawingContext.Pop();
    }

    private void DrawRegionLabels(
        DrawingContext drawingContext,
        long firstRegionX,
        long lastRegionX,
        long firstRegionZ,
        long lastRegionZ)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int labelCount = 0;
        for(long regionZ = firstRegionZ; regionZ <= lastRegionZ && labelCount < 80; regionZ++)
        {
            for(long regionX = firstRegionX; regionX <= lastRegionX && labelCount < 80; regionX++)
            {
                Point origin = new(WorldToScreenX(regionX * 32d) + 4d, WorldToScreenZ(regionZ * 32d) + 3d);
                if(origin.X < -80d || origin.Y < -20d || origin.X > ActualWidth || origin.Y > ActualHeight) continue;
                FormattedText label = new(
                    $"r.{regionX}.{regionZ}",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    9d,
                    Brushes.White,
                    pixelsPerDip);
                drawingContext.DrawText(label, origin);
                labelCount++;
            }
        }
    }

    private void DrawSelection(DrawingContext drawingContext)
    {
        if(selection is not MinecraftChunkSquareBounds bounds) return;
        Rect selectionRect = ChunkBoundsToScreen(
            bounds.MinChunkX,
            bounds.MinChunkZ,
            bounds.MaxChunkX,
            bounds.MaxChunkZ);
        drawingContext.DrawRectangle(null, SelectionPen, selectionRect);
        drawingContext.DrawEllipse(SelectionHandleFillBrush, SelectionHandlePen, selectionRect.TopLeft, SelectionHandleRadius, SelectionHandleRadius);
        drawingContext.DrawEllipse(SelectionHandleFillBrush, SelectionHandlePen, selectionRect.TopRight, SelectionHandleRadius, SelectionHandleRadius);
        drawingContext.DrawEllipse(SelectionHandleFillBrush, SelectionHandlePen, selectionRect.BottomLeft, SelectionHandleRadius, SelectionHandleRadius);
        drawingContext.DrawEllipse(SelectionHandleFillBrush, SelectionHandlePen, selectionRect.BottomRight, SelectionHandleRadius, SelectionHandleRadius);
    }

    private void DrawSpawn(DrawingContext drawingContext)
    {
        if(spawnChunk is not WorldCropChunkPoint spawn) return;
        Rect cell = ChunkToScreen(spawn.X, spawn.Z);
        Point center = new(cell.Left + cell.Width / 2d, cell.Top + cell.Height / 2d);
        double radius = Math.Clamp(scale * 0.35d, 4d, 10d);
        drawingContext.DrawEllipse(null, SpawnPen, center, radius, radius);
        drawingContext.DrawLine(SpawnPen, new Point(center.X - radius - 3d, center.Y), new Point(center.X + radius + 3d, center.Y));
        drawingContext.DrawLine(SpawnPen, new Point(center.X, center.Y - radius - 3d), new Point(center.X, center.Y + radius + 3d));
    }

    private Rect ChunkBoundsToScreen(int minimumX, int minimumZ, int maximumX, int maximumZ)
    {
        double left = WorldToScreenX(minimumX);
        double top = WorldToScreenZ(minimumZ);
        double right = WorldToScreenX((double)maximumX + 1d);
        double bottom = WorldToScreenZ((double)maximumZ + 1d);
        return new Rect(left, top, Math.Max(0d, right - left), Math.Max(0d, bottom - top));
    }

    private Rect ChunkToScreen(int x, int z) => new(
        WorldToScreenX(x),
        WorldToScreenZ(z),
        scale,
        scale);

    private double WorldToScreenX(double worldX) => (worldX - centerX) * scale + ActualWidth / 2d;

    private double WorldToScreenZ(double worldZ) => (worldZ - centerZ) * scale + ActualHeight / 2d;

    private (double X, double Z) ScreenToWorld(Point screen) =>
        (centerX + (screen.X - ActualWidth / 2d) / scale,
         centerZ + (screen.Y - ActualHeight / 2d) / scale);

    private (int X, int Z) ScreenToChunk(Point screen)
    {
        (double worldX, double worldZ) = ScreenToWorld(screen);
        return (ClampFloorToInt(worldX), ClampFloorToInt(worldZ));
    }

    private static int ClampFloorToInt(double value)
    {
        if(value <= int.MinValue) return int.MinValue;
        if(value >= int.MaxValue) return int.MaxValue;
        return (int)Math.Floor(value);
    }

    private static int ClampCeilingToInt(double value)
    {
        if(value <= int.MinValue) return int.MinValue;
        if(value >= int.MaxValue) return int.MaxValue;
        return (int)Math.Ceiling(value);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private enum DragMode
    {
        None,
        Pan,
        Select,
        Resize,
    }

    private enum SelectionHandle
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }
}
