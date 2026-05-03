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
    public async Task StartJobContextAnalysis_UrlMode_UsesUrlResearchMethods()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium" };
        var jobResearchService = new CapturingJobResearchService();
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LlmOperationBroker>();
        services.AddSingleton<IJobResearchService>(jobResearchService);

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
        var startResult = broker.StartJobContextAnalysis(new StartJobContextOperationRequest(jobSetId));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Equal(1, jobResearchService.AnalyzeUrlCalls);
        Assert.Equal(1, jobResearchService.CompanyUrlCalls);
        Assert.Equal(0, jobResearchService.AnalyzeTextCalls);
        Assert.Equal(0, jobResearchService.CompanyTextCalls);
        Assert.Equal(new Uri("https://example.test/job"), jobResearchService.LastJobUrl);
        Assert.Equal(new Uri("https://example.test/company"), Assert.Single(jobResearchService.LastCompanyUrls));
    }

    [Fact]
    public async Task StartJobContextAnalysis_WithoutCompanyUrls_AutoDiscoversAndPersistsUrls()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium" };
        var jobResearchService = new CapturingJobResearchService
        {
            DiscoveredCompanyUrls = [new Uri("https://contoso.example/about")]
        };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LlmOperationBroker>();
        services.AddSingleton<IJobResearchService>(jobResearchService);

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
            string.Empty,
            string.Empty,
            string.Empty);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartJobContextAnalysis(new StartJobContextOperationRequest(jobSetId));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Equal(1, jobResearchService.DiscoverCompanyUrlCalls);
        Assert.Equal(1, jobResearchService.CompanyUrlCalls);
        Assert.Equal("https://contoso.example/about", Assert.Single(jobResearchService.LastCompanyUrls).AbsoluteUri);
        Assert.Equal("https://contoso.example/about", workspace.GetJobSet(jobSetId).CompanyUrlsText.Trim());
    }

    [Fact]
    public async Task StartJobContextAnalysis_PasteTextMode_UsesTextResearchMethods()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium" };
        var jobResearchService = new CapturingJobResearchService();
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LlmOperationBroker>();
        services.AddSingleton<IJobResearchService>(jobResearchService);

        await using var serviceProvider = services.BuildServiceProvider();
        var workspace = serviceProvider.GetRequiredService<WorkspaceSession>();

        workspace.SetOllamaAvailability(new OllamaModelAvailability(
            "0.19.0",
            "configured-model",
            true,
            ["configured-model"]));
        workspace.SetLlmSessionSettings("configured-model", "medium");
        workspace.AddJobSet(JobSetInputMode.PasteText);
        workspace.UpdateJobSetInputs("job-set-02",
            string.Empty,
            string.Empty,
            "Pasted job posting text",
            "Pasted company context");

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartJobContextAnalysis(new StartJobContextOperationRequest("job-set-02"));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Equal(0, jobResearchService.AnalyzeUrlCalls);
        Assert.Equal(0, jobResearchService.CompanyUrlCalls);
        Assert.Equal(1, jobResearchService.AnalyzeTextCalls);
        Assert.Equal(1, jobResearchService.CompanyTextCalls);
        Assert.Equal("Pasted job posting text", jobResearchService.LastJobText);
        Assert.Equal("Pasted company context", jobResearchService.LastCompanyText);
    }

    [Fact]
    public async Task StartJobContextAnalysis_SameJobSetAlreadyRunning_RejectsSecondOperation()
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
        var startResult = broker.StartJobContextAnalysis(new StartJobContextOperationRequest(jobSetId));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            broker.StartJobContextAnalysis(new StartJobContextOperationRequest(jobSetId)));

        Assert.Contains("already running", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(broker.Cancel(startResult.OperationId));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);
        Assert.Equal("cancelled", finalSnapshot.Status);
    }

    [Fact]
    public async Task StartJobContextAnalysis_DifferentJobSets_AllowsIndependentOperations()
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
            "https://example.test/job-1",
            "https://example.test/company-1",
            string.Empty,
            string.Empty);
        workspace.AddJobSet();
        workspace.UpdateJobSetInputs("job-set-02",
            "https://example.test/job-2",
            "https://example.test/company-2",
            string.Empty,
            string.Empty);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var firstStart = broker.StartJobContextAnalysis(new StartJobContextOperationRequest(jobSetId));
        var secondStart = broker.StartJobContextAnalysis(new StartJobContextOperationRequest("job-set-02"));

        Assert.NotEqual(firstStart.OperationId, secondStart.OperationId);

        Assert.True(broker.Cancel(firstStart.OperationId));
        Assert.True(broker.Cancel(secondStart.OperationId));

        var firstSnapshot = await WaitForTerminalSnapshotAsync(broker, firstStart.OperationId);
        var secondSnapshot = await WaitForTerminalSnapshotAsync(broker, secondStart.OperationId);

        Assert.Equal("cancelled", firstSnapshot.Status);
        Assert.Equal("cancelled", secondSnapshot.Status);
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
    public async Task StartFitReviewAnalysis_ResetsEvidenceSelectionsToUpdatedDefaults()
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
        var initialResult = broker.StartFitReviewAnalysis(new StartFitReviewOperationRequest("job-set-01"));
        await WaitForTerminalSnapshotAsync(broker, initialResult.OperationId);

        var evidenceId = workspace.GetJobSet(jobSetId).EvidenceSelection.SelectedEvidence[0].Evidence.Id;
        workspace.SetJobSetEvidenceSelected(jobSetId, evidenceId, false);
        Assert.DoesNotContain(workspace.GetJobSet(jobSetId).EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == evidenceId);

        var refreshResult = broker.StartFitReviewAnalysis(new StartFitReviewOperationRequest("job-set-01"));
        await WaitForTerminalSnapshotAsync(broker, refreshResult.OperationId);

        Assert.Contains(workspace.GetJobSet(jobSetId).EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == evidenceId);
    }

    [Fact]
    public async Task StartFitReviewAnalysis_StreamEvents_EmitsExplicitCompletedAfterProgressCompleted()
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
        var startResult = broker.StartFitReviewAnalysis(new StartFitReviewOperationRequest(jobSetId));
        var eventTypes = new List<string>();

        await foreach (var operationEvent in broker.StreamEventsAsync(startResult.OperationId))
        {
            eventTypes.Add(operationEvent.EventType);
            if (operationEvent.EventType == "completed")
            {
                break;
            }
        }

        Assert.Contains("progress-completed", eventTypes);
        Assert.Equal("completed", eventTypes[^1]);
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
    public async Task StartRefreshAllAnalysis_UpdatesOnlyRequestedJobSet()
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
        workspace.AddJobSet();
        workspace.SetJobSetJobPosting("job-set-02", new JobPostingAnalysis
        {
            RoleTitle = "Principal Consultant",
            CompanyName = "Fabrikam",
            Summary = "Lead advisory delivery.",
            SourceUrl = new Uri("https://example.test/fabrikam-job")
        });
        workspace.SetJobSetGeneratedDocuments("job-set-02",
            [new GeneratedDocument(DocumentKind.Cv, "Existing CV", "# Existing", "Existing", DateTimeOffset.UtcNow)],
            [new DocumentExportResult(DocumentKind.Cv, "c:/exports/fabrikam-cv.docx")]);
        var untouchedJobSet = workspace.GetJobSet("job-set-02");
        var untouchedTitle = untouchedJobSet.Title;
        var untouchedOutputFolder = untouchedJobSet.OutputFolderName;
        var untouchedProgressDetail = untouchedJobSet.ProgressDetail;

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartRefreshAllAnalysis(new StartRefreshAllOperationRequest("job-set-01"));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Equal("Lead Architect", workspace.GetJobSet(jobSetId).JobPosting!.RoleTitle);
        Assert.True(workspace.GetJobSet(jobSetId).EvidenceSelection.HasSignals);

        var otherJobSet = workspace.GetJobSet("job-set-02");
        Assert.Equal(untouchedTitle, otherJobSet.Title);
        Assert.Equal(untouchedOutputFolder, otherJobSet.OutputFolderName);
        Assert.Equal(JobSetProgressState.Done, otherJobSet.ProgressState);
        Assert.Equal(untouchedProgressDetail, otherJobSet.ProgressDetail);
        Assert.Single(otherJobSet.GeneratedDocuments);
        Assert.Equal("Existing CV", otherJobSet.GeneratedDocuments[0].Title);
        Assert.False(otherJobSet.JobFitAssessment.HasSignals);
        Assert.False(otherJobSet.TechnologyGapAssessment.HasSignals);
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

    [Fact]
    public async Task StartRefreshAllAnalysis_WhenFirstAttemptFails_RetriesAndCompletes()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", RetryAttempts = 3, RetryDelaySeconds = 0 };
        var jobResearchService = new FailOnceThenSucceedJobResearchService();
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CandidateEvidenceService>();
        services.AddSingleton<JobFitAnalysisService>();
        services.AddSingleton(provider => new EvidenceSelectionService(provider.GetRequiredService<CandidateEvidenceService>()));
        services.AddSingleton<LlmOperationBroker>();
        services.AddSingleton<IJobResearchService>(jobResearchService);
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
        workspace.UpdateJobSetInputs(jobSetId,
            "https://example.test/job",
            "https://example.test/company",
            string.Empty,
            string.Empty);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartRefreshAllAnalysis(new StartRefreshAllOperationRequest(jobSetId));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId, attempts: 300);

        Assert.Equal("completed", finalSnapshot.Status);
        Assert.Equal(2, jobResearchService.CallCount);
        Assert.Equal("Lead Architect", workspace.GetJobSet(jobSetId).JobPosting!.RoleTitle);
    }

    [Fact]
    public async Task StartRefreshAllAnalysis_WhenAllAttemptsExhausted_FailsWithLastExceptionMessage()
    {
        var options = new OllamaOptions { Model = "configured-model", Think = "medium", RetryAttempts = 2, RetryDelaySeconds = 0 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new WorkspaceSession(options));
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CandidateEvidenceService>();
        services.AddSingleton<JobFitAnalysisService>();
        services.AddSingleton(provider => new EvidenceSelectionService(provider.GetRequiredService<CandidateEvidenceService>()));
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<IJobResearchService, AlwaysFailingJobResearchService>();
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
        workspace.UpdateJobSetInputs(jobSetId,
            "https://example.test/job",
            "https://example.test/company",
            string.Empty,
            string.Empty);

        var broker = serviceProvider.GetRequiredService<LlmOperationBroker>();
        var startResult = broker.StartRefreshAllAnalysis(new StartRefreshAllOperationRequest(jobSetId));
        var finalSnapshot = await WaitForTerminalSnapshotAsync(broker, startResult.OperationId, attempts: 300);

        Assert.Equal("failed", finalSnapshot.Status);
        Assert.Contains("persistent failure", finalSnapshot.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobSetProgressState.Failed, workspace.GetJobSet(jobSetId).ProgressState);
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

    private sealed class FakeCompositeLlmClient : ILlmClient
    {
        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OllamaModelAvailability(
                "0.19.0",
                "configured-model",
                true,
                ["configured-model"]));

        public Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<OllamaModelInfo?>(null);

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            var content = request.PromptId switch
            {
                LlmPromptCatalog.FitEnhanceJson => """
                    {
                      "enhancedRequirements": [
                        {
                          "requirement": "Transformation leadership",
                          "newMatch": "Strong",
                          "evidence": ["Led Azure platform delivery and modernization programs for enterprise clients."],
                          "rationale": "The experience description shows direct transformation leadership in enterprise delivery."
                        }
                      ],
                      "gapFramingStrategies": [],
                      "positioningAngle": null
                    }
                    """,
                LlmPromptCatalog.TechGapJson => """
                    {
                      "detectedTechnologies": ["Azure", "Kubernetes"],
                      "possiblyUnderrepresentedTechnologies": ["Kubernetes"]
                    }
                    """,
                LlmPromptCatalog.JsonRepair => "{}",
                _ => throw new NotSupportedException($"Unexpected prompt id '{request.PromptId}'.")
            };

            progress?.Invoke(new LlmProgressUpdate(
                request.PromptId == LlmPromptCatalog.TechGapJson ? "Analyzing technology gaps" : "Enhancing fit review with LLM",
                "Streaming model output.",
                request.Model,
                TimeSpan.FromMilliseconds(100),
                Sequence: 1));

            progress?.Invoke(new LlmProgressUpdate(
                request.PromptId == LlmPromptCatalog.TechGapJson ? "Analyzing technology gaps" : "Enhancing fit review with LLM",
                "Model output completed.",
                request.Model,
                TimeSpan.FromMilliseconds(200),
                Completed: true,
                PromptTokens: 12,
                CompletionTokens: 18,
                ResponseContent: content,
                ThinkingPreview: "Reasoning",
                ThinkingContent: "Reasoning in full",
                Sequence: 2));

            return Task.FromResult(new LlmResponse(
                request.Model,
                content,
                "Reasoning in full",
                true,
                12,
                18,
                TimeSpan.FromMilliseconds(220)));
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

        public Task<IReadOnlyList<Uri>> DiscoverCompanyContextUrlsAsync(Uri jobPostingUrl, string? companyName = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Uri>>([new Uri("https://example.test/company")]);

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

        public Task<IReadOnlyList<Uri>> DiscoverCompanyContextUrlsAsync(Uri jobPostingUrl, string? companyName = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Uri>>([new Uri("https://example.test/company")]);

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => AnalyzeAsync(new Uri("https://example.test/job"), selectedModel, selectedThinkingLevel, progress, sourceLanguageHint, cancellationToken);

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => BuildCompanyProfileAsync(Array.Empty<Uri>(), selectedModel, selectedThinkingLevel, progress, sourceLanguageHint, cancellationToken);
    }

    private sealed class CapturingJobResearchService : IJobResearchService
    {
        public int AnalyzeUrlCalls { get; private set; }
        public int CompanyUrlCalls { get; private set; }
        public int DiscoverCompanyUrlCalls { get; private set; }
        public int AnalyzeTextCalls { get; private set; }
        public int CompanyTextCalls { get; private set; }
        public Uri? LastJobUrl { get; private set; }
        public IReadOnlyList<Uri> LastCompanyUrls { get; private set; } = Array.Empty<Uri>();
        public string? LastJobText { get; private set; }
        public string? LastCompanyText { get; private set; }
        public IReadOnlyList<Uri> DiscoveredCompanyUrls { get; init; } = Array.Empty<Uri>();

        public Task<JobPostingAnalysis> AnalyzeAsync(Uri jobPostingUrl, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
        {
            AnalyzeUrlCalls++;
            LastJobUrl = jobPostingUrl;
            return Task.FromResult(BuildJobPosting(jobPostingUrl));
        }

        public Task<CompanyResearchProfile> BuildCompanyProfileAsync(IEnumerable<Uri> sourceUrls, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
        {
            CompanyUrlCalls++;
            LastCompanyUrls = sourceUrls.ToArray();
            return Task.FromResult(new CompanyResearchProfile
            {
                Summary = "URL company summary",
                SourceUrls = LastCompanyUrls
            });
        }

        public Task<IReadOnlyList<Uri>> DiscoverCompanyContextUrlsAsync(Uri jobPostingUrl, string? companyName = null, CancellationToken cancellationToken = default)
        {
            DiscoverCompanyUrlCalls++;
            return Task.FromResult(DiscoveredCompanyUrls);
        }

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
        {
            AnalyzeTextCalls++;
            LastJobText = jobPostingText;
            return Task.FromResult(BuildJobPosting(null));
        }

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
        {
            CompanyTextCalls++;
            LastCompanyText = companyContextText;
            return Task.FromResult(new CompanyResearchProfile
            {
                Summary = "Text company summary"
            });
        }

        private static JobPostingAnalysis BuildJobPosting(Uri? sourceUrl)
            => new()
            {
                RoleTitle = "Lead Architect",
                CompanyName = "Contoso",
                Summary = "Build resilient systems",
                MustHaveThemes = ["Azure"],
                SourceUrl = sourceUrl
            };
    }

    private sealed class FakeTechnologyGapLlmClient : ILlmClient
    {
        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<OllamaModelInfo?>(null);

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

    private sealed class FailOnceThenSucceedJobResearchService : IJobResearchService
    {
        private int callCount;

        public int CallCount => callCount;

        public Task<JobPostingAnalysis> AnalyzeAsync(Uri jobPostingUrl, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                throw new InvalidOperationException("Transient failure on first attempt.");
            }

            return Task.FromResult(new JobPostingAnalysis
            {
                RoleTitle = "Lead Architect",
                CompanyName = "Contoso",
                Summary = "Build resilient systems",
                SourceUrl = jobPostingUrl
            });
        }

        public Task<CompanyResearchProfile> BuildCompanyProfileAsync(IEnumerable<Uri> sourceUrls, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompanyResearchProfile { Summary = "Contoso summary", SourceUrls = sourceUrls.ToArray() });

        public Task<IReadOnlyList<Uri>> DiscoverCompanyContextUrlsAsync(Uri jobPostingUrl, string? companyName = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Uri>>([new Uri("https://example.test/company")]);

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => AnalyzeAsync(new Uri("https://example.test/job"), selectedModel, selectedThinkingLevel, progress, sourceLanguageHint, cancellationToken);

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompanyResearchProfile { Summary = companyContextText });
    }

    private sealed class AlwaysFailingJobResearchService : IJobResearchService
    {
        public Task<JobPostingAnalysis> AnalyzeAsync(Uri jobPostingUrl, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Persistent failure on every attempt.");

        public Task<CompanyResearchProfile> BuildCompanyProfileAsync(IEnumerable<Uri> sourceUrls, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Persistent failure on every attempt.");

        public Task<IReadOnlyList<Uri>> DiscoverCompanyContextUrlsAsync(Uri jobPostingUrl, string? companyName = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Persistent failure on every attempt.");

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Persistent failure on every attempt.");

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Persistent failure on every attempt.");
    }
}
