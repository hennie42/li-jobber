# Playwright Benchmark Demo Assets

These benchmark demo assets are generated from local Playwright sessions against the Setup / LLM benchmark surface. The current walkthrough focuses on a compact dual-provider story: one usable small Ollama model, one usable small Foundry Local model, live benchmark telemetry for both, and a held final state so the finished Foundry results stay visible at the end.

The current benchmark harness prefers `codellama:7b-instruct` on the Ollama side and `phi-4-mini` on the Foundry side when those models are visible. If either is missing, the script falls back to the next visible preferred small model for that provider.

## What The Recording Shows

1. Open Setup / LLM on the shared benchmark workspace.
2. Switch to Ollama and select one small local model.
3. Start the Ollama benchmark and hold on live rail plus activity telemetry.
4. Show the completed Ollama summary shell, results table, and result row.
5. Switch to Foundry Local and select one small catalog model.
6. Start the Foundry benchmark and hold on the live rail plus diagnostics card.
7. Show the completed Foundry summary shell, results table, and result row.
8. Hold the finished benchmark state for an extra 30 seconds so the Foundry result remains readable at the end.

## Tracked Media

- [Benchmark demo WebM](assets/playwright-benchmark-demo/benchmark-demo.webm) — tracked benchmark showcase in repository-friendly WebM format
- [LinkedIn MP4](assets/playwright-benchmark-demo/benchmark-demo-linkedin.mp4) — silent H.264 MP4 export for general sharing
- [LinkedIn 1080p MP4](assets/playwright-benchmark-demo/benchmark-demo-linkedin-1080p.mp4) — silent padded 1920x1080 export for LinkedIn upload

The current tracked WebM is `177.52` seconds long. That includes the extra 30-second end hold on the completed Foundry benchmark state.

## Local Regeneration Notes

The benchmark recording is gated behind live Playwright flags because it depends on local LLM providers being available.

1. Start Ollama with at least one installed local model.
2. Start Foundry Local with at least one usable catalog model.
3. Set `LICVWRITER_RUN_PLAYWRIGHT_E2E=1`.
4. Set `LICVWRITER_PLAYWRIGHT_WRITE_BENCHMARK_DEMO=1`.
5. Run `dotnet test .\tests\LiCvWriter.Tests\LiCvWriter.Tests.csproj --filter BenchmarkDemoE2ETests`.

If Playwright writes a valid raw WebM but the test host does not promote it automatically, the latest raw file under `artifacts/playwright/videos/` can be validated with `ffprobe` and copied into the tracked asset folder manually.

## Derived Exports

The tracked WebM is the source of truth for the published benchmark media. Additional deliverables are generated with `ffmpeg`:

- lossless WebM remux under `artifacts/playwright/benchmark-demo-lossless.webm`
- silent MP4 copy under `artifacts/playwright/benchmark-demo-linkedin.mp4`
- silent padded 1080p MP4 copy under `artifacts/playwright/benchmark-demo-linkedin-1080p.mp4`

The LinkedIn variants intentionally remove audio and keep the benchmark UI readable during the final Foundry completion hold.

## Validation

The benchmark media flow has been validated with these checks:

- the tracked WebM has a valid EBML header
- the promoted WebM probes as VP8 at `1440x1000` and `25 fps`
- the current promoted WebM duration is `177.520000` seconds
- the benchmark walkthrough code now includes one Ollama model, one Foundry model, completed summaries for both, and the extended final hold
- the silent LinkedIn MP4 variants were regenerated from the same tracked WebM source after the final benchmark update