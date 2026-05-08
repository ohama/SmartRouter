# Roadmap: Smart Router

## Overview

Smart Router ships in six phases that follow the build-order constraint of the hexagonal architecture: Core domain and routing pure functions first, then the two atomic correctness clusters (SSE streaming, 122B concurrency gate), then the health/fallback/graph_indexing correctness unit, then OpenAI wire-format compliance and observability, and finally launchd deployment. Each phase delivers a coherent, independently verifiable capability; no phase can be entered before its predecessor compiles and passes tests.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Foundation** ✓ — Project scaffold, Core domain types, routing pipeline, non-streaming HTTP adapter, appsettings wiring
- [x] **Phase 2: SSE Streaming Pass-Through** ✓ — Complete atomic SSE correctness cluster (STRM-01..07); Hermes is unblocked when this ships
- [ ] **Phase 3: 122B Concurrency Gate** — Complete atomic concurrency cluster (CONC-01..06 + REL-05); Graphify concurrent requests are safe when this ships
- [ ] **Phase 4: Health + Fallback + graph_indexing No-Fallback** — Health probing, retry policy, fallback routing, and the graph_indexing-must-fail correctness unit
- [ ] **Phase 5: Observability + Unit/Integration Tests** — Structured per-request logging, correlation IDs, unit tests for routing pipeline, integration tests with fake upstream servers
- [ ] **Phase 6: Deployment + Documentation** — launchd plist, /v1/models endpoint, README

## Phase Details

### Phase 1: Foundation
**Goal**: The project compiles, the routing pipeline is testable in isolation, and the non-streaming HTTP path reaches a real upstream and returns a response.
**Depends on**: Nothing (first phase)
**Requirements**: ARCH-01, ARCH-02, ARCH-03, ARCH-04, ARCH-05, ARCH-06, ARCH-07, ROUT-01, ROUT-02, ROUT-03, ROUT-04, ROUT-05, ROUT-06, ROUT-07, API-01, API-02, API-03, API-04, OBS-04, OPS-04, OPS-05, TEST-07, CONC-07
**Success Criteria** (what must be TRUE):
  1. A non-streaming `POST /v1/chat/completions` request reaches the router and a response from the upstream Qwen model is returned to the caller with no field stripping.
  2. Sending `{"task": "graph_indexing"}` routes to 122B; sending `{"task": "retrieval"}` routes to 35B; sending neither with a long complex prompt routes to 122B; sending neither with a short simple prompt routes to 35B — all four verified by Expecto unit tests that run without a live upstream.
  3. Sending `{"model": "35b"}` or `{"model": "122b"}` overrides task and heuristic routing — verified by unit test.
  4. `SmartRouter.Core` has zero compilation references to Serilog, HttpClient, or ASP.NET Core — verified by `dotnet build` succeeding after manually removing those NuGet packages from Core.
  5. `check-no-async.sh` passes; the Expecto test runner uses the explicit `rootTests` list and reports zero test discovery warnings.
**Plans**: 3 plans

Plans:
- [x] 01-01-SCAFFOLD-PLAN.md ✓ — Scaffold solution (SmartRouter.Core/Cli/Tests), pin NuGet packages, wire Kestrel to 127.0.0.1:4000, add check-no-async.sh + explicit Expecto rootTests skeleton
- [x] 01-02-CORE-DOMAIN-PLAN.md ✓ — Core domain (Domain.fs DUs, Routing.fs three-stage pipeline, Ports.fs interfaces), copy Json.fs + Logging.fs from blueCode, write RoutingTests.fs covering full pipeline
- [x] 01-03-UPSTREAM-WIRING-PLAN.md ✓ — QwenUpstreamClient non-streaming path (HF-id defense, 300s timeout, sampling defaults, UnknownFields forwarding), ChatCompletions endpoint with 501 on stream=true, CompositionRoot DI wiring, full appsettings.json

### Phase 2: SSE Streaming Pass-Through
**Goal**: Clients that send `stream=true` receive upstream SSE chunks incrementally, mid-stream disconnect aborts the upstream call, and the `[DONE]` sentinel is always forwarded — all five pitfall conditions satisfied atomically.
**Depends on**: Phase 1
**Requirements**: STRM-01, STRM-02, STRM-03, STRM-04, STRM-05, STRM-06, STRM-07, TEST-03
**Success Criteria** (what must be TRUE):
  1. A `curl -N` streaming request to `POST /v1/chat/completions` with `"stream": true` delivers chunks incrementally (time-to-first-byte under 2 seconds, no burst delivery at end).
  2. The response has `Content-Type: text/event-stream` and chunks arrive in order with no `data: ...` event split across two writes.
  3. The final event in the stream is `data: [DONE]\n\n`.
  4. Killing the curl client mid-stream causes the upstream HTTP call to abort within one chunk interval — no orphaned upstream call remains.
  5. The streaming tests (chunk ordering, mid-stream cancellation, `[DONE]` sentinel, header assertions) all pass against a fake upstream Kestrel server that delivers chunks with controlled latency.
**Plans**: 2 plans

Plans:
- [x] 02-01-STREAMING-IMPL-PLAN.md ✓ — Implement `QwenUpstreamClient.StreamAsync` (taskSeq + `HttpCompletionOption.ResponseHeadersRead` + `use _ = resp` disposal scope + line-level `ReadLineAsync` yielding); wire SSE headers + per-chunk `FlushAsync` + Strategy D `[DONE]` injection in `ChatCompletions.fs` (replaces the Phase-1 HTTP 501 stub)
- [x] 02-02-STREAMING-TESTS-PLAN.md ✓ — Author `StreamingTests.fs` with real Kestrel-on-`127.0.0.1:0` fake upstream wrapped in `testSequenced`: TTFB under 2 s, chunk ordering, 100-chunk integrity, mid-stream cancellation (asserts fake upstream's RequestAborted fires within 5 s), `[DONE]` forwarded + `[DONE]` injected, SSE header assertions, routing-error-before-streaming order-of-operations test

### Phase 3: 122B Concurrency Gate
**Goal**: At most one 122B request is in flight at any time; high-priority tasks (graph_indexing, compiler_debug, architecture_analysis) preempt low-priority ones in the queue; cancellation or upstream hang never leaks the semaphore.
**Depends on**: Phase 1
**Requirements**: CONC-01, CONC-02, CONC-03, CONC-04, CONC-05, CONC-06, REL-05, API-07, OBS-02, TEST-04, TEST-06
**Success Criteria** (what must be TRUE):
  1. Sending five concurrent 122B-routed requests confirms that only one is active at a time — the other four queue and execute serially (verified by QueueTests with controlled fake upstream latency).
  2. A high-priority `graph_indexing` request submitted while a low-priority request is queued executes before the low-priority request (verified by priority ordering test).
  3. Cancelling a queued request before it acquires the semaphore removes it from the queue and leaves `SemaphoreSlim.CurrentCount` unchanged.
  4. A request that hangs the upstream beyond the configured timeout releases the semaphore and allows the next queued request to proceed.
  5. `GET /stats` returns current queue depth, active request count, and average wait time that reflect the live queue state.
**Plans**: TBD

Plans:
- [ ] 03-01: Implement QueueDispatcher.fs (PriorityQueue + TaskCompletionSource waiter pattern, SemaphoreSlim(1), linked CTS + timeout CTS, try/finally Release, 35B bypass)
- [ ] 03-02: Wire /stats endpoint (OBS-02 counters/gauges); write QueueTests.fs (semaphore enforcement, priority ordering, cancellation release, timeout release)
- [ ] 03-03: Load tests validating 122B throughput cap holds under burst

### Phase 4: Health + Fallback + graph_indexing No-Fallback
**Goal**: The router knows whether each upstream is reachable, gracefully reroutes 122B requests to 35B when 122B is down — except for graph_indexing which must return an error rather than silently downgrade.
**Depends on**: Phase 3
**Requirements**: REL-01, REL-02, REL-03, REL-04, API-05, TEST-05
**Success Criteria** (what must be TRUE):
  1. `GET /health` returns per-upstream reachability status that reflects whether each Qwen server is actually responding.
  2. When 122B is stopped, a `reasoning` task request is transparently served by 35B (logged as fallback); the response reaches the caller without error.
  3. When 122B is stopped, a `graph_indexing` request returns an error response (not a 35B response) — the response body contains a clear error message, not model output.
  4. A request that fails on first attempt due to a transient upstream error is retried with backoff and succeeds on retry — verified by failure tests with a fake upstream that fails once then succeeds.
  5. The failure tests (timeout, malformed JSON, unavailable model, fallback path, graph_indexing-must-fail) all pass.
**Plans**: TBD

Plans:
- [ ] 04-01: Implement HealthAdapter.fs (background poll of /v1/models per upstream, reachability tracking); wire /health endpoint
- [ ] 04-02: Add fallback policy to QueueDispatcher (check IHealthProbe before enqueue; graph_indexing → GraphIndexingMustFail error; other 122B → reroute to 35B with IsFallback=true); wire retry policy via AddResilienceHandler in QwenUpstreamClient
- [ ] 04-03: Write failure tests (graph_indexing-must-fail, 122B-unavailable fallback, retry-on-transient, health probe timeout)

### Phase 5: Observability + Unit/Integration Tests
**Goal**: Every request produces a structured log line with routing reason and latency; each request carries a correlation ID through logs and error events; the routing pipeline and integration path are fully tested.
**Depends on**: Phase 4
**Requirements**: OBS-01, OBS-03, TEST-01, TEST-02
**Success Criteria** (what must be TRUE):
  1. After a request completes, stderr contains a structured JSON log line with: selected model, routing reason (ExplicitModelOverride / ExplicitTask / Heuristic / Default), latency, token count, backend status, and queue wait time.
  2. The correlation ID in the request log line matches the ID in any SSE error events emitted for that same request.
  3. Expecto unit tests cover all routing pipeline branches: model override short-circuits, all seven task-table entries, heuristic keyword hits, heuristic prompt-length threshold, priority assignment for each task type, fallback decisions.
  4. Integration tests with fake upstream Kestrel servers on random ports pass: full routing path for non-streaming and streaming, upstream failure scenarios, concurrent request ordering.
**Plans**: TBD

Plans:
- [ ] 05-01: Wire OBS-01 per-request log line (Serilog structured event with all required fields) and OBS-03 correlation ID generation/propagation; verify stderr/stdout separation
- [ ] 05-02: Write RouterTests.fs unit coverage (all routing pipeline branches, priority assignment, fallback decisions); write IntegrationTests.fs with fake upstream servers

### Phase 6: Deployment + Documentation
**Goal**: The router auto-starts under launchd supervision, the /v1/models endpoint proxies both upstream model lists, and the README gives the operator everything needed to tune, debug, and connect both clients.
**Depends on**: Phase 5
**Requirements**: API-06, OPS-01, OPS-02, OPS-03
**Success Criteria** (what must be TRUE):
  1. `launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist` starts the router and it is reachable at `http://127.0.0.1:4000/health` without running `dotnet run`.
  2. After a simulated crash (kill -9 on the router process), launchd restarts it automatically within 5 seconds.
  3. `GET /v1/models` returns a deduplicated list that includes model entries from both upstream servers.
  4. The README explains the routing decision pipeline, how to adjust the heuristic threshold and keyword list, how to connect Hermes, and how to connect Graphify — a new operator can follow the steps without asking for clarification.
**Plans**: TBD

Plans:
- [ ] 06-01: Implement /v1/models endpoint (proxy both upstreams, deduplicate by id); configure dotnet publish (-r osx-arm64 --self-contained); write com.ohama.smart-router.plist with absolute dotnet path
- [ ] 06-02: Write README (architecture overview, routing rules, threshold tuning, debugging, Hermes integration steps, Graphify integration steps, launchd restart procedure)

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → 6

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation | 3/3 | ✓ Complete | 2026-05-07 |
| 2. SSE Streaming Pass-Through | 2/2 | ✓ Complete | 2026-05-08 |
| 3. 122B Concurrency Gate | 0/3 | Not started | - |
| 4. Health + Fallback + graph_indexing No-Fallback | 0/3 | Not started | - |
| 5. Observability + Unit/Integration Tests | 0/2 | Not started | - |
| 6. Deployment + Documentation | 0/2 | Not started | - |
