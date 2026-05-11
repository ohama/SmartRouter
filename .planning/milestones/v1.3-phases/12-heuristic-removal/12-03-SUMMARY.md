---
phase: 12-heuristic-removal
plan: 03
subsystem: tests
tags: [heuristic-removal, test-deletion, fsproj, routing-tests]
requires: ["12-02"]
provides: ["RoutingTests.fs deleted", "fsproj pruned", "rootTests cleaned"]
affects: ["12-06"]
tech-stack:
  added: []
  patterns: ["Q5 전체 삭제 — file delete + fsproj entry + rootTests entry atomic removal"]
key-files:
  created: []
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
  deleted:
    - tests/SmartRouter.Tests/RoutingTests.fs
decisions:
  - "RoutingTests.fs fully deleted (Q5=전체 삭제) — 22 heuristic-internal + stage-pipeline tests removed per user explicit choice; integration tests provide indirect stage-1/2 coverage"
  - "MLRoutingTests.fs grep hits are false positives — 'RoutingTests.' pattern matches 'MLRoutingTests.' substring; zero standalone RoutingTests references remain"
metrics:
  duration: ~5 min
  completed: 2026-05-09
---

# Phase 12 Plan 03: RoutingTests.fs Deletion Summary

**One-liner:** Deleted RoutingTests.fs (22 heuristic tests), removed fsproj Compile entry, removed rootTests list entry — Tests project builds clean.

## Objective

Execute Q5=전체 삭제: remove `tests/SmartRouter.Tests/RoutingTests.fs` and all references to it from the Tests project.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | Delete RoutingTests.fs + remove from Tests.fsproj | 2974968 | RoutingTests.fs (deleted), SmartRouter.Tests.fsproj |
| 2 | RouterTests.fs — remove RoutingTests.tests from rootTests | 0c73824 | RouterTests.fs |
| 3 | Run tests to confirm count drop | (no commit — read-only) | — |

## What Was Done

**Task 1:** `git rm tests/SmartRouter.Tests/RoutingTests.fs` removed the file from disk and git index. Removed `<Compile Include="RoutingTests.fs" />` from `SmartRouter.Tests.fsproj`. The `<Compile Include="MLRoutingTests.fs" />` line (Wave 3 plan 12-04's scope) was correctly left in place.

**Task 2:** Removed `SmartRouter.Tests.RoutingTests.tests` from the `rootTests : Test list` in `RouterTests.fs`. No `open` statement needed — the module was never separately opened. `MLRoutingTests.tests` remained in the list.

**Task 3:** `dotnet build` — 0 errors, 0 warnings (Debug). Test run: `62 tests run — 41 passed, 16 ignored, 0 failed, 21 errored`. The 21 errored tests are from MLRoutingTests/LoggingTests/StreamingTests/HealthFallbackTests still referencing heuristic artifacts — those are 12-04/12-05's scope. Zero failures and zero RoutingTests-related undefined reference errors.

## Verification Results

- `test ! -f tests/SmartRouter.Tests/RoutingTests.fs` → OK (file absent on disk)
- `grep "RoutingTests.fs" SmartRouter.Tests.fsproj` (excluding MLRouting) → 0 hits
- `grep "RoutingTests.tests" RouterTests.fs` (excluding MLRouting) → 0 hits
- `dotnet build` Debug → Build succeeded, 0 errors, 0 warnings
- Test run → 41 passed, 16 ignored, 21 errored (all errors in 12-04/12-05 scope)
- All `.fs` files: zero standalone `RoutingTests.` references

## Test Count Delta

- Baseline before plan: 86 passed (Debug, Phase 11 complete)
- RoutingTests.fs had 22 test cases (confirmed by file read)
- Errored count (21) reflects heuristic refs in 12-04/12-05 scope — not this plan's work
- After 12-04/12-05 complete: expected baseline ~64 passed (86 − 22 routing tests)

## Deviations from Plan

None — plan executed exactly as written.

The plan's Task 3 verify grep `grep -c "RoutingTests\." tests/SmartRouter.Tests/*.fs` showed counts of 1 in MLRoutingTests.fs and RouterTests.fs — both confirmed to be `MLRoutingTests.` substring matches (a false-positive artifact of the grep pattern), not standalone RoutingTests references.

## Next Phase Readiness

- 12-04 (MLRoutingTests prune) and 12-05 (fixture migration) run in parallel against different files — no conflicts
- 12-06 (cleanup) depends on 12-03/04/05 all complete
- `RoutingTests.fs` is fully gone from disk, fsproj, and rootTests; no cleanup needed in 12-06 for this file
