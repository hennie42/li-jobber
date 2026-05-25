using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Infrastructure.Llm;

namespace LiCvWriter.Web.Services;

internal sealed class PlaywrightFoundryCatalogClient(FoundryCatalogClient innerClient) : IFoundryCatalogClient
{
    private readonly object gate = new();
    private FoundryCatalogSnapshot? playwrightSnapshot;

    public void EnableSetupRemoveDemo()
    {
        lock (gate)
        {
            playwrightSnapshot = CreateSetupRemoveSnapshot();
        }
    }

    public FoundryCatalogSnapshot GetCurrentPlaywrightSnapshot()
    {
        lock (gate)
        {
            return playwrightSnapshot ?? throw new InvalidOperationException("The Playwright Foundry demo snapshot has not been initialized.");
        }
    }

    public async Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => TryGetPlaywrightSnapshot(out var snapshot)
            ? snapshot
            : await innerClient.GetSnapshotAsync(cancellationToken);

    public async Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(
        IReadOnlyList<string>? names = null,
        Action<string, double>? progress = null,
        CancellationToken cancellationToken = default)
        => TryGetPlaywrightSnapshot(out var snapshot)
            ? snapshot.Acceleration
            : await innerClient.RegisterExecutionProvidersAsync(names, progress, cancellationToken);

    public async Task DownloadModelAsync(
        string alias,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryUpdatePlaywrightSnapshot(alias, isCached: true))
        {
            await innerClient.DownloadModelAsync(alias, progress, cancellationToken);
            return;
        }

        progress?.Invoke(1.0);
    }

    public async Task RemoveModelAsync(
        string alias,
        CancellationToken cancellationToken = default)
    {
        if (TryUpdatePlaywrightSnapshot(alias, isCached: false))
        {
            return;
        }

        await innerClient.RemoveModelAsync(alias, cancellationToken);
    }

    public async Task UnloadModelAsync(
        string alias,
        CancellationToken cancellationToken = default)
    {
        if (TryClearPlaywrightLoadedModel(alias))
        {
            return;
        }

        await innerClient.UnloadModelAsync(alias, cancellationToken);
    }

    private bool TryGetPlaywrightSnapshot(out FoundryCatalogSnapshot snapshot)
    {
        lock (gate)
        {
            if (playwrightSnapshot is null)
            {
                snapshot = default!;
                return false;
            }

            snapshot = playwrightSnapshot;
            return true;
        }
    }

    private bool TryUpdatePlaywrightSnapshot(string alias, bool isCached)
    {
        lock (gate)
        {
            if (playwrightSnapshot is null)
            {
                return false;
            }

            var updatedModels = playwrightSnapshot.Models
                .Select(model => model.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    ? model with { IsCached = isCached, IsLoaded = false }
                    : model with { IsLoaded = false })
                .ToArray();

            var cachedAliases = updatedModels
                .Where(static model => model.IsCached)
                .Select(static model => model.Alias)
                .ToArray();

            playwrightSnapshot = playwrightSnapshot with
            {
                Availability = playwrightSnapshot.Availability with
                {
                    Installed = cachedAliases.Length > 0,
                    AvailableModels = cachedAliases,
                    RunningModels = Array.Empty<LlmRunningModel>()
                },
                Models = updatedModels,
                CollectedAtUtc = DateTimeOffset.UtcNow
            };

            return true;
        }
    }

    private bool TryClearPlaywrightLoadedModel(string alias)
    {
        lock (gate)
        {
            if (playwrightSnapshot is null)
            {
                return false;
            }

            var updatedModels = playwrightSnapshot.Models
                .Select(model => model.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    ? model with { IsLoaded = false }
                    : model)
                .ToArray();

            playwrightSnapshot = playwrightSnapshot with
            {
                Availability = playwrightSnapshot.Availability with
                {
                    RunningModels = updatedModels
                        .Where(static model => model.IsLoaded)
                        .Select(static model => new LlmRunningModel(model.Alias, model.ModelId, null, null, null, LlmProviderKind.Foundry))
                        .ToArray()
                },
                Models = updatedModels,
                CollectedAtUtc = DateTimeOffset.UtcNow
            };

            return true;
        }
    }

    private static FoundryCatalogSnapshot CreateSetupRemoveSnapshot()
    {
        var models = new[]
        {
            new FoundryCatalogModel(
                Alias: "playwright-session",
                DisplayName: "Playwright Session Model",
                ModelId: "playwright-session:1",
                FileSizeMb: 768,
                IsCached: true,
                IsLoaded: false,
                Description: "Stable cached model reserved for the active session."),
            new FoundryCatalogModel(
                Alias: "playwright-removable",
                DisplayName: "Playwright Removable Model",
                ModelId: "playwright-removable:1",
                FileSizeMb: 896,
                IsCached: true,
                IsLoaded: false,
                Description: "Cached model used by the Playwright remove-selected-cached regression."),
            new FoundryCatalogModel(
                Alias: "playwright-downloadable",
                DisplayName: "Playwright Downloadable Model",
                ModelId: "playwright-downloadable:1",
                FileSizeMb: 1024,
                IsCached: false,
                IsLoaded: false,
                Description: "Uncached model used to keep the setup page mix realistic.")
        };

        return new FoundryCatalogSnapshot(
            new LlmModelAvailability(
                Version: "playwright-demo",
                Model: "playwright-session",
                Installed: true,
                AvailableModels: models.Where(static model => model.IsCached).Select(static model => model.Alias).ToArray(),
                RunningModels: Array.Empty<LlmRunningModel>(),
                Provider: LlmProviderKind.Foundry),
            models,
            FoundryAccelerationSnapshot.Unsupported("Playwright demo mode does not emulate Windows ML execution-provider registration."),
            DateTimeOffset.UtcNow);
    }
}