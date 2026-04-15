# LI CV Writer — Technical Reference

This document is the maintainer, contributor, and evaluator reference for how LI CV Writer works. It covers architecture, data flows, session state, every major pipeline, and the design decisions behind deterministic and LLM-backed behavior.

For the user-facing summary, see [README.md](../README.md).

---

## 1. Architecture Overview

The solution follows a Domain-Driven Design layering where each project has a single responsibility direction and dependencies flow inward.

```mermaid
graph TB
    Web["Web (Blazor Server)"] --> Application
    Web --> Infrastructure
    Application --> Core
    Infrastructure --> Application
    Infrastructure --> Core
```

### Project Responsibilities

| Project | Responsibility |
| --- | --- |
| **LiCvWriter.Core** | Domain records: profiles, jobs, documents, auditing. No behavior, no dependencies. |
| **LiCvWriter.Application** | Abstractions (interfaces), deterministic services (fit scoring, evidence ranking, profile merging), models, and options. References Core only. |
| **LiCvWriter.Infrastructure** | Integrations: Ollama LLM client, LinkedIn DMA import, HTTP research, document rendering, Word/Markdown export, audit storage, workspace recovery. References Application and Core. |
| **LiCvWriter.Web** | Blazor Server host: pages, session state, DI registration, operation status. References all layers. |
| **LiCvWriter.Tests** | xUnit test suite covering application services, infrastructure services, and web components. |

### Primary Files

| Concern | Primary files | Purpose |
| --- | --- | --- |
| Bootstrap | [Program.cs](../src/LiCvWriter.Web/Program.cs), [App.razor](../src/LiCvWriter.Web/Components/App.razor), [Routes.razor](../src/LiCvWriter.Web/Components/Routes.razor) | DI, routing, HTTP client configuration |
| Shell | [MainLayout.razor](../src/LiCvWriter.Web/Components/Layout/MainLayout.razor), [NavMenu.razor](../src/LiCvWriter.Web/Components/Layout/NavMenu.razor) | Floating navigation, dual CRT monitors, completed-activity sidebar |
| Streaming transport | [Program.cs](../src/LiCvWriter.Web/Program.cs), [LlmOperationBroker.cs](../src/LiCvWriter.Web/Services/LlmOperationBroker.cs), [llm-stream.js](../src/LiCvWriter.Web/wwwroot/llm-stream.js) | Start/status/events/cancel endpoints, per-job-tab operation broker, browser `EventSource` bridge |
| Session state | [WorkspaceSession.cs](../src/LiCvWriter.Web/Services/WorkspaceSession.cs), [WorkspaceRecoveryStore.cs](../src/LiCvWriter.Web/Services/WorkspaceRecoveryStore.cs) | In-memory state container, recovery persistence |
| Setup flow | [Home.razor](../src/LiCvWriter.Web/Components/Pages/Home.razor) | Ollama check, model selection, DMA import, differentiators |
| Workbench flow | [JobWorkbench.razor](../src/LiCvWriter.Web/Components/Pages/Workspace/JobWorkbench.razor) | Brokered job research, fit review, evidence, technology gap, refresh-all, generation |
| LinkedIn import | [LinkedInMemberSnapshotImporter.cs](../src/LiCvWriter.Infrastructure/LinkedIn/LinkedInMemberSnapshotImporter.cs), [LinkedInExportImporter.cs](../src/LiCvWriter.Infrastructure/LinkedIn/LinkedInExportImporter.cs) | DMA fetch, domain routing, CSV staging, profile assembly |
| Deterministic scoring | [JobFitAnalysisService.cs](../src/LiCvWriter.Application/Services/JobFitAnalysisService.cs), [EvidenceSelectionService.cs](../src/LiCvWriter.Application/Services/EvidenceSelectionService.cs), [CandidateEvidenceService.cs](../src/LiCvWriter.Application/Services/CandidateEvidenceService.cs) | Fit assessment, evidence ranking, evidence cataloguing |
| LLM research | [HttpJobResearchService.cs](../src/LiCvWriter.Infrastructure/Research/HttpJobResearchService.cs), [LlmTechnologyGapAnalysisService.cs](../src/LiCvWriter.Web/Services/LlmTechnologyGapAnalysisService.cs), [LlmFitEnhancementService.cs](../src/LiCvWriter.Infrastructure/Workflows/LlmFitEnhancementService.cs) | Structured job/company parsing, technology gap analysis, semantic fit enhancement |
| Generation | [DraftGenerationService.cs](../src/LiCvWriter.Infrastructure/Workflows/DraftGenerationService.cs) | Orchestrates LLM call → render → export → audit per document kind |
| Document rendering | [MarkdownDocumentRenderer.cs](../src/LiCvWriter.Infrastructure/Documents/MarkdownDocumentRenderer.cs) | Shapes LLM output into structured Markdown with ATS sections |
| Document export | [LocalDocumentExportService.cs](../src/LiCvWriter.Infrastructure/Documents/LocalDocumentExportService.cs) | Writes .md and .docx files via Markdig + HtmlToOpenXml pipeline |
| Diagnostics | [SessionDiagnostics.razor](../src/LiCvWriter.Web/Components/Pages/Diagnostics/SessionDiagnostics.razor), [OperationStatusService.cs](../src/LiCvWriter.Web/Services/OperationStatusService.cs) | Sidebar telemetry feeds, import diagnostics, session inspection |

---

## 2. System Map

```mermaid
graph TB
    A[App.razor] --> B[Routes.razor]
    B --> C[MainLayout.razor]
    C --> D[Start / Setup]
    C --> E[Job Workbench]
    C --> F[Session Diagnostics]
    D --> G[WorkspaceSession]
    E --> G
    F --> G
    D --> H[OperationStatusService]
    E --> H
    F --> H
```

The three pages share `WorkspaceSession` for state and `OperationStatusService` for activity telemetry. `MainLayout.razor` subscribes to `OperationStatusService` to keep the floating navigation, reasoning monitor, status monitor, and completed-activity list in sync while long-running work streams in.

---

## 3. Domain Records

### Core/Profiles

| Record | Fields |
| --- | --- |
| `CandidateProfile` | Name, Headline, Summary, Location, Industry, PublicProfileUrl, PrimaryEmail, Experience, Education, Skills, Certifications, Projects, Recommendations, ManualSignals |
| `ExperienceEntry` | CompanyName, Title, Description, Location, Period, Highlights |
| `ProjectEntry` | Title, Description, Url, Period |
| `RecommendationEntry` | Author (PersonName), Company, JobTitle, Text, VisibilityStatus, CreatedOn |
| `CertificationEntry` | Name, Authority, Url, Period, LicenseNumber |
| `EducationEntry` | SchoolName, DegreeName, Notes, Activities, Period |
| `ApplicantDifferentiatorProfile` | WorkStyle, CommunicationStyle, LeadershipStyle, StakeholderStyle, Motivators, TargetNarrative, Watchouts, AboutApplicantBasis |
| `EvidenceSelectionResult` | RankedEvidence, SelectedEvidence |

### Core/Jobs

| Record | Fields |
| --- | --- |
| `JobPostingAnalysis` | SourceUrl, RoleTitle, CompanyName, Summary, MustHaveThemes, NiceToHaveThemes, CulturalSignals, Signals |
| `CompanyResearchProfile` | Name, Summary, SourceUrls, GuidingPrinciples, CulturalSignals, Differentiators, Signals |
| `JobFitAssessment` | OverallScore, Recommendation, Requirements, Strengths, Gaps |
| `TechnologyGapAssessment` | DetectedTechnologies, PossiblyUnderrepresentedTechnologies |

### Core/Documents

| Record | Fields |
| --- | --- |
| `DocumentKind` | Cv, CoverLetter, ProfileSummary, InterviewNotes |
| `GeneratedDocument` | Kind, Title, Markdown, PlainText, GeneratedAtUtc, OutputPath, LlmDuration, PromptTokens, CompletionTokens, Model |

---

## 4. Session State and Recovery

`WorkspaceSession` is the main in-memory state container. It splits into session-global state and per-job-tab state and raises a `Changed` event for page rerendering.

### State Ownership

| Scope | Container | Examples |
| --- | --- | --- |
| Session-global | `WorkspaceSession` | `CandidateProfile`, `ApplicantDifferentiatorProfile`, `OllamaAvailability`, `SelectedLlmModel`, `SelectedThinkingLevel`, `HasStartedLlmWork`, `LinkedInAuthorizationStatus` |
| Job-tab-local | `JobSetSessionState` | `JobPosting`, `CompanyProfile`, `JobFitAssessment`, `EvidenceSelection`, `TechnologyGapAssessment`, `GeneratedDocuments`, `Exports`, `SelectedEvidenceIds`, `OutputLanguage` |
| Recovery | `WorkspaceRecoveryStore` | Active tab, job-tab inputs, applicant differentiators, selected evidence IDs, output folders |

### Workspace Lifecycle

```mermaid
stateDiagram-v2
    [*] --> SessionCreated
    SessionCreated --> SetupPending: WorkspaceSession constructed
    SetupPending --> OllamaChecked: SetOllamaAvailability
    OllamaChecked --> SessionConfigured: SetLlmSessionSettings
    SessionConfigured --> ProfileImported: SetImportResult
    ProfileImported --> JobContextLoaded: SetJobPosting / SetCompanyProfile
    JobContextLoaded --> FitReviewed: Refresh fit review
    FitReviewed --> TechGapChecked: Refresh technology gap
    FitReviewed --> DraftsGenerated: Generate and export drafts
    TechGapChecked --> DraftsGenerated
```

### State Invalidation Rules

These rules prevent stale outputs by clearing downstream results when upstream inputs change.

| Action | Scope | Impact |
| --- | --- | --- |
| `SetImportResult()` | All job tabs | Replaces `CandidateProfile`, stores `ImportResult`, clears generated artifacts, fit assessments, technology gaps, and evidence selections for all tabs |
| `SetApplicantDifferentiatorProfile()` | All job tabs | Stores differentiator profile, clears all fit assessments, clears evidence selections (preserves selected IDs for reranking) |
| `SetJobPosting()` | Active tab | Replaces job posting, resets fit review, technology gap, evidence, progress, generated docs, exports |
| `SetCompanyProfile()` | Active tab | Replaces company profile, resets fit review, technology gap, evidence, progress, generated docs, exports |
| `SetOllamaAvailability()` | Session-global | Updates model availability, clears `IsLlmSessionConfigured` if selected model is no longer available |
| `MarkLlmWorkStarted()` | Session-global | Records that the session has performed LLM-backed work so the setup UI can warn that later model/thinking changes affect only future operations |
| `SetGeneratedDocuments()` | Active tab | Marks tab done, stores generated documents and file exports |

---

## 5. Start / Setup Flow

Implemented in [Home.razor](../src/LiCvWriter.Web/Components/Pages/Home.razor). Combines three setup steps with shared status messaging.

### Sequence

```mermaid
sequenceDiagram
    actor User
    participant Home as Home.razor
    participant Llm as ILlmClient
    participant Importer as ILinkedInExportImporter
    participant Drafting as InsightsDiscoveryDraftingService
    participant Workspace as WorkspaceSession

    User->>Home: Check Ollama and load models
    Home->>Llm: VerifyModelAvailabilityAsync()
    Llm-->>Home: OllamaModelAvailability
    Home->>Workspace: SetOllamaAvailability()

    User->>Home: Use session LLM settings
    Home->>Workspace: SetLlmSessionSettings()

    User->>Home: Load DMA member snapshot
    Home->>Importer: ImportMemberSnapshotAsync(token)
    Importer-->>Home: LinkedInExportImportResult
    Home->>Workspace: SetImportResult()

    User->>Home: Save differentiators or upload PDF
    alt PDF upload path
        Home->>Workspace: MarkLlmWorkStarted()
        Home->>Drafting: DraftAsync()
    end
    Home->>Workspace: SetApplicantDifferentiatorProfile()
```

### Step 1: Ollama and Session Model

The page checks Ollama through `ILlmClient.VerifyModelAvailabilityAsync()`. The returned `OllamaModelAvailability` determines which models the user can select. The model and thinking level are session-scoped and remain editable throughout the session. After LLM-backed work begins, the setup page warns that later changes apply to future operations only, so completed analyses or generated drafts need to be rerun if the user wants them refreshed with the new settings.

The panel is collapsible — after `UseSessionLlmSettingsAsync()`, the controls collapse into a compact shell. Clicking `CheckOllamaAsync()` expands them again and refreshes model availability.

### Step 2: LinkedIn DMA Import

Takes a runtime DMA portability token, calls the LinkedIn importer pipeline, updates `WorkspaceSession.ImportResult`, and makes the `CandidateProfile` available to all downstream flows.

### Step 3: Applicant Differentiators

Optional session-global notes (work style, communication, leadership, stakeholders, motivators, target narrative, watchouts, proof points). The manual path is immediate. The PDF path sends extracted text through the session model and calls `MarkLlmWorkStarted()`, which records that the session has used LLM-backed work but does not prevent later model/thinking changes.

---

## 6. LinkedIn DMA Import

The import is a two-stage pipeline: fetch and route DMA snapshot domains, then parse staged CSV exports into the application profile model.

### Domain Buckets

| Bucket | Domains | Destination |
| --- | --- | --- |
| First-class typed | `PROFILE`, `POSITIONS`, `EDUCATION`, `SKILLS`, `CERTIFICATIONS`, `PROJECTS`, `RECOMMENDATIONS` | Typed `CandidateProfile` fields (Experience, Education, Skills, etc.) |
| Enrichment | `VOLUNTEERING_EXPERIENCES`, `LANGUAGES`, `PUBLICATIONS`, `PATENTS`, `HONORS`, `COURSES`, `ORGANIZATIONS` | `CandidateProfile.ManualSignals` (note-like summaries) |
| Explicitly ignored | `ARTICLES`, `LEARNING`, `WHATSAPP_NUMBERS`, `PROFILE_SUMMARY`, `PHONE_NUMBERS`, `EMAIL_ADDRESSES` | Not imported, not written, no diagnostics warnings |

### Import Pipeline

```mermaid
graph LR
    A[DMA portability token] --> B[LinkedInMemberSnapshotImporter]
    B --> C[LinkedIn DMA API]
    C --> B
    B --> D[Exact domain routing]
    D --> E[Temporary CSV files]
    E --> F[LinkedInExportImporter]
    F --> G[CandidateProfile]
    G --> H[LinkedInExportImportResult]
    H --> I[WorkspaceSession.SetImportResult]
    I --> J[Start / Setup UI and diagnostics]
```

`LinkedInMemberSnapshotImporter` pages through the API, applies the domain registry, and writes temporary export-root files. `LinkedInExportImporter` parses those files and maps them into `CandidateProfile` collections.

The typed/enrichment split is deliberate. Typed collections (Experience, Education, Skills, Certifications, Projects, Recommendations) feed ranking and document generation more strongly than enrichment notes. Enrichment domains in `ManualSignals` preserve extra context without overloading narrower concepts.

### Diagnostics

The diagnostics page reads `WorkspaceSession.ImportResult` and formats it via `LinkedInImportDiagnosticsFormatter` into overview counts, discovered files, experience previews, enrichment notes, and warnings.

```mermaid
graph TD
    A[WorkspaceSession.ImportResult] --> B[LinkedInImportDiagnosticsFormatter]
    B --> C[Profile summary]
    B --> D[Experience entries]
    B --> E[Manual signal entries]
    B --> F[Files and warnings]
    C --> G[SessionDiagnostics.razor]
    D --> G
    E --> G
    F --> G
```

---

## 7. Job Workbench Flow

Implemented in [JobWorkbench.razor](../src/LiCvWriter.Web/Components/Pages/Workspace/JobWorkbench.razor). Each job tab is independent and carries its own state through `JobSetSessionState`.

### Per-Tab State

See [State Ownership](#state-ownership) for the full field list per job tab (`JobSetSessionState`).

### Brokered Streaming Transport

Most LLM-backed workbench actions no longer execute as page-local long-running calls. The page starts a brokered operation through Minimal API endpoints, receives an operation id plus snapshot/events/cancel URLs, and subscribes to `/api/llm/operations/{operationId}/events` via `EventSource` in [llm-stream.js](../src/LiCvWriter.Web/wwwroot/llm-stream.js).

`LlmOperationBroker` enforces one active LLM operation per job tab, updates `WorkspaceSession` / `OperationStatusService`, and publishes SSE events for:

- `job-context`
- `fit-review`
- `technology-gap`
- `refresh-all`
- `generate-drafts`

The shared sidebar consumes those telemetry updates through `OperationStatusService`: the reasoning monitor shows current or last captured model thinking, the status monitor shows the current or last streaming status detail, and the activity panel keeps only finished entries.

### End-to-End Pipeline

```mermaid
graph LR
    A[Job URL + company URLs] --> B[HttpJobResearchService]
    B --> C[JobPostingAnalysis + CompanyResearchProfile]
    C --> D[JobFitWorkspaceRefreshService]
    D --> E[JobFitAnalysisService]
    D --> F[EvidenceSelectionService]
    C --> G[LlmTechnologyGapAnalysisService]
    E --> H[JobFitAssessment]
    F --> I[EvidenceSelectionResult]
    H --> J[DraftGenerationService]
    I --> J
    G --> J
    J --> K[MarkdownDocumentRenderer]
    J --> L[LocalDocumentExportService]
    J --> M[LocalMarkdownAuditStore]
    L --> N[".md + .docx files"]
```

### Research

The workbench starts job-context analysis through the broker, then streams updates back into the page and shared sidebar. Inside the operation, the broker validates inputs, persists input fields, marks LLM work as started, then runs two sequential steps:

1. `ExecuteJobAnalysisAsync()` → `IJobResearchService.AnalyzeAsync()` — fetches and strips HTML from the job URL, sends to LLM for structured parsing, returns `JobPostingAnalysis`
2. `ExecuteCompanyContextAsync()` → `IJobResearchService.BuildCompanyProfileAsync()` — fetches all company-context URLs, sends to LLM, returns `CompanyResearchProfile`

Sequential execution is deliberate: both stages mutate shared page state and telemetry. If job analysis fails, company-context building does not run.

### Fit Review and Evidence Ranking

Refreshed through `JobFitWorkspaceRefreshService`, which coordinates two deterministic services:

```mermaid
graph TD
    A[CandidateProfile] --> B[CandidateEvidenceService.BuildCatalog]
    C[JobPostingAnalysis] --> D[JobFitAnalysisService.Analyze]
    E[CompanyResearchProfile] --> D
    F[ApplicantDifferentiatorProfile] --> D
    B --> D
    D --> G[JobFitAssessment]
    A --> H[EvidenceSelectionService.Build]
    C --> H
    E --> H
    F --> H
    G --> H
    H --> I[Ranked + selected evidence]
```

**Fit scoring** — `JobFitAnalysisService` compares the candidate profile against job requirements. Evidence is weighted by type (Experience: 60, Project: 55, Recommendation: 50, Certification: 40, Summary: 20, Headline: 18, Note: 14). Requirements are categorized as must-have, nice-to-have, or cultural, and matched as strong, partial, or missing. The output is a `JobFitAssessment` with an overall score (0–100) and apply/stretch/skip recommendation.

**Optional LLM enhancement** — the broker can pass the deterministic fit output through `LlmFitEnhancementService` to add semantic evidence matching and stronger recommendation text. When enhancement is used, the workbench labels the result as LLM-enhanced while still relying on the deterministic fit pipeline as the base layer.

**Evidence cataloguing** — `CandidateEvidenceService.BuildCatalog()` transforms the `CandidateProfile` into a flat catalog of evidence items across seven types: Headline, Summary, Experience, Project, Recommendation, Certification, and Note (manual signals). Deduplication groups by ID and selects the richest variant.

**Evidence ranking** — `EvidenceSelectionService.Build()` multi-criteria ranks the evidence catalog:
- Base score by type (Experience: 24, Project: 20, Recommendation: 18, Certification: 12, Summary: 8, Headline/Note: 6)
- Job requirement match (+18 must-have, +10 nice-to-have, +12 cultural)
- Narrative alignment with differentiator profile (+8)
- Third-party validation for recommendations (+6)
- Concrete work history for experiences with source reference (+4)
- Context term matching against job/company signals (+4)

Returns top 30 ranked items. Evidence selection is interactive — the user can change which items are selected before generating.

### Technology Gap Analysis

LLM-backed, with deterministic fallback. Compares candidate profile against job-detected technologies and company signals to surface possibly underrepresented technologies. Returns `TechnologyGapAssessment` with detected technologies and gap candidates.

---

## 8. Document Rendering and Export

Document generation is a three-stage pipeline orchestrated by `DraftGenerationService`: LLM generation → Markdown rendering → file export.

### Generation Orchestration

```mermaid
sequenceDiagram
    actor User
    participant Workbench as JobWorkbench.razor
    participant Refresh as JobFitWorkspaceRefreshService
    participant Generate as DraftGenerationService
    participant Llm as ILlmClient
    participant Render as MarkdownDocumentRenderer
    participant Export as LocalDocumentExportService
    participant Audit as LocalMarkdownAuditStore
    participant Workspace as WorkspaceSession

    User->>Workbench: Generate and export drafts
    Workbench->>Refresh: RefreshActiveJobSetAsync()
    Workbench->>Generate: GenerateAsync(request)
    loop per requested document kind
        Generate->>Llm: GenerateAsync(LlmRequest)
        Llm-->>Generate: LlmResponse (content + tokens)
        Generate->>Render: RenderAsync(DocumentRenderRequest)
        Render-->>Generate: GeneratedDocument (Markdown)
        Generate->>Export: ExportAsync(GeneratedDocument)
        Export-->>Generate: DocumentExportResult (.md + .docx paths)
        Generate->>Audit: SaveAsync(AuditTrailEntry)
    end
    Generate-->>Workbench: DraftGenerationResult
    Workbench->>Workspace: SetGeneratedDocuments()
```

### Markdown Rendering — MarkdownDocumentRenderer

The renderer shapes LLM-generated body text into structured Markdown with ATS-friendly section titles. The output varies by `DocumentKind`:

**CV rendering flow:**

1. **Header** — candidate name (H1), headline (blockquote), target role section
2. **Professional Profile** — LLM-generated profile overview + keyword line
3. **Fit Snapshot** — strengths and overall score from `JobFitAssessment`
4. **Professional Experience** — up to 12 entries with Title | Company (H3), period, description
5. **Projects** — all `CandidateProfile.Projects` with title, period, description, URL
6. **Recommendations** — all recommendations with author/company/title and language annotation
7. **Certifications** — from selected evidence items of type Certification

**Other document kinds:**
- **Cover Letter** — letter body + fit snapshot + applicant angle + selected evidence
- **Profile Summary** — summary body + applicant angle + certifications
- **Interview Notes** — talking points + fit snapshot + selected evidence + recommendations (if not already in evidence)

### Keyword Line (ATS Optimization)

`BuildKeywordLine()` cross-references the job's `MustHaveThemes`, `NiceToHaveThemes`, and `TechnologyGapAssessment.DetectedTechnologies` against evidence tags from the selected evidence. Only terms the candidate has evidence for are included. Output: a comma-separated "Key Technologies & Competencies" line under the professional profile.

### Language Detection and Translation Annotation

`AppendAllRecommendations()` annotates each recommendation with translation context when the recommendation language differs from the output language.

Detection uses `DetectDanish()`, a word-frequency heuristic:
- Scans all words against a 36-word `DanishMarkers` set (common Danish function words: "og", "er", "med", "har", "det", "en", "af", "til", etc.)
- Requires minimum 5 words in the text
- Threshold: 8% Danish marker ratio → classified as Danish
- Returns `true` for Danish text, `false` for English or inconclusive

`GetTranslationAnnotation()` compares detected language against output language:
- Danish text + English output → " *(translated from Danish)*"
- English text + Danish output → " *(translated from English)*"
- Same language → empty string

### Word (.docx) Export Pipeline

`LocalDocumentExportService` writes both Markdown and Word files for every exported document.

```mermaid
graph LR
    A[GeneratedDocument.Markdown] --> B[Markdig Pipeline]
    B --> C[HTML]
    C --> D[HtmlToOpenXml.HtmlConverter]
    D --> E[OpenXml Body Content]
    E --> F[WordprocessingDocument]
    G[Style Definitions] --> F
    H[Document Defaults] --> F
    I[Page Layout] --> F
    J[Document Properties] --> F
    F --> K[".docx file"]
    A --> L[".md file"]
```

**Pipeline steps:**

1. **Markdown → HTML** — Markdig with `UseAdvancedExtensions()` converts the rendered Markdown to HTML
2. **HTML → OpenXml** — `HtmlConverter.ParseBody()` from HtmlToOpenXml converts HTML elements into OpenXml body content
3. **Style injection** — `AddStyleDefinitions()` creates built-in Word heading styles (Heading1: 14pt, Heading2: 12pt, Heading3: 11pt) plus Normal body style, all Calibri
4. **Document defaults** — `AddDocumentDefaults()` sets Calibri 11pt, 1.15 line spacing, 120 twips after-paragraph spacing
5. **Page layout** — `SetSingleColumnLayout()` applies 1-inch margins on all sides
6. **Metadata** — `SetDocumentProperties()` writes `dc:title` and `dc:subject` to `CoreFilePropertiesPart` for ATS metadata extraction

### ATS/AI Readability Design Decisions

| Decision | Rationale |
| --- | --- |
| Built-in Word heading styles (`heading 1`, `heading 2`, `heading 3`) | ATS parsers recognize built-in heading styles; custom styles are often ignored |
| Single-column layout | Multi-column and table-based layouts confuse most ATS parsers |
| No tables for content layout | Tables are for data only; using them for layout breaks ATS reading order |
| Standard section titles ("Professional Profile", "Professional Experience", "Projects", "Recommendations") | ATS parsers match against known section name patterns |
| Calibri font throughout | Universal, clean, highly readable sans-serif |
| Document metadata (title, subject) | Some ATS systems extract metadata for candidate identification |
| Keyword-rich profile line | Increases ATS keyword match rate for technology skills |

---

## 9. LLM Prompt Architecture

`DraftGenerationService` constructs a system prompt and a user prompt per document kind.

### System Prompt (per DocumentKind)

Each system prompt specifies:
- Target language (English or Danish)
- Document kind focus (e.g., "Write a concise {lang} CV for a {role} position at {company}")
- Evidence grounding rule ("grounded strictly in supplied evidence")
- Naming convention for Danish ("Keep technology names, company names, quoted job phrases in their original or English form")
- CV-specific: "Weave as many of the job's key technologies and themes into the professional profile as truthfully possible"
- CV-specific: "If any recommendation text is not in {lang}, translate it to {lang} and append '(translated from \<original language\>)'"

### User Prompt Structure

The user prompt assembles context from multiple sources into a single structured prompt:

```
Generate a {Kind} in {Language}.

Rules:
- Use only facts from supplied evidence
- Do not invent data
- Do not mention gaps or weaknesses
- Do not expose internal assessment data
- Keep names in original form
- Use job themes only to guide emphasis

Target role: {RoleTitle} at {CompanyName}
Summary: {JobSummary}
Must-have themes: {themes}
Nice-to-have themes: {themes}

Fit review: {score, strengths, gaps}

Candidate: {Name} | {Headline} | {Location} | {Industry}
Summary: {summary}
Certifications: {list}

Experience: {up to 8 most recent roles}

Projects: {all projects}

Company context: {company research text}

Applicant differentiators: {profile lines}

Selected evidence: {ranked evidence items}

Technology context: {detected techs, underrepresented techs}

Recommendations: {all recommendations with author and company}

Additional instructions: {user-supplied}
```

Experience is capped at 8 entries in the prompt with a truncation note. Recommendations and projects are included in full.

---

## 10. Telemetry and Diagnostics

`OperationStatusService` is the global activity and LLM telemetry feed. Pages and the broker call into it through `RunAsync()` and LLM progress callbacks.

```mermaid
graph LR
    A[Page actions + broker progress callbacks] --> B[OperationStatusService]
    B --> C[MainLayout.razor]
    C --> D[Reasoning monitor]
    C --> E[Status monitor]
    C --> F[Finished activity list]
    G[WorkspaceSession] --> H[SessionDiagnostics.razor]
    B --> H
    I[LinkedInImportDiagnosticsFormatter] --> H
    H --> J[Current telemetry]
    H --> K[Session context]
    H --> L[LinkedIn import details]
    H --> M[Thinking preview]
    H --> N[Recent activity]
```

The telemetry model carries message/detail text, selected model, elapsed time, token counts, estimated remaining time, thinking preview, full thinking content, final response content, completion flag, and an event sequence number.

The shared sidebar surfaces the operational summary continuously:

- Floating navigation with the active page highlighted
- A reasoning monitor that auto-scrolls while new thinking arrives
- A compact status monitor that shows streaming status detail without scrolling
- An activity panel that lists only finished entries

The diagnostics page remains the deeper inspection surface for verbose telemetry, response capture, import diagnostics, and session-level state.

---

## 11. Deterministic vs. LLM-Backed Behavior

| Concern | Path | Needs session model? | Notes |
| --- | --- | --- | --- |
| Ollama verification | `ILlmClient.VerifyModelAvailabilityAsync()` | No | Checks service reachability |
| Job parsing | `HttpJobResearchService.AnalyzeAsync()` | Yes | LLM structured parsing |
| Company parsing | `HttpJobResearchService.BuildCompanyProfileAsync()` | Yes | LLM structured parsing |
| Insights PDF drafting | `InsightsDiscoveryApplicantDifferentiatorDraftingService.DraftAsync()` | Yes | LLM extraction + field drafting |
| Fit review | `JobFitWorkspaceRefreshService` + optional `LlmFitEnhancementService` via `LlmOperationBroker` | Optional | Deterministic core with semantic enhancement when requested or triggered by broker flow |
| Evidence ranking | `EvidenceSelectionService.Build()` | No | Deterministic once context exists |
| Technology gap | `LlmTechnologyGapAnalysisService.AnalyzeAsync()` | Yes (primary) | Deterministic fallback in analyzer layer |
| Refresh all | `LlmOperationBroker.StartRefreshAllAnalysis()` | Yes | Orchestrates job context, fit review, and technology gap as one streamed operation |
| Draft generation | `DraftGenerationService.GenerateAsync()` via `LlmOperationBroker` | Yes | Uses session model + thinking level; broker owns pre-flight fit refresh |
| Document rendering | `MarkdownDocumentRenderer.RenderAsync()` | No | Deterministic Markdown shaping |
| Word export | `LocalDocumentExportService.ExportAsync()` | No | Deterministic Markdown → HTML → DOCX |
| Diagnostics | `MainLayout.razor`, `SessionDiagnostics.razor`, and formatters | No | Shared sidebar for live summary, diagnostics page for deep inspection |

---

## 12. Implementation Boundaries

- Start / Setup is the entry point for session-wide LLM and profile context.
- Any locally available Ollama model works — the user picks during setup.
- Job Workbench research runs job parsing then company-context building sequentially from one button.
- LinkedIn DMA import is the only supported import path.
- First-class typed domains: `PROFILE`, `POSITIONS`, `EDUCATION`, `SKILLS`, `CERTIFICATIONS`, `PROJECTS`, `RECOMMENDATIONS`.
- Enrichment domains preserved as notes: `VOLUNTEERING_EXPERIENCES`, `LANGUAGES`, `PUBLICATIONS`, `PATENTS`, `HONORS`, `COURSES`, `ORGANIZATIONS`.
- Explicitly ignored: `ARTICLES`, `LEARNING`, `WHATSAPP_NUMBERS`, `PROFILE_SUMMARY`, `PHONE_NUMBERS`, `EMAIL_ADDRESSES`.
- Evidence ranking is deterministic; fit review starts from deterministic scoring and can be optionally LLM-enhanced.
- Session model and thinking settings remain editable after LLM-backed work starts; changes apply to future operations until the user reruns affected analyses or drafts.
- Brokered SSE endpoints exist for job-context, fit-review, technology-gap, refresh-all, and draft-generation operations.
- Document export produces both .md and .docx for every generated document.
- CV rendering includes all recommendations (not just selected evidence) with language detection.
- The shared sidebar carries floating navigation, two CRT monitors, and finished activity history; the diagnostics page remains the verbose inspection surface.

---

For the primary files index, see [§1 Primary Files](#primary-files).
