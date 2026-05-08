# Requirements: Smart Router

**Defined:** 2026-05-07
**Core Value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.

## v1 Requirements

### API

- [x] **API-01**: Router exposes `POST /v1/chat/completions` on `localhost:4000`
- [x] **API-02**: Router parses standard OpenAI fields (`messages`, `model`, `stream`, `temperature`, `top_p`, `max_tokens`)
- [x] **API-03**: Router accepts optional non-OpenAI `task` field as a top-level body property (per `extra_body` industry convention)
- [x] **API-04**: Router preserves unknown request fields when proxying upstream (no field-stripping)
- [ ] **API-05**: Router exposes `GET /health` returning liveness + reachability of both upstream ports
- [ ] **API-06**: Router exposes `GET /v1/models` proxying both upstreams' model lists, deduped
- [ ] **API-07**: Router exposes `GET /stats` returning queue size, active requests, average wait time, requests/sec, failures, streaming duration

### Routing

- [x] **ROUT-01**: When request `model` field matches `35b` / `122b` aliases, router short-circuits to that target without consulting task or heuristic
- [x] **ROUT-02**: When request includes `task` field, router uses authoritative task→model table (graph_indexing/compiler_debug/architecture_analysis/dependency_analysis/reasoning → 122B; retrieval/summary → 35B)
- [x] **ROUT-03**: When neither override nor task is present, router applies heuristic fallback (prompt length, complex-keyword set, code-block detection, message count, total context size)
- [x] **ROUT-04**: Heuristic biases toward 35B for ambiguous cases (latency-first default for Hermes path)
- [x] **ROUT-05**: Routing rules are loaded from `appsettings.json` (threshold, keyword list, task→model table, model URLs)
- [x] **ROUT-06**: Each routing decision attaches a `RoutingReason` (DU: ExplicitModelOverride / ExplicitTask / Heuristic / Default) for logging and `/stats`
- [x] **ROUT-07**: Router preserves blueCode's HF-id trap defense — POST body's `model` field carries the upstream's local-path id (preferring `data[0]` entries that start with `/`), not the HF repo id

### Concurrency + Queueing

- [ ] **CONC-01**: Router enforces `SemaphoreSlim(1)` on 122B-bound requests — at most one heavy request in flight per gateway process
- [ ] **CONC-02**: Router applies a two-level priority queue (high / low FIFO-within-level) for 122B-bound requests
- [ ] **CONC-03**: Tasks `graph_indexing`, `compiler_debug`, `architecture_analysis` map to high priority; everything else routed to 122B is low priority
- [ ] **CONC-04**: 35B-bound requests bypass the priority queue and run concurrently bounded only by HttpClient pool
- [ ] **CONC-05**: Cancellation token propagates client → queue wait → semaphore acquire → upstream HTTP call (`HttpContext.RequestAborted` linked to upstream `CancellationToken`)
- [ ] **CONC-06**: Semaphore release is in `try/finally` — never leaks on exception or cancellation
- [x] **CONC-07**: Per-request timeout is configurable (default 300s, matching blueCode's 122B cold-start window)

### Streaming

- [x] **STRM-01**: When client sends `stream=true`, router forwards upstream SSE chunks unchanged via `HttpClient.SendAsync(..., HttpCompletionOption.ResponseHeadersRead)` + `Stream.CopyToAsync`
- [x] **STRM-02**: Router preserves SSE chunk ordering and never splits a `data: ...\n\n` event across writes
- [x] **STRM-03**: Router calls `FlushAsync` on `HttpContext.Response.Body` after each chunk (no chunk batching)
- [x] **STRM-04**: Router sets `Content-Type: text/event-stream` and disables response buffering
- [x] **STRM-05**: Downstream client disconnect aborts the upstream call mid-stream and releases the semaphore
- [x] **STRM-06**: `HttpResponseMessage` lifetime is held until SSE stream copy completes (no early disposal)
- [x] **STRM-07**: Router forwards the `[DONE]` SSE sentinel from upstream to client

### Reliability

- [ ] **REL-01**: Router retries transient upstream failures with bounded retries + backoff (idempotent chat/completions only)
- [ ] **REL-02**: Router probes upstream health (35B, 122B reachability) on a background cadence and exposes results via `/health`
- [ ] **REL-03**: When 122B is unavailable, router falls back to 35B for all 122B-routed requests **except** `task=graph_indexing`
- [ ] **REL-04**: When 122B is unavailable and request has `task=graph_indexing`, router returns an error (does NOT silently downgrade to 35B)
- [ ] **REL-05**: Per-request `CancellationToken` always carries a timeout (paired with `CancellationTokenSource.CreateLinkedTokenSource`) so a hung upstream cannot deadlock a queue slot

### Observability

- [ ] **OBS-01**: Per-request structured log line (Serilog → stderr) includes: selected model, routing reason, latency, token count, backend status, queue wait time
- [ ] **OBS-02**: Router maintains in-process counters/gauges feeding `/stats`: requests/sec, active requests, 122B queue depth, average latency, failure count, streaming duration
- [ ] **OBS-03**: Each request gets a correlation id propagated through logs and SSE-error events
- [x] **OBS-04**: Logs go to stderr only; stdout is reserved for application output (matches blueCode stream-separation invariant)

### Architecture

- [x] **ARCH-01**: `SmartRouter.Core` has zero references to Serilog / Spectre / HTTP clients — pure DUs, ports, routing decisions, with `FsToolkit.ErrorHandling` as the only NuGet dependency
- [x] **ARCH-02**: Core uses `task {}` exclusively — no `async {}` literals (CI grep enforces, mirroring blueCode `scripts/check-no-async.sh`)
- [x] **ARCH-03**: Routing pipeline is composable as pure functions: `model` override → `task` table → heuristic → default, each independently testable
- [x] **ARCH-04**: Queue + semaphore live in a `QueueDispatcher` adapter that wraps any `IUpstreamClient` — concurrency policy is independently swappable from HTTP mechanics
- [x] **ARCH-05**: SSE streaming crosses the port boundary as `IAsyncEnumerable<Result<string, RouterError>>` — no `HttpResponseMessage` leaks into Core
- [x] **ARCH-06**: Service is stateless — no static mutable state; queue/semaphore/counters live in DI-singleton scope
- [x] **ARCH-07**: Provider-extension seams (interface for non-Qwen upstream) exist but only Qwen 35B + 122B are implemented

### Operability

- [ ] **OPS-01**: launchd plist (`com.ohama.smart-router.plist`) auto-starts the router and supervises restart, mirroring `com.ohama.qwen122b.plist` shape
- [ ] **OPS-02**: launchd plist references the dotnet runtime by absolute path (PATH unavailable to launchd at load time)
- [ ] **OPS-03**: README documents architecture, routing rules, threshold tuning, debugging, Hermes integration, Graphify integration
- [x] **OPS-04**: Router binds explicitly to `127.0.0.1` (not `0.0.0.0`, not just `localhost`) to avoid Mac firewall surprises
- [x] **OPS-05**: `appsettings.json` captures all tunables (model URLs, threshold, keyword list, task table, timeouts, retry policy)

### Testing

- [ ] **TEST-01**: Expecto unit tests cover routing pipeline: model override, task table, heuristic scoring, keyword detection, priority assignment, fallback decisions
- [ ] **TEST-02**: Integration tests run against fake upstream Kestrel servers on random ports (deterministic responses, controlled latency, controlled failures)
- [x] **TEST-03**: Streaming tests verify chunk ordering, mid-stream cancellation, mid-stream upstream failure, `[DONE]` propagation
- [ ] **TEST-04**: Concurrency tests verify SemaphoreSlim enforcement, priority ordering, semaphore-release on cancellation
- [ ] **TEST-05**: Failure tests cover upstream timeout, malformed JSON from upstream, unavailable model server, fallback path, `graph_indexing`-must-fail path
- [ ] **TEST-06**: Load tests measure latency under contention and validate 122B throughput cap holds under burst
- [x] **TEST-07**: Tests use the explicit `rootTests` list pattern in the test entrypoint (matches blueCode; Expecto auto-discovery is unreliable)

## v2 Requirements

Deferred. Tracked but not in current roadmap.

### Reliability

- **REL2-01**: Circuit breaker as a distinct mechanism (with explicit open/half-open/closed state)
- **REL2-02**: Multi-level priority queue beyond two levels with aging / starvation prevention

### Observability

- **OBS2-01**: Prometheus `/metrics` exposition

### Routing

- **ROUT2-01**: ML / learned routing trained on accumulated `/stats` + log data
- **ROUT2-02**: Embedding-based semantic task classification

### Providers

- **PROV2-01**: Claude provider implementation
- **PROV2-02**: OpenAI cloud provider implementation
- **PROV2-03**: DeepSeek / Gemini / Gemma / Llama provider implementations

### Operability

- **OPS2-01**: Rate limiting per-client / per-task
- **OPS2-02**: Auth (API keys, mTLS, etc.)
- **OPS2-03**: Docker / docker-compose deployment

## Out of Scope

| Feature | Reason |
|---------|--------|
| Windows support | Mac-only; mirrors blueCode's Unix-path heuristic |
| Persistence / session state | Router is stateless per request; conversation memory lives in client |
| xUnit / FsUnit | Single test framework (Expecto) across smart-router and blueCode |
| Channels / TPL Dataflow as baseline pattern | Priority queue + SemaphoreSlim covers v1 needs without abstraction tax |
| Public exposure | Loopback-only by design; no auth surface needed |
| Function-call rewriting | Pure pass-through router; client owns OpenAI tools/function-call shape |
| Prompt caching | Not in router scope; upstream mlx_lm.server handles its own KV cache |
| Embeddings endpoint | Out of scope; unrelated to chat-completions routing core value |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| API-01 | Phase 1 | Complete |
| API-02 | Phase 1 | Complete |
| API-03 | Phase 1 | Complete |
| API-04 | Phase 1 | Complete |
| API-05 | Phase 4 | Pending |
| API-06 | Phase 6 | Pending |
| API-07 | Phase 3 | Pending |
| ROUT-01 | Phase 1 | Complete |
| ROUT-02 | Phase 1 | Complete |
| ROUT-03 | Phase 1 | Complete |
| ROUT-04 | Phase 1 | Complete |
| ROUT-05 | Phase 1 | Complete |
| ROUT-06 | Phase 1 | Complete |
| ROUT-07 | Phase 1 | Complete |
| CONC-01 | Phase 3 | Pending |
| CONC-02 | Phase 3 | Pending |
| CONC-03 | Phase 3 | Pending |
| CONC-04 | Phase 3 | Pending |
| CONC-05 | Phase 3 | Pending |
| CONC-06 | Phase 3 | Pending |
| CONC-07 | Phase 1 | Complete |
| STRM-01 | Phase 2 | Complete |
| STRM-02 | Phase 2 | Complete |
| STRM-03 | Phase 2 | Complete |
| STRM-04 | Phase 2 | Complete |
| STRM-05 | Phase 2 | Complete |
| STRM-06 | Phase 2 | Complete |
| STRM-07 | Phase 2 | Complete |
| REL-01 | Phase 4 | Pending |
| REL-02 | Phase 4 | Pending |
| REL-03 | Phase 4 | Pending |
| REL-04 | Phase 4 | Pending |
| REL-05 | Phase 3 | Pending |
| OBS-01 | Phase 5 | Pending |
| OBS-02 | Phase 3 | Pending |
| OBS-03 | Phase 5 | Pending |
| OBS-04 | Phase 1 | Complete |
| ARCH-01 | Phase 1 | Complete |
| ARCH-02 | Phase 1 | Complete |
| ARCH-03 | Phase 1 | Complete |
| ARCH-04 | Phase 1 | Complete |
| ARCH-05 | Phase 1 | Complete |
| ARCH-06 | Phase 1 | Complete |
| ARCH-07 | Phase 1 | Complete |
| OPS-01 | Phase 6 | Pending |
| OPS-02 | Phase 6 | Pending |
| OPS-03 | Phase 6 | Pending |
| OPS-04 | Phase 1 | Complete |
| OPS-05 | Phase 1 | Complete |
| TEST-01 | Phase 5 | Pending |
| TEST-02 | Phase 5 | Pending |
| TEST-03 | Phase 2 | Complete |
| TEST-04 | Phase 3 | Pending |
| TEST-05 | Phase 4 | Pending |
| TEST-06 | Phase 3 | Pending |
| TEST-07 | Phase 1 | Complete |

**Coverage:**
- v1 requirements: 56 total
- Mapped to phases: 56 ✓
- Unmapped: 0
- Complete: 31 (Phase 1 ✓ + Phase 2 ✓)
- Pending: 25

---
*Requirements defined: 2026-05-07*
*Last updated: 2026-05-08 after Phase 2 (SSE Streaming Pass-Through) completion — 31 requirements verified Complete*
