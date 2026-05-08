---
phase: 04-ml-algorithm-seam
plan: "03"
subsystem: testing
tags: [expecto, fsharp, ml-routing, di, configuration, testsequenced]

# Dependency graph
requires:
  - phase: 04-01
    provides: RoutingAlgorithm type alias, ML.fs placeholder, Heuristic.fs extracted
  - phase: 04-02
    provides: Routing.Algorithm config key, RoutingAlgorithm DI singleton, --routing-algorithm CLI flag
provides:
  - 5 ML routing tests in MLRoutingTests.fs covering ML-01..04 + algorithm dispatch seam
  - appsettings.json bin-copy via fsproj <None Include> for Tests 4+5 DI/config tests
  - Phase 4 verification complete — all 44 tests pass
affects:
  - phase: 05-decision-logging
  - phase: 06-real-ml-classifier

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "testSequenced wrapping for all Phase-4 test lists (CONTEXT.md locked decision)"
    - "AddJsonFile + AddInMemoryCollection layering for DI config tests (last-wins override pattern)"
    - "appsettings.json bin-copy via <None Include><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory> for integration-style unit tests"

key-files:
  created:
    - tests/SmartRouter.Tests/MLRoutingTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "appsettings.json bin-copied via fsproj <None Include> (rejected: inline AddInMemoryCollection for full Routing section — too verbose and drifts)"
  - "MLRoutingTests.fs is a NEW file (not appended to RoutingTests.fs) — phase ownership clarity"
  - "testSequenced wraps entire MLRoutingTests list (CONTEXT.md locked; uniform discipline even for pure tests)"
  - "mkReq helper copied private into MLRoutingTests.fs (avoids cross-module private visibility dependency)"

patterns-established:
  - "DI dispatch test pattern: ServiceCollection + ConfigurationBuilder.AddJsonFile.AddInMemoryCollection + configureServices + GetRequiredService<RoutingAlgorithm>"
  - "CLI override test pattern: same as DI dispatch but AddInMemoryCollection comes AFTER AddJsonFile (last-wins = CLI wins)"

# Metrics
duration: 3min
completed: 2026-05-08
---

# Phase 4 Plan 03: ML Routing Tests Summary

**5 testSequenced tests in MLRoutingTests.fs prove the ML algorithm seam end-to-end: type alias parity, placeholder behavior, routeRequest dispatch, DI config dispatch, and CLI override**

## Performance

- **Duration:** 3 min
- **Started:** 2026-05-08T04:38:46Z
- **Completed:** 2026-05-08T04:41:11Z
- **Tasks:** 2
- **Files modified:** 3 (created 1, modified 2)

## Accomplishments

- Authored `MLRoutingTests.fs` with 5 tests covering all Phase 4 success criteria (ML-01..04)
- Wired appsettings.json bin-copy in fsproj so Tests 4+5 can call `AddJsonFile("appsettings.json")` and satisfy `validateConfig`
- 44/44 tests pass (39 baseline + 5 new ML tests), 2 ignored (load tests), 0 failed
- `check-routing-isolation.sh` and `check-no-async.sh` both exit 0
- Phase 4 all success criteria satisfied

## Task Commits

1. **Task 1: Author MLRoutingTests.fs** - `f0cb3b5` (feat)
2. **Task 2: Wire fsproj + RouterTests rootTests + appsettings.json bin-copy** - `58f795b` (feat)

**Plan metadata:** (see below — docs commit)

## Test Coverage (ML-01..04)

| Test | Criteria | Assertion |
|------|----------|-----------|
| Test 1 | ML-01: RoutingAlgorithm type alias parity | Both applyHeuristic and applyML assignable to RoutingAlgorithm binding; ML returns Qwen35B |
| Test 2 | ML-02: Placeholder behavior | applyML returns Qwen35B/Low/ML/IsFallback=false for ANY input (3 varied cases) |
| Test 3 | ML-02 dispatch | routeRequest with applyML → Reason=ML; with applyHeuristic → Reason≠ML (proves parameter drives dispatch) |
| Test 4 | ML-02 DI config | CompositionRoot.configureServices with Routing:Algorithm=ml → RoutingAlgorithm resolves to ML.applyML |
| Test 5 | ML-03 CLI override | AddInMemoryCollection after AddJsonFile → later layer wins → Reason=ML even with baseline Algorithm=heuristic |

ML-04 isolation: `scripts/check-routing-isolation.sh` exits 0 (Heuristic.fs and ML.fs have zero cross-imports).

## Files Created/Modified

- `tests/SmartRouter.Tests/MLRoutingTests.fs` — New test module with 5 testSequenced tests; 123 lines
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — MLRoutingTests.fs added to Compile list + appsettings.json <None Include> entry
- `tests/SmartRouter.Tests/RouterTests.fs` — SmartRouter.Tests.MLRoutingTests.tests added to rootTests list

## Decisions Made

- **appsettings.json bin-copy approach confirmed.** The alternative (build full Routing section inline in AddInMemoryCollection) was explicitly rejected per CONTEXT.md — it drifts from real config when appsettings.json changes. The `<None Include><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>` fsproj entry copies the canonical file to the test bin directory at build time.
- **mkReq helper copied private.** RoutingTests.fs declares mkReq as `let private`. Rather than change its visibility (test surface change) or use a shared module (new file, over-engineering), the minimal helper was copied into MLRoutingTests.fs — a pattern consistent with the plan's recommendation.
- **Test 3 exercises BOTH paths.** The plan's "revised" requirement asks for both heuristic and ML paths in the same test. Test 3 calls routeRequest twice — once with applyML (assert Reason=ML) and once with applyHeuristic (assert Reason≠ML). This proves the algorithm parameter actually drives dispatch rather than both paths accidentally converging.

## Deviations from Plan

None — plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

Phase 4 is COMPLETE. All 4 criteria met:
- ML-01: RoutingAlgorithm type alias; both functions conform (Test 1)
- ML-02: placeholder ML.applyML behavior + dispatch (Tests 2, 3, 4)
- ML-03: CLI override via AddInMemoryCollection last-wins (Test 5; mechanism wired in 04-02)
- ML-04: isolation grep enforced by check-routing-isolation.sh

Ready for Phase 5 (Decision Logging — Loop B's input): the RoutingDecision.Reason = ML tag is available for JSONL writers to consume; heuristic decisions carry their score. No blockers.

---
*Phase: 04-ml-algorithm-seam*
*Completed: 2026-05-08*
