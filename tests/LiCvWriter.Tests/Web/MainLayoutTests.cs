using Bunit;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Web.Services;
using MainLayoutComponent = LiCvWriter.Web.Components.Layout.MainLayout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Tests.Web;

public sealed class MainLayoutTests
{
    [Fact]
    public async Task Render_WhenBusyBenchmarkOnlyHasLastCompletedTelemetry_ShowsRecentModelTrace()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var workspace = new WorkspaceSession(new OllamaOptions { Model = "phi", Think = "medium" });
        var operations = new OperationStatusService();
        var releaseBenchmark = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        context.Services.AddSingleton(workspace);
        context.Services.AddSingleton(operations);

        var cut = context.Render<MainLayoutComponent>(parameters => parameters.Add(
            component => component.Body,
            (RenderFragment)(builder => builder.AddMarkupContent(0, "<div>Body</div>"))));

        var benchmarkTask = operations.RunAsync("Benchmarking selected models", "Preparing live benchmark.", async () =>
        {
            operations.BeginLlmOperation("Benchmarking selected models", "Streaming the warm-up call.");
            operations.UpdateCurrent(new LlmProgressUpdate(
                "Warm-up completed",
                "The model finished streaming the warm-up call.",
                "deepseek-r1-1.5b",
                TimeSpan.FromSeconds(4),
                PromptTokens: 12,
                CompletionTokens: 34,
                ThinkingPreview: "Reasoning captured",
                ThinkingContent: "Reasoning captured in full",
                Completed: true,
                Sequence: 2));
            operations.UpdateCurrent("Scoring benchmark result", "Calculating quality and fit metrics.");

            await releaseBenchmark.Task;
        });

        cut.WaitForAssertion(() =>
        {
            var activityMonitor = cut.Find(".sidebar-crt-screen-activity").TextContent;

            Assert.Contains("STATE : Recent model", activityMonitor, StringComparison.Ordinal);
            Assert.Contains("MODEL : deepseek-r1-1.5b", activityMonitor, StringComparison.Ordinal);
            Assert.Contains("TOKENS: P 12 / C 34", activityMonitor, StringComparison.Ordinal);
        });

        releaseBenchmark.SetResult();
        await benchmarkTask;
    }

    [Fact]
    public async Task Render_WhenLiveTelemetryOnlyHasResponseContent_ShowsResponseInReasoningMonitor()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var workspace = new WorkspaceSession(new OllamaOptions { Model = "phi", Think = "medium" });
        var operations = new OperationStatusService();
        var releaseBenchmark = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        context.Services.AddSingleton(workspace);
        context.Services.AddSingleton(operations);

        var cut = context.Render<MainLayoutComponent>(parameters => parameters.Add(
            component => component.Body,
            (RenderFragment)(builder => builder.AddMarkupContent(0, "<div>Body</div>"))));

        var benchmarkTask = operations.RunAsync("Benchmarking selected models", "Preparing live benchmark.", async () =>
        {
            operations.BeginLlmOperation("Benchmarking selected models", "Streaming the structured benchmark response.");
            operations.UpdateCurrent(new LlmProgressUpdate(
                "Generating response",
                "Structured output stream is active.",
                "phi-4-reasoning",
                TimeSpan.FromSeconds(9),
                PromptTokens: 24,
                CompletionTokens: 48,
                ResponseContent: "{\"roleTitle\":\"Senior Backend Engineer\"}",
                Sequence: 1));

            await releaseBenchmark.Task;
        });

        cut.WaitForAssertion(() =>
        {
            var reasoningMonitor = cut.FindAll(".sidebar-crt-screen")
                .Single(element => !element.ClassList.Contains("sidebar-crt-screen-activity"))
                .TextContent;

            Assert.DoesNotContain(
                "No reasoning text captured yet. Run a streamed LLM operation to light up this monitor.",
                reasoningMonitor,
                StringComparison.Ordinal);
            Assert.Contains("Senior Backend Engineer", reasoningMonitor, StringComparison.Ordinal);
        });

        releaseBenchmark.SetResult();
        await benchmarkTask;
    }
}