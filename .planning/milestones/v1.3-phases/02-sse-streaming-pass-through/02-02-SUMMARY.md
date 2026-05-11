---
phase: 02-sse-streaming-pass-through
plan: 02
subsystem: testing
tags: [expecto, kestrel, sse, streaming, fsharp, aspnetcore, integration-tests]

# Dependency graph
requires:
  - phase: 02-sse-streaming-pass-through plan 01
    provides: StreamAsync with taskSeq+ResponseHeadersRead, ChatCompletions SSE forward loop with FlushAsync/[DONE]-injection
provides:
  - 8 streaming integration tests against real Kestrel fake upstream proving all 5 SSE pitfalls mitigated
  - startFakeUpstream helper (Kestrel on 127.0.0.1:0, /v1/models + /v1/chat/completions mapped, RequestAborted hook)
  - startTestRouter helper (AddInMemoryCollection-before-configureServices, mapEndpoints, Kestrel on 127.0.0.1:0)
  - Complete Phase 2 atomic correctness unit: both streaming implementation AND streaming test coverage
affects:
  - Phase 3 (concurrency gate): test helper pattern for in-process router is reusable
  - Phase 5 (observability): streaming metrics tests can extend StreamingTests.fs

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Fake upstream Kestrel on 127.0.0.1:0 (not TestServer) for SSE integration tests — real TCP for TTFB/backpressure accuracy"
    - "AddInMemoryCollection BEFORE configureServices — overrides bind here; IOptions<UpstreamOptions> binds to test URLs"
    - "teardown helper using .GetAwaiter().GetResult() for StopAsync/DisposeAsync in F# task{} finally blocks (do! not allowed in finally)"
    - "Client TCP socket disposal (response.Dispose()) to trigger ctx.RequestAborted in router for cancellation test"
    - "testSequenced wrapper on streaming testList — prevents parallel port contention"

key-files:
  created:
    - tests/SmartRouter.Tests/StreamingTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "ConfigurationManager.AddInMemoryCollection requires explicit cast to IConfigurationBuilder — extension method resolves only on interface"
  - "F# task{} finally blocks do not allow do!; StopAsync/DisposeAsync must use .GetAwaiter().GetResult() for synchronous teardown"
  - "Mid-stream cancellation test uses response.Dispose() (closes TCP socket) not CancellationToken.Cancel() — ctx.RequestAborted fires on socket close, not token cancel"
  - "Fake upstream returns /fake/model with leading / — satisfies QwenUpstreamClient path-like id heuristic, avoids HF-id trap during tests"

patterns-established:
  - "Pattern: in-process router test = AddInMemoryCollection (FIRST) + configureServices + mapEndpoints + StartAsync"
  - "Pattern: 8-test streaming cluster grouped in testSequenced wrapping full testList"

# Metrics
duration: 6min
completed: 2026-05-07
---

# Phase 2 Plan 02: Streaming Tests Summary

**8 real-Kestrel SSE integration tests proving all 5 streaming pitfalls mitigated: TTFB, ordering, 100-chunk integrity, mid-stream cancellation, [DONE] forwarded/injected, SSE headers, routing-error-before-streaming**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-05-07T11:58:07Z
- **Completed:** 2026-05-07T12:04:44Z
- **Tasks:** 2 of 2
- **Files modified:** 3

## Accomplishments

- 8 streaming integration tests covering all 5 SSE pitfalls, running against real Kestrel fake upstream
- startFakeUpstream helper: Kestrel on 127.0.0.1:0, /v1/models + POST /v1/chat/completions with configurable chunk count, delay, [DONE] emission, and RequestAborted hook for cancellation test
- startTestRouter helper: in-process SmartRouter.Cli WebApplication with AddInMemoryCollection-before-configureServices ordering (the critical config bind sequencing), mapEndpoints registration
- Phase 2 atomic correctness unit complete: implementation (02-01) + tests (02-02) ship together; all 5 pitfalls have both a code mitigation AND a test that fails if the mitigation regresses

## Test List

| # | Test Name | REQ/Pitfall | What it Proves |
|---|-----------|-------------|----------------|
| 1 | TTFB under 2 s | STRM-01, STRM-03 | ResponseHeadersRead sends first byte < 2000ms; FlushAsync fires per chunk |
| 2 | chunks arrive in order | STRM-02 | 10 chunks 0..9 arrive in order, no reordering |
| 3 | 100-chunk integrity | STRM-02 reliability | 100 chunks: no loss, no duplication, byte-equal to upstream emissions |
| 4 | mid-stream cancellation aborts upstream | STRM-05, STRM-06, PITFALL-4 | Socket close → ctx.RequestAborted fires in fake upstream within 5s; no ObjectDisposedException |
| 5 | [DONE] forwarded when upstream emits it | STRM-07 forward path | Exactly one [DONE], it is the final element |
| 6 | [DONE] injected when upstream omits it | STRM-07 inject path (Strategy D) | Router synthesizes [DONE] when upstream omits it; exactly one [DONE] |
| 7 | response headers are SSE | STRM-04, PITFALL-6 | Content-Type: text/event-stream, Cache-Control: no-cache, no Content-Length |
| 8 | routing error returns HTTP 400 not SSE | TEST-03 order-of-operations | stream=true + task=foobar → HTTP 400 JSON (routing runs before SSE headers) |

**Total runtime:** ~4.7s for all 8 streaming tests (sequential, testSequenced)

## Fake Upstream Architecture

- `startFakeUpstream chunkCount delayMs emitDone abortTcs`: Kestrel on 127.0.0.1:0
  - GET /v1/models → `{ data: [{ id: "/fake/model" }] }` (path-like id satisfies QwenUpstreamClient probe)
  - POST /v1/chat/completions → emits chunks as `data: {"choices":[{"delta":{"content":"N"}}]}` with optional inter-chunk delay; optional `data: [DONE]`; hooks `ctx.RequestAborted.Register` if `abortTcs` provided
- Port resolved via `IServer → IServerAddressesFeature → Addresses |> Seq.head`

## startTestRouter Helper — Critical Ordering

```
Step 1: WebApplication.CreateBuilder() + UseUrls("http://127.0.0.1:0")
Step 2: (testBuilder.Configuration :> IConfigurationBuilder).AddInMemoryCollection([...])
        // MUST happen before configureServices — overrides bind here
        // cast to IConfigurationBuilder required — extension method on interface, not ConfigurationManager
Step 3: CompositionRoot.configureServices testBuilder.Services testBuilder.Configuration
Step 4: testBuilder.Build() + ChatCompletions.mapEndpoints app   (without this ALL 8 tests 404)
Step 5: app.StartAsync()
```

The in-memory collection provides: Upstreams:Model35B/122B pointing to fake port, full Routing section (TaskTable, ModelAliases, Keywords, thresholds) required by buildRoutingConfig + validateConfig.

## Task Commits

1. **Task 1: Wire test project (fsproj + stub + rootTests)** — `73b6955` (chore)
2. **Task 2: Author StreamingTests.fs — 8 real tests** — `71e1d46` (test)

**Plan metadata:** (in this docs commit)

## Files Created/Modified

- `tests/SmartRouter.Tests/StreamingTests.fs` — 8-test streaming module (474 lines); helpers: startFakeUpstream, startTestRouter, readSseChunks, postStreamRequest, teardown
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — FrameworkReference Microsoft.AspNetCore.App, ProjectReference SmartRouter.Cli, StreamingTests.fs compile entry between RoutingTests and RouterTests
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests updated to include SmartRouter.Tests.StreamingTests.tests

## Decisions Made

- **IConfigurationBuilder cast required**: `testBuilder.Configuration` is `ConfigurationManager` which implements `IConfigurationBuilder` but the `AddInMemoryCollection` extension method only resolves on the interface; explicit cast `(testBuilder.Configuration :> IConfigurationBuilder)` needed.
- **F# task{} finally = synchronous only**: F# `task {}` finally blocks do not allow `do!`. `StopAsync()` and `DisposeAsync()` called via `.GetAwaiter().GetResult()` in a `teardown` helper. Semantically correct for test teardown.
- **Cancellation via socket close, not token cancel**: `ctx.RequestAborted` in Kestrel fires when the client's TCP connection closes, not when a CancellationToken is cancelled. The cancellation test disposes `response` (which closes the socket) rather than calling `cts.Cancel()`.
- **Routing section in AddInMemoryCollection**: `configureServices` calls `validateConfig` at build time (via DI singleton factory), which validates all canonical tasks are present. The test's in-memory config must supply the full Routing section.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] IConfigurationBuilder cast for AddInMemoryCollection**
- **Found during:** Task 2 (startTestRouter helper)
- **Issue:** `testBuilder.Configuration.AddInMemoryCollection(...)` failed — `ConfigurationManager` doesn't expose the extension method directly
- **Fix:** Cast to `IConfigurationBuilder` explicitly: `(testBuilder.Configuration :> IConfigurationBuilder).AddInMemoryCollection([...])`
- **Files modified:** tests/SmartRouter.Tests/StreamingTests.fs
- **Verification:** Build clean, test router starts with correct config
- **Committed in:** 71e1d46

**2. [Rule 1 - Bug] F# task{} finally blocks reject do!**
- **Found during:** Task 2 (test teardown pattern)
- **Issue:** `do! routerApp.StopAsync()` inside `finally` block caused FS0750 compiler error — F# task CE does not support `do!` in `finally`
- **Fix:** Extracted `teardown` helper calling `.GetAwaiter().GetResult()` for async stop/dispose calls; each test's `finally` calls `teardown routerApp fakeApp`
- **Files modified:** tests/SmartRouter.Tests/StreamingTests.fs
- **Verification:** Build clean, teardown works correctly
- **Committed in:** 71e1d46

**3. [Rule 1 - Bug] Mid-stream cancellation requires socket close, not token cancel**
- **Found during:** Task 2 (mid-stream cancellation test failing)
- **Issue:** `cts.Cancel()` did not trigger fake upstream's `ctx.RequestAborted` within 5s — token cancellation doesn't close the TCP socket
- **Fix:** After reading 5 chunks, explicitly dispose `reader`, `stream`, and `response` to close the TCP socket. Kestrel detects socket close → `ctx.RequestAborted` fires in router → propagates through `StreamAsync` → fake upstream's `RequestAborted` fires
- **Files modified:** tests/SmartRouter.Tests/StreamingTests.fs
- **Verification:** Test passes; abortTcs fires within ~200ms of response.Dispose() (well under 5s)
- **Committed in:** 71e1d46

---

**Total deviations:** 3 auto-fixed (all Rule 1 — bugs discovered during implementation)
**Impact on plan:** All 3 fixes essential for correctness. No scope creep. Plan test intent fully realized.

## Issues Encountered

All issues were auto-fixed (see Deviations). No blocking issues that required architectural decisions or user input.

## Phase 2 Pitfall Coverage Map

| Pitfall | Code Mitigation (02-01) | Test (02-02) | Would fail if mitigation removed |
|---------|------------------------|--------------|----------------------------------|
| PITFALL-2 / STRM-01 | `HttpCompletionOption.ResponseHeadersRead` | Test 1 (TTFB) | TTFB would exceed 2s — buffered |
| PITFALL-3 / STRM-03 | `FlushAsync` after each chunk | Test 1 + Test 2 | Chunks would clump — no per-chunk flush |
| PITFALL-4 / STRM-06 | `use _ = resp` covers entire read loop | Test 4 (mid-stream cancel) | `ObjectDisposedException` on concurrent read+dispose |
| PITFALL-6 / STRM-04 | SSE headers set before first WriteAsync | Test 7 (header assertions) | `text/event-stream` not visible in response |
| PITFALL-19 / STRM-07 | Strategy D `sentDone` flag + injection | Test 5 (forwarded) + Test 6 (injected) | Double [DONE] or missing [DONE] |
| STRM-02 | ReadLineAsync line-level yielding | Test 2 + Test 3 | Out-of-order or split SSE events |
| STRM-05 | `ctx.RequestAborted` → ct chain | Test 4 (cancel) | Upstream connection not closed on client disconnect |

## Next Phase Readiness

Phase 2 is **complete**. Both the streaming implementation (02-01) and streaming tests (02-02) are green. Phase 3 (concurrency gate) can begin:
- Phase 3 depends on Phase 1 only (STATE.md Accumulated Decisions); no Phase 2 dependency
- The in-process test router pattern from startTestRouter is reusable for Phase 3 concurrency tests
- `use _ = resp` + `enumerator.DisposeAsync()` disposal chain is proven correct by Test 4

---
*Phase: 02-sse-streaming-pass-through*
*Completed: 2026-05-07*
