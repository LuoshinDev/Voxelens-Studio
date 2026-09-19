using System.Numerics;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.WorldPreview;

public static class WorldPreviewNavigation
{
    public const int MinimumRadiusChunks = 1;
    public const int MaximumRadiusChunks = 20;
    public const int DefaultRadiusChunks = 3;
    public const int DefaultChunkCount = 49;
    public const int ReloadMarginChunks = 1;
    public const int MaximumPrefetchLeadChunks = 6;

    public static int ChunkCountForRadius(int radiusChunks)
    {
        if(radiusChunks is < MinimumRadiusChunks or > MaximumRadiusChunks)
            throw new ArgumentOutOfRangeException(nameof(radiusChunks));
        int diameter = checked(radiusChunks * 2 + 1);
        return checked(diameter * diameter);
    }

    public static int RadiusForChunkCount(int chunkCount)
    {
        for(int radius = MinimumRadiusChunks; radius <= MaximumRadiusChunks; radius++)
        {
            if(ChunkCountForRadius(radius) == chunkCount) return radius;
        }
        throw new ArgumentOutOfRangeException(nameof(chunkCount), "区块数量必须对应受支持的正方形视野窗口。");
    }

    public static int NormalizeChunkCount(int chunkCount)
    {
        try
        {
            _ = RadiusForChunkCount(chunkCount);
            return chunkCount;
        }
        catch(ArgumentOutOfRangeException)
        {
            return DefaultChunkCount;
        }
    }

    /// <summary>Enumerates a square preview window from its center outwards so the useful view appears first.</summary>
    public static IReadOnlyList<MinecraftChunkAddress> CreateCenterFirstAddresses(
        MinecraftDimensionId dimension,
        BlockPosition focus,
        int radiusChunks)
    {
        if(radiusChunks is < MinimumRadiusChunks or > MaximumRadiusChunks)
            throw new ArgumentOutOfRangeException(nameof(radiusChunks));
        int centerChunkX = FloorDivideBySixteen(focus.X);
        int centerChunkZ = FloorDivideBySixteen(focus.Z);
        var addresses = new List<MinecraftChunkAddress>(ChunkCountForRadius(radiusChunks));
        for(int z = -radiusChunks; z <= radiusChunks; z++)
        {
            for(int x = -radiusChunks; x <= radiusChunks; x++)
            {
                addresses.Add(new MinecraftChunkAddress(
                    dimension,
                    checked(centerChunkX + x),
                    checked(centerChunkZ + z)));
            }
        }
        return addresses
            .OrderBy(address => Math.Max(Math.Abs((long)address.X - centerChunkX), Math.Abs((long)address.Z - centerChunkZ)))
            .ThenBy(address => Square((long)address.X - centerChunkX) + Square((long)address.Z - centerChunkZ))
            .ThenBy(static address => address.X)
            .ThenBy(static address => address.Z)
            .ToArray();

        static long Square(long value) => checked(value * value);
    }

    public static bool TryParseCoordinate(string? text, out float value)
    {
        if(float.TryParse(text, out value) && float.IsFinite(value) && value > int.MinValue && value < int.MaxValue)
        {
            return true;
        }
        value = 0f;
        return false;
    }

    public static bool TryCreateBlockPosition(Vector3 target, out BlockPosition position)
    {
        if(!CanFloorToInt(target.X) || !CanFloorToInt(target.Y) || !CanFloorToInt(target.Z))
        {
            position = default;
            return false;
        }
        position = new BlockPosition(
            (int)MathF.Floor(target.X),
            (int)MathF.Floor(target.Y),
            (int)MathF.Floor(target.Z));
        return true;
    }

    public static bool TryGetSpawn(MinecraftWorldDescriptor? descriptor, out BlockPosition spawn)
        => TryGetSpawn(descriptor, 0, out spawn);

    public static bool TryGetSpawn(MinecraftWorldDescriptor? descriptor, int yOffset, out BlockPosition spawn)
    {
        if(descriptor?.SpawnLocation is BlockPosition found)
        {
            long translatedY = (long)found.Y + yOffset;
            if(translatedY < int.MinValue || translatedY > int.MaxValue)
            {
                spawn = default;
                return false;
            }
            spawn = found with { Y = (int)translatedY };
            return true;
        }
        spawn = default;
        return false;
    }

    public static MinecraftChunkBounds CreateBounds(BlockPosition focus, int radiusChunks)
    {
        if(radiusChunks is < 0 or > 1024) throw new ArgumentOutOfRangeException(nameof(radiusChunks));
        int centerChunkX = FloorDivideBySixteen(focus.X);
        int centerChunkZ = FloorDivideBySixteen(focus.Z);
        return new MinecraftChunkBounds(
            centerChunkX - radiusChunks,
            centerChunkZ - radiusChunks,
            centerChunkX + radiusChunks,
            centerChunkZ + radiusChunks);
    }

    public static bool ContainsInterior(MinecraftChunkBounds bounds, Vector3 target, int marginChunks)
    {
        if(marginChunks < 0) throw new ArgumentOutOfRangeException(nameof(marginChunks));
        if(!TryCreateBlockPosition(target, out BlockPosition focus)) return false;
        int chunkX = FloorDivideBySixteen(focus.X);
        int chunkZ = FloorDivideBySixteen(focus.Z);
        long minimumX = bounds.MinimumX + (long)marginChunks;
        long minimumZ = bounds.MinimumZ + (long)marginChunks;
        long maximumX = bounds.MaximumX - (long)marginChunks;
        long maximumZ = bounds.MaximumZ - (long)marginChunks;
        return minimumX <= maximumX && minimumZ <= maximumZ &&
               chunkX >= minimumX && chunkX <= maximumX &&
               chunkZ >= minimumZ && chunkZ <= maximumZ;
    }

    /// <summary>Moves a streaming request ahead of direct X/Z navigation so terrain arrives before the camera reaches the edge.</summary>
    public static Vector3 CreatePrefetchTarget(Vector3 target, Vector2 movementDirection, int radiusChunks)
    {
        if(!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
            throw new ArgumentOutOfRangeException(nameof(target));
        if(!float.IsFinite(movementDirection.X) || !float.IsFinite(movementDirection.Y))
            throw new ArgumentOutOfRangeException(nameof(movementDirection));
        if(radiusChunks is < MinimumRadiusChunks or > MaximumRadiusChunks)
            throw new ArgumentOutOfRangeException(nameof(radiusChunks));

        int leadChunks = Math.Clamp((radiusChunks + 1) / 2, 1, MaximumPrefetchLeadChunks);
        float leadBlocks = leadChunks * 16f;
        float leadX = MathF.Abs(movementDirection.X) < 0.01f ? 0f : MathF.CopySign(leadBlocks, movementDirection.X);
        float leadZ = MathF.Abs(movementDirection.Y) < 0.01f ? 0f : MathF.CopySign(leadBlocks, movementDirection.Y);
        return new Vector3(target.X + leadX, target.Y, target.Z + leadZ);
    }

    private static bool CanFloorToInt(float value) =>
        float.IsFinite(value) && value > int.MinValue && value < int.MaxValue;

    private static int FloorDivideBySixteen(int value) => value >= 0 ? value / 16 : (value + 1) / 16 - 1;
}

public sealed class NavigationPreviewRequestDebouncer
{
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maximumDelay;
    private Vector3? _pendingTarget;
    private TimeSpan _firstRequestAt;
    private TimeSpan _lastRequestAt;

    public NavigationPreviewRequestDebouncer() : this(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(300))
    {
    }

    public NavigationPreviewRequestDebouncer(TimeSpan quietPeriod, TimeSpan maximumDelay)
    {
        if(quietPeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        if(maximumDelay < quietPeriod) throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        _quietPeriod = quietPeriod;
        _maximumDelay = maximumDelay;
    }

    public Vector3? PendingTarget => _pendingTarget;

    public void Request(Vector3 target, TimeSpan now)
    {
        if(!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
            throw new ArgumentOutOfRangeException(nameof(target));
        if(now < TimeSpan.Zero || (_pendingTarget is not null && now < _lastRequestAt))
            throw new ArgumentOutOfRangeException(nameof(now));

        if(_pendingTarget is null) _firstRequestAt = now;
        _pendingTarget = target;
        _lastRequestAt = now;
    }

    public bool TryTake(TimeSpan now, out Vector3 target)
    {
        if(_pendingTarget is not Vector3 pending)
        {
            target = default;
            return false;
        }
        if(now < _lastRequestAt) throw new ArgumentOutOfRangeException(nameof(now));
        if(now - _lastRequestAt < _quietPeriod && now - _firstRequestAt < _maximumDelay)
        {
            target = default;
            return false;
        }

        target = pending;
        Clear();
        return true;
    }

    public void Clear()
    {
        _pendingTarget = null;
        _firstRequestAt = default;
        _lastRequestAt = default;
    }
}
