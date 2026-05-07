using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class GuidedDemoWalkthrough(
    IPage page,
    JobWorkbenchPage workbench,
    DemoArtifactWriter artifacts,
    IReadOnlyList<string> companyNames)
{
    private readonly IPage page = page;
    private readonly JobWorkbenchPage workbench = workbench;
    private readonly DemoArtifactWriter artifacts = artifacts;
    private readonly IReadOnlyList<string> companyNames = companyNames;
    private const int OpeningSceneMilliseconds = 7_000;
    private const int JobSetReviewSceneMilliseconds = 6_000;
    private const int SelectionPreviewMilliseconds = 3_500;
    private const int SelectionAppliedMilliseconds = 3_500;
    private const int StartPreviewMilliseconds = 7_000;
    private const int BatchRunningSceneMilliseconds = 8_000;
    private const int StatusMonitorSceneMilliseconds = 26_000;
    private const int ReasoningMonitorSceneMilliseconds = 24_000;
    private const int ActivityFeedSceneMilliseconds = 18_000;
    private const int WorkbenchLabelsSceneMilliseconds = 18_000;
    private const int FinalReviewSceneMilliseconds = 5_000;

    public async Task RecordAsync()
    {
        await workbench.GotoAsync();
        await CompanyNameMasker.StartAsync(page, companyNames);
        await Expect(workbench.SelectedJobCheckboxes).ToHaveCountAsync(0);

        await GuideAsync(workbench.JobRowTitles.First, "Ready job sets", "Three seeded job sets are prepared for a batch run.");
    await HoldAsync(OpeningSceneMilliseconds);
        await SweepJobSetsAsync("Review job set");
        await HideGuideAsync();
        await artifacts.CaptureAsync(
            page,
            "01-ready-jobsets.png",
            "screenshot of the Job Workbench showing three ready job sets with company names blurred");

        for (var index = 0; index < 3; index++)
        {
            await GuideAsync(workbench.JobRowSelectionLabels.Nth(index), $"Select job set {index + 1}", "Each selected row joins the batch queue.");
            await HoldAsync(SelectionPreviewMilliseconds);
            await workbench.SelectJobSetAsync(index);
            await HoldAsync(SelectionAppliedMilliseconds);
        }

        await HideGuideAsync();
        await artifacts.CaptureAsync(
            page,
            "02-three-jobsets-selected.png",
            "screenshot of the Job Workbench with all three batch checkboxes selected and company names blurred");

        await workbench.HoverStartSelectedAsync();
        await GuideAsync(workbench.StartSelectedButton, "Start selected", "The batch starts only after the three checked job sets are ready.");
        await HoldAsync(StartPreviewMilliseconds);
        await workbench.StartSelectedAsync();

        await GuideAsync(workbench.BatchStatusText, "Batch running", "The workbench label changes as the selected job sets begin processing.");
        await workbench.WaitForLiveLlmProgressAsync();
        await HoldAsync(BatchRunningSceneMilliseconds);

        await workbench.FocusStatusMonitorAsync();
        await GuideAsync(workbench.StatusMonitor, "Status Monitor", "Live LLM output streams here while the batch is running.");
        await HoldAsync(StatusMonitorSceneMilliseconds);
        await HideGuideAsync();
        await artifacts.CaptureAsync(
            page,
            "03-live-llm-progress.png",
            "screenshot of the running batch with the Status Monitor and job-set labels updating while company names are blurred");

        await workbench.FocusReasoningMonitorAsync();
        await GuideAsync(workbench.ReasoningMonitor, "Reasoning Monitor", "The side monitor keeps the current model activity visible during the run.");
        await HoldAsync(ReasoningMonitorSceneMilliseconds);

        await workbench.FocusActivityFeedAsync();
        await GuideAsync(workbench.ActivityFeed, "Activity feed", "Completed steps are collected below the live monitors.");
        await HoldAsync(ActivityFeedSceneMilliseconds);

        await workbench.ScrollWorkbenchToTopAsync();
        await GuideAsync(workbench.JobRowStatuses.First, "Workbench labels", "The rows and batch label show the live state without waiting for full completion.");
        await HoldAsync(WorkbenchLabelsSceneMilliseconds);
        await HideGuideAsync();
        await CompanyNameMasker.ApplyAsync(page);
        await artifacts.CaptureAsync(
            page,
            "04-workbench-labels-updated.png",
            "screenshot of the Job Workbench after live LLM output appears in the Status Monitor and workbench labels update with company names blurred");
        await HoldAsync(FinalReviewSceneMilliseconds);
    }

    private async Task SweepJobSetsAsync(string label)
    {
        for (var index = 0; index < 3; index++)
        {
            await workbench.HoverJobSetAsync(index);
            await GuideAsync(workbench.JobRowTitles.Nth(index), $"{label} {index + 1}", "The blurred company text remains masked while the row is inspected.");
            await HoldAsync(JobSetReviewSceneMilliseconds);
        }
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

                const id = 'li-cv-guided-demo-overlay';
                let overlay = document.getElementById(id);
                if (!overlay) {
                    overlay = document.createElement('div');
                    overlay.id = id;
                    overlay.innerHTML = '<div data-guide-box></div><div data-guide-card><strong></strong><span></span><i></i></div>';
                    document.body.appendChild(overlay);
                }

                let style = document.getElementById('li-cv-guided-demo-style');
                if (!style) {
                    style = document.createElement('style');
                    style.id = 'li-cv-guided-demo-style';
                    style.textContent = `
                        #li-cv-guided-demo-overlay { position: fixed; inset: 0; z-index: 2147483000; pointer-events: none; font-family: "Segoe UI", Arial, sans-serif; }
                        #li-cv-guided-demo-overlay [data-guide-box] { position: fixed; border: 3px solid #1f6feb; border-radius: 12px; box-shadow: 0 0 0 9999px rgba(10, 18, 30, 0.08), 0 0 28px rgba(31, 111, 235, 0.42); transition: all 260ms ease; }
                        #li-cv-guided-demo-overlay [data-guide-card] { position: fixed; max-width: 340px; padding: 12px 14px 13px; border-radius: 8px; background: rgba(18, 24, 32, 0.94); color: #fff; box-shadow: 0 16px 40px rgba(0,0,0,0.25); overflow: hidden; transition: all 260ms ease; }
                        #li-cv-guided-demo-overlay strong { display: block; margin-bottom: 4px; font-size: 15px; line-height: 1.25; }
                        #li-cv-guided-demo-overlay span { display: block; font-size: 12px; line-height: 1.4; color: rgba(255,255,255,0.84); }
                        #li-cv-guided-demo-overlay i { position: absolute; left: -45%; right: auto; bottom: 0; width: 45%; height: 3px; background: linear-gradient(90deg, transparent, #58a6ff, transparent); animation: liCvGuideSweep 1.15s linear infinite; }
                        @keyframes liCvGuideSweep { from { transform: translateX(0); } to { transform: translateX(320%); } }
                    `;
                    document.head.appendChild(style);
                }

                const margin = 8;
                const boxElement = overlay.querySelector('[data-guide-box]');
                const cardElement = overlay.querySelector('[data-guide-card]');
                const titleElement = cardElement.querySelector('strong');
                const detailElement = cardElement.querySelector('span');
                const targetLeft = fallback ? 72 : rect.left;
                const targetTop = fallback ? 96 : rect.top;
                const targetWidth = fallback ? Math.min(460, window.innerWidth - 144) : rect.width;
                const targetHeight = fallback ? 120 : rect.height;
                const x = Math.max(margin, targetLeft - margin);
                const y = Math.max(margin, targetTop - margin);
                const width = Math.min(window.innerWidth - x - margin, targetWidth + margin * 2);
                const height = Math.min(window.innerHeight - y - margin, targetHeight + margin * 2);
                const cardWidth = Math.min(340, window.innerWidth - margin * 2);
                const cardLeft = Math.min(window.innerWidth - cardWidth - margin, Math.max(margin, x));
                const below = y + height + 12;
                const cardTop = below + 112 < window.innerHeight ? below : Math.max(margin, y - 132);

                boxElement.style.left = `${x}px`;
                boxElement.style.top = `${y}px`;
                boxElement.style.width = `${width}px`;
                boxElement.style.height = `${height}px`;
                cardElement.style.left = `${cardLeft}px`;
                cardElement.style.top = `${cardTop}px`;
                cardElement.style.width = `${cardWidth}px`;
                titleElement.textContent = args.title;
                detailElement.textContent = args.detail;
            }
            """,
                new { title, detail });
    }

    private Task HideGuideAsync()
        => page.EvaluateAsync("() => document.getElementById('li-cv-guided-demo-overlay')?.remove()");

    private Task HoldAsync(int milliseconds)
        => page.WaitForTimeoutAsync(milliseconds);
}