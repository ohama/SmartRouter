---
phase: 14-quality-fallback-and-trace
plan: 05
subsystem: testing
tags: [expecto, fsharp, integration-tests, quality-fallback, trace-logger, decision-log, jsonl, kestrel]

# Dependency graph
requires:
  - phase: 14-01
    provides: ColdStart.fs infrastructure (temp dir patterns mirrored)
  - phase: 14-02
    provides: TraceLogger.fs + ITraceLogger + TraceLoggerOptions (triple-reg pattern)
  - phase: 14-04
    provides: ChatCompletions quality fallback branch + QualityFallbackOptions DI singleton

provides:
  - Two integration tests (QF-01 + QF-02) covering 35B-only success and quality-fallback paths
  - Verified via JsonDocument.Parse of JSONL logs (decision + trace)
  - Test baseline raised from 80 to 82 passed

affects:
  - 14-06 (docs): tests confirm quality fallback behavior documented in README
  - Future test authors: QualityFallbackTests.fs establishes stub-IHealthProbe + TraceLogger-triple-reg pattern

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Fake-Kestrel path dispatch: /v1/models → model-id JSON, other paths → canned chat response"
    - "Stub IHealthProbe via F# object expression (curried IsReachableAsync: target -> ct -> Task<bool>)"
    - "TraceLogger triple-reg in test fixture (configureWithoutMl does not register it; fixture mimics production always-enabled state)"
    - "computePromptUid single-message equivalence: SHA-256(raw prompt string)[:12] == production computePromptHash with one message"
    - "JSONL verification via JsonDocument.Parse + findRow helper (no string-Contains shortcuts)"
    - "Unique tempDir per test with try/finally cleanup"

key-files:
  created:
    - tests/SmartRouter.Tests/QualityFallbackTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "Used stub IHealthProbe (always returns true) instead of real HealthService — simpler, avoids health-probe HTTP traffic in tests"
  - "Fake upstreams dispatch on ctx.Request.Path.Value: /v1/models → model ID JSON, other paths → canned chat response — required because QwenUpstreamClient probes /v1/models before first CompleteAsync"
  - "QualityFallbackOptions not re-registered in fixture — configureWithoutMl already binds it from Routing:QualityFallback config keys"

patterns-established:
  - "Fake upstream must handle /v1/models in addition to /v1/chat/completions (QwenUpstreamClient probes /v1/models per upstream on first call)"
  - "IHealthProbe stub in F# object expression uses curried signature: member _.IsReachableAsync(_target) _ct = ..."

# Metrics
duration: 12min
completed: 2026-05-10
---

# Phase 14 Plan 05: Quality Fallback Tests Summary

**Two integration tests (QF-01 no-fallback, QF-02 quality-fallback) verify the 35B→122B retry path end-to-end via JSONL log inspection using fake-Kestrel upstreams and stub routing**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-05-10T16:06:00Z
- **Completed:** 2026-05-10T16:17:00Z
- **Tasks:** 1
- **Files modified:** 3

## Accomplishments

- Created `QualityFallbackTests.fs` with 2 testCase entries wrapped in `testSequenced (testList "quality-fallback" [...])` per project convention
- QF-01: fake 35B returns good response (length > 30, no bad keywords) → asserts DecisionLog `routing_reason="ml"`, `target="Qwen35B"`, `fallback_used=false`; Trace `fallback_kind=null`, `final_target="Qwen35B"`
- QF-02: fake 35B returns "TODO: implement this" → quality check fires → 122B retry → asserts DecisionLog `routing_reason="fallback_to_122b"`, `target="Qwen122B"`, `fallback_used=true`; Trace `fallback_kind="quality"`, `initial_response_excerpt` contains "TODO", `final_response_excerpt` contains "Recursion"
- Both tests verify via `JsonDocument.Parse` of JSONL log files (not string-Contains shortcuts)
- Test count raised from 80 to 82 passed + 16 ignored + 0 failed

## Task Commits

1. **Task 1: Create QualityFallbackTests.fs with 2 testCase entries** - `55cecff` (test)

**Plan metadata:** (pending final docs commit)

## Files Created/Modified

- `tests/SmartRouter.Tests/QualityFallbackTests.fs` - Two integration tests for quality fallback path; `startTestRouter` helper with stub IHealthProbe + TraceLogger triple-reg + QwenUpstreamClient + QueueDispatcher; `computePromptUid` helper; `findRow` JSONL parser
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` - Added `<Compile Include="QualityFallbackTests.fs" />` after MLLiveVersionTests.fs
- `tests/SmartRouter.Tests/RouterTests.fs` - Added `SmartRouter.Tests.QualityFallbackTests.tests` to rootTests

## Decisions Made

- **Stub IHealthProbe vs real HealthService:** Used a stub IHealthProbe (returns `true` for all upstreams) instead of wiring the real HealthService. Rationale: simpler fixture, avoids health-probe HTTP activity, quality fallback path only needs `IsReachable(Qwen122B)` to return `true`.

- **QualityFallbackOptions:** Not re-registered in fixture because `configureWithoutMl` already registers it as a standalone singleton from the `Routing:QualityFallback:*` in-memory config keys. The 14-04 DI deviation (standalone singleton) made this work correctly.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fake upstream path dispatch**

- **Found during:** Task 1 - test execution (first run)
- **Issue:** Plan skeleton showed fake upstreams returning canned chat response for ALL requests. `QwenUpstreamClient.CompleteAsync` probes `GET /v1/models` before the first `POST /v1/chat/completions` call (lazy probe pattern). With no path dispatch, the probe received chat-completions JSON and failed to parse a valid model ID, causing a 502 on the first request.
- **Fix:** Added `if ctx.Request.Path.Value = "/v1/models" then ... else ...` dispatch in each fake upstream handler, returning `{"data":[{"id":"/fake/qwen35b"}]}` (or 122b variant) for the probe path.
- **Files modified:** `tests/SmartRouter.Tests/QualityFallbackTests.fs`
- **Verification:** Both QF-01 and QF-02 pass (82 total passed)
- **Committed in:** `55cecff` (task commit)

**2. [Rule 1 - Bug] IHealthProbe object expression curried signature**

- **Found during:** Task 1 - build (FS0768 error)
- **Issue:** Plan skeleton used tupled syntax `member _.IsReachableAsync(_target, _ct)` but the interface defines it as curried `target: ModelId -> ct: CancellationToken -> Task<bool>`. F# compiler rejected the tupled form.
- **Fix:** Changed to curried form: `member _.IsReachableAsync(_target) _ct = Task.FromResult(true)`
- **Files modified:** `tests/SmartRouter.Tests/QualityFallbackTests.fs`
- **Verification:** Build succeeded (0 warnings, 0 errors with TreatWarningsAsErrors=true)
- **Committed in:** `55cecff` (task commit)

---

**Total deviations:** 2 auto-fixed (both Rule 1 - Bug)
**Impact on plan:** Both necessary for correctness. No scope creep. Plan skeleton was an approximation; exact patterns taken from `HealthFallbackTests.fs`.

## Issues Encountered

- Initial test run produced 502 Bad Gateway for both QF-01 and QF-02 before the `/v1/models` path dispatch fix was applied. Root cause: `QwenUpstreamClient`'s lazy probe fires on first `CompleteAsync` call, not just at startup. Resolved by mirroring the `HealthFallbackTests.fs` pattern which already handled this.

## Next Phase Readiness

- 14-06 (docs): all quality fallback behavior is now covered by passing tests — documentation can accurately describe the tested scenarios
- Quality fallback test pattern (fake-Kestrel + stub IHealthProbe + TraceLogger triple-reg) is established for any future tests involving trace logging

---
*Phase: 14-quality-fallback-and-trace*
*Completed: 2026-05-10*
