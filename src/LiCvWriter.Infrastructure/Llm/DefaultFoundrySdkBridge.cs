using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Infrastructure.Foundry;
using Microsoft.AI.Foundry.Local;

namespace LiCvWriter.Infrastructure.Llm;

/// <summary>
/// Uses the cross-platform Foundry Local SDK package as the default bridge implementation.
/// </summary>
public sealed class DefaultFoundrySdkBridge(FoundryLocalManagerAccessor managerAccessor, FoundryOptions options) : IFoundrySdkBridge
{
    private static readonly TimeSpan CatalogSnapshotCacheDuration = TimeSpan.FromSeconds(10);
    private readonly FoundryCatalogSnapshotCache catalogSnapshotCache = new(CatalogSnapshotCacheDuration);

    public async Task<FoundryCatalogSnapshot> GetCatalogSnapshotAsync(CancellationToken cancellationToken = default)
        => await catalogSnapshotCache.GetOrCreateAsync(GetFreshCatalogSnapshotAsync, cancellationToken);

    private async Task<FoundryCatalogSnapshot> GetFreshCatalogSnapshotAsync(CancellationToken cancellationToken)
    {
        var manager = await managerAccessor.GetManagerAsync(cancellationToken);
        var catalog = await manager.GetCatalogAsync();
        var models = await catalog.ListModelsAsync();
        var cachedModels = await catalog.GetCachedModelsAsync();
        var loadedModels = await catalog.GetLoadedModelsAsync();

        var cachedAliases = cachedModels
            .Select(static model => model.Alias)
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var loadedAliases = loadedModels
            .Select(static model => model.Alias)
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var catalogModels = models
            .Select(model =>
            {
                var displayName = string.IsNullOrWhiteSpace(model.Info.DisplayName) ? model.Alias : model.Info.DisplayName;
                var description = FoundryCatalogMetadataReader.ReadModelDescription(model.Info);
                var suitability = FoundryTextBenchmarkSuitabilityEvaluator.Evaluate(
                    model.Alias,
                    displayName,
                    description,
                    FoundryCatalogMetadataReader.ReadModelMetadata(model.Info));

                return new FoundryCatalogModel(
                    model.Alias,
                    displayName,
                    model.Id,
                    model.Info.FileSizeMb,
                    cachedAliases.Contains(model.Alias),
                    loadedAliases.Contains(model.Alias),
                    description,
                    suitability.IsUsable,
                    suitability.Reason);
            })
            .OrderByDescending(static model => model.IsLoaded)
            .ThenByDescending(static model => model.IsCached)
            .ThenBy(static model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var availability = new LlmModelAvailability(
            GetSdkVersion(),
            options.DefaultModelAlias,
            cachedAliases.Count > 0,
            catalogModels.Where(static model => model.IsCached).Select(static model => model.Alias).ToArray(),
            catalogModels.Where(static model => model.IsLoaded)
                .Select(static model => new LlmRunningModel(model.Alias, model.ModelId, null, null, null, LlmProviderKind.Foundry))
                .ToArray(),
            LlmProviderKind.Foundry);

        return new FoundryCatalogSnapshot(availability, catalogModels, BuildAccelerationSnapshot(manager), DateTimeOffset.UtcNow);
    }

    public async Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(
        IReadOnlyList<string>? names = null,
        Action<string, double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.UseWindowsMlAcceleration)
        {
            return FoundryAccelerationSnapshot.Disabled("Windows ML acceleration is disabled in configuration.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return FoundryAccelerationSnapshot.Unsupported("Windows ML acceleration is only available on Windows.");
        }

        var manager = await managerAccessor.GetManagerAsync(cancellationToken);
        var requestedNames = NormalizeExecutionProviderNames(names);
        var cancellation = (CancellationToken?)cancellationToken;

        if (requestedNames.Count == 0)
        {
            if (progress is null)
            {
                await manager.DownloadAndRegisterEpsAsync(cancellation);
            }
            else
            {
                await manager.DownloadAndRegisterEpsAsync(progress, cancellation);
            }
        }
        else if (progress is null)
        {
            await manager.DownloadAndRegisterEpsAsync(requestedNames, cancellation);
        }
        else
        {
            await manager.DownloadAndRegisterEpsAsync(requestedNames, progress, cancellation);
        }

        catalogSnapshotCache.Invalidate();
        return BuildAccelerationSnapshot(manager);
    }

    public async Task DownloadModelAsync(
        string alias,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("A Foundry model alias is required.", nameof(alias));
        }

        if (!options.AllowDownloads)
        {
            throw new InvalidOperationException("Foundry downloads are disabled in configuration.");
        }

        var manager = await managerAccessor.GetManagerAsync(cancellationToken);
        var catalog = await manager.GetCatalogAsync();
        var model = await catalog.GetModelAsync(alias.Trim())
            ?? throw new InvalidOperationException($"The Foundry model '{alias}' was not found in the local catalog.");

        Action<float>? rawProgress = progress is null ? null : value => progress(value);
        await model.DownloadAsync(rawProgress);
        catalogSnapshotCache.Invalidate();
    }

    public async Task RemoveModelAsync(
        string alias,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("A Foundry model alias is required.", nameof(alias));
        }

        var manager = await managerAccessor.GetManagerAsync(cancellationToken);
        var catalog = await manager.GetCatalogAsync();
        var model = await catalog.GetModelAsync(alias.Trim())
            ?? throw new InvalidOperationException($"The Foundry model '{alias}' was not found in the local catalog.");

        await FoundrySdkTaskInvoker.InvokeOptionalAsync(model, "UnloadAsync", cancellationToken);
        await FoundrySdkTaskInvoker.InvokeRequiredAsync(model, "RemoveFromCacheAsync", cancellationToken);
        catalogSnapshotCache.Invalidate();
    }

    public async Task UnloadModelAsync(
        string alias,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("A Foundry model alias is required.", nameof(alias));
        }

        var manager = await managerAccessor.GetManagerAsync(cancellationToken);
        var catalog = await manager.GetCatalogAsync();
        var model = await catalog.GetModelAsync(alias.Trim())
            ?? throw new InvalidOperationException($"The Foundry model '{alias}' was not found in the local catalog.");

        await FoundrySdkTaskInvoker.InvokeOptionalAsync(model, "UnloadAsync", cancellationToken);
        catalogSnapshotCache.Invalidate();
    }

    public async Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
        => (await GetCatalogSnapshotAsync(cancellationToken)).Availability;

    public async Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var manager = await managerAccessor.GetManagerAsync(cancellationToken);
        var catalog = await manager.GetCatalogAsync();
        var foundryModel = await catalog.GetModelAsync(model.Trim());
        if (foundryModel is null)
        {
            return null;
        }

        return new LlmModelInfo(
            foundryModel.Alias,
            FileSizeBytes: null,
            ParameterSize: null,
            QuantizationLevel: null,
            Family: null,
            ContextLength: null,
            Provider: LlmProviderKind.Foundry);
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelAlias = ResolveModelAlias(request.Model);
        var captureThinking = FoundryOpenAiResponseMapper.ShouldCaptureThinking(request);
        var snapshot = await GetCatalogSnapshotAsync(cancellationToken);
        if (!snapshot.Availability.AvailableModels.Any(model => model.Equals(modelAlias, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The Foundry model '{modelAlias}' is not downloaded. Download it from Start / Setup before using it.");
        }

        var manager = await managerAccessor.GetManagerAsync(cancellationToken);
        var catalog = await manager.GetCatalogAsync();
        var model = await catalog.GetModelAsync(modelAlias)
            ?? throw new InvalidOperationException($"The Foundry model '{modelAlias}' was not found in the local catalog.");

        if (options.AutoLoadSelectedModel && !IsModelLoaded(snapshot, modelAlias))
        {
            await model.LoadAsync();
            catalogSnapshotCache.Invalidate();
        }

        var chatClient = await model.GetChatClientAsync();
        FoundryOpenAiResponseMapper.ConfigureChatClient(chatClient, request);
        var messages = BuildMessages(request);
        var stopwatch = Stopwatch.StartNew();

        if (request.Stream || progress is not null)
        {
            return await FoundryStreamingCompletionRunner.CompleteAsync(chatClient, modelAlias, messages, progress, stopwatch, captureThinking, cancellationToken);
        }

        var response = await chatClient.CompleteChatAsync(messages);
        stopwatch.Stop();

        return FoundryOpenAiResponseMapper.MapChatCompletion(modelAlias, response, stopwatch.Elapsed, captureThinking);
    }

    private static string GetSdkVersion()
        => typeof(FoundryLocalManager).Assembly.GetName().Version?.ToString() ?? string.Empty;

    private FoundryAccelerationSnapshot BuildAccelerationSnapshot(FoundryLocalManager manager)
    {
        if (!options.UseWindowsMlAcceleration)
        {
            return FoundryAccelerationSnapshot.Disabled("Windows ML acceleration is disabled in configuration.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return FoundryAccelerationSnapshot.Unsupported("Windows ML acceleration is only available on Windows.");
        }

        var discoverMethod = FoundrySdkTaskInvoker.GetOptionalPublicInstanceMethod(manager, "DiscoverEps");
        if (discoverMethod is null)
        {
            return FoundryAccelerationSnapshot.Unavailable("This Foundry SDK build does not expose execution-provider discovery APIs.");
        }

        try
        {
            var executionProviders = FoundryCatalogMetadataReader.MapExecutionProviders(discoverMethod.Invoke(manager, null) as IEnumerable);
            var registeredCount = executionProviders.Count(static executionProvider => executionProvider.IsRegistered);
            var discoveredCount = executionProviders.Count;
            var statusMessage = discoveredCount switch
            {
                0 => "No Windows ML execution providers were reported by Foundry Local.",
                _ when registeredCount == 0 => $"Foundry Local discovered {discoveredCount} Windows ML execution provider(s), but none are registered yet.",
                _ when registeredCount < discoveredCount => $"Foundry Local reports {registeredCount}/{discoveredCount} Windows ML execution provider(s) registered.",
                _ => $"Foundry Local reports all {discoveredCount} Windows ML execution provider(s) registered."
            };

            return new FoundryAccelerationSnapshot(
                true,
                true,
                true,
                statusMessage,
                executionProviders,
                DateTimeOffset.UtcNow);
        }
        catch (TargetInvocationException exception)
        {
            return FoundryAccelerationSnapshot.Unavailable($"Unable to query Windows ML execution providers: {exception.InnerException?.Message ?? exception.Message}");
        }
    }

    private IReadOnlyList<string> NormalizeExecutionProviderNames(IReadOnlyList<string>? names)
    {
        var rawNames = names is { Count: > 0 } ? names : options.PreferredExecutionProviders;

        return rawNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ChatMessage[] BuildMessages(LlmRequest request)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = request.SystemPrompt
            });
        }

        messages.AddRange(request.Messages.Select(message => new ChatMessage
        {
            Role = message.Role,
            Content = message.Content
        }));

        return messages.ToArray();
    }

    private static bool IsModelLoaded(FoundryCatalogSnapshot snapshot, string modelAlias)
        => snapshot.Models.Any(model => model.IsLoaded && model.Alias.Equals(modelAlias, StringComparison.OrdinalIgnoreCase))
           || snapshot.Availability.EffectiveRunningModels.Any(model =>
               model.Name.Equals(modelAlias, StringComparison.OrdinalIgnoreCase)
               || model.Model.Equals(modelAlias, StringComparison.OrdinalIgnoreCase));

    private string ResolveModelAlias(string requestedModel)
        => string.IsNullOrWhiteSpace(requestedModel) ? options.DefaultModelAlias : requestedModel.Trim();
}