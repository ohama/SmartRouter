---
phase: 15-quality-signal-enrichment
plan: 03
subsystem: quality-signal-enrichment
tags: [tests, readme, changelog, quality-check, expecto, integration-test, bad_reason, stats]

dependency-graph:
  requires: ["15-01", "15-02"]
  provides: [QSE unit+integration tests, README-sync, CHANGELOG entry]
  affects: ["Phase 16 planner"]

tech-stack:
  added: []
  patterns: [fake-Kestrel integration test, last-registration-wins DI override, testSequenced]

key-files:
  created:
    - tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - README.md
    - CHANGELOG.md
    - .planning/docs/quality-check-improvement-options.md

decisions:
  - "14 test cases: 3x QSE-01 (finish_reason length/stop/content_filter), 2x QSE-02 (case-insensitive keyword), 1x QSE-03 (refusal opt-in), 2x QSE-04 (Korean length), 2x QSE-05 (entropy), 2x QSE-06 (Phase 14 wrapper), 1x cascade order, 1x integration"
  - "ICanaryState registration required in startTestRouter: /stats endpoint resolves it (not needed by QualityFallbackTests which never GETs /stats)"
  - "IQualityCheckStats overridden after configureWithoutMl NoOp with real QueueDispatcher-backed instance (last-registration-wins) so counter assertions work"
  - "PITFALL-10 (queue starvation test) flaky under ThreadPool pressure — pre-existing from Phase 3; 102/102 passed in one run; 101/102 in another (PITFALL-10 flake)"
  - "README sections updated: §5.5 (5-dimension trigger list), §7 (BadFinishReasons + EntropyThreshold rows), §9.3 (bad_reason field + jq workflows), §8 /stats (4 quality_check_hits_* fields + curl example)"

metrics:
  duration: ~20 min
  completed: 2026-05-10
---

# Phase 15 Plan 03: Tests and Docs Summary

**One-liner:** 14 QSE test cases (13 unit + 1 fake-Kestrel integration) covering all 5 detection dimensions + Phase 14 backward-compat; README §5.5/§7/§9.3/§8 updated; CHANGELOG [Unreleased] silent-enable entry added.

## Commits

| Hash    | Message                                                                              | Files                                                             |
|---------|--------------------------------------------------------------------------------------|-------------------------------------------------------------------|
| da35014 | test(15-03): add QualitySignalEnrichmentTests for 5 detection dimensions + integration test | QualitySignalEnrichmentTests.fs, SmartRouter.Tests.fsproj, RouterTests.fs |
| cf97738 | docs(15-03): update README §5.5/§7/§9.3 + /stats with Phase 15 quality signal enrichment | README.md |
| 34bc979 | docs(15-03): add CHANGELOG [Unreleased] entry + Phase 15 status note in quality-check-improvement-options.md | CHANGELOG.md, quality-check-improvement-options.md |

## Tasks Completed

| Task | Name                                                              | Commit  | Files                                                              |
|------|-------------------------------------------------------------------|---------|--------------------------------------------------------------------|
| 1    | Create QualitySignalEnrichmentTests.fs + register in fsproj + rootTests | da35014 | QualitySignalEnrichmentTests.fs, SmartRouter.Tests.fsproj, RouterTests.fs |
| 2    | Update README §5.5/§7/§9.3 + /stats section                      | cf97738 | README.md                                                         |
| 3    | CHANGELOG [Unreleased] entry + quality-check-improvement-options.md status note | 34bc979 | CHANGELOG.md, quality-check-improvement-options.md |

## Test Baseline

**Before plan 15-03:** 88 passed + 16 ignored + 0 failed (Phase 14 baseline)
**After plan 15-03:** 102 tested (88 + 14 new QSE cases) — 102 passed + 16 ignored + 0 failed

**Test breakdown:**
- 13 unit tests — pure function calls to `analyzeResponse`, `charEntropy`, `isBadResponse`; no Kestrel
- 1 integration test (QSE-INT) — fake-Kestrel 35B + 122B, asserts trace `bad_reason` field + `/stats` counter increment

**QSE test case distribution:**
- QSE-01: 3 cases (finish_reason=length triggers; stop passes; content_filter triggers)
- QSE-02: 2 cases (todo lowercase; ToDo mixed-case)
- QSE-03: 1 case (operator refusal pattern opt-in)
- QSE-04: 2 cases (Korean passes; ASCII fails)
- QSE-05: 2 cases (repetitive loop triggers; normal text entropy > 4.0)
- QSE-06: 2 cases (Phase 14 wrapper true/false backward-compat)
- Cascade: 1 case (stage-1 finish_reason wins over stage-2 length AND stage-4 keyword)
- QSE-INT: 1 integration case (full pipeline: finish_reason → trace bad_reason + stats counter)

**Phase 14 baseline preserved:** `dotnet run --project tests/SmartRouter.Tests -- --sequenced --filter-test-list "quality-fallback"` returns 8 passed, 0 failed.

## README Sections Updated

| Section | Change |
|---|---|
| §5.5 Quality fallback | Replaced 2-condition trigger with 5-dimension cheap-first cascade; added stage descriptions (finish_reason, Korean-aware length, entropy, case-insensitive keyword); added bad_reason trace reference; added refusal opt-in example |
| §7 Routing.QualityFallback | Table expanded 3→5 rows: BadFinishReasons (Phase 15) + EntropyThreshold (Phase 15); BadKeywords description updated with case-insensitive note + refusal opt-in guidance |
| §9.3 Trace log schema | Added bad_reason as 13th field with type/description table; added 2 jq workflow examples (aggregate by tag, filter by entropy) |
| §8 /stats | Added 4 quality_check_hits_* fields to JSON example + field description table + curl monitoring example |

## CHANGELOG [Unreleased] Entry (verbatim)

```markdown
### Changed

- Quality fallback now triggers on 5 dimensions (Phase 15 — silent enable): finish_reason,
  Korean-aware length, Shannon entropy, case-insensitive keyword match. New config keys
  BadFinishReasons and EntropyThreshold activate silently with defaults.
  - Restore Phase 14: set BadFinishReasons:[] + EntropyThreshold:0.01

### Added

- TraceLog bad_reason field (tag=value format)
- /stats 4 quality_check_hits_* counters (int64, process-lifetime)
- Config keys BadFinishReasons + EntropyThreshold
```

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] ICanaryState missing from startTestRouter fixture**

- **Found during:** Task 1 integration test execution
- **Issue:** `/stats` endpoint calls `GetRequiredService<ICanaryState>()` at request time; `QualityFallbackTests.startTestRouter` never calls `/stats` so this wasn't visible before. The new integration test GETs `/stats` and hit HTTP 500 with `InvalidOperationException: No service for type ICanaryState`.
- **Fix:** Added `CanaryState(0)` + `ICanaryState` singleton registrations to the QSE test fixture. Pattern mirrors `QueueTests.fs` lines 538-539.
- **Files modified:** `tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs`
- **Commit:** da35014 (included in Task 1 commit)

**2. [Rule 2 - Missing functionality] IQualityCheckStats NoOp override needed**

- **Found during:** Task 1 — understanding the DI graph
- **Issue:** `configureWithoutMl` registers a NoOp `IQualityCheckStats` (0 counters always). The integration test asserting counter increments needs the real `QueueDispatcher`-backed implementation.
- **Fix:** Added `AddSingleton<IQualityCheckStats>` registration AFTER `configureWithoutMl` (last-registration-wins) resolving from the manually-registered `QueueDispatcher` singleton.
- **Files modified:** `tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs`
- **Commit:** da35014 (included in Task 1 commit)

## Open Items for Phase 16 Planner

1. **`bad_reason` semantics are stable** — `"tag=value"` format with `=` separator; `jq -r '.bad_reason | split("=")[0]'` is the canonical aggregation pattern. Phase 16 borderline-band classifier can read `bad_reason` to understand why Phase 15 fired.
2. **`IQualityCheckStats` interface is available** for Phase 16 to extend if a borderline-band check needs its own counter (`RecordBorderlineHit` etc.). Struct tuple return pattern on `GetHits()` is allocation-free on the `/stats` hot path.
3. **Entropy band edge thresholds (2.5..3.5)** are referenced in CONTEXT.md as a Phase 16 concept — the "borderline zone" between clearly-bad (< 2.5) and clearly-good (> 3.5) entropy. Phase 16 owns these band thresholds; Phase 15 uses a single hard cutoff at 2.5.
4. **Tier 2-B (prompt-relative length)**, **Tier 2-C (logprob threshold)**, **Tier 3 (122B-as-judge)**, and **Tier 4 (QualityClassifier ML model)** are not implemented — documented in `.planning/docs/quality-check-improvement-options.md` as Phase 16+ candidates.
5. **Refusal-pattern default expansion** (Tier 1-C) deferred — operator opt-in via `BadKeywords` is the chosen path per CONTEXT.md §"Refusal pattern default 정책".

## ARCH-01 + ARCH-02 Verification

- `grep -E "(Microsoft.ML|Serilog|HttpClient|System.Net.Http)" src/SmartRouter.Cli/Adapters/QualityCheck.fs` → 0 matches
- `bash scripts/check-no-async.sh` → "OK: no async {} expressions in src/SmartRouter.Core"
