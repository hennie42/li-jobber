using System.Text.Json;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Services;

namespace LiCvWriter.Tests.Application;

public sealed class LlmJsonInvokerTests
{
    private sealed record Sample(string Name, int Score);

    private static Sample? ParseSample(string content)
    {
        var doc = JsonSerializer.Deserialize<Sample>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return doc;
    }

    [Fact]
    public async Task InvokeAsync_ParsesOnFirstAttempt_WhenContentIsStrictJson()
    {
        var stub = new StubLlmClient(new Queue<string>(["{\"name\":\"alpha\",\"score\":42}"]));
        var invoker = new LlmJsonInvoker(stub);

        var result = await invoker.InvokeAsync(BuildRequest(), ParseSample);

        Assert.NotNull(result.Value);
        Assert.Equal("alpha", result.Value!.Name);
        Assert.Equal(42, result.Value.Score);
        Assert.Single(result.Attempts);
        Assert.Equal("strict", result.Attempts[0].Stage);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task InvokeAsync_FallsBackToLenientExtraction_WhenContentHasFencesAndProse()
    {
        const string wrapped = "Here is your JSON:\n```json\n{\"name\":\"beta\",\"score\":7}\n```\nLet me know if you need changes.";
        var stub = new StubLlmClient(new Queue<string>([wrapped]));
        var invoker = new LlmJsonInvoker(stub);

        var result = await invoker.InvokeAsync(BuildRequest(), ParseSample);

        Assert.NotNull(result.Value);
        Assert.Equal("beta", result.Value!.Name);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal("lenient", result.Attempts[1].Stage);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task InvokeAsync_TriggersRepairCall_WhenLenientFails_AndSucceeds()
    {
        // First response is unparseable garbage with no JSON object; repair returns valid JSON.
        var stub = new StubLlmClient(new Queue<string>([
            "totally not json, just narrative text",
            "{\"name\":\"gamma\",\"score\":99}"
        ]));
        var invoker = new LlmJsonInvoker(stub);

        var result = await invoker.InvokeAsync(BuildRequest(), ParseSample);

        Assert.NotNull(result.Value);
        Assert.Equal("gamma", result.Value!.Name);
        Assert.Equal(3, result.Attempts.Count);
        Assert.Equal("repair", result.Attempts[2].Stage);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsNullValue_WhenAllAttemptsFail()
    {
        var stub = new StubLlmClient(new Queue<string>(["nope", "still nope"]));
        var invoker = new LlmJsonInvoker(stub);

        var result = await invoker.InvokeAsync(BuildRequest(), ParseSample);

        Assert.Null(result.Value);
        Assert.Equal(3, result.Attempts.Count);
        Assert.Equal(2, stub.Calls);
    }

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("Sure! {\"a\":1} and that's it.", "{\"a\":1}")]
    [InlineData("```\n{\"a\":{\"b\":2}}\n```", "{\"a\":{\"b\":2}}")]
    public void ExtractJsonObject_StripsWrappers(string input, string expected)
    {
        Assert.Equal(expected, LlmJsonInvoker.ExtractJsonObject(input));
    }

    private static LlmRequest BuildRequest() => new(
        Model: "test-model",
        SystemPrompt: "Return JSON.",
        Messages: [new LlmChatMessage("user", "go")],
        UseChatEndpoint: false,
        Stream: false,
        Think: "low",
        KeepAlive: "5m",
        Temperature: 0.0,
        ResponseFormat: LlmResponseFormat.Json);

    private sealed class StubLlmClient(Queue<string> responses) : ILlmClient
    {
        public int Calls { get; private set; }

        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var content = responses.Count > 0 ? responses.Dequeue() : string.Empty;
            return Task.FromResult(new LlmResponse(
                Model: request.Model,
                Content: content,
                Thinking: null,
                Completed: true,
                PromptTokens: null,
                CompletionTokens: null,
                Duration: TimeSpan.Zero));
        }

        public Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<OllamaModelInfo?>(null);
    }
}
