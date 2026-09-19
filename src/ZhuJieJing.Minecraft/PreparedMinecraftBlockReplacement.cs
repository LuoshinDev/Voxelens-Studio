namespace ZhuJieJing.Minecraft;

/// <summary>Dispose the source mount before committing; remount before finalizing, or roll back on failure.</summary>
public sealed class PreparedMinecraftBlockReplacement : IAsyncDisposable
{
    private readonly PreparedWorldDirectoryTransaction transaction;

    internal PreparedMinecraftBlockReplacement(PreparedWorldDirectoryTransaction transaction) => this.transaction = transaction;

    public string SourceDirectory => transaction.SourceDirectory;
    public string? CleanupWarning => transaction.CleanupWarning;
    public void Commit() => transaction.Commit();
    public void FinalizeCommit() => transaction.FinalizeCommit();
    public void Rollback() => transaction.Rollback();
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
