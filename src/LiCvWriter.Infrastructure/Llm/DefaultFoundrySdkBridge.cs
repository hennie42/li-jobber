using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
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
    private const int FoundryContentRepetitionMinLength = 240;
    private const int FoundryThinkingRepetitionMinLength = 120;

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
            .Select(model =>
            {
                var displayName = string.IsNullOrWhiteSpace(model.Info.DisplayName) ? model.Alias : model.Info.DisplayName;
                var description = ReadModelDescription(model.Info);
                var suitability = FoundryTextBenchmarkSuitabilityEvaluator.Evaluate(
                    model.Alias,
                    displayName,
                    description,
                    ReadModelMetadata(model.Info));

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

        await InvokeOptionalTaskAsync(model, "UnloadAsync", cancellationToken);
        await InvokeRequiredTaskAsync(model, "RemoveFromCacheAsync", cancellationToken);
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

        await InvokeOptionalTaskAsync(model, "UnloadAsync", cancellationToken);
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
        var availability = await VerifyModelAvailabilityAsync(cancellationToken);
        if (!availability.AvailableModels.Any(model => model.Equals(modelAlias, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The Foundry model '{modelAlias}' is not downloaded. Download it from Start / Setup before using it.");
        }

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

    private static string GetSdkVersion()
        => typeof(FoundryLocalManager).Assembly.GetName().Version?.ToString() ?? string.Empty;

    private static async Task InvokeOptionalTaskAsync(object target, string methodName, CancellationToken cancellationToken)
    {
        var invocation = ResolveTaskInvocation(target, methodName, cancellationToken);
        if (invocation is null)
        {
            return;
        }

        try
        {
            if (invocation.Method.Invoke(target, invocation.Arguments) is Task task)
            {
                await task;
            }
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static async Task InvokeRequiredTaskAsync(object target, string methodName, CancellationToken cancellationToken)
    {
        var invocation = ResolveTaskInvocation(target, methodName, cancellationToken)
            ?? throw new InvalidOperationException($"This Foundry SDK build does not expose '{methodName}'.");

        try
        {
            if (invocation.Method.Invoke(target, invocation.Arguments) is not Task task)
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

    private static TaskInvocation? ResolveTaskInvocation(object target, string methodName, CancellationToken cancellationToken)
    {
        foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return new TaskInvocation(method, []);
            }

            if (parameters.Length == 1 && TryBuildCancellationTokenArgument(parameters[0].ParameterType, cancellationToken, out var argument))
            {
                return new TaskInvocation(method, [argument]);
            }
        }

        return null;
    }

    private static bool TryBuildCancellationTokenArgument(Type parameterType, CancellationToken cancellationToken, out object? argument)
    {
        if (parameterType == typeof(CancellationToken))
        {
            argument = cancellationToken;
            return true;
        }

        if (parameterType == typeof(CancellationToken?))
        {
            argument = (CancellationToken?)cancellationToken;
            return true;
        }

        argument = null;
        return false;
    }

    private sealed record TaskInvocation(MethodInfo Method, object?[] Arguments);

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

    private IReadOnlyList<string> NormalizeExecutionProviderNames(IReadOnlyList<string>? names)
    {
        var rawNames = names is { Count: > 0 } ? names : options.PreferredExecutionProviders;

        return rawNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
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

    private static IReadOnlyList<string> ReadModelMetadata(object modelInfo)
    {
        var type = modelInfo.GetType();
        var metadata = new List<string>();

        foreach (var propertyName in new[] { "Task", "Tasks", "Modality", "Modalities", "Capability", "Capabilities" })
        {
            metadata.AddRange(ReadPropertyValues(type, modelInfo, propertyName));
        }

        return metadata;
    }

    private static IReadOnlyList<string> ReadPropertyValues(Type type, object instance, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
        {
            return Array.Empty<string>();
        }

        var value = property.GetValue(instance);
        return value switch
        {
            null => Array.Empty<string>(),
            string text when !string.IsNullOrWhiteSpace(text) => [text.Trim()],
            IEnumerable sequence when value is not string => sequence
                .Cast<object?>()
                .Select(static item => item?.ToString())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!.Trim())
                .ToArray(),
            _ => value.ToString() is { } text && !string.IsNullOrWhiteSpace(text)
                ? [text.Trim()]
                : Array.Empty<string>()
        };
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

    private static async Task<LlmResponse> CompleteStreamingAsync(
        OpenAIChatClient chatClient,
        string modelAlias,
        IReadOnlyList<ChatMessage> messages,
        Action<LlmProgressUpdate>? progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var responseBuffer = new StringBuilder();
        var thinkingBuffer = new StringBuilder();
        long? promptTokens = null;
        long? completionTokens = null;
        long sequence = 0;

        await foreach (var chunk in chatClient.CompleteChatStreamingAsync(messages, cancellationToken))
        {
            FoundryOpenAiResponseMapper.MergeStreamingChunk(chunk, responseBuffer, thinkingBuffer, ref promptTokens, ref completionTokens);

            if (StreamingRepetitionDetector.DetectRepetitionLoop(thinkingBuffer, FoundryThinkingRepetitionMinLength))
            {
                throw new TimeoutException(
                    $"Foundry thinking output entered a repetition loop after {thinkingBuffer.Length} characters. " +
                    "The model may need a less reasoning-heavy prompt or a lower thinking setting.");
            }

            if (StreamingRepetitionDetector.DetectRepetitionLoop(responseBuffer, FoundryContentRepetitionMinLength))
            {
                throw new TimeoutException(
                    $"Foundry content output entered a repetition loop after {responseBuffer.Length} characters. " +
                    "The model may need a more constrained prompt or lower temperature.");
            }

            var responseContent = responseBuffer.ToString();
            var thinkingContent = thinkingBuffer.Length == 0 ? null : thinkingBuffer.ToString();

            progress?.Invoke(new LlmProgressUpdate(
                "Generating response",
                null,
                modelAlias,
                stopwatch.Elapsed,
                Completed: false,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                ThinkingPreview: FoundryOpenAiResponseMapper.BuildThinkingPreview(thinkingContent),
                ResponseContent: responseContent,
                ThinkingContent: thinkingContent,
                Sequence: ++sequence));
        }

        stopwatch.Stop();
        var finalResponseContent = responseBuffer.ToString();
        var finalThinkingContent = thinkingBuffer.Length == 0 ? null : thinkingBuffer.ToString();

        progress?.Invoke(new LlmProgressUpdate(
            "Generating response",
            "Foundry Local completed the response.",
            modelAlias,
            stopwatch.Elapsed,
            Completed: true,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            ThinkingPreview: FoundryOpenAiResponseMapper.BuildThinkingPreview(finalThinkingContent),
            ResponseContent: finalResponseContent,
            ThinkingContent: finalThinkingContent,
            Sequence: ++sequence));

        return new LlmResponse(
            modelAlias,
            finalResponseContent,
            finalThinkingContent,
            Completed: true,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Duration: stopwatch.Elapsed,
            EvalDuration: completionTokens is > 0 ? stopwatch.Elapsed : null);
    }

    private string ResolveModelAlias(string requestedModel)
        => string.IsNullOrWhiteSpace(requestedModel) ? options.DefaultModelAlias : requestedModel.Trim();
}