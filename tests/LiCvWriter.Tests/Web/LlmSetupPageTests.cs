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
    private static readonly ModelBenchmarkHangClockPolicy DefaultHangClockPolicy = ModelBenchmarkHangClockPolicy.Default;

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
        Assert.Contains("Select visible usable models", cut.Markup);
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

        var modelCheckbox = cut.FindAll("tbody tr")
            .Single(row => row.TextContent.Contains("phi-foundry", StringComparison.Ordinal))
            .QuerySelector("input[type='checkbox']");

        Assert.NotNull(modelCheckbox);
        modelCheckbox!.Change(true);

        var downloadButton = FindButton(cut, "Download selected");
        var removeButton = FindButton(cut, "Remove selected cached");
        var benchmarkButton = FindButton(cut, "Benchmark selected");

        Assert.False(downloadButton.HasAttribute("disabled"));
        Assert.True(removeButton.HasAttribute("disabled"));
        Assert.False(benchmarkButton.HasAttribute("disabled"));
    }

    [Fact]
    public void Interactions_WhenRemoveSelectedCachedIsClicked_RemovesSelectedFoundryModelFromCache()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        var initialSnapshot = CreateFoundrySnapshot();
        var foundryClient = new StubFoundryCatalogClient(initialSnapshot);
        workspace.SetFoundryAvailability(initialSnapshot.Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            foundryClient);

        var cut = context.Render<LlmSetupPage>();
        cut.WaitForAssertion(() => Assert.Contains("Filter catalog", cut.Markup));

        var cachedModelCheckbox = cut.FindAll("tbody tr")
            .Single(row => row.TextContent.Contains("mistral-foundry", StringComparison.Ordinal))
            .QuerySelector("input[type='checkbox']");

        Assert.NotNull(cachedModelCheckbox);
        cachedModelCheckbox!.Change(true);

        var removeButton = FindButton(cut, "Remove selected cached");
        Assert.False(removeButton.HasAttribute("disabled"));

        removeButton.Click();

        cut.WaitForAssertion(() => Assert.Equal(["mistral-foundry"], foundryClient.RemovedAliases));
        cut.WaitForAssertion(() => Assert.Contains("No models selected for batch actions.", cut.Markup));
        cut.WaitForAssertion(() => Assert.Contains(
            "Available to download",
            cut.FindAll("tbody tr").Single(row => row.TextContent.Contains("mistral-foundry", StringComparison.Ordinal)).TextContent,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Interactions_WhenSelectVisibleUsableModelsIsClicked_SelectsOnlyUsableFoundryModels()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        workspace.SetFoundryAvailability(CreateFoundrySnapshotWithBenchmarkConstraint().Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshotWithBenchmarkConstraint()));

        var cut = context.Render<LlmSetupPage>();
        cut.WaitForAssertion(() => Assert.Contains("Filter catalog", cut.Markup));

        FindButton(cut, "Select visible usable models").Click();

        cut.WaitForAssertion(() => Assert.Contains("Selected models: 1. 1 selected model(s) will be downloaded before use.", cut.Markup));
        var checkedRows = cut.FindAll("tbody tr")
            .Where(row => row.QuerySelector("input[type='checkbox']")?.HasAttribute("checked") == true)
            .ToArray();

        Assert.Single(checkedRows);
        Assert.Contains("phi-foundry", checkedRows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Audio transcription model; text JSON benchmark is not a good fit.", cut.Markup);
        Assert.Contains("unusable for text benchmark", cut.Markup);
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

        cut.WaitForAssertion(() => Assert.Contains("Benchmark results", cut.Markup));

        var benchmarkTable = cut.FindAll("table")
            .Single(table => table.QuerySelectorAll("thead th button").Any(button => button.TextContent.Trim().StartsWith("#", StringComparison.Ordinal)));
        var rows = benchmarkTable.QuerySelectorAll("tbody tr");
        var cells = rows[0].QuerySelectorAll("td");

        Assert.Single(rows);
        Assert.Contains("fast", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("1", cells[0].TextContent.Trim());
        Assert.Contains("fast", cells[2].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenBenchmarkRowFails_ShowsFailureStatusWithoutUsingFitColumn()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var workspace = new WorkspaceSession(ollamaOptions);
        workspace.SetOllamaAvailability(new LlmModelAvailability(
            Version: "0.1.0",
            Model: "phi",
            Installed: true,
            AvailableModels: ["phi"],
            RunningModels: Array.Empty<LlmRunningModel>(),
            Provider: LlmProviderKind.Ollama));
        workspace.SetLlmSessionSettings("phi", "medium");

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(CreateFoundrySnapshot()));

        workspace.SetLastBenchmarkSession(new ModelBenchmarkSession(
            StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-3),
            CompletedUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            IsRunning: false,
            IsCancelled: false,
            CompletedCount: 1,
            TotalCount: 1,
            CurrentModel: null,
            Results:
            [
                new ModelBenchmarkResult(
                    Model: "broken",
                    Rank: 1,
                    OverallScore: 0.0,
                    QualityScore: 0.0,
                    DecodeTokensPerSecond: null,
                    LoadDuration: null,
                    TotalDuration: TimeSpan.FromSeconds(12),
                    Fit: OllamaCapacityFit.Unknown,
                    Notes: ["runtime logs preserved"],
                    FailedReason: "Foundry model load failed.",
                    Provider: LlmProviderKind.Foundry)
            ],
            Provider: LlmProviderKind.Foundry));

        var cut = context.Render<LlmSetupPage>();

        cut.WaitForAssertion(() => Assert.Contains("Benchmark results", cut.Markup));

        var benchmarkTable = cut.FindAll("table")
            .Single(table => table.QuerySelectorAll("thead th button").Any(button => button.TextContent.Trim().StartsWith("#", StringComparison.Ordinal)));
        var headers = benchmarkTable.QuerySelectorAll("thead th").Select(static header => header.TextContent.Trim()).ToArray();
        var cells = benchmarkTable.QuerySelectorAll("tbody tr").Single().QuerySelectorAll("td");

        Assert.Contains("Status", headers);
        Assert.Contains("Fit", headers);
        Assert.Equal("Failed: Foundry model load failed.", cells[6].TextContent.Trim());
        Assert.Equal("-", cells[7].TextContent.Trim());
    }

    [Fact]
    public void Render_WhenLiveBenchmarkIsMonitoringSuspectedHang_ShowsHangWarning()
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
                StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-4),
                CompletedUtc: null,
                IsRunning: true,
                IsCancelled: false,
                CompletedCount: 0,
                TotalCount: 2,
                CurrentModel: "slow",
                Results: Array.Empty<ModelBenchmarkResult>(),
                Provider: LlmProviderKind.Ollama)
            {
                CurrentPhase = ModelBenchmarkRunPhase.Evaluating,
                CurrentFixtureDisplayName = ModelBenchmarkFixtures.CompanyFixtureDisplayName,
                CurrentFixtureNumber = 2,
                CurrentDetail = "Running fixture 2 of 3: Company context values JSON.",
                CompletedFixtureCount = 1,
                TotalFixtureCount = ModelBenchmarkFixtures.DefaultSuite.Count,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastRealProgressUtc = DateTimeOffset.UtcNow.AddSeconds(-45),
                HangState = ModelBenchmarkHangState.Warning,
                HangDetail = "No benchmark progress detected for 45 second(s) during fixture 2: Company context values JSON. If the stall continues, the queue will move on.",
                HangWarningStartedUtc = DateTimeOffset.UtcNow.AddSeconds(-15),
                HangDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(15)
            });

        var cut = context.Render<LlmSetupPage>();

        cut.WaitForAssertion(() => Assert.Contains("Monitoring suspected hang.", cut.Markup));
        Assert.Contains("Last real progress was", cut.Markup);
        Assert.Contains("No benchmark progress detected", cut.Markup);
    }

    [Fact]
    public void Render_WhenLiveFoundryBenchmarkStartsBeforePerModelDiagnostics_ShowsFoundryAccelerationFallback()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var foundrySnapshot = CreateFoundrySnapshot(CreateUnregisteredAccelerationSnapshot());
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        workspace.SetFoundryAvailability(foundrySnapshot.Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            new StubFoundryCatalogClient(foundrySnapshot));

        var coordinator = context.Services.GetRequiredService<ModelBenchmarkCoordinator>();
        SetCurrentBenchmarkSession(
            coordinator,
            new ModelBenchmarkSession(
                StartedUtc: DateTimeOffset.UtcNow.AddSeconds(-10),
                CompletedUtc: null,
                IsRunning: true,
                IsCancelled: false,
                CompletedCount: 0,
                TotalCount: 2,
                CurrentModel: "deepseek-r1-14b",
                Results: Array.Empty<ModelBenchmarkResult>(),
                Provider: LlmProviderKind.Foundry)
            {
                CurrentPhase = ModelBenchmarkRunPhase.Preparing,
                CurrentDetail = "Queue slot reserved for benchmark run.",
                UpdatedUtc = DateTimeOffset.UtcNow,
                CurrentPhaseStartedUtc = DateTimeOffset.UtcNow.AddSeconds(-4)
            });

        var cut = context.Render<LlmSetupPage>();

        cut.WaitForAssertion(() => Assert.Contains("Acceleration NeedsRegistration", cut.Markup));
        Assert.Contains("Foundry Local discovered 2 Windows ML execution provider(s), but none are registered yet.", cut.Markup);
    }

    [Fact]
    public void HandleChanged_WhenLiveFoundryBenchmarkStartsWithoutDiagnostics_RefreshesFoundrySnapshotFallback()
    {
        using var context = new BunitContext();
        var ollamaOptions = new OllamaOptions { Model = "phi", Think = "medium" };
        var initialSnapshot = CreateFoundrySnapshot(FoundryAccelerationSnapshot.Unsupported("Not available"));
        var refreshedSnapshot = CreateFoundrySnapshot(CreateUnregisteredAccelerationSnapshot());
        var workspace = new WorkspaceSession(ollamaOptions, foundryOptions: new FoundryOptions { DefaultModelAlias = "phi-foundry" });
        workspace.SetFoundryAvailability(initialSnapshot.Availability);
        workspace.SetLlmProviderSelection(LlmProviderKind.Foundry);
        var foundryClient = new StubFoundryCatalogClient(initialSnapshot);

        RegisterCommonServices(
            context.Services,
            workspace,
            ollamaOptions,
            new StubLlmClient(),
            foundryClient);

        var cut = context.Render<LlmSetupPage>();
        cut.WaitForAssertion(() => Assert.True(foundryClient.GetSnapshotCallCount >= 1));

        var coordinator = context.Services.GetRequiredService<ModelBenchmarkCoordinator>();
        SetCurrentBenchmarkSession(
            coordinator,
            new ModelBenchmarkSession(
                StartedUtc: DateTimeOffset.UtcNow.AddSeconds(-10),
                CompletedUtc: null,
                IsRunning: true,
                IsCancelled: false,
                CompletedCount: 0,
                TotalCount: 2,
                CurrentModel: "deepseek-r1-14b",
                Results: Array.Empty<ModelBenchmarkResult>(),
                Provider: LlmProviderKind.Foundry)
            {
                CurrentPhase = ModelBenchmarkRunPhase.Preparing,
                CurrentDetail = "Queue slot reserved for benchmark run.",
                UpdatedUtc = DateTimeOffset.UtcNow,
                CurrentPhaseStartedUtc = DateTimeOffset.UtcNow.AddSeconds(-4)
            });
        foundryClient.SetSnapshot(refreshedSnapshot);

        InvokeHandleChanged(cut.Instance);

        cut.WaitForAssertion(() => Assert.True(foundryClient.GetSnapshotCallCount >= 2));
        cut.WaitForAssertion(() => Assert.Contains("Acceleration NeedsRegistration", cut.Markup));
        Assert.Contains("Foundry Local discovered 2 Windows ML execution provider(s), but none are registered yet.", cut.Markup);
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
        services.AddSingleton(DefaultHangClockPolicy);
        services.AddSingleton(ollamaOptions);
        services.AddSingleton(new OperationStatusService());
        services.AddSingleton(workspace);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<FoundryBenchmarkLifecycleService>();
        services.AddSingleton<ModelBenchmarkHangMonitor>();
        services.AddSingleton(sp => new OllamaCapacityProbe(sp.GetRequiredService<ILlmClient>(), sp.GetRequiredService<OllamaOptions>()));
        services.AddSingleton(sp => new ModelBenchmarkCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<WorkspaceSession>(),
            sp.GetRequiredService<OperationStatusService>(),
            sp.GetRequiredService<OllamaOptions>(),
            sp.GetRequiredService<FoundryBenchmarkLifecycleService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ModelBenchmarkHangMonitor>()));
    }

    private static IElement FindButton(IRenderedComponent<LlmSetupPage> cut, string label)
        => cut.FindAll("button").Single(button => button.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase));

    private static void SetCurrentBenchmarkSession(ModelBenchmarkCoordinator coordinator, ModelBenchmarkSession session)
    {
        var currentField = typeof(ModelBenchmarkCoordinator).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(coordinator, session);
    }

    private static void InvokeHandleChanged(LlmSetupPage component)
    {
        var handleChanged = typeof(LlmSetupPage).GetMethod("HandleChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handleChanged);
        handleChanged!.Invoke(component, null);
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

    private static FoundryCatalogSnapshot CreateFoundrySnapshotWithBenchmarkConstraint(FoundryAccelerationSnapshot? acceleration = null)
        => new(
            new LlmModelAvailability(
                Version: "1.0.0",
                Model: "phi-foundry",
                Installed: true,
                AvailableModels: ["phi-foundry"],
                RunningModels: Array.Empty<LlmRunningModel>(),
                Provider: LlmProviderKind.Foundry),
            [
                new FoundryCatalogModel("phi-foundry", "Phi Foundry", "phi-foundry", 1024, false, false, "Lightweight reasoning and chat tasks."),
                new FoundryCatalogModel(
                    "whisper-small",
                    "Whisper Small",
                    "whisper-small",
                    1536,
                    false,
                    false,
                    "Speech transcription model.",
                    false,
                    "Audio transcription model; text JSON benchmark is not a good fit.")
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

    private sealed class StubFoundryCatalogClient : IFoundryCatalogClient
    {
        private FoundryCatalogSnapshot snapshot;

        public StubFoundryCatalogClient(FoundryCatalogSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        public List<string> RemovedAliases { get; } = [];

        public int GetSnapshotCallCount { get; private set; }

        public void SetSnapshot(FoundryCatalogSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        public Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            GetSnapshotCallCount++;
            return Task.FromResult(snapshot);
        }

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(IReadOnlyList<string>? names = null, Action<string, double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot.Acceleration);

        public Task DownloadModelAsync(string alias, Action<double>? progress = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnloadModelAsync(string alias, CancellationToken cancellationToken = default)
        {
            var updatedModels = snapshot.Models
                .Select(model => model.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    ? model with { IsLoaded = false }
                    : model)
                .ToArray();

            snapshot = snapshot with
            {
                Availability = snapshot.Availability with
                {
                    RunningModels = updatedModels
                        .Where(static model => model.IsLoaded)
                        .Select(static model => new LlmRunningModel(model.Alias, model.ModelId, null, null, null, LlmProviderKind.Foundry))
                        .ToArray()
                },
                Models = updatedModels,
                CollectedAtUtc = DateTimeOffset.UtcNow
            };

            return Task.CompletedTask;
        }

        public Task RemoveModelAsync(string alias, CancellationToken cancellationToken = default)
        {
            RemovedAliases.Add(alias);

            var updatedModels = snapshot.Models
                .Select(model => model.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    ? model with { IsCached = false, IsLoaded = false }
                    : model)
                .ToArray();

            var updatedAvailability = snapshot.Availability with
            {
                Installed = updatedModels.Any(static model => model.IsCached),
                AvailableModels = updatedModels
                    .Where(static model => model.IsCached)
                    .Select(static model => model.Alias)
                    .ToArray(),
                RunningModels = updatedModels
                    .Where(static model => model.IsLoaded)
                    .Select(static model => new LlmRunningModel(model.Alias, model.ModelId, null, null, null, LlmProviderKind.Foundry))
                    .ToArray()
            };

            snapshot = snapshot with
            {
                Availability = updatedAvailability,
                Models = updatedModels,
                CollectedAtUtc = DateTimeOffset.UtcNow
            };

            return Task.CompletedTask;
        }
    }
}