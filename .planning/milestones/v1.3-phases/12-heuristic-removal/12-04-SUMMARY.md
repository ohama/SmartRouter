---
phase: 12-heuristic-removal
plan: "04"
subsystem: tests
tags: [fsharp, heuristic-removal, test-pruning, ml-routing]

dependency-graph:
  requires: ["12-02"]
  provides: ["MLRoutingTests pruned of all heuristic references"]
  affects: ["12-06"]

tech-stack:
  added: []
  patterns: []

key-files:
  created: []
  modified:
    - tests/SmartRouter.Tests/MLRoutingTests.fs

decisions:
  - "Deleted testCaseAsync 'routeRequest dispatches the algorithm parameter' entirely (both ML path + heuristic divergence half) — safe: ML threshold contract already covered by ML-02 synchronous test"
  - "Updated mlTestCase 'CompositionRoot registers makeApplyML...' name and comment to remove Routing:Algorithm reference (key no longer exists; comment was misleading)"

metrics:
  duration: "~5 min"
  completed: "2026-05-09"
---

# Phase 12 Plan 04: MLRoutingTests Prune Summary

**One-liner:** Deleted 3 heuristic-related tests from MLRoutingTests.fs and removed all Routing:Algorithm config key references; test count 12 → 9.

## What Was Done

Pruned `tests/SmartRouter.Tests/MLRoutingTests.fs` to remove all heuristic-related tests and config key references following the Q3+Q4 changes in 12-02 (Routing.Algorithm key deleted; heuristic code gone).

### Changes Made

1. **Removed `open SmartRouter.Core.Heuristic`** — module no longer exists after 12-01.

2. **Deleted ML-01 test** (`Heuristic.applyHeuristic and ML.makeApplyML closure both satisfy RoutingAlgorithm`): Referenced `applyHeuristic` directly; no longer valid.

3. **Deleted ML-02/03 test** (`testCaseAsync "routeRequest dispatches the algorithm parameter"`): Contained heuristic-vs-ML divergence comparison. The ML-path assertions were independently valid but the test's stated purpose was to prove routeRequest honors the algorithm parameter via heuristic divergence — that proof is no longer meaningful. ML threshold contract is already fully covered by the remaining synchronous ML-02 test.

4. **Deleted ML-03/CLI test** (`mlTestCase "CLI --routing-algorithm=ml overrides config Algorithm=heuristic"`): Entire premise invalid — the flag and key were both deleted in 12-02.

5. **Removed `Routing:Algorithm` key from all `AddInMemoryCollection` calls**: Three sites in the original file (mlTestCase DI test, Phase 6 ModelVersion test, Phase 6 DI smoke test). The mlTestCase DI test had a two-entry dict so only the key was removed; the two Phase 6 tests had single-entry dicts so the entire `AddInMemoryCollection(dict [ ... ])` chain call was removed.

6. **Updated comment and test name** on `mlTestCase "CompositionRoot registers makeApplyML..."` to not reference `Routing:Algorithm=ml` (key is gone; name was misleading readers into thinking there's still a config toggle).

## Verification Results

```
grep -c "applyHeuristic|open SmartRouter.Core.Heuristic" MLRoutingTests.fs  → 0
grep -c "Routing:Algorithm"                               MLRoutingTests.fs  → 0
grep -c "routing-algorithm"                               MLRoutingTests.fs  → 0
grep -c "testCase|mlTestCase|ptestCase|testCaseAsync"     MLRoutingTests.fs  → 9 (was 12)
dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj                → 0 errors 0 warnings
dotnet test  tests/SmartRouter.Tests/SmartRouter.Tests.fsproj                → exit 0
```

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Cleanup] Comment and test name updated to remove stale Routing:Algorithm reference**

- **Found during:** Step 5 (grep verification showed 2 remaining hits)
- **Issue:** Plan's verify step expected `grep -c "Routing:Algorithm" = 0`, but the mlTestCase comment and test name string still contained the phrase.
- **Fix:** Updated comment header and test name to reflect that ml is the only algorithm (no toggle key).
- **Files modified:** tests/SmartRouter.Tests/MLRoutingTests.fs
- **Commit:** e8bd311

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| Task 1 (all steps) | e8bd311 | refactor(12-04): prune heuristic tests and references from MLRoutingTests.fs |

## Next Phase Readiness

- **12-06 (Cleanup)** can run: MLRoutingTests.fs has zero heuristic references, zero Routing:Algorithm entries, zero routing-algorithm mentions. 12-06's global grep checklist should pass for MLRoutingTests.
- **12-03 and 12-05** are parallel Wave 3 plans and do not depend on this plan's output.
