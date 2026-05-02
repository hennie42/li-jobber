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
                    {"model":"session-model","message":{"content":"Northwind","thinking":"Thinking about Northwind"},"done":false}
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

    [Fact]
    public async Task GenerateAsync_WhenStreamingStalls_ThrowsTimeoutException()
    {
        using var httpClient = CreateClient(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/chat" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingAfterFirstLineStream())
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var client = new OllamaClient(httpClient, new OllamaOptions
        {
            Model = "session-model",
            StreamingInactivitySeconds = 1
        });

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => client.GenerateAsync(
            new LlmRequest(
                "session-model",
                null,
                [new LlmChatMessage("user", "hi")],
                UseChatEndpoint: true,
                Stream: true)));

        Assert.Contains("stopped streaming", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_WhenThinkingEntersRepetitionLoop_ThrowsTimeoutException()
    {
        // Build incremental thinking chunks that produce a repeating pattern in the buffer.
        // Each chunk is a small unique word-level delta so AppendStreamingText appends them.
        var phrase = "worked with multiple teams doing work with generative AI and I ";
        var chunks = new StringBuilder();
        for (var i = 0; i < 15; i++)
        {
            foreach (var word in phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                chunks.AppendLine($"{{\"model\":\"session-model\",\"message\":{{\"content\":\"\",\"thinking\":\"{EscapeJson(word + " ")}\"}},\"done\":false}}");
            }
        }

        using var httpClient = CreateClient(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/chat" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(chunks.ToString(), Encoding.UTF8, "application/x-ndjson")
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var client = new OllamaClient(httpClient, new OllamaOptions
        {
            Model = "session-model",
            RepetitionDetectionThinkingMinLength = 200
        });

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => client.GenerateAsync(
            new LlmRequest(
                "session-model",
                null,
                [new LlmChatMessage("user", "hi")],
                UseChatEndpoint: true,
                Stream: true)));

        Assert.Contains("repetition loop", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("short text", 500, false)]
    [InlineData("", 500, false)]
    public void DetectRepetitionLoop_ShortBuffer_ReturnsFalse(string text, int minLength, bool expected)
    {
        var buffer = new StringBuilder(text);
        Assert.Equal(expected, OllamaClient.DetectRepetitionLoop(buffer, minLength));
    }

    [Fact]
    public void DetectRepetitionLoop_RepeatingPattern_ReturnsTrue()
    {
        var pattern = "worked with multiple teams doing ";
        var buffer = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            buffer.Append(pattern);
        }

        Assert.True(OllamaClient.DetectRepetitionLoop(buffer, 100));
    }

    [Fact]
    public void DetectRepetitionLoop_NormalVariedText_ReturnsFalse()
    {
        var buffer = new StringBuilder();
        buffer.Append("The candidate has extensive experience in software architecture and cloud-native solutions. ");
        buffer.Append("They led cross-functional teams at multiple organizations, delivering microservice platforms. ");
        buffer.Append("Key achievements include reducing deployment time by 60% and improving system reliability to 99.9%. ");
        buffer.Append("Their expertise spans Azure, Kubernetes, and event-driven architectures with strong mentoring skills. ");
        buffer.Append("This is clearly a strong candidate for the role with relevant domain experience in financial services. ");
        buffer.Append("The recommendation highlights their ability to communicate complex technical concepts to stakeholders. ");

        Assert.False(OllamaClient.DetectRepetitionLoop(buffer, 100));
    }

    [Fact]
    public void DetectRepetitionLoop_DisabledWithZeroMinLength_ReturnsFalse()
    {
        var pattern = "repeating phrase ";
        var buffer = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            buffer.Append(pattern);
        }

        Assert.False(OllamaClient.DetectRepetitionLoop(buffer, 0));
    }

    [Fact]
    public async Task GenerateAsync_PayloadIncludesFormatJson_AndNumCtx_WhenRequested()
    {
        string? capturedBody = null;
        using var httpClient = CreateClient(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/chat")
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"model":"session-model","message":{"content":"{\"ok\":true}"},"done":true,"prompt_eval_count":4,"eval_count":3,"total_duration":1000000000,"load_duration":250000000,"prompt_eval_duration":100000000,"eval_duration":750000000}""",
                        Encoding.UTF8,
                        "application/x-ndjson")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new OllamaClient(httpClient, new OllamaOptions { Model = "session-model" });

        var response = await client.GenerateAsync(new LlmRequest(
            "session-model",
            null,
            [new LlmChatMessage("user", "hi")],
            UseChatEndpoint: true,
            Stream: true,
            NumCtx: 8192,
            ResponseFormat: LlmResponseFormat.Json));

        Assert.NotNull(capturedBody);
        using var doc = System.Text.Json.JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        Assert.Equal("json", root.GetProperty("format").GetString());
        var options = root.GetProperty("options");
        Assert.Equal(8192, options.GetProperty("num_ctx").GetInt32());
        Assert.True(options.TryGetProperty("temperature", out _));

        // Duration capture from the done chunk.
        Assert.Equal(TimeSpan.FromMilliseconds(250), response.LoadDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(100), response.PromptEvalDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(750), response.EvalDuration);
    }

    [Fact]
    public async Task GenerateAsync_PayloadOmitsFormat_WhenResponseFormatIsNull()
    {
        string? capturedBody = null;
        using var httpClient = CreateClient(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/chat")
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"model":"session-model","message":{"content":"ok"},"done":true,"total_duration":1000000000}""",
                        Encoding.UTF8,
                        "application/x-ndjson")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new OllamaClient(httpClient, new OllamaOptions { Model = "session-model" });

        await client.GenerateAsync(new LlmRequest(
            "session-model",
            null,
            [new LlmChatMessage("user", "hi")],
            UseChatEndpoint: true,
            Stream: true));

        Assert.NotNull(capturedBody);
        using var doc = System.Text.Json.JsonDocument.Parse(capturedBody!);
        Assert.False(doc.RootElement.TryGetProperty("format", out _));
        Assert.False(doc.RootElement.GetProperty("options").TryGetProperty("num_ctx", out _));
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(new StubMessageHandler(responseFactory))
        {
            BaseAddress = new Uri("http://localhost:11434/api/")
        };

    private sealed class BlockingAfterFirstLineStream : Stream
    {
        private readonly byte[] payload = Encoding.UTF8.GetBytes("{\"model\":\"session-model\",\"message\":{\"content\":\"Hel\"},\"done\":false}\n");
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= payload.Length)
            {
                throw new NotSupportedException("Synchronous reads are not supported after the initial payload.");
            }

            var available = Math.Min(count, payload.Length - position);
            payload.AsSpan(position, available).CopyTo(buffer.AsSpan(offset, available));
            position += available;
            return available;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position < payload.Length)
            {
                var available = Math.Min(buffer.Length, payload.Length - position);
                payload.AsMemory(position, available).CopyTo(buffer);
                position += available;
                return available;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class StubMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
