---
phase: 14-quality-fallback-and-trace
plan: 04
subsystem: routing
tags: [quality-fallback, 35b-122b-retry, trace-logger, isBadResponse, non-streaming, fsharp, aspnetcore-di]

# Dependency graph
requires:
  - phase: 14-03
    provides: "RoutingReason.FallbackTo122B DU case, isBadResponse heuristic, QualityFallbackOptions, appsettings QualityFallback section"
  - phase: 14-02
    provides: "ITraceLogger interface + TraceLogger BackgroundService + TraceRecord type"
provides:
  - "Non-streaming branch quality fallback: 35B → 122B retry on isBadResponse returning true"
  - "DecisionLog final-decision semantics (target = final model, routing_reason = fallback_to_122b when fired)"
  - "Trace JSONL emission per request (when --trace-responses enabled)"
  - "Graceful degradation: 122B unreachable or retry fails → return 35B response as-is"
  - "QualityFallbackOptions registered as DI singleton (compile-order-safe pattern)"
affects:
  - "14-05 Tests (exercises this branch)"
  - "14-06 Docs (references quality fallback behavior)"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "GetService<ITraceLogger>() null-guard pattern: compile-time optional DI dependency without Option wrapper"
    - "Standalone DI singleton for sub-options (QualityFallbackOptions) to resolve F# fsproj compile-order constraint"
    - "versionProvider.CurrentVersion live read on retry decision (issue #12 pattern)"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs

key-decisions:
  - "QualityFallbackOptions registered as standalone DI singleton in CompositionRoot (not via IOptions<RoutingOptions>) because ChatCompletions.fs compiles before CompositionRoot.fs in fsproj order"
  - "traceLogger resolved via GetService (returns null when --trace-responses absent); null-guard with isNull (box traceLogger) avoids F# option wrapping overhead on hot path"
  - "qualityFallbackTriggered captured before the task{} block so trace emission can reference it after final response is determined"
  - "fallback_kind = 'availability' for IsFallback from Phase 10 reroute; 'quality' for Phase 14 trigger — mutually exclusive in current call flow"

patterns-established:
  - "Inline truncate helper at module top: returns empty string for null, appends ellipsis on truncation"
  - "INTENTIONALLY SKIPPED comment pattern for streaming branch quality-fallback exemption"

# Metrics
duration: 22min
completed: 2026-05-10
---

# Phase 14 Plan 04: ChatCompletions Quality Fallback Summary

**Non-streaming 35B → 122B quality retry with trace JSONL emission; streaming branch explicitly documented as exempt; QualityFallbackOptions DI singleton works around F# fsproj compile-order constraint**

## Performance

- **Duration:** 22 min
- **Started:** 2026-05-10T06:48Z
- **Completed:** 2026-05-10T07:10Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments

- Quality fallback branch added to non-streaming path: `qualityFallbackTriggered` flag checks `initialDecision.Target = Qwen35B && isBadResponse qualityFallbackOpts initialBody`; retries on 122B when reachable; falls back to 35B response when 122B is unreachable or the retry itself fails
- DecisionLog reflects final decision (`target`, `routing_reason`, `fallback_used` all sourced from `finalDecision`)
- Trace JSONL emitted (when `--trace-responses` enabled) with all 12 TraceRecord fields including `prompt_uid = promptHash[:12]`, `truncate 200` prompt excerpt, `truncate 500` response excerpts, `fallback_kind`, latency
- Streaming branch unchanged with explicit `INTENTIONALLY SKIPPED` comment documenting why quality fallback is not applied

## Task Commits

1. **Task 1: Quality fallback branch in non-streaming path + trace emission** - `38b75f3` (feat)

**Plan metadata:** (follows — docs commit)

## Files Created/Modified

- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — truncate helper; open QualityCheck + TraceLogger; non-streaming branch quality fallback + trace block; streaming comment
- `src/SmartRouter.Cli/CompositionRoot.fs` — QualityFallbackOptions DI singleton added in both configureRequestPipeline and configureWithoutMl

## Decisions Made

- **QualityFallbackOptions as standalone DI singleton**: `ChatCompletions.fs` (line 58 in fsproj) compiles before `CompositionRoot.fs` (line 63), so `open SmartRouter.Cli.CompositionRoot` is forbidden at compile time. Registering `QualityFallbackOptions` (defined in `QualityCheck.fs`, line 23) as a separate singleton lets the endpoint call `GetRequiredService<QualityFallbackOptions>()` without referencing `RoutingOptions` or `CompositionRoot`.
- **`isNull (box traceLogger)` null-check**: `ITraceLogger` is an interface; F# `isNull` on an interface requires boxing. This is the idiomatic null-guard pattern for optional DI services resolved via `GetService<T>()`.
- **`versionProvider.CurrentVersion` on retry**: Live read at retry time honors any model version updates since request start (issue #12 pattern, consistent with Phase 9).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] QualityFallbackOptions registered as standalone DI singleton**

- **Found during:** Task 1 (first build attempt)
- **Issue:** Plan specified `ctx.RequestServices.GetRequiredService<IOptions<RoutingOptions>>().Value` to get `QualityFallback` options, but `RoutingOptions` is defined in `CompositionRoot.fs` which compiles after `ChatCompletions.fs` — F# compile-order error FS0039.
- **Fix:** Registered `QualityFallbackOptions` as a standalone `AddSingleton<QualityFallbackOptions>` in both `configureRequestPipeline` and `configureWithoutMl`, sourced from `normalizeQualityFallback`. Handler uses `GetRequiredService<QualityFallbackOptions>()` instead. `QualityFallbackOptions` is in `QualityCheck.fs` (compiled at line 23, well before ChatCompletions.fs at line 58).
- **Files modified:** `CompositionRoot.fs`
- **Verification:** Build clean, dotnet build 0 warnings 0 errors
- **Committed in:** `38b75f3` (same task commit)

**2. [Rule 1 - Bug] `QualityCheck.isBadResponse` qualified name → unqualified `isBadResponse`**

- **Found during:** Task 1 (second build attempt after fix 1)
- **Issue:** `open SmartRouter.Cli.Adapters.QualityCheck` was added but code used `QualityCheck.isBadResponse` (qualified). With the module opened, the compiler couldn't resolve the module name alone as a qualifier.
- **Fix:** Changed to `isBadResponse qualityFallbackOpts initialBody` (unqualified, resolved via open).
- **Files modified:** `ChatCompletions.fs`
- **Verification:** Build clean on next attempt
- **Committed in:** `38b75f3`

**3. [Rule 1 - Bug] Streaming comment "INTENTIONALLY SKIPPED" split across two lines → joined on one line**

- **Found during:** Task 1 verification step
- **Issue:** Plan verify command `grep -c "INTENTIONALLY SKIPPED"` would return 0 because the original comment had the words on separate lines.
- **Fix:** Moved "SKIPPED" to end of line 1 of the comment: `// … is INTENTIONALLY SKIPPED\n// for streaming requests.`
- **Files modified:** `ChatCompletions.fs`
- **Verification:** `grep -c "INTENTIONALLY SKIPPED" … = 1`
- **Committed in:** `38b75f3`

---

**Total deviations:** 3 auto-fixed (1 blocking compile-order fix, 2 rule-1 bugs)
**Impact on plan:** All three were correctness/build fixes. No scope creep; all edge cases from the plan's spec are implemented exactly as described.

## Issues Encountered

- First test run showed 79 passed / 1 failed due to test timing flakiness (not our change); re-run confirmed 80 passed / 16 ignored / 0 failed baseline is intact.

## Next Phase Readiness

- `14-05` (Tests) can now exercise the quality fallback branch end-to-end using fake-Kestrel 35B/122B stubs and `--trace-responses` flag
- Both `qualityFallbackTriggered = true` (35B bad → 122B success) and `qualityFallbackTriggered = false` (35B good → no fallback) paths are live
- `ITraceLogger` null-guard pattern verified; trace JSONL will only appear when `--trace-responses` is set

---
*Phase: 14-quality-fallback-and-trace*
*Completed: 2026-05-10*
