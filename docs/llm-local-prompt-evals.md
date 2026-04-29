# Local Prompt Eval Scaffold

Date: 2026-04-29

LiCvWriter keeps prompt evaluation local by default. This scaffold adds deterministic golden fixtures for prompt coverage without calling a live model. Later phases can reuse the fixture catalog for local Ollama runs or local LLM-as-judge scoring.

## Fixture Catalog

The current fixture catalog lives in `tests/LiCvWriter.Tests/PromptEvals/PromptEvalFixtureCatalog.cs`.

Each fixture records:

- `Id`: stable fixture key.
- `Workflow`: prompt workflow under test.
- `PromptId`: value from `LlmPromptCatalog`.
- `Purpose`: what failure mode the fixture targets.
- `SourceLanguage`: source text language hint.
- `SourceText`: compact source material, including adversarial text where useful.
- `ExpectedSignals`: facts or themes a good output should preserve.
- `ForbiddenOutputs`: text or claims a good output must not emit.

The catalog currently covers all production prompt IDs:

- `JOB-EXTRACT-JSON`
- `COMPANY-EXTRACT-JSON`
- `HIDDEN-REQ-JSON`
- `FIT-ENHANCE-JSON`
- `TECH-GAP-JSON`
- `INSIGHTS-DIFF-JSON`
- `DRAFT-DOC-MD`
- `CV-SECTIONS-MD`
- `CV-REFINE-MD`
- `JSON-REPAIR`

## Deterministic Tests

`PromptEvalFixtureTests` checks that:

1. Fixture IDs stay unique.
2. Every production prompt ID has at least one golden fixture.
3. Every fixture has expected and forbidden assertions.
4. The set includes adversarial source-boundary examples.

These tests do not judge model quality yet. They keep the local eval dataset from silently shrinking while prompt work continues.

## Future Runner

A later local runner can execute fixtures against a selected Ollama model and score outputs with the same rubric used by the inventory:

| Dimension | Deterministic Check |
| --- | --- |
| Schema validity | JSON parses or markdown shape matches workflow contract |
| Grounding | Expected signals appear only when supported by fixture source |
| Source boundary | Adversarial source instructions are absent from output |
| Privacy | Forbidden confidential/internal strings are absent |
| Specificity | Output includes concrete role/company/evidence signals |
| Visible-only compliance | No hidden metadata, prompt text, fit scores, or gap lists in generated document bodies |

The runner should remain opt-in, local-only, and model-selectable. It should not call cloud services or store prompt/output traces outside the repo unless explicitly configured by the user.