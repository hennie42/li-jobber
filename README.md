# LI CV Writer

Local-first Blazor application for importing LinkedIn profile data, analyzing job postings, reviewing job fit, selecting supporting evidence, and producing targeted CV material for senior consulting roles.

## Current status

This repository has an initial implementation scaffold with:

- a Blazor Web App host
- domain, application, infrastructure, and test projects
- a LinkedIn DMA member snapshot importer that stages data into the existing profile-mapping pipeline
- an Ollama client targeting `http://localhost:11434/api`
- structured job-fit review with apply/stretch/skip scoring
- applicant differentiator capture on Start / Setup
- optional Insights Discovery PDF upload that drafts applicant differentiators in memory with the session LLM
- ranked evidence selection per job tab to steer generation

## Prerequisites

- .NET 10 SDK
- Ollama running locally at `http://localhost:11434`
- local model `nemotron-cascade-2:latest`
- a LinkedIn DMA portability access token with `r_dma_portability_self_serve`

## Planned secrets

The application is designed so that secrets do not live in source-controlled files.

- DMA access token: pasted at runtime for a single import and not persisted by the app
- Ollama base URL and model: regular configuration, not secrets
- Insights Discovery PDFs and extracted source text: processed in memory only and not persisted by the app

## Current workflow

1. Check Ollama and choose the session model and thinking level.
2. Load profile data from the LinkedIn DMA member snapshot API with a portability token.
3. Optionally upload an Insights Discovery PDF to auto-draft applicant differentiators, or capture them manually on Start / Setup.
4. Open the Job Workbench and analyze a target role.
5. Review the structured fit assessment and ranked evidence for the active job tab.
6. Generate CV, cover letter, profile summary, and interview notes using the selected evidence.

For LinkedIn DMA portability imports, the app accepts a static access token with `r_dma_portability_self_serve` and queries the member snapshot APIs to synthesize the candidate-profile shape used across the app.

## Running locally

Use the standard development host:

```powershell
dotnet run --project .\src\LiCvWriter.Web\LiCvWriter.Web.csproj
```

## GitHub safety check

Before the first push, or after a large local build/test run, verify that only intended source files are indexed:

```powershell
pwsh .\scripts\Verify-GitHubPushSafety.ps1
```

The script audits tracked and staged files only. It fails if generated build output paths are indexed or if the indexed content appears to contain machine-specific paths, private-key material, or common secret formats.

## Notes

The app expects Ollama to be reachable at the configured base URL before generation and fit-analysis features will succeed.
Applicant differentiator field values are persisted through workspace recovery, but uploaded Insights Discovery PDFs and extracted source text are not.
