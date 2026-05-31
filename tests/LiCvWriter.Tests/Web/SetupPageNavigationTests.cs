using Bunit;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Web.Components.Layout;
using LiCvWriter.Web.Components.Pages;
using LiCvWriter.Web.Services;
using LinkedInSetupPage = LiCvWriter.Web.Components.Pages.Setup.LinkedIn;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Tests.Web;

public sealed class SetupPageNavigationTests
{
    [Fact]
    public void Render_WhenWorkspaceHasSetupState_ShowsOverviewLinksAndJobStatuses()
    {
        using var context = new BunitContext();
        var workspace = CreateConfiguredWorkspace();

        context.Services.AddSingleton(workspace);

        var cut = context.Render<Home>();

        Assert.Contains("/setup/llm", cut.Markup);
        Assert.Contains("/setup/linkedin", cut.Markup);
        Assert.Contains("/setup/differentiators", cut.Markup);
        Assert.Contains("Ada Lovelace", cut.Markup);
        Assert.Contains("Structured and evidence-driven", cut.Markup);
        Assert.Contains("Refreshing context", cut.Markup);
        Assert.Contains("Job set 1", cut.Markup);
    }

    [Fact]
    public void Render_WhenRendered_ShowsGroupedSetupLinks()
    {
        using var context = new BunitContext();
        var workspace = new WorkspaceSession(new OllamaOptions());

        context.Services.AddSingleton(workspace);

        var cut = context.Render<NavMenu>();

        Assert.Contains("Start / Overview", cut.Markup);
        Assert.Contains("/setup/llm", cut.Markup);
        Assert.Contains("/setup/linkedin", cut.Markup);
        Assert.Contains("/setup/differentiators", cut.Markup);
        Assert.Contains("/workspace/job-workbench", cut.Markup);
    }

    [Fact]
    public void Render_WhenProfileExists_ShowsImportAndProfileSections()
    {
        using var context = new BunitContext();
        var workspace = new WorkspaceSession(new OllamaOptions());

        workspace.UpdateCandidateProfile(new CandidateProfile
        {
            Name = new PersonName("Ada", "Lovelace")
        });

        context.Services.AddSingleton<ILinkedInExportImporter>(new StubLinkedInExportImporter());
        context.Services.AddSingleton(new LinkedInAuthOptions());
        context.Services.AddSingleton(new OperationStatusService());
        context.Services.AddSingleton(workspace);

        var cut = context.Render<LinkedInSetupPage>();

        Assert.Contains("LinkedIn DMA import", cut.Markup);
        Assert.Contains("Imported profile", cut.Markup);
        Assert.Contains("Ada Lovelace", cut.Markup);
        Assert.Contains("Experience", cut.Markup);
        Assert.Contains("Notes", cut.Markup);
    }

    [Fact]
    public void Render_WhenLinkedInSetupLoads_ShowsDefaultSnapshotDomainSelections()
    {
        using var context = new BunitContext();
        var workspace = new WorkspaceSession(new OllamaOptions());

        context.Services.AddSingleton<ILinkedInExportImporter>(new StubLinkedInExportImporter());
        context.Services.AddSingleton(new LinkedInAuthOptions());
        context.Services.AddSingleton(new OperationStatusService());
        context.Services.AddSingleton(workspace);

        var cut = context.Render<LinkedInSetupPage>();
        var checkboxes = cut.FindAll("input[type=checkbox]");

        Assert.Contains("Snapshot domains (14 selected)", cut.Markup);
        Assert.Contains("Profile builder", cut.Markup);
        Assert.Contains("Enrichment notes", cut.Markup);
        Assert.Contains("Optional diagnostics", cut.Markup);
        Assert.Equal(LinkedInSnapshotDomainOption.All.Count, checkboxes.Count);
        Assert.Equal(LinkedInSnapshotDomainOption.DefaultDomains.Count, checkboxes.Count(static checkbox => checkbox.HasAttribute("checked")));
    }

    private static WorkspaceSession CreateConfiguredWorkspace()
    {
        var workspace = new WorkspaceSession(new OllamaOptions { Model = "configured-model", Think = "medium" });

        workspace.SetOllamaAvailability(new LlmModelAvailability(
            "0.9.0",
            "configured-model",
            true,
            ["configured-model", "session-model"]));
        workspace.SetLlmSessionSettings("session-model", "high");
        workspace.UpdateCandidateProfile(new CandidateProfile
        {
            Name = new PersonName("Ada", "Lovelace")
        });
        workspace.SetLinkedInAuthorizationStatus(new LinkedInAuthorizationStatus(
            true,
            "LinkedIn profile ready.",
            null,
            null,
            "r_dma_portability_self_serve"));
        workspace.SetApplicantDifferentiatorProfile(new ApplicantDifferentiatorProfile
        {
            WorkStyle = "Structured and evidence-driven"
        });
        workspace.MarkJobSetRunning("job-set-01", "Refreshing context", JobSetSubtask.JobContext);

        return workspace;
    }

    private sealed class StubLinkedInExportImporter : ILinkedInExportImporter
    {
        public Task<LinkedInExportImportResult> ImportAsync(string exportRootPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LinkedInExportImportResult> ImportMemberSnapshotAsync(
            string accessToken,
            Action<string>? onProgress = null,
            IReadOnlyCollection<string>? selectedDomains = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}