---
phase: 24-tier-04-ml-mode-integration-test
plan: "24-01"
subsystem: testing
tags: [expecto, di, session-cascade, tier-04, ml-mode, integration-test]

# Dependency graph
requires:
  - phase: 22-cascade-rewire-migration-obs
    provides: SessionCascadeStats registered unconditionally in configureRequestPipeline (lines 447-456); ISessionCascadeStats interface; TC-1..TC-6 baseline
provides:
  - TC-7: executable assertion that ISessionCascadeStats resolves non-null in Routing.Mode="ml" DI provider
  - TD-1 from v2.1-MILESTONE-AUDIT.md closed
  - TIER-04 evidence upgraded from structural inference to CI-enforced executable assertion
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "box-for-isNotNull: F# interfaces cannot be null at the type level; use (box value) to obtain an obj reference compatible with Expect.isNotNull (mirrors MLRoutingTests.fs line 146)"
    - "ml-mode-no-mlsection: omit Routing:ML config section to make mlOpts null at CompositionRoot line 342, skipping ML bootstrap and ONNX file requirements; same technique as ModeSwitchTests.fs lines 7-9"

key-files:
  created: []
  modified:
    - "tests/SmartRouter.Tests/SessionKeyCascadeTests.fs"

key-decisions:
  - "Use (box stats) with Expect.isNotNull — F# interfaces are non-nullable reference types; box lifts to obj enabling the null check. GetRequiredService throws if unregistered, so non-null is the meaningful assertion."
  - "SC-2 (paired counter test running resolveSessionCascade in ml-mode) NOT added — resolveSessionCascade has no mode branch; TC-1..TC-4 already cover all cascade branches exhaustively; adding SC-2 provides zero coverage gain (per Q11 of 24-RESEARCH.md)."

patterns-established: []

# Metrics
duration: 4min
completed: 2026-05-12
---

# Phase 24: TIER-04 ml-mode Integration Test Summary

**TC-7 executable assertion: ISessionCascadeStats resolves non-null from a DI provider configured with Routing:Mode="ml", closing TD-1 from the v2.1 audit and upgrading TIER-04 from structural to CI-enforced proof.**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-05-12T06:00:55Z
- **Completed:** 2026-05-12T06:04:33Z
- **Tasks:** 2 (1 content + 1 metadata)
- **Files modified:** 1 (test file)

## Accomplishments

- TC-7 appended inside the existing `testList "SessionKeyCascadeTests"` — no new file, no `fsproj` change, no `rootTests` change
- Test constructs a full DI provider via `configureRequestPipeline` with `Routing:Mode="ml"` and no `Routing:ML` section, resolves `ISessionCascadeStats`, asserts non-null
- Build: 0 errors, 0 warnings (`TreatWarningsAsErrors=true`)
- Test baseline: 186 passed → 187 passed; 18 ignored unchanged; 0 failed

## Task Commits

1. **Task 1: Add TC-7 ml-mode DI resolution test** — `cc5592d` (test)

**Plan metadata:** this commit (docs: complete plan)

## Files Created/Modified

- `tests/SmartRouter.Tests/SessionKeyCascadeTests.fs` — TC-7 testCase appended (33 lines inserted after TC-6 closing assertion, before outer testList `]`)

## Decisions Made

- **`box stats` for `Expect.isNotNull`:** F# interfaces cannot be null at the type-system level, so `Expect.isNotNull stats` fails to compile (`FS0001: 'ISessionCascadeStats' does not have a proper null value`). Applied `(box stats)` to obtain an `obj` reference, consistent with `MLRoutingTests.fs` line 146 (`Expect.isNotNull (box emb) "IEmbedder resolves"`).
- **SC-2 not added:** The research file (Q11) explicitly recommends against a paired counter test. `resolveSessionCascade` has no mode branch; TC-1..TC-4 already cover all four cascade outputs exhaustively. SC-2 would add zero coverage.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `Expect.isNotNull stats` compile error; fixed with `(box stats)`**
- **Found during:** Task 1 (TC-7 implementation — first build attempt)
- **Issue:** F# type system rejects `Expect.isNotNull` on a non-nullable interface type (`FS0001`). The research skeleton used `Expect.isNotNull stats` verbatim, which does not compile.
- **Fix:** Changed to `Expect.isNotNull (box stats)`. The `box` operator lifts to `obj`, which is a nullable reference type — the correct overload for `Expect.isNotNull`. `GetRequiredService<T>()` always returns a non-null value or throws, so `box` does not change the semantic meaning of the assertion.
- **Files modified:** `tests/SmartRouter.Tests/SessionKeyCascadeTests.fs`
- **Verification:** `dotnet build` 0 errors/warnings; `dotnet test` 187 passed, 0 failed
- **Committed in:** `cc5592d` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — compile bug in research skeleton)
**Impact on plan:** Required and semantically correct. No scope creep.

## Issues Encountered

- Research skeleton's `Expect.isNotNull stats` did not compile under F# strict type system. Fixed immediately with `(box stats)` — the same pattern already present in `MLRoutingTests.fs:146`.

## README Impact

None. CLAUDE.md README sync rule does not apply. This plan changes a test file only; no production code, no public surface, no observable behavior is altered. None of the 12 README-trigger areas are crossed.

## Next Phase Readiness

- Phase 24 is the final gap-closure phase for v2.1. TD-1 is now closed.
- v2.1 milestone is ready for archive (`/gsd:complete-milestone` or equivalent).
- Remaining carry-overs (TD-2/3 ModelsTests migration + configureServices alias, TD-4 live-rig smoke) remain deferred per operator decision.

---
*Phase: 24-tier-04-ml-mode-integration-test*
*Completed: 2026-05-12*
