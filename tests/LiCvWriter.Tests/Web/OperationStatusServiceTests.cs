using LiCvWriter.Application.Models;
using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class OperationStatusServiceTests
{
    [Fact]
    public void UpdateCurrent_WithLlmProgress_StoresTelemetryAndKeepsLastCompletedSnapshot()
    {
        var service = new OperationStatusService();

        service.UpdateCurrent(new LlmProgressUpdate(
            "Generating draft",
            "The model is streaming.",
            "session-model",
            TimeSpan.FromSeconds(3),
            PromptTokens: 12,
            CompletionTokens: 18,
            ThinkingPreview: "Current reasoning"));

        Assert.NotNull(service.CurrentLlmTelemetry);
        Assert.NotNull(service.ActiveLlmTelemetry);
        Assert.Equal("Generating draft", service.CurrentLlmTelemetry!.Message);
        Assert.Equal(12, service.CurrentLlmTelemetry.PromptTokens);
        Assert.Equal(18, service.CurrentLlmTelemetry.CompletionTokens);
        Assert.True(service.CurrentLlmTelemetry.HasThinkingPreview);
        Assert.Null(service.LastCompletedLlmTelemetry);

        service.UpdateCurrent(new LlmProgressUpdate(
            "Draft completed",
            "The stream finished.",
            "session-model",
            TimeSpan.FromSeconds(5),
            Completed: true,
            PromptTokens: 12,
            CompletionTokens: 34,
            EstimatedRemaining: TimeSpan.Zero,
            ThinkingPreview: "Final reasoning"));

        Assert.NotNull(service.LastCompletedLlmTelemetry);
        Assert.Null(service.ActiveLlmTelemetry);
        Assert.True(service.LastCompletedLlmTelemetry!.Completed);
        Assert.Equal(34, service.LastCompletedLlmTelemetry.CompletionTokens);
        Assert.Equal(TimeSpan.Zero, service.LastCompletedLlmTelemetry.EstimatedRemaining);

        service.Success("Completed: Generating draft", "Ready for the next step.");

        Assert.Null(service.CurrentLlmTelemetry);
        Assert.Null(service.ActiveLlmTelemetry);
        Assert.NotNull(service.LastCompletedLlmTelemetry);
    }
}