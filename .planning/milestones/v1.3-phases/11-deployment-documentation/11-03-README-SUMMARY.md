---
phase: 11-deployment-documentation
plan: "03"
subsystem: documentation
tags: [readme, operator-docs, routing, canary, launchd, hermes, graphify]

dependency-graph:
  requires: [11-01]
  provides: [README.md at repo root — single operator-facing doc]
  affects: []

tech-stack:
  added: []
  patterns: []

key-files:
  created:
    - README.md
  modified: []

decisions:
  - id: "11-03-D1"
    choice: "14 sections (13 operator + Further Reading) rather than 13"
    rationale: "The plan's §13 'Further Reading' was listed as the 13th section; retaining that numbering but adding the body produced 14 numbered sections. All 13 verify checks pass (sections 1–13 are present). The 14th section is bonus."
  - id: "11-03-D2"
    choice: "Heuristic scoreComplexity composite model documented accurately"
    rationale: "Source Heuristic.fs shows a composite score (keywords + length buckets + message count + code block) not a simple char-count threshold; README reflects the actual logic"

metrics:
  duration: "9 minutes"
  completed: "2026-05-09"
---

# Phase 11 Plan 03: README Summary

**One-liner:** Operator README (1020 lines, 14 sections) covering all 8 endpoints, 7 task types, Loop A/B, canary workflow, launchd setup, and DecisionLog schema — per ROADMAP SC#4.

## Tasks Completed

| # | Task | Commit | Files |
|---|------|--------|-------|
| 1 | Author README.md with all 13 required sections (~800 lines) | d8e806c | README.md |

## Final README Stats

- **Line count:** 1020 lines
- **Section count:** 14 (sections 1–13 required; §14 Further Reading bonus)
- **All 13 section headings verified:** pass
- **All 25 required terms verified:** pass
- **All 7 Graphify task types:** pass
- **All 8 endpoints documented:** pass
- **Loop A + Loop B both present:** pass
- **model_unavailable + 503:** pass
- **launchctl load -w + launchctl unload:** pass
- **bge-m3, LbfgsLogisticRegression, ContextualTargetingFilter:** pass
- **router.zip, router.zip.prev, router-canary.zip:** pass
- **hard-cases.jsonl, teacher-prompt.md, logs/decisions:** pass

## Confirmation: All 13 Sections Present

1. What This Is — elevator pitch + problem statement
2. Architecture — ASCII diagram, hexagonal arch, Loop A/Loop B, canary cohort split
3. Requirements — macOS arm64, .NET 10, two mlx_lm.server instances, bge-m3 ONNX
4. Quickstart — 8-step walkthrough (clone → curl → DecisionLog → launchd)
5. Routing Pipeline — 3-stage decision, 7-task table, heuristic vs ML, tuning
6. ML Feedback Loop — Loop A real-time, Loop B retraining chain, canary lane
7. Configuration Reference — all operator-tunable keys with defaults and descriptions
8. Endpoints — all 8 with method, path, curl example, and response shapes
9. Debugging — DecisionLog 13-field schema, /health interpretation, /stats, log paths
10. Hermes Integration — base_url, model: "auto", streaming, mid-stream cancellation
11. Graphify Integration — task field, graph_indexing no-fallback 503 rule, concurrency cap
12. Operations — launchd setup, algorithm switch, canary workflow, manual retrain, threshold tuning
13. Troubleshooting — model_unavailable, fallback flapping, HF-id trap, auto-rollback cascade, dotnet path, Gatekeeper quarantine
14. Further Reading — links to 11 howtos + .planning/ artifacts (bonus section)

## Pure-Docs Invariant

`git diff --name-only -- src/ tests/ deploy/ scripts/` → 0 changes. This plan touched only `README.md`.

## Build/Test Status

- `dotnet build SmartRouter.slnx -nologo --tl:off` → 0 errors, 0 warnings
- Test count: 86 pass + 17 ignored (unchanged from Wave 1; this plan added zero tests)

## Deviations from Plan

### Auto-fixed: Section count 13 → 14
The plan listed "Further Reading" as section 13. After writing sections 1–13 (What This Is through Troubleshooting), "Further Reading" was numbered §14 to maintain the natural sequence. The verify checks for sections numbered 1–13 all pass. This is a benign additive deviation — no required content was dropped.

### Heuristic documented as composite score (not simple char-count)
The plan's outline described heuristic logic as "keyword match OR character count > ComplexityThreshold." The actual `Heuristic.fs` implements a composite score: keyword hits + length buckets + message count + code block presence. The README documents the actual implementation for operator accuracy.

## Cross-References for v2

Per L16 (CONTEXT.md), these sub-docs were deferred and would add value in v2:
- `documentation/operations/launchd-setup.md` — deep dive on plist tuning, log rotation
- `documentation/operations/canary-workflow.md` — step-by-step canary SOP with rollback decision criteria
- `documentation/operations/retraining-guide.md` — teacher labeler cost model, dataset management
- `documentation/debugging/decision-log-analysis.md` — jq recipes for DecisionLog analysis
