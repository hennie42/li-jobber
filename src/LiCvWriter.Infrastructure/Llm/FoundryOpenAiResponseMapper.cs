using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels;
using LiCvWriter.Application.Models;
using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;

namespace LiCvWriter.Infrastructure.Foundry;

internal static class FoundryOpenAiResponseMapper
{
    private const string JsonObjectResponseFormatType = "json_object";

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

    public static LlmResponse MapChatCompletion(string modelAlias, ChatCompletionCreateResponse response, TimeSpan duration)
    {
        var message = response.Choices?.FirstOrDefault()?.Message;
        var promptTokens = response.Usage is null ? null : (long?)response.Usage.PromptTokens;
        var completionTokens = response.Usage?.CompletionTokens is { } tokenCount ? (long?)tokenCount : null;

        return new LlmResponse(
            modelAlias,
            ExtractContent(message),
            ExtractThinking(message),
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

        if (!string.IsNullOrWhiteSpace(responseFormat.SchemaJson)
            || string.Equals(responseFormat.Format, "json", StringComparison.OrdinalIgnoreCase))
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

    private static string? ExtractThinking(ChatMessage? message)
        => string.IsNullOrWhiteSpace(message?.ReasoningContent) ? null : message.ReasoningContent;
}