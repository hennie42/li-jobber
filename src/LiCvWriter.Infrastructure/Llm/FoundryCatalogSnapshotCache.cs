using LiCvWriter.Application.Models;

namespace LiCvWriter.Infrastructure.Foundry;

internal sealed class FoundryCatalogSnapshotCache(TimeSpan timeToLive)
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private FoundryCatalogSnapshot? snapshot;

    public async Task<FoundryCatalogSnapshot> GetOrCreateAsync(
        Func<CancellationToken, Task<FoundryCatalogSnapshot>> refresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refresh);

        if (TryGetFreshSnapshot(out var currentSnapshot))
        {
            return currentSnapshot;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFreshSnapshot(out currentSnapshot))
            {
                return currentSnapshot;
            }

            currentSnapshot = await refresh(cancellationToken);
            snapshot = currentSnapshot;

            return currentSnapshot;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public void Invalidate()
        => snapshot = null;

    private bool TryGetFreshSnapshot(out FoundryCatalogSnapshot currentSnapshot)
    {
        if (snapshot is { } cachedSnapshot
            && DateTimeOffset.UtcNow - cachedSnapshot.CollectedAtUtc <= timeToLive)
        {
            currentSnapshot = cachedSnapshot;
            return true;
        }

        currentSnapshot = default!;
        return false;
    }
}