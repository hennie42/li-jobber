# Documentation Guide

The root README is the friendly front desk.

This folder is where the wiring diagrams, prompt inventories, design notes, and "why is it built like that?" answers live.

## Start Here

- [details.md](details.md) — the main technical reference for architecture, workflows, session state, rendering, exports, telemetry, and implementation boundaries
- [llm-prompt-inventory.md](llm-prompt-inventory.md) — production prompt inventory, risk ratings, coverage status, and prompt-hardening next steps
- [llm-local-prompt-evals.md](llm-local-prompt-evals.md) — local prompt-eval scaffold, fixture catalog, and deterministic scoring approach
- [llm-cv-chunking-design.md](llm-cv-chunking-design.md) — outline-first CV generation design and planned evolution of the CV pipeline
- [playwright-demo.html](https://hennie42.github.io/li-jobber/playwright-demo.html) — browser-playable full-app Playwright E2E demo video
- [playwright-job-workbench-demo.md](playwright-job-workbench-demo.md) — generated screenshots, transcript, tracked WebM link, and Playwright artifact validation notes

## Read This If You Want To...

| Goal | Read |
| --- | --- |
| Understand what the app does without reading implementation details | [the root README](../README.md) |
| Understand the full app architecture and workflow | [details.md](details.md) |
| Audit prompt surfaces and grounding rules | [llm-prompt-inventory.md](llm-prompt-inventory.md) |
| Understand the local prompt-eval approach | [llm-local-prompt-evals.md](llm-local-prompt-evals.md) |
| Follow planned CV-generation improvements | [llm-cv-chunking-design.md](llm-cv-chunking-design.md) |
| Watch the full app E2E demo | [playwright-demo.html](https://hennie42.github.io/li-jobber/playwright-demo.html) |
| Review generated demo screenshots and validation notes | [playwright-job-workbench-demo.md](playwright-job-workbench-demo.md) |

## Contributor Notes

- Start with [details.md](details.md) if you are changing behavior rather than just wording.
- If you change prompt behavior, update both [llm-prompt-inventory.md](llm-prompt-inventory.md) and [llm-local-prompt-evals.md](llm-local-prompt-evals.md).
- Live Playwright E2E tests are opt-in. Install browsers, start Ollama, then set `LICVWRITER_RUN_PLAYWRIGHT_E2E=1` before running the E2E subset. Set `LICVWRITER_PLAYWRIGHT_WRITE_DEMO=1` to regenerate the guided screenshots, local WebM copy, and Playwright trace; set `LICVWRITER_PLAYWRIGHT_WRITE_FULL_DEMO=1` to regenerate the full-app video and copy it into the tracked docs asset. The demo validators check WebM headers, size thresholds, markdown links, screenshots, and trace contents.
- Before pushing, run `pwsh .\scripts\Verify-GitHubPushSafety.ps1` from the repo root.

## Short Version

If you came here looking for a quick product overview, retreat to [the root README](../README.md).

If you came here looking for diagrams, prompt catalogs, or implementation boundaries, you are in the correct cave.