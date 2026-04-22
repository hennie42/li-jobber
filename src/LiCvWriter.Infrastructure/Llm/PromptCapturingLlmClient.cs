using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;

namespace LiCvWriter.Infrastructure.Llm;

public sealed class PromptCapturingLlmClient(ILlmClient inner) : ILlmClient
{
    private const int MaxCapturedPrompts = 20;
    private readonly object gate = new();
    private readonly Queue<LlmPromptSnapshot> capturedPrompts = new();

    public IReadOnlyList<LlmPromptSnapshot> CapturedPrompts
    {
        get
        {
            lock (gate)
            {
                return capturedPrompts.Reverse().ToList();
            }
        }
    }

    public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
        => inner.VerifyModelAvailabilityAsync(cancellationToken);

    public Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
        => inner.GetModelInfoAsync(model, cancellationToken);

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

        lock (gate)
        {
            capturedPrompts.Enqueue(new LlmPromptSnapshot(
                operationLabel ?? TruncateSystemPrompt(request.SystemPrompt),
                request.SystemPrompt ?? string.Empty,
                FormatUserMessages(request.Messages),
                DateTimeOffset.UtcNow));

            while (capturedPrompts.Count > MaxCapturedPrompts)
            {
                capturedPrompts.Dequeue();
            }
        }

        return response;
    }

    public void ClearPrompts()
    {
        lock (gate)
        {
            capturedPrompts.Clear();
        }
    }

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
