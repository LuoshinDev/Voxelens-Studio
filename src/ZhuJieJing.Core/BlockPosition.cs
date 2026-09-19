namespace ZhuJieJing.Core;

public readonly record struct BlockPosition(int X, int Y, int Z) : IComparable<BlockPosition>
{
    public int CompareTo(BlockPosition other)
    {
        var xComparison = X.CompareTo(other.X);
        if (xComparison != 0) return xComparison;

        var yComparison = Y.CompareTo(other.Y);
        return yComparison != 0 ? yComparison : Z.CompareTo(other.Z);
    }

    public BlockPosition Offset(int x, int y, int z) => new(checked(X + x), checked(Y + y), checked(Z + z));
}
