namespace ZhuJieJing.Core;

public sealed record MaterialIntent
{
    public BlockState? ExactState { get; init; }

    public List<BlockState> Candidates { get; init; } = [];

    public Dictionary<string, string> Traits { get; init; } = new(StringComparer.Ordinal);

    public bool AllowAir { get; init; }

    public static MaterialIntent Exact(BlockState state, bool allowAir = false) => new()
    {
        ExactState = state,
        AllowAir = allowAir,
    };

    public BlockState? Resolve()
    {
        var state = ExactState ?? Candidates.FirstOrDefault(candidate => AllowAir || !candidate.IsAir);
        if (state is null || state.IsAir && !AllowAir) return null;
        return state;
    }
}
