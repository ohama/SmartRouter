---
phase: 16-122b-as-judge-for-borderline-cases
plan: "01"
subsystem: routing
tags: [fsharp, bcl, borderline-detection, quality-check, entropy, effective-length]

# Dependency graph
requires:
  - phase: 15-quality-signal-enrichment
    provides: QualityFallbackOptions (EntropyThreshold + MinResponseLength fields), charEntropy public helper, analyzeResponse returning Verdict.Good|Bad

provides:
  - BorderlineKind DU (UncertainEntropy of float | UncertainLength of int)
  - classifyBorderline : QualityFallbackOptions -> string -> BorderlineKind option
  - BCL-only Adapters/BorderlineClassifier.fs registered in fsproj compile order

affects:
  - 16-02-judge-client (consumes BorderlineKind in judge wiring)
  - 16-03-wiring (calls classifyBorderline between analyzeResponse and judge)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "OQ #1 resolution: hard-code band widths (EntropyThreshold+1.0, MinResponseLength*1.5) before empirical data; expose config knobs only when operators demand them"
    - "OQ #2 resolution: re-implement private Phase 15 helpers inline (koreanRatio, effectiveLength) rather than broadening QualityCheck.fs public surface"
    - "Borderline as Good qualifier: BorderlineKind is separate from Verdict DU — avoids cascade of pattern-match updates in ChatCompletions.fs"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj

key-decisions:
  - "OQ #1 hard-code: entropy upper band = EntropyThreshold + 1.0; length upper band = MinResponseLength * 1.5 (not config keys)"
  - "OQ #2 inline: koreanRatio + effectiveLength re-implemented as private helpers in BorderlineClassifier.fs; QualityCheck.fs visibility unchanged"
  - "BorderlineKind DU is separate from QualityCheck.Verdict — avoids 6 ChatCompletions.fs match-arm updates"
  - "Keyword and finish_reason excluded from borderline: binary signals, no natural partial zone"
  - "charEntropy is module-public in QualityCheck.fs (not private) — reused directly via open import"

patterns-established:
  - "Cascade cheap-first: entropy band checked before length band (mirrors Phase 15 analyzeResponse order)"
  - "Defensive String.IsNullOrEmpty guard: returns None immediately to avoid bogus UncertainLength 0"
  - "Band-edge semantics: half-open intervals [lower, upper) — lower inclusive, upper exclusive"

# Metrics
duration: 4min
completed: 2026-05-11
---

# Phase 16 Plan 01: Borderline Classifier Summary

**BCL-only BorderlineClassifier.fs with UncertainEntropy/UncertainLength DU and cheap-first entropy-then-length band detection, resolving researcher open questions OQ #1 (hard-code bands) and OQ #2 (inline helpers)**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-05-10T15:10:29Z
- **Completed:** 2026-05-10T15:14:06Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Created `src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs` (77 lines, BCL-only)
- Defined `BorderlineKind` DU with `UncertainEntropy of float` and `UncertainLength of int` cases
- Implemented `classifyBorderline` with cheap-first cascade: entropy band [T, T+1.0) then length band [L, L*1.5)
- Registered in fsproj at compile position 24 (after QualityCheck.fs line 23, before ChatCompletions.fs line 59)
- Build: 0 warnings, 0 errors under TreatWarningsAsErrors=true
- Test baseline preserved: 102 passed, 16 ignored, 0 failed

## Task Commits

Each task was committed atomically:

1. **Task 1 + Task 2: Create BorderlineClassifier.fs + fsproj registration** - `014bf5b` (feat)

**Plan metadata:** (docs commit follows this summary)

## Files Created/Modified
- `src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs` — New module: BorderlineKind DU + classifyBorderline function (77 lines)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Added `<Compile Include="Adapters/BorderlineClassifier.fs" />` at line 24

## Decisions Made

**OQ #1 — Band widths (hard-code vs config):** HARD-CODE.
- Entropy upper = `EntropyThreshold + 1.0`
- Length upper = `int (float opts.MinResponseLength * 1.5)`
- Rationale: avoid over-configuration before empirical data; Phase 14/15 precedent of shipping defaults first; if operators demand tuning knobs, Phase 17 can add `Routing.Judge.EntropyBandWidth` / `Routing.Judge.LengthBandFactor`.

**OQ #2 — charEntropy/effectiveLength visibility:** RE-IMPLEMENT INLINE.
- `koreanRatio` and `effectiveLength` declared `private` in QualityCheck.fs — do not change visibility.
- Inline 5-line copies in BorderlineClassifier.fs (pure functions, divergence not a concern at this scale).
- `charEntropy` is module-public in QualityCheck.fs (Phase 15 left it public for testability) — reused directly via `open SmartRouter.Cli.Adapters.QualityCheck`.

**Architectural separation:**
- `BorderlineKind` is a separate DU from `QualityCheck.Verdict` (NOT a third Verdict case).
- Rationale: avoids 6 ChatCompletions.fs pattern-match arm updates; borderline is a qualifier on Good, not a third verdict category.
- Architecture: `analyzeResponse → if Good → classifyBorderline → if Some → judge → decide`.

**Exclusions:**
- Keyword dimension: binary match (present/absent) — no natural partial zone.
- finish_reason dimension: decisive signals — not a "maybe bad" case.

## Deviations from Plan

None - plan executed exactly as written.

Note: 16-02 (JudgeClient) ran concurrently and also added a line to fsproj. The Edit-not-Write approach preserved both edits correctly: line 24 = BorderlineClassifier.fs, line 25 = JudgeClient.fs (from 16-02).

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- `classifyBorderline` ready for consumption by 16-03 (wiring plan)
- No call sites yet — this is a pure leaf adapter
- Phase 16's full pipeline: `analyzeResponse → if Good → classifyBorderline → if Some borderlineKind → judgeClient.Judge → decide`

---
*Phase: 16-122b-as-judge-for-borderline-cases*
*Completed: 2026-05-11*
