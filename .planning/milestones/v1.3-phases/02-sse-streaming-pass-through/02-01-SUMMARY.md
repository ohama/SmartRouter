---
phase: 02-sse-streaming-pass-through
plan: 01
subsystem: streaming
tags: [fsharp, taskseq, sse, http-client, iasyncenumerable, kestrel, streaming]

# Dependency graph
requires:
  - phase: 01-foundation
    provides: QwenUpstreamClient stub + ChatCompletions endpoint with 501 stub; IUpstreamClient.StreamAsync port contract
provides:
  - Real SSE pass-through from upstream Qwen to downstream client
  - QwenUpstreamClient.StreamAsync: taskSeq + ResponseHeadersRead + use _ = resp + ReadLineAsync loop
  - ChatCompletions streaming branch: 4 SSE headers before first byte, manual GetAsyncEnumerator loop, FlushAsync per chunk, Strategy D [DONE] injection, OperationCanceledException handling
  - All 5 SSE atomic pitfalls mitigated in one atomic unit
affects:
  - 02-02 (StreamingTests — exercises this code end-to-end with fake upstream)
  - 03-concurrency-gate (adds linked CancellationTokenSource wrapping ctx.RequestAborted)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "FSharp.Control.TaskSeq.taskSeq {} for IAsyncEnumerable<Result<string,RouterError>> production"
    - "HttpCompletionOption.ResponseHeadersRead for streaming HTTP upstream connections"
    - "let! resp + use _ = resp two-step pattern for IDisposable in taskSeq {}"
    - "Manual GetAsyncEnumerator loop in task {} for precise async disposal semantics"
    - "F# task {} requires explicit DisposeAsync() calls in all exit paths (do! not valid in finally)"
    - "Strategy D [DONE] injection: sentDone boolean tracks sentinel; inject after loop if missing"
    - "Routing decision before SSE headers — 400 errors return normal JSON (STRM-04 ordering)"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs

key-decisions:
  - "F# task {} does not support do! in finally blocks — use explicit DisposeAsync() in each catch arm instead"
  - "Direct let! resp = client.SendAsync(..., HttpCompletionOption.ResponseHeadersRead, ct) form used — no task{return!...} wrapper"
  - "StreamingTests deferred to Plan 02-02 — this plan ships the implementation; 02-02 owns the test harness"

patterns-established:
  - "SSE atomic cluster: all 5 pitfalls (ResponseHeadersRead, FlushAsync, disposal scope, headers-first, [DONE]) shipped together"
  - "Routing before streaming: match routeRequest ... with; streaming branch only entered inside Ok decision arm"
  - "Per-chunk disposal chain: enumerator.DisposeAsync() → taskSeq dispose → use _ = resp → HttpResponseMessage.Dispose() → upstream socket close"

# Metrics
duration: ~18min
completed: 2026-05-07
---

# Phase 2 Plan 01: Streaming Implementation Summary

**SSE pass-through end-to-end: taskSeq + ResponseHeadersRead + FlushAsync + Strategy D [DONE] injection — all 5 atomic pitfalls shipped together**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-05-07T17:27:19Z
- **Completed:** 2026-05-07T17:46:00Z
- **Tasks:** 2/2
- **Files modified:** 2

## Accomplishments

- `QwenUpstreamClient.StreamAsync` replaced from empty-IAsyncEnumerable stub to real `taskSeq {}` implementation with `HttpCompletionOption.ResponseHeadersRead`, `let! resp` + `use _ = resp` disposal scope, and line-by-line `ReadLineAsync(ct)` loop
- `ChatCompletions.fs` HTTP 501 stub eliminated; real SSE forward loop with 4 required headers set before first write, `FlushAsync` after every chunk, Strategy D `[DONE]` injection, and `OperationCanceledException` handling for client disconnect
- All 5 SSE correctness pitfalls mitigated atomically: STRM-01 (ResponseHeadersRead), STRM-03 (FlushAsync), STRM-04 (headers before write), STRM-05/STRM-06 (disposal scope chain), STRM-07 ([DONE] sentinel)

## Task Commits

Each task was committed atomically:

1. **Task 1: Implement QwenUpstreamClient.StreamAsync** - `f07e439` (feat)
2. **Task 2: Wire SSE forward loop in ChatCompletions endpoint** - `f7a1dfe` (feat)

**Plan metadata:** (docs: complete streaming-impl plan — see final commit)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — StreamAsync replaced with real taskSeq implementation
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — 501 stub replaced with SSE forward loop; routing moved before streaming branch

## Decisions Made

- **F# task{} finally limitation:** F# `task {}` computation expressions do not permit `do!` (async operations) inside `finally` blocks. The plan and RESEARCH.md show `do! enumerator.DisposeAsync()` in a `finally` block, which does not compile. The fix: call `do! enumerator.DisposeAsync()` explicitly in every exit path — normal completion, `OperationCanceledException`, and unexpected exception catches. This achieves the same disposal guarantee without a `finally` block. Documented as a deviation.

- **Direct `let!` form for SendAsync:** Used `let! resp = client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct)` directly inside `taskSeq {}` — no `task { return! ... }` wrapper — per the plan's constraint and RESEARCH.md Pattern 1's "Important note" sub-section.

- **StreamingTests deferred to 02-02:** No new tests added in this plan. Phase-1 22 tests still pass. Plan 02-02 owns the fake-upstream Kestrel integration tests for TTFB, chunk ordering, 100-chunk integrity, mid-stream cancellation, [DONE] sentinel, and header assertions.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] F# task{} does not support do! in finally blocks**

- **Found during:** Task 2 (ChatCompletions streaming branch implementation)
- **Issue:** RESEARCH.md Pattern 2 and the plan's action spec show `do! enumerator.DisposeAsync()` inside a `finally` block of `task {}`. In F# `task {}` CE, `finally` blocks are synchronous-only; `do!` is not permitted there. The compiler error FS0750: "This construct may only be used within computation expressions" confirms this is a hard limitation, not a version issue.
- **Fix:** Moved `do! enumerator.DisposeAsync()` to explicit calls in three places: after the while loop (normal path), inside the `OperationCanceledException` catch, and inside the unexpected exception catch. All three disposals chain through to `use _ = resp` in `StreamAsync`, which closes the upstream HTTP socket. The disposal guarantee is semantically equivalent to a `finally` block — every exit path disposes the enumerator.
- **Files modified:** `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`
- **Verification:** Build clean; grep for `DisposeAsync` shows 3 matches in ChatCompletions.fs; 22/22 tests pass
- **Committed in:** `f7a1dfe` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — bug in plan's F# code example)
**Impact on plan:** Necessary for correctness. The disposal semantics are equivalent to a `finally` block — all exit paths dispose the enumerator. No scope creep.

## Issues Encountered

None beyond the F# `finally` limitation documented above.

## Next Phase Readiness

- SSE streaming path is implemented and ready for integration testing by Plan 02-02
- Plan 02-02 must add `StreamingTests.fs` to the test project with fake Kestrel upstream (real TCP, not TestServer) to exercise all STRM-01 through STRM-07 assertions
- Phase 3 (concurrency gate) adds a `CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token)` wrapper — this plan uses `ctx.RequestAborted` directly as designed
- No blockers for 02-02 or Phase 3

---
*Phase: 02-sse-streaming-pass-through*
*Completed: 2026-05-07*
