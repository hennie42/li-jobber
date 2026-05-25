using System.Text;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels;
using LiCvWriter.Application.Models;
using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;

namespace LiCvWriter.Infrastructure.Foundry;

internal static class FoundryOpenAiResponseMapper
{
    private const string JsonObjectResponseFormatType = "json_object";

    public static bool ShouldCaptureThinking(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return !IsStructuredJsonResponse(request.ResponseFormat);
    }

    public static void ConfigureChatClient(OpenAIChatClient chatClient, LlmRequest request)
    {
        if (request.Temperature is { } temperature)
        {
            chatClient.Settings.Temperature = (float)temperature;
        }

        if (request.NumPredict is > 0 and var maxTokens)
        {
            chatClient.Settings.MaxTokens = maxTokens;
        }

        var responseFormat = BuildResponseFormat(request.ResponseFormat);
        if (responseFormat is not null)
        {
            chatClient.Settings.ResponseFormat = responseFormat;
        }
    }

    public static LlmResponse MapChatCompletion(string modelAlias, ChatCompletionCreateResponse response, TimeSpan duration, bool captureThinking)
    {
        var message = response.Choices?.FirstOrDefault()?.Message;
        var (promptTokens, completionTokens) = ExtractUsage(response);

        return new LlmResponse(
            modelAlias,
            ExtractContent(message),
            captureThinking ? ExtractThinking(message) : null,
            Completed: response.Successful || response.Choices?.Count > 0,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Duration: duration,
            LoadDuration: null,
            PromptEvalDuration: null,
            EvalDuration: completionTokens is > 0 ? duration : null);
    }

    internal static ResponseFormatExtended? BuildResponseFormat(LlmResponseFormat? responseFormat)
    {
        if (responseFormat is null)
        {
            return null;
        }

        if (IsStructuredJsonResponse(responseFormat))
        {
            // Foundry's OpenAI-compatible surface expects OpenAI response_format values.
            // json_object is sufficient for the app's current structured-output flows.
            return new ResponseFormatExtended
            {
                Type = JsonObjectResponseFormatType
            };
        }

        return null;
    }

    internal static (long? PromptTokens, long? CompletionTokens) ExtractUsage(ChatCompletionCreateResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return (
            response.Usage is null ? null : (long?)response.Usage.PromptTokens,
            response.Usage?.CompletionTokens is { } tokenCount ? (long?)tokenCount : null);
    }

    internal static string ExtractContent(ChatMessage? message)
    {
        if (!string.IsNullOrWhiteSpace(message?.Content))
        {
            return message.Content;
        }

        if (message?.Contents is { Count: > 0 })
        {
            var combined = string.Concat(
                message.Contents
                    .Select(static content => content.Text)
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));

            if (!string.IsNullOrWhiteSpace(combined))
            {
                return combined;
            }
        }

        return message?.ContentCalculated is string calculated && !string.IsNullOrWhiteSpace(calculated)
            ? calculated
            : string.Empty;
    }

    internal static string? ExtractThinking(ChatMessage? message)
        => string.IsNullOrWhiteSpace(message?.ReasoningContent) ? null : message.ReasoningContent;

    internal static string? BuildThinkingPreview(string? thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking))
        {
            return null;
        }

        const int maxCharacters = 320;
        var trimmed = thinking.Trim();
        if (trimmed.Length <= maxCharacters)
        {
            return trimmed;
        }

        return $"...{trimmed[^maxCharacters..]}";
    }

    internal static void MergeStreamingChunk(
        ChatCompletionCreateResponse chunk,
        StringBuilder responseBuffer,
        StringBuilder thinkingBuffer,
        ref long? promptTokens,
        ref long? completionTokens,
        bool captureThinking)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(responseBuffer);
        ArgumentNullException.ThrowIfNull(thinkingBuffer);

        var message = chunk.Choices?.FirstOrDefault()?.Message;
        var contentDelta = ExtractContent(message);
        if (!string.IsNullOrEmpty(contentDelta))
        {
            AppendStreamingText(responseBuffer, contentDelta);
        }

        var thinkingDelta = captureThinking ? ExtractThinking(message) : null;
        if (!string.IsNullOrEmpty(thinkingDelta))
        {
            AppendStreamingText(thinkingBuffer, thinkingDelta);
        }

        var usage = ExtractUsage(chunk);
        promptTokens = usage.PromptTokens ?? promptTokens;
        completionTokens = usage.CompletionTokens ?? completionTokens;
    }

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

    private static bool IsStructuredJsonResponse(LlmResponseFormat? responseFormat)
        => responseFormat is not null
           && (!string.IsNullOrWhiteSpace(responseFormat.SchemaJson)
               || string.Equals(responseFormat.Format, "json", StringComparison.OrdinalIgnoreCase));
}