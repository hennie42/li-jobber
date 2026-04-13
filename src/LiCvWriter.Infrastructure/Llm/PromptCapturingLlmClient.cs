using System.Collections.Concurrent;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;

namespace LiCvWriter.Infrastructure.Llm;

public sealed class PromptCapturingLlmClient(ILlmClient inner) : ILlmClient
{
    private readonly ConcurrentBag<LlmPromptSnapshot> capturedPrompts = [];

    public IReadOnlyList<LlmPromptSnapshot> CapturedPrompts => capturedPrompts.Reverse().ToList();

    public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
        => inner.VerifyModelAvailabilityAsync(cancellationToken);

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? operationLabel = null;

        Action<LlmProgressUpdate>? wrappedProgress = progress is null
            ? null
            : update =>
            {
                operationLabel ??= update.Message;
                progress(update);
            };

        var response = await inner.GenerateAsync(request, wrappedProgress, cancellationToken);

        capturedPrompts.Add(new LlmPromptSnapshot(
            operationLabel ?? TruncateSystemPrompt(request.SystemPrompt),
            request.SystemPrompt ?? string.Empty,
            FormatUserMessages(request.Messages),
            DateTimeOffset.UtcNow));

        return response;
    }

    public void ClearPrompts() => capturedPrompts.Clear();

    private static string TruncateSystemPrompt(string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return "LLM call";
        }

        var firstLine = systemPrompt.AsSpan().TrimStart();
        var lineEnd = firstLine.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
        {
            firstLine = firstLine[..lineEnd];
        }

        return firstLine.Length > 80
            ? $"{firstLine[..77]}..."
            : firstLine.ToString();
    }

    private static string FormatUserMessages(IReadOnlyList<LlmChatMessage> messages)
        => string.Join("\n\n---\n\n", messages.Select(static message => message.Content));
}
