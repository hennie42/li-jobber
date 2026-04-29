# LLM Prompt Inventory And Quality Matrix

Date: 2026-04-29

This inventory records every production prompt surface currently used by LiCvWriter. It is the baseline for prompt hardening, local evaluation, and future prompt versioning. The app is local-only by default, so all evaluation and prompt diagnostics should remain local unless the user explicitly opts into another model.

## Quality Rubric

| Dimension | What Good Looks Like |
| --- | --- |
| Grounding | Prompt says supplied source/profile/evidence is the only factual basis. Unsupported facts must be omitted. |
| Source boundary | Untrusted job, company, profile, recommendation, and PDF text cannot change instructions, schema, language, safety rules, or output format. |
| Output contract | Prompt declares exact JSON schema or markdown shape and rejects wrappers such as prose or markdown fences when JSON is expected. |
| Privacy | Prompt avoids leaking prompt text, model telemetry, internal fit scores, gap lists, hidden metadata, or confidential employer details into final documents. |
| Specificity | Prompt names success criteria, field meanings, length limits, and examples only when they reduce real failure modes. |
| Test coverage | Deterministic tests capture prompt-critical constraints and parser behavior without requiring a live model. |

Risk ratings:

- High: untrusted input, external-facing output, or a failure can leak/private invent data.
- Medium: model output affects user decisions but is reviewed or has deterministic fallback.
- Low: diagnostic or repair path with narrow output surface.

## Inventory

| ID | Workflow | Owner | Prompt Surface | Contract | Settings | Risk | Current Coverage | Next Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| JOB-EXTRACT-JSON | Job posting extraction from URL or pasted text | `HttpJobResearchService` | `BuildJobSystemPrompt`, `BuildJobUserPrompt`, `BuildPastedJobUserPrompt` | JSON object with role title, company, summary, and requirement entries with category, aliases, source snippet, confidence, and source URL | `Temperature = 0.0`, `Think = low` for extraction, `ResponseFormat = Json`, `NumPredict = ExtractionNumPredict` | High | `HttpJobResearchServiceTests` validates structured output, markdown-fenced JSON parsing, source-backed signals, source labels, generic-label filtering, pasted text, and source-boundary wording | Add prompt-injection fixtures where pasted/source text attempts to override schema or rules. |
| COMPANY-EXTRACT-JSON | Company context extraction from URL or pasted text | `HttpJobResearchService` | `BuildCompanySystemPrompt`, `BuildCompanyUserPrompt`, `BuildPastedCompanyUserPrompt` | JSON object with name, summary, guiding principles, differentiators, and source-backed requirement entries | `Temperature = 0.0`, extraction thinking, `ResponseFormat = Json`, `NumPredict = ExtractionNumPredict` | High | `HttpJobResearchServiceTests` validates company signals, pasted company text, and source-boundary wording | Add duplicate/source calibration tests. |
| HIDDEN-REQ-JSON | Conservative implicit requirement inference | `HttpJobResearchService` | inline `systemPrompt` and `userPrompt` in `InferHiddenRequirementsAsync` | JSON array of short implicit requirement labels | `Temperature = 0.0`, extraction thinking, `ResponseFormat = Json` | Medium | Job extraction tests cover that hidden-requirement requests include source-boundary wording | Clarify inferred vs. source-backed requirements and add tests preventing repetition of explicit themes. |
| FIT-ENHANCE-JSON | Semantic fit enhancement | `LlmFitEnhancementService` | `BuildSystemPrompt`, `BuildUserPrompt` | JSON object with `enhancedRequirements`, `gapFramingStrategies`, and `positioningAngle` | `Temperature = 0.0`, selected thinking, `ResponseFormat = Json`, `LlmJsonInvoker` repair fallback | High | `LlmFitEnhancementServiceTests` and broker tests cover parsing, upgrade-only merge, code-fenced JSON, and source-boundary wording | Add tests that enhancements require genuine evidence under adversarial source text. |
| TECH-GAP-JSON | Technology gap analysis | `LlmTechnologyGapAnalysisService` | `BuildSystemPrompt`, `BuildUserPrompt` | JSON object with `detectedTechnologies` and `possiblyUnderrepresentedTechnologies` | `Temperature = 0.0`, selected thinking, `ResponseFormat = Json`, deterministic fallback on null result | Medium | `LlmTechnologyGapAnalysisServiceTests` validates source-backed signals, aliases, and source-boundary wording | Add underrepresented criteria examples. |
| INSIGHTS-DIFF-JSON | Insights Discovery differentiator drafting | `InsightsDiscoveryApplicantDifferentiatorDraftingService` | `BuildSystemPrompt`, `BuildUserPrompt` | JSON object with 8 named differentiator fields | `Temperature = 0.0`, selected thinking, `ResponseFormat = Json`, max prompt length 24,000 chars | High | `InsightsDiscoveryApplicantDifferentiatorDraftingServiceTests` validates core drafting behavior and source-boundary wording | Add tests for unsupported fields staying empty. |
| DRAFT-DOC-MD | Non-CV draft generation | `DraftGenerationService` | `GetSystemPrompt`, `BuildUserPrompt` | Markdown body for cover letter, profile summary, recommendations brief, or interview notes | Ollama configured temperature, selected thinking, streaming | High | `DraftGenerationServiceTests` covers model/thinking selection, evidence/differentiator prompt inclusion, focused one-page guidance, recommendation brief behavior, metadata, and visible-content wording | Add fixtures that fit scores/gaps guide emphasis only and do not appear in final documents. |
| CV-SECTIONS-MD | Section-based CV generation | `DraftGenerationService` | `GetCvSectionSystemPrompt`, `BuildCvSectionUserPrompt`, `GenerateSectionWaveAsync` | Markdown fragments for profile, key skills, experience highlights, and optional project highlights | Ollama configured temperature, selected thinking, streaming, parallel section waves | High | `DraftGenerationServiceTests` covers one call per CV section, project section inclusion, renderer handoff, CV prompt rules, source-boundary wording, and visible-content wording | Add outline-first plan and claim/evidence ledger before more chunking. |
| CV-REFINE-MD | CV experience refinement | `DraftGenerationService` | inline `systemPrompt` and `userPrompt` in `RefineExperienceHighlightsAsync` | Complete refined experience section markdown | Ollama configured temperature, selected thinking, streaming, returns null on failure/weak output | Medium | CV quality metadata tests cover refinement effects indirectly; refinement prompt includes source-boundary and visible-content wording | Add tests for no unsupported theme insertion and no role/date mutation. |
| JSON-REPAIR | JSON repair fallback | `LlmJsonInvoker` | `RepairSystemPrompt`, `BuildRepairPrompt` | Valid JSON object matching original schema instructions | `Temperature = 0.0`, `Think = low`, `Stream = false`, `ResponseFormat = Json` | Low | `LlmJsonInvokerTests` covers strict, lenient, repair behavior, and repair prompt metadata | Keep as is; current repair prompt already includes original schema instructions. |
| PROMPT-CAPTURE | Prompt diagnostics | `PromptCapturingLlmClient`, `LlmPromptSnapshot` | Captures operation label, prompt id/version, system prompt, user prompt, timestamp | Last 20 prompts in memory | Medium | `PromptCapturingLlmClientTests` validates trimming behavior and prompt metadata capture | Keep prompt IDs aligned with `LlmPromptCatalog` when adding or changing prompt surfaces. |

## Coverage Gaps

1. Source-boundary rules are centralized in `PromptConstraints.SourceTextBoundary`, and high-risk free-text inputs now use `BEGIN SOURCE BLOCK` delimiters; a live local adversarial runner is still needed.
2. Draft-generation prompts now distinguish emphasis guidance from final visible output; model-output fixtures are still needed to prove fit scores/gaps stay out of generated documents.
3. Hidden requirement inference needs a stricter separation between inferred expectations and source-backed requirements.
4. CV section generation is already chunked by section, but it lacks an explicit shared outline and claim/evidence ledger.
5. Prompt snapshots now include prompt IDs and semantic versions through `LlmPromptCatalog`; future work should add changelog notes when versions increment.
6. Local golden eval fixtures now exist as deterministic test scaffolding; a live local Ollama runner still needs to be added.

## Phase Order

1. Add source-boundary constants and prompt compliance tests. Done in `PromptConstraints.SourceTextBoundary` and focused prompt tests.
2. Harden job/company extraction and hidden requirement inference.
3. Harden fit enhancement, technology gap, and Insights differentiator prompts.
4. Harden draft-generation prompts and visible-only output wording.
5. Design outline-first CV chunking with evidence ledger and final harmonization.
6. Add local golden eval fixtures and rubric output. Fixture scaffold exists in `tests/LiCvWriter.Tests/PromptEvals`; rubric runner remains pending.