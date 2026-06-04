using System.Diagnostics;
using System.Text;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Services;
using Microsoft.AI.Foundry.Local;

namespace LiCvWriter.Infrastructure.Foundry;

internal static class FoundryStreamingCompletionRunner
{
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(75);
    private const int FoundryContentRepetitionMinLength = 240;
    private const int FoundryThinkingRepetitionMinLength = 120;

    public static async Task<LlmResponse> CompleteAsync(
        OpenAIChatClient chatClient,
        string modelAlias,
        IReadOnlyList<ChatMessage> messages,
        Action<LlmProgressUpdate>? progress,
        Stopwatch stopwatch,
        bool captureThinking,
        CancellationToken cancellationToken)
    {
        var responseBuffer = new StringBuilder();
        var thinkingBuffer = new StringBuilder();
        long? promptTokens = null;
        long? completionTokens = null;
        long sequence = 0;
        var lastProgressElapsed = TimeSpan.Zero;

        await foreach (var chunk in chatClient.CompleteChatStreamingAsync(messages, cancellationToken))
        {
            FoundryOpenAiResponseMapper.MergeStreamingChunk(chunk, responseBuffer, thinkingBuffer, ref promptTokens, ref completionTokens, captureThinking);

            if (captureThinking && StreamingRepetitionDetector.DetectRepetitionLoop(thinkingBuffer, FoundryThinkingRepetitionMinLength))
            {
                throw new TimeoutException(
                    $"Foundry thinking output entered a repetition loop after {thinkingBuffer.Length} characters. " +
                    "The model may need a less reasoning-heavy prompt or a lower thinking setting.");
            }

            if (StreamingRepetitionDetector.DetectRepetitionLoop(responseBuffer, FoundryContentRepetitionMinLength))
            {
                throw new TimeoutException(
                    $"Foundry content output entered a repetition loop after {responseBuffer.Length} characters. " +
                    "The model may need a more constrained prompt or lower temperature.");
            }

            if (progress is not null && stopwatch.Elapsed - lastProgressElapsed >= ProgressUpdateInterval)
            {
                lastProgressElapsed = stopwatch.Elapsed;
                PublishProgress(
                    progress,
                    modelAlias,
                    stopwatch.Elapsed,
                    completed: false,
                    promptTokens,
                    completionTokens,
                    responseBuffer,
                    thinkingBuffer,
                    captureThinking,
                    ++sequence);
            }
        }

        stopwatch.Stop();
        var finalResponseContent = responseBuffer.ToString();
        var finalThinkingContent = captureThinking && thinkingBuffer.Length > 0 ? thinkingBuffer.ToString() : null;

        progress?.Invoke(new LlmProgressUpdate(
            "Generating response",
            "Foundry Local completed the response.",
            modelAlias,
            stopwatch.Elapsed,
            Completed: true,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            ThinkingPreview: FoundryOpenAiResponseMapper.BuildThinkingPreview(finalThinkingContent),
            ResponseContent: finalResponseContent,
            ThinkingContent: finalThinkingContent,
            Sequence: ++sequence));

        return new LlmResponse(
            modelAlias,
            finalResponseContent,
            finalThinkingContent,
            Completed: true,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Duration: stopwatch.Elapsed,
            EvalDuration: completionTokens is > 0 ? stopwatch.Elapsed : null);
    }

    private static void PublishProgress(
        Action<LlmProgressUpdate> progress,
        string modelAlias,
        TimeSpan elapsed,
        bool completed,
        long? promptTokens,
        long? completionTokens,
        StringBuilder responseBuffer,
        StringBuilder thinkingBuffer,
        bool captureThinking,
        long sequence)
    {
        var responseContent = responseBuffer.ToString();
        var thinkingContent = captureThinking && thinkingBuffer.Length > 0 ? thinkingBuffer.ToString() : null;

        progress(new LlmProgressUpdate(
            "Generating response",
            null,
            modelAlias,
            elapsed,
            Completed: completed,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            ThinkingPreview: FoundryOpenAiResponseMapper.BuildThinkingPreview(thinkingContent),
            ResponseContent: responseContent,
            ThinkingContent: thinkingContent,
            Sequence: sequence));
    }
}