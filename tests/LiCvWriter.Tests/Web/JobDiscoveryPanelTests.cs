using Bunit;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Web.Components.Pages.Workspace;
using LiCvWriter.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Tests.Web;

public sealed class JobDiscoveryPanelTests
{
    [Fact]
    public void HandleSearchCriteriaChanged_WhenQueryChanges_ClearsVisibleSuggestionsAndShowsFindSuggestions()
    {
        using var context = new BunitContext();
        var workspace = new WorkspaceSession(new OllamaOptions());
        var discoveryOptions = CreateDiscoveryOptions();
        var profileService = new JobDiscoveryProfileLightService();
        var searchPlanService = new JobDiscoverySearchPlanService(discoveryOptions);
        var candidateProfile = CreateCandidateProfile();
        var profileLight = profileService.Build(candidateProfile);
        var initialPlan = searchPlanService.Build(profileLight);
        var updatedPlan = searchPlanService.Build(profileLight, queryOverride: "principal architect", locationOverride: profileLight.PreferredLocation);

        workspace.UpdateCandidateProfile(candidateProfile);
        workspace.MergeSavedSuggestions(initialPlan, [CreateSuggestionReview(initialPlan, "Lead AI Architect", "https://jobs.example.test/lead-ai-architect")]);
        workspace.MergeSavedSuggestions(updatedPlan, [CreateSuggestionReview(updatedPlan, "Principal Architect", "https://jobs.example.test/principal-architect")]);

        RegisterPanelServices(context.Services, workspace, discoveryOptions);

        var cut = context.Render<JobDiscoveryPanel>();

        Assert.Equal("Update suggestions", GetDiscoverButton(cut).TextContent.Trim());
        Assert.Contains("Lead AI Architect", cut.Markup);

        cut.Find("#discoveryQuery").Input("principal architect");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Find suggestions", GetDiscoverButton(cut).TextContent.Trim());
            Assert.Empty(cut.FindAll("article.details-card"));
            Assert.DoesNotContain("Accept as Job set", cut.Markup);
            Assert.DoesNotContain("Dismiss", cut.Markup);
        });
    }

    [Fact]
    public void CreateJobSetFromSuggestion_WhenAccepted_CreatesJobSetWithoutNavigation()
    {
        using var context = new BunitContext();
        var workspace = new WorkspaceSession(new OllamaOptions());
        var discoveryOptions = CreateDiscoveryOptions();
        var profileService = new JobDiscoveryProfileLightService();
        var searchPlanService = new JobDiscoverySearchPlanService(discoveryOptions);
        var candidateProfile = CreateCandidateProfile();
        var profileLight = profileService.Build(candidateProfile);
        var initialPlan = searchPlanService.Build(profileLight);

        workspace.UpdateCandidateProfile(candidateProfile);
        workspace.MergeSavedSuggestions(initialPlan, [CreateSuggestionReview(initialPlan, "Lead AI Architect", "https://jobs.example.test/lead-ai-architect")]);

        RegisterPanelServices(context.Services, workspace, discoveryOptions);

        var cut = context.Render<JobDiscoveryPanel>();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var initialUri = navigation.Uri;

        cut.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Accept as Job set")
            .Click();

        cut.WaitForAssertion(() => Assert.Equal(2, workspace.JobSets.Count));

        var createdJobSet = workspace.JobSets.Single(jobSet => !jobSet.Id.Equals("job-set-01", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(initialUri, navigation.Uri);
        Assert.Equal("https://jobs.example.test/lead-ai-architect", createdJobSet.JobUrl);
        Assert.Equal("Lead AI Architect", createdJobSet.JobPosting?.RoleTitle);
    }

    private static void RegisterPanelServices(IServiceCollection services, WorkspaceSession workspace, JobDiscoveryOptions discoveryOptions)
    {
        var discoveryService = new StubJobDiscoveryService();
        var researchService = new StubJobResearchService();
        var profileService = new JobDiscoveryProfileLightService();
        var searchPlanService = new JobDiscoverySearchPlanService(discoveryOptions);
        var candidateEvidenceService = new CandidateEvidenceService();
        var suggestionService = new JobDiscoverySuggestionService(
            discoveryService,
            researchService,
            new JobFitAnalysisService(candidateEvidenceService),
            new EvidenceSelectionService(candidateEvidenceService));

        services.AddSingleton<IJobDiscoveryService>(discoveryService);
        services.AddSingleton(discoveryOptions);
        services.AddSingleton(profileService);
        services.AddSingleton(searchPlanService);
        services.AddSingleton(suggestionService);
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton(workspace);
    }

    private static JobDiscoveryOptions CreateDiscoveryOptions()
        => new()
        {
            Enabled = true,
            DefaultProviderId = "jobindex",
            Providers =
            [
                new JobDiscoveryProviderOptions
                {
                    Id = "jobindex",
                    DisplayName = "Jobindex",
                    BaseUrl = "https://jobindex.dk",
                    SearchPath = "/jobsoegning/it/systemudvikling/storkoebenhavn",
                    QueryParameterName = "q",
                    AllowedHosts = ["jobindex.dk", "www.jobindex.dk"]
                }
            ]
        };

    private static CandidateProfile CreateCandidateProfile()
        => new()
        {
            Headline = "Lead AI Architect",
            Location = "Copenhagen",
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead AI Architect",
                    "Led Azure and AI architecture delivery.",
                    "Copenhagen",
                    new DateRange(new PartialDate("2023", 2023)))
            ],
            Skills = [new SkillTag("Azure", 1), new SkillTag("Architecture", 2)]
        };

    private static JobDiscoverySuggestionReview CreateSuggestionReview(JobDiscoverySearchPlan searchPlan, string title, string detailUrl)
        => new(
            new JobDiscoverySuggestion(
                "jobindex",
                "Jobindex",
                title,
                "Contoso",
                "Copenhagen",
                $"{title} summary",
                new Uri(detailUrl),
                "Today",
                searchPlan.SearchUri!),
            new JobPostingAnalysis
            {
                SourceUrl = new Uri(detailUrl),
                RoleTitle = title,
                CompanyName = "Contoso",
                Summary = $"{title} summary"
            },
            JobFitAssessment.Empty,
            EvidenceSelectionResult.Empty);

    private static AngleSharp.Dom.IElement GetDiscoverButton(IRenderedComponent<JobDiscoveryPanel> cut)
        => cut.Find("section > .hero-row .secondary-button");

    private sealed class StubJobDiscoveryService : IJobDiscoveryService
    {
        public Task<IReadOnlyList<JobDiscoverySuggestion>> DiscoverAsync(JobDiscoverySearchPlan searchPlan, Action<JobDiscoveryProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<JobDiscoverySuggestion>>(Array.Empty<JobDiscoverySuggestion>());
    }

    private sealed class StubJobResearchService : IJobResearchService
    {
        public Task<JobPostingAnalysis> AnalyzeAsync(Uri jobPostingUrl, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CompanyProfileBuildResult> BuildCompanyProfileAsync(IEnumerable<Uri> sourceUrls, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Uri>> DiscoverCompanyContextUrlsAsync(Uri jobPostingUrl, string? companyName = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Uri>>(Array.Empty<Uri>());

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}