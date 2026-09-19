namespace ZhuJieJing.Minecraft;

/// <summary>
/// Legal integer Y offsets for moving occupied source blocks into a target build range without clipping.
/// Null limits mean that the source contains no occupied blocks and any offset is legal.
/// </summary>
public sealed record MinecraftYTranslationLimits(
    BlockYRange? OccupiedSourceRange,
    BlockYRange TargetBuildRange,
    int? MinimumOffset,
    int? MaximumOffset,
    int SuggestedOffset)
{
    public bool HasOccupiedBlocks => OccupiedSourceRange is not null;

    public bool CanFitWithoutClipping =>
        !HasOccupiedBlocks || MinimumOffset <= MaximumOffset;

    public bool Allows(int offset) =>
        !HasOccupiedBlocks ||
        (CanFitWithoutClipping && offset >= MinimumOffset && offset <= MaximumOffset);

    public static MinecraftYTranslationLimits Calculate(
        BlockYRange? occupiedSourceRange,
        BlockYRange targetBuildRange)
    {
        if (occupiedSourceRange is null)
            return new(null, targetBuildRange, null, null, 0);

        BlockYRange source = occupiedSourceRange.Value;
        long minimum = (long)targetBuildRange.Minimum - source.Minimum;
        long maximum = (long)targetBuildRange.Maximum - source.Maximum;
        int minimumOffset = CheckedOffset(minimum);
        int maximumOffset = CheckedOffset(maximum);
        int suggested = minimumOffset <= maximumOffset
            ? Math.Clamp(0, minimumOffset, maximumOffset)
            : 0;

        return new(source, targetBuildRange, minimumOffset, maximumOffset, suggested);
    }

    private static int CheckedOffset(long value)
    {
        if (value < int.MinValue || value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Y translation exceeds the supported integer coordinate range.");

        return (int)value;
    }
}
