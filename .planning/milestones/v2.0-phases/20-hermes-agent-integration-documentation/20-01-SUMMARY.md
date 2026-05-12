---
phase: 20-hermes-agent-integration-documentation
plan: 01
subsystem: routing
tags: [fingerprint, session, sha256, correlation-middleware, sticky-escalation, smoke-test]

# Dependency graph
requires:
  - phase: 18-session-store-sticky-escalation
    provides: SessionStore + ISessionStore + CorrelationMiddleware X-Session-Id header support
  - phase: 19-35b-self-routing
    provides: 167-test baseline; SelfRouter cascade wiring; RoutingReason.SelfRoute

provides:
  - Routing.Session.FingerprintEnabled config opt-in (default false; preserves v1.x stateless behavior)
  - CorrelationMiddleware SHA-256(IP+|+UA)[0..15] fingerprint fallback when header absent and enabled
  - HermesFingerprintTests.fs — 8 test cases FP-1..FP-8 via DefaultHttpContext (no Kestrel)
  - scripts/smoke-hermes-session.sh — operator end-to-end sticky escalation smoke test (no Hermes Agent dep)

affects:
  - 20-02 (Plan 20-02 documents this in README §10; HMRS-01..04 requirements closure)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Startup-time config close-over pattern: read bool once from IConfiguration at app startup, close over in app.Use lambda (NOT inside per-request lambda)"
    - "SHA-256 per-request: use sha = SHA256.Create() inside task {} for thread safety; no shared instance"
    - "CLIMutable record field addition: always add new field LAST; add FingerprintEnabled=false to SessionOptions"
    - "Expecto test fix pattern: when middleware signature changes, fix all test callers (LoggingTests + SessionStoreTests) in same commit as implementation"

key-files:
  created:
    - tests/SmartRouter.Tests/HermesFingerprintTests.fs (130 lines — 8 FP tests via DefaultHttpContext)
    - scripts/smoke-hermes-session.sh (77 lines — curl-based E2E sticky smoke test)
  modified:
    - src/SmartRouter.Cli/Adapters/SessionStore.fs (FingerprintEnabled : bool added to SessionOptions)
    - src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs (fingerprintEnabled: bool first param + SHA-256 block)
    - src/SmartRouter.Cli/Program.fs (startup-time config read + updated app.Use lambda)
    - src/SmartRouter.Cli/appsettings.json (Routing.Session.FingerprintEnabled: false)
    - tests/SmartRouter.Tests/LoggingTests.fs (correlationMiddleware false ctx next — compile fix)
    - tests/SmartRouter.Tests/SessionStoreTests.fs (FingerprintEnabled = false in mkStore record literal)
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj (HermesFingerprintTests.fs compile registration)
    - tests/SmartRouter.Tests/RouterTests.fs (HermesFingerprintTests.tests in rootTests list)

key-decisions:
  - "FingerprintEnabled: false default is opt-in only; operators enabling it get SHA-256(IP|UA)[0..15] sticky key without X-Session-Id header needed"
  - "startup-time config read: fingerprintEnabled bool is read once before app.Use registration, not inside the per-request lambda (mirrors routingMode resolution in CompositionRoot.fs)"
  - "SHA-256 per-request (not shared): System.Security.Cryptography.SHA256.Create() inside task{} is thread-safe; sharing an instance across concurrent requests is NOT"
  - "lowercase hex via sprintf '%02x': matches canonical pattern in DecisionLogger.fs and SelfRouter.fs; NOT Convert.ToHexString (uppercase)"
  - "Session option change is purely additive: CLIMutable bool defaults to false when JSON key absent; existing SessionStore consumers unaffected"
  - "Auto-fix deviation: LoggingTests.fs + SessionStoreTests.fs compile errors from CorrelationMiddleware signature change and SessionOptions record extension were fixed in the same Task 1 commit (deviation Rule 1)"

patterns-established:
  - "fingerprintEnabled close-over pattern: read once at startup, pass into middleware registration lambda so per-request path is bool-gated only"
  - "Middleware signature addition convention: new bool flags go FIRST in parameter list; callers in tests pass literal (false) for v1.x stateless path"

# Metrics
duration: 10min
completed: 2026-05-12
---

# Phase 20 Plan 01: Hermes Agent Integration — Fingerprint Fallback Summary

**SHA-256(IP+UA) fingerprint fallback session key opt-in via Routing.Session.FingerprintEnabled, with 8-test HermesFingerprintTests.fs suite and operator smoke test script**

## Performance

- **Duration:** ~10 min
- **Started:** 2026-05-12T00:22:31Z
- **Completed:** 2026-05-12T00:32:28Z
- **Tasks:** 3
- **Files modified:** 8 (6 source + 2 test project files)

## Accomplishments

- Fingerprint fallback session key: SHA-256(RemoteIpAddress+"|"+User-Agent)[0..15] — 16 lowercase hex chars; opt-in via `Routing.Session.FingerprintEnabled=true` (default `false`)
- 8 new HermesFingerprintTests (FP-1..FP-8): disabled/enabled paths, explicit header priority, determinism, UA-dependence, whitespace-only header fallback, null IP sentinel — all using DefaultHttpContext (no Kestrel)
- `scripts/smoke-hermes-session.sh`: operator-runnable curl smoke test asserting `routing_reason="sticky_to_122b"` on the follow-up request; exit 0 PASS / exit 1 FAIL; no Hermes Agent dependency
- Test baseline: 175 passed / 0 failed / 18 ignored (was 167 + 8 new)

## Task Commits

1. **Task 1: SessionOptions + CorrelationMiddleware + Program.fs + appsettings.json** - `9589830` (feat)
2. **Task 2: HermesFingerprintTests.fs + fsproj + rootTests** - `368b549` (test)
3. **Task 3: smoke-hermes-session.sh** - `1ac8b1d` (feat)

**Plan metadata:** (staged in final docs commit after SUMMARY.md + STATE.md)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/SessionStore.fs` — SessionOptions.FingerprintEnabled : bool field added
- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — fingerprintEnabled: bool first param; SHA-256(IP|UA)[0..15] block
- `src/SmartRouter.Cli/Program.fs` — startup-time config read + updated app.Use lambda
- `src/SmartRouter.Cli/appsettings.json` — Routing.Session.FingerprintEnabled: false
- `tests/SmartRouter.Tests/HermesFingerprintTests.fs` (NEW, 130 lines) — 8 FP tests via DefaultHttpContext
- `tests/SmartRouter.Tests/LoggingTests.fs` — correlationMiddleware false ctx next (compile fix)
- `tests/SmartRouter.Tests/SessionStoreTests.fs` — FingerprintEnabled = false in mkStore record literal (compile fix)
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — HermesFingerprintTests.fs registered
- `tests/SmartRouter.Tests/RouterTests.fs` — HermesFingerprintTests.tests in rootTests list
- `scripts/smoke-hermes-session.sh` (NEW, 77 lines) — E2E sticky smoke test

## Decisions Made

- **opt-in default `false`**: Operators who do not set `FingerprintEnabled` continue to receive v1.x stateless behavior (empty string session ID = no sticky bucket). No silent behavior change on upgrade.
- **startup-time config read (not per-request)**: `fingerprintEnabled` is resolved once from `IConfiguration` before `app.Use(...)` registration, then closed over by the lambda. Per-request overhead is zero (no config lookup per call). Mirrors `routingMode` pattern in CompositionRoot.fs.
- **SHA-256 per-request instance**: `use sha = SHA256.Create()` inside `task {}` — allocates one instance per request, disposed at end of scope. SHA256 is NOT thread-safe to share; concurrent requests from different IPs/UAs would corrupt state.
- **lowercase hex via `sprintf "%02x"`**: Consistent with canonical pattern in DecisionLogger.fs and SelfRouter.fs. `Convert.ToHexString` produces uppercase and would fail the FP-3 lowercase assertion.
- **Auto-fix deviation**: CorrelationMiddleware's new `fingerprintEnabled: bool` first parameter broke `LoggingTests.fs` (which passes `ctx next` without the bool) and `SessionStoreTests.fs` (which uses `SessionOptions` record literal without the new field). Fixed in Task 1's commit per deviation Rule 1 (bug introduced by the planned change; fix necessary for the project to compile).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Compile errors in LoggingTests.fs and SessionStoreTests.fs from CorrelationMiddleware signature change and SessionOptions record extension**
- **Found during:** Task 1 (build verification after all 4 files changed)
- **Issue:** `correlationMiddleware ctx next` call in LoggingTests.fs line 317 became arity error; `{ TtlMinutes = ...; MaxEntries = ... }` record in SessionStoreTests.fs missing `FingerprintEnabled` field
- **Fix:** `LoggingTests.fs` — added `false` as first arg (stateless path appropriate for test fixture); `SessionStoreTests.fs` — added `FingerprintEnabled = false` to `mkStore` record literal
- **Files modified:** `tests/SmartRouter.Tests/LoggingTests.fs`, `tests/SmartRouter.Tests/SessionStoreTests.fs`
- **Verification:** `dotnet build` succeeded 0 warnings / 0 errors; `dotnet run -- --summary` showed 167 passed before adding new tests
- **Committed in:** `9589830` (bundled with Task 1 — atomically correct; broken intermediate state was never committed)

---

**Total deviations:** 1 auto-fixed (Rule 1 — compile errors from signature/record changes)
**Impact on plan:** Fix necessary for compile correctness; no scope change; no behavior change in test logic (both callers correctly use `false` for the stateless v1.x path).

## Issues Encountered

None. The only issue was the expected compile error from the signature change (flagged as detection mechanism in the plan's Step 3 constraints); resolved in the same commit.

## Next Phase Readiness

- Plan 20-01 COMPLETE: all 3 tasks committed; 175 tests green; ARCH-01/ARCH-02 preserved
- Plan 20-02 ready to proceed: README §10 + §7 config row + CHANGELOG + REQUIREMENTS HMRS-01..04 closure
- NOTE for Plan 20-02 docs: `Routing.Session.FingerprintEnabled` is a new config key → must add a row to README §7 Configuration Reference (CLAUDE.md sync rule area 9)
- Fingerprint behavior documented in `HermesFingerprintTests.fs` inline comments; README §10 will surface operator-facing guidance (proxy caveat: fingerprint is unstable behind SNAT/reverse-proxy — document as known limitation)

---
*Phase: 20-hermes-agent-integration-documentation*
*Completed: 2026-05-12*
