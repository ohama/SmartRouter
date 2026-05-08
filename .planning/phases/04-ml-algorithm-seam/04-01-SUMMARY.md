---
phase: 04-ml-algorithm-seam
plan: 01
subsystem: routing
tags: [fsharp, routing, heuristic, ml-seam, domain, ci-script]

# Dependency graph
requires:
  - phase: 03-122b-concurrency-gate
    provides: routeRequest (2-arg), Routing.fs with scoreComplexity+applyHeuristic, 39 passing tests
provides:
  - RoutingAlgorithm type alias in Domain.fs (RoutingConfig -> RouterRequest -> RoutingDecision)
  - RoutingReason.ML DU case in Domain.fs
  - Heuristic.fs flat sibling module (applyHeuristic + scoreComplexity extracted from Routing.fs)
  - ML.fs flat sibling module (applyML placeholder: always Qwen35B, Reason=ML)
  - routeRequest 3-arg signature (RoutingConfig -> RoutingAlgorithm -> RouterRequest -> Result<...>)
  - RoutingTests.fs migrated to pass Heuristic.applyHeuristic at all callsites
  - scripts/check-routing-isolation.sh CI grep enforcing ML-04 zero cross-imports
affects:
  - 04-02-config-dispatch (consumes RoutingAlgorithm, routeRequest 3-arg, Heuristic.applyHeuristic, ML.applyML)
  - 04-03-cli-override (same)
  - Any future phase adding new routing algorithms

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "RoutingAlgorithm function-type alias as pluggable seam: both algorithm modules conform to same shape, Routing.fs dispatches via parameter"
    - "Zero cross-imports enforced by CI grep script (mirrors check-no-async.sh shape)"
    - "Atomic wave refactor: Domain.fs defines alias, Heuristic.fs + ML.fs define implementations, Routing.fs dispatches — all before Cli callsite update (Wave 2)"

key-files:
  created:
    - src/SmartRouter.Core/Heuristic.fs
    - src/SmartRouter.Core/ML.fs
    - scripts/check-routing-isolation.sh
  modified:
    - src/SmartRouter.Core/Domain.fs
    - src/SmartRouter.Core/Routing.fs
    - src/SmartRouter.Core/SmartRouter.Core.fsproj
    - tests/SmartRouter.Tests/RoutingTests.fs

key-decisions:
  - "RoutingAlgorithm alias lives in Domain.fs (upstream of both Heuristic.fs and ML.fs in compile order)"
  - "ML.fs comment mentioning Heuristic module name revised to avoid triggering CI isolation grep false positive"
  - "Test run deferred to Wave 2 boundary (04-02) — Tests project depends on Cli which has expected Wave 1 Cli callsite failure"

patterns-established:
  - "Algorithm seam: pass algorithm as function parameter to routeRequest, not a DU dispatch inside routeRequest"
  - "Flat sibling layout for Core algorithm modules (no subdirectory)"
  - "CI isolation script: set -euo pipefail + grep pattern + exit 0/1/2 + OK message"

# Metrics
duration: 3min
completed: 2026-05-08
---

# Phase 4 Plan 01: Core Refactor Summary

**Routing.fs split into flat siblings Heuristic.fs + ML.fs with RoutingAlgorithm seam; routeRequest gains algorithm parameter; CI isolation enforced by check-routing-isolation.sh**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-05-08T04:24:47Z
- **Completed:** 2026-05-08T04:27:30Z
- **Tasks:** 4
- **Files modified:** 7 (3 created, 4 modified)

## Accomplishments

- Introduced `RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision` type alias in Domain.fs; added `| ML` case to `RoutingReason` DU
- Extracted `scoreComplexity` + `applyHeuristic` verbatim into `Heuristic.fs`; created `ML.fs` placeholder always returning `{ Target = Qwen35B; Reason = ML; Priority = Low }`
- `routeRequest` new signature: `RoutingConfig -> RoutingAlgorithm -> RouterRequest -> Result<RoutingDecision, RouterError>` — dispatches `algorithm config req` at Stage 3
- All 22 RoutingTests callsites updated: `route` helper now passes `Heuristic.applyHeuristic`; 3 direct `routeRequest edited ...` calls updated; `open SmartRouter.Core.Heuristic` added
- `scripts/check-routing-isolation.sh` enforces ML-04 zero cross-imports; negative-tested (injected violation caught with exit 1)
- Core builds with 0 warnings under `TreatWarningsAsErrors=true`

## Task Commits

1. **Task 1: Domain.fs — RoutingAlgorithm alias + RoutingReason.ML** — `66f4d85` (feat)
2. **Task 2: Heuristic.fs + ML.fs + Routing.fs refactor + .fsproj order** — `e3dcdb2` (feat)
3. **Task 3: RoutingTests.fs migration to 3-arg routeRequest** — `f2372a7` (feat)
4. **Task 4: check-routing-isolation.sh CI script** — `bf30243` (chore)

## Files Created/Modified

- `src/SmartRouter.Core/Domain.fs` — Added `| ML` to RoutingReason DU; added `type RoutingAlgorithm` alias after RoutingConfig
- `src/SmartRouter.Core/Heuristic.fs` — New; contains `scoreComplexity` + `applyHeuristic` moved verbatim from Routing.fs
- `src/SmartRouter.Core/ML.fs` — New; contains `applyML` placeholder (always Qwen35B/Low/ML)
- `src/SmartRouter.Core/Routing.fs` — scoreComplexity + applyHeuristic removed; routeRequest now 3-arg with algorithm parameter
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` — Compile order: Domain → Heuristic → ML → Routing → Ports
- `tests/SmartRouter.Tests/RoutingTests.fs` — open Heuristic added; route helper + 3 direct calls updated
- `scripts/check-routing-isolation.sh` — New executable; CI grep for ML-04 zero cross-imports

## Decisions Made

- **ML.fs comment wording:** The original plan template included a comment "MUST NOT import SmartRouter.Core.Heuristic" in ML.fs. The loose grep during cross-import check (`SmartRouter\.Core\.Heuristic` without trailing dot) matched the comment text. Comment revised to "MUST NOT import the Heuristic module" to avoid false positives from the CI script's `SmartRouter\.Core\.Heuristic\.` pattern (which uses a trailing dot — so the original comment was actually fine for the CI script). Applied as a cleanliness fix.

- **Test run at Wave 1 boundary:** The Tests project (`SmartRouter.Tests.fsproj`) depends on Cli via `<ProjectReference>`. Since `ChatCompletions.fs` in Cli still uses the old 2-arg `routeRequest` signature, `dotnet build tests/...` fails with expected FS0001/FS0025 errors. This is the documented Wave 1 boundary state; full `dotnet test` 39/39 verification is deferred to plan 04-02 boundary per the plan's explicit scope.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] ML.fs comment triggered false positive on cross-import grep**

- **Found during:** Task 4 (check-routing-isolation.sh verification)
- **Issue:** The inline verification grep `'SmartRouter\.Core\.Heuristic\|open SmartRouter\.Core\.Heuristic'` (without trailing dot) matched the comment text "MUST NOT import SmartRouter.Core.Heuristic" in ML.fs
- **Fix:** Revised comment to "MUST NOT import the Heuristic module (zero cross-imports enforced by ML-04)" — no actual code or logic change; the CI script's pattern uses a trailing dot (`SmartRouter\.Core\.Heuristic\.`) so it would not have matched the original comment
- **Files modified:** src/SmartRouter.Core/ML.fs (comment only, in Task 2 commit)
- **Verification:** check-routing-isolation.sh exits 0; negative test confirms violation detection still works
- **Committed in:** e3dcdb2 (Task 2 commit, comment fix was part of the atomic edit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — false-positive comment wording fix)
**Impact on plan:** Cosmetic comment change only. No behavior change.

## Issues Encountered

- Tests project build unavailable at this wave boundary due to Cli ProjectReference (expected, documented in plan and context). Core builds independently with 0 warnings — the critical verifiable invariant.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Plan 04-02 (config dispatch + DI + CLI override) is fully unblocked:
  - `RoutingAlgorithm` type alias available in Domain.fs
  - `Heuristic.applyHeuristic` and `ML.applyML` both conform to the alias
  - `routeRequest` 3-arg signature ready for CompositionRoot dispatch
  - RoutingTests.fs already migrated — 04-02 only needs to fix the Cli callsite and wire DI
- After 04-02 fixes `ChatCompletions.fs` Cli callsite, full `dotnet test` 39/39 will be verifiable
- `check-routing-isolation.sh` ready to add to CI pipeline

---
*Phase: 04-ml-algorithm-seam*
*Completed: 2026-05-08*
