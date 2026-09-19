using System.Text.Json.Serialization;

namespace ZhuJieJing.Core;

public readonly record struct BoxSelection
{
    [JsonConstructor]
    public BoxSelection(BlockPosition min, BlockPosition maxExclusive)
    {
        if (maxExclusive.X <= min.X || maxExclusive.Y <= min.Y || maxExclusive.Z <= min.Z)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "BoxSelection 的 MaxExclusive 必须在三个轴上都大于 Min。");
        }

        Min = min;
        MaxExclusive = maxExclusive;
    }

    public BlockPosition Min { get; }

    public BlockPosition MaxExclusive { get; }

    public long Volume => checked(
        ((long)MaxExclusive.X - Min.X) *
        ((long)MaxExclusive.Y - Min.Y) *
        ((long)MaxExclusive.Z - Min.Z));

    public bool Contains(BlockPosition position) =>
        position.X >= Min.X && position.X < MaxExclusive.X &&
        position.Y >= Min.Y && position.Y < MaxExclusive.Y &&
        position.Z >= Min.Z && position.Z < MaxExclusive.Z;

    public bool Contains(BoxSelection other) => Contains(other.Min) &&
        other.MaxExclusive.X <= MaxExclusive.X &&
        other.MaxExclusive.Y <= MaxExclusive.Y &&
        other.MaxExclusive.Z <= MaxExclusive.Z;

    public IEnumerable<BlockPosition> EnumeratePositions()
    {
        for (var y = Min.Y; y < MaxExclusive.Y; y++)
        {
            for (var z = Min.Z; z < MaxExclusive.Z; z++)
            {
                for (var x = Min.X; x < MaxExclusive.X; x++) yield return new BlockPosition(x, y, z);
            }
        }
    }

    public static BoxSelection FromMinAndSize(BlockPosition min, int width, int height, int depth)
    {
        if (width <= 0 || height <= 0 || depth <= 0) throw new ArgumentOutOfRangeException(nameof(width), "BoxSelection 的尺寸必须为正数。");
        return new BoxSelection(min, min.Offset(width, height, depth));
    }
}
