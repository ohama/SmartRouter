# Project Research Summary

**Project:** Smart Router
**Domain:** F# .NET 10 OpenAI-compatible LLM gateway (local, dual-model, task-typed routing)
**Researched:** 2026-05-07
**Confidence:** HIGH

---

## Executive Summary

Smart Router is a purpose-built F# .NET 10 reverse proxy that fronts two local Qwen models — 35B (fast, `localhost:8000`) and 122B (slow/powerful, `localhost:8001`) — behind a single OpenAI-compatible endpoint at `localhost:4000`. The router's core job is a three-stage decision pipeline: honor an explicit `model` override, then honor an explicit `task` field (used by Graphify), then fall back to a keyword/length heuristic with an aggressive 35B bias (used by Hermes). No comparable product — LiteLLM, Ollama, vLLM, OpenRouter — implements deterministic task-typed routing plus a gateway-level serial execution gate plus a priority queue for local model protection. These three together are what justify building rather than buying.

The architecture mirrors blueCode, the companion F# project on the same host: hexagonal layout with a pure Core (Domain.fs + Routing.fs + Ports.fs) and Cli adapters (QwenUpstreamClient, QueueDispatcher, HealthAdapter, endpoints). Several adapters can be lifted verbatim from blueCode — `QwenHttpClient.fs`, `Json.fs`, `Logging.fs` — bringing hard-won operational knowledge (HF-id trap defense, 300s timeout, sampling-param defaults) forward at zero re-research cost. Core must be rewritten from scratch because blueCode's Core is an agent loop, not a router.

The dominant risks are clustered in two areas. First, the SSE pass-through path has five interlocking pitfalls (no `ResponseHeadersRead`, no flush-per-chunk, wrong disposal scope, missing headers, chunk reframing) that must all ship correctly in a single phase — splitting them across phases leaves the streaming path in a broken intermediate state. Second, the 122B concurrency gate (SemaphoreSlim, priority dispatcher, cancellation linkage, per-request timeout CTS) has three interdependent correctness requirements that must also ship together: a semaphore leak on cancellation, FIFO ordering ignoring priority, and upstream hang holding the slot forever are all the same logical failure mode.

---

## Key Findings

### Recommended Stack

The stack is almost entirely fixed by PROJECT.md constraints and blueCode precedent. F# .NET 10 + ASP.NET Core Minimal API is locked. The serialization chain (`System.Text.Json` + `FSharp.SystemTextJson 1.4.36`) and logging chain (Serilog 4.3.1 + Sinks.Console 6.1.1) are pinned to blueCode-verified versions. Test framework is Expecto 10.2.1. Raw `WebApplication.MapPost` is correct for a four-endpoint gateway — Falco, Saturn, and Giraffe add abstraction cost without benefit. `Microsoft.Extensions.Http.Resilience` replaces deprecated `Microsoft.Extensions.Http.Polly` for the retry pipeline. SSE forwarding uses `HttpClient.SendAsync` with `HttpCompletionOption.ResponseHeadersRead` plus raw byte-buffer pass-through — no YARP (overkill), no SSE library (parses when the router should pass through verbatim). Deployment is a self-contained binary (`dotnet publish -c Release -r osx-arm64 --self-contained`) referenced from a launchd plist, not `dotnet run`.

See STACK.md for the full package list, version confidence table, and verification commands.

**Core technologies:**
- F# / .NET 10 + ASP.NET Core Minimal API — runtime and HTTP hosting — fixed by constraint; raw `MapPost`/`MapGet` for 4 endpoints
- `System.Text.Json` + `FSharp.SystemTextJson 1.4.36` — serialization — blueCode-verified; handles F# DUs/options/lists
- `Microsoft.Extensions.Http` (IHttpClientFactory) + `Microsoft.Extensions.Http.Resilience` — named HTTP clients per upstream + retry pipeline
- `FSharp.Control.TaskSeq` (~0.4.3) — `IAsyncEnumerable` iteration for SSE chunk-level logging/inspection
- Serilog 4.3.1 + Sinks.Console 6.1.1 + Serilog.AspNetCore — structured logging to stderr — blueCode-verified versions
- `FsToolkit.ErrorHandling` (~4.x) — `result {}` / `taskResult {}` CE for routing pipeline composition
- `System.Collections.Generic.PriorityQueue<T,int>` + `TaskCompletionSource` waiter pattern — two-level 122B queue — BCL, no extra package
- Expecto 10.2.1 + `Microsoft.AspNetCore.Mvc.Testing` — test framework — blueCode-verified version

**Version note (MEDIUM confidence):** `Serilog.AspNetCore`, `Microsoft.Extensions.Http.Resilience`, `FsToolkit.ErrorHandling`, `FSharp.Control.TaskSeq`, and `Microsoft.AspNetCore.Mvc.Testing` should be verified at scaffold time: `dotnet package search <name> --take 1`. Exact minor versions may have advanced since training cutoff.

### Expected Features

No comparable product implements the three differentiating features together: deterministic `task`-field routing, a gateway-level per-model serial execution gate, and a priority queue. These are unique to the local dual-model mlx_lm constraint and must not be deferred.

See FEATURES.md for the full competitor matrix and feature dependency graph.

**Must have (table stakes) — router fails without these:**
- `POST /v1/chat/completions` — parse + proxy + SSE pass-through; Hermes hangs without streaming
- Request field preservation — Hermes sends `stream_options: {include_usage: true}`; stripping breaks it silently
- `GET /health`, `GET /v1/models`, `GET /stats` — required by Graphify spec; cheap now, expensive to retrofit
- HttpClient 300s timeout — 122B cold-start reaches 240s; default 100s fails every cold start
- Cancellation token propagation — Hermes drops connections mid-stream; orphaned 122B calls hold the semaphore
- Sampling-param defaults (temp=0.7, top_p=0.8, top_k=20) — mlx_lm.server behaves incorrectly without explicit values
- HF-id trap defense (`tryParseModelId`) — sending the HF repo id overwrites the loaded tokenizer; responses become FIM garbage
- OpenAI error shape (`{"error": {"message": "...", "type": "..."}}`) — OpenAI SDK clients parse this; bare ASP.NET problem details break them

**Should have (differentiators) — reason the router exists:**
- Explicit `task` field routing — deterministic, zero-overhead; no semantic similarity approximation needed
- Two-level priority queue for 122B — graph_indexing must preempt lighter work; no comparable implements this at gateway level
- `SemaphoreSlim(1)` on 122B — prevents `[METAL] Insufficient Memory`; cheaper than letting mlx_lm serialize at the metal
- `graph_indexing` no-fallback rule — must ship with `graph_indexing` routing; silent quality regression is worse than an error
- Heuristic fallback for Hermes — keyword + prompt length + code-block + message count; aggressive 35B bias for ambiguous cases
- Backend health probing + retry — powers fallback decisions; required for Graphify reliability requirements
- `GET /stats` — queue depth, wait time, requests/sec, failure count; Graphify spec requires it

**Defer (v2+):**
- Prometheus `/metrics` — add only when a scraper actually arrives
- Circuit breaker — add only when a recurring failure mode needing "open" state is observed
- ML/learned routing — revisit after `/stats` + structured logs accumulate decision data
- Rate limiting — single host, two known clients, no abuse vector

**Critical scoping note:** `graph_indexing` routing and the `graph_indexing` no-fallback rule are a single correctness unit. They must ship in the same phase. A `graph_indexing` route that silently falls back to 35B produces durable but lower-quality index artifacts that poison downstream retrieval for the lifetime of the index.

### Architecture Approach

The architecture is a strict hexagonal mirror of blueCode: pure `SmartRouter.Core` (no ASP.NET, no HttpClient, no Serilog references) and `SmartRouter.Cli` containing all I/O adapters and ASP.NET endpoints. The routing pipeline is a three-stage pure function (`routeRequest`) with exhaustive DU matching — no `| _ ->` catch-alls anywhere. `QueueDispatcher` wraps `IUpstreamClient` as a decorator (not merged into `QwenUpstreamClient`) so concurrency policy and HTTP mechanics are independently testable. Health fallback policy lives in the adapter layer (QueueDispatcher), not Core, because health probing is I/O.

See ARCHITECTURE.md for concrete F# type signatures, the full request lifecycle sequence diagram, cancellation propagation chain, and the blueCode reuse vs. rewrite decision table.

**Major components:**
1. `SmartRouter.Core/Domain.fs` — all DUs and record types (`ModelId`, `Priority`, `TaskType`, `RoutingDecision`, `RouterRequest`, `RouterError`); pure, no I/O
2. `SmartRouter.Core/Routing.fs` — `routeRequest` three-stage pipeline (tryModelOverride → tryTaskTable → applyHeuristic); pure functions; exhaustive matches over `TaskType` DU
3. `SmartRouter.Core/Ports.fs` — `IUpstreamClient`, `IClock`, `IHealthProbe` interfaces; Core boundary
4. `SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — HTTP forwarding to Qwen ports; HF-id probe; `ResponseHeadersRead` streaming; copy from blueCode
5. `SmartRouter.Cli/Adapters/QueueDispatcher.fs` — `SemaphoreSlim(1)` on 122B; two-level priority dispatcher; cancellation-safe `finally Release()`; fallback policy
6. `SmartRouter.Cli/Adapters/HealthAdapter.fs` — polls upstream `/v1/models`; exposes reachability for `/health` and QueueDispatcher fallback
7. `SmartRouter.Cli/Endpoints/ChatCompletions.fs` — parses request; calls `routeRequest`; dispatches to `IUpstreamClient`; SSE forward loop with per-chunk flush
8. `SmartRouter.Cli/Endpoints/{Health,Models,Stats}.fs` — lightweight endpoints backed by in-process counters
9. `SmartRouter.Cli/CompositionRoot.fs` + `Program.fs` — DI wiring; WebApplication builder; middleware

### Critical Pitfalls

27 pitfalls documented across five thematic clusters. Full detail in PITFALLS.md. Highest-severity:

1. **HF-id fallback trap** (PITFALL-1) — sending the HF repo id in the POST `model` field overwrites the Instruct tokenizer with Base Coder; all responses become FIM garbage. Prevention: copy `tryParseModelId` verbatim from blueCode; probe `/v1/models` on startup; prefer the id that starts with `/`. Must be solved before any upstream call.

2. **SSE pass-through cluster — all five must ship together** (PITFALLS 2, 3, 4, 6, 7) — Missing `ResponseHeadersRead` buffers entire body. Missing per-chunk `FlushAsync` causes burst delivery. Early `HttpResponseMessage` disposal truncates with `ObjectDisposedException`. Missing SSE headers breaks client parse mode. Attempting to reframe events splits `data: ...\n\n` boundaries. All five are correctness failures on every streaming request. Must all be addressed in a single phase.

3. **SemaphoreSlim concurrency cluster — all three must ship together** (PITFALLS 8, 9, 11) — Missing `finally Release()` after cancellation leaves semaphore at count 0 forever. Calling `SemaphoreSlim.WaitAsync` directly bypasses the priority queue (FIFO ordering). Missing per-request timeout CTS leaves the semaphore held during upstream hangs. All three produce the same failure mode: 122B capacity permanently stuck. Must all be addressed in the same concurrency phase.

4. **`async {}` in Core** (PITFALL-13) — does not compose cleanly with `task {}`; cancellation propagation breaks across the boundary. Ban at project scaffold time with `scripts/check-no-async.sh` mirroring blueCode.

5. **Expecto test discovery** (PITFALL-26) — `[<Tests>]` auto-discovery is unreliable (burned four executors in blueCode). Every new test module must be added to both the `.fsproj` `<Compile>` list and the explicit `rootTests` list. Establish at project scaffold before writing any tests.

---

## Implications for Roadmap

### Phase ordering principles

**Dependency chain:**
- Core types must exist before any adapter can compile
- `QwenUpstreamClient` must exist before `QueueDispatcher` can wrap it
- All adapters must be wired before `CompositionRoot` compiles
- SSE pitfall cluster requires an atomic phase — do not split across "get it running" and "make it correct"
- Concurrency pitfall cluster requires an atomic phase
- `graph_indexing` no-fallback rule must ship in the same phase as `graph_indexing` routing

**Atomic units that must not be split across phases:**
- SSE pass-through: `ResponseHeadersRead` + per-chunk `FlushAsync` + `use!` scope covering full pipe + SSE headers + `[DONE]` injection
- 122B concurrency gate: `SemaphoreSlim(1)` + priority dispatcher (not raw `WaitAsync`) + linked `CancellationTokenSource` + timeout CTS + `finally Release()`
- `graph_indexing` routing + no-fallback rule

---

### Phase 1: Foundation — Project Scaffold + Core Domain + HTTP Client Bootstrap

**Rationale:** Everything downstream depends on Core types being defined. This phase also locks in the hardest-to-change decisions: project structure, Kestrel binding (`127.0.0.1:4000`, not `localhost`), CI grep (`check-no-async.sh`), Expecto `rootTests` pattern, named HttpClients with 300s timeout, HF-id trap defense, and sampling-param defaults. These are expensive to retrofit later.

**Delivers:**
- `SmartRouter.Core`: Domain.fs (all DUs + record types), Routing.fs (three-stage pipeline), Ports.fs (interfaces)
- `SmartRouter.Cli/Adapters/Json.fs` + `Logging.fs` (copied from blueCode verbatim)
- `SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — non-streaming POST path only; HF-id probe; named HttpClients at 300s; sampling defaults; error mapping
- `SmartRouter.Tests/RouterTests.fs` — explicit `rootTests` entrypoint; `RoutingTests.fs` covering pure routing pipeline
- CI scripts: `check-no-async.sh`
- `appsettings.json`: Kestrel bound to `127.0.0.1:4000`; upstream URLs; routing thresholds

**Features addressed (FEATURES.md):** Task routing table, heuristic fallback, model override, HF-id defense, sampling defaults, configurable routing rules

**Pitfalls addressed (PITFALLS.md):** PITFALL-1 (HF-id trap), PITFALL-12 (100s timeout default), PITFALL-13 (`async {}` ban), PITFALL-15 (named vs typed HttpClient), PITFALL-24 (`127.0.0.1` binding), PITFALL-25 (smoke-test `enable_thinking`), PITFALL-26 (Expecto rootTests), PITFALL-27 (testSequenced)

**Research flag:** None — all patterns have blueCode references; F# type signatures in ARCHITECTURE.md are implementation-ready.

---

### Phase 2: SSE Streaming Pass-Through (Atomic Unit)

**Rationale:** Hermes defaults to `stream=True` on every call. Until SSE pass-through works end-to-end, the router cannot be used with Hermes at all. This phase implements the complete, correct streaming path as a single atomic unit. Do not ship until the "looks done but isn't" checklist passes: TTFB < 2s, chunks arrive incrementally in curl, final event is `data: [DONE]\n\n`, no `ObjectDisposedException` under mid-stream cancellation.

**Delivers:**
- `QwenUpstreamClient.StreamAsync` — `ResponseHeadersRead`; `use!` scope covering full pipe; raw byte buffer loop (no SSE parsing/reframing)
- `ChatCompletions.fs` endpoint — SSE headers before first byte; per-chunk `WriteAsync` + `FlushAsync`; `[DONE]` injection if upstream omits it; `ctx.RequestAborted` as `ct`
- `SmartRouter.Tests/StreamingTests.fs` — TTFB timing test; chunk-by-chunk delivery test; 100-chunk integrity test; mid-stream cancellation test; `[DONE]` sentinel test; header assertion test

**Features addressed (FEATURES.md):** SSE streaming pass-through, cancellation token propagation, request field preservation (UnknownFields forwarded)

**Pitfalls addressed (PITFALLS.md):** PITFALL-2 (`ResponseHeadersRead`), PITFALL-3 (flush per chunk), PITFALL-4 (`HttpResponseMessage` disposal race), PITFALL-6 (SSE headers), PITFALL-7 (chunk reframing), PITFALL-14 (`let!` vs `use!`), PITFALL-19 (`[DONE]` sentinel)

**Research flag:** None — patterns fully specified in STACK.md and PITFALLS.md with code snippets.

---

### Phase 3: 122B Concurrency Gate (Atomic Unit)

**Rationale:** Graphify sends concurrent requests. Without the semaphore + priority queue, concurrent 122B calls trigger `[METAL] Insufficient Memory` crashes. This phase implements the complete concurrency gate atomically: SemaphoreSlim(1), priority dispatcher (not `WaitAsync` directly), linked CancellationTokenSource with per-request timeout, and `finally Release()`.

**Delivers:**
- `QueueDispatcher.fs` — wraps `IUpstreamClient`; dispatcher loop with `PriorityQueue<QueueEntry, int>` + per-entry `TaskCompletionSource`; `SemaphoreSlim(1)` acquired by dispatcher after dequeue; `CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token)`; `finally Release()` in all code paths; aging/promotion for low-priority starvation prevention (30s default threshold)
- `/stats` endpoint — queue depth, active count, `SemaphoreSlim.CurrentCount`, avg wait time
- `SmartRouter.Tests/QueueTests.fs` — semaphore release-on-cancellation test; priority ordering test (high before low under concurrent load); starvation test with aging; upstream-hang + timeout test

**Features addressed (FEATURES.md):** SemaphoreSlim(1) on 122B, two-level priority queue, cancellation propagation, 35B served with higher concurrency

**Pitfalls addressed (PITFALLS.md):** PITFALL-5 (cancellation not propagated), PITFALL-8 (SemaphoreSlim leak on cancellation), PITFALL-9 (FIFO bypasses priority), PITFALL-10 (low-priority starvation), PITFALL-11 (upstream hang / semaphore held forever), PITFALL-22 (`[METAL] Insufficient Memory`)

**Research flag:** None — the dispatcher pattern is fully specified in PITFALLS.md (PITFALL-9) with code.

---

### Phase 4: Health Probing + Fallback + `graph_indexing` No-Fallback Rule (Correctness Unit)

**Rationale:** Health probing is the prerequisite for the fallback decision, and `graph_indexing` routing + no-fallback rule are a single correctness unit (see Features section above). Shipping `graph_indexing` routing without the no-fallback rule creates a window where 122B unavailability silently routes to 35B and produces a poisoned index.

**Delivers:**
- `HealthAdapter.fs` — polls `GET /v1/models` per upstream; tracks reachability; distinguishes "temporarily restarting" from "permanently down"
- `QueueDispatcher` updated — checks `IHealthProbe` before enqueuing for 122B; 122B unavailable + `task=graph_indexing` → return `GraphIndexingMustFail` error; 122B unavailable + other task → reroute to 35B with `IsFallback=true`
- `/health` endpoint — reports per-upstream reachability
- Retry policy wired into `QwenUpstreamClient` via `AddResilienceHandler` (2 retries, exponential backoff, transient errors only)
- Load-aware health probe: poll until upstream responds before router enters service (covers PITFALL-20 cold-start window)
- Tests: `graph_indexing`-must-fail test; 122B-unavailable-falls-back-to-35B test; retry-on-transient test; health probe timeout test

**Features addressed (FEATURES.md):** `graph_indexing` no-fallback rule, backend health probing, retry policy, `/health` endpoint

**Pitfalls addressed (PITFALLS.md):** PITFALL-20 (cold-start request during load window), PITFALL-21 (port rebind race after kickstart)

**Research flag:** None — resilience handler pattern in STACK.md; health probe pattern from blueCode `probeModelInfoAsync`.

---

### Phase 5: OpenAI Wire Format Compliance + Non-Streaming Path

**Rationale:** Router-generated responses (errors, fallback messages) must emit full OpenAI-compatible envelopes. Bare `{"error": "..."}` JSON breaks the OpenAI SDK. This phase also locks in the `model` echo-back policy (canonical alias, not HF path) and `usage` stub injection.

**Delivers:**
- Non-streaming `CompleteAsync` path fully wired in `ChatCompletions.fs`
- Router-generated error responses in `{"error": {"message": "...", "type": "...", "code": ...}}` shape
- `model` field rewritten to canonical alias (`qwen35b` / `qwen122b`) in all responses
- `usage` stub injected when upstream omits it
- OpenAI contract tests: assert all required fields present in both streaming and non-streaming responses; assert `response.model` equals canonical alias

**Features addressed (FEATURES.md):** OpenAI error shape, model field rewriting, `usage` field handling

**Pitfalls addressed (PITFALLS.md):** PITFALL-16 (missing OpenAI response fields), PITFALL-17 (`model` echo-back policy), PITFALL-18 (missing `usage` field)

**Research flag:** None — OpenAI field contract is authoritative; full field list in PITFALLS.md (PITFALL-16).

---

### Phase 6: Integration Tests + launchd Deployment

**Rationale:** Integration tests with fake upstream Kestrel servers validate the full request lifecycle. The launchd plist must use the self-contained published binary — `dotnet run` is invalid in a launchd context because PATH is not inherited.

**Delivers:**
- `IntegrationTests.fs` — fake upstream servers via Kestrel-on-random-port; full routing path; streaming with controlled latency; upstream failure scenarios; concurrent request ordering
- `SmartRouter.Cli.fsproj` publish configuration (`-r osx-arm64 --self-contained`)
- `com.ohama.smart-router.plist` — absolute path to published binary; `ASPNETCORE_URLS=http://127.0.0.1:4000`; `StandardErrorPath` to structured log file
- Operational runbook section in README: restart procedure (`launchctl unload + load -w`, not `kickstart -k`); cold-start window; threshold tuning via `/stats`

**Features addressed (FEATURES.md):** launchd plist, README documentation, load tests, failure tests

**Pitfalls addressed (PITFALLS.md):** PITFALL-23 (launchd PATH / `dotnet` not found), PITFALL-21 (port rebind race — documented in runbook)

**Research flag:** None — launchd plist skeleton in STACK.md; fake upstream pattern in STACK.md.

---

### Phase Ordering Rationale

- Core domain types must exist before any adapter references them (Phase 1 before all others)
- `QwenUpstreamClient` non-streaming path must exist before `QueueDispatcher` can wrap it (Phase 1 before Phase 3)
- SSE streaming (Phase 2) and concurrency (Phase 3) have no cross-dependency after Phase 1 — the roadmapper may run them in sequence or merge into a single phase depending on scope
- Health probing (Phase 4) depends on `QwenUpstreamClient` (Phase 1) and integrates with `QueueDispatcher` (Phase 3) — must follow Phase 3
- Wire format compliance (Phase 5) depends on the full HTTP pipeline being in place — follows Phase 2
- Integration tests + deployment (Phase 6) validates the complete system — must be last

**The SSE pitfall cluster cannot be split.** If Phase 2 ships `ResponseHeadersRead` but defers `FlushAsync` to a later phase, streaming is broken in production between phases. The "looks done but isn't" checklist in PITFALLS.md is the exit criterion for Phase 2.

**The concurrency pitfall cluster cannot be split.** If Phase 3 ships `SemaphoreSlim` but defers the priority dispatcher or the linked timeout CTS, the semaphore can be leaked on cancellation and 122B capacity is permanently stuck. All three must be in the same phase.

### Research Flags

All phases have well-documented patterns — no phase requires `/gsd:research-phase` before planning:

- **Phase 1:** All patterns from blueCode (copy verbatim); Core types specified in ARCHITECTURE.md with concrete F# signatures ready to implement
- **Phase 2:** SSE forwarding fully specified in STACK.md and PITFALLS.md with code snippets
- **Phase 3:** Priority dispatcher pattern fully specified in PITFALLS.md (PITFALL-9) with code
- **Phase 4:** Resilience handler in STACK.md; health check pattern from blueCode `probeModelInfoAsync`
- **Phase 5:** OpenAI field contract in PITFALLS.md (PITFALL-16 to 18); canonical behavior table in FEATURES.md
- **Phase 6:** launchd plist skeleton in STACK.md; fake upstream pattern in STACK.md

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH (core) / MEDIUM (5 versions) | Core framework, architecture style, and 4 package versions verified in blueCode. `Serilog.AspNetCore`, `Microsoft.Extensions.Http.Resilience`, `FsToolkit.ErrorHandling`, `FSharp.Control.TaskSeq`, and `Microsoft.AspNetCore.Mvc.Testing` are MEDIUM — verify with `dotnet package search` at scaffold time. |
| Features | HIGH | Competitor analysis grounded in publicly documented behavior. Hermes behavior confirmed from source files. Graphify requirements from graphify_smart_router_prompt.md. Feature dependency graph has no speculative edges. |
| Architecture | HIGH | Derived directly from blueCode codebase analysis. F# type signatures and `routeRequest` pipeline in ARCHITECTURE.md are implementation-ready, not sketches. Build order respects F# compilation order constraints. |
| Pitfalls | HIGH | All 27 pitfalls grounded in blueCode operational history on the same hardware/servers or in documented .NET/ASP.NET Core/Expecto behavior. HF-id trap, `[METAL]` crashes, and Expecto auto-discovery failure are all confirmed by blueCode history. |

**Overall confidence:** HIGH

### Gaps to Address

- **NuGet package versions (5 packages):** Verify at scaffold time with `dotnet package search`. See STACK.md Version Confidence Summary table for exact commands.

- **Graphify `task` field exact string values:** The literals (`"graph_indexing"`, `"compiler_debug"`, etc.) come from `graphify_smart_router_prompt.md`. When Graphify is implemented, confirm these strings match what the client sends — a mismatch silently falls through to the heuristic path.

- **mlx_lm.server `/v1/models` response shape:** HF-id trap defense assumes `data[n].id` exists and path-like ids start with `/`. Verified against blueCode operational history but not via live probe at research time. Run the PITFALL-1 smoke test during Phase 1 integration.

- **Priority queue aging threshold (30s default):** Not validated against real Graphify workload patterns. Tune via `/stats` after Phase 3 ships.

---

## Sources

### Primary (HIGH confidence — verified in blueCode codebase)
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs` — HF-id trap defense, 300s timeout, sampling defaults, `probeModelInfoAsync` pattern
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/Adapters/Json.fs`, `Logging.fs` — copy-verbatim adapter sources
- `/Users/ohama/projs/blueCode/src/BlueCode.Cli/BlueCode.Cli.fsproj` — verified: `FSharp.SystemTextJson 1.4.36`, `Serilog 4.3.1`, `Serilog.Sinks.Console 6.1.1`
- `/Users/ohama/projs/blueCode/tests/BlueCode.Tests/BlueCode.Tests.fsproj` — verified: `Expecto 10.2.1`
- `/Users/ohama/projs/blueCode/CLAUDE.md` — invariants: `task {}`, `testSequenced`, explicit `rootTests`, stderr routing, `git add <file>`
- `/Users/ohama/projs/smart-router/.planning/PROJECT.md` — authoritative project constraints, key decisions, out-of-scope items
- `~/hermes-agent/run_agent.py` (lines 6922–6941) — confirmed `stream=True` default, no `task` field
- `~/hermes-agent/plugins/model-providers/custom/__init__.py` — confirmed: no `task` field, only `extra_body.options.num_ctx` and `extra_body.think`

### Secondary (MEDIUM confidence — competitor documentation and .NET docs)
- LiteLLM routing/scheduler docs — competitor feature matrix (task routing, priority queue, auto-router)
- vLLM OpenAI-compatible server docs — `extra_body` convention, concurrency model
- Ollama, llama.cpp, OpenRouter, Portkey docs — feature matrix
- .NET `HttpClient` documentation — `ResponseHeadersRead`, `IHttpClientFactory`, named clients
- ASP.NET Core response streaming docs — `FlushAsync`, response body write semantics
- `SemaphoreSlim` documentation — `WaitAsync` FIFO ordering, `try/finally Release` pattern
- OpenAI Chat Completions API spec — required response fields, SSE `[DONE]` sentinel
- F# `task {}` CE documentation — `let!` vs `use!`, cancellation propagation vs `async {}`
- `Microsoft.Extensions.Http.Resilience` docs — `AddResilienceHandler`, `StandardResilienceOptions`

### Tertiary (context, not load-bearing)
- `graphify_smart_router_prompt.md` — Graphify feature requirements (task field values, priority assignments, /stats requirements)

---

*Research completed: 2026-05-07*
*Ready for roadmap: yes*
