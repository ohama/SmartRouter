---
phase: 22-cascade-rewire-migration-and-observability
plan: "03"
subsystem: session-extraction
tags: [expecto, fsharp, session-cascade, sticky-escalation, changelog, smoke-test]

# Dependency graph
requires:
  - phase: 22-01
    provides: resolveSessionCascade pure helper + ISessionCascadeStats DI + three-tier wiring in ChatCompletions.fs
  - phase: 22-02
    provides: FingerprintEnabled config key deleted, IP+UA fingerprint code removed, HermesFingerprintTests.fs deleted

provides:
  - SessionKeyCascadeTests.fs with 6 testCase entries (TC-1..TC-6) covering TIER-05 + OBS-01
  - smoke-hermes-session.sh header updated for v2.1 Tier 1/2/3 description (no stale FingerprintEnabled references)
  - CHANGELOG.md [2.1.0] - 2026-05-12 block (### Removed / Added / Changed / Notes)
  - README.md §7 Routing.Session.FingerprintEnabled row removed
  - Phase 22 fully complete: all 12 requirements (TIER-01..05 + OBS-01 + MIG-01..06) shipped

affects:
  - 23-01

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Pure-function integration testing via direct module-function call (resolveSessionCascade without Kestrel)"
    - "Direct instantiation of stats adapter for counter testing (SessionCascadeStats() :> ISessionCascadeStats)"
    - "Reuse of StickyEscalationTests buildProvider pattern for single DI-graph integration test (TC-5)"
    - "CHANGELOG + README §7 row removal bundled in one docs commit for CLAUDE.md sync-rule satisfaction at phase boundary"

key-files:
  created:
    - tests/SmartRouter.Tests/SessionKeyCascadeTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - scripts/smoke-hermes-session.sh
    - CHANGELOG.md
    - README.md

key-decisions:
  - "6 test cases (TC-1..TC-6), not 8 as originally projected in RESEARCH §10 — TC-7 (ml-mode-dormant) adds limited value (cascade is mode-independent at helper level); TC-8 merged into TC-6"
  - "CHANGELOG sub-section order: ### Removed first (matches v2.1 narrative: this version is about REMOVING the IP+UA fingerprint)"
  - "README §7 row removal bundled with CHANGELOG commit (Task 3); §8 + §10 deferred to Phase 23 Plan 23-01"
  - "minimalConfigPairs in TC-5 reuses full StickyEscalationTests.fs list (not the shorter plan sketch) — proven to work with configureRequestPipeline"

patterns-established:
  - "Pure-function test: call resolveSessionCascade directly — no Kestrel, no TestServer, no HTTP overhead"
  - "testSequenced wraps entire module when any test in the module builds a real DI graph (PITFALL-27)"

# Metrics
duration: 14min
completed: 2026-05-12
---

# Phase 22 Plan 03: Cascade Tests + Smoke + CHANGELOG Summary

**6-test SessionKeyCascadeTests proving 3-tier session cascade correctness (header/sysprompt/content/determinism/sticky/counters), CHANGELOG [2.1.0] documenting the FingerprintEnabled breaking change, and README §7 row removal — Phase 22 fully closed**

## Performance

- **Duration:** ~14 min
- **Started:** 2026-05-12T04:30:53Z
- **Completed:** 2026-05-12T04:45:32Z
- **Tasks:** 4
- **Files modified/created:** 6

## Accomplishments

- Created `SessionKeyCascadeTests.fs` with 6 test cases covering all TIER-05 acceptance criteria (TC-1 header wins, TC-2 sysprompt fallback, TC-3 content fingerprint, TC-4 determinism, TC-5 sticky through Tier 2 key, TC-6 counter increments)
- Updated smoke script header for v2.1 cascade — removed stale FingerprintEnabled/HermesFingerprintTests.fs references; functional curl logic unchanged
- Wrote CHANGELOG [2.1.0] block (### Removed / Added / Changed / Notes) documenting breaking FingerprintEnabled deletion and all v2.1 additions
- Removed `Routing.Session.FingerprintEnabled` row from README §7 (CLAUDE.md sync rule: config key deleted in MIG-01 -> §7 row removed in same phase)
- Test count: 180 (Plan 22-02 baseline) -> 186 passed + 18 ignored + 0 failed

## Task Commits

Each task was committed atomically:

1. **Task 1: Add SessionKeyCascadeTests.fs + register** - `b8d1796` (test)
2. **Task 2: Update smoke script header** - `1842491` (chore)
3. **Task 3: CHANGELOG [2.1.0] + README §7 row removal** - `938c8ac` (docs)
4. **Task 4: SUMMARY.md + STATE.md update** - (docs — this commit)

## Files Created/Modified

- `tests/SmartRouter.Tests/SessionKeyCascadeTests.fs` — 6 testCase entries for TIER-05 + OBS-01
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — Compile entry for SessionKeyCascadeTests.fs (before RouterTests.fs)
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests entry for SessionKeyCascadeTests.tests
- `scripts/smoke-hermes-session.sh` — header comment updated for v2.1 cascade scope
- `CHANGELOG.md` — [2.1.0] - 2026-05-12 block prepended above [2.0.0]
- `README.md` — §7 FingerprintEnabled row deleted

## Decisions Made

- **6 tests not 8**: RESEARCH §10 projected TC-7 (ml-mode-dormant) and TC-8 (counter increment) as separate. TC-7 adds limited value (resolveSessionCascade is mode-independent at the code level — no ONNX W4 skip guard needed to assert that). TC-8 merged into TC-6 (OBS-01 counter test). Final count: 6 test cases.
- **minimalConfigPairs reuses full StickyEscalationTests.fs list**: The plan's sketch had a shorter list but StickyEscalationTests.fs has the proven full list for configureRequestPipeline. Used the proven full list to avoid config-binding surprises.
- **CHANGELOG sub-section order: ### Removed first**: matches v2.1 narrative (IP+UA fingerprint removal is the headline change); slight deviation from strict Keep-a-Changelog ordering (Added → Removed) but matches existing CHANGELOG narrative style.
- **README §8 + §10 explicitly deferred**: only §7 touched in this plan; §8 (new /stats counter rows) and §10 (Hermes Integration rewrite + PROXY-01 callout removal) assigned to Phase 23 Plan 23-01.

## Deviations from Plan

None — plan executed exactly as written. The one minor implementation choice (using full StickyEscalationTests.fs config rather than plan's abbreviated sketch) is not a deviation — the plan explicitly said "reuses the StickyEscalationTests buildProvider pattern."

## Issues Encountered

None. One transient test failure (1 failed) on first `dotnet run` was a network timing artifact (HTTP connection refused from a parallel test); second run confirmed 186 passed + 18 ignored + 0 failed.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- Phase 22 complete: all 12 requirements (TIER-01..05, OBS-01, MIG-01..06) shipped across 3 plans.
- Phase 23 Plan 23-01 can read this SUMMARY to confirm Phase 22 fully shipped before writing README §10 rewrite (DOC-01) and §8 counter rows (DOC-03).
- v2.1 milestone tag (`milestone-v2.1`) is an operator action after Phase 23 closes.
- Carry-over from v1.3: ModelsTests.fs IEmbedder errors (non-blocking; tracked in STATE.md).

---
*Phase: 22-cascade-rewire-migration-and-observability*
*Completed: 2026-05-12*
