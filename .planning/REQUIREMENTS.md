# Requirements: Smart Router

**Defined:** 2026-05-07
**Last restructured:** 2026-05-08 — Operator folded ML-routing arc forward (was originally PROJECT.md "Out of Scope" v2 work). Phases 4-9 now ship ML augmentation; original Phase 4 (Health/Fallback) and Phase 6 (Deploy/Docs) deferred to Phases 10-11. Old Phase 5 (Observability) dissolves: OBS-01/OBS-03 absorbed into NEW Phase 5 (Decision Logging — Loop B's input); TEST-01/TEST-02 retroactively marked Complete (covered by Phases 1-3 tests collectively).
**Core Value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.

## v1 Requirements

### API

- [x] **API-01**: Router exposes `POST /v1/chat/completions` on `localhost:4000`
- [x] **API-02**: Router parses standard OpenAI fields (`messages`, `model`, `stream`, `temperature`, `top_p`, `max_tokens`)
- [x] **API-03**: Router accepts optional non-OpenAI `task` field as a top-level body property (per `extra_body` industry convention)
- [x] **API-04**: Router preserves unknown request fields when proxying upstream (no field-stripping)
- [x] **API-05**: Router exposes `GET /health` returning liveness + reachability of both upstream ports
- [ ] **API-06**: Router exposes `GET /v1/models` proxying both upstreams' model lists, deduped
- [x] **API-07**: Router exposes `GET /stats` returning queue size, active requests, average wait time, requests/sec, failures, streaming duration

### Routing

- [x] **ROUT-01**: When request `model` field matches `35b` / `122b` aliases, router short-circuits to that target without consulting task or heuristic
- [x] **ROUT-02**: When request includes `task` field, router uses authoritative task→model table (graph_indexing/compiler_debug/architecture_analysis/dependency_analysis/reasoning → 122B; retrieval/summary → 35B)
- [x] **ROUT-03**: When neither override nor task is present, router applies heuristic fallback (prompt length, complex-keyword set, code-block detection, message count, total context size)
- [x] **ROUT-04**: Heuristic biases toward 35B for ambiguous cases (latency-first default for Hermes path)
- [x] **ROUT-05**: Routing rules are loaded from `appsettings.json` (threshold, keyword list, task→model table, model URLs)
- [x] **ROUT-06**: Each routing decision attaches a `RoutingReason` (DU: ExplicitModelOverride / ExplicitTask / Heuristic / Default / ML) for logging and `/stats`
- [x] **ROUT-07**: Router preserves blueCode's HF-id trap defense — POST body's `model` field carries the upstream's local-path id (preferring `data[0]` entries that start with `/`), not the HF repo id

### Concurrency + Queueing

- [x] **CONC-01**: Router enforces `SemaphoreSlim(1)` on 122B-bound requests — at most one heavy request in flight per gateway process
- [x] **CONC-02**: Router applies a two-level priority queue (high / low FIFO-within-level) for 122B-bound requests
- [x] **CONC-03**: Tasks `graph_indexing`, `compiler_debug`, `architecture_analysis` map to high priority; everything else routed to 122B is low priority
- [x] **CONC-04**: 35B-bound requests bypass the priority queue and run concurrently bounded only by HttpClient pool
- [x] **CONC-05**: Cancellation token propagates client → queue wait → semaphore acquire → upstream HTTP call (`HttpContext.RequestAborted` linked to upstream `CancellationToken`)
- [x] **CONC-06**: Semaphore release is in `try/finally` — never leaks on exception or cancellation
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

- [x] **REL-01**: Router retries transient upstream failures with bounded retries + backoff (idempotent chat/completions only — streaming EXCLUDED via separate -stream named HttpClients without resilience handler; partial SSE output cannot be replayed)
- [x] **REL-02**: Router probes upstream health (35B, 122B reachability) on a background cadence (HealthService BackgroundService + PeriodicTimer; default 10s) and exposes results via `/health`
- [x] **REL-03**: When 122B is unavailable, router falls back to 35B for all 122B-routed requests **except** `task=graph_indexing` (with `fallback_used=true` in DecisionLog activating Phase 7 FailureDetector → Phase 8 RetrainingService loop)
- [x] **REL-04**: When 122B is unavailable and request has `task=graph_indexing`, router returns 503 with structured `{error: {message, type: "model_unavailable"}}` body (does NOT silently downgrade to 35B)
- [x] **REL-05**: Per-request `CancellationToken` always carries a timeout (paired with `CancellationTokenSource.CreateLinkedTokenSource`) so a hung upstream cannot deadlock a queue slot

### Observability

- [x] **OBS-01**: Per-request structured log line (Serilog → stderr) includes: selected model, routing reason, latency, token count, backend status, queue wait time
- [x] **OBS-02**: Router maintains in-process counters/gauges feeding `/stats`: requests/sec, active requests, 122B queue depth, average latency, failure count, streaming duration
- [x] **OBS-03**: Each request gets a correlation id propagated through logs and SSE-error events
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

- [x] **TEST-01**: Expecto unit tests cover routing pipeline: model override, task table, heuristic scoring, keyword detection, priority assignment, fallback decisions
- [x] **TEST-02**: Integration tests run against fake upstream Kestrel servers on random ports (deterministic responses, controlled latency, controlled failures)
- [x] **TEST-03**: Streaming tests verify chunk ordering, mid-stream cancellation, mid-stream upstream failure, `[DONE]` propagation
- [x] **TEST-04**: Concurrency tests verify SemaphoreSlim enforcement, priority ordering, semaphore-release on cancellation
- [x] **TEST-05**: Failure tests cover unavailable model server (HLTH-04), fallback path (HLTH-04), `graph_indexing`-must-fail path (HLTH-05), transient retry (HLTH-06), and streaming-no-retry isolation (HLTH-07); /health endpoint shape (HLTH-08)
- [x] **TEST-06**: Load tests measure latency under contention and validate 122B throughput cap holds under burst
- [x] **TEST-07**: Tests use the explicit `rootTests` list pattern in the test entrypoint (matches blueCode; Expecto auto-discovery is unreliable)

### ML Algorithm Seam (Phase 4)

- [x] **ML-01**: `RoutingAlgorithm` is a function-type alias `RoutingConfig -> RouterRequest -> RoutingDecision` in Core; both `Heuristic.applyHeuristic` and `ML.applyML` conform to the same shape
- [x] **ML-02**: `appsettings.json` `Routing.Algorithm` key (`"heuristic" | "ml"`) selects the active algorithm at startup; default is `"heuristic"` if absent
- [x] **ML-03**: CLI flag `--routing-algorithm=heuristic|ml` overrides the config value at startup; verified by start-twice integration test
- [x] **ML-04**: `Routing/Heuristic.fs` and `Routing/ML.fs` are separate modules with **zero cross-imports** — verified by CI grep that fails the build on any cross-module reference

### Decision Logging (Phase 5)

- [x] **LOG-01**: Each routing decision emits a JSONL line at `logs/decisions/YYYY-MM-DD.jsonl` (UTC date) with the full schema: `schema_version` (int, currently `1`), `correlation_id`, `prompt_hash` (SHA-256 of concatenated message contents), `prompt_korean_char_ratio` (float 0..1, count of Hangul chars in `[가-힣]` / total chars), `routing_algorithm` (heuristic|ml), `routing_reason`, `target` (Qwen35B|Qwen122B), `latency_ms`, `fallback_used` (bool), `model_version` (string), `task_type` (optional), `timestamp`
- [x] **LOG-02**: JSONL writer is thread-safe via single-writer `Channel<DecisionLog>` background pump — `File.AppendAllText` is explicitly forbidden (CI grep); 100 concurrent requests produce 100 valid JSON lines with no `IOException` or interleaved bytes
- [x] **LOG-03**: Daily file rotation creates a new dated file at midnight local time; writer flushes pending entries on graceful shutdown (`app.StopAsync`); no log loss on clean exit
- [x] **LOG-04**: Correlation ID is generated per request (middleware), propagated through Serilog stderr output AND the JSONL file, AND included in any SSE error event body — verified by a test that captures all three sources

### Embeddings + Classifier (Phase 6)

- [x] **EMBED-01**: `IEmbedder` port in Core (no NuGet deps); `BgeM3Embedder` adapter in Cli using `Microsoft.ML.OnnxRuntime` + SentencePiece tokenizer (XLM-R compatible); loads **bge-m3 int8 dynamic-quantized** ONNX (~580MB, NOT the 2.3GB FP32 baseline); produces **1024-dim** L2-normalized vectors. **bge-m3 chosen over bge-small** because operator's traffic mixes Korean+English; bge-small's tokenizer cannot handle Hangul (sub-`[UNK]` fallback) per `~/projs/smart-router-distillation/docs/embedding-classifier-decision-deep-dive.md` §1.7.1. **int8 from start** (not as fallback) per §1.7.7 row 4 ("라우터 latency budget 빠듯 → bge-m3 int8 quantized"); accuracy regressions caught downstream by Phase 8 validation gate.
- [x] **EMBED-02**: Same prompt produces the same vector across runs (determinism); verified by unit test on three fixed prompts (one English, one Korean, one mixed)
- [x] **EMBED-03**: Embedding latency budget — single-prompt embedding completes in <50ms p95 on Mac M-series CPU with int8 quantized bge-m3 (cold-start may exceed; warm path target). If int8 path exceeds budget under load, fallback path is ONNX CoreML execution provider (hardware acceleration on Apple Silicon Neural Engine). Verified by latency benchmark test in `MLRoutingTests.fs` or `LoadTests.fs`
- [x] **CLS-01**: `IClassifier` port in Core; `MlNetClassifier` adapter in Cli loading via `Microsoft.Extensions.ML.PredictionEnginePool`; predicts a binary label + confidence given a 1024-dim vector
- [x] **CLS-02**: First-run bootstrap — when `models/router.zip` is missing at startup, a dummy model with random weights (1024-dim input) is auto-generated; logged warning explains it's a placeholder; `applyML` does not throw on cold start
- [x] **CLS-03**: bge-m3 multilingual embedding is verified via **cosine similarity test** in Phase 6: `cosine(embed("디버깅 도와줘"), embed("debug this")) > 0.7` AND `cosine(embed("F# 컴파일러 에러 분석"), embed("analyze F# compiler error")) > 0.7`. This deterministically proves bge-m3 (not bge-small or random weights) is producing meaningful multilingual vectors — independent of the classifier (which has dummy random weights in Phase 6 until Phase 8 retraining produces real ones). Full routing-decision divergence (heuristic vs ML on Korean prompts) is verified later by Phase 8's validation gate when a trained classifier exists.

### Failure Detection + Teacher Labeling (Phase 7)

- [x] **FAIL-01**: `FailureDetector` reads `decisions/*.jsonl`, filters records where `fallback_used=true`, returns the hard-case set; verified by unit test on a fixture file
- [x] **FAIL-02**: `TeacherLabeler` calls 122B with the prompt template from `~/projs/smart-router-distillation/prompts/teacher_prompt.md`, parses the response into `(prompt, label)`, enforces 30s timeout per call + 3x retry on transient failure
- [x] **FAIL-03**: Daily cost cap (configurable; default 1000 calls/day) — calls beyond the cap are skipped with a logged warning; verified by a test that exercises the limit
- [x] **FAIL-04**: Hard-case dataset persists to `datasets/hard-cases.jsonl` append-only with a single-writer file lock; 10 concurrent runs produce a corruption-free file (line count == sum of inputs)

### Retraining Loop (Phase 8)

- [x] **RETRAIN-01**: `DatasetMerger` produces a 70/30 old/new split with class-stratified balance — both classes appear in ≥30% of samples (no catastrophic forgetting); verified by unit test on synthetic input
- [x] **RETRAIN-02**: `RetrainingService : BackgroundService` triggers retrain when `hard-cases.jsonl` count ≥500 OR a 1-hour `PeriodicTimer` fires (whichever first); verified by two integration tests
- [x] **RETRAIN-03**: `Validator` rejects the new model if held-out accuracy < baseline OR `fallback_rate` on validation set > baseline; failed models are NOT written; rejection is logged with rationale
- [x] **RETRAIN-04**: New `models/router.zip` is written atomically (temp file + rename); `PredictionEnginePool` with `watchForChanges:true` swaps the live classifier; in-flight requests complete on the previous model — verified by a 3-request before/retrain/after test that asserts the `model_version` flip in DecisionLog
- [x] **RETRAIN-05**: A `Mutex` (or equivalent single-writer lock) ensures only one retrain runs at a time; concurrent triggers serialize or skip; verified by a 2-concurrent-trigger test
- [x] **RETRAIN-06**: Retraining failures (mid-pipeline throws) are caught with `try/with`, do NOT crash the host, do NOT block subsequent retrains, and Loop A (request handling) is unaffected; verified by a force-throw test

### Canary Deployment (Phase 9)

- [x] **CANARY-01**: `Microsoft.FeatureManagement.AspNetCore` + `ContextualTargetingFilter` (NOT `PercentageFilter` — non-sticky) splits traffic between baseline and canary models; bucket assignment is sticky per `correlation_id` (same correlation always lands in the same cohort); verified statistically over 1000 deterministic requests (binomial 95% CI)
- [x] **CANARY-02**: `Routing.Canary.PercentageEnabled` config (default 10%) controls canary share; `model_version` in DecisionLog distinguishes canary vs baseline cohorts (suffix `-canary`); cohort comparison (avg `fallback_rate`, latency) is trivial via JSONL group-by
- [x] **CANARY-03**: Auto-rollback fires when canary's rolling-60s `fallback_rate` exceeds baseline by >10% (configurable threshold; `AutoRollbackEnabled` default `false` until Phase 10's signal is real); admin endpoint `/canary` supports manual promote (canary → 100%) and rollback (canary → 0%); verified by integration tests for both transitions

## v2 Requirements

Deferred. Tracked but not in current roadmap.

### Reliability

- **REL2-01**: Circuit breaker as a distinct mechanism (with explicit open/half-open/closed state)
- **REL2-02**: Multi-level priority queue beyond two levels with aging / starvation prevention

### Observability

- **OBS2-01**: Prometheus `/metrics` exposition

### ML / Routing

- **ML2-01**: Active learning — only label uncertain (low-confidence) cases instead of all fallbacks
- **ML2-02**: Online learning — real-time weight updates per request without full retrain
- **ML2-03**: Multi-model routing (3+ models, cost-quality trade-off space) instead of binary 35B/122B
- **ML2-04**: Embedding model alternatives evaluated when Phase 8 validation gate signals bge-m3 int8 quality is insufficient: (a) bge-m3 FP32 (recover quality at ~5x latency cost if int8 regression observed), (b) `intfloat/multilingual-e5-large` (alt multilingual, 1024-dim, similar cost profile), (c) `Snowflake/snowflake-arctic-embed-l-v2.0` (newer multilingual SOTA), (d) `jhgan/ko-sroberta-multitask` if Korean ratio approaches 80%+ per `~/projs/smart-router-distillation/docs/embedding-classifier-decision-deep-dive.md` §1.7.5. Vector-dim change requires classifier retrain (Phase 8 handles transparently). Migration via Phase 9 canary infrastructure (10/90 split, cohort comparison via `model_version` + `prompt_korean_char_ratio`).

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
| Active / online learning | Deferred to v2 (ML2-01, ML2-02); v1 ML retrains in batch via BackgroundService |
| Multi-model routing (>2) | Deferred to v2 (ML2-03); v1 is binary 35B/122B |
| Removing the heuristic algorithm | Heuristic stays forever as v1 baseline + emergency fallback when ML fails (model file corrupt/missing); A/B comparator |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| API-01 | Phase 1 | Complete |
| API-02 | Phase 1 | Complete |
| API-03 | Phase 1 | Complete |
| API-04 | Phase 1 | Complete |
| API-05 | Phase 10 | Complete |
| API-06 | Phase 11 | Pending |
| API-07 | Phase 3 | Complete |
| ROUT-01 | Phase 1 | Complete |
| ROUT-02 | Phase 1 | Complete |
| ROUT-03 | Phase 1 | Complete |
| ROUT-04 | Phase 1 | Complete |
| ROUT-05 | Phase 1 | Complete |
| ROUT-06 | Phase 1 | Complete |
| ROUT-07 | Phase 1 | Complete |
| CONC-01 | Phase 3 | Complete |
| CONC-02 | Phase 3 | Complete |
| CONC-03 | Phase 3 | Complete |
| CONC-04 | Phase 3 | Complete |
| CONC-05 | Phase 3 | Complete |
| CONC-06 | Phase 3 | Complete |
| CONC-07 | Phase 1 | Complete |
| STRM-01 | Phase 2 | Complete |
| STRM-02 | Phase 2 | Complete |
| STRM-03 | Phase 2 | Complete |
| STRM-04 | Phase 2 | Complete |
| STRM-05 | Phase 2 | Complete |
| STRM-06 | Phase 2 | Complete |
| STRM-07 | Phase 2 | Complete |
| REL-01 | Phase 10 | Complete |
| REL-02 | Phase 10 | Complete |
| REL-03 | Phase 10 | Complete |
| REL-04 | Phase 10 | Complete |
| REL-05 | Phase 3 | Complete |
| OBS-01 | Phase 5 | Complete |
| OBS-02 | Phase 3 | Complete |
| OBS-03 | Phase 5 | Complete |
| OBS-04 | Phase 1 | Complete |
| ARCH-01 | Phase 1 | Complete |
| ARCH-02 | Phase 1 | Complete |
| ARCH-03 | Phase 1 | Complete |
| ARCH-04 | Phase 1 | Complete |
| ARCH-05 | Phase 1 | Complete |
| ARCH-06 | Phase 1 | Complete |
| ARCH-07 | Phase 1 | Complete |
| OPS-01 | Phase 11 | Pending |
| OPS-02 | Phase 11 | Pending |
| OPS-03 | Phase 11 | Pending |
| OPS-04 | Phase 1 | Complete |
| OPS-05 | Phase 1 | Complete |
| TEST-01 | Phases 1+3 | Complete |
| TEST-02 | Phases 2+3 | Complete |
| TEST-03 | Phase 2 | Complete |
| TEST-04 | Phase 3 | Complete |
| TEST-05 | Phase 10 | Complete |
| TEST-06 | Phase 3 | Complete |
| TEST-07 | Phase 1 | Complete |
| ML-01 | Phase 4 | Complete |
| ML-02 | Phase 4 | Complete |
| ML-03 | Phase 4 | Complete |
| ML-04 | Phase 4 | Complete |
| LOG-01 | Phase 5 | Complete |
| LOG-02 | Phase 5 | Complete |
| LOG-03 | Phase 5 | Complete |
| LOG-04 | Phase 5 | Complete |
| EMBED-01 | Phase 6 | Complete |
| EMBED-02 | Phase 6 | Complete |
| EMBED-03 | Phase 6 | Complete |
| CLS-01 | Phase 6 | Complete |
| CLS-02 | Phase 6 | Complete |
| CLS-03 | Phase 6 | Complete |
| FAIL-01 | Phase 7 | Complete |
| FAIL-02 | Phase 7 | Complete |
| FAIL-03 | Phase 7 | Complete |
| FAIL-04 | Phase 7 | Complete |
| RETRAIN-01 | Phase 8 | Complete |
| RETRAIN-02 | Phase 8 | Complete |
| RETRAIN-03 | Phase 8 | Complete |
| RETRAIN-04 | Phase 8 | Complete |
| RETRAIN-05 | Phase 8 | Complete |
| RETRAIN-06 | Phase 8 | Complete |
| CANARY-01 | Phase 9 | Complete |
| CANARY-02 | Phase 9 | Complete |
| CANARY-03 | Phase 9 | Complete |

**Coverage:**
- v1 requirements: 83 total (56 original + 27 ML-arc additions; +1 EMBED-03 for bge-m3 latency)
- Mapped to phases: 83 ✓
- Unmapped: 0
- Complete: 60 (Phases 1-6 ✓ + TEST-01/TEST-02 retroactive)
- Pending: 23 (11 ML arc remaining: Phases 7-9 + 12 deferred heuristic-cleanup)

---
*Requirements defined: 2026-05-07*
*Last updated: 2026-05-08 after Phase 6 (Real ML Routing) completion — 60 requirements verified Complete (Phase 6 EMBED-01/02/03 + CLS-01/02/03; live bge-m3 inference verification approved on automated evidence per operator)*
