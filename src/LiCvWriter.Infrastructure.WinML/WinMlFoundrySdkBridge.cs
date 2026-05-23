using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Infrastructure.Foundry;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiCvWriter.Infrastructure.WinML;

internal sealed class WinMlFoundrySdkBridge(FoundryOptions options) : IFoundrySdkBridge, IDisposable
{
    private readonly WinMlFoundryLocalManagerAccessor managerAccessor = new(options);

    public async Task<FoundryCatalogSnapshot> GetCatalogSnapshotAsync(CancellationToken cancellationToken = default)
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
            .Select(model => new FoundryCatalogModel(
                model.Alias,
                string.IsNullOrWhiteSpace(model.Info.DisplayName) ? model.Alias : model.Info.DisplayName,
                model.Id,
                model.Info.FileSizeMb,
                cachedAliases.Contains(model.Alias),
                loadedAliases.Contains(model.Alias),
                ReadModelDescription(model.Info)))
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

        await InvokeOptionalTaskAsync(model, "UnloadAsync");
        await InvokeRequiredTaskAsync(model, "RemoveFromCacheAsync");
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
        try
        {
            await EnsureModelAvailabilityAsync(modelAlias, cancellationToken);
            return await ExecuteGenerateAsync(modelAlias, request, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            IReadOnlyList<string>? recoveryNotes = FoundryRuntimeFailureClassifier.IsRetriableModelLoadFailure(exception)
                ? ["The app skipped an in-process Foundry runtime reset because the current SDK can return disposed-object failures after a reset. Restart the app to force a clean Foundry runtime."]
                : null;

            if (TryNormalizeFoundryRuntimeException(exception, retryAttempted: false, additionalNotes: recoveryNotes) is { } normalizedException)
            {
                throw normalizedException;
            }

            throw;
        }
    }

    public void Dispose()
    {
        managerAccessor.Dispose();
    }

    private static string GetSdkVersion()
        => typeof(FoundryLocalManager).Assembly.GetName().Version?.ToString() ?? string.Empty;

    private static async Task InvokeOptionalTaskAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, binder: null, Type.EmptyTypes, modifiers: null);
        if (method is null)
        {
            return;
        }

        try
        {
            if (method.Invoke(target, null) is Task task)
            {
                await task;
            }
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static async Task InvokeRequiredTaskAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, binder: null, Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException($"This Foundry SDK build does not expose '{methodName}'.");

        try
        {
            if (method.Invoke(target, null) is not Task task)
            {
                throw new InvalidOperationException($"Foundry SDK method '{methodName}' did not return a Task.");
            }

            await task;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private FoundryAccelerationSnapshot BuildAccelerationSnapshot(FoundryLocalManager manager)
    {
        if (!options.UseWindowsMlAcceleration)
        {
            return FoundryAccelerationSnapshot.Disabled("Windows ML acceleration is disabled in configuration.");
        }

        var discoverMethod = manager.GetType().GetMethod("DiscoverEps", BindingFlags.Instance | BindingFlags.Public);
        if (discoverMethod is null)
        {
            return FoundryAccelerationSnapshot.Unavailable("This Foundry SDK build does not expose execution-provider discovery APIs.");
        }

        try
        {
            var executionProviders = MapExecutionProviders(discoverMethod.Invoke(manager, null) as IEnumerable);
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

    private static IReadOnlyList<FoundryExecutionProviderInfo> MapExecutionProviders(IEnumerable? rawExecutionProviders)
    {
        if (rawExecutionProviders is null)
        {
            return [];
        }

        var executionProviders = new List<FoundryExecutionProviderInfo>();
        foreach (var rawExecutionProvider in rawExecutionProviders)
        {
            if (rawExecutionProvider is null)
            {
                continue;
            }

            var executionProviderType = rawExecutionProvider.GetType();
            var name = ReadStringProperty(executionProviderType, rawExecutionProvider, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var displayName = ReadStringProperty(executionProviderType, rawExecutionProvider, "DisplayName");
            executionProviders.Add(new FoundryExecutionProviderInfo(
                name,
                string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                ReadBooleanProperty(executionProviderType, rawExecutionProvider, "IsRegistered")));
        }

        return executionProviders
            .OrderByDescending(static executionProvider => executionProvider.IsRegistered)
            .ThenBy(static executionProvider => executionProvider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ReadBooleanProperty(Type type, object instance, string propertyName)
        => type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance) as bool? ?? false;

    private static string ReadStringProperty(Type type, object instance, string propertyName)
        => type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance)?.ToString() ?? string.Empty;

    private static string? ReadModelDescription(object modelInfo)
    {
        var type = modelInfo.GetType();
        foreach (var propertyName in new[] { "Description", "Summary", "ShortDescription" })
        {
            var value = ReadStringProperty(type, modelInfo, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
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

    private async Task EnsureModelAvailabilityAsync(string modelAlias, CancellationToken cancellationToken)
    {
        var availability = await VerifyModelAvailabilityAsync(cancellationToken);
        if (!availability.AvailableModels.Any(model => model.Equals(modelAlias, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The Foundry model '{modelAlias}' is not downloaded. Download it from Start / Setup before using it.");
        }
    }

    private async Task<LlmResponse> ExecuteGenerateAsync(
        string modelAlias,
        LlmRequest request,
        Action<LlmProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var manager = await managerAccessor.GetManagerAsync(cancellationToken);
        var catalog = await manager.GetCatalogAsync();
        var model = await catalog.GetModelAsync(modelAlias)
            ?? throw new InvalidOperationException($"The Foundry model '{modelAlias}' was not found in the local catalog.");

        if (options.AutoLoadSelectedModel)
        {
            await model.LoadAsync();
        }

        var chatClient = await model.GetChatClientAsync();
        FoundryOpenAiResponseMapper.ConfigureChatClient(chatClient, request);
        var messages = BuildMessages(request);
        var stopwatch = Stopwatch.StartNew();

        if (request.Stream || progress is not null)
        {
            return await CompleteStreamingAsync(chatClient, modelAlias, messages, progress, stopwatch, cancellationToken);
        }

        var response = await chatClient.CompleteChatAsync(messages);
        stopwatch.Stop();

        return FoundryOpenAiResponseMapper.MapChatCompletion(modelAlias, response, stopwatch.Elapsed);
    }

    private FoundryRuntimeException? TryNormalizeFoundryRuntimeException(
        Exception exception,
        bool retryAttempted,
        IReadOnlyList<string>? additionalNotes)
        => FoundryRuntimeFailureClassifier.TryCreateException(
            exception,
            retryAttempted,
            managerAccessor.GetResolvedModelCacheDirectory(),
            managerAccessor.GetResolvedLogsDirectory(),
            additionalNotes);

    private static async Task<LlmResponse> CompleteStreamingAsync(
        OpenAIChatClient chatClient,
        string modelAlias,
        IReadOnlyList<ChatMessage> messages,
        Action<LlmProgressUpdate>? progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        long sequence = 0;

        await foreach (var chunk in chatClient.CompleteChatStreamingAsync(messages, cancellationToken))
        {
            var delta = chunk.Choices?.FirstOrDefault()?.Message?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                buffer.Append(delta);
            }

            progress?.Invoke(new LlmProgressUpdate(
                "Generating response",
                null,
                modelAlias,
                stopwatch.Elapsed,
                Completed: false,
                ResponseContent: buffer.ToString(),
                Sequence: ++sequence));
        }

        stopwatch.Stop();
        progress?.Invoke(new LlmProgressUpdate(
            "Generating response",
            "Foundry Local completed the response.",
            modelAlias,
            stopwatch.Elapsed,
            Completed: true,
            ResponseContent: buffer.ToString(),
            Sequence: ++sequence));

        return new LlmResponse(
            modelAlias,
            buffer.ToString(),
            Thinking: null,
            Completed: true,
            PromptTokens: null,
            CompletionTokens: null,
            Duration: stopwatch.Elapsed);
    }

    private string ResolveModelAlias(string requestedModel)
        => string.IsNullOrWhiteSpace(requestedModel) ? options.DefaultModelAlias : requestedModel.Trim();

    private sealed class WinMlFoundryLocalManagerAccessor(FoundryOptions options)
    {
        private static readonly System.Text.RegularExpressions.Regex InvalidAppNameCharacters = new("[^\\p{L}\\p{Nd} _-]+", System.Text.RegularExpressions.RegexOptions.Compiled);
        private readonly SemaphoreSlim initializationGate = new(1, 1);

        public string GetResolvedModelCacheDirectory()
            => ResolveModelCacheDirectory(options);

        public string GetResolvedLogsDirectory()
            => ResolveLogsDirectory(options);

        public async Task<FoundryLocalManager> GetManagerAsync(CancellationToken cancellationToken = default)
        {
            if (FoundryLocalManager.IsInitialized)
            {
                return FoundryLocalManager.Instance;
            }

            await initializationGate.WaitAsync(cancellationToken);
            try
            {
                if (!FoundryLocalManager.IsInitialized)
                {
                    try
                    {
                        await FoundryLocalManager.CreateAsync(BuildConfiguration(options), NullLoggerFactory.Instance.CreateLogger("FoundryLocal.WinML"));
                    }
                    catch (Exception exception)
                    {
                        throw NormalizeInitializationException(exception);
                    }
                }

                return FoundryLocalManager.Instance;
            }
            finally
            {
                initializationGate.Release();
            }
        }

        public async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            await initializationGate.WaitAsync(cancellationToken);
            try
            {
                if (FoundryLocalManager.IsInitialized)
                {
                    FoundryLocalManager.Instance.Dispose();
                }
            }
            finally
            {
                initializationGate.Release();
            }
        }

        public void Dispose()
        {
            initializationGate.Dispose();

            if (FoundryLocalManager.IsInitialized)
            {
                FoundryLocalManager.Instance.Dispose();
            }
        }

        private static Configuration BuildConfiguration(FoundryOptions options)
            => new()
            {
                AppName = NormalizeAppName(options.AppName),
                AppDataDir = NormalizeOptionalPath(options.AppDataDir),
                ModelCacheDir = NormalizeOptionalPath(options.ModelCacheDir),
                LogsDir = NormalizeOptionalPath(options.LogsDir)
            };

        private static Exception NormalizeInitializationException(Exception exception)
        {
            var message = $"Microsoft Foundry Local WinML could not start in the Windows-only adapter. {exception.Message}";
            return new InvalidOperationException(message, exception);
        }

        private static string? NormalizeOptionalPath(string? path)
            => string.IsNullOrWhiteSpace(path) ? null : path.Trim();

        private static string ResolveAppDataDirectory(FoundryOptions options)
        {
            var configuredPath = NormalizeOptionalPath(options.AppDataDir);
            if (configuredPath is not null)
            {
                return configuredPath;
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, $".{NormalizeAppName(options.AppName)}");
        }

        private static string ResolveModelCacheDirectory(FoundryOptions options)
        {
            var configuredPath = NormalizeOptionalPath(options.ModelCacheDir);
            return configuredPath ?? Path.Combine(ResolveAppDataDirectory(options), "cache", "models");
        }

        private static string ResolveLogsDirectory(FoundryOptions options)
        {
            var configuredPath = NormalizeOptionalPath(options.LogsDir);
            return configuredPath ?? Path.Combine(ResolveAppDataDirectory(options), "logs");
        }

        private static string NormalizeAppName(string? appName)
        {
            var candidate = string.IsNullOrWhiteSpace(appName) ? "LI-CV-Writer" : appName.Trim();
            candidate = InvalidAppNameCharacters.Replace(candidate, "-");

            return string.IsNullOrWhiteSpace(candidate) ? "LI-CV-Writer" : candidate;
        }
    }
}