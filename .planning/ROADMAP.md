# Roadmap: Smart Router

## Overview

Smart Router ships in two arcs:

**Arc A — Heuristic baseline (Phases 1-3, DONE; SOFT-PAUSED 2026-05-08):** Hexagonal F# foundation, SSE pass-through, 122B concurrency gate. Originally the heuristic was framed as the permanent baseline. As of 2026-05-08, the operator soft-paused heuristic development: heuristic code stays in `src/SmartRouter.Core/Heuristic.fs` + `Routing.Algorithm` dispatch as a **dormant emergency fallback** (model-file-corrupt scenario, debugging, rollback) but is no longer actively developed. Snapshot preserved at git branch `archive/heuristic-baseline` and tag `v0.5-heuristic-baseline`.

**Arc B — ML routing (Phases 4-9, PRIMARY PATH):** ML is now the primary routing algorithm, not "an additional option." Phase 4 ships the dispatch seam; Phases 5-9 build out logging → real bge-m3 int8 classifier → failure detection → retraining loop → canary deployment. When Phase 6 ships real ML, `appsettings.json` `Routing.Algorithm` flips default from `"heuristic"` to `"ml"`. Loop A (real-time routing) calls the ML classifier; Loop B (`BackgroundService`) eats fallback logs and self-improves the classifier on a periodic cadence. Phase 9 canary compares **ML model versions to each other** (cohort tagging via `model_version` + `prompt_korean_char_ratio`) — heuristic-vs-ML A/B is no longer a goal.

**Arc C — Heuristic-baseline cleanup (Phases 10-11, DEFERRED):** What was originally Phases 4 and 6 — health probing + fallback + graph_indexing-no-fallback rule, then launchd deployment + `/v1/models` + README. Originally deferred to ship the ML arc first; with heuristic now soft-paused, **the priority of these phases is further reduced.** They may eventually ship for production deploy (launchd, README, `/v1/models`) but the heuristic-specific health/fallback work is no longer load-bearing — ML's own validation gate (Phase 8) and CoreML-EP fallback (Phase 6 EMBED-03) cover most of the same ground. Old Phase 5 (Observability + Tests) dissolves: OBS-01 / OBS-03 are absorbed into NEW Phase 5 (Decision Logging — literally Loop B's input); TEST-01 / TEST-02 are already covered by Phases 1-3 tests (RoutingTests 22, StreamingTests 8, QueueTests 9).

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3, ...): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Foundation** ✓ — Project scaffold, Core domain types, routing pipeline, non-streaming HTTP adapter, appsettings wiring
- [x] **Phase 2: SSE Streaming Pass-Through** ✓ — Complete atomic SSE correctness cluster (STRM-01..07); Hermes is unblocked when this ships
- [x] **Phase 3: 122B Concurrency Gate** ✓ — Complete atomic concurrency cluster (CONC-01..06 + REL-05); Graphify concurrent requests are safe when this ships
- [x] **Phase 4: ML Algorithm Seam** ✓ — Placeholder ML algorithm + config dispatch (`Routing.Algorithm: "heuristic" | "ml"`) + CLI `--routing-algorithm` override; heuristic stays default *until Phase 6 ships real ML* (then flips to "ml"); existing tests stay green; same-shape ML test confirms dispatch. **Heuristic soft-paused 2026-05-08; ML is primary path going forward.**
- [x] **Phase 5: Routing-Decision Logging** ✓ — Per-request structured JSONL log with routing reason, latency, model_version, fallback flag, correlation ID; thread-safe writer; absorbs OBS-01 and OBS-03 from old Phase 5 — this is Loop B's input
- [x] **Phase 6: Real ML Routing** ✓ — `Microsoft.ML.OnnxRuntime` + bge-m3 **int8 dynamic-quantized** (~580MB, 1024-dim, multilingual; chosen over bge-small for Korean+English mixed traffic; quantized from start per §1.7.7 row 4) + ML.NET `LbfgsLogisticRegression` + replace placeholder; first model file auto-generated on first run; latency budget <50ms p95 (CoreML EP fallback if exceeded under load)
- [ ] **Phase 7: Failure Detection + Teacher Labeling** — Failure detector (fallback-used + error + short-response + low-confidence signals); teacher labeler (HTTP to 122B with timeout/retry/cost cap, `prompts/teacher_prompt.md`); hard-case dataset extraction
- [ ] **Phase 8: Retraining Loop** — Dataset merger (old 70 + new 30 with class balance); ML.NET trainer; held-out validator with rollback gate; `BackgroundService` + `PeriodicTimer`; `PredictionEnginePool` + `watchForChanges:true` for atomic hot-reload; idempotency lock
- [ ] **Phase 9: Canary Deployment** — `Microsoft.FeatureManagement` + `PercentageFilter` for 10/90 split; `model_version` cohort tagging in logs; comparison + rollout/rollback workflow
- [ ] **Phase 10: Health + Fallback + graph_indexing No-Fallback** — (was old Phase 4) Health probing, retry policy, fallback routing, and the graph_indexing-must-fail correctness unit
- [ ] **Phase 11: Deployment + Documentation** — (was old Phase 6) launchd plist, `/v1/models` endpoint, README

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
**Plans**: 3 plans

Plans:
- [x] 03-01-QUEUE-DISPATCHER-PLAN.md ✓ — Port-shape change (IUpstreamClient takes RoutingDecision) + QueueDispatcher.fs (SemaphoreSlim(1), two Queue<Ticket> high/low + fairness K, dispatcher loop sub-pattern A, linked CTS timeout from acquire, try/finally Release, 35B bypass) + CompositionRoot DI swap + appsettings Queue section + Program.fs MaxConcurrent122B=1 validation
- [x] 03-02-STATS-AND-QUEUE-TESTS-PLAN.md ✓ — GET /stats endpoint (Stats.fs) reading IStatsProvider; QueueTests.fs with FakeUpstreamClient + LatencyFake (9 tests: serialization, priority preempt, K-th forced low pick at low1Idx==4, cancel-pre-dequeue, cancel-post-dequeue mid-acquire, timeout release, 35B bypass, live snapshot, in-process Kestrel /stats JSON wire assertion for all 10 snake_case keys)
- [x] 03-03-LOAD-TESTS-PLAN.md ✓ — LoadTests.fs with ptestCaseAsync burst tests (20-concurrent serialization + mixed-priority cap-holds-under-load); opt-in only, default dotnet test unchanged at 39/39

### Phase 4: ML Algorithm Seam
**Goal**: A new ML routing algorithm exists as a parallel option to the heuristic. `Routing.Algorithm` config key (`"heuristic" | "ml"`) selects which one runs at request time. CLI flag `--routing-algorithm=...` overrides config. The placeholder ML algorithm is intentionally dumb (always picks 35B) — the value of this phase is the *seam*, not the model.
**Depends on**: Phase 3
**Requirements**: ML-01, ML-02, ML-03, ML-04
**Success Criteria** (what must be TRUE):
  1. `routeRequest` accepts a routing algorithm function as a parameter; `applyHeuristic` and `applyML` both have signature `RoutingConfig -> RouterRequest -> RoutingDecision`. Existing 39 heuristic tests still pass after every callsite is updated to pass `Heuristic.applyHeuristic` explicitly.
  2. With `appsettings.json: "Routing.Algorithm": "ml"`, sending any request hits the placeholder `applyML` (verified by a unit test that injects a probe-able placeholder); with `"heuristic"`, the heuristic path runs (verified by existing heuristic tests staying green).
  3. CLI flag `--routing-algorithm=ml` overrides the config-set `"heuristic"` (verified by a startup-test that boots both ways and asserts the registered function).
  4. `SmartRouter.Core/Routing/Heuristic.fs` and `SmartRouter.Core/Routing/ML.fs` are separate modules with **zero cross-imports** — verified by grep.
  5. Default behavior is unchanged: `Routing.Algorithm` defaults to `"heuristic"` if absent from config; old behavior preserved bit-for-bit.
**Plans**: 3 plans

Plans:
- [x] 04-01-CORE-REFACTOR-PLAN.md ✓ — Refactor Routing.fs into flat siblings Heuristic.fs + ML.fs; add `RoutingAlgorithm` alias + `| ML` DU case in Domain.fs; update routeRequest signature; migrate RoutingTests callsites; add scripts/check-routing-isolation.sh
- [x] 04-02-CONFIG-AND-CLI-PLAN.md ✓ — appsettings.json `Routing.Algorithm` key + CompositionRoot dispatch singleton + ChatCompletions DI resolution + Program.fs `--routing-algorithm` CLI flag (AddInMemoryCollection BEFORE configureServices)
- [x] 04-03-ML-ROUTING-TESTS-PLAN.md ✓ — New MLRoutingTests.fs with 5 testSequenced tests (placeholder behavior + routeRequest dispatch + config dispatch + CLI override); wire into .fsproj + rootTests list; appsettings.json bin-copied via fsproj for Tests 4+5

### Phase 5: Routing-Decision Logging
**Goal**: Every routing decision (whether heuristic or ML) emits a structured JSONL log line with all the fields Loop B's retrainer needs: prompt hash, request features, routing reason, target model, latency, fallback flag, model_version, correlation ID. The writer is thread-safe (Serilog `Channel`-backed, NOT `File.AppendAllText`). This phase delivers OBS-01 and OBS-03 (absorbed from old Phase 5) plus the persistence destination for ML retraining.
**Depends on**: Phase 4
**Requirements**: OBS-01, OBS-03, LOG-01, LOG-02, LOG-03, LOG-04
**Success Criteria** (what must be TRUE):
  1. After a request completes, `logs/decisions/YYYY-MM-DD.jsonl` (UTC date) contains exactly one line per request with: `schema_version` (int, =1), `correlation_id`, `prompt_hash` (SHA-256), `prompt_korean_char_ratio` (float 0..1), `routing_algorithm` (heuristic|ml), `routing_reason` (ExplicitModelOverride|ExplicitTask|Heuristic|Default|ML), `target` (Qwen35B|Qwen122B), `latency_ms`, `fallback_used` (bool, always false in this phase — flag set in Phase 10), `model_version` (string; "heuristic-v1" or e.g. "ml-v0-placeholder"), `task_type` (optional), `timestamp` (ISO 8601 UTC).
  2. Two concurrent identical requests both produce two valid JSON lines (no `IOException`, no interleaved bytes) — verified by a 100-concurrent-requests test that reads the file and counts valid JSON lines.
  3. The same correlation ID appears in stderr Serilog output and the JSONL file for the same request — verified by a test that captures both.
  4. JSONL writer flushes on shutdown (graceful `app.StopAsync`) — no in-flight log loss; verified by start/route/stop/read sequence.
**Plans**: 3 plans

Plans:
- [x] 05-01-DECISION-LOG-INFRA-PLAN.md ✓ — DecisionLog record + 12-field schema, Channel<DecisionLog> + BackgroundService single-writer with daily UTC rotation, CorrelationMiddleware (HttpContext.Items + Serilog LogContext), DI wiring, .gitignore logs/
- [x] 05-02-ENDPOINT-WIRING-PLAN.md ✓ — Extract RoutingAlgorithmRegistration to Adapters/RoutingAlgorithm.fs (F# compile-order fix); ChatCompletions handler emits DecisionLog at 8 exit points; ALL SSE error event bodies include correlation_id field (LOG-04 third source)
- [x] 05-03-LOGGING-TESTS-PLAN.md ✓ — LoggingTests.fs with 5 testSequenced tests (schema, 100-concurrent integrity, correlation across 3 sources via CapturingSink ILogEventSink, graceful drain, SSE-error correlation_id end-to-end) + StreamingTests.startTestRouter temp-dir hygiene

### Phase 6: Real ML Routing
**Goal**: Replace the placeholder `applyML` with a real classifier: bge-m3 **int8 dynamic-quantized** (1024-dim, multilingual, ~580MB) via `Microsoft.ML.OnnxRuntime` + ML.NET `LbfgsLogisticRegression` loaded from a model file. Same-prompt comparison shows heuristic and ML producing different decisions on the same input — including a Korean prompt where bge-m3's tokenizer-aware multilingual semantics differ from the heuristic's English keyword matcher. The first model file is auto-generated at startup if missing (dummy 1024-dim weights → ~50/50 routing) so the system bootstraps without a pre-trained model.
**Depends on**: Phase 5
**Requirements**: EMBED-01, EMBED-02, EMBED-03, CLS-01, CLS-02, CLS-03
**Why bge-m3 int8 over bge-small (operator decision 2026-05-08)**: operator's traffic mixes Korean+English. bge-small (-en) tokenizer cannot handle Hangul (`[가-힣]`) — sub-`[UNK]` byte fallback, Korean prompts route at random per smart-router-distillation `embedding-classifier-decision-deep-dive.md` §1.7.1. **int8 quantization from start** per §1.7.7 row 4 ("라우터 latency budget 빠듯 → bge-m3 int8 quantized"): file 2.3GB → ~580MB, latency 50-80ms → 20-30ms (CPU, M-series), accuracy regression typically <2% which Phase 8's validation gate catches automatically. Phase 8 will retrain with the int8 embedder so the trained classifier matches the production embedder; no double-quantization concerns.
**Success Criteria** (what must be TRUE):
  1. `IEmbedder` port in Core; `BgeM3Embedder` adapter in Cli using `Microsoft.ML.OnnxRuntime` + SentencePiece tokenizer; loads **int8 dynamic-quantized** bge-m3 ONNX; produces 1024-dim L2-normalized vectors; verified by unit test on three prompts (English, Korean, mixed).
  2. `IClassifier` port in Core; `MlNetClassifier` adapter in Cli loading `models/router.zip` via `PredictionEnginePool`; predicts on 1024-dim input; verified by unit test that loads a hand-built dummy model and predicts.
  3. `applyML` is the real implementation: embed → classify → threshold → `RoutingDecision { Target; Priority; Reason = ML }`. Heuristic decision and ML decision diverge on at least one test prompt INCLUDING a Korean prompt where bge-m3's multilingual semantics differ from heuristic keyword matching (verified by a/b assertion).
  4. First-run bootstrap: if `models/router.zip` is missing, a startup task creates a dummy model with random 1024-dim weights so `applyML` doesn't throw; logged warning explains the model is dummy.
  5. `model_version` in DecisionLog reflects the loaded model's filename hash (so Loop B's retraining is cohorted correctly).
  6. Embedding latency p95 on Mac M-series CPU stays under 50ms per prompt with int8 quantized bge-m3 (cold-start may exceed; warm path is the target). If exceeded under load, fallback path is ONNX CoreML execution provider (Apple Neural Engine) — measured by EMBED-03 verification test.
**Plans**: 3 plans

Plans:
- [x] 06-01-CORE-PORTS-AND-NUGET-PLAN.md ✓ — Core MLPorts.fs (IEmbedder + IClassifier + ClassifierPrediction); RoutingConfig.MlThreshold field; ML.fs makeApplyML closure factory; Cli .fsproj 4 NuGet pins (Microsoft.ML.OnnxRuntime 1.25.1, Microsoft.ML.Tokenizers 2.0.0, Microsoft.ML 5.0.0, Microsoft.Extensions.ML 5.0.0); .gitignore models/; scripts/download-models.sh + scripts/export-bge-m3-int8.sh
- [x] 06-02-CLI-ADAPTERS-AND-DI-PLAN.md ✓ — Adapters/BgeM3Embedder.fs (ONNX + SentencePiece + mean-pool + L2 normalize; warm-up) + MlNetClassifier.fs (PredictionEnginePool watchForChanges:true) + ModelBootstrapper.fs (ensureEmbeddingFilesPresent + ensureDummyModel + computeModelVersion); CompositionRoot strict ordering; Routing.ML appsettings section; legacy applyML removed; **Routing.Algorithm flipped to "ml" as last edit**
- [x] 06-03-ML-TESTS-PLAN.md ✓ — MLEmbeddingTests.fs (EMBED-01/02/03 + CLS-03 cosine) + MLClassifierTests.fs (CLS-01 pool predict + CLS-02 bootstrap + model_version hash) + MLRoutingTests +2 tests; mlTestCase-gated; default `dotnet test` reports 50 pass + 10 ignored without model files

### Phase 7: Failure Detection + Teacher Labeling
**Goal**: Build the offline data pipeline that produces labeled training samples from production logs. Failure detector reads JSONL logs and emits hard cases (currently: `fallback_used=true` only — Phase 10 expands signals). Teacher labeler calls 122B (or Claude) per hard case and produces `(prompt, label)` pairs with timeout, retry, and a daily cost cap. Hard-case dataset is appended to `datasets/hard-cases.jsonl`. This phase produces no behavior change at request time — it's prep for Phase 8's retraining loop.
**Depends on**: Phase 6
**Requirements**: FAIL-01, FAIL-02, FAIL-03, FAIL-04
**Success Criteria** (what must be TRUE):
  1. Given a `decisions/*.jsonl` file with mixed records, `extractHardCases` returns only `fallback_used=true` records; verified by unit test on a fixture file.
  2. Teacher labeler sends a single `prompt` to 122B with the prompt template from `~/projs/smart-router-distillation/prompts/teacher_prompt.md` and parses the response into a label; verified by integration test against a fake upstream Kestrel that returns canned responses.
  3. Teacher labeler enforces 30s timeout per request, 3x retry on transient failure, daily cost cap (configurable; default 1000 calls/day) — verified by tests that exercise each limit.
  4. `datasets/hard-cases.jsonl` is append-only; concurrent runs don't corrupt it (single-writer file lock); verified by 10-concurrent-runs test.
**Plans**: 6 plans

Plans:
- [ ] 07-01-FOUNDATION-PLAN.md — Core RetrainingPorts.fs + 3 Cli adapter stubs + .fsproj wiring (unblocks Wave 2 parallel adapter implementations)
- [ ] 07-02-FAILURE-DETECTOR-PLAN.md — FailureDetector real impl (JSONL reader + fallback_used filter + empty-result Information log)
- [ ] 07-03-TEACHER-LABELER-PLAN.md — TeacherLabeler real impl (named HttpClient + prompt template loader + persistent daily cost cap + ROUTE_35B/ROUTE_122B parser)
- [ ] 07-04-DATASET-WRITER-PLAN.md — HardCaseDatasetWriter real impl (Channel + BackgroundService + Wait-on-overflow + dedupe HashSet)
- [ ] 07-05-CLI-WIRING-PLAN.md — CompositionRoot DI registrations + named "teacher" HttpClient with retry handler + Program.fs --retrain CLI handler + appsettings.json + prompts/teacher-prompt.md + scripts/seed-hard-cases.fsx + .gitignore datasets/
- [ ] 07-06-TESTS-PLAN.md — 3 test files (FailureDetectorTests + TeacherLabelerTests + HardCaseDatasetTests; 15+ tests total) + Tests.fsproj + RouterTests.rootTests

### Phase 8: Retraining Loop
**Goal**: Loop B is real. A `BackgroundService` periodically (every hour, or when `hard-cases.jsonl` exceeds 500 entries) reads hard-case dataset + old training set, merges 70/30 with class balance, retrains the ML.NET LR classifier, validates against a held-out set, and writes the new model to `models/router.zip` only if validation passes. `PredictionEnginePool` with `watchForChanges:true` swaps the live classifier atomically; in-flight requests complete on the old model. A `Mutex` ensures only one retrain runs at a time. Failures in Loop B never affect Loop A — `try/with` isolation is mandatory.
**Depends on**: Phase 7
**Requirements**: RETRAIN-01, RETRAIN-02, RETRAIN-03, RETRAIN-04, RETRAIN-05, RETRAIN-06
**Success Criteria** (what must be TRUE):
  1. Dataset merger produces a balanced training set: 70% old, 30% new, with each class appearing in ≥30% of samples (no catastrophic forgetting); verified by a unit test on synthetic input.
  2. Retraining is triggered by `hard-cases.jsonl` count ≥ 500 OR by a 1-hour timer (whichever comes first); verified by two integration tests.
  3. Validation gate: new model must hit accuracy ≥ baseline AND `fallback_rate` on validation set ≤ baseline. If either fails, the new `router.zip` is not written; the previous model stays live; the rejection is logged.
  4. After a successful retrain, the next request transparently uses the new model — `model_version` in DecisionLog changes; verified by a 3-request test (before / retrain / after).
  5. Concurrent retrain triggers are serialized by `Mutex`; the second trigger waits or skips; verified by a 2-concurrent-trigger test.
  6. A retrain that throws mid-way doesn't crash the host process or block subsequent retrains; verified by a force-throw test that confirms Loop A keeps responding and the next tick retries.
**Plans**: TBD

Plans:
- [ ] 08-01: Implement `DatasetMerger` (70/30 balance, class-stratified sampling); `Retrainer` (ML.NET `LbfgsLogisticRegression.Fit`); `Validator` (held-out accuracy + fallback rate vs baseline); `ModelRegistry` (write router.zip atomically via temp file + rename)
- [ ] 08-02: Implement `RetrainingService : BackgroundService` (`PeriodicTimer` + count-based trigger + Mutex idempotency + try/with isolation); wire `PredictionEnginePool.AddPredictionEnginePool<...>` with `watchForChanges:true`
- [ ] 08-03: RetrainingTests.fs — merger balance, validation-gate rejection on bad model, concurrent-trigger serialization, mid-retrain throw isolation, end-to-end retrain → hot-reload → DecisionLog model_version flip

### Phase 9: Canary Deployment
**Goal**: When a new model lands, route only a percentage of traffic (default 10%) to it for a configurable window before promoting to 100%. `Microsoft.FeatureManagement` + `PercentageFilter` does the split based on `correlation_id` hash. Logs always tag `model_version` so cohort comparison is straightforward (canary vs baseline fallback rate, latency, etc.). Manual or automatic rollback: if canary's `fallback_rate` exceeds baseline by >10%, the canary model is unloaded and traffic returns to 100% baseline.
**Depends on**: Phase 8
**Requirements**: CANARY-01, CANARY-02, CANARY-03
**Success Criteria** (what must be TRUE):
  1. With canary set to 10%, ~10% of requests go to the new model and ~90% to the previous; verified statistically over 1000 fake-upstream requests (binomial 95% CI test).
  2. `DecisionLog` records `model_version` distinguishing canary vs baseline; cohort comparison is trivial (group by `model_version`).
  3. A `/canary` admin endpoint (or config setting) promotes the canary to 100% OR rolls back; verified by integration test that exercises both transitions.
  4. Auto-rollback: if canary's rolling 60s `fallback_rate` exceeds baseline's by >10%, canary is unloaded automatically; logged. Verified by an integration test that injects fake "bad" responses for the canary and confirms rollback fires.
**Plans**: TBD

Plans:
- [ ] 09-01: Wire `Microsoft.FeatureManagement` + `PercentageFilter`; tag `correlation_id` for sticky bucketing; expose `Routing.Canary.PercentageEnabled` config
- [ ] 09-02: Implement `/canary` admin endpoint (promote / rollback); auto-rollback watcher (rolling-window fallback-rate compare)
- [ ] 09-03: CanaryTests.fs — split distribution test (1000-request statistical), promote/rollback transitions, auto-rollback trigger

### Phase 10: Health + Fallback + graph_indexing No-Fallback
**(Was Phase 4 in the original roadmap; deferred at operator request after Phase 3 completion to ship the ML arc first.)**

**Goal**: The router knows whether each upstream is reachable, gracefully reroutes 122B requests to 35B when 122B is down — except for graph_indexing which must return an error rather than silently downgrade.
**Depends on**: Phase 9 (or earlier — could land between Phases 4-9 if priority shifts; currently slotted last in the heuristic-cleanup arc)
**Requirements**: REL-01, REL-02, REL-03, REL-04, API-05, TEST-05
**Success Criteria** (what must be TRUE):
  1. `GET /health` returns per-upstream reachability status that reflects whether each Qwen server is actually responding.
  2. When 122B is stopped, a `reasoning` task request is transparently served by 35B (logged as fallback; `fallback_used=true` in DecisionLog); the response reaches the caller without error.
  3. When 122B is stopped, a `graph_indexing` request returns an error response (not a 35B response) — the response body contains a clear error message, not model output.
  4. A request that fails on first attempt due to a transient upstream error is retried with backoff and succeeds on retry — verified by failure tests with a fake upstream that fails once then succeeds.
  5. The failure tests (timeout, malformed JSON, unavailable model, fallback path, graph_indexing-must-fail) all pass.
**Plans**: TBD

Plans:
- [ ] 10-01: Implement HealthAdapter.fs (background poll of /v1/models per upstream, reachability tracking); wire /health endpoint
- [ ] 10-02: Add fallback policy to QueueDispatcher (check IHealthProbe before enqueue; graph_indexing → GraphIndexingMustFail error; other 122B → reroute to 35B with IsFallback=true); wire retry policy via AddResilienceHandler in QwenUpstreamClient
- [ ] 10-03: Write failure tests (graph_indexing-must-fail, 122B-unavailable fallback, retry-on-transient, health probe timeout)

### Phase 11: Deployment + Documentation
**(Was Phase 6 in the original roadmap; deferred along with Phase 4 → 10.)**

**Goal**: The router auto-starts under launchd supervision, the /v1/models endpoint proxies both upstream model lists, and the README gives the operator everything needed to tune, debug, and connect both clients.
**Depends on**: Phase 10
**Requirements**: API-06, OPS-01, OPS-02, OPS-03
**Success Criteria** (what must be TRUE):
  1. `launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist` starts the router and it is reachable at `http://127.0.0.1:4000/health` without running `dotnet run`.
  2. After a simulated crash (kill -9 on the router process), launchd restarts it automatically within 5 seconds.
  3. `GET /v1/models` returns a deduplicated list that includes model entries from both upstream servers.
  4. The README explains the routing decision pipeline (heuristic + ML), how to switch algorithms, how to tune the heuristic threshold and keyword list, how to interpret DecisionLog, how to connect Hermes, how to connect Graphify, the canary workflow, and the launchd restart procedure — a new operator can follow the steps without asking for clarification.
**Plans**: TBD

Plans:
- [ ] 11-01: Implement /v1/models endpoint (proxy both upstreams, deduplicate by id); configure dotnet publish (-r osx-arm64 --self-contained); write com.ohama.smart-router.plist with absolute dotnet path
- [ ] 11-02: Write README (architecture overview, routing rules incl. heuristic + ML + canary, threshold tuning, debugging, Hermes integration, Graphify integration, launchd restart procedure, retraining loop operations)

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → **(ML arc)** 4 → 5 → 6 → 7 → 8 → 9 → **(deferred heuristic-cleanup arc)** 10 → 11

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation | 3/3 | ✓ Complete | 2026-05-07 |
| 2. SSE Streaming Pass-Through | 2/2 | ✓ Complete | 2026-05-08 |
| 3. 122B Concurrency Gate | 3/3 | ✓ Complete | 2026-05-08 |
| 4. ML Algorithm Seam | 3/3 | ✓ Complete | 2026-05-08 |
| 5. Routing-Decision Logging | 3/3 | ✓ Complete | 2026-05-08 |
| 6. Real ML Routing | 3/3 | ✓ Complete | 2026-05-08 |
| 7. Failure Detection + Teacher Labeling | 0/3 | Not started | - |
| 8. Retraining Loop | 0/3 | Not started | - |
| 9. Canary Deployment | 0/3 | Not started | - |
| 10. Health + Fallback + graph_indexing No-Fallback | 0/3 | Not started (was Phase 4) | - |
| 11. Deployment + Documentation | 0/2 | Not started (was Phase 6) | - |
