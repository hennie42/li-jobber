using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Auditing;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Documents;
using LiCvWriter.Infrastructure.Workflows;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class DraftGenerationServiceTests
{
    [Fact]
    public async Task GenerateAsync_UsesSessionSelectedModelAndThinkingLevel()
    {
        var llmClient = new CapturingLlmClient();
        var auditStore = new RecordingAuditStore();
        var service = CreateService(llmClient, auditStore, new OllamaOptions
        {
            Model = "configured-model",
            Think = "low",
            UseChatEndpoint = true,
            KeepAlive = "5m",
            Temperature = 0.1
        });

        var request = CreateRequest(LlmModel: "session-model", LlmThinkingLevel: "high");

        await service.GenerateAsync(request);

        Assert.NotNull(llmClient.LastRequest);
        Assert.Equal("session-model", llmClient.LastRequest!.Model);
        Assert.Equal("high", llmClient.LastRequest.Think);
        Assert.True(llmClient.LastRequest.Stream);
        Assert.Contains(auditStore.Entries, entry => entry.Metadata.TryGetValue("ThinkingLevel", out var value) && value == "high");
    }

    [Fact]
    public async Task GenerateAsync_FallsBackToConfiguredModelAndThinkingLevel()
    {
        var llmClient = new CapturingLlmClient();
        var service = CreateService(llmClient, new RecordingAuditStore(), new OllamaOptions
        {
            Model = "configured-model",
            Think = "medium",
            UseChatEndpoint = true,
            KeepAlive = "5m",
            Temperature = 0.1
        });

        await service.GenerateAsync(CreateRequest());

        Assert.NotNull(llmClient.LastRequest);
        Assert.Equal("configured-model", llmClient.LastRequest!.Model);
        Assert.Equal("medium", llmClient.LastRequest.Think);
    }

    [Fact]
    public async Task GenerateAsync_EmbedsFitDifferentiatorsAndSelectedEvidenceIntoPrompt()
    {
        var llmClient = new CapturingLlmClient();
        var service = CreateService(llmClient, new RecordingAuditStore(), new OllamaOptions
        {
            Model = "configured-model",
            Think = "medium",
            UseChatEndpoint = true,
            KeepAlive = "5m",
            Temperature = 0.1
        });

        var request = CreateRequest(
            JobFitAssessment: new JobFitAssessment(
                81,
                JobFitRecommendation.Apply,
                [new JobRequirementAssessment("Must have", "Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong, ["Lead Architect @ Contoso"], "Supported by recent architecture delivery.")],
                ["Azure: Supported by recent architecture delivery."],
                Array.Empty<string>()),
            ApplicantDifferentiatorProfile: new ApplicantDifferentiatorProfile
            {
                TargetNarrative = "Pragmatic AI architect",
                StakeholderStyle = "Trusted advisor to client leadership"
            },
            EvidenceSelection: new EvidenceSelectionResult(
            [
                new RankedEvidenceItem(
                    new CandidateEvidenceItem("experience:lead-architect", CandidateEvidenceType.Experience, "Lead Architect @ Contoso", "Led Azure platform delivery.", ["Azure"]),
                    42,
                    ["Supports must-have: Azure"],
                    true)
            ]));

        await service.GenerateAsync(request);

        var combinedPrompts = string.Join("\n", llmClient.AllRequests.Select(r => r.Messages[0].Content));
        Assert.Contains("Fit review:", combinedPrompts);
        Assert.Contains("Strength: Azure", combinedPrompts);
        Assert.Contains("Overall fit score: 81/100 (Apply)", combinedPrompts);
        Assert.Contains("Do not expose fit scores", combinedPrompts);
        Assert.Contains("Applicant differentiators", combinedPrompts);
        Assert.Contains("Pragmatic AI architect", combinedPrompts);
        Assert.Contains("Selected evidence:", combinedPrompts);
        Assert.Contains("Lead Architect @ Contoso", combinedPrompts);
    }

    [Fact]
    public async Task GenerateAsync_Cv_AppliesQualityValidationMetricsToAuditMetadata()
    {
        var llmClient = new CapturingLlmClient();
        var auditStore = new RecordingAuditStore();
        var service = new DraftGenerationService(
            llmClient,
            new LongProfileDocumentRenderer(),
            new UnexpectedExportService(),
            auditStore,
            new CvQualityValidator(),
            new OllamaOptions
            {
                Model = "configured-model",
                Think = "medium",
                UseChatEndpoint = true,
                KeepAlive = "5m",
                Temperature = 0.1
            });

        var result = await service.GenerateAsync(CreateRequest());

        var document = Assert.Single(result.Documents);
        Assert.DoesNotContain("line 5", document.Markdown, StringComparison.OrdinalIgnoreCase);

        var entry = Assert.Single(auditStore.Entries);
        Assert.Equal("True", entry.Metadata["CvSummaryTrimmed"]);
        Assert.Equal("TrimmedProfessionalProfile", entry.Metadata["CvAppliedFixes"]);
    }

    [Fact]
    public async Task GenerateAsync_AttachesPerDocumentLlmMetadata()
    {
        var llmClient = new CapturingLlmClient();
        var service = CreateService(llmClient, new RecordingAuditStore(), new OllamaOptions
        {
            Model = "configured-model",
            Think = "medium",
            UseChatEndpoint = true,
            KeepAlive = "5m",
            Temperature = 0.1
        });

        var result = await service.GenerateAsync(CreateRequest());
        var document = Assert.Single(result.Documents);

        // CV path makes one call per section (3 default sections: ProfileSummary, KeySkills, ExperienceHighlights).
        var calls = llmClient.AllRequests.Count;
        Assert.Equal(3, calls);
        Assert.Equal(TimeSpan.FromSeconds(2 * calls), document.LlmDuration);
        Assert.Equal(12L * calls, document.PromptTokens);
        Assert.Equal(34L * calls, document.CompletionTokens);
        Assert.Equal("configured-model", document.Model);
    }

    [Fact]
    public async Task GenerateAsync_Cv_MakesOneLlmCallPerSection()
    {
        var llmClient = new CapturingLlmClient();
        var service = CreateService(llmClient, new RecordingAuditStore(), new OllamaOptions
        {
            Model = "configured-model",
            Think = "medium",
            UseChatEndpoint = true,
            KeepAlive = "5m",
            Temperature = 0.1
        });

        await service.GenerateAsync(CreateRequest());

        // No projects in default candidate → ProjectHighlights skipped → 3 calls.
        Assert.Equal(3, llmClient.AllRequests.Count);
    }

    [Fact]
    public async Task GenerateAsync_Cv_AddsProjectHighlightsCallWhenProjectsExist()
    {
        var llmClient = new CapturingLlmClient();
        var service = CreateService(llmClient, new RecordingAuditStore(), new OllamaOptions
        {
            Model = "configured-model",
            Think = "medium",
            UseChatEndpoint = true,
            KeepAlive = "5m",
            Temperature = 0.1
        });

        var baseRequest = CreateRequest();
        var requestWithProjects = baseRequest with
        {
            Candidate = baseRequest.Candidate with
            {
                Projects =
                [
                    new ProjectEntry("Cloud Migration Portal", "Built a self-service migration portal.", null, new DateRange())
                ]
            }
        };

        await service.GenerateAsync(requestWithProjects);

        Assert.Equal(4, llmClient.AllRequests.Count);
    }

    [Fact]
    public async Task GenerateAsync_NonCvKind_MakesSingleLlmCall()
    {
        var llmClient = new CapturingLlmClient();
        var service = CreateService(llmClient, new RecordingAuditStore(), new OllamaOptions
        {
            Model = "configured-model",
            Think = "medium",
            UseChatEndpoint = true,
            KeepAlive = "5m",
            Temperature = 0.1
        });

        var baseRequest = CreateRequest();
        var coverLetterRequest = baseRequest with { DocumentKinds = [DocumentKind.CoverLetter] };

        await service.GenerateAsync(coverLetterRequest);

        Assert.Single(llmClient.AllRequests);
    }

    [Fact]
    public async Task GenerateAsync_NonCvKinds_IncludeFocusedOnePagePromptGuidance()
    {
        var llmClient = new CapturingLlmClient();
        var service = CreateService(llmClient, new RecordingAuditStore(), new OllamaOptions
        {
            Model = "configured-model",
            Think = "medium",
            UseChatEndpoint = true,
            KeepAlive = "5m",
            Temperature = 0.1
        });

        var baseRequest = CreateRequest();
        var request = baseRequest with
        {
            DocumentKinds = [DocumentKind.CoverLetter, DocumentKind.ProfileSummary, DocumentKind.InterviewNotes]
        };

        await service.GenerateAsync(request);

        var combinedSystemPrompts = string.Join("\n", llmClient.AllRequests.Select(static request => request.SystemPrompt));
        var combinedUserPrompts = string.Join("\n", llmClient.AllRequests.Select(static request => request.Messages[0].Content));

        Assert.Contains("no more than one page", combinedSystemPrompts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("very focused", combinedUserPrompts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Generate interview questions", combinedUserPrompts);
        Assert.DoesNotContain("STAR Examples", combinedSystemPrompts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_Cv_PassesGeneratedSectionsToRenderer()
    {
        var llmClient = new CapturingLlmClient();
        var capturingRenderer = new CapturingDocumentRenderer();
        var service = new DraftGenerationService(
            llmClient,
            capturingRenderer,
            new UnexpectedExportService(),
            new RecordingAuditStore(),
            new CvQualityValidator(),
            new OllamaOptions
            {
                Model = "configured-model",
                Think = "medium",
                UseChatEndpoint = true,
                KeepAlive = "5m",
                Temperature = 0.1
            });

        await service.GenerateAsync(CreateRequest());

        Assert.NotNull(capturingRenderer.LastRequest);
        Assert.NotNull(capturingRenderer.LastRequest!.GeneratedSections);
        var sections = capturingRenderer.LastRequest.GeneratedSections!;
        Assert.Contains(sections, s => s.Section == CvSection.ProfileSummary);
        Assert.Contains(sections, s => s.Section == CvSection.KeySkills);
        Assert.Contains(sections, s => s.Section == CvSection.ExperienceHighlights);
    }

    private static DraftGenerationService CreateService(CapturingLlmClient llmClient, RecordingAuditStore auditStore, OllamaOptions options)
        => new(
            llmClient,
            new FakeDocumentRenderer(),
            new UnexpectedExportService(),
            auditStore,
            new CvQualityValidator(),
            options);

    private static DraftGenerationRequest CreateRequest(
        string? LlmModel = null,
        string? LlmThinkingLevel = null,
        JobFitAssessment? JobFitAssessment = null,
        ApplicantDifferentiatorProfile? ApplicantDifferentiatorProfile = null,
        EvidenceSelectionResult? EvidenceSelection = null)
        => new(
            new CandidateProfile
            {
                Name = new PersonName("Alex", "Taylor"),
                Summary = "Senior architect"
            },
            new JobPostingAnalysis
            {
                RoleTitle = "Lead Architect",
                CompanyName = "Contoso",
                Summary = "Build resilient systems"
            },
            CompanyContext: "Consulting context",
            AdditionalInstructions: "Stay concrete.",
            DocumentKinds: [DocumentKind.Cv],
            ExportToFiles: false,
            LlmModel: LlmModel,
            LlmThinkingLevel: LlmThinkingLevel,
            JobFitAssessment: JobFitAssessment,
            ApplicantDifferentiatorProfile: ApplicantDifferentiatorProfile,
            EvidenceSelection: EvidenceSelection);

    private sealed class CapturingLlmClient : ILlmClient
    {
        public LlmRequest? LastRequest { get; private set; }
        public List<LlmRequest> AllRequests { get; } = [];

        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<OllamaModelInfo?>(null);

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            Action<LlmProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            AllRequests.Add(request);
            return Task.FromResult(new LlmResponse(
                request.Model,
                "Generated content",
                null,
                true,
                12,
                34,
                TimeSpan.FromSeconds(2)));
        }
    }

    private sealed class FakeDocumentRenderer : IDocumentRenderer
    {
        public Task<GeneratedDocument> RenderAsync(DocumentRenderRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedDocument(
                request.Kind,
                $"{request.JobPosting.RoleTitle} draft",
                "# Draft",
                "Draft",
                DateTimeOffset.UtcNow));
    }

    private sealed class CapturingDocumentRenderer : IDocumentRenderer
    {
        public DocumentRenderRequest? LastRequest { get; private set; }

        public Task<GeneratedDocument> RenderAsync(DocumentRenderRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new GeneratedDocument(
                request.Kind,
                $"{request.JobPosting.RoleTitle} draft",
                "# Draft",
                "Draft",
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class UnexpectedExportService : IDocumentExportService
    {
        public Task<DocumentExportResult> ExportAsync(GeneratedDocument document, CancellationToken cancellationToken = default)
            => throw new Xunit.Sdk.XunitException("ExportAsync should not be called when ExportToFiles is false.");
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        public List<AuditTrailEntry> Entries { get; } = [];

        public Task SaveAsync(AuditTrailEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class LongProfileDocumentRenderer : IDocumentRenderer
    {
        public Task<GeneratedDocument> RenderAsync(DocumentRenderRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedDocument(
                request.Kind,
                $"{request.JobPosting.RoleTitle} draft",
                """
# Draft

## Professional Profile

line 1
line 2
line 3
line 4
line 5

## Professional Experience

- Delivered 3 major programs.
""",
                "Draft",
                DateTimeOffset.UtcNow));
    }
}