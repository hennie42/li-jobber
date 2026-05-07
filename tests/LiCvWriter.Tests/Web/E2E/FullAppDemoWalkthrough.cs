using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class FullAppDemoWalkthrough(
    IPage page,
    string baseUrl,
    JobWorkbenchPage workbench,
    IReadOnlyList<string> companyNames,
    IReadOnlyList<string> jobSetIds)
{
    private readonly IPage page = page;
    private readonly string baseUrl = baseUrl.TrimEnd('/');
    private readonly JobWorkbenchPage workbench = workbench;
    private readonly IReadOnlyList<string> companyNames = companyNames;
    private readonly IReadOnlyList<string> jobSetIds = jobSetIds;

    private const int FastSceneMilliseconds = 1_200;
    private const int StandardSceneMilliseconds = 2_000;
    private const int FeatureSceneMilliseconds = 2_800;
    private const int MajorSceneMilliseconds = 4_200;
    private const int LiveMonitorSceneMilliseconds = 7_000;

    public async Task RecordAsync()
    {
        if (jobSetIds.Count == 0)
        {
            throw new InvalidOperationException("The full-app demo seed did not return any job set IDs.");
        }

        await ShowStartSetupAsync();
        await ShowRedirectPagesAsync();
        await ShowWorkbenchOverviewAsync();
        await ShowJobSetDetailAsync(jobSetIds[0]);
        await ShowBatchRunAsync();
        await HideGuideAsync();
    }

    private async Task ShowStartSetupAsync()
    {
        await GotoAsync("/");
        await CompanyNameMasker.StartAsync(page, companyNames);

        await GuideAsync(page.Locator(".readiness-chips"), "Start / Setup", "The session opens with LLM, profile, differentiator, and job-set readiness visible.");
        await HoldAsync(MajorSceneMilliseconds);

        await GuideAsync(page.Locator("details.step-card").Nth(0), "LLM session", "Ollama availability and the selected local model are summarized in the first setup step.");
        await HoldAsync(StandardSceneMilliseconds);

        await GuideAsync(page.Locator("details.step-card").Nth(1), "LinkedIn profile", "The seeded DMA-style profile is loaded, so downstream fit and evidence features are available.");
        await HoldAsync(StandardSceneMilliseconds);

        await GuideAsync(page.Locator("details.step-card").Nth(2), "Applicant differentiators", "Work style, communication, leadership, and positioning notes are stored for fit review and drafting.");
        await HoldAsync(StandardSceneMilliseconds);

        await GuideSectionAsync("Imported LinkedIn profile", "Profile review", "The profile reviewer supports editable experience plus education, skills, certifications, projects, recommendations, and notes.");
        await HoldAsync(FeatureSceneMilliseconds);
        await ShowProfileTabsAsync();
    }

    private async Task ShowProfileTabsAsync()
    {
        string[] tabNames = ["Education", "Skills", "Certifications", "Projects", "Recommendations", "Notes"];
        foreach (var tabName in tabNames)
        {
            var tab = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex($"^{Regex.Escape(tabName)}", RegexOptions.IgnoreCase) });
            if (await tab.CountAsync() == 0)
            {
                continue;
            }

            await tab.ClickAsync();
            await CompanyNameMasker.ApplyAsync(page);
            await GuideAsync(page.Locator(".profile-tabs"), $"Profile: {tabName}", "Profile tabs expose the imported material that powers evidence matching and generated documents.");
            await HoldAsync(FastSceneMilliseconds);
        }
    }

    private async Task ShowRedirectPagesAsync()
    {
        await ShowRedirectAsync("/workspace/import-profile", "Import Profile route", "This route now redirects back to Start / Setup because profile import lives in the setup flow.");
        await ShowRedirectAsync("/workspace/generate-drafts", "Generate Drafts route", "Draft generation redirects to Job Workbench, where each job set owns its draft settings.");
        await ShowRedirectAsync("/workspace/job-research", "Job Research route", "Job research also lands in Job Workbench, keeping discovery, research, fit, and drafting together.");
    }

    private async Task ShowRedirectAsync(string path, string title, string detail)
    {
        await GotoAsync(path);
        await page.WaitForURLAsync(new Regex("/(workspace/job-workbench)?$", RegexOptions.IgnoreCase), new PageWaitForURLOptions { Timeout = 60_000 });
        await GuideAsync(page.Locator("body"), title, detail);
        await HoldAsync(StandardSceneMilliseconds);
    }

    private async Task ShowWorkbenchOverviewAsync()
    {
        await workbench.GotoAsync();
        await CompanyNameMasker.StartAsync(page, companyNames);

        await GuideAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Job Workbench" }), "Job Workbench", "The overview combines discovery, batch action settings, and every job set in the workspace.");
        await HoldAsync(MajorSceneMilliseconds);

        await GuideSectionAsync("Discovery", "Discovery", "The discovery panel derives a public job-search query from the loaded profile and stored differentiators.");
        await HoldAsync(FeatureSceneMilliseconds);

        await GuideAsync(page.GetByText("Batch action"), "Batch action", "Document type checkboxes control what the selected job sets will generate.");
        await HoldAsync(FeatureSceneMilliseconds);

        await GuideAsync(workbench.JobRows.First, "Job sets", "Each row shows readiness, fit score, subtask status, output folder, language, and export chips.");
        await HoldAsync(MajorSceneMilliseconds);
    }

    private async Task ShowJobSetDetailAsync(string jobSetId)
    {
        await GotoAsync($"/workspace/job-workbench/{jobSetId}");
        await Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("All jobsets", RegexOptions.IgnoreCase) })).ToBeVisibleAsync();

        await GuideAsync(page.Locator(".hero-row").First, "Job-set detail", "The detail page brings language settings, refresh actions, and subtask state into one job-specific workspace.");
        await HoldAsync(MajorSceneMilliseconds);

        await GuideSectionAsync("Research", "Research", "Research stores the job snapshot, company context, source-backed signals, deadline, themes, and inferred requirements.");
        await HoldAsync(FeatureSceneMilliseconds);

        await GuideSectionAsync("Fit review", "Fit review", "The deterministic fit review scores requirements, explains rationale, and connects each match to profile evidence.");
        await HoldAsync(MajorSceneMilliseconds);

        await GuideSectionAsync("Technology gap check", "Technology gap check", "The technology gap panel compares job context against profile coverage before drafting.");
        await HoldAsync(FeatureSceneMilliseconds);

        await GuideSectionAsync("Ranked evidence", "Ranked evidence", "Evidence cards let the user include or exclude proof points before documents are generated.");
        await HoldAsync(MajorSceneMilliseconds);

        await GuideSectionAsync("Draft generation", "Draft generation", "The draft panel controls output language, document kinds, contact details, and additional prompt instructions.");
        await HoldAsync(FeatureSceneMilliseconds);

        await GuideSectionAsync("Generated markdown drafts", "Generated drafts", "Generated markdown previews stay attached to the job set for review.");
        await HoldAsync(FeatureSceneMilliseconds);

        await GuideSectionAsync("Exported files", "Exported files", "Export links show the Word outputs that would be opened from the local workspace.");
        await HoldAsync(FeatureSceneMilliseconds);
    }

    private async Task ShowBatchRunAsync()
    {
        await workbench.GotoAsync();
        await GuideAsync(workbench.JobRows.First, "Return to batch overview", "The full tour ends by running the seeded job sets through the live batch workflow.");
        await HoldAsync(StandardSceneMilliseconds);

        for (var index = 0; index < Math.Min(3, jobSetIds.Count); index++)
        {
            await GuideAsync(workbench.JobRowSelectionLabels.Nth(index), $"Select job set {index + 1}", "The selected rows join the batch queue.");
            await workbench.SelectJobSetAsync(index);
            await HoldAsync(FastSceneMilliseconds);
        }

        await workbench.HoverStartSelectedAsync();
        await GuideAsync(workbench.StartSelectedButton, "Start selected", "The batch starts with the selected job sets and the current document-kind preferences.");
        await HoldAsync(StandardSceneMilliseconds);
        await workbench.StartSelectedAsync();

        await GuideAsync(workbench.BatchStatusText, "Batch running", "The button and status label switch into live batch progress.");
        await workbench.WaitForLiveLlmProgressAsync();
        await HoldAsync(StandardSceneMilliseconds);

        await workbench.FocusStatusMonitorAsync();
        await GuideAsync(workbench.StatusMonitor, "Status Monitor", "Live LLM status appears while the operation is running.");
        await HoldAsync(LiveMonitorSceneMilliseconds);

        await workbench.FocusReasoningMonitorAsync();
        await GuideAsync(workbench.ReasoningMonitor, "Reasoning Monitor", "The companion monitor keeps current model activity visible.");
        await HoldAsync(LiveMonitorSceneMilliseconds);

        await workbench.FocusActivityFeedAsync();
        await GuideAsync(workbench.ActivityFeed, "Activity feed", "The activity feed records completed setup, analysis, and generation events.");
        await HoldAsync(LiveMonitorSceneMilliseconds);
    }

    private async Task GotoAsync(string path)
    {
        await page.GotoAsync($"{baseUrl}{path}", new PageGotoOptions
        {
            Timeout = 60_000,
            WaitUntil = WaitUntilState.Commit
        });

        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await CompanyNameMasker.ApplyAsync(page);
    }

    private async Task GuideSectionAsync(string heading, string title, string detail)
    {
        await OpenDetailsBySummaryAsync(heading);
        var section = page.Locator($"details:has-text(\"{heading}\")").First;
        await GuideAsync(section, title, detail);
    }

    private async Task OpenDetailsBySummaryAsync(string heading)
    {
        var opened = await page.EvaluateAsync<bool>(
            """
            heading => {
                const detailsElements = [...document.querySelectorAll('details')];
                const match = detailsElements.find(detailsElement => detailsElement.querySelector('summary')?.innerText.includes(heading));
                if (!match) return false;
                match.open = true;
                match.scrollIntoView({ block: 'center', behavior: 'smooth' });
                return true;
            }
            """,
            heading);

        if (!opened)
        {
            throw new InvalidOperationException($"Could not find a details section headed '{heading}' for the full-app demo.");
        }

        await HoldAsync(FastSceneMilliseconds);
        await CompanyNameMasker.ApplyAsync(page);
    }

    private async Task GuideAsync(ILocator target, string title, string detail)
    {
        await Expect(target).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        await target.ScrollIntoViewIfNeededAsync();
        await target.EvaluateAsync(
            """
            (element, args) => {
                const rect = element.getBoundingClientRect();
                const fallback = rect.width <= 0 || rect.height <= 0;

                const overlayId = 'li-cv-full-demo-overlay';
                let overlay = document.getElementById(overlayId);
                if (!overlay) {
                    overlay = document.createElement('div');
                    overlay.id = overlayId;
                    overlay.innerHTML = '<div data-guide-box></div><div data-guide-card><strong></strong><span></span><i></i></div>';
                    document.body.appendChild(overlay);
                }

                let style = document.getElementById('li-cv-full-demo-style');
                if (!style) {
                    style = document.createElement('style');
                    style.id = 'li-cv-full-demo-style';
                    style.textContent = `
                        #li-cv-full-demo-overlay { position: fixed; inset: 0; z-index: 2147483000; pointer-events: none; font-family: "Segoe UI", Arial, sans-serif; }
                        #li-cv-full-demo-overlay [data-guide-box] { position: fixed; border: 3px solid #0f766e; border-radius: 10px; box-shadow: 0 0 0 9999px rgba(10, 18, 30, 0.07), 0 0 28px rgba(15, 118, 110, 0.42); transition: all 180ms ease; }
                        #li-cv-full-demo-overlay [data-guide-card] { position: fixed; max-width: 360px; padding: 12px 14px 13px; border-radius: 8px; background: rgba(18, 24, 32, 0.95); color: #fff; box-shadow: 0 16px 40px rgba(0,0,0,0.25); overflow: hidden; transition: all 180ms ease; }
                        #li-cv-full-demo-overlay strong { display: block; margin-bottom: 4px; font-size: 15px; line-height: 1.25; }
                        #li-cv-full-demo-overlay span { display: block; font-size: 12px; line-height: 1.4; color: rgba(255,255,255,0.84); }
                        #li-cv-full-demo-overlay i { position: absolute; left: -45%; bottom: 0; width: 45%; height: 3px; background: linear-gradient(90deg, transparent, #5eead4, transparent); animation: liCvFullGuideSweep 0.95s linear infinite; }
                        @keyframes liCvFullGuideSweep { from { transform: translateX(0); } to { transform: translateX(320%); } }
                    `;
                    document.head.appendChild(style);
                }

                const margin = 8;
                const boxElement = overlay.querySelector('[data-guide-box]');
                const cardElement = overlay.querySelector('[data-guide-card]');
                const titleElement = cardElement.querySelector('strong');
                const detailElement = cardElement.querySelector('span');
                const targetLeft = fallback ? 64 : rect.left;
                const targetTop = fallback ? 80 : rect.top;
                const targetWidth = fallback ? Math.min(560, window.innerWidth - 128) : rect.width;
                const targetHeight = fallback ? 140 : Math.min(rect.height, window.innerHeight - 32);
                const boxLeft = Math.max(margin, targetLeft - margin);
                const boxTop = Math.max(margin, targetTop - margin);
                const boxWidth = Math.min(window.innerWidth - boxLeft - margin, targetWidth + margin * 2);
                const boxHeight = Math.min(window.innerHeight - boxTop - margin, targetHeight + margin * 2);
                const cardWidth = Math.min(360, window.innerWidth - margin * 2);
                const cardLeft = Math.min(window.innerWidth - cardWidth - margin, Math.max(margin, boxLeft));
                const below = boxTop + boxHeight + 10;
                const cardTop = below + 120 < window.innerHeight ? below : Math.max(margin, boxTop - 132);

                boxElement.style.left = `${boxLeft}px`;
                boxElement.style.top = `${boxTop}px`;
                boxElement.style.width = `${boxWidth}px`;
                boxElement.style.height = `${boxHeight}px`;
                cardElement.style.left = `${cardLeft}px`;
                cardElement.style.top = `${cardTop}px`;
                cardElement.style.width = `${cardWidth}px`;
                titleElement.textContent = args.title;
                detailElement.textContent = args.detail;
            }
            """,
            new { title, detail });

        await CompanyNameMasker.ApplyAsync(page);
    }

    private Task HideGuideAsync()
        => page.EvaluateAsync("() => document.getElementById('li-cv-full-demo-overlay')?.remove()");

    private Task HoldAsync(int milliseconds)
        => page.WaitForTimeoutAsync(milliseconds);
}