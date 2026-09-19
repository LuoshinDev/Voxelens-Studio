namespace ZhuJieJing.Core;

public enum PreviewTransactionState
{
    Pending,
    Committed,
    Discarded,
}

public sealed record PreviewTransaction
{
    private PreviewTransaction(Guid id, SceneDelta delta, PreviewTransactionState state)
    {
        Id = id;
        Delta = delta;
        State = state;
    }

    public Guid Id { get; }

    public SceneDelta Delta { get; }

    public PreviewTransactionState State { get; }

    public string PreviewRevision => $"preview:{Delta.StableHash}";

    public static PreviewTransaction Create(SceneDelta delta) => new(Guid.NewGuid(), delta, PreviewTransactionState.Pending);

    public PreviewTransaction Commit(string currentRevision)
    {
        EnsurePending();
        if (!string.Equals(currentRevision, Delta.BaseRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"预览基于 {Delta.BaseRevision}，当前场景为 {currentRevision}。");
        }

        return new PreviewTransaction(Id, Delta, PreviewTransactionState.Committed);
    }

    public PreviewTransaction Discard()
    {
        EnsurePending();
        return new PreviewTransaction(Id, Delta, PreviewTransactionState.Discarded);
    }

    public SceneDelta CreateInverse(string committedRevision) => Delta.Invert(committedRevision);

    private void EnsurePending()
    {
        if (State != PreviewTransactionState.Pending) throw new InvalidOperationException("预览事务已经结束。 ");
    }
}
