using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class BenchmarkDemoWalkthrough(IPage page, LlmSetupPage setupPage, string preferredOllamaModel)
{
    private static readonly string[] PreferredSmallOllamaModels =
    [
        "codellama:7b-instruct",
        "codellama:7b",
        "gemma4:e2b",
        "granite4.1:8b-q8_0",
        "dotnet-coder:expert"
    ];

    private static readonly string[] PreferredSmallFoundryAliases =
    [
        "phi-4-mini",
        "deepseek-r1-1.5b",
        "phi-3.5-mini",
        "phi-3-mini-4k",
        "phi-3-mini-128k",
        "gemma-3-4b-it",
        "olmo-3-7b-instruct",
        "qwen2.5-7b-instruct"
    ];

    private readonly IPage page = page;
    private readonly LlmSetupPage setupPage = setupPage;
    private readonly string preferredOllamaModel = preferredOllamaModel;

    private const int IntroSceneMilliseconds = 8_000;
    private const int ProviderSummarySceneMilliseconds = 10_000;
    private const int SelectionSceneMilliseconds = 8_000;
    private const int ActionSceneMilliseconds = 8_000;
    private const int LiveRailSceneMilliseconds = 26_000;
    private const int MonitorSceneMilliseconds = 14_000;
    private const int SummarySceneMilliseconds = 14_000;
    private const int FinalSceneMilliseconds = 38_000;

    public async Task RecordAsync()
    {
        await setupPage.GotoAsync();

        await ShowIntroAsync();

        var ollamaModel = await ShowOllamaBenchmarkAsync();
        await ShowOllamaSummaryAsync(ollamaModel);

        var foundryModel = await ShowFoundryBenchmarkAsync();
        await ShowFoundrySummaryAsync(foundryModel);

        await ShowClosingSceneAsync();
        await HideGuideAsync();
    }

    private async Task ShowIntroAsync()
    {
        await GuideAsync(setupPage.ProviderSelect, "Shared benchmark workspace", "The setup page keeps Ollama and Microsoft Foundry Local on the same benchmark surface.");
        await HoldAsync(IntroSceneMilliseconds);
    }

    private async Task<string> ShowOllamaBenchmarkAsync()
    {
        await setupPage.SelectOllamaProviderAsync();
        await setupPage.ClearSelectedModelsAsync();

        var visibleModels = await setupPage.GetVisibleOllamaModelsAsync();
        if (visibleModels.Count == 0)
        {
            throw new InvalidOperationException("No visible Ollama models were available for the benchmark demo.");
        }

        var selectedModel = PreferredSmallOllamaModels
            .Concat([preferredOllamaModel])
            .FirstOrDefault(model => visibleModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            ?? visibleModels[0];

        await GuideAsync(page.Locator("section.details-card").Filter(new LocatorFilterOptions { HasText = "Model inventory" }).First, "Ollama inventory", "The Ollama leg starts from the local inventory, loaded models, and session-ready selection controls.");
        await HoldAsync(ProviderSummarySceneMilliseconds);

        await setupPage.SelectOllamaModelAsync(selectedModel);
        await setupPage.WaitForSelectedModelCountAsync(1, TimeSpan.FromMinutes(1));
        await GuideAsync(page.Locator("table.setup-list-table tbody tr").Filter(new LocatorFilterOptions { HasText = selectedModel }).First, "Ollama target", "The demo uses a small local Ollama model so the benchmark finishes quickly while still showing the full results surface.");
        await HoldAsync(SelectionSceneMilliseconds);

        await GuideAsync(setupPage.StartBenchmarkButton, "Start Ollama benchmark", "Launching the run switches the page into live benchmark telemetry with current model, queue progress, cleanup policy, and diagnostics.");
        await HoldAsync(ActionSceneMilliseconds);
        await setupPage.StartBenchmarkAsync();

        await WaitForLiveRailOrSummaryAsync();
        if (await IsVisibleAsync(page.Locator(".benchmark-live-rail")))
        {
            await GuideAsync(page.Locator(".benchmark-live-rail"), "Ollama live benchmark", "While the benchmark is running, the page shows the active model, stage, queue progress, and recent benchmark events.");
            await HoldAsync(LiveRailSceneMilliseconds);

            await GuideAsync(setupPage.ActivityMonitor, "Activity Monitor", "The side activity monitor stays visible during the benchmark so the current local operation is easy to follow.");
            await HoldAsync(MonitorSceneMilliseconds);

            await CompleteBenchmarkAsync();
        }

        return selectedModel;
    }

    private async Task ShowOllamaSummaryAsync(string model)
    {
        await Expect(page.Locator(".benchmark-summary-shell")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 180_000
        });

        await GuideAsync(page.Locator(".benchmark-summary-shell"), "Ollama results", "Finished runs keep a summary shell with top model, usable results, and sortable benchmark rows for the selected provider.");
        await HoldAsync(SummarySceneMilliseconds);

        var resultsTable = page.Locator(".benchmark-summary-shell + table").First;
        await GuideAsync(resultsTable, "Ollama results table", "The benchmark table stays on screen after completion so the recorded demo clearly shows the ranked result layout, even for a single selected model.");
        await HoldAsync(SummarySceneMilliseconds);

        await GuideAsync(page.Locator(".benchmark-summary-shell + table tbody tr").Filter(new LocatorFilterOptions { HasText = model }).First, "Ollama benchmark row", "The result row captures provider, model, overall score, quality, decode speed, status, and fit in one place.");
        await HoldAsync(SelectionSceneMilliseconds);
    }

    private async Task<string> ShowFoundryBenchmarkAsync()
    {
        await setupPage.SelectFoundryProviderAsync();
        await setupPage.ClearSelectedModelsAsync();

        var visibleAliases = await setupPage.GetVisibleAliasesAsync();
        if (visibleAliases.Count == 0)
        {
            throw new InvalidOperationException("No visible Foundry aliases were available for the benchmark demo.");
        }

        var selectedAlias = PreferredSmallFoundryAliases.FirstOrDefault(alias => visibleAliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
            ?? visibleAliases[0];

        await GuideAsync(page.Locator("section.details-card").Filter(new LocatorFilterOptions { HasText = "Windows ML acceleration" }).First.Or(page.Locator("section.details-card").Filter(new LocatorFilterOptions { HasText = "Model inventory" }).First), "Foundry readiness", "The Foundry leg surfaces acceleration readiness, execution-provider state, and the benchmark-ready catalog in the same setup flow.");
        await HoldAsync(ProviderSummarySceneMilliseconds);

        await setupPage.SelectFoundryModelAsync(selectedAlias);
        await setupPage.WaitForSelectedModelCountAsync(1, TimeSpan.FromMinutes(1));
        await GuideAsync(page.Locator("table.setup-list-table tbody tr").Filter(new LocatorFilterOptions { HasText = selectedAlias }).First, "Foundry target", "The Foundry leg uses the same selection and benchmark actions, even though the provider has different runtime diagnostics.");
        await HoldAsync(SelectionSceneMilliseconds);

        await GuideAsync(setupPage.StartBenchmarkButton, "Start Foundry benchmark", "Starting the Foundry run preserves provider-specific diagnostics such as acceleration guidance while the queue is active.");
        await HoldAsync(ActionSceneMilliseconds);
        await setupPage.StartBenchmarkAsync();

        await WaitForLiveRailOrSummaryAsync();
        if (await IsVisibleAsync(page.Locator(".benchmark-live-rail")))
        {
            await GuideAsync(page.Locator(".benchmark-live-rail"), "Foundry live benchmark", "The live rail shows the current benchmark stage, queue progress, cleanup policy, and benchmark events while Foundry is running.");
            await HoldAsync(LiveRailSceneMilliseconds);

            var diagnosticsCard = page.Locator(".benchmark-live-card").Filter(new LocatorFilterOptions { HasText = "Diagnostics" }).First;
            await Expect(diagnosticsCard).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 120_000
            });
            await GuideAsync(diagnosticsCard, "Foundry diagnostics", "Provider-specific diagnostics stay browser-visible, so acceleration issues and runtime guidance are visible during the run instead of only in logs.");
            await HoldAsync(MonitorSceneMilliseconds);

            await CompleteBenchmarkAsync();
        }

        return selectedAlias;
    }

    private async Task ShowFoundrySummaryAsync(string alias)
    {
        await Expect(page.Locator(".benchmark-summary-shell")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 240_000
        });

        await GuideAsync(page.Locator(".benchmark-summary-shell"), "Foundry results", "The Foundry leg finishes on the same benchmark summary shell, so both local providers are demonstrated through the same workflow.");
        await HoldAsync(SummarySceneMilliseconds);

        var resultsTable = page.Locator(".benchmark-summary-shell + table").First;
        await GuideAsync(resultsTable, "Foundry results table", "The recording also holds on the Foundry results table so the final video covers one usable small model from each provider.");
        await HoldAsync(SummarySceneMilliseconds);

        await GuideAsync(page.Locator(".benchmark-summary-shell + table tbody tr").Filter(new LocatorFilterOptions { HasText = alias }).First, "Foundry benchmark row", "The Foundry result row keeps the provider-specific score, quality, speed, status, and fit details browser-visible at the end of the demo.");
        await HoldAsync(SelectionSceneMilliseconds);
    }

    private async Task ShowClosingSceneAsync()
    {
        await GuideAsync(page.Locator(".benchmark-summary-shell").First, "Combined local benchmark story", "The recording closes on the shared benchmark results surface after one small Ollama model and one small Foundry model finish through the same benchmark flow.");
        await HoldAsync(FinalSceneMilliseconds);
    }

    private async Task WaitForLiveRailOrSummaryAsync()
    {
        await page.WaitForFunctionAsync(
            "() => Boolean(document.querySelector('.benchmark-live-rail')) || Boolean(document.querySelector('.benchmark-summary-shell'))",
            null,
            new PageWaitForFunctionOptions
            {
                Timeout = 180_000
            });
    }

    private async Task CompleteBenchmarkAsync()
    {
        if (await IsVisibleAsync(setupPage.CancelBenchmarkButton))
        {
            await setupPage.CancelBenchmarkAsync();
        }

        await setupPage.WaitForBenchmarkCompletionAsync();
    }

    private static async Task<bool> IsVisibleAsync(ILocator locator)
    {
        if (await locator.CountAsync() == 0)
        {
            return false;
        }

        return await locator.First.IsVisibleAsync();
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

                const overlayId = 'li-cv-benchmark-demo-overlay';
                let overlay = document.getElementById(overlayId);
                if (!overlay) {
                    overlay = document.createElement('div');
                    overlay.id = overlayId;
                    overlay.innerHTML = '<div data-guide-box></div><div data-guide-card><strong></strong><span></span><i></i></div>';
                    document.body.appendChild(overlay);
                }

                let style = document.getElementById('li-cv-benchmark-demo-style');
                if (!style) {
                    style = document.createElement('style');
                    style.id = 'li-cv-benchmark-demo-style';
                    style.textContent = `
                        #li-cv-benchmark-demo-overlay { position: fixed; inset: 0; z-index: 2147483000; pointer-events: none; font-family: "Segoe UI", Arial, sans-serif; }
                        #li-cv-benchmark-demo-overlay [data-guide-box] { position: fixed; border: 3px solid #0f766e; border-radius: 12px; box-shadow: 0 0 0 9999px rgba(10, 18, 30, 0.08), 0 0 28px rgba(15, 118, 110, 0.42); transition: all 220ms ease; }
                        #li-cv-benchmark-demo-overlay [data-guide-card] { position: fixed; max-width: 360px; padding: 12px 14px 13px; border-radius: 8px; background: rgba(18, 24, 32, 0.95); color: #fff; box-shadow: 0 16px 40px rgba(0,0,0,0.25); overflow: hidden; transition: all 220ms ease; }
                        #li-cv-benchmark-demo-overlay strong { display: block; margin-bottom: 4px; font-size: 15px; line-height: 1.25; }
                        #li-cv-benchmark-demo-overlay span { display: block; font-size: 12px; line-height: 1.4; color: rgba(255,255,255,0.84); }
                        #li-cv-benchmark-demo-overlay i { position: absolute; left: -45%; bottom: 0; width: 45%; height: 3px; background: linear-gradient(90deg, transparent, #5eead4, transparent); animation: liCvBenchmarkGuideSweep 1.1s linear infinite; }
                        @keyframes liCvBenchmarkGuideSweep { from { transform: translateX(0); } to { transform: translateX(320%); } }
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
    }

    private Task HideGuideAsync()
        => page.EvaluateAsync("() => document.getElementById('li-cv-benchmark-demo-overlay')?.remove()");

    private Task HoldAsync(int milliseconds)
        => page.WaitForTimeoutAsync(milliseconds);
}