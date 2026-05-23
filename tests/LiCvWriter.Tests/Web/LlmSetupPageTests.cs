using AngleSharp.Dom;
using Bunit;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
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
            sp.GetRequiredService<TimeProvider>()));
    }

    private static IElement FindButton(IRenderedComponent<LlmSetupPage> cut, string label)
        => cut.FindAll("button").Single(button => button.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase));

    private static FoundryCatalogSnapshot CreateFoundrySnapshot()
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
            FoundryAccelerationSnapshot.Unsupported("Not available"),
            DateTimeOffset.UtcNow);

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