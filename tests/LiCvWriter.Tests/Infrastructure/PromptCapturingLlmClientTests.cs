using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Infrastructure.Llm;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class PromptCapturingLlmClientTests
{
    [Fact]
    public async Task GenerateAsync_WhenMoreThanMaxPromptsCaptured_TrimsOldestEntries()
    {
        var client = new PromptCapturingLlmClient(new StubLlmClient());

        for (var index = 1; index <= 25; index++)
        {
            await client.GenerateAsync(new LlmRequest(
                "model",
                $"System prompt {index}",
                [new LlmChatMessage("user", $"User prompt {index}")]));
        }

        Assert.Equal(20, client.CapturedPrompts.Count);
        Assert.DoesNotContain(client.CapturedPrompts, prompt => string.Equals(prompt.SystemPrompt, "System prompt 5", StringComparison.Ordinal));
        Assert.Contains(client.CapturedPrompts, prompt => string.Equals(prompt.SystemPrompt, "System prompt 6", StringComparison.Ordinal));
        Assert.Contains(client.CapturedPrompts, prompt => string.Equals(prompt.SystemPrompt, "System prompt 25", StringComparison.Ordinal));
    }

    private sealed class StubLlmClient : ILlmClient
    {
        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse(request.Model, "ok", null, true, null, null, null));
    }
}