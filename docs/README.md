# Documentation Guide

The root README is the friendly front desk.

This folder is where the wiring diagrams, prompt inventories, design notes, and "why is it built like that?" answers live.

## Start Here

- [details.md](details.md) — the main technical reference for architecture, workflows, session state, rendering, exports, telemetry, and implementation boundaries
- [llm-prompt-inventory.md](llm-prompt-inventory.md) — production prompt inventory, risk ratings, coverage status, and prompt-hardening next steps
- [llm-local-prompt-evals.md](llm-local-prompt-evals.md) — local prompt-eval scaffold, fixture catalog, and deterministic scoring approach
- [llm-cv-chunking-design.md](llm-cv-chunking-design.md) — outline-first CV generation design and planned evolution of the CV pipeline
- [playwright-job-workbench-demo.md](playwright-job-workbench-demo.md) — guided local Playwright walkthrough for the Job Workbench batch run, with screenshots, a tracked WebM recording, and trace validation notes

## Read This If You Want To...

| Goal | Read |
| --- | --- |
| Understand what the app does without reading implementation details | [the root README](../README.md) |
| Understand the full app architecture and workflow | [details.md](details.md) |
| Audit prompt surfaces and grounding rules | [llm-prompt-inventory.md](llm-prompt-inventory.md) |
| Understand the local prompt-eval approach | [llm-local-prompt-evals.md](llm-local-prompt-evals.md) |
| Follow planned CV-generation improvements | [llm-cv-chunking-design.md](llm-cv-chunking-design.md) |
| Run or review the Job Workbench E2E demo | [playwright-job-workbench-demo.md](playwright-job-workbench-demo.md) |

## Contributor Notes

- Start with [details.md](details.md) if you are changing behavior rather than just wording.
- If you change prompt behavior, update both [llm-prompt-inventory.md](llm-prompt-inventory.md) and [llm-local-prompt-evals.md](llm-local-prompt-evals.md).
- Live Playwright E2E tests are opt-in. Install browsers, start Ollama, then set `LICVWRITER_RUN_PLAYWRIGHT_E2E=1` before running the E2E subset. Set `LICVWRITER_PLAYWRIGHT_WRITE_DEMO=1` to regenerate the guided screenshots, tracked WebM recording, local WebM copy, and Playwright trace; the demo validator checks the WebM header, a size larger than 5 MB, markdown links, screenshots, and trace contents.
- Before pushing, run `pwsh .\scripts\Verify-GitHubPushSafety.ps1` from the repo root.

## Short Version

If you came here looking for a quick product overview, retreat to [the root README](../README.md).

If you came here looking for diagrams, prompt catalogs, or implementation boundaries, you are in the correct cave.