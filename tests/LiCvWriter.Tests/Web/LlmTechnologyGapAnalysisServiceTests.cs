using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Workflows;

namespace LiCvWriter.Tests.Web;

public sealed class LlmTechnologyGapAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_ParsesStructuredJsonResponse()
    {
        var llmClient = new FakeLlmClient(
            """
            {
              "detectedTechnologies": ["Generative AI", "LLMs", "Kubernetes"],
              "possiblyUnderrepresentedTechnologies": ["Generative AI", "LLMs"]
            }
            """);

        var service = new LlmTechnologyGapAnalysisService(llmClient, new OllamaOptions());

        var result = await service.AnalyzeAsync(
            new CandidateProfile { Summary = "Azure and .NET consultant" },
            new JobPostingAnalysis { RoleTitle = "Lead AI Architect", CompanyName = "Contoso", Summary = "Generative AI, LLM, Kubernetes" },
            new CompanyResearchProfile { Summary = "We build platform products" },
            "session-model",
            "high");

        Assert.Contains("Generative AI", result.DetectedTechnologies);
        Assert.Contains("LLMs", result.PossiblyUnderrepresentedTechnologies);
        Assert.Equal("session-model", llmClient.LastRequest!.Model);
        Assert.Equal("high", llmClient.LastRequest.Think);
        Assert.True(llmClient.LastRequest.Stream);
    }

    [Fact]
    public async Task AnalyzeAsync_IncludesSourceBackedSignalsAndAliasesInPrompt()
    {
        var llmClient = new FakeLlmClient(
            """
            {
              "detectedTechnologies": ["RAG"],
              "possiblyUnderrepresentedTechnologies": []
            }
            """);

        var service = new LlmTechnologyGapAnalysisService(llmClient, new OllamaOptions());

        await service.AnalyzeAsync(
            new CandidateProfile { Summary = "Built Azure AI Search assistants" },
            new JobPostingAnalysis
            {
                RoleTitle = "Lead AI Architect",
                CompanyName = "Contoso",
                Summary = "Design retrieval systems.",
                Signals =
                [
                    new JobContextSignal(
                        "Must have",
                        "RAG",
                        JobRequirementImportance.MustHave,
                        "jobs.example.test",
                        "Experience with Azure AI Search and vector search for copilots.",
                        95,
                        ["Azure AI Search", "vector search"])
                ]
            },
            companyProfile: null,
            "session-model",
            "high");

        var prompt = Assert.Single(llmClient.LastRequest!.Messages).Content;
        Assert.Contains("Source-backed job/company signals", prompt);
        Assert.Contains("RAG", prompt);
        Assert.Contains("Azure AI Search", prompt);
        Assert.Contains("vector search", prompt);
    }

    private sealed class FakeLlmClient(string content) : ILlmClient
    {
        public LlmRequest? LastRequest { get; private set; }

        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            Action<LlmProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new LlmResponse(request.Model, content, null, true, null, null, null));
        }
    }
}