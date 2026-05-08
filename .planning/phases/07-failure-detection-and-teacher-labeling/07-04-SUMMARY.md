---
phase: 07-failure-detection-and-teacher-labeling
plan: "04"
subsystem: data-pipeline
tags: [fsharp, channel, background-service, jsonl, dedup, back-pressure]

# Dependency graph
requires:
  - phase: 07-01-foundation
    provides: "HardCaseDatasetWriter stub (BackgroundService + IHardCaseDatasetWriter interface), HardCaseDatasetOptions record, HardCaseEntry type in RetrainingPorts.fs"
  - phase: 05-routing-decision-logging
    provides: "DecisionLogWriter.fs — structural mirror for Channel + BackgroundService + per-line FlushAsync + graceful drain pattern"
provides:
  - "HardCaseDatasetWriter real implementation: Channel<HardCaseEntry> + BackgroundService consumer + dedupe HashSet + per-line FlushAsync + graceful drain"
  - "BoundedChannelFullMode.Wait enforced (training data back-pressure, never DropWrite)"
  - "Append-time dedupe by (CorrelationId|PromptHash) via HashSet<string> seeded from existing JSONL at startup"
  - "FileShare.None single-writer guarantee on datasets/hard-cases.jsonl"
affects:
  - "07-05: DI wiring (AddSingleton + AddHostedService triple-registration, mirrors DecisionLogWriter)"
  - "07-06: HardCaseDatasetTests — concurrency, idempotency, graceful drain"
  - "Phase 8: RetrainingService BackgroundService injects IHardCaseDatasetWriter"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Channel<T> + BackgroundService consumer: same architecture as DecisionLogWriter (Phase 5); single consumer per file for lock-free I/O"
    - "BoundedChannelFullMode.Wait for critical data paths (vs DropWrite for hot logging paths)"
    - "HashSet<string> dedupe key pattern: CorrelationId|PromptHash string concat; safe because both are hex strings (no | character)"
    - "Lazy StreamWriter open: getWriter() only touches the file on first write; idle consumers never open file"

key-files:
  created: []
  modified:
    - "src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs — 154 lines added; stub bodies replaced with real Channel + drain implementation"

key-decisions:
  - "BoundedChannelFullMode.Wait chosen over DropWrite — losing training labels is unacceptable; back-pressure is acceptable because hard-case writes are infrequent (one per teacher round-trip, ~30s)"
  - "HashSet<string> keyed on sprintf '%s|%s' CorrelationId PromptHash — simple, BCL-only; pipe separator safe because both fields are hex strings"
  - "Dedupe seeded from existing file at ExecuteAsync startup (not constructor) — constructor is sync; file I/O belongs in the async task body"
  - "FileShare.None open exclusively — in-process Channel already guarantees single writer; FileShare.None defends against accidental dual-process invocations during testing"
  - "getWriter() lazy-open pattern — BackgroundService that receives no messages never opens the file; important for tests that start/stop without producing entries"

patterns-established:
  - "Critical data Channel: BoundedChannelFullMode.Wait (vs hot-path logging: DropWrite)"
  - "Startup file-seed: read existing JSONL at ExecuteAsync start to initialize dedupe set before accepting writes"

# Metrics
duration: 6min
completed: 2026-05-08
---

# Phase 7 Plan 04: Dataset Writer Summary

**Channel-backed BackgroundService for datasets/hard-cases.jsonl with BoundedChannelFullMode.Wait, HashSet dedupe seeded from existing file, and FileShare.None single-writer guarantee — direct structural mirror of DecisionLogWriter**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-05-08T12:31:40Z
- **Completed:** 2026-05-08T12:37:40Z
- **Tasks:** 1
- **Files modified:** 1

## Accomplishments
- Replaced 07-01 stub (11 lines) with full 165-line Channel + BackgroundService implementation
- BoundedChannelFullMode.Wait enforced — load-bearing diff from DecisionLogWriter; training data must not be dropped silently
- Append-time dedupe via HashSet<string> seeded from existing JSONL file at startup — prevents double-labeling on rerun
- FileShare.None open mode for single-writer guarantee within and across processes
- Lazy StreamWriter open — idle service (no messages received) never touches disk
- Graceful drain loop after stoppingToken cancellation, mirrors DecisionLogWriter Phase 5 Pitfall P2 pattern

## Task Commits

1. **Task 1: Implement HardCaseDatasetWriter (Channel + BackgroundService + dedupe + per-line append)** - `5127178` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` — replaced 07-01 stub; module name, HardCaseDatasetOptions record, BackgroundService inheritance, and constructor signature preserved unchanged; AppendAsync + ExecuteAsync + StopAsync bodies fully implemented

## Decisions Made
- BoundedChannelFullMode.Wait over DropWrite: training labels are irreplaceable; back-pressure acceptable (writes at ~1/teacher-call cadence, not hot path)
- HashSet<string> with `"CorrelationId|PromptHash"` key: pipe is safe delimiter (both fields are hex strings), BCL-only, no struct tuple complexity
- Dedupe seeded in ExecuteAsync (not constructor): constructor is synchronous; file I/O belongs in async task body
- FileShare.None: defends against accidental dual-process dataset corruption during testing/scripting
- Lazy getWriter() pattern: avoids touching disk when BackgroundService starts but receives no entries (test-safe)

## Deviations from Plan

None — plan executed exactly as written. The plan provided the complete F# source; implementation matched verbatim.

## Issues Encountered

None. Build: 0 errors, 0 warnings. Tests: 50 passed + 10 ignored (Phase 6 baseline unchanged).

## Build / Test Status

| Check | Result |
|-------|--------|
| `dotnet build SmartRouter.slnx` | 0 errors, 0 warnings |
| `grep NotImplementedException HardCaseDatasetWriter.fs` | 0 hits (stub fully replaced) |
| `grep BoundedChannelFullMode.Wait HardCaseDatasetWriter.fs` | 2 hits (comment + code) |
| `grep BoundedChannelFullMode.DropWrite HardCaseDatasetWriter.fs` | 0 hits |
| Channel API surface (WriteAsync/ReadAsync/TryComplete/TryRead) | 4 hits |
| HashSet / dedupe / seedDedupe | 14+ hits |
| Tests passed | 50/50 |
| Tests ignored | 10/10 |

## User Setup Required

None — no external service configuration required. DI registration lands in Plan 07-05; tests land in Plan 07-06.

## Next Phase Readiness
- HardCaseDatasetWriter is ready for DI wiring in Plan 07-05 (AddSingleton + AddHostedService triple-registration, identical to DecisionLogWriter pattern)
- Plan 07-06 can write HardCaseDatasetTests against this implementation (concurrency, idempotency, graceful drain)
- Plans 07-02 (FailureDetector) and 07-03 (TeacherLabeler) own their stubs in parallel — no file conflict possible
- No blockers for remaining Wave 2 / Wave 3 plans

---
*Phase: 07-failure-detection-and-teacher-labeling*
*Completed: 2026-05-08*
