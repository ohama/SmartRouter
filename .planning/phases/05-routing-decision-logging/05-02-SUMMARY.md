---
phase: 05-routing-decision-logging
plan: 02
subsystem: observability
tags: [fsharp, decision-logging, jsonl, sse, correlation-id, di, routing-algorithm]

# Dependency graph
requires:
  - phase: 05-01
    provides: DecisionLog record, IDecisionLogger, DecisionLogWriter BackgroundService, CorrelationMiddleware
  - phase: 04-02
    provides: RoutingAlgorithm DI singleton, ML/Heuristic algorithm seam
provides:
  - RoutingAlgorithmRegistration record in Adapters/RoutingAlgorithm.fs (Name + ModelVersion + Algorithm)
  - 8 decisionLogger.Log call sites in ChatCompletions.fs covering every exit path
  - SSE error event bodies include correlation_id field (LOG-04 third source)
  - escapeJsonString helper for safe JSON string interpolation in SSE events
affects: [05-03, future phases using DecisionLog JSONL data, Phase 9 canary cohort comparison]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "RoutingAlgorithmRegistration record: algorithm function + observable name + model_version in single DI-injectable record"
    - "Back-compat DI alias: AddSingleton<T> delegates to GetRequiredService<TRegistration>.Field to preserve existing test resolutions"
    - "DecisionLog at every exit: buildDecisionLog helper + decisionLogger.Log after write/dispose in each arm"
    - "SSE error correlation: escapeJsonString + sprintf injects correlation_id into every SSE error JSON body"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs

key-decisions:
  - "RoutingAlgorithmRegistration lives in its own Adapters/RoutingAlgorithm.fs file (not in CompositionRoot.fs) so ChatCompletions.fs (compile pos 14) can open it without F# compile-order violation — CompositionRoot is at compile pos 16"
  - "streamError flag in streaming branch: tracks whether the Error arm was hit during the enumerator loop so the normal-exit DisposeAsync path can still set reason to 'stream_error' without a second try/with"
  - "SSE error events use escapeJsonString before sprintf to prevent JSON injection from upstream error messages containing quotes or newlines"
  - "Streaming path logs AFTER enumerator.DisposeAsync() in all 3 try/with arms so latency_ms reflects time-to-last-byte not time-to-first-byte"

patterns-established:
  - "DecisionLog emission pattern: capture started + correlationId at top; call decisionLogger.Log(buildDecisionLog ...) as LAST statement in each exit arm after all I/O"
  - "F# compile-order for shared types: types used by both endpoint and composition root must live in an Adapters/ file before both in .fsproj"

# Metrics
duration: 6min
completed: 2026-05-08
---

# Phase 05 Plan 02: Endpoint Wiring Summary

**DecisionLog emitted at 8 ChatCompletions exit points with RoutingAlgorithmRegistration (Name + ModelVersion) and correlation_id in SSE error bodies, producing 12-field JSONL lines at every request exit**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-05-08T06:08:07Z
- **Completed:** 2026-05-08T06:14:07Z
- **Tasks:** 2
- **Files modified:** 4 (1 created, 3 modified)

## Accomplishments

- New `Adapters/RoutingAlgorithm.fs` defines `RoutingAlgorithmRegistration = { Algorithm; Name; ModelVersion }` in its own file so both `ChatCompletions.fs` (compile pos 14) and `CompositionRoot.fs` (compile pos 16) can open it without F# compile-order violation
- CompositionRoot.fs registers `RoutingAlgorithmRegistration` singleton with `null|""|"heuristic" -> heuristic-v1` and `"ml" -> ml-v0-placeholder` dispatch; backwards-compat `AddSingleton<RoutingAlgorithm>` alias delegates to `.Algorithm` keeping MLRoutingTests Tests 4+5 passing
- ChatCompletions.fs: 8 `decisionLogger.Log` call sites covering every exit path (null-body, UnsupportedTask 400, generic Error 400, streaming normal, streaming cancel, streaming unexpected-ex, non-streaming Ok 200, non-streaming 502); streaming path logs AFTER `enumerator.DisposeAsync()` in all 3 arms so `latency_ms` reflects time-to-last-byte
- SSE upstream-error event body upgraded to include `correlation_id` field (`"data: {\"error\":{\"message\":\"%s\",\"type\":\"upstream_error\",\"correlation_id\":\"%s\"}}\n\n"`) satisfying LOG-04's third source; `escapeJsonString` helper prevents JSON injection
- Smoke test confirms: single curl produces 12-field JSONL line with all required keys; 502 response expected (Qwen not running); routing_algorithm = "heuristic", model_version = "heuristic-v1"

## Task Commits

1. **Task 1: Extract RoutingAlgorithmRegistration into Adapters/RoutingAlgorithm.fs + DI registration** - `f311a1f` (feat)
2. **Task 2: Wire DecisionLog at every ChatCompletions exit point** - `2f47b71` (feat)

**Plan metadata:** (pending)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs` - New file: `RoutingAlgorithmRegistration` record type in module `SmartRouter.Cli.Adapters.RoutingAlgorithm`
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` - Inserted `Adapters/RoutingAlgorithm.fs` AFTER `CorrelationMiddleware.fs` and BEFORE `QwenUpstreamClient.fs`
- `src/SmartRouter.Cli/CompositionRoot.fs` - Added `open SmartRouter.Cli.Adapters.RoutingAlgorithm`; replaced bare `AddSingleton<RoutingAlgorithm>` with `AddSingleton<RoutingAlgorithmRegistration>` + back-compat alias
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` - Handler refactored: `regn: RoutingAlgorithmRegistration` + `decisionLogger: IDecisionLogger`; `buildDecisionLog` + `escapeJsonString` helpers; 8 log call sites; SSE error includes `correlation_id`

## Decisions Made

- `RoutingAlgorithmRegistration` lives in its own `Adapters/RoutingAlgorithm.fs` file (not in `CompositionRoot.fs`) so `ChatCompletions.fs` (compile pos 14) can open it without F# compile-order violation — `CompositionRoot.fs` is at compile pos 16, too late for `ChatCompletions.fs` to reference types defined there.
- A `streamError` mutable flag in the streaming branch tracks whether the upstream-error SSE arm fired during the enumerator loop, so the normal-exit disposal path can append `;stream_error` to the reason string without needing a second try/with layer.
- `escapeJsonString` helper is defined as a private function in `ChatCompletions.fs` (not in `DecisionLogger.fs`) because it's used only in SSE string interpolation, not in JSONL serialization (which uses STJ).
- Streaming path logs AFTER `enumerator.DisposeAsync()` in all three try/with arms per plan spec (latency_ms reflects time-to-last-byte, not time-to-first-byte).

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## Verification Results

- `dotnet build SmartRouter.slnx`: 0 errors, 0 warnings
- `44/44` tests pass (2 pending/ignored as expected)
- `grep -c "decisionLogger.Log" ChatCompletions.fs` = 8 (≥7 required)
- SSE error literal at ChatCompletions.fs:242 includes `correlation_id` field
- `scripts/check-no-async.sh`: OK
- `scripts/check-routing-isolation.sh`: OK
- Smoke test JSONL line keys: `correlation_id,fallback_used,latency_ms,model_version,prompt_hash,prompt_korean_char_ratio,routing_algorithm,routing_reason,schema_version,target,task_type,timestamp` (all 12 fields)

### Sample JSONL Line from Smoke Test

```json
{"schema_version":1,"correlation_id":"d25b8a32fd9148cd8a261a97a3029dc2","prompt_hash":"8f434346648f6b96df89dda901c5176b10a6d83961dd3c1ac88b59b2dc327aa4","prompt_korean_char_ratio":0,"routing_algorithm":"heuristic","routing_reason":"heuristic:score=0;upstream_error","target":"Qwen35B","latency_ms":165.82,"fallback_used":false,"model_version":"heuristic-v1","task_type":null,"timestamp":"2026-05-08T06:12:24.720171+00:00"}
```

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready for 05-03: DecisionLog integration tests (streaming SSE correlation_id test, null-body log test, etc.) + StreamingTests temp-dir hygiene
- LOG-04 fully wired: (a) JSONL correlation_id from CorrelationMiddleware, (b) Serilog LogContext from CorrelationMiddleware, (c) SSE error body correlation_id from this plan
- 44/44 tests pass with 0 warnings; check scripts both pass
- No blockers

---
*Phase: 05-routing-decision-logging*
*Completed: 2026-05-08*
