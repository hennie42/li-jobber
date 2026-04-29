using System.Text.Json;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Services;

/// <summary>
/// Wraps an <see cref="ILlmClient"/> call that is expected to return JSON. Tries strict
/// parse first, then a lenient extraction (strip markdown fences, slice between the first
/// '{' and last '}'), then a single corrective LLM round-trip asking the model to reformat
/// its previous output as valid JSON. Each attempt is exposed via <paramref name="parse"/>
/// so the caller decides what counts as a successful parse.
/// </summary>
public sealed class LlmJsonInvoker(ILlmClient llmClient)
{
    private const string RepairSystemPrompt =
        "You are a JSON formatter. Reformat the user's previous text into valid JSON that " +
        "matches their original schema. Return only the JSON object, no commentary, no " +
        "markdown fences. If the input is already valid, return it unchanged.";

    public async Task<LlmJsonInvocationResult<T>> InvokeAsync<T>(
        LlmRequest request,
        Func<string, T?> parse,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var response = await llmClient.GenerateAsync(request, progress, cancellationToken);
        var attempts = new List<LlmJsonAttempt> { new("strict", response.Content) };

        var parsed = TryParseStrict(parse, response.Content);
        if (parsed is not null)
        {
            return new LlmJsonInvocationResult<T>(parsed, response, attempts);
        }

        var lenient = ExtractJsonObject(response.Content);
        attempts.Add(new("lenient", lenient));
        var lenientParsed = TryParseStrict(parse, lenient);
        if (lenientParsed is not null)
        {
            return new LlmJsonInvocationResult<T>(lenientParsed, response, attempts);
        }

        // Final attempt: ask the model to reformat its own output.
        var repairRequest = new LlmRequest(
            request.Model,
            RepairSystemPrompt,
            [new LlmChatMessage("user", BuildRepairPrompt(response.Content, request.SystemPrompt))],
            UseChatEndpoint: request.UseChatEndpoint,
            Stream: false,
            Think: "low",
            KeepAlive: request.KeepAlive,
            Temperature: 0.0,
            NumPredict: request.NumPredict,
            NumCtx: request.NumCtx,
            ResponseFormat: request.ResponseFormat ?? LlmResponseFormat.Json,
            PromptId: LlmPromptCatalog.JsonRepair,
            PromptVersion: LlmPromptCatalog.Version1);

        var repairResponse = await llmClient.GenerateAsync(repairRequest, progress: null, cancellationToken);
        attempts.Add(new("repair", repairResponse.Content));

        var repairParsed = TryParseStrict(parse, repairResponse.Content)
            ?? TryParseStrict(parse, ExtractJsonObject(repairResponse.Content));

        return new LlmJsonInvocationResult<T>(repairParsed, response, attempts);
    }

    private static T? TryParseStrict<T>(Func<string, T?> parse, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }

        try
        {
            return parse(content);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    private static string BuildRepairPrompt(string previousOutput, string? originalSystemPrompt)
    {
        var schemaHint = string.IsNullOrWhiteSpace(originalSystemPrompt)
            ? string.Empty
            : $"Original schema instructions:\n{originalSystemPrompt}\n\n";

        return schemaHint + "Previous output to reformat as valid JSON:\n" + previousOutput;
    }

    /// <summary>
    /// Strips common wrappers from an LLM JSON response (markdown fences, prose around the
    /// object, trailing commentary) and returns the candidate object string.
    /// </summary>
    public static string ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = trimmed.Split('\n');
            trimmed = string.Join('\n', lines.Skip(1).Take(Math.Max(0, lines.Length - 2)));
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }
}

public sealed record LlmJsonInvocationResult<T>(
    T? Value,
    LlmResponse RawResponse,
    IReadOnlyList<LlmJsonAttempt> Attempts)
{
    public bool Succeeded => Value is not null;
}

public sealed record LlmJsonAttempt(string Stage, string RawContent);
