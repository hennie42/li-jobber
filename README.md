# LI CV Writer

Local-first AI-powered CV generation from LinkedIn data.

## Overview

LI CV Writer is a local-first Blazor application that imports your LinkedIn profile through the DMA Portability API (or a CSV data export), analyzes target job postings, reviews candidate-job fit, ranks supporting evidence, and generates tailored application documents — all running on your machine with a local LLM via Ollama.

The application walks a senior professional through a structured pipeline: establish a session with a local model, import the full LinkedIn member snapshot, optionally capture applicant differentiators (manually or from an Insights Discovery PDF), then open parallel job tabs where each target role gets its own research, fit review, evidence ranking, technology gap analysis, and multi-document generation. LLM-backed job-context analysis, fit enhancement, technology-gap analysis, refresh-all, and draft generation stream progress into the UI through a brokered SSE pipeline, with persistent sidebar reasoning and status monitors. The output is a set of ATS-friendly Word (.docx) and Markdown files ready for direct submission.

No data leaves your machine. The LinkedIn token is used once for import and discarded, the LLM runs locally through Ollama, and all generated files are written to a local export folder. The application is designed as a productivity tool for senior consulting and technology professionals targeting roles where evidence-grounded, keyword-optimized application material makes a measurable difference.

## Tech Stack

| Layer | Technology |
| --- | --- |
| Runtime | .NET 10 / C# 14 |
| Web framework | Blazor Server (interactive SSR) |
| LLM inference | Ollama (local, `http://localhost:11434`) |
| Architecture | Domain-Driven Design — Core → Application → Infrastructure → Web |
| Profile import | LinkedIn DMA Portability API (`r_dma_portability_self_serve`) or CSV data export |
| Word export | Embedded .dotx template with OpenXml content controls, Markdig, HtmlToOpenXml — built-in heading styles, Calibri, single-column ATS layout |
| Testing | xUnit (206 tests) |

## Key Features

- **LinkedIn DMA member snapshot import** — typed mapping of experience, education, skills, certifications, projects, recommendations, and enrichment domains; CSV data export import also supported
- **LLM-powered job analysis** — structured parsing of job postings and company context pages
- **Fit review with optional LLM enhancement** — deterministic apply / stretch / skip scoring with semantic enhancement when requested; fingerprint-based caching skips redundant recalculations
- **Ranked evidence selection** — interactive multi-criteria evidence ranking by requirement alignment, differentiator alignment, and recommendation strength
- **Technology gap analysis** — surfaces underrepresented technologies from the target role
- **Two-pass CV generation** — first pass generates experience highlights, refinement pass fills must-have theme gaps with grounded evidence, and CV output is kept to a maximum of four pages without recommendation quotes
- **Template-based Word export** — embedded .dotx template with named content controls, ATS-friendly unwrapping, hyperlink flattening, and underline stripping
- **Early career separation** — roles ending before 2009 rendered in a compact "Early Career" section; modern roles get full detail with achievement bullets
- **Umbrella role detection** — consulting roles covering 3+ projects shown with client sub-items inline
- **Multi-document generation** — CV, cover letter, profile summary, recommendations, and interview questions per job tab
- **Output language selection** — English or Danish per job tab, with automatic recommendation translation annotation
- **Applicant differentiator profiling** — manual capture or automated drafting from Insights Discovery PDF
- **Brokered streaming UI** — SSE-backed operations keep the workbench responsive while reasoning and status stream into dual retro CRT sidebar monitors
- **Repetition loop detection** — automatically aborts LLM streaming when thinking or content output enters a degenerate repetition cycle
- **Session model control** — change model and thinking level at any time; rerun completed analysis or draft generation with the new settings
- **Refresh-all orchestration** — single-button re-analysis of job context, fit review, and technology gaps with shared progress streaming
- **Workspace recovery** — full session state persists across restarts via JSON snapshot

## Workflow

```mermaid
graph TD
    A[Start / Setup] --> B[Check Ollama and load models]
    B --> C[Choose session model and thinking level]
    C --> D[Load LinkedIn DMA member snapshot]
    D --> E[Optional applicant differentiators]
    E --> F[Open Job Workbench]
    F --> G[Analyze job and build company context]
    G --> H[Fit review, evidence ranking, and optional LLM enhancement]
    H --> I[Optional technology gap check]
    I --> J[Generate and export Word + Markdown drafts]
    J --> K[Two-pass refinement for must-have theme coverage]
    F --> L[Refresh All — rerun analysis pipeline in one step]
```

## Prerequisites

- .NET 10 SDK
- Ollama running locally at `http://localhost:11434`
- At least one locally installed Ollama model
- A LinkedIn DMA portability access token with `r_dma_portability_self_serve` — see the [LinkedIn DMA docs](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/member-data-portability-member/?view=li-dma-data-portability-2025-11) — or a LinkedIn CSV data export

## Running Locally

```powershell
dotnet run --project .\src\LiCvWriter.Web\LiCvWriter.Web.csproj
```

## Secrets

No secrets live in source-controlled files.

- **DMA access token** — pasted at runtime for a single import, not persisted
- **Ollama base URL and model** — regular configuration, not secrets
- **Insights Discovery PDFs** — processed in memory only, not persisted

## GitHub Safety Check

Before pushing, verify that only intended source files are indexed:

```powershell
pwsh .\scripts\Verify-GitHubPushSafety.ps1
```

The script audits tracked and staged files and fails on build output, machine-specific paths, or secret-like content.

## Detailed Walkthrough

For the full architecture reference, data flows, Mermaid diagrams, and implementation details, see [docs/details.md](docs/details.md).

## License

See [LICENSE](LICENSE).
