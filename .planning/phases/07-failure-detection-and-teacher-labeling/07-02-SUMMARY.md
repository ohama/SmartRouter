---
phase: 07-failure-detection-and-teacher-labeling
plan: 02
subsystem: retraining-pipeline
tags: [fsharp, jsonl, failure-detection, fallback-filter, serilog, system-text-json, snake-case]

# Dependency graph
requires:
  - phase: 07-01-foundation
    provides: "IFailureDetector port + HardCase type in Core/RetrainingPorts.fs; FailureDetector.fs stub with constructor signature (logsDirectory: string)"
  - phase: 05-routing-decision-logging
    provides: "DecisionLog record with fallback_used field; JSONL written by DecisionLogWriter with SnakeCaseLower + JsonFSharpConverter serializer"
provides:
  - "FailureDetector.ExtractHardCases: reads logs/decisions/*.jsonl, filters fallback_used=true, returns HardCase list"
  - "Malformed-line tolerance: per-line try/catch logs Warning and skips bad lines"
  - "Missing-directory handling: Information log + return [] (no exception)"
  - "Per-file I/O isolation: one unreadable file does not abort the scan"
  - "Empty-result operator message referencing Phase 10 + seed script"
  - "jsonOpts mirroring DecisionLogWriter: SnakeCaseLower + JsonFSharpConverter for correct round-trip"
affects:
  - "07-05: DI wiring registers FailureDetector(logsDirectory) — constructor signature confirmed final"
  - "07-06: FailureDetector tests use this implementation"
  - "Phase 8: BackgroundService injects IFailureDetector; empty-result behavior until Phase 10"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "JSONL scan pattern: Directory.GetFiles + File.ReadAllLines + List.choose tryParseLine"
    - "Graceful degradation: missing dir + malformed lines both log-and-continue, never throw"
    - "JSON options mirror: FailureDetector.jsonOpts intentionally identical to DecisionLogWriter.jsonOptions (SnakeCaseLower + JsonFSharpConverter)"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Adapters/FailureDetector.fs

key-decisions:
  - "PromptText=None from detector: LOG-01 stores only prompt_hash; Phase 8 BackgroundService provides prompt text inline; offline CLI and seed script cover the gap"
  - "_ct underscore prefix on CancellationToken: accepted but not propagated to sync File.ReadAllLines (millisecond I/O at v1 scale; avoids unused-binding warning)"
  - "per-file try/catch isolation: one permission-denied or transient error on a single file does not abort full scan"
  - "Empty-result Info message explicitly names Phase 10 and seed script for operator visibility"

patterns-established:
  - "JSONL reader pattern: tryParseLine returns option, List.choose filters, per-line and per-file isolation separate concerns"

# Metrics
duration: 2min
completed: 2026-05-08
---

# Phase 7 Plan 02: Failure Detector Summary

**FailureDetector reads logs/decisions/*.jsonl, deserializes via SnakeCaseLower+JsonFSharpConverter, filters fallback_used=true, returns HardCase list with graceful malformed-line and missing-directory handling.**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-05-08T12:24:34Z
- **Completed:** 2026-05-08T12:26:17Z
- **Tasks:** 1 of 1
- **Files modified:** 1

## Accomplishments

- Replaced Wave-1 NotImplementedException stub with real JSONL reader in FailureDetector.ExtractHardCases
- Malformed JSON lines logged at Warning and skipped — a single bad line does not derail the scan
- Missing logs directory handled gracefully (Information log + return empty list, no exception)
- Per-file I/O isolation: one unreadable file logged at Warning; scan continues on remaining files
- Empty-result operator message explicitly references Phase 10 + seed script (PITFALL-1 mitigation from RESEARCH.md)
- jsonOpts mirrors DecisionLogWriter exactly (SnakeCaseLower + JsonFSharpConverter) for correct JSONL round-trip
- PromptText=None per LOG-01 privacy decision; downstream callers (Phase 8 BackgroundService, seed script) handle the gap

## Task Commits

1. **Task 1: Implement FailureDetector.ExtractHardCases** - `3546b2f` (feat)

**Plan metadata:** (docs commit follows this summary)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/FailureDetector.fs` - Full JSONL reader replacing Wave-1 stub; 79 insertions replacing 4 stub lines

## Decisions Made

- `_ct` underscore prefix on CancellationToken: accepted but not forwarded to synchronous `File.ReadAllLines`; JSONL reads complete in milliseconds at v1 scale; propagating via async file streaming is unjustified complexity; underscore prevents F# unused-binding warning (consistent with stub established in 07-01)
- PromptText=None is correct for this adapter; the comment in code explicitly explains the two paths (Phase 8 in-process vs seed script) so future readers understand the None is intentional
- Log.Warning (not Log.Error) for malformed lines — a single bad line is noteworthy but not alarming; Error-level would be misleading for a single corrupt line in an otherwise healthy log file

## Deviations from Plan

None - plan executed exactly as written. The F# source provided in the `<action>` block compiled cleanly on first attempt with 0 warnings.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- FailureDetector implementation is complete and build-verified; constructor signature `(logsDirectory: string)` confirmed final for Plan 07-05 DI wiring
- fallback_used filter is live; returns empty list until Phase 10 ships (correct behavior per CONTEXT.md decisions)
- Plan 07-05 (DI wiring) and Plan 07-06 (FailureDetector tests) can proceed without any further changes to this file
- Build: 0 errors, 0 warnings (TreatWarningsAsErrors=true confirmed clean)
- Tests: 50 passed + 10 ignored (Phase 6 baseline unchanged; FailureDetector tests land in 07-06)

---
*Phase: 07-failure-detection-and-teacher-labeling*
*Completed: 2026-05-08*
