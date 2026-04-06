using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class TechnologyGapAnalyzerTests
{
    [Fact]
    public void Analyze_FindsPossiblyUnderrepresentedTechnologies()
    {
        var candidate = new CandidateProfile
        {
            Headline = "Senior .NET consultant",
            Summary = "Experienced with C#, .NET, Azure, and consulting delivery.",
            Skills = [new SkillTag("Azure", 1), new SkillTag(".NET", 2)]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Contoso",
            Summary = "Drive generative AI, LLM, RAG, and Kubernetes platform work for enterprise delivery."
        };

        var companyProfile = new CompanyResearchProfile
        {
            Summary = "We build agentic AI products on Azure and Kubernetes."
        };

        var result = TechnologyGapAnalyzer.Analyze(candidate, jobPosting, companyProfile);

        Assert.Contains("Generative AI", result.DetectedTechnologies);
        Assert.Contains("LLMs", result.DetectedTechnologies);
        Assert.Contains("RAG", result.DetectedTechnologies);
        Assert.Contains("Kubernetes", result.DetectedTechnologies);
        Assert.Contains("Generative AI", result.PossiblyUnderrepresentedTechnologies);
        Assert.Contains("LLMs", result.PossiblyUnderrepresentedTechnologies);
        Assert.Contains("RAG", result.PossiblyUnderrepresentedTechnologies);
        Assert.Contains("Kubernetes", result.PossiblyUnderrepresentedTechnologies);
        Assert.DoesNotContain("Azure", result.PossiblyUnderrepresentedTechnologies);
    }

    [Fact]
    public void Analyze_UsesSourceBackedAliasesToAvoidFalseTechnologyGaps()
    {
        var candidate = new CandidateProfile
        {
            Headline = "AI consultant",
            Summary = "Built Azure AI Search based assistants for enterprise teams.",
            Skills = [new SkillTag("Azure AI Search", 1)]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Contoso",
            Summary = "Design knowledge retrieval systems for enterprise copilots.",
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
        };

        var result = TechnologyGapAnalyzer.Analyze(candidate, jobPosting, companyProfile: null);

        Assert.Contains("RAG", result.DetectedTechnologies);
        Assert.DoesNotContain("RAG", result.PossiblyUnderrepresentedTechnologies);
    }
}