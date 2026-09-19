using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.ConversionPreview;

public sealed record Minecraft1122ConversionPreviewCacheKey(
    string SourceRevision,
    int? RequestedYOffset,
    string MappingRevision,
    string ResourceRevision);

/// <summary>
/// Owns the one converted view associated with the mounted project. The cache deliberately keeps the preview
/// alive while the UI shows the original world, so already converted chunks can be reused on the next toggle.
/// </summary>
public sealed class Minecraft1122ConversionPreviewSessionCache : IAsyncDisposable
{
    private readonly object sync = new();
    private Minecraft1122ConversionPreviewCacheKey? key;
    private IMinecraftDowngradePreview? preview;
    private bool disposed;

    public bool TryGet(
        Minecraft1122ConversionPreviewCacheKey requestedKey,
        out IMinecraftDowngradePreview cachedPreview)
    {
        ArgumentNullException.ThrowIfNull(requestedKey);
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            bool offsetMatches = key?.RequestedYOffset == requestedKey.RequestedYOffset ||
                                 key?.RequestedYOffset is null &&
                                 requestedKey.RequestedYOffset is int requestedOffset &&
                                 preview?.Summary.AppliedYOffset == requestedOffset;
            if(key is not null &&
               string.Equals(key.SourceRevision, requestedKey.SourceRevision, StringComparison.Ordinal) &&
               string.Equals(key.MappingRevision, requestedKey.MappingRevision, StringComparison.Ordinal) &&
               string.Equals(key.ResourceRevision, requestedKey.ResourceRevision, StringComparison.Ordinal) &&
               offsetMatches && preview is not null)
            {
                cachedPreview = preview;
                return true;
            }
        }

        cachedPreview = null!;
        return false;
    }

    public async ValueTask StoreAsync(
        Minecraft1122ConversionPreviewCacheKey newKey,
        IMinecraftDowngradePreview newPreview)
    {
        ArgumentNullException.ThrowIfNull(newKey);
        ArgumentNullException.ThrowIfNull(newPreview);
        IMinecraftDowngradePreview? replaced;
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            replaced = ReferenceEquals(preview, newPreview) ? null : preview;
            key = newKey;
            preview = newPreview;
        }

        if(replaced is not null) await replaced.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Prevents future reuse without tearing down a preview that may still be visible. The stale preview is
    /// disposed atomically when a replacement is stored or when the project cache is cleared.
    /// </summary>
    public void MarkStale()
    {
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            key = null;
        }
    }

    public async ValueTask InvalidateAsync()
    {
        IMinecraftDowngradePreview? removed;
        lock(sync)
        {
            if(disposed) return;
            key = null;
            removed = preview;
            preview = null;
        }

        if(removed is not null) await removed.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        IMinecraftDowngradePreview? removed;
        lock(sync)
        {
            if(disposed) return;
            disposed = true;
            key = null;
            removed = preview;
            preview = null;
        }

        if(removed is not null) await removed.DisposeAsync().ConfigureAwait(false);
    }
}
