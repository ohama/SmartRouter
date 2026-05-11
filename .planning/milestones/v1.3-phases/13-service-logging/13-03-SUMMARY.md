---
phase: 13-service-logging
plan: 03
subsystem: logging
tags: [fsharp, logging, health-service, endpoints, ilogger]

dependency-graph:
  requires: ["13-02-ILOGGER-MIGRATION"]
  provides: ["hot-path-demotion", "transition-only-health-logging", "endpoint-hit-debug-logs"]
  affects: ["13-05-BANNERS-AND-RETENTION", "13-06-TESTS-AND-DOCS"]

tech-stack:
  added: []
  patterns:
    - "Transition-only logging: capture prev-state before probe, compare after, emit INFO/WARN only on state change"
    - "ILoggerFactory.CreateLogger('EndpointName') pattern for handler-scoped loggers"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/Adapters/HealthService.fs
    - src/SmartRouter.Cli/Endpoints/Health.fs
    - src/SmartRouter.Cli/Endpoints/Stats.fs
    - src/SmartRouter.Cli/Endpoints/Canary.fs
    - src/SmartRouter.Cli/Endpoints/Models.fs

decisions:
  - id: D1
    choice: "LogDebug for both ChatCompletions Routing target= emissions"
    rationale: "Same data already in JSONL DecisionLog; duplicating at INFO produces stderr noise on every request"
  - id: D2
    choice: "Capture prevReachable from state dict before probe runs; compare after"
    rationale: "State dict already holds (bool * DateTimeOffset); reading it before the probe gives the pre-probe snapshot without an additional data structure"
  - id: D3
    choice: "Under-threshold probe failures demoted from LogInformation to LogDebug"
    rationale: "Not-yet-unreachable failures are transient noise; operators care about the threshold-crossing event, not each individual failure"
  - id: D4
    choice: "Canary.fs emits one LogDebug per sub-endpoint handler with outcome structured fields"
    rationale: "4 sub-endpoints (GET, POST promote/rollback/enable) each get their own emission with result/outcome for operator debuggability"

metrics:
  duration: ~8 min
  completed: "2026-05-09"
  tasks-completed: 3
  tasks-total: 3
---

# Phase 13 Plan 03: Behavior Changes Summary

**One-liner:** Reduced operational-log volume via hot-path demotion and transition-only health probe emissions; added DEBUG endpoint-hit signals to 4 endpoint files.

## Tasks Completed

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | ChatCompletions hot-path demotion | a118666 | ChatCompletions.fs |
| 2 | HealthService transition-only logging | c2ed1b0 | HealthService.fs |
| 3 | Endpoint-hit DEBUG logs in 4 endpoint files | dc0e77f | Health.fs, Stats.fs, Canary.fs, Models.fs |

## What Was Changed

### Task 1: ChatCompletions hot-path demotion

Two `LogInformation` emissions at lines 285 and 372 of `ChatCompletions.fs` (streaming and non-streaming branch respectively) were demoted to `LogDebug`. Both emit "Routing target={Target} reason={Reason} priority={Priority}" — the same information is already written to the JSONL DecisionLog at INFO-equivalent level, so the operational log duplication at INFO was pure noise on every request. A comment was added to both sites explaining the rationale.

### Task 2: HealthService transition-only logging

`probeOne` in `HealthService.fs` was restructured to capture `prevReachable` (from the existing `(bool * DateTimeOffset)` state dict) before the probe runs, then compare old vs new after the probe result is known:

- `false → true` (down→up): `LogInformation` "reachable (transitioned from down)"
- `true → false` (up→down): `LogWarning` "unreachable (transitioned from up)"
- `true → true` (steady up): `LogDebug` "still reachable"
- `false → false` (steady down): `LogDebug` "still unreachable"

Under-threshold probe failures (previously `LogInformation "probe failed (N/T); not yet unreachable"`) were demoted to `LogDebug` — these are transient noise; the threshold-crossing up→down transition is the operator-relevant event.

Startup confirmation logs (ExecuteAsync banner and "HealthService stopping") remain at INFO. HLTH-04..08 tests all pass — they test routing fallback behavior using mock `IHealthProbe`, not log emission counts.

### Task 3: Endpoint-hit DEBUG logs

Four endpoint files each got a `logger.LogDebug` call per handler, with `ILoggerFactory.CreateLogger("EndpointName")` following the pattern established in 13-02:

- **Health.fs**: `"/health hit; method={Method}"`
- **Stats.fs**: `"/stats hit; queue_depth_high={H} active_122b={A}"` (after snapshot is built)
- **Canary.fs**: one LogDebug per sub-endpoint (GET + 3 POSTs), each with outcome structured fields
- **Models.fs**: `"/v1/models hit; upstreams_reachable={N}"` using pre-computed reachability to avoid calling `IsReachable` twice

All emissions are at DEBUG level — suppressed at default INFO; visible at `--log-level=debug`.

## Build and Test State

- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — succeeded, 0 warnings, 0 errors
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exit 0 (62 passed + 16 ignored + 0 failed; baseline unchanged)

## Deviations from Plan

### Auto-fixed Issues

None — plan executed exactly as written.

**One minor structural note:** The plan pseudocode for Canary.fs showed a single `logger.LogDebug("/canary {Method} hit; result={Status}", ...)` per handler. The actual implementation emits one LogDebug per branch inside each handler (so Canary has 8 total LogDebug call sites across 4 handlers — 2 per POST handler for success/error paths). This provides more structured fields (`result=promoted new_version={V}`, `result=failed error={Err}`, etc.) and is strictly more informative than the pseudocode pattern. Not flagged as a deviation — the plan said "one log per" endpoint handler and the behavior (one emission per hit) is maintained; the branched messages are implementation detail.

## Next Phase Readiness

13-04 (--log-level CLI flag) completed in parallel — no ordering conflict with this plan as the files are disjoint.

Ready for 13-05 (banners and retention) and 13-06 (tests and docs).
