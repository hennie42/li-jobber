using AngleSharp.Dom;
using Bunit;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using System.Reflection;
using LiCvWriter.Web.Services;
using LlmSetupPage = LiCvWriter.Web.Components.Pages.Setup.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Tests.Web;

public sealed class LlmSetupPageTests
{
    [Fact]
    public void Render_WhenOllamaModelsAreAvailable_ShowsSharedActionLayout()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions);
        workspace.SetOllamaAvailability(new LlmModelAvailability(
            Version: "0.1.0",
            Model: "phi",
            Installed: true,
            AvailableModels: ["phi", "mistral"],
            RunningModels: Array.Empty<LlmRunningModel>(),
            Provider: LlmProviderKind.Ollama));
        workspace.SetLlmSessionSettings("phi", "medium");

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshot()));

        var cut = context.Render<LlmSetupPage>();

        Assert.Contains("Session model", cut.Markup);
        Assert.Contains("Model actions", cut.Markup);
        Assert.Contains("Select all visible", cut.Markup);
        Assert.Contains("Select none", cut.Markup);
        Assert.Contains("Benchmark selected", cut.Markup);
        Assert.Contains("Download selected", cut.Markup);
        Assert.Contains("type=\"checkbox\"", cut.Markup);
    }

    [Fact]
    public void Interactions_WhenFoundryModelIsChecked_EnablesBatchActions()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        workspace.SetFoundryAvailability(CreateFoundrySnapshot().Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshot()));

        var cut = context.Render<LlmSetupPage>();
        cut.WaitForAssertion(() => Assert.Contains("Remove models classified as too large for interactive use after benchmark.", cut.Markup));

        var modelCheckbox = cut.FindAll("table input[type=checkbox]").First();
        modelCheckbox.Change(true);

        var downloadButton = FindButton(cut, "Download selected");
        var benchmarkButton = FindButton(cut, "Benchmark selected");

        Assert.False(downloadButton.HasAttribute("disabled"));
        Assert.False(benchmarkButton.HasAttribute("disabled"));
    }

    [Fact]
    public void Interactions_WhenSelectAllVisibleIsClickedWithFoundryFilter_SelectsOnlyVisibleModels()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        workspace.SetFoundryAvailability(CreateFoundrySnapshot().Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshot()));

        var cut = context.Render<LlmSetupPage>();
        cut.WaitForAssertion(() => Assert.Contains("Filter catalog", cut.Markup));

        cut.Find("#foundryModelFilter").Change("phi");
        FindButton(cut, "Select all visible").Click();

        cut.WaitForAssertion(() => Assert.Contains("Selected models: 1. 1 selected model(s) will be downloaded before use.", cut.Markup));
        Assert.Single(cut.FindAll("tbody input[type=checkbox][checked]"));
    }

    [Fact]
    public void Render_WhenFoundryCatalogIsLoaded_ShowsDescriptionsAndAlternatingAccentRows()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        workspace.SetFoundryAvailability(CreateFoundrySnapshot().Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshot()));

        var cut = context.Render<LlmSetupPage>();
        cut.WaitForAssertion(() => Assert.Contains("Lightweight reasoning and chat tasks.", cut.Markup));

        var accentRows = cut.FindAll(".setup-list-table tbody tr.setup-list-row-accent");

        Assert.Contains("Lightweight reasoning and chat tasks.", cut.Markup);
        Assert.Contains("Balanced general-purpose local assistant.", cut.Markup);
        Assert.Single(accentRows);
    }

    [Fact]
    public void Render_WhenFoundrySelected_DefaultsRemovalAfterBenchmarkToChecked()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        workspace.SetFoundryAvailability(CreateFoundrySnapshot().Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshot()));

        var cut = context.Render<LlmSetupPage>();
        cut.WaitForAssertion(() => Assert.Contains("Remove models classified as too large for interactive use after benchmark.", cut.Markup));

        var removalLabel = cut.FindAll("label")
            .Single(label => label.TextContent.Contains("Remove models classified as too large for interactive use after benchmark.", StringComparison.Ordinal));
        var removalCheckbox = removalLabel.QuerySelector("input[type='checkbox']");

        Assert.NotNull(removalCheckbox);
        Assert.True(removalCheckbox!.HasAttribute("checked"));
    }

    [Fact]
    public void Render_WhenFoundryExecutionProvidersNeedRegistration_ShowsAccelerationGuidance()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        workspace.SetFoundryAvailability(CreateFoundrySnapshot(CreateUnregisteredAccelerationSnapshot()).Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshot(CreateUnregisteredAccelerationSnapshot())));

        var cut = context.Render<LlmSetupPage>();

        cut.WaitForAssertion(() => Assert.Contains("Execution providers are discovered but none are registered yet.", cut.Markup));
        Assert.Contains("Foundry Local discovered 2 Windows ML execution provider(s), but none are registered yet.", cut.Markup);
    }

    [Fact]
    public void Render_WhenLiveBenchmarkHasRankedPartialResults_ShowsLiveResultsTable()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions);
        workspace.SetOllamaAvailability(new LlmModelAvailability(
            Version: "0.1.0",
            Model: "phi",
            Installed: true,
            AvailableModels: ["phi", "mistral"],
            RunningModels: Array.Empty<LlmRunningModel>(),
            Provider: LlmProviderKind.Ollama));
        workspace.SetLlmSessionSettings("phi", "medium");

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshot()));

        var coordinator = context.Services.GetRequiredService<ModelBenchmarkCoordinator>();
        SetCurrentBenchmarkSession(
            coordinator,
            new ModelBenchmarkSession(
                StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedUtc: null,
                IsRunning: true,
                IsCancelled: false,
                CompletedCount: 1,
                TotalCount: 2,
                CurrentModel: "slow",
                Results:
                [
                    new ModelBenchmarkResult(
                        Model: "fast",
                        Rank: 1,
                        OverallScore: 0.92,
                        QualityScore: 0.88,
                        DecodeTokensPerSecond: 18.4,
                        LoadDuration: TimeSpan.FromSeconds(0.8),
                        TotalDuration: TimeSpan.FromSeconds(3.2),
                        Fit: OllamaCapacityFit.Comfortable,
                        Notes: ["steady throughput"],
                        FailedReason: null,
                        Provider: LlmProviderKind.Ollama)
                ],
                Provider: LlmProviderKind.Ollama)
            {
                CurrentPhase = ModelBenchmarkRunPhase.Evaluating,
                CurrentDetail = "Running the second model benchmark.",
                CompletedFixtureCount = ModelBenchmarkFixtures.DefaultSuite.Count,
                TotalFixtureCount = ModelBenchmarkFixtures.DefaultSuite.Count
            });

        var cut = context.Render<LlmSetupPage>();

        cut.WaitForAssertion(() => Assert.Contains("Live results so far", cut.Markup));

        var benchmarkTable = cut.FindAll("table")
            .Single(table => table.QuerySelectorAll("thead th").First().TextContent.Trim() == "#");
        var rows = benchmarkTable.QuerySelectorAll("tbody tr");
        var cells = rows[0].QuerySelectorAll("td");

        Assert.Single(rows);
        Assert.Contains("fast", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("1", cells[0].TextContent.Trim());
        Assert.Equal("fast", cells[2].TextContent.Trim());
    }

    private static void RegisterCommonServices(
        IServiceCollection services,
        WorkspaceSession workspace,
        OllamaOptions ollamaOptions,
        ILlmClient llmClient,
        IFoundryCatalogClient foundryCatalogClient)
    {
        services.AddSingleton<ILlmClient>(llmClient);
        services.AddSingleton<IFoundryCatalogClient>(foundryCatalogClient);
        services.AddSingleton(new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        services.AddSingleton(ollamaOptions);
        services.AddSingleton(new OperationStatusService());
        services.AddSingleton(workspace);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new OllamaCapacityProbe(sp.GetRequiredService<ILlmClient>(), sp.GetRequiredService<OllamaOptions>()));
        services.AddSingleton(sp => new ModelBenchmarkCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<WorkspaceSession>(),
            sp.GetRequiredService<OperationStatusService>(),
            sp.GetRequiredService<OllamaOptions>(),
                sp.GetRequiredService<FoundryOptions>(),
            sp.GetRequiredService<TimeProvider>()));
    }

    private static IElement FindButton(IRenderedComponent<LlmSetupPage> cut, string label)
        => cut.FindAll("button").Single(button => button.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase));

    private static void SetCurrentBenchmarkSession(ModelBenchmarkCoordinator coordinator, ModelBenchmarkSession session)
    {
        var currentField = typeof(ModelBenchmarkCoordinator).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(coordinator, session);
    }

    private static FoundryCatalogSnapshot CreateFoundrySnapshot(FoundryAccelerationSnapshot? acceleration = null)
        => new(
            new LlmModelAvailability(
                Version: "1.0.0",
                Model: "mistral-foundry",
                Installed: true,
                AvailableModels: ["mistral-foundry"],
                RunningModels: Array.Empty<LlmRunningModel>(),
                Provider: LlmProviderKind.Foundry),
            [
                new FoundryCatalogModel("phi-foundry", "Phi Foundry", "phi-foundry", 1024, false, false, "Lightweight reasoning and chat tasks."),
                new FoundryCatalogModel("mistral-foundry", "Mistral Foundry", "mistral-foundry", 2048, true, false, "Balanced general-purpose local assistant.")
            ],
            acceleration ?? FoundryAccelerationSnapshot.Unsupported("Not available"),
            DateTimeOffset.UtcNow);

    private static FoundryAccelerationSnapshot CreateUnregisteredAccelerationSnapshot()
        => new(
            IsSupported: true,
            IsEnabled: true,
            CanManageExecutionProviders: true,
            StatusMessage: "Foundry Local discovered 2 Windows ML execution provider(s), but none are registered yet.",
            ExecutionProviders:
            [
                new FoundryExecutionProviderInfo("dml", "DirectML", false),
                new FoundryExecutionProviderInfo("cuda", "CUDA", false)
            ],
            CollectedAtUtc: DateTimeOffset.UtcNow);

    private sealed class StubLlmClient : ILlmClient
    {
        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelAvailability("0.0.0", string.Empty, true, Array.Empty<string>(), Array.Empty<LlmRunningModel>()));

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse(request.Model, "ready", null, true, 1, 1, TimeSpan.FromSeconds(1.0)));

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);
    }

    private sealed class StubFoundryCatalogClient(FoundryCatalogSnapshot snapshot) : IFoundryCatalogClient
    {
        public Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(IReadOnlyList<string>? names = null, Action<string, double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot.Acceleration);

        public Task DownloadModelAsync(string alias, Action<double>? progress = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveModelAsync(string alias, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}