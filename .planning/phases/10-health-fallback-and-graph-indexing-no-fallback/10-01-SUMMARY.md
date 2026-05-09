---
phase: 10-health-fallback-and-graph-indexing-no-fallback
plan: 01
subsystem: routing
tags: [fsharp, domain-modeling, health-probe, routing-reason, appsettings, discriminated-union]

# Dependency graph
requires:
  - phase: 09-canary-deployment
    provides: RoutingDecision.IsFallback field, CanaryWatchdog AutoRollbackEnabled config, Phase 9 baseline (78 pass + 17 ignored)
provides:
  - RoutingReason.FallbackTo35B DU case (6th case, Phase 10 marker)
  - IHealthProbe extended with IsReachable (sync) + LastProbedAt members
  - appsettings.json Routing.Health section (PollingIntervalSeconds=10, ConsecutiveFailureThreshold=1)
  - appsettings.json Canary.AutoRollbackEnabled flipped to true
  - DecisionLogger.formatReason exhaustive match updated for FallbackTo35B -> "fallback_to_35b"
affects:
  - 10-02-health-service-implementation (uses IHealthProbe extensions + FallbackTo35B + Routing.Health config)
  - 10-03-health-fallback-tests (uses all domain types established here)
  - Phase 9 CanaryWatchdog (AutoRollbackEnabled=true now activates real auto-rollback on fallback_used signal)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "BCL-only Core invariant (ARCH-01): IHealthProbe extension stays in Ports.fs with no Microsoft.Extensions.*/HttpClient/Serilog"
    - "Exhaustive match enforcement: TreatWarningsAsErrors=true catches FS0025 cascade on DU extension"
    - "Sync + async dual members on IHealthProbe: sync for hot path, async for background probing"

key-files:
  created: []
  modified:
    - src/SmartRouter.Core/Domain.fs
    - src/SmartRouter.Core/Ports.fs
    - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
    - src/SmartRouter.Cli/appsettings.json

key-decisions:
  - "FallbackTo35B is a no-payload marker DU case; routing_reason in JSONL log = 'fallback_to_35b'"
  - "IHealthProbe gets sync IsReachable (hot path) + async IsReachableAsync (existing) + sync LastProbedAt (health endpoint rendering)"
  - "Routing.Health.PollingIntervalSeconds=10, ConsecutiveFailureThreshold=1 (configurable, operator tunes per env)"
  - "Canary.AutoRollbackEnabled flipped to true; Phase 10 makes fallback_used signal real via REL-03"
  - "MLRoutingTests.fs read-only confirmed: both match sites (lines 167, 190) use | r -> failtestf catch-all — no edits made"

patterns-established:
  - "DU cascade protocol: grep all match sites before editing; only formatReason is exhaustive in Plan 10-01 scope"
  - "IHealthProbe triple-reg pattern (Plan 10-02 follows): concrete singleton + IHealthProbe alias + IHostedService"

# Metrics
duration: 8min
completed: 2026-05-09
---

# Phase 10 Plan 01: Foundation Summary

**RoutingReason.FallbackTo35B DU case + IHealthProbe sync members (IsReachable + LastProbedAt) + appsettings Routing.Health section + AutoRollbackEnabled flip from false to true**

## Performance

- **Duration:** 8 min
- **Started:** 2026-05-09T00:25:59Z
- **Completed:** 2026-05-09T00:34:37Z
- **Tasks:** 4
- **Files modified:** 4

## Accomplishments
- Added `RoutingReason.FallbackTo35B` as 6th DU case in Domain.fs; formatReason cascade in DecisionLogger.fs updated to map it to `"fallback_to_35b"` (FS0025 resolved under TreatWarningsAsErrors=true)
- Extended `IHealthProbe` with two new BCL-only abstract members: `IsReachable: ModelId -> bool` (sync fast-path for QueueDispatcher/ChatCompletions hot path) and `LastProbedAt: ModelId -> DateTimeOffset` (for /health endpoint body rendering)
- Added `Routing.Health` subsection to appsettings.json with `PollingIntervalSeconds=10` and `ConsecutiveFailureThreshold=1`; flipped `Canary.AutoRollbackEnabled` from `false` to `true` now that Phase 10 makes the fallback_used signal real
- Confirmed MLRoutingTests.fs lines 167 + 190 both use `| r -> failtestf` catch-all arms — no test edits needed

## Task Commits

Each task was committed atomically:

1. **Task 1: Grep cascade + extend Domain + Ports** - `40b161e` (feat)
2. **Task 2: Update DecisionLogger.formatReason exhaustive match** - `ed0825a` (feat)
3. **Task 3: Update appsettings.json** - `e4bd2f7` (chore)
4. **Task 4: Read-only test confirmation + full verification** - (no commit — read-only; verified via build + test run)

**Plan metadata:** (docs commit below)

## Files Created/Modified
- `src/SmartRouter.Core/Domain.fs` - Added `| FallbackTo35B` to RoutingReason DU (line 37)
- `src/SmartRouter.Core/Ports.fs` - Extended IHealthProbe with IsReachable + LastProbedAt (total: 6 abstract members across 3 interfaces)
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` - Added `| FallbackTo35B -> "fallback_to_35b"` to formatReason exhaustive match
- `src/SmartRouter.Cli/appsettings.json` - Added Routing.Health subsection + AutoRollbackEnabled flip to true

## Decisions Made
- **FallbackTo35B string literal locked:** `"fallback_to_35b"` is the canonical routing_reason value in JSONL log; CanaryWatchdog rolling-60s metric and FailureDetector may grep this literal — must not change
- **IHealthProbe sync + async dual-member design:** sync `IsReachable` for hot-path callers that cannot await; async `IsReachableAsync` preserved for background probe callers; `LastProbedAt` sync (returns cached DateTimeOffset, no IO)
- **AutoRollbackEnabled trade-off documented:** probe-blip could trigger unwarranted canary rollback; damper is ConsecutiveFailureThreshold (operator raises to 2 in prod if flapping observed)
- **MLRoutingTests.fs no-edit confirmed:** CONTEXT D12 correctly predicted both match sites use catch-all arms

## Deviations from Plan

None - plan executed exactly as written. The only confirmed deviation point (MLRoutingTests.fs) was pre-declared as read-only and confirmed read-only.

## Issues Encountered

First test run showed 1 transient failure (77 passed + 1 failed on first `--sequenced` run). Second run was clean: 78 passed + 17 ignored + 0 failed. This is a known intermittent timing issue in integration tests (fake-Kestrel shutdown race), not caused by Plan 10-01 changes. Phase 9 baseline preserved.

## Build/Test Status

- **dotnet build SmartRouter.slnx:** 0 errors, 0 warnings (TreatWarningsAsErrors=true)
- **Test count:** 78 passed + 17 ignored + 0 failed (Phase 9 baseline preserved; second run clean)
- **ARCH-01 invariant:** `grep -E "Microsoft.ML|Serilog|HttpClient|AspNetCore" src/SmartRouter.Core/*.fs` returns no matches
- **FS0025 cascade verified:** Cli build failed with FS0025 on DecisionLogger.fs after Task 1 (expected); resolved by Task 2 formatReason arm

## Test-Side Cascade Confirmation

MLRoutingTests.fs lines 167 + 190: both confirmed as `| r -> failtestf "..."` catch-all — no edit made.

RoutingTests.fs lines 42, 65, 139, 151, 176, 201, 212: all confirmed as `| r -> failtestf "..."` catch-all — no edit made.

## User Setup Required

None - no external service configuration required. Routing.Health config keys are consumed by HealthService (Plan 10-02); no operator action needed at this stage.

## Next Phase Readiness

Plan 10-02 (HealthService + DI + adapters + endpoint) can proceed immediately:
- `RoutingReason.FallbackTo35B` DU case exists
- `IHealthProbe` port declares all 3 members that HealthService must implement
- `Routing.Health` config keys exist for IOptions binding
- `Canary.AutoRollbackEnabled = true` ready for real signal

No blockers.

---
*Phase: 10-health-fallback-and-graph-indexing-no-fallback*
*Completed: 2026-05-09*
