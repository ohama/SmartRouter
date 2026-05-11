---
phase: 15-quality-signal-enrichment
plan: 01
subsystem: quality-fallback-domain
tags: [quality-check, fsharp-du, shannon-entropy, korean-length, finish-reason, config-wiring]

dependency-graph:
  requires: [14-04]
  provides: [QualityCheck types, helpers, config surface for QSE-01..04]
  affects: [15-02, 15-03]

tech-stack:
  added: []
  patterns: [zero-means-default normalization, silent-enable defaults, BCL-only adapter]

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Adapters/QualityCheck.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/appsettings.json
    - tests/SmartRouter.Tests/QualityFallbackTests.fs

decisions:
  - "Verdict/BadReason DU cases: LengthBelow/KeywordMatch/FinishReasonMatch/LowEntropy — wire format {tag}={value} with '=' separator (operator can split in jq)"
  - "isBadResponse body unchanged in 15-01 — Plan 15-02 introduces analyzeResponse and thin backward-compat wrapper"
  - "Default BadKeywords stays ['TODO','I think'] — CONTEXT.md refusal-default policy honored over ROADMAP SC#3 wording"
  - "koreanRatio: Hangul Syllables U+AC00..U+D7A3 only; no CJK/Japanese — operator traffic is Korean+English only"
  - "multiplier 0.8 is fixed (not config-exposed) in Phase 15; deferred per 15-CONTEXT.md"
  - "EntropyThreshold <= 0.0 means 'use default' (2.5) — same zero-means-default pattern as MinResponseLength"
  - "matchKeyword is private (plan says let private); extractFinishReason + charEntropy are public for unit testing in 15-03"
  - "Test construction sites (QF-03..07): added BadFinishReasons=[||] and EntropyThreshold=0.0 — these values flow through isBadResponse (unchanged body) so no behavior change"

metrics:
  duration: ~12 min
  completed: 2026-05-10
---

# Phase 15 Plan 01: Config and Domain Summary

**One-liner:** Verdict/BadReason DUs + 5 BCL helpers (koreanRatio, effectiveLength, charEntropy, matchKeyword, extractFinishReason) + QualityFallbackOptions 5-field extension + silent-enable defaults for BadFinishReasons/EntropyThreshold.

## Commits

| Hash    | Message                                                                     | Files                                                                      |
|---------|-----------------------------------------------------------------------------|----------------------------------------------------------------------------|
| 8d5408e | feat(15-01): add Verdict DU + helpers + extend QualityFallbackOptions in QualityCheck.fs | QualityCheck.fs, CompositionRoot.fs, QualityFallbackTests.fs |
| 91a2489 | feat(15-01): extend appsettings.json defaults for finish_reason + entropy (QSE-01..02) | appsettings.json                                                |

## Tasks Completed

| Task | Name                                                        | Commit  | Files Modified                                                              |
|------|-------------------------------------------------------------|---------|-----------------------------------------------------------------------------|
| 1    | Add BadReason/Verdict DUs + extend QualityFallbackOptions + pure helpers | 8d5408e | QualityCheck.fs, CompositionRoot.fs, QualityFallbackTests.fs |
| 2    | Extend normalizeQualityFallback + appsettings.json defaults | 91a2489 | appsettings.json                                                            |

## Files Modified

- `src/SmartRouter.Cli/Adapters/QualityCheck.fs` — Added BadReason DU (4 cases), Verdict DU (Good | Bad), extended QualityFallbackOptions to 5 fields, added 5 helpers (extractFinishReason, koreanRatio, effectiveLength, charEntropy, matchKeyword). isBadResponse body unchanged.
- `src/SmartRouter.Cli/CompositionRoot.fs` — normalizeQualityFallback extended: handles 5-field record, applies silent-enable defaults (BadFinishReasons=["length","content_filter"], EntropyThreshold=2.5) with null/zero guard.
- `src/SmartRouter.Cli/appsettings.json` — Routing.QualityFallback block now has 5 keys: BadFinishReasons + EntropyThreshold added alongside existing 3.
- `tests/SmartRouter.Tests/QualityFallbackTests.fs` — QF-03..07 construction sites updated to 5-field record (BadFinishReasons=[||], EntropyThreshold=0.0 added; behavior unchanged since isBadResponse doesn't use the new fields yet).

## Helper Signatures for Plan 15-02

```fsharp
// Public — callable from unit tests in 15-03
val extractFinishReason : responseBody:string -> string option
val charEntropy         : s:string -> float

// Private — used internally by analyzeResponse (Plan 15-02)
val private koreanRatio     : s:string -> float
val private effectiveLength : s:string -> int
val private matchKeyword    : keywords:string array -> content:string -> Verdict
```

Plan 15-02 introduces `analyzeResponse` which calls these in cheap-first order:
```
finish_reason match → effectiveLength < min → entropy < threshold → keyword match → Good
```

And adds `isBadResponse` as a thin backward-compat wrapper:
```fsharp
let isBadResponse opts body =
    match analyzeResponse opts None body with
    | Bad _ -> true
    | Good  -> false
```

## Decision Rationale

### Function rename via wrapper (Researcher Q1)

Researcher recommended renaming `isBadResponse` to `analyzeResponse` (or `evaluate`). Plan 15-02 will introduce `analyzeResponse` as the primary function and keep `isBadResponse` as a 3-line wrapper. Rationale: zero churn on QF-03..QF-08 unit tests; Verdict-aware callers use `analyzeResponse` directly. CONTEXT.md §"Claude's Discretion" says "호출자 한 곳이라 영향 적음" — even less impact when the legacy name keeps working.

### Refusal-default policy (CONTEXT.md vs ROADMAP SC#3)

ROADMAP SC#3 / QSE-03 wording suggests adding refusal patterns (e.g., "I cannot", "As an AI") to default BadKeywords. CONTEXT.md §"Refusal pattern default 정책" explicitly forbids this (false positive risk; operator opt-in is safer). Plan 15-01 honors CONTEXT.md — default BadKeywords stays `["TODO", "I think"]`. QSE-03 acceptance criterion is satisfied by capability (case-insensitive matchKeyword supports refusal patterns once operator adds them to BadKeywords) rather than silent default expansion. README §7 opt-in guidance added in Plan 15-03.

### Silent-enable defaults policy

Both new config keys (BadFinishReasons, EntropyThreshold) are silently enabled by default per CONTEXT.md §"Silent enable". Existing operators who upgrade without touching appsettings.json will automatically get finish_reason and entropy checks. This is intentional: "더 똑똑해진" (smarter) experience without requiring operator action. CHANGELOG entry in Plan 15-03.

## Test Baseline Confirmation

Phase 14 baseline (88 passed, 16 ignored, 0 failed) preserved. The QF-03..07 construction site updates add the two new fields with neutral values (BadFinishReasons=[||], EntropyThreshold=0.0) — since `isBadResponse` body is unchanged in this plan, these values are present in the record but unused. No behavior change. Full test run confirmed.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] QualityFallbackTests.fs construction sites broke after record extension**

- **Found during:** Task 1 — build verification after QualityCheck.fs change
- **Issue:** F# record extension to 5 fields causes FS0764 "no assignment for BadFinishReasons" at all QF-03..07 test construction sites (5 sites in QualityFallbackTests.fs)
- **Fix:** Added `BadFinishReasons = [||]; EntropyThreshold = 0.0` to all 5 sites. Values are neutral (isBadResponse doesn't use the new fields yet). No behavior change.
- **Files modified:** `tests/SmartRouter.Tests/QualityFallbackTests.fs`
- **Commit:** 8d5408e (included in Task 1 commit alongside QualityCheck.fs and CompositionRoot.fs)

**Note:** Plan structure said Task 1 = QualityCheck.fs and Task 2 = CompositionRoot.fs + appsettings.json. In practice the record extension causes CompositionRoot.fs to break simultaneously with QualityCheck.fs. Both were staged in commit 1 (Task 1) for a clean build. Commit 2 (Task 2) holds only the appsettings.json change, which is semantically independent (config defaults) and builds cleanly on its own.

## Next Phase Readiness

Plan 15-02 can now:
- Import `Verdict`, `BadReason`, `extractFinishReason`, `charEntropy` from `QualityCheck`
- Write `analyzeResponse : QualityFallbackOptions -> string option -> string -> Verdict` using the 5 helpers in cheap-first order
- Replace `isBadResponse` body with a wrapper
- Update `ChatCompletions.fs` caller to use `analyzeResponse` and capture `bad_reason` for trace

No blockers. All types, helpers, config keys, and DI wiring are in place.
