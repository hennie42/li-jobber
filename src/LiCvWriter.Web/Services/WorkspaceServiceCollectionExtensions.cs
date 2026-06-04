using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Csv;
using LiCvWriter.Infrastructure.Documents;
using LiCvWriter.Infrastructure.LinkedIn;
using LiCvWriter.Infrastructure.Storage;
using LiCvWriter.Infrastructure.Workflows;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Registers workspace, document, import, and drafting services used by the interactive web app.
/// </summary>
internal static class WorkspaceServiceCollectionExtensions
{
    public static IServiceCollection AddLiCvWriterWorkspaceServices(
        this IServiceCollection services,
        int defaultSelectedEvidenceCount)
    {
        services.AddSingleton<SimpleCsvParser>();
        services.AddSingleton<LinkedInPartialDateParser>();
        services.AddSingleton<CandidateProfileMergeService>();
        services.AddSingleton<JobDiscoveryProfileLightService>();
        services.AddSingleton<JobDiscoverySearchPlanService>();
        services.AddScoped<JobDiscoverySuggestionService>();

        services.AddSingleton<CandidateEvidenceService>();
        services.AddSingleton<JobFitAnalysisService>();
        services.AddSingleton(provider =>
            new EvidenceSelectionService(provider.GetRequiredService<CandidateEvidenceService>(), defaultSelectedEvidenceCount));
        services.AddSingleton<WorkspaceRecoveryStore>();
        services.AddSingleton<OperationStatusService>();
        services.AddSingleton<LlmOperationBroker>();
        services.AddScoped<LlmTechnologyGapAnalysisService>();
        services.AddScoped<LlmFitEnhancementService>();
        services.AddScoped<InsightsDiscoveryApplicantDifferentiatorDraftingService>();
        services.AddSingleton<WorkspaceSession>();
        services.AddScoped<JobFitWorkspaceRefreshService>();
        services.AddSingleton<ILinkedInExportImporter, LinkedInExportImporter>();
        services.AddSingleton<IInsightsDiscoveryPdfImporter, InsightsDiscoveryPdfImporter>();
        services.AddSingleton<IAuditStore, LocalMarkdownAuditStore>();
        services.AddSingleton<IDocumentRenderer, MarkdownDocumentRenderer>();
        services.AddSingleton<CvQualityValidator>();
        services.AddSingleton<IDocumentExportService, TemplateBasedDocumentExportService>();
        services.AddScoped<IDraftGenerationService, DraftGenerationService>();

        return services;
    }
}