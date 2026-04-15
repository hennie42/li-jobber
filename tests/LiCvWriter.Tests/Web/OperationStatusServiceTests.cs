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
            ThinkingPreview: "Current reasoning",
            ThinkingContent: "Current reasoning in full",
            Sequence: 2));

        Assert.NotNull(service.CurrentLlmTelemetry);
        Assert.NotNull(service.ActiveLlmTelemetry);
        Assert.Equal("Generating draft", service.CurrentLlmTelemetry!.Message);
        Assert.Equal(12, service.CurrentLlmTelemetry.PromptTokens);
        Assert.Equal(18, service.CurrentLlmTelemetry.CompletionTokens);
        Assert.True(service.CurrentLlmTelemetry.HasThinkingPreview);
        Assert.True(service.CurrentLlmTelemetry.HasThinkingContent);
        Assert.Equal(2, service.CurrentLlmTelemetry.Sequence);
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
            ThinkingPreview: "Final reasoning",
            ThinkingContent: "Final reasoning in full",
            Sequence: 3));

        Assert.NotNull(service.LastCompletedLlmTelemetry);
        Assert.Null(service.ActiveLlmTelemetry);
        Assert.True(service.LastCompletedLlmTelemetry!.Completed);
        Assert.Equal(34, service.LastCompletedLlmTelemetry.CompletionTokens);
        Assert.Equal(TimeSpan.Zero, service.LastCompletedLlmTelemetry.EstimatedRemaining);
        Assert.Equal("Final reasoning in full", service.LastCompletedLlmTelemetry.ThinkingContent);
        Assert.Equal(3, service.LastCompletedLlmTelemetry.Sequence);

        service.Success("Completed: Generating draft", "Ready for the next step.");

        Assert.Null(service.CurrentLlmTelemetry);
        Assert.Null(service.ActiveLlmTelemetry);
        Assert.NotNull(service.LastCompletedLlmTelemetry);
    }

    [Fact]
    public void Entries_ReturnsSnapshotThatIsSafeAfterFurtherUpdates()
    {
        var service = new OperationStatusService();

        service.Info("First message");
        var snapshot = service.Entries;

        service.Info("Second message");

        Assert.Single(snapshot);
        Assert.Equal("First message", snapshot[0].Message);
        Assert.Equal(2, service.Entries.Count);
    }
}