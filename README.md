# LI CV Writer

LI CV Writer turns your LinkedIn data, a target job ad, and a local LLM into tailored application documents.

It runs on your machine, which is helpful because your career history has already been through enough.

## What It Does

- imports your LinkedIn profile from the DMA Portability API or a CSV export
- lets you work on multiple target roles in parallel as separate Job sets
- analyzes job postings and company context
- reviews candidate-job fit and supporting evidence
- generates a CV, cover letter, profile summary, recommendation brief, and interview questions
- exports Markdown and Word files without sending your life story to a cloud service

## See It In Action

Watch the [full Playwright E2E demo](https://hennie42.github.io/li-jobber/playwright-demo.html) for a two-minute walkthrough of setup, profile review, discovery, job-set analysis, fit review, ranked evidence, draft generation, exports, and live batch activity.

The player source lives at [docs/playwright-demo.html](docs/playwright-demo.html). GitHub Pages should be enabled from the `docs/` folder for browser playback from the online repo.

## Why It Exists

Applying for jobs is repetitive.

Rewriting the same achievements for slightly different roles is repetitive.

Pretending this is your favorite part of the week is also repetitive.

LI CV Writer gives the repetitive part to a local model while keeping the review, editing, and final judgment with you.

## Privacy First

- your profile data stays local
- job research stays local
- Ollama runs locally
- generated files are written locally

In short: no cloud confessional booth required.

## Quick Start

1. Install the .NET 10 SDK.
2. Install Ollama and pull at least one local model.
3. Run:

```powershell
dotnet run --project .\src\LiCvWriter.Web\LiCvWriter.Web.csproj
```

4. Open the app.
5. Load your LinkedIn data.
6. Open Job Workbench and let the robot tackle the paperwork it was clearly born for.

## What You Need

- .NET 10 SDK
- Ollama running at `http://localhost:11434`
- at least one local Ollama model
- a LinkedIn DMA portability token with `r_dma_portability_self_serve`, or a LinkedIn CSV export

## Where The Technical Details Live

This README is the lobby.

The wiring diagrams, architecture notes, prompt inventory, evaluation scaffolding, and other engineer-facing cave systems live in [docs/README.md](docs/README.md).

## License

See [LICENSE](LICENSE).
