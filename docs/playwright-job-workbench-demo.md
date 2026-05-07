# Playwright Job Workbench Demo

This guided walkthrough was generated from a local Playwright session against the LI-CV-Writer web app. The session seeds three ready job sets, selects them, starts the batch run, and records live LLM activity in the workbench monitors.

Company names in screenshots and video are blurred by the Playwright masking helper before media is captured. Review the video before sharing outside the local workspace so privacy masking is confirmed across the full recording.

## Walkthrough

1. Open the Job Workbench with a loaded profile, a live Ollama model, and three ready job sets.
2. Review each job-set row while the company-name masking remains visible.
3. Select the three job sets in the batch list.
4. Click Start selected.
5. Watch the batch label, job-set status chips, Status Monitor, Reasoning Monitor, and Activity feed update while the LLM operation runs.

![screenshot of the Job Workbench showing three ready job sets with company names blurred](assets/playwright-job-workbench-demo/01-ready-jobsets.png)

![screenshot of the Job Workbench with all three batch checkboxes selected and company names blurred](assets/playwright-job-workbench-demo/02-three-jobsets-selected.png)

![screenshot of the running batch with the Status Monitor and job-set labels updating while company names are blurred](assets/playwright-job-workbench-demo/03-live-llm-progress.png)

![screenshot of the Job Workbench after live LLM output appears in the Status Monitor and workbench labels update with company names blurred](assets/playwright-job-workbench-demo/04-workbench-labels-updated.png)

## Video

The guided recording is tracked in the repository as [the Job Workbench WebM walkthrough](assets/playwright-job-workbench-demo/job-workbench-demo.webm), so it is available from the online repo as well as the local workspace.

A local copy of the WebM and the diagnostic Playwright trace are also written under `artifacts/playwright/` when the demo is regenerated.

## Video Transcript

1. The recording opens on the seeded Job Workbench and reviews the three ready job sets with company names blurred.
2. Each job set is selected one at a time so the batch queue state is visible.
3. The Start selected command is highlighted before the batch begins.
4. The recording holds on the running batch label, Status Monitor, Reasoning Monitor, and Activity feed while live LLM output appears.
5. The walkthrough returns to the workbench rows to show the updated labels and status chips.

## Validation

The artifact validator checks that all expected screenshots exist, PNG dimensions are plausible, markdown links resolve, the tracked WebM has an EBML header, the WebM is larger than 5 MB, and the trace archive contains trace and resource entries.
