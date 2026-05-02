using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Infrastructure.Llm;

public sealed class OllamaClient(HttpClient httpClient, OllamaOptions options) : ILlmClient
{
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(75);
    private const int DefaultRepetitionMinLength = 500;
    private const int RepetitionMinCycleLength = 20;
    private const int RepetitionMaxCycleLength = 300;
    private const int RepetitionRequiredRepeats = 3;

    public async Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        using var versionDocument = JsonDocument.Parse(await GetRequiredStatusPayloadAsync("version", cancellationToken));
        var version = versionDocument.RootElement.GetProperty("version").GetString() ?? string.Empty;

        using var tagsDocument = JsonDocument.Parse(await GetRequiredStatusPayloadAsync("tags", cancellationToken));
        var availableModels = tagsDocument.RootElement
            .GetProperty("models")
            .EnumerateArray()
            .Select(static element => element.GetProperty("name").GetString() ?? string.Empty)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        var runningModels = await TryGetRunningModelsAsync(cancellationToken);

        return new OllamaModelAvailability(
            version,
            options.Model,
            availableModels.Contains(options.Model, StringComparer.OrdinalIgnoreCase),
            availableModels,
            runningModels);
    }

    public async Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        long? fileSizeBytes = await TryGetTagFileSizeAsync(model, cancellationToken);

        var payload = await TryPostShowAsync(model, cancellationToken);
        if (payload is null)
        {
            return new OllamaModelInfo(model, fileSizeBytes, null, null, null, null);
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        string? parameterSize = null;
        string? quantization = null;
        string? family = null;
        if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            parameterSize = ReadString(details, "parameter_size");
            quantization = ReadString(details, "quantization_level");
            family = ReadString(details, "family");
        }

        long? contextLength = null;
        if (root.TryGetProperty("model_info", out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
        {
            // The context_length key is namespaced by the architecture, e.g. "llama.context_length".
            foreach (var property in modelInfo.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length", StringComparison.Ordinal)
                    && property.Value.TryGetInt64(out var length))
                {
                    contextLength = length;
                    break;
                }
            }
        }

        return new OllamaModelInfo(model, fileSizeBytes, parameterSize, quantization, family, contextLength);
    }

    private async Task<long?> TryGetTagFileSizeAsync(string model, CancellationToken cancellationToken)
    {
        var payload = await TryGetOptionalStatusPayloadAsync("tags", cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("models", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var element in array.EnumerateArray())
        {
            var name = ReadString(element, "name");
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(name, model, StringComparison.OrdinalIgnoreCase)
                && element.TryGetProperty("size", out var size)
                && size.TryGetInt64(out var sizeValue))
            {
                return sizeValue;
            }
        }

        return null;
    }

    private async Task<string?> TryPostShowAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCancellation = CreateStatusTimeoutCancellation(cancellationToken);
            using var response = await httpClient.PostAsJsonAsync("show", new { name = model, verbose = true }, timeoutCancellation.Token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.UseChatEndpoint)
        {
            return await GenerateWithChatAsync(request, progress, cancellationToken);
        }

        return await GenerateWithPromptAsync(request, progress, cancellationToken);
    }

    private async Task<LlmResponse> GenerateWithChatAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var messages = new List<object>();
        var useStreamingTransport = request.Stream || progress is not null;

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new { role = "system", content = request.SystemPrompt });
        }

        messages.AddRange(request.Messages.Select(message => new { role = message.Role, content = message.Content }));

        var payload = BuildChatPayload(
            request.Model,
            messages,
            useStreamingTransport,
            request.Think ?? options.Think,
            request.KeepAlive ?? options.KeepAlive,
            request.Temperature ?? options.Temperature,
            request.NumPredict,
            request.NumCtx ?? (options.NumCtx > 0 ? options.NumCtx : (int?)null),
            request.ResponseFormat);

        if (useStreamingTransport)
        {
            return await SendStreamingAsync("chat", payload, request.Model, ExtractChatChunk, progress, cancellationToken);
        }

        using var response = await httpClient.PostAsJsonAsync("chat", payload, cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var message = root.GetProperty("message");

        return new LlmResponse(
            root.GetProperty("model").GetString() ?? request.Model,
            message.GetProperty("content").GetString() ?? string.Empty,
            message.TryGetProperty("thinking", out var thinking) ? thinking.GetString() : null,
            root.GetProperty("done").GetBoolean(),
            ReadLong(root, "prompt_eval_count"),
            ReadLong(root, "eval_count"),
            ReadDuration(root, "total_duration"),
            ReadDuration(root, "load_duration"),
            ReadDuration(root, "prompt_eval_duration"),
            ReadDuration(root, "eval_duration"));
    }

    private async Task<LlmResponse> GenerateWithPromptAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var prompt = string.Join(Environment.NewLine + Environment.NewLine, request.Messages.Select(static message => $"{message.Role}: {message.Content}"));
        var useStreamingTransport = request.Stream || progress is not null;

        var payload = BuildGeneratePayload(
            request.Model,
            prompt,
            request.SystemPrompt,
            useStreamingTransport,
            request.Think ?? options.Think,
            request.KeepAlive ?? options.KeepAlive,
            request.Temperature ?? options.Temperature,
            request.NumPredict,
            request.NumCtx ?? (options.NumCtx > 0 ? options.NumCtx : (int?)null),
            request.ResponseFormat);

        if (useStreamingTransport)
        {
            return await SendStreamingAsync("generate", payload, request.Model, ExtractGenerateChunk, progress, cancellationToken);
        }

        using var response = await httpClient.PostAsJsonAsync("generate", payload, cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        return new LlmResponse(
            root.GetProperty("model").GetString() ?? request.Model,
            root.GetProperty("response").GetString() ?? string.Empty,
            root.TryGetProperty("thinking", out var thinking) ? thinking.GetString() : null,
            root.GetProperty("done").GetBoolean(),
            ReadLong(root, "prompt_eval_count"),
            ReadLong(root, "eval_count"),
            ReadDuration(root, "total_duration"),
            ReadDuration(root, "load_duration"),
            ReadDuration(root, "prompt_eval_duration"),
            ReadDuration(root, "eval_duration"));
    }

    private static object BuildChatPayload(
        string model,
        List<object> messages,
        bool stream,
        string think,
        string keepAlive,
        double temperature,
        int? numPredict,
        int? numCtx,
        LlmResponseFormat? responseFormat)
    {
        var optionsObject = BuildOptionsObject(temperature, numPredict, numCtx);
        var format = ResolveFormatField(responseFormat);
        return BuildPayload(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["think"] = think,
            ["keep_alive"] = keepAlive,
            ["options"] = optionsObject,
            ["format"] = format
        });
    }

    private static object BuildGeneratePayload(
        string model,
        string prompt,
        string? system,
        bool stream,
        string think,
        string keepAlive,
        double temperature,
        int? numPredict,
        int? numCtx,
        LlmResponseFormat? responseFormat)
    {
        var optionsObject = BuildOptionsObject(temperature, numPredict, numCtx);
        var format = ResolveFormatField(responseFormat);
        return BuildPayload(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["system"] = system,
            ["stream"] = stream,
            ["think"] = think,
            ["keep_alive"] = keepAlive,
            ["options"] = optionsObject,
            ["format"] = format
        });
    }

    private static IDictionary<string, object?> BuildOptionsObject(double temperature, int? numPredict, int? numCtx)
    {
        var dictionary = new Dictionary<string, object?>
        {
            ["temperature"] = temperature
        };

        if (numPredict is > 0)
        {
            dictionary["num_predict"] = numPredict.Value;
        }

        if (numCtx is > 0)
        {
            dictionary["num_ctx"] = numCtx.Value;
        }

        return dictionary;
    }

    private static object? ResolveFormatField(LlmResponseFormat? responseFormat)
    {
        if (responseFormat is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(responseFormat.SchemaJson))
        {
            // Ollama accepts a JSON Schema object directly as the format field.
            return JsonSerializer.Deserialize<JsonElement>(responseFormat.SchemaJson);
        }

        return responseFormat.Format;
    }

    private static object BuildPayload(IDictionary<string, object?> fields)
    {
        // Strip null entries so we never emit them on the wire (Ollama treats some
        // null fields as overrides rather than absences).
        var trimmed = new Dictionary<string, object?>();
        foreach (var (key, value) in fields)
        {
            if (value is null)
            {
                continue;
            }

            trimmed[key] = value;
        }

        return trimmed;
    }

    private async Task<IReadOnlyList<OllamaRunningModel>> TryGetRunningModelsAsync(CancellationToken cancellationToken)
    {
        var payload = await TryGetOptionalStatusPayloadAsync("ps", cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<OllamaRunningModel>();
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<OllamaRunningModel>();
        }

        return models.EnumerateArray()
            .Select(static element => new OllamaRunningModel(
                ReadString(element, "name") ?? string.Empty,
                ReadString(element, "model") ?? string.Empty,
                ReadDateTimeOffset(element, "expires_at"),
                ReadLong(element, "size_vram"),
                ReadLong(element, "size")))
            .Where(static runningModel => !string.IsNullOrWhiteSpace(runningModel.Name) || !string.IsNullOrWhiteSpace(runningModel.Model))
            .ToArray();
    }

    private async Task<LlmResponse> SendStreamingAsync(
        string relativePath,
        object payload,
        string fallbackModel,
        Func<JsonElement, StreamingChunk> readChunk,
        Action<LlmProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = JsonContent.Create(payload)
        };

        ReportProgress(
            progress,
            "Waiting for Ollama response",
            $"{fallbackModel} request sent to Ollama.",
            fallbackModel,
            TimeSpan.Zero,
            completed: false,
            sequence: 1);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        var nextProgressAt = TimeSpan.Zero;
        var chunkCount = 0;
        var lastReportedContentLength = 0;
        var lastReportedThinkingLength = 0;
        long sequence = 1;
        var model = fallbackModel;
        var completed = false;
        long? promptTokens = null;
        long? completionTokens = null;
        TimeSpan? duration = null;
        TimeSpan? loadDuration = null;
        TimeSpan? promptEvalDuration = null;
        TimeSpan? evalDuration = null;

        while (true)
        {
            var line = await ReadStreamingLineAsync(reader, cancellationToken);
            if (line is null)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var chunk = readChunk(document.RootElement);
            if (!string.IsNullOrWhiteSpace(chunk.Model))
            {
                model = chunk.Model;
            }

            if (!string.IsNullOrWhiteSpace(chunk.ContentDelta))
            {
                AppendStreamingText(content, chunk.ContentDelta);
            }

            if (!string.IsNullOrWhiteSpace(chunk.ThinkingDelta))
            {
                AppendStreamingText(thinking, chunk.ThinkingDelta);
            }

            if (chunk.PromptTokens is not null)
            {
                promptTokens = chunk.PromptTokens;
            }

            if (chunk.CompletionTokens is not null)
            {
                completionTokens = chunk.CompletionTokens;
            }

            if (chunk.Duration is not null)
            {
                duration = chunk.Duration;
            }

            if (chunk.LoadDuration is not null)
            {
                loadDuration = chunk.LoadDuration;
            }

            if (chunk.PromptEvalDuration is not null)
            {
                promptEvalDuration = chunk.PromptEvalDuration;
            }

            if (chunk.EvalDuration is not null)
            {
                evalDuration = chunk.EvalDuration;
            }

            completed |= chunk.Done;
            chunkCount++;

            var contentRepetitionMinLength = options.RepetitionDetectionMinLength > 0
                ? options.RepetitionDetectionMinLength
                : DefaultRepetitionMinLength;

            var thinkingRepetitionMinLength = options.RepetitionDetectionThinkingMinLength;

            if (!chunk.Done && thinkingRepetitionMinLength > 0 && DetectRepetitionLoop(thinking, thinkingRepetitionMinLength))
            {
                throw new TimeoutException(
                    $"Ollama thinking output entered a repetition loop after {thinking.Length} characters. " +
                    $"The model may need a lower temperature or a different prompt.");
            }

            if (!chunk.Done && DetectRepetitionLoop(content, contentRepetitionMinLength))
            {
                throw new TimeoutException(
                    $"Ollama content output entered a repetition loop after {content.Length} characters. " +
                    $"The model may need a lower temperature or a different prompt.");
            }

            var hasStreamedVisibleContent = content.Length > 0 || thinking.Length > 0;
            var hasNewVisibleContent = content.Length != lastReportedContentLength || thinking.Length != lastReportedThinkingLength;
            if (hasStreamedVisibleContent && hasNewVisibleContent && (stopwatch.Elapsed >= nextProgressAt || chunk.Done))
            {
                ReportProgress(
                    progress,
                    "Ollama is responding",
                    $"{model} has been streaming for {FormatElapsed(stopwatch.Elapsed)} across {chunkCount} updates.",
                    model,
                    stopwatch.Elapsed,
                    completed: false,
                    promptTokens: promptTokens,
                    completionTokens: completionTokens,
                    estimatedRemaining: null,
                    thinkingPreview: BuildThinkingPreview(thinking),
                    responseContent: content.ToString(),
                    thinkingContent: thinking.Length == 0 ? null : thinking.ToString(),
                    sequence: ++sequence);

                lastReportedContentLength = content.Length;
                lastReportedThinkingLength = thinking.Length;
                nextProgressAt = stopwatch.Elapsed + ProgressUpdateInterval;
            }
        }

        stopwatch.Stop();
        var finalDuration = duration ?? stopwatch.Elapsed;
        ReportProgress(
            progress,
            "Ollama response completed",
            $"{model} finished in {FormatElapsed(finalDuration)}.",
            model,
            finalDuration,
            completed: true,
            promptTokens: promptTokens,
            completionTokens: completionTokens,
            estimatedRemaining: TimeSpan.Zero,
            thinkingPreview: BuildThinkingPreview(thinking),
            responseContent: content.ToString(),
            thinkingContent: thinking.Length == 0 ? null : thinking.ToString(),
            sequence: ++sequence);

        return new LlmResponse(
            model,
            content.ToString(),
            thinking.Length == 0 ? null : thinking.ToString(),
            completed,
            promptTokens,
            completionTokens,
            finalDuration,
            loadDuration,
            promptEvalDuration,
            evalDuration);
    }

    private async Task<string?> ReadStreamingLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var inactivityTimeout = TimeSpan.FromSeconds(Math.Max(1, options.StreamingInactivitySeconds));
        using var inactivityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivityCancellation.CancelAfter(inactivityTimeout);

        try
        {
            return await reader.ReadLineAsync(inactivityCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Ollama stopped streaming for more than {inactivityTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<string> GetRequiredStatusPayloadAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CreateStatusTimeoutCancellation(cancellationToken);
        using var response = await httpClient.GetAsync(relativePath, timeoutCancellation.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeoutCancellation.Token);
    }

    private async Task<string?> TryGetOptionalStatusPayloadAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            return await GetRequiredStatusPayloadAsync(relativePath, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private CancellationTokenSource CreateStatusTimeoutCancellation(CancellationToken cancellationToken)
    {
        var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.StatusTimeoutSeconds)));
        return timeoutCancellation;
    }

    private static StreamingChunk ExtractChatChunk(JsonElement root)
    {
        var message = root.TryGetProperty("message", out var candidate) && candidate.ValueKind == JsonValueKind.Object
            ? candidate
            : default;

        return new StreamingChunk(
            ReadString(root, "model"),
            message.ValueKind == JsonValueKind.Object ? ReadString(message, "content") : null,
            message.ValueKind == JsonValueKind.Object ? ReadString(message, "thinking") : null,
            ReadBool(root, "done"),
            ReadLong(root, "prompt_eval_count"),
            ReadLong(root, "eval_count"),
            ReadDuration(root, "total_duration"),
            ReadDuration(root, "load_duration"),
            ReadDuration(root, "prompt_eval_duration"),
            ReadDuration(root, "eval_duration"));
    }

    private static StreamingChunk ExtractGenerateChunk(JsonElement root)
        => new(
            ReadString(root, "model"),
            ReadString(root, "response"),
            ReadString(root, "thinking"),
            ReadBool(root, "done"),
            ReadLong(root, "prompt_eval_count"),
            ReadLong(root, "eval_count"),
            ReadDuration(root, "total_duration"),
            ReadDuration(root, "load_duration"),
            ReadDuration(root, "prompt_eval_duration"),
            ReadDuration(root, "eval_duration"));

    private static void ReportProgress(
        Action<LlmProgressUpdate>? progress,
        string message,
        string? detail,
        string model,
        TimeSpan? elapsed,
        bool completed,
        long? promptTokens = null,
        long? completionTokens = null,
        TimeSpan? estimatedRemaining = null,
        string? thinkingPreview = null,
        string? responseContent = null,
        string? thinkingContent = null,
        long sequence = 0)
        => progress?.Invoke(new LlmProgressUpdate(
            message,
            detail,
            model,
            elapsed,
            completed,
            promptTokens,
            completionTokens,
            estimatedRemaining,
            thinkingPreview,
            responseContent,
            thinkingContent,
            sequence));

    /// <summary>
    /// Detects whether a streaming text buffer has entered a repetition loop by checking
    /// if the tail consists of a short pattern repeated multiple times consecutively.
    /// </summary>
    internal static bool DetectRepetitionLoop(StringBuilder buffer, int minLength)
    {
        if (minLength <= 0 || buffer.Length < minLength)
        {
            return false;
        }

        // Check a window at the tail of the buffer for repeating cycles.
        var tailLength = Math.Min(buffer.Length, RepetitionMaxCycleLength * (RepetitionRequiredRepeats + 1));
        var tailStart = buffer.Length - tailLength;
        var tail = buffer.ToString(tailStart, tailLength);

        for (var cycleLength = RepetitionMinCycleLength; cycleLength <= RepetitionMaxCycleLength; cycleLength++)
        {
            if (tail.Length < cycleLength * RepetitionRequiredRepeats)
            {
                break;
            }

            var candidate = tail[^cycleLength..];
            var matched = 0;

            for (var offset = tail.Length - cycleLength; offset >= cycleLength; offset -= cycleLength)
            {
                var segment = tail.Substring(offset - cycleLength, cycleLength);
                if (!segment.Equals(candidate, StringComparison.Ordinal))
                {
                    break;
                }

                matched++;
            }

            if (matched >= RepetitionRequiredRepeats - 1)
            {
                return true;
            }
        }

        return false;
    }

    private static string? BuildThinkingPreview(StringBuilder thinking)
    {
        if (thinking.Length == 0)
        {
            return null;
        }

        const int maxCharacters = 320;
        var start = Math.Max(0, thinking.Length - maxCharacters);
        var preview = thinking.ToString(start, thinking.Length - start).Trim();

        if (string.IsNullOrWhiteSpace(preview))
        {
            return null;
        }

        return start > 0 ? $"...{preview}" : preview;
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalMinutes >= 1
            ? elapsed.ToString(@"m\:ss")
            : elapsed.ToString(@"s\.f\s");

    private static void AppendStreamingText(StringBuilder aggregate, string incoming)
    {
        if (string.IsNullOrEmpty(incoming))
        {
            return;
        }

        if (aggregate.Length == 0)
        {
            aggregate.Append(incoming);
            return;
        }

        var existing = aggregate.ToString();
        if (incoming.Equals(existing, StringComparison.Ordinal) || existing.StartsWith(incoming, StringComparison.Ordinal))
        {
            return;
        }

        if (incoming.StartsWith(existing, StringComparison.Ordinal))
        {
            aggregate.Append(incoming.AsSpan(existing.Length));
            return;
        }

        aggregate.Append(incoming);
    }

    private static long? ReadLong(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value) ? value : null;

    private static bool ReadBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static TimeSpan? ReadDuration(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || !property.TryGetInt64(out var nanoseconds))
        {
            return null;
        }

        return TimeSpan.FromTicks(nanoseconds / 100);
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), out var value)
                ? value
                : null;

    private sealed record StreamingChunk(
        string? Model,
        string? ContentDelta,
        string? ThinkingDelta,
        bool Done,
        long? PromptTokens,
        long? CompletionTokens,
        TimeSpan? Duration,
        TimeSpan? LoadDuration,
        TimeSpan? PromptEvalDuration,
        TimeSpan? EvalDuration);
}
