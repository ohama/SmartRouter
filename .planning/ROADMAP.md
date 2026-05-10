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
- [x] **Phase 7: Failure Detection + Teacher Labeling** ✓ — Failure detector (fallback-used signal — Phase 10 expands); teacher labeler (named "teacher" HttpClient + AddResilienceHandler + persistent daily cost cap + ROUTE_35B/ROUTE_122B parser); hard-case dataset writer (Channel + BackgroundService + dedupe HashSet); --retrain CLI handler; 16 new tests (66 total + 10 ignored)
- [x] **Phase 8: Retraining Loop** ✓ — Loop B real (`RetrainingService : BackgroundService` + 2 PeriodicTimers); `DatasetMerger` 70/30 class-stratified merge with first-retrain bootstrap; `Retrainer` ML.NET LbfgsLogisticRegression + atomic `File.Move(overwrite=true)`; `Validator` with `fallback_rate := 1.0 - PositiveRecall` gate; `IModelVersionProvider` BCL-only port + concrete adapter for live `model_version` updates; `SemaphoreSlim(1,1).Wait(0)` skip-if-busy idempotency; `try/with` Loop A/B isolation. 7 new tests covering RETRAIN-01..06 (73 total + 10 ignored)
- [x] **Phase 9: Canary Deployment** ✓ — `Microsoft.FeatureManagement.AspNetCore 4.5.0` + `ContextualTargetingFilter` for sticky 10/90 split keyed on `correlation_id` (NOT `PercentageFilter` — non-sticky); `RoutingDecision.ModelVersion` cohort label propagated from ML.fs (single `isCanary` boolean gates classifier-selection AND model_version); rolling-60s fallback-rate watchdog with `AutoRollbackEnabled=false` default until Phase 10's `fallback_used` signal is real; `/canary` admin endpoint (status / promote / rollback / enable) coordinates with Phase 8 RetrainingService via shared `IRetrainLock`; `CanaryService` IHostedService with FileSystemWatcher for `router-canary.zip` lifecycle. 12 new tests covering CANARY-01..04 (78 always-on + 17 ignored when models absent; 85+10 with embedding files present)
- [x] **Phase 10: Health + Fallback + graph_indexing No-Fallback** ✓ — (was old Phase 4) `HealthService` BackgroundService + PeriodicTimer (10s default) probing `/v1/models` per upstream; ConcurrentDictionary state with `ConsecutiveFailureThreshold=1`; `IHealthProbe` extended with sync `IsReachable` + `LastProbedAt`. 5 named HttpClients via `.ConfigureHttpClient(...)` chain form (-nonstream variants with retry via `AddResilienceHandler`; -stream variants and `health-probe` no retry — SSE non-idempotent, partial output cannot be replayed). Fallback policy: ChatCompletions pre-flight (BEFORE SSE headers) — graph_indexing+122B-down → 503 + `{error: {type: "model_unavailable"}}`; non-graph_indexing+122B-down → shadow-rebind decision to Qwen35B with `IsFallback=true` + `Reason=FallbackTo35B`; existing decisionLogger.Log calls auto-pickup `fallback_used=true`. `AutoRollbackEnabled` flipped to `true` — Phase 9 CanaryWatchdog rolling-60s metric becomes meaningful. ML feedback loop is now self-sustaining: real upstream failures → fallback fires → fallback_used=true → FailureDetector → TeacherLabeler → RetrainingService → ModelVersionProvider.Update → ChatCompletions sees new model_version. 5 new tests covering HLTH-04..08 (83 pass + 17 ignored without embeddings; 90+10 with embeddings)
- [x] **Phase 11: Deployment + Documentation** ✓ — (was old Phase 6) `GET /v1/models` endpoint (parallel upstream fetch via `Task.WhenAll` + `IHealthProbe.IsReachable` gating + reuses Phase 10 `health-probe` HttpClient + `JsonElement.Clone()` lifetime guard + dedupe by `id` first-seen-wins; both-down → HTTP 200 + empty data array). `deploy/com.ohama.smart-router.plist` mirroring operator's `qwen36-35b.plist` + `qwen122b.plist` convention exactly (KeepAlive=`<true/>`, ThrottleInterval=30, RunAtLoad=`<true/>`, dotnet absolute path `/opt/homebrew/bin/dotnet`, WorkingDirectory `~/llm-system/services/smart-router`). `scripts/deploy.sh` (framework-dependent `dotnet publish -c Release`) + `scripts/install-launchd.sh` (manual `launchctl load -w` documented in heredoc, NOT auto-executed). 1020-line repo-root `README.md` covering 14 sections — all 8 endpoints, 7 task types, Loop A/B, canary workflow, launchd procedure, troubleshooting. 3 new tests (MODELS-01..03; 86 pass + 17 ignored / 93 + 10 with embeddings). 22/22 automated must-haves verified; 3 ROADMAP success criteria deferred to manual host UAT (require live macOS launchd + running mlx_lm servers).
- [x] **Phase 12: Heuristic Routing Removal** ✓ — Routing-decision heuristic 코드 경로 완전 제거. 6 plans + 1 gap-closure across 4 waves: 12-01 Core deletion (Heuristic.fs gone, RoutingReason.Heuristic DU case + RoutingConfig.Keywords/ComplexityThreshold fields gone, canonicalKeywords gone, DecisionLogger.formatReason cascade), 12-02 Cli rewire (configureServices split into configureRequestPipeline + configureWithoutMl per Q1=B; Routing.Algorithm config key + --routing-algorithm CLI flag entirely deleted per Q3+Q4; ML wiring unconditional), 12-03 RoutingTests.fs deleted entirely per Q5, 12-04 MLRoutingTests pruned (3 heuristic-related tests gone), 12-05 fixture migration (StreamingTests/LoggingTests/HealthFallbackTests inject test-stub RoutingAlgorithmRegistration via Q2=B; LoggingTests routing_algorithm assertion → "ml"; HealthFallbackTests uses option-b configureWithoutMl + manual HealthService/QueueDispatcher), 12-06 cleanup (check-routing-isolation.sh deleted, stray JSONL gone, .gitignore src/**/logs/ guard, 4-file comment cleanup), gap-closure ModelsTests.fs migrated to configureWithoutMl. archive/heuristic-baseline branch + v0.5-heuristic-baseline tag UNTOUCHED per Q7. New baseline: 62 pass + 16 ignored + 0 failed (down 24 from Phase 11; user-decided RoutingTests entire deletion). 7/7 verifier must-haves passed. ML routing is the sole stage-3 path.
- [x] **Phase 13: Service Logging** ✓ — Dual sink shipped: Serilog Console (stderr OBS-04) + rolling File at `logs/operational/smart-router-{Date}.log` (50MB cap, 30-file retention, 2s flush). Output template `{Timestamp:ISO-8601} [{Level:u3}] {SourceContext} [{correlation_id}] {Message:lj}` with `[-]` default for non-request scope. `appsettings.json:Serilog.MinimumLevel.Override` activated (suppresses Microsoft.AspNetCore noise to Warning). 12 type-based adapters migrated to `ILogger<T>` ctor injection; 4 module-based files (Validator/DatasetMerger/Retrainer/ModelBootstrapper) accept `(logger: ILogger)` function parameter; ChatCompletions endpoint uses `ILoggerFactory.CreateLogger("ChatCompletions")`. Static `Log.*` retained at 3 bootstrap sites (Program.fs:61/263, CompositionRoot.fs:134). ChatCompletions hot-path `LogInformation` → `LogDebug` (was 100s req/min × ~50MB/day waste). HealthService transition-only INFO emissions (was 12/min steady-state); 4 endpoint files (Health/Stats/Canary/Models) emit one DEBUG per hit. `--log-level=enum` CLI flag (verbose/debug/information/warning/error/fatal + short aliases); legacy `--trace` raises clear migration error. `LogRetentionService : BackgroundService` registered in `configureRequestPipeline` only (NOT `configureWithoutMl`); prunes operational logs > 30 days, decision JSONL > 90 days, teacher-cap > 7 days; PeriodicTimer 60-min interval. Startup + shutdown banners (port, model.version, canary state, queue config, teacher cap, log dir). README §9.6-9.9 updated to implemented reality (no "v1: ignored" caveats). 14 new tests in `LogRotationTests.fs`; **76 pass + 16 ignored + 0 failed** (was 62+16). gsd-verifier: 10/10 must-haves passed. ARCH-01 preserved (zero Core changes; all Cli-only).
- [x] **Phase 14: Quality Fallback (35B → 122B retry) + Trace Infrastructure** ✓ — distillation 디자인의 quality-based fallback 패턴을 smart-router 에 첫 구현. 6 plans / 6 waves: 14-01 cold-start CLI (`Adapters/ColdStart.fs` + `--cold-start` flag with timestamp backup of router.zip + .prev + datasets, idempotent on missing, single-startup behavior); 14-02 prompt UID + trace logging (`Adapters/TraceLogger.fs` Channel + BackgroundService single-writer mirror of DecisionLogWriter, `--trace-responses` CLI flag injects `Trace:Enabled=true`, conditional triple-reg in `configureRequestPipeline` only); 14-03 Core types (`RoutingReason.FallbackTo122B` 6th DU case + `formatReason` arm `"fallback_to_122b"` + `Adapters/QualityCheck.fs` BCL-only `isBadResponse` + `Routing.QualityFallback.{Enabled,MinResponseLength,BadKeywords}` config); 14-04 ChatCompletions handler (non-streaming branch quality check + 122B retry; streaming branch `INTENTIONALLY SKIPPED` comment; `truncate` helper; trace emission via null-safe `GetService<ITraceLogger>`); 14-05 `QualityFallbackTests.fs` 2 integration tests (QF-01 35B-only success / QF-02 quality fallback fires; both verified by JSONL log inspection with prompt_uid grep); 14-06 README §5.5/§7/§9.1/§9.10/§12.6 + planning docs updates. `prompt_uid` = `prompt_hash[:12]` (no new field; reuse Phase 5 hash). Edge cases: 122B unreachable + 35B bad → 35B as-is (graceful); 122B retry Error → 35B as-is; QualityFallback.Enabled=false → never fires (kill switch); streaming requests bypass entirely. **82 passed + 16 ignored + 0 failed** (was 80+16). gsd-verifier: 7/7 must-haves passed. ARCH-01 preserved (only RoutingReason DU case in Core; QualityCheck/TraceLogger/ColdStart all Cli adapters).
- [ ] **Phase 15: Quality Signal Enrichment** — Phase 14 의 `isBadResponse` heuristic 을 풍부화. 모델이 이미 보내주는 `finish_reason` 활용 (`"length"`/`"content_filter"` → bad), case-insensitive 키워드 매칭, default keyword 에 refusal 패턴 추가 (`"I cannot"`, `"As an AI"` 등), 한글 응답 길이 보정 (한글 비율 비례), Shannon entropy 기반 반복 루프 감지 (`"the the the..."` 류). 모든 변경은 `Adapters/QualityCheck.fs` (Cli 어댑터) 안에서만 일어나 ARCH-01 보존. Backward-compat: 기존 QF-01/QF-02 테스트 통과 + 신규 QSE-* 테스트 추가. Phase 16 의 borderline classifier 가 entropy/length band 신호를 입력으로 사용. `~/projs/smart-router/.planning/docs/quality-check-improvement-options.md` Tier 1+2 구현.
- [ ] **Phase 16: 122B-as-Judge for Borderline Cases** — Phase 15 heuristic 통과했지만 quality 가 borderline 한 케이스 (entropy/length/keyword band edge) 에만 122B 에 1-token verification call (`"Is this response good for the question? YES/NO"`) 을 보냄. 명백한 good/bad 는 fast path 유지 (judge 안 부름). `prompt_hash + response_hash` 키 cache (LRU bounded; in-memory) 로 같은 응답 재사용. 별도 named "judge" HttpClient (122B endpoint 재사용; 짧은 prompt + 1-token max_tokens). Borderline classifier 는 Phase 15 의 entropy/length 값에서 직접 도출 (별도 ML 없음). 새 trace 필드 `judge_called`/`judge_verdict`/`judge_latency_ms` 추가 (schema_version=1 유지). distillation 디자인의 "lazy verification" 단계 — Tier 4 self-improving classifier 를 위한 라벨 데이터 수집 부가 효과. `quality-check-improvement-options.md` Tier 3-A 구현.
- [ ] **Phase 17: QualityClassifier (distillation endgame)** — Tier 4. 별도의 ML.NET binary classifier (`models/quality-classifier.zip`) 학습 — `bge-m3 embed(prompt) ⊕ bge-m3 embed(response)` (2048-dim) → good/bad. 학습 데이터: TraceLog (`initial_response_excerpt`, `fallback_kind`, `final_response_excerpt`) + DecisionLog (`fallback_used`, `routing_reason`) + Phase 16 의 judge verdict 자동 추출. `Core/QualityClassifierPort.fs` BCL-only 포트 + `Adapters/MlNetQualityClassifier.fs` (`PredictionEnginePool watchForChanges:true`) + `Adapters/QualityClassifierTrainer.fs` (Phase 8 RetrainingService 인프라 재사용; LbfgsLogisticRegression; held-out validation; atomic File.Move). `QualityCheck.isBadResponse` 가 classifier prediction 호출로 대체됨 (인터페이스 동일). Bootstrap: sample 수 < 500 일 때 Phase 15+16 heuristic 으로 fallback (`IQualityClassifier` 두 구현 — `HeuristicQualityClassifier` + `MlNetQualityClassifier` — sample 수 따라 switch). Canary: `models/quality-classifier-canary.zip` 패턴 (Phase 9 canary infra 재사용). distillation 디자인의 closed-loop self-improvement 가 처음으로 완전 구현됨. `quality-check-improvement-options.md` Tier 4 구현.

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
- [x] 07-01-FOUNDATION-PLAN.md ✓ — Core RetrainingPorts.fs (BCL-only: 3 interfaces + 4 supporting types) + 3 Cli adapter stubs + .fsproj wiring (unblocks Wave 2 parallel)
- [x] 07-02-FAILURE-DETECTOR-PLAN.md ✓ — FailureDetector real impl (JSONL reader + fallback_used filter + empty-result Information log; malformed-line tolerant)
- [x] 07-03-TEACHER-LABELER-PLAN.md ✓ — TeacherLabeler real impl (named HttpClient bypass per Pitfall 5 + prompt template loader with double-checked-lock cache + persistent UTC daily cost cap + ROUTE_35B/ROUTE_122B parser; ROUTE_122B wins on collision)
- [x] 07-04-DATASET-WRITER-PLAN.md ✓ — HardCaseDatasetWriter real impl (Channel + BackgroundService mirror of DecisionLogWriter + BoundedChannelFullMode.Wait + (CorrelationId,PromptHash) HashSet dedupe seeded from existing JSONL)
- [x] 07-05-CLI-WIRING-PLAN.md ✓ — CompositionRoot DI triple-registration + named "teacher" HttpClient with AddResilienceHandler (5xx/transient retry, no 4xx) + Program.fs --retrain handler (offline pipeline before host startup) + appsettings.json + prompts/teacher-prompt.md + scripts/seed-hard-cases.fsx + .gitignore datasets/
- [x] 07-06-TESTS-PLAN.md ✓ — 3 test files (FailureDetectorTests 6 + TeacherLabelerTests 6 with fake Kestrel + HardCaseDatasetTests 4 with explicit BackgroundService lifecycle = 16 new tests) + Tests.fsproj wiring + RouterTests.rootTests; flushed two real adapter bugs (CapCounter STJ + Flush/Dispose F# parsing trap)

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
**Plans**: 3 plans

Plans:
- [x] 08-01-PLAN.md ✓ — DatasetMerger (70/30 + class-stratified + bootstrap-empty-old per Lock 3) + Retrainer (`MLContext -> IDataView -> string -> float32 -> ITransformer`, LbfgsLogisticRegression + atomic `File.Move(overwrite=true)`) + Validator (`fallback_rate := 1.0 - PositiveRecall` per Lock 1, accuracy + fallback gate, rejection log) + IModelVersionProvider port (BCL-only Core port)
- [x] 08-02-PLAN.md ✓ — RetrainingService BackgroundService (two PeriodicTimer race: IntervalMinutes sweep + CountCheckIntervalMinutes count-check; SemaphoreSlim(1,1).Wait(0) skip-if-busy per Lock 7; nested try/with isolation with ExceptionDispatchInfo.Capture for OperationCanceledException through task{} await points; mandatory pipeline order per Lock 5: split FIRST → train on split.TrainSet → evaluate baseline + candidate on split.TestSet; cumulative training-set persistence per Lock 3; router.zip.prev backup; Phase 9 forward-compat per Lock 11) + ModelVersionProvider adapter (mutable ref cell + Update) + ChatCompletions per-request IModelVersionProvider read + CompositionRoot 5 registrations (ModelVersionProvider double-reg + RetrainingService double-reg + Configure) + appsettings Retraining section (12 keys)
- [x] 08-03-PLAN.md ✓ — RetrainingTests.fs with 7 testCases: DatasetMerger 70/30 class-balance (RETRAIN-01) + bootstrap-empty-old (RETRAIN-01); RunNowAsync smoke + count-trigger StartAsync integration (RETRAIN-02 — two integration tests per ROADMAP success criteria #2); Validator accept + reject + rejection-log (RETRAIN-03); 3-request before/retrain/after model_version flip (RETRAIN-04); 2-concurrent-trigger CapturingSink Serilog skip-log assertion (RETRAIN-05); force-throw isolation Loop A unaffected (RETRAIN-06); +1 test reliability fix on Phase 7 HardCaseDatasetWriter graceful drain test (replaced Thread.Yield with Thread.Sleep(200) — flaked under 73-test ThreadPool contention)

### Phase 9: Canary Deployment
**Goal**: When a new model lands, route only a percentage of traffic (default 10%) to it for a configurable window before promoting to 100%. `Microsoft.FeatureManagement.AspNetCore` + `ContextualTargetingFilter` (NOT `PercentageFilter` — research found `PercentageFilter` is non-sticky/random per evaluation, so the same `correlation_id` would get different cohorts on different requests; `ContextualTargetingFilter` hashes SHA-256(correlation_id + featureName) deterministically) does the split based on `correlation_id` hash. Logs always tag `model_version` (`ml-{sha8}` baseline vs `ml-{sha8}-canary` canary) so cohort comparison via JSONL group-by is straightforward. Manual transitions via `/canary` admin endpoint (loopback-only, mirrors `/stats`): `/canary/promote` atomically moves `models/router-canary.zip` → `models/router.zip` under shared `IRetrainLock` (race-prevention with Phase 8 RetrainingService — promote returns 409 if retrain in progress); `/canary/rollback` sets in-memory percentage=0 (instant; no IConfiguration write needed). Auto-rollback: if canary's rolling 60s `fallback_rate` exceeds baseline's by >10% AND baseline sample size >= 50, canary is unloaded automatically and logged as `AUTO-ROLLBACK fired`. Auto-rollback is disabled by default (`Canary.AutoRollbackEnabled=false`) until Phase 10 ships the real `fallback_used` signal — until then the metric uses `routing_reason` suffix (`;upstream_error` / `;stream_error`) as a proxy. Operator opt-in via config flag once trustworthy.
**Depends on**: Phase 8
**Requirements**: CANARY-01, CANARY-02, CANARY-03
**Success Criteria** (what must be TRUE):
  1. With canary set to 10%, ~10% of requests go to the new model and ~90% to the previous; verified statistically over 1000 fake-upstream requests (binomial 95% CI test).
  2. `DecisionLog` records `model_version` distinguishing canary vs baseline; cohort comparison is trivial (group by `model_version`).
  3. A `/canary` admin endpoint (or config setting) promotes the canary to 100% OR rolls back; verified by integration test that exercises both transitions.
  4. Auto-rollback: if canary's rolling 60s `fallback_rate` exceeds baseline's by >10%, canary is unloaded automatically; logged. Verified by an integration test that injects fake "bad" responses for the canary and confirms rollback fires.
**Plans**: 3 plans

Plans:
- [x] 09-01-PLAN.md ✓ — Foundation: `RouterRequest.CorrelationId` + `RoutingDecision.ModelVersion` Core domain fields (19 enumerated construction sites updated); `SmartRouter.Core/CanaryPorts.fs` BCL-only `ICanaryGate` port; `IModelVersionProvider` extended (`CanaryVersion` getter + `UpdateCanary` setter); `ML.fs makeApplyML` 6-param factory (single `isCanary` boolean gates classifier selection AND `decision.ModelVersion` per Pitfall 8); `ChatCompletions` threads `correlationId` into `mapWireToRequest` and cascades `decision.ModelVersion → versionProvider.CurrentVersion`; appsettings `Canary` + `feature_management` sections; `Microsoft.FeatureManagement.AspNetCore 4.5.0` NuGet pinned to Cli only (ARCH-01 preserved). Build clean, 73 + 10 baseline preserved.
- [x] 09-02-PLAN.md ✓ — Implementation: 7 new Cli adapters (CanaryTargetingAccessor / CanaryState / CanaryGate / CanaryMetrics + NoOpCanaryMetrics / CanaryWatchdog / RetrainLock / CanaryService with FileSystemWatcher) + Endpoints/Canary.fs (GET status / POST promote / POST rollback / POST enable; loopback only); `MlNetClassifier` parameterized on modelName via AddKeyedSingleton; PredictionEnginePool dual-classifier registration; CompositionRoot: Step 1.0 unconditional `TryAddSingleton<ICanaryGate>(NullCanaryGate)` + `TryAddSingleton<ICanaryMetrics>(NoOpCanaryMetrics)` BEFORE the ml block, Step 1.2 conditional `AddSingleton` overrides + `WithTargeting<CanaryTargetingContextAccessor>()` (last-registration-wins); CanaryService triple-reg (concrete + ICanaryService + AddHostedService); RetrainingService refactored to share `IRetrainLock` for /canary/promote coordination; ChatCompletions records `metrics.Record(isCanary, isFallback)` post-response. PercentageFilter explicitly forbidden (non-sticky). Build clean, 73 + 10 baseline preserved.
- [x] 09-03-PLAN.md ✓ — CanaryTests.fs (5 unit testCase + 7 mlIntegTest integration = 12 tests; testSequenced): CANARY-01 statistical split with `mkStableCorrelationIds` (Random(seed=42) → 1000 deterministic Guids; binomial 95% CI [80,120]) + sticky bucket + 3 short-circuit guards (empty correlation_id / missing canary file / 0% percentage); CANARY-02 cohort tagging via `JsonDocument.Parse` + `GetProperty("model_version")` + `EndsWith("-canary")` (NOT string-Contains); CANARY-03 manual rollback / enable / promote-success / promote-no-canary endpoint integration; CANARY-03 auto-rollback fires via injected metrics + `CapturingSink ILogEventSink` for "AUTO-ROLLBACK" log assertion; CANARY-03 auto-rollback disabled by default (Lock 1); CANARY-04 `canary04_fileSystemWatcher` (CanaryModelExists=false boot + 200ms settle + post-startup file write + 20×100ms poll for -canary suffix + delete + clear assertion — verifies CONTEXT.md Lock 9). Result: 78 pass + 17 ignored, 0 failed (without embeddings) / 85 + 10 (with embeddings).

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
**Plans**: 3 plans

Plans:
- [x] 10-01-PLAN.md ✓ — Foundation: `RoutingReason.FallbackTo35B` DU case + exhaustive `formatReason` cascade in DecisionLogger.fs (`"fallback_to_35b"`); `IHealthProbe` Core port extended with sync `IsReachable: ModelTarget -> bool` + `LastProbedAt: ModelTarget -> DateTimeOffset` (BCL-only, ARCH-01 safe); appsettings `Routing.Health` section (PollingIntervalSeconds=10, ConsecutiveFailureThreshold=1) + flip `Canary.AutoRollbackEnabled: false → true` (Phase 9 was held back until Phase 10's signal becomes real); MLRoutingTests.fs / RoutingTests.fs read-only confirmation passed (catch-all `| r ->` arms guard the cascade — no edits needed). Build clean, 78 + 17 baseline preserved.
- [x] 10-02-PLAN.md ✓ — Implementation (4 tasks): Task 1 — `HealthService.fs` BackgroundService (PeriodicTimer probes `/v1/models` per upstream every 10s; ConcurrentDictionary state with per-target consecutive-failure counter; ExceptionDispatchInfo.Capture for OperationCanceledException through task{} await points) + `Endpoints/Health.fs` GET /health (HTTP 200; JSON shape with reachable + last_probed_at "never" if MinValue; loopback-only). Task 2a — CompositionRoot DI: 5 named HttpClients via `.ConfigureHttpClient(...)` chain form (forbidden 2-arg `AddHttpClient(name, fun c -> ...)` form replaced — F# overload-binding pitfall); upstream35b/122b have AddResilienceHandler retry; -stream variants and health-probe have NO retry; HealthService triple-reg; QueueDispatcher 3-arg DI. Task 2b — QueueDispatcher fallback policy in BOTH CompleteAsync (return Error) AND StreamAsync (taskSeq yield Error + implicit terminate); QwenUpstreamClient.resolveProbe extends to 4-way (target × stream) client-name map; ChatCompletions Option B early-return + shadow-rebind (NOT wrap-in-else — high-risk indentation refactor avoided); 5+ existing decisionLogger.Log calls auto-pickup `decision.IsFallback=true`; Program.fs registers /health endpoint. Task 3 — 11 test construction sites updated for QueueDispatcher 3-arg signature. Build clean, 78+17 baseline preserved.
- [x] 10-03-PLAN.md ✓ — `HealthFallbackTests.fs` with 5 integration tests (testSequenced + temp-dir + fake-Kestrel pattern): HLTH-04 (122B-down + reasoning task → 35B serves it; DecisionLog JSONL `fallback_used=true` parsed via JsonDocument.Parse, NOT string-Contains); HLTH-05 (122B-down + graph_indexing → HTTP 503 + structured `{error: {type: "model_unavailable"}}` body, NOT model output); HLTH-06 (transient retry — fake 502 then 200; succeeds with elapsed ≥400ms proving Polly retry-delay jitter); HLTH-07 (streaming no-retry — Counter==1, separate -stream client without resilience handler); HLTH-08 (/health JSON shape — qwen35b/qwen122b reachable + last_probed_at). Tests.fsproj + RouterTests.rootTests wired. Result: **83 pass + 17 ignored, 0 failed** (without embeddings) / 90+10 (with embeddings).

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
**Plans**: 3 plans

Plans:
- [ ] 11-01-MODELS-ENDPOINT-PLAN.md — GET /v1/models endpoint (parallel fetch + IHealthProbe gating + JsonElement.Clone() lifetime guard + dedupe by id) + 3 integration tests; reuses existing health-probe named HttpClient (no new DI)
- [ ] 11-02-LAUNCHD-OPS-PLAN.md — deploy/com.ohama.smart-router.plist (mirrors qwen36-35b convention exactly) + scripts/deploy.sh (framework-dependent dotnet publish to ~/llm-system/services/smart-router/) + scripts/install-launchd.sh (copies plist; prints manual UAT steps)
- [ ] 11-03-README-PLAN.md — README.md at repo root (~800 lines, 13 sections covering routing pipeline, ML feedback loops, configuration, all 8 endpoints, DecisionLog schema, Hermes + Graphify integration, canary workflow, launchd setup, troubleshooting)

### Phase 15: Quality Signal Enrichment
**Goal**: `isBadResponse` 가 모델이 이미 보내주는 신호 (`finish_reason`) 와 더 견고한 휴리스틱 (case-insensitive keywords, refusal 패턴, 한글 응답 길이 보정, Shannon entropy 기반 반복 감지) 을 활용한다. False negative (반복 루프, 잘린 응답, refusal) 가 큰 폭으로 감소; false positive (정상 답 오판) 도 약간 감소. Phase 14 의 QF-01/QF-02 테스트는 그대로 통과 (backward-compat).
**Depends on**: Phase 14
**Requirements**: QSE-01, QSE-02, QSE-03, QSE-04, QSE-05, QSE-06
**Success Criteria** (what must be TRUE):
  1. 35B 응답이 `finish_reason="length"` 또는 `"content_filter"` 일 때 `isBadResponse` 가 true 반환 — 길이/키워드 통과해도 잘림은 bad. `Routing.QualityFallback.BadFinishReasons` config (default `["length", "content_filter"]`).
  2. BadKeywords 매칭이 case-insensitive — operator 가 `"TODO"` 한 번 추가하면 `"todo"`, `"Todo"`, `"ToDo"` 모두 cover. 변경 검증: 기존 default `["TODO", "I think"]` 가 `"todo"` 포함 응답도 잡음.
  3. Default BadKeywords 가 refusal 패턴 포함 — `["TODO", "I think", "I cannot", "I'm unable", "I don't have access", "As an AI", "Sorry, I can't"]`. 35B 의 흔한 refusal 시나리오 (32 chars 32+ 충분 길이지만 "I cannot help with this") 가 bad 로 잡힘.
  4. 한글 비율 비례 길이 보정 — 응답의 한글 ratio (Hangul 코드포인트 `[가-힣]` 비율) 이 높으면 effective length 가 `length × (1 + ratio × 0.8)` 로 증가. 한글 28 chars (effective ~50) 는 `MinResponseLength=30` 통과.
  5. Shannon entropy 반복 감지 — `charEntropy(response) < EntropyThreshold` (default 2.5) 일 때 bad. `"the the the..."` 류 token 루프 (정상 텍스트 entropy 4-5+, 루프는 1-2) 잡음. `Routing.QualityFallback.EntropyThreshold` config (default 2.5).
  6. Phase 14 의 QF-01 (good 35B response) + QF-02 (TODO trigger fallback) 테스트가 그대로 통과 — signature/behavior backward-compat. 신규 QSE-* 테스트가 finish_reason / case-insensitive / refusal / Korean / entropy 5 차원을 cover.
**Plans**: 3 plans (예상)

Plans:
- [ ] 15-01-CONFIG-AND-DOMAIN-PLAN.md — `QualityFallbackOptions` 확장 (`BadFinishReasons: string array` + `EntropyThreshold: float`); `appsettings.json:Routing.QualityFallback` 신규 키; default BadKeywords 확장 (refusal 패턴 7 개); CompositionRoot Configure binding 변경
- [ ] 15-02-ISBADRESPONSE-IMPL-PLAN.md — `isBadResponse` 시그니처 확장 (현재: `opts -> response -> bool`; 신규: `opts -> finishReason: string option -> response -> bool`); 5 개 신규 검사 (finish_reason, case-insensitive, refusal default, Korean length, entropy); `ChatCompletions.fs` non-streaming branch 가 mlx_lm JSON 응답에서 `finish_reason` 추출 후 전달
- [ ] 15-03-TESTS-AND-DOCS-PLAN.md — `QualitySignalEnrichmentTests.fs` (QSE-01..06 6 testCase + 회귀 테스트로 QF-01/QF-02 재실행); README §5.5 quality fallback trigger conditions 확장 + §7 `Routing.QualityFallback` 표에 BadFinishReasons/EntropyThreshold 추가; planning docs 업데이트

### Phase 16: 122B-as-Judge for Borderline Cases
**Goal**: Phase 15 heuristic 통과했지만 quality 가 borderline 한 케이스 — entropy/length/keyword band edge — 에 대해서만 122B 에 1-token verification call ("Is this response good for the question? YES/NO") 을 보낸다. 명백한 good/bad 는 fast path (judge 안 부름) 유지. Cache by `(prompt_hash + response_hash)` 로 같은 응답 검증 재사용. distillation 디자인의 lazy verification 단계 — Phase 17 self-improving classifier 의 학습 데이터 수집 부가 효과.
**Depends on**: Phase 15
**Requirements**: JDG-01, JDG-02, JDG-03, JDG-04, JDG-05
**Success Criteria** (what must be TRUE):
  1. Borderline classifier 가 명백한 good (entropy 4+, length 100+, no keywords) / 명백한 bad (Phase 15 검사 fail) / borderline (band edge) 를 구분 — 단위 테스트로 3 클래스 분류 검증.
  2. Borderline 일 때만 judge call — 명백한 good/bad 케이스에서는 judge HttpClient 호출 횟수 = 0 (fake-Kestrel counter 로 검증).
  3. Judge cache hit — 같은 (prompt, response) 쌍에 대해 두 번째 호출은 cache hit (judge HttpClient counter +0); /stats 에 `judge_cache_hits`, `judge_cache_misses` 노출.
  4. Judge call latency p95 < 100ms (1-token 응답; 실제 122B normal call 의 ~50× 빠름) — 통합 테스트에서 fake-Kestrel 으로 측정.
  5. Trace JSONL 신규 필드 `judge_called`, `judge_verdict` (`"yes"|"no"|null`), `judge_latency_ms` 추가; 운영자가 borderline 케이스 비율 모니터링 가능. schema_version 유지 (필드 추가만; rename/remove 안 함).
**Plans**: 4 plans (예상)

Plans:
- [ ] 16-01-BORDERLINE-CLASSIFIER-PLAN.md — `Adapters/BorderlineClassifier.fs` (pure F#, BCL only): Phase 15 의 entropy/length/keyword band edge 임계값 (예: entropy 2.5..3.5, length 30..60) 기반 3 클래스 분류; `Verdict = Good | Bad | Borderline of reason: string`
- [ ] 16-02-JUDGE-ADAPTER-PLAN.md — `Adapters/JudgeClient.fs` (IJudgeClient 포트 + named "judge" HttpClient + prompt template `prompts/judge-prompt.md` + 1-token max_tokens + ROUTE_YES/ROUTE_NO parser); LRU cache (`(prompt_hash, response_hash) → verdict`; bounded ~10000 entries; in-memory `ConcurrentDictionary` + LRU eviction); cache stats counter for /stats
- [ ] 16-03-CHATCOMPLETIONS-WIRING-PLAN.md — `ChatCompletions.fs` non-streaming branch: 35B 응답 → BorderlineClassifier 분류 → Borderline 일 때 JudgeClient.Verdict → No 면 122B 로 fallback; trace 에 judge 필드 emit; null-safe `GetService<IJudgeClient>` (judge 비활성 시 borderline = good 으로 처리; backward-compat)
- [ ] 16-04-TESTS-AND-DOCS-PLAN.md — `JudgeIntegrationTests.fs` (JDG-01..05 5 testCase, fake-Kestrel + cache hit/miss assertions + latency 측정); README §5.5 judge step 흐름 추가 + §7 `Routing.Judge` 신규 config 표 + §9.3 trace schema 에 judge 필드 3 개 추가; planning docs 업데이트

### Phase 17: QualityClassifier (distillation endgame)
**Goal**: 별도의 ML.NET binary classifier (`models/quality-classifier.zip`) 학습 — `(bge-m3 embed(prompt) ⊕ bge-m3 embed(response))` 2048-dim → good/bad. 학습 데이터는 TraceLog + DecisionLog + Phase 16 의 judge verdict 로부터 자동 추출. `QualityCheck.isBadResponse` 가 classifier prediction 호출로 대체됨 (인터페이스 동일; bootstrap fallback 으로 sample < 500 일 때 Phase 15+16 heuristic 사용). Phase 8 RetrainingService 인프라 + Phase 9 canary 패턴 재사용. distillation 디자인의 closed-loop self-improvement 가 처음으로 완전 구현됨.
**Depends on**: Phase 16
**Requirements**: QCLS-01, QCLS-02, QCLS-03, QCLS-04, QCLS-05, QCLS-06, QCLS-07
**Success Criteria** (what must be TRUE):
  1. `Core/QualityClassifierPort.fs` BCL-only 포트 (`IQualityClassifier { IsBad : prompt: string * response: string -> bool }`); ARCH-01 보존 (Microsoft.ML 참조 없음).
  2. `Adapters/MlNetQualityClassifier.fs` 가 `PredictionEnginePool` (`watchForChanges:true`) 으로 `models/quality-classifier.zip` 로드; 2048-dim concat embedding 입력 → binary good/bad 출력; in-flight 요청은 swap 시점에 이전 모델로 완료.
  3. `Adapters/QualityDatasetExtractor.fs` 가 `logs/trace/*.jsonl` + `logs/decisions/*.jsonl` 에서 라벨 추출: `fallback_kind="quality"` AND `final_target=Qwen122B` AND `final≠initial` → bad (positive); `fallback_kind=null` AND fallback_used=false → good (negative); Phase 16 judge verdict NO → bad (강한 신호); 이 로직 단위 테스트로 검증.
  4. `Adapters/QualityClassifierTrainer.fs` — Phase 8 `RetrainingService` 패턴 미러: 70/30 blend with held-out validation; `LbfgsLogisticRegression`; atomic `File.Move(overwrite=true)`; rejection log to `logs/quality-classifier-rejections.jsonl` if held-out fallback rate ≥ baseline. `RetrainingService` 와 별도 `BackgroundService` (`QualityClassifierRetrainService`) 로 독립 운영.
  5. Bootstrap fallback — `IQualityClassifier` 두 구현: `HeuristicQualityClassifier` (Phase 15+16 wrapping) + `MlNetQualityClassifier`. CompositionRoot 가 sample 수 (`datasets/quality-cases.jsonl` row count) < 500 일 때 Heuristic 등록, ≥ 500 일 때 MlNet 등록 (재시작 후 swap; in-process 동적 swap 안 함).
  6. Canary — `models/quality-classifier-canary.zip` FileSystemWatcher (Phase 9 패턴 미러); 10% 코호트가 canary classifier 사용; rolling-60s 비교로 auto-rollback (`Canary.QualityClassifier.AutoRollbackEnabled` 별도 키; default false 까지 검증 끝날 때까지).
  7. `QualityCheck.isBadResponse` 호출자 (`ChatCompletions.fs` 의 quality fallback path) 변경 최소 — `IQualityClassifier.IsBad(prompt, response)` 한 줄 호출로 대체; Phase 14 의 QF-01/QF-02 + Phase 15 의 QSE-01..06 + Phase 16 의 JDG-01..05 모두 backward-compat 통과 (heuristic fallback 경로로).
**Plans**: 5 plans (예상)

Plans:
- [ ] 17-01-CORE-PORT-AND-DATASET-PLAN.md — `Core/QualityClassifierPort.fs` BCL-only 포트; `Adapters/QualityDatasetExtractor.fs` (trace + decision JSONL → labeled rows); `datasets/quality-cases.jsonl` schema; 단위 테스트로 추출 로직 검증
- [ ] 17-02-CLASSIFIER-AND-TRAINER-PLAN.md — `Adapters/MlNetQualityClassifier.fs` (PredictionEnginePool); `Adapters/QualityClassifierTrainer.fs` (LbfgsLogisticRegression + Validator + atomic File.Move); `Adapters/QualityClassifierRetrainService.fs` (BackgroundService + PeriodicTimer; Phase 8 패턴 미러)
- [ ] 17-03-BOOTSTRAP-AND-WIRING-PLAN.md — `Adapters/HeuristicQualityClassifier.fs` (Phase 15+16 wrapping; `IQualityClassifier` 구현); CompositionRoot 의 sample-count-based switch (startup 시 1회 결정); `ChatCompletions.fs` 가 `IQualityClassifier.IsBad` 호출 (현재 `isBadResponse` 직접 호출 대체)
- [ ] 17-04-CANARY-PLAN.md — `models/quality-classifier-canary.zip` FileSystemWatcher; Phase 9 `CanaryService`/`CanaryGate`/`CanaryWatchdog` 인프라 재사용 (별도 키 prefix `Canary.QualityClassifier.*`); 10% sticky cohort by `correlation_id` (Phase 9 와 같은 sticky bucketing 재사용 가능 여부 분석 필요 — 두 canary 가 독립이어야 함)
- [ ] 17-05-TESTS-AND-DOCS-PLAN.md — `QualityClassifierTests.fs` (QCLS-01..07 7 testCase + bootstrap heuristic-fallback 회귀); README §5.5 quality fallback path 흐름 업데이트 (heuristic → judge → classifier 3 단계) + §6 ML Feedback Loop 에 quality classifier retraining 섹션 추가 + §11 Operations 에 quality classifier canary workflow 추가; planning docs 업데이트

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → **(ML arc)** 4 → 5 → 6 → 7 → 8 → 9 → **(deferred heuristic-cleanup arc)** 10 → 11 → **(post-v1)** 12 → 13 → 14 → **(quality enrichment arc)** 15 → 16 → 17

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation | 3/3 | ✓ Complete | 2026-05-07 |
| 2. SSE Streaming Pass-Through | 2/2 | ✓ Complete | 2026-05-08 |
| 3. 122B Concurrency Gate | 3/3 | ✓ Complete | 2026-05-08 |
| 4. ML Algorithm Seam | 3/3 | ✓ Complete | 2026-05-08 |
| 5. Routing-Decision Logging | 3/3 | ✓ Complete | 2026-05-08 |
| 6. Real ML Routing | 3/3 | ✓ Complete | 2026-05-08 |
| 7. Failure Detection + Teacher Labeling | 6/6 | ✓ Complete | 2026-05-08 |
| 8. Retraining Loop | 3/3 | ✓ Complete | 2026-05-08 |
| 9. Canary Deployment | 3/3 | ✓ Complete | 2026-05-09 |
| 10. Health + Fallback + graph_indexing No-Fallback | 3/3 | ✓ Complete (was Phase 4) | 2026-05-09 |
| 11. Deployment + Documentation | 3/3 | ✓ Complete (was Phase 6) | 2026-05-09 |
| 12. Heuristic Routing Removal | 6/6 + gap-closure | ✓ Complete | 2026-05-09 |
| 13. Service Logging | 6/6 | ✓ Complete | 2026-05-09 |
| 14. Quality Fallback + Trace Infrastructure | 6/6 | ✓ Complete | 2026-05-10 |
| 15. Quality Signal Enrichment | 0/3 | 📋 Planned | — |
| 16. 122B-as-Judge for Borderline Cases | 0/4 | 📋 Planned | — |
| 17. QualityClassifier (distillation endgame) | 0/5 | 📋 Planned | — |
