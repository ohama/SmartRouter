---
phase: 22-cascade-rewire-migration-and-observability
plan: "02"
subsystem: session-extraction
tags: [session, fingerprint, middleware, deletion, migration, archive]

# Dependency graph
requires:
  - phase: 22-01
    provides: Three-tier session cascade (ChatCompletions.fs) making HMRS-02 dead code safe to delete
provides:
  - HMRS-02 v2.0 IP+UA network fingerprint code fully deleted from codebase
  - annotated tag v2.0-network-fingerprint + branch archive/v2.0-network-fingerprint preserving pre-deletion snapshot
  - CorrelationMiddleware.correlationMiddleware with clean 2-param signature (ctx, next)
  - SessionOptions record with 2 fields only (TtlMinutes, MaxEntries)
  - HermesFingerprintTests.fs deleted; test count 180 passing (188 − 8 FP-* tests)
affects:
  - 22-03

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Archive-before-delete: MIG-06 creates tag/branch BEFORE first deletion commit so pre-deletion state is permanently reachable"
    - "Reverse-order deletion: MIG-03 (file that uses field) before MIG-01 (field deletion) to keep each intermediate state buildable"
    - "Atomic deletion batching: signature changes batched with ALL callers in ONE commit (FS0001 prevention)"

key-files:
  created:
    - .planning/phases/22-cascade-rewire-migration-and-observability/22-02-MIG-06.md
  modified:
    - src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs
    - src/SmartRouter.Cli/Adapters/SessionStore.fs
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/appsettings.json
    - tests/SmartRouter.Tests/LoggingTests.fs
    - tests/SmartRouter.Tests/SessionStoreTests.fs
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
  deleted:
    - tests/SmartRouter.Tests/HermesFingerprintTests.fs

key-decisions:
  - "Tag is ANNOTATED (not lightweight) per RESEARCH §4 — v2.0 milestone warrants formal preservation with tagger + message"
  - "Commit order: MIG-06 → MIG-03 → MIG-02 → MIG-01 (reverse of requirement numbering) to keep every intermediate state buildable"
  - "MIG-02 batches CorrelationMiddleware + Program.fs + LoggingTests in ONE commit — signature change breaks both callers (FS0001)"
  - "MIG-01 batches SessionOptions + SessionStoreTests + appsettings in ONE commit — record-field removal breaks all literal callers (FS0764)"
  - "Operator appsettings.json retaining stale FingerprintEnabled key is SAFE: CLIMutable binding silently ignores unknown JSON keys"
  - "README §7 row removal deferred to Plan 22-03 per RESEARCH §9 recommendation"

patterns-established:
  - "Archive-before-delete pattern: create git tag/branch FIRST then proceed with deletion commits"
  - "PITFALL-26 enforcement: fsproj Compile entry + file deletion + rootTests entry must be ONE commit"
  - "Deletion ordering by compile dependency: delete consumers before producers"

# Metrics
duration: 15min
completed: 2026-05-12
---

# Phase 22 Plan 02: HMRS-02 IP+UA Network Fingerprint Deletion Summary

**Deleted the v2.0 SHA-256(RemoteIp+UA) fingerprint code path (CorrelationMiddleware, SessionOptions.FingerprintEnabled, HermesFingerprintTests.fs) and preserved the pre-deletion state in annotated tag v2.0-network-fingerprint + branch archive/v2.0-network-fingerprint**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-05-12T13:18:00Z
- **Completed:** 2026-05-12T13:33:00Z
- **Tasks:** 4 deletion tasks + 1 SUMMARY task
- **Files modified/deleted:** 9 files

## Accomplishments

- Created annotated tag `v2.0-network-fingerprint` and branch `archive/v2.0-network-fingerprint` both pointing to d4797e7 (Plan 22-01 final commit) — HMRS-02 code permanently reachable for archaeological reference
- Deleted `HermesFingerprintTests.fs` (8 tests FP-01..FP-08, 130 lines) with atomic fsproj + rootTests cleanup (PITFALL-26 compliance)
- Removed `fingerprintEnabled: bool` parameter from `CorrelationMiddleware.correlationMiddleware` and the SHA-256(RemoteIp+UA) block (~30 lines); updated both callers (Program.fs, LoggingTests.fs) in ONE commit
- Removed `FingerprintEnabled` field from `SessionOptions`, JSON key from `appsettings.json`, and record literal from `SessionStoreTests.fs` in ONE commit
- Test count: 188 → 180 (expected, intentional; Plan 22-03 adds replacement tests)

## Task Commits

1. **Task 1: MIG-06 — Create archive tag + branch** - `cfa5c91` (chore)
2. **Task 2: MIG-03 — Delete HermesFingerprintTests + fsproj/rootTests entries** - `c966d70` (chore)
3. **Task 3: MIG-02 — Delete CorrelationMiddleware fingerprint block + Program.fs + LoggingTests** - `0af61b6` (refactor)
4. **Task 4: MIG-01 — Delete FingerprintEnabled config + SessionOptions field** - `9806ebd` (refactor)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — removed `fingerprintEnabled` parameter and SHA-256 block; signature now `HttpContext -> RequestDelegate -> Task`
- `src/SmartRouter.Cli/Adapters/SessionStore.fs` — `SessionOptions` now has 2 mutable fields (TtlMinutes, MaxEntries)
- `src/SmartRouter.Cli/Program.fs` — removed 4-line Phase 20 config read; lambda calls `correlationMiddleware ctx next`
- `src/SmartRouter.Cli/appsettings.json` — `Routing.Session` block now has 2 keys only (valid JSON, no trailing comma)
- `tests/SmartRouter.Tests/LoggingTests.fs` — updated middleware call to 2-arg form
- `tests/SmartRouter.Tests/SessionStoreTests.fs` — updated `mkStore` SessionOptions literal to 2-field form
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — removed Phase 20 comment + `<Compile Include="HermesFingerprintTests.fs" />`
- `tests/SmartRouter.Tests/RouterTests.fs` — removed `HermesFingerprintTests.tests` from rootTests
- `tests/SmartRouter.Tests/HermesFingerprintTests.fs` — DELETED (git rm)
- `.planning/phases/22-cascade-rewire-migration-and-observability/22-02-MIG-06.md` — audit record of archive operation

## Decisions Made

- Annotated tag chosen over lightweight (v2.0 milestone formality; mirrors milestone-v2.0 precedent)
- Commit order reversed from requirement numbering: MIG-06 → MIG-03 → MIG-02 → MIG-01
  - MIG-03 before MIG-01 because `HermesFingerprintTests.fs` used `SessionOptions.FingerprintEnabled`; deleting the field first would break the test file compile
  - MIG-02 and MIG-01 kept separate because they have different dependency sets
- README §7 `Routing.Session.FingerprintEnabled` row left for Plan 22-03 (RESEARCH §9)

## Deviations from Plan

None - plan executed exactly as written, including the exact commit order specified in resolved_decision §7.

## Issues Encountered

None. Build remained 0 warnings / 0 errors at every commit. Tests remained 0 failed at every commit.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

Plan 22-03 can proceed immediately:
- `grep FingerprintEnabled src/ tests/` (excluding build dirs) returns 0 hits — SC-4 satisfied
- `archive/v2.0-network-fingerprint` branch + `v2.0-network-fingerprint` tag in place for CHANGELOG `### Removed` claim
- Plan 22-03 scope: SessionKeyCascadeTests.fs (TIER-01..05 + OBS-01 integration tests), smoke-hermes-session.sh update (MIG-04), CHANGELOG [2.1.0] block (MIG-05), README §7 row removal

---
*Phase: 22-cascade-rewire-migration-and-observability*
*Completed: 2026-05-12*
