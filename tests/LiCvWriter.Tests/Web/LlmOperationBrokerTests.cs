using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Workflows;
using LiCvWriter.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Tests.Web;

public sealed class LlmOperationBrokerTests
{
    private string jobSetId = "job-set-01";

    [Fact]
    public async Task StartDraftGeneration_CompletesAndPublishesFinalSnapshot()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", UseChatEndpoint = true, KeepAlive = "5m", Temperature = 0.1 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CandidateEvidenceService>();
        services.AddSingleton<JobFitAnalysisService>();
        services.AddSingleton(provider => new EvidenceSelectionService(provider.GetRequiredService<CandidateEvidenceService>()));
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<JobFitWorkspaceRefreshService>();
        services.AddScoped<ILlmClient, FakeCompositeLlmClient>();
        services.AddScoped<LlmFitEnhancementService>();
        services.AddScoped<IDraftGenerationService, FakeDraftGenerationService>();

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Senior architect",
                    Experience =
                    [
                        new ExperienceEntry(
                            "Contoso",
                            "Lead Architect",
                            "Led Azure platform delivery and modernization programs for enterprise clients.",
                            null,
                            new DateRange(new PartialDate("2023", 2023)))
                    ]
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));
        workspace.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
        {
            RoleTitle = "Lead Architect",
            CompanyName = "Contoso",
            Summary = "Drive Azure delivery and transformation leadership.",
            MustHaveThemes = ["Azure", "Transformation leadership"]
        });

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartDraftGeneration(new StartDraftGenerationOperationRequest("job-set-01", ["Cv"]));

        LlmOperationSnapshot? finalSnapshot = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            finalSnapshot = broker.GetSnapshot(startResult.OperationId);
            if (finalSnapshot is { Status: "completed" })
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.NotNull(finalSnapshot);
        Assert.Equal("completed", finalSnapshot!.Status);
        Assert.True(finalSnapshot.Completed);
        Assert.True(workspace.GetJobSet(jobSetId).JobFitAssessment.IsLlmEnhanced);
        Assert.True(workspace.GetJobSet(jobSetId).EvidenceSelection.HasSignals);
        Assert.Equal("Generated content (enhanced fit)", workspace.GetJobSet(jobSetId).GeneratedDocuments[0].Markdown);
        Assert.Equal(JobSetProgressState.Done, workspace.GetJobSet(jobSetId).ProgressState);
    }

    [Fact]
    public async Task StartDraftGeneration_WhenFitReviewIsCurrent_SkipsPreflightRefresh()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", UseChatEndpoint = true, KeepAlive = "5m", Temperature = 0.1 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CandidateEvidenceService>();
        services.AddSingleton<JobFitAnalysisService>();
        services.AddSingleton(provider => new EvidenceSelectionService(provider.GetRequiredService<CandidateEvidenceService>()));
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<JobFitWorkspaceRefreshService>();
        services.AddScoped<ILlmClient, FakeCompositeLlmClient>();
        services.AddScoped<LlmFitEnhancementService>();
        services.AddScoped<IDraftGenerationService, FakeDraftGenerationService>();

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Senior architect",
                    Experience =
                    [
                        new ExperienceEntry(
                            "Contoso",
                            "Lead Architect",
                            "Led Azure platform delivery and modernization programs for enterprise clients.",
                            null,
                            new DateRange(new PartialDate("2023", 2023)))
                    ]
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));
        workspace.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
        {
            RoleTitle = "Lead Architect",
            CompanyName = "Contoso",
            Summary = "Drive Azure delivery and transformation leadership.",
            MustHaveThemes = ["Azure", "Transformation leadership"]
        });

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var fitReviewResult = broker.StartFitReviewAnalysis(new StartFitReviewOperationRequest("job-set-01"));
        await WaitForTerminalSnapshotAsync(broker, fitReviewResult.OperationId);

        var startResult = broker.StartDraftGeneration(new StartDraftGenerationOperationRequest("job-set-01", ["Cv"]));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);
        var snapshot = broker.GetSnapshot(startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.NotNull(snapshot);
        Assert.Equal("Generated content (enhanced fit)", workspace.GetJobSet(jobSetId).GeneratedDocuments[0].Markdown);
        Assert.Equal("Draft generation completed", snapshot!.Message);
        Assert.Equal(3, snapshot.Sequence);
    }

    [Fact]
    public async Task StartJobContextAnalysis_CompletesAndStoresJobAndCompanyContext()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium" };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<IJobResearchService, FakeJobResearchService>();

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.UpdateJobSetInputs(jobSetId, 
            "https://example.test/job",
            "https://example.test/company",
            string.Empty,
            string.Empty);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartJobContextAnalysis(new StartJobContextOperationRequest("job-set-01"));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Equal("Lead Architect", workspace.GetJobSet(jobSetId).JobPosting!.RoleTitle);
        Assert.Equal("Contoso summary", workspace.GetJobSet(jobSetId).CompanyProfile!.Summary);
    }

    [Fact]
    public async Task StartTechnologyGapAnalysis_CompletesAndStoresAssessment()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", UseChatEndpoint = true, KeepAlive = "5m", Temperature = 0.1 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<ILlmClient, FakeTechnologyGapLlmClient>();
        services.AddScoped<LlmTechnologyGapAnalysisService>();

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Senior architect"
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));
        workspace.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
        {
            RoleTitle = "Lead Architect",
            CompanyName = "Contoso",
            Summary = "Build resilient systems"
        });

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartTechnologyGapAnalysis(new StartTechnologyGapOperationRequest("job-set-01"));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Contains("Azure", workspace.GetJobSet(jobSetId).TechnologyGapAssessment.DetectedTechnologies);
        Assert.Contains("Kubernetes", workspace.GetJobSet(jobSetId).TechnologyGapAssessment.PossiblyUnderrepresentedTechnologies);
    }

    [Fact]
    public async Task StartFitReviewAnalysis_CompletesAndStoresEnhancedAssessment()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", UseChatEndpoint = true, KeepAlive = "5m", Temperature = 0.1 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CandidateEvidenceService>();
        services.AddSingleton<JobFitAnalysisService>();
        services.AddSingleton(provider => new EvidenceSelectionService(provider.GetRequiredService<CandidateEvidenceService>()));
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<JobFitWorkspaceRefreshService>();
        services.AddScoped<ILlmClient, FakeCompositeLlmClient>();
        services.AddScoped<LlmFitEnhancementService>();

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Senior architect",
                    Experience =
                    [
                        new ExperienceEntry(
                            "Contoso",
                            "Lead Architect",
                            "Led Azure platform delivery and modernization programs for enterprise clients.",
                            null,
                            new DateRange(new PartialDate("2023", 2023)))
                    ]
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));
        workspace.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
        {
            RoleTitle = "Lead Architect",
            CompanyName = "Contoso",
            Summary = "Drive Azure delivery and transformation leadership.",
            MustHaveThemes = ["Azure", "Transformation leadership"]
        });

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartFitReviewAnalysis(new StartFitReviewOperationRequest("job-set-01"));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.True(workspace.GetJobSet(jobSetId).JobFitAssessment.IsLlmEnhanced);
        Assert.Contains(workspace.GetJobSet(jobSetId).JobFitAssessment.Requirements, requirement =>
            requirement.Requirement == "Transformation leadership"
            && requirement.IsLlmEnhanced
            && requirement.Match == JobRequirementMatch.Strong);
        Assert.True(workspace.GetJobSet(jobSetId).EvidenceSelection.HasSignals);
    }

    [Fact]
    public async Task StartRefreshAllAnalysis_CompletesAndRefreshesResearchFitAndTechnologyGap()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", UseChatEndpoint = true, KeepAlive = "5m", Temperature = 0.1 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CandidateEvidenceService>();
        services.AddSingleton<JobFitAnalysisService>();
        services.AddSingleton(provider => new EvidenceSelectionService(provider.GetRequiredService<CandidateEvidenceService>()));
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<IJobResearchService, FakeJobResearchService>();
        services.AddScoped<JobFitWorkspaceRefreshService>();
        services.AddScoped<ILlmClient, FakeCompositeLlmClient>();
        services.AddScoped<LlmFitEnhancementService>();
        services.AddScoped<LlmTechnologyGapAnalysisService>();

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Senior architect",
                    Experience =
                    [
                        new ExperienceEntry(
                            "Contoso",
                            "Lead Architect",
                            "Led Azure platform delivery and modernization programs for enterprise clients.",
                            null,
                            new DateRange(new PartialDate("2023", 2023)))
                    ]
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));
        workspace.UpdateJobSetInputs(jobSetId, 
            "https://example.test/job",
            "https://example.test/company",
            string.Empty,
            string.Empty);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartRefreshAllAnalysis(new StartRefreshAllOperationRequest("job-set-01"));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Equal("Lead Architect", workspace.GetJobSet(jobSetId).JobPosting!.RoleTitle);
        Assert.Equal("Contoso summary", workspace.GetJobSet(jobSetId).CompanyProfile!.Summary);
        Assert.True(workspace.GetJobSet(jobSetId).JobFitAssessment.IsLlmEnhanced);
        Assert.True(workspace.GetJobSet(jobSetId).EvidenceSelection.HasSignals);
        Assert.Contains("Azure", workspace.GetJobSet(jobSetId).TechnologyGapAssessment.DetectedTechnologies);
    }

    [Fact]
    public async Task StartJobContextAnalysis_WhenOperationExceedsTimeout_FailsWithTimedOutSnapshot()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", MaxOperationSeconds = 1 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<IJobResearchService, SlowJobResearchService>();

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.UpdateJobSetInputs(jobSetId, 
            "https://example.test/job",
            "https://example.test/company",
            string.Empty,
            string.Empty);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartJobContextAnalysis(new StartJobContextOperationRequest("job-set-01"));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId, attempts: 350);

        Assert.Equal("failed", finalSnapshot.Status);
        Assert.Contains("timed out", finalSnapshot.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disable the hard operation cap", finalSnapshot.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobSetProgressState.Failed, workspace.GetJobSet(jobSetId).ProgressState);
    }

    [Fact]
    public async Task StartJobContextAnalysis_WithoutHardCap_AllowsSlowOperationToComplete()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", MaxOperationSeconds = 0 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<IJobResearchService, SlowJobResearchService>();

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.UpdateJobSetInputs(jobSetId, 
            "https://example.test/job",
            "https://example.test/company",
            string.Empty,
            string.Empty);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartJobContextAnalysis(new StartJobContextOperationRequest("job-set-01"));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId, attempts: 1200);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Equal(JobSetProgressState.NotStarted, workspace.GetJobSet(jobSetId).ProgressState);
        Assert.Equal("Lead Architect", workspace.GetJobSet(jobSetId).JobPosting!.RoleTitle);
        Assert.Equal("Slow company summary", workspace.GetJobSet(jobSetId).CompanyProfile!.Summary);
    }

    [Fact]
    public void StartJobContextAnalysis_WhenPreviousTelemetryExists_ClearsLastCompletedTelemetryBeforeStreaming()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium" };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<IJobResearchService, FakeJobResearchService>();

        using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();
        var operations = serviceProvider.GetRequiredService<OperationStatusService>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.UpdateJobSetInputs(jobSetId, 
            "https://example.test/job",
            "https://example.test/company",
            string.Empty,
            string.Empty);

        operations.UpdateCurrent(new LlmProgressUpdate(
            "Draft completed",
            "The stream finished.",
            "configured-model",
            TimeSpan.FromSeconds(2),
            Completed: true,
            ThinkingPreview: "Northwind Health",
            ThinkingContent: "Northwind Health Northwind Health",
            Sequence: 7));

        Assert.NotNull(operations.LastCompletedLlmTelemetry);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        broker.StartJobContextAnalysis(new StartJobContextOperationRequest("job-set-01"));

        Assert.Null(operations.LastCompletedLlmTelemetry);
    }

    private static async Task<LlmOperationSnapshot> WaitForTerminalSnapshotAsync(LlmOperationBroker broker, string operationId, int attempts = 200)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var snapshot = broker.GetSnapshot(operationId);
            if (snapshot is { IsTerminal: true })
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Operation '{operationId}' did not reach a terminal state.");
    }

    private sealed class FakeDraftGenerationService : IDraftGenerationService
    {
        public Task<DraftGenerationResult> GenerateAsync(
            DraftGenerationRequest request,
            Action<LlmProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var content = request.JobFitAssessment?.IsLlmEnhanced == true ? "Generated content (enhanced fit)" : "Generated content";
            progress?.Invoke(new LlmProgressUpdate(
                "Generating Cv",
                "Streaming draft content.",
                request.LlmModel ?? "configured-model",
                TimeSpan.FromMilliseconds(200),
                PromptTokens: 12,
                CompletionTokens: 18,
                ResponseContent: content,
                ThinkingPreview: "Reasoning",
                ThinkingContent: "Reasoning in full",
                Sequence: 1));

            return Task.FromResult(new DraftGenerationResult(
                [new GeneratedDocument(DocumentKind.Cv, "CV", content, content, DateTimeOffset.UtcNow)],
                Array.Empty<DocumentExportResult>()));
        }
    }

    private sealed class FakeJobResearchService : IJobResearchService
    {
        public Task<JobPostingAnalysis> AnalyzeAsync(Uri jobPostingUrl, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
        {
            progress?.Invoke(new LlmProgressUpdate(
                "Parsing job posting",
                "Structured job parsing is running.",
                selectedModel ?? "configured-model",
                TimeSpan.FromMilliseconds(100),
                ResponseContent: "{\"roleTitle\":\"Lead Architect\"}",
                Sequence: 1));

            return Task.FromResult(new JobPostingAnalysis
            {
                RoleTitle = "Lead Architect",
                CompanyName = "Contoso",
                Summary = "Build resilient systems",
                MustHaveThemes = ["Azure", "Transformation leadership"],
                SourceUrl = jobPostingUrl
            });
        }

        public Task<CompanyResearchProfile> BuildCompanyProfileAsync(IEnumerable<Uri> sourceUrls, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompanyResearchProfile
            {
                Summary = "Contoso summary",
                SourceUrls = sourceUrls.ToArray()
            });

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => AnalyzeAsync(new Uri("https://example.test/job"), selectedModel, selectedThinkingLevel, progress, sourceLanguageHint, cancellationToken);

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompanyResearchProfile { Summary = companyContextText });
    }

    private sealed class SlowJobResearchService : IJobResearchService
    {
        public async Task<JobPostingAnalysis> AnalyzeAsync(Uri jobPostingUrl, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
        {
            progress?.Invoke(new LlmProgressUpdate(
                "Parsing job posting",
                "Structured job parsing is running.",
                selectedModel ?? "configured-model",
                TimeSpan.FromMilliseconds(100),
                ThinkingContent: "Still thinking",
                Sequence: 1));

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            return new JobPostingAnalysis
            {
                RoleTitle = "Lead Architect",
                CompanyName = "Contoso",
                Summary = "Build resilient systems",
                SourceUrl = jobPostingUrl
            };
        }

        public async Task<CompanyResearchProfile> BuildCompanyProfileAsync(IEnumerable<Uri> sourceUrls, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new CompanyResearchProfile { Summary = "Slow company summary", SourceUrls = sourceUrls.ToArray() };
        }

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => AnalyzeAsync(new Uri("https://example.test/job"), selectedModel, selectedThinkingLevel, progress, sourceLanguageHint, cancellationToken);

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => BuildCompanyProfileAsync(Array.Empty<Uri>(), selectedModel, selectedThinkingLevel, progress, sourceLanguageHint, cancellationToken);
    }

    private sealed class FakeTechnologyGapLlmClient : ILlmClient
    {
        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Invoke(new LlmProgressUpdate(
                "Analyzing technology gaps",
                "Technology gap analysis is running.",
                request.Model,
                TimeSpan.FromMilliseconds(120),
                ResponseContent: "{\"detectedTechnologies\":[\"Azure\",\"Kubernetes\"],\"possiblyUnderrepresentedTechnologies\":[\"Kubernetes\"]}",
                ThinkingPreview: "Comparing technology evidence",
                ThinkingContent: "Comparing technology evidence",
                Sequence: 1));

            return Task.FromResult(new LlmResponse(
                request.Model,
                "{\"detectedTechnologies\":[\"Azure\",\"Kubernetes\"],\"possiblyUnderrepresentedTechnologies\":[\"Kubernetes\"]}",
                null,
                true,
                12,
                18,
                TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class FakeCompositeLlmClient : ILlmClient
    {
        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            var isFitEnhancement = request.SystemPrompt?.Contains("semantic evidence matcher", StringComparison.OrdinalIgnoreCase) == true;
            var content = isFitEnhancement
                ? "{\"enhancedRequirements\":[{\"requirement\":\"Transformation leadership\",\"newMatch\":\"Strong\",\"evidence\":[\"Led Azure platform delivery and modernization programs\"],\"rationale\":\"Modernization delivery demonstrates transformation leadership.\"}]}"
                : "{\"detectedTechnologies\":[\"Azure\",\"Kubernetes\"],\"possiblyUnderrepresentedTechnologies\":[\"Kubernetes\"]}";
            var message = isFitEnhancement ? "Enhancing fit review with LLM" : "Analyzing technology gaps";
            var thinking = isFitEnhancement ? "Comparing semantic fit evidence" : "Comparing technology evidence";

            progress?.Invoke(new LlmProgressUpdate(
                message,
                $"{message} is running.",
                request.Model,
                TimeSpan.FromMilliseconds(120),
                ResponseContent: content,
                ThinkingPreview: thinking,
                ThinkingContent: thinking,
                Sequence: 1));

            return Task.FromResult(new LlmResponse(
                request.Model,
                content,
                null,
                true,
                12,
                18,
                TimeSpan.FromSeconds(1)));
        }
    }
}