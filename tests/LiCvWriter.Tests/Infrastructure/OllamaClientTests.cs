using System.Net;
using System.Text;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Infrastructure.Llm;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class OllamaClientTests
{
    [Fact]
    public async Task VerifyModelAvailabilityAsync_ReturnsAvailableAndRunningModels()
    {
        using var httpClient = CreateClient(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/version" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"0.19.0"}""", Encoding.UTF8, "application/json")
            },
            "/api/tags" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"models":[{"name":"gpt-oss:120b"},{"name":"small-model:latest"}]}""", Encoding.UTF8, "application/json")
            },
            "/api/ps" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"models":[{"name":"gpt-oss:120b","model":"gpt-oss:120b","expires_at":"2026-04-05T12:00:00Z","size_vram":2048}]}""", Encoding.UTF8, "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var client = new OllamaClient(httpClient, new OllamaOptions
        {
            Model = "gpt-oss:120b",
            StatusTimeoutSeconds = 1
        });

        var result = await client.VerifyModelAvailabilityAsync();

        Assert.Equal("0.19.0", result.Version);
        Assert.True(result.Installed);
        Assert.True(result.IsConfiguredModelLoaded);
        Assert.Equal(2, result.AvailableModels.Count);
        Assert.Single(result.EffectiveRunningModels);
        Assert.Equal("gpt-oss:120b", result.EffectiveRunningModels[0].Name);
    }

    [Fact]
    public async Task VerifyModelAvailabilityAsync_WhenRunningModelsFail_ReturnsWithoutLoadedModels()
    {
        using var httpClient = CreateClient(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/version" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"0.19.0"}""", Encoding.UTF8, "application/json")
            },
            "/api/tags" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"models":[{"name":"gpt-oss:120b"}]}""", Encoding.UTF8, "application/json")
            },
            "/api/ps" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var client = new OllamaClient(httpClient, new OllamaOptions
        {
            Model = "gpt-oss:120b",
            StatusTimeoutSeconds = 1
        });

        var result = await client.VerifyModelAvailabilityAsync();

        Assert.True(result.Installed);
        Assert.Empty(result.EffectiveRunningModels);
    }

    [Fact]
    public async Task GenerateAsync_WithStreamingChat_AggregatesChunksAndReportsProgress()
    {
        using var httpClient = CreateClient(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/chat" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"model":"session-model","message":{"content":"Hel","thinking":"Reasoning about"},"done":false}
                    {"model":"session-model","message":{"content":"lo","thinking":" the response"},"done":false}
                    {"model":"session-model","message":{"content":""},"done":true,"prompt_eval_count":12,"eval_count":34,"total_duration":2000000000}
                    """,
                    Encoding.UTF8,
                    "application/x-ndjson")
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var client = new OllamaClient(httpClient, new OllamaOptions { Model = "session-model" });
        var progress = new List<LlmProgressUpdate>();

        var result = await client.GenerateAsync(
            new LlmRequest(
                "session-model",
                null,
                [new LlmChatMessage("user", "hi")],
                UseChatEndpoint: true,
                Stream: true),
            progress.Add);

        Assert.Equal("Hello", result.Content);
        Assert.True(result.Completed);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(34, result.CompletionTokens);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Duration);
        Assert.NotEmpty(progress);
        Assert.Contains(progress, update => update.Message == "Waiting for Ollama response");
        Assert.Contains(progress, update => update.ResponseContent == "Hel");
        Assert.Contains(progress, update => update.PromptTokens == 12 && update.CompletionTokens == 34);
        Assert.Contains(progress, update => update.ThinkingPreview is not null && update.ThinkingPreview.Contains("Reasoning about", StringComparison.Ordinal));
        Assert.Contains(progress, update => update.ThinkingContent == "Reasoning about the response");
        Assert.Contains(progress, update => update.EstimatedRemaining == TimeSpan.Zero && update.Completed);
        Assert.True(progress[^1].Sequence > progress[0].Sequence);
        Assert.Contains(progress, update => update.Completed);
    }

    [Fact]
    public async Task GenerateAsync_WithCumulativeStreamingChat_DeduplicatesRepeatedContent()
    {
        using var httpClient = CreateClient(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/chat" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"model":"session-model","message":{"content":"Novo","thinking":"Thinking about Novo"},"done":false}
                    {"model":"session-model","message":{"content":"Northwind Health","thinking":"Thinking about Northwind Health"},"done":false}
                    {"model":"session-model","message":{"content":"Northwind Health Northwind Health","thinking":"Thinking about Northwind Health Northwind Health"},"done":false}
                    {"model":"session-model","message":{"content":""},"done":true,"prompt_eval_count":11,"eval_count":22,"total_duration":1000000000}
                    """,
                    Encoding.UTF8,
                    "application/x-ndjson")
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var client = new OllamaClient(httpClient, new OllamaOptions { Model = "session-model" });
        var progress = new List<LlmProgressUpdate>();

        var result = await client.GenerateAsync(
            new LlmRequest(
                "session-model",
                null,
                [new LlmChatMessage("user", "hi")],
                UseChatEndpoint: true,
                Stream: true),
            progress.Add);

        Assert.Equal("Northwind Health Northwind Health", result.Content);
        Assert.Equal("Thinking about Northwind Health Northwind Health", result.Thinking);
        Assert.Contains(progress, update => update.ResponseContent == "Northwind Health Northwind Health");
        Assert.Contains(progress, update => update.ThinkingContent == "Thinking about Northwind Health Northwind Health");
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(new StubMessageHandler(responseFactory))
        {
            BaseAddress = new Uri("http://localhost:11434/api/")
        };

    private sealed class StubMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}