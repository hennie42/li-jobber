using System.Text;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Services;
using LiCvWriter.Infrastructure.Foundry;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class FoundryOpenAiResponseMapperTests
{
    [Fact]
    public void BuildResponseFormat_WhenJsonRequested_ReturnsJsonObjectFormat()
    {
        var responseFormat = FoundryOpenAiResponseMapper.BuildResponseFormat(LlmResponseFormat.Json);

        Assert.NotNull(responseFormat);
        Assert.Equal("json_object", responseFormat!.Type);
    }

    [Fact]
    public void ExtractContent_WhenMultipartJsonIsSplitAcrossContentItems_PreservesValidJson()
    {
        var message = new ChatMessage
        {
            Contents =
            [
                new MessageContent { Type = "text", Text = "{" },
                new MessageContent { Type = "text", Text = "\"roleTitle\":\"Senior Backend Engineer\"," },
                new MessageContent { Type = "text", Text = "\"companyName\":\"Acme Robotics\"," },
                new MessageContent { Type = "text", Text = "\"mustHaveThemes\":[\"go\",\"kubernetes\",\"distributed systems\"]}" }
            ]
        };

        var content = FoundryOpenAiResponseMapper.ExtractContent(message);

        Assert.Equal(
            "{\"roleTitle\":\"Senior Backend Engineer\",\"companyName\":\"Acme Robotics\",\"mustHaveThemes\":[\"go\",\"kubernetes\",\"distributed systems\"]}",
            content);
        Assert.InRange(ModelBenchmarkFixtures.Score(content), 0.99, 1.0);
    }

    [Fact]
    public void MapChatCompletion_WhenUsageIsPresent_PopulatesBenchmarkMetrics()
    {
        var response = new ChatCompletionCreateResponse
        {
            Choices =
            [
                new ChatChoiceResponse
                {
                    Message = new ChatMessage
                    {
                        Content = """
                            {
                              "roleTitle": "Senior Backend Engineer",
                              "companyName": "Acme Robotics",
                              "mustHaveThemes": ["go"]
                            }
                            """,
                        ReasoningContent = "trace"
                    }
                }
            ],
            Usage = new UsageResponse
            {
                PromptTokens = 8,
                CompletionTokens = 24
            }
        };

        var mapped = FoundryOpenAiResponseMapper.MapChatCompletion("deepseek-r1-14b", response, TimeSpan.FromSeconds(2));

        Assert.Equal(8, mapped.PromptTokens);
        Assert.Equal(24, mapped.CompletionTokens);
        Assert.Equal(TimeSpan.FromSeconds(2), mapped.EvalDuration);
        Assert.Equal("trace", mapped.Thinking);
        Assert.Contains("\"roleTitle\": \"Senior Backend Engineer\"", mapped.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeStreamingChunk_WhenChunkCarriesMultipartContentReasoningAndUsage_PopulatesBuffersAndTokenCounts()
    {
        var chunk = new ChatCompletionCreateResponse
        {
            Choices =
            [
                new ChatChoiceResponse
                {
                    Message = new ChatMessage
                    {
                        Contents =
                        [
                            new MessageContent { Type = "text", Text = "{" },
                            new MessageContent { Type = "text", Text = "\"roleTitle\":\"Senior Backend Engineer\"}" }
                        ],
                        ReasoningContent = "step one"
                    }
                }
            ],
            Usage = new UsageResponse
            {
                PromptTokens = 9,
                CompletionTokens = 21
            }
        };

        var responseBuffer = new StringBuilder();
        var thinkingBuffer = new StringBuilder();
        long? promptTokens = null;
        long? completionTokens = null;

        FoundryOpenAiResponseMapper.MergeStreamingChunk(chunk, responseBuffer, thinkingBuffer, ref promptTokens, ref completionTokens);

        Assert.Equal("{\"roleTitle\":\"Senior Backend Engineer\"}", responseBuffer.ToString());
        Assert.Equal("step one", thinkingBuffer.ToString());
        Assert.Equal(9, promptTokens);
        Assert.Equal(21, completionTokens);
        Assert.Equal("step one", FoundryOpenAiResponseMapper.BuildThinkingPreview(thinkingBuffer.ToString()));
    }

    [Fact]
    public void MergeStreamingChunk_WhenChunkCarriesCumulativeReasoningAndContent_DeduplicatesSnapshots()
    {
        var firstChunk = new ChatCompletionCreateResponse
        {
            Choices =
            [
                new ChatChoiceResponse
                {
                    Message = new ChatMessage
                    {
                        Content = "Engineer",
                        ReasoningContent = "Engineer"
                    }
                }
            ]
        };

        var secondChunk = new ChatCompletionCreateResponse
        {
            Choices =
            [
                new ChatChoiceResponse
                {
                    Message = new ChatMessage
                    {
                        Content = "Engineer Engineer",
                        ReasoningContent = "Engineer Engineer"
                    }
                }
            ]
        };

        var thirdChunk = new ChatCompletionCreateResponse
        {
            Choices =
            [
                new ChatChoiceResponse
                {
                    Message = new ChatMessage
                    {
                        Content = "Engineer Engineer Engineer",
                        ReasoningContent = "Engineer Engineer Engineer"
                    }
                }
            ]
        };

        var responseBuffer = new StringBuilder();
        var thinkingBuffer = new StringBuilder();
        long? promptTokens = null;
        long? completionTokens = null;

        FoundryOpenAiResponseMapper.MergeStreamingChunk(firstChunk, responseBuffer, thinkingBuffer, ref promptTokens, ref completionTokens);
        FoundryOpenAiResponseMapper.MergeStreamingChunk(secondChunk, responseBuffer, thinkingBuffer, ref promptTokens, ref completionTokens);
        FoundryOpenAiResponseMapper.MergeStreamingChunk(thirdChunk, responseBuffer, thinkingBuffer, ref promptTokens, ref completionTokens);

        Assert.Equal("Engineer Engineer Engineer", responseBuffer.ToString());
        Assert.Equal("Engineer Engineer Engineer", thinkingBuffer.ToString());
    }
}