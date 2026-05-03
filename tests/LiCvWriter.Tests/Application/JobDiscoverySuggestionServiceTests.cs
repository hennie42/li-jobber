using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Tests.Application;

public sealed class JobDiscoverySuggestionServiceTests
{
    [Fact]
    public async Task DiscoverAsync_WithAnalyzableSuggestions_ReturnsFitRankedReviews()
    {
        var discoveryService = new FakeJobDiscoveryService(
        [
            new JobDiscoverySuggestion(
                "jobindex",
                "Jobindex",
                "Lead AI Architect",
                "Contoso",
                "Copenhagen",
                "Lead architecture and AI delivery.",
                new Uri("https://jobs.example.test/lead-ai-architect"),
                "Today",
                new Uri("https://www.jobindex.dk/jobsoegning?q=architect")),
            new JobDiscoverySuggestion(
                "jobindex",
                "Jobindex",
                "React Native Architect",
                "Fabrikam",
                "Aalborg",
                "Own the mobile architecture.",
                new Uri("https://jobs.example.test/react-native-architect"),
                "Today",
                new Uri("https://www.jobindex.dk/jobsoegning?q=architect"))
        ]);
        var researchService = new FakeJobResearchService(new Dictionary<string, JobPostingAnalysis>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://jobs.example.test/lead-ai-architect"] = new()
            {
                SourceUrl = new Uri("https://jobs.example.test/lead-ai-architect"),
                RoleTitle = "Lead AI Architect",
                CompanyName = "Contoso",
                Summary = "Lead Azure and AI delivery.",
                MustHaveThemes = ["Azure", "Architecture", "Client leadership"]
            },
            ["https://jobs.example.test/react-native-architect"] = new()
            {
                SourceUrl = new Uri("https://jobs.example.test/react-native-architect"),
                RoleTitle = "React Native Architect",
                CompanyName = "Fabrikam",
                Summary = "Own React Native delivery.",
                MustHaveThemes = ["React", "Mobile architecture"]
            }
        });
        var service = CreateService(discoveryService, researchService);

        var candidate = new CandidateProfile
        {
            Headline = "Lead architect for Azure consulting delivery",
            Summary = "Client-facing architect with Azure platform delivery.",
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Owned Azure architecture and stakeholder management for enterprise clients.",
                    null,
                    new DateRange(new PartialDate("2023", 2023)))
            ],
            Skills = [new SkillTag("Azure", 1), new SkillTag("Architecture", 2)]
        };

        var result = await service.DiscoverAsync(
            new JobDiscoverySearchPlan("jobindex", "Jobindex", "architect", "Copenhagen", new Uri("https://www.jobindex.dk/jobsoegning?q=architect")),
            candidate,
            new ApplicantDifferentiatorProfile { TargetNarrative = "Pragmatic AI architect" },
            selectedModel: "session-model",
            selectedThinkingLevel: "low",
            enrichWithFit: true);

        Assert.Equal(2, result.Count);
        Assert.Equal("Lead AI Architect", result[0].Suggestion.Title);
        Assert.True(result[0].HasJobPosting);
        Assert.True(result[0].HasFitAssessment);
        Assert.True(result[0].HasEvidenceSelection);
        Assert.True(result[0].JobFitAssessment.OverallScore >= result[1].JobFitAssessment.OverallScore);
    }

    [Fact]
    public async Task DiscoverAsync_WhenAnalysisFails_LeavesRawSuggestionWithError()
    {
        var discoveryService = new FakeJobDiscoveryService(
        [
            new JobDiscoverySuggestion(
                "jobindex",
                "Jobindex",
                "Lead AI Architect",
                "Contoso",
                "Copenhagen",
                "Lead architecture and AI delivery.",
                new Uri("https://jobs.example.test/lead-ai-architect"),
                "Today",
                new Uri("https://www.jobindex.dk/jobsoegning?q=architect"))
        ]);
        var researchService = new ThrowingJobResearchService();
        var service = CreateService(discoveryService, researchService);

        var result = await service.DiscoverAsync(
            new JobDiscoverySearchPlan("jobindex", "Jobindex", "architect", "Copenhagen", new Uri("https://www.jobindex.dk/jobsoegning?q=architect")),
            new CandidateProfile { Skills = [new SkillTag("Azure", 1)] },
            ApplicantDifferentiatorProfile.Empty,
            selectedModel: "session-model",
            selectedThinkingLevel: "low",
            enrichWithFit: true);

        var review = Assert.Single(result);
        Assert.False(review.HasJobPosting);
        Assert.False(review.HasFitAssessment);
        Assert.True(review.HasAnalysisError);
    }

    [Fact]
    public async Task DiscoverAsync_WithoutFitEnrichment_ReturnsRawSuggestions()
    {
        var discoveryService = new FakeJobDiscoveryService(
        [
            new JobDiscoverySuggestion(
                "jobindex",
                "Jobindex",
                "Lead AI Architect",
                "Contoso",
                "Copenhagen",
                "Lead architecture and AI delivery.",
                new Uri("https://jobs.example.test/lead-ai-architect"),
                "Today",
                new Uri("https://www.jobindex.dk/jobsoegning?q=architect"))
        ]);
        var service = CreateService(discoveryService, new ThrowingJobResearchService());

        var result = await service.DiscoverAsync(
            new JobDiscoverySearchPlan("jobindex", "Jobindex", "architect", "Copenhagen", new Uri("https://www.jobindex.dk/jobsoegning?q=architect")),
            candidateProfile: null,
            ApplicantDifferentiatorProfile.Empty,
            enrichWithFit: false);

        var review = Assert.Single(result);
        Assert.False(review.HasJobPosting);
        Assert.False(review.HasFitAssessment);
        Assert.False(review.HasAnalysisError);
    }

    private static JobDiscoverySuggestionService CreateService(IJobDiscoveryService discoveryService, IJobResearchService researchService)
        => new(
            discoveryService,
            researchService,
            new JobFitAnalysisService(new CandidateEvidenceService()),
            new EvidenceSelectionService(new CandidateEvidenceService(), defaultSelectedEvidenceCount: 10));

    private sealed class FakeJobDiscoveryService(IReadOnlyList<JobDiscoverySuggestion> suggestions) : IJobDiscoveryService
    {
        public Task<IReadOnlyList<JobDiscoverySuggestion>> DiscoverAsync(JobDiscoverySearchPlan searchPlan, CancellationToken cancellationToken = default)
            => Task.FromResult(suggestions);
    }

    private sealed class FakeJobResearchService(IReadOnlyDictionary<string, JobPostingAnalysis> results) : IJobResearchService
    {
        public Task<JobPostingAnalysis> AnalyzeAsync(Uri jobPostingUrl, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(results[jobPostingUrl.AbsoluteUri]);

        public Task<CompanyResearchProfile> BuildCompanyProfileAsync(IEnumerable<Uri> sourceUrls, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingJobResearchService : IJobResearchService
    {
        public Task<JobPostingAnalysis> AnalyzeAsync(Uri jobPostingUrl, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Parse failed.");

        public Task<CompanyResearchProfile> BuildCompanyProfileAsync(IEnumerable<Uri> sourceUrls, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<JobPostingAnalysis> AnalyzeTextAsync(string jobPostingText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(string companyContextText, string? selectedModel = null, string? selectedThinkingLevel = null, Action<LlmProgressUpdate>? progress = null, string? sourceLanguageHint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}