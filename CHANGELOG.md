# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-05-10

First production-ready release. Smart-router routes OpenAI-compatible chat completion
requests between Qwen 35B (latency-focused) and Qwen 122B (quality-focused) running
locally as `mlx_lm.server` instances, using a 3-stage pipeline: explicit model override
→ task table → ML classifier (bge-m3 int8 embeddings + ML.NET LbfgsLogisticRegression).
Includes auto-retraining loop, canary deployment, fallback to 35B on 122B downtime,
and operational logging with rolling files + retention.

### Added

#### Routing pipeline (Phases 1-4)
- Hexagonal F# / .NET 10 architecture; `SmartRouter.Core` is BCL-only (ARCH-01)
- 3-stage routing pipeline: model override → task table → algorithm
- 7 task types: `graph_indexing`, `compiler_debug`, `architecture_analysis`,
  `dependency_analysis`, `reasoning`, `retrieval`, `summary`
- `RoutingAlgorithm` function-type alias seam allowing algorithm swap

#### SSE streaming (Phase 2)
- Pass-through SSE chunks via `taskSeq` + `HttpCompletionOption.ResponseHeadersRead`
- Strategy D `[DONE]` injection if upstream omits it
- Mid-stream cancellation aborts upstream within one chunk interval
- Per-chunk `FlushAsync`; Content-Type `text/event-stream`

#### 122B concurrency gate (Phase 3)
- `SemaphoreSlim(1)` cap on concurrent 122B requests
- Two-level priority queue (high/low) with fairness counter K=10
- Sub-pattern A: dispatcher acquires semaphore before signalling TCS
- Linked CTS timeout starts at slot grant, not at queue enqueue
- 35B requests bypass the queue
- `GET /stats` endpoint with snake_case JSON wire shape

#### ML routing (Phases 4-6)
- bge-m3 int8 dynamic-quantized ONNX (~542 MB, 1024-dim, multilingual) for
  Korean+English mixed traffic; chosen over bge-small for Hangul tokenizer support
- ML.NET `LbfgsLogisticRegression` classifier loaded via `PredictionEnginePool`
  with `watchForChanges:true` for hot-swap on retraining
- First-run bootstrap via `ModelBootstrapper.ensureDummyModel` — 200-sample random
  weights so cold-start never throws
- `Routing.ML.Threshold` config tunable; default 0.5
- `model_version` = SHA-256 first 8 hex of `models/router.zip`

#### Decision logging (Phase 5)
- 12-field JSONL schema at `logs/decisions/YYYY-MM-DD.jsonl`:
  `schema_version`, `correlation_id`, `prompt_hash`, `prompt_korean_char_ratio`,
  `routing_algorithm`, `routing_reason`, `target`, `latency_ms`, `fallback_used`,
  `model_version`, `task_type`, `timestamp`
- `DecisionLogWriter : BackgroundService` Channel + single-writer pattern
- `CorrelationMiddleware` injects 32-hex correlation_id per request

#### Failure detection + teacher labeling (Phase 7)
- `FailureDetector` reads `logs/decisions/*.jsonl` and filters `fallback_used=true`
- `TeacherLabeler` calls 122B with prompt template; 30s timeout + 3x retry +
  daily cost cap; named HttpClient bypassing QueueDispatcher
- `HardCaseDatasetWriter` writes labeled samples to `datasets/hard-cases.jsonl`
  (Channel + BackgroundService; `BoundedChannelFullMode.Wait`)
- `--retrain` CLI command for manual operator-driven offline pipeline
- `prompts/teacher-prompt.md` template (configurable path)

#### Auto-retraining loop (Phase 8)
- `RetrainingService : BackgroundService` with two `PeriodicTimer` loops:
  daily timer + count-threshold check
- Pipeline: TrainTestSplit → train on TrainSet → evaluate baseline + candidate on
  TestSet → atomic `File.Move(overwrite=true)` if `fallback_rate <= baseline`
- 70/30 dataset blend with class stratification; first-retrain bootstrap
- `IModelVersionProvider` BCL-only Core port for live `model_version` updates
- `SemaphoreSlim(1,1).Wait(0)` skip-if-busy idempotency
- Rejected candidate models logged to `logs/retraining-rejections.jsonl`

#### Canary deployment (Phase 9)
- `Microsoft.FeatureManagement.AspNetCore` with `ContextualTargetingFilter`
  (sticky 10/90 split keyed on `correlation_id`)
- `RoutingDecision.ModelVersion` cohort label: `ml-{sha8}` baseline vs
  `ml-{sha8}-canary` canary
- Rolling-60s fallback-rate watchdog with auto-rollback when delta exceeds 10%
  (`AutoRollbackEnabled` flag; default false in Phase 9, flipped true in Phase 10)
- `CanaryService` `IHostedService` with `FileSystemWatcher` on
  `models/router-canary.zip`
- `/canary` admin endpoint: GET status, POST promote/rollback/enable
- `IRetrainLock` shared between RetrainingService and CanaryService for
  promote-vs-retrain race prevention

#### Health + fallback + graph_indexing no-fallback (Phase 10)
- `HealthService : BackgroundService` probes `/v1/models` per upstream every 10s;
  `ConcurrentDictionary` state with `ConsecutiveFailureThreshold`
- `IHealthProbe` Core port with `IsReachable` (sync) + `LastProbedAt`
- 5 named `HttpClient` instances via `.ConfigureHttpClient(...)` chain
  (with/without retry resilience handler; -stream variants exclude retry —
  partial SSE output cannot be replayed)
- `GET /health` endpoint: per-upstream reachability + last-probed-at
- Fallback policy: 122B unreachable → shadow-rebind to 35B with
  `Reason=FallbackTo35B` and `IsFallback=true`; `graph_indexing` task → HTTP 503
  (REL-04 — never silently downgrade)
- ML feedback loop now self-sustaining: real upstream failures → fallback →
  `fallback_used=true` → FailureDetector → TeacherLabeler → RetrainingService →
  ModelVersionProvider.Update

#### Deployment + documentation (Phase 11)
- `GET /v1/models` endpoint: parallel upstream fetch via `Task.WhenAll` +
  `IHealthProbe` gating + `JsonElement.Clone()` lifetime guard + dedupe by id
  (first-seen wins); both upstreams down → HTTP 200 + empty data array
- `deploy/com.ohama.smart-router.plist` launchd LaunchAgent
  (KeepAlive=`<true/>`, ThrottleInterval=30, RunAtLoad=`<true/>`,
  absolute dotnet path `/opt/homebrew/bin/dotnet`)
- `scripts/deploy.sh` (framework-dependent `dotnet publish -c Release`) +
  `scripts/install-launchd.sh` (manual `launchctl load -w` documented)
- 1097-line repo-root `README.md`: 14 sections covering all 8 endpoints, 7 task
  types, Loop A/B, canary workflow, launchd procedure, troubleshooting

#### Service logging (Phase 13)
- Dual sink: Serilog Console (stderr; OBS-04 invariant) + rolling File at
  `logs/operational/smart-router-{Date}.log` (50 MB cap, 30-file retention,
  2-second flush, FileShare.None)
- New output template: `{Timestamp:ISO-8601} [{Level:u3}] {SourceContext}
  [{correlation_id}] {Message:lj}{NewLine}{Exception}` with `[-]` default for
  non-request scope
- `appsettings.json:Serilog.MinimumLevel.Override` activated (suppresses
  `Microsoft.AspNetCore` middleware noise to Warning)
- 12 type-based adapters migrated to `ILogger<T>` constructor injection;
  4 module-based files (Validator/DatasetMerger/Retrainer/ModelBootstrapper)
  use `ILogger` function parameter; `ChatCompletions` endpoint uses
  `ILoggerFactory.CreateLogger("ChatCompletions")`
- `--log-level=enum` CLI flag (verbose|debug|information|warning|error|fatal +
  short aliases) replaces legacy `--trace`
- Hot-path log demotion: `ChatCompletions` `Routing target=...` from INFO to
  DEBUG (cuts ~50 MB/day stderr at 100 req/min default)
- `HealthService` transition-only INFO emissions; 4 endpoint files emit
  one DEBUG per hit
- Startup banner (port, model.version, canary state, queue config, teacher
  cap, log dir) + shutdown banner via `IHostApplicationLifetime.ApplicationStopping`
- `LogRetentionService : BackgroundService` prunes operational logs > 30 days,
  decision JSONL > 90 days, teacher-cap > 7 days; PeriodicTimer 60-min interval

#### Operational tooling (Issues #3, #6, #7, #10)
- `--port=N` CLI flag override (1024..65535) — bypass `appsettings.json`
  `Kestrel:Endpoints:Http:Url`
- `X-Correlation-Id` response header on every request (registered via
  `Response.OnStarting` so it works for both buffered and SSE streaming responses)
- `/healthz` alias for `/health` (Kubernetes/cloud probe convention)
- `/stats` extended with `baseline_model_version`, `canary_model_version`,
  `canary_percent`, `canary_active` — single-endpoint scrape for monitoring
- `--cold-start` CLI flag (Phase 14 prep — implementation deferred)

### Changed

- `appsettings.json:Serilog` activated via `.ReadFrom.Configuration(...)` —
  was previously dead config (Phase 13)
- DecisionLog `routing_reason` field gains `fallback_to_35b` value (Phase 10)
- `RoutingAlgorithm` is the only stage-3 algorithm; `Routing.Algorithm` config
  key removed entirely (Phase 12; was previously `"heuristic" | "ml"`)
- `--routing-algorithm` CLI flag removed entirely (Phase 12; ML is the only path)
- `configureServices` split into `configureRequestPipeline` (full ML wiring) +
  `configureWithoutMl` (offline retrain + tests; no ML model file checks)
  (Phase 12)
- README §3 augmented with Prerequisites + First-time setup section calling
  out ML embedding file requirement (Issue #4)

### Fixed

- **Issue #1**: FS3511 F# state-machine fallback warning unblocking Release
  build via `<NoWarn>` on `SmartRouter.Cli.fsproj` (commit `7e85e8d`)
- **Issue #2**: HTTP 500 on every `/v1/chat/completions` due to
  `IVariantFeatureManager` (Scoped) being captured from root provider in
  `ICanaryGate` (Singleton) factory; fix uses `IServiceScopeFactory` with
  per-call `CreateScope()` (commit `95e9c06`)
- **Issue #5**: `scripts/download-models.sh` migrated from deprecated
  `huggingface-cli` to `hf` CLI; corrected source path to
  `onnx/model_int8.onnx` (commit `f0f90e3`)
- **Issue #8**: Production DI graph integration tests added
  (`ProductionDiTests.fs`) using `BuildServiceProvider(ServiceProviderOptions(
  ValidateScopes=true))` to catch scoped-from-singleton regressions in unit
  tests (commit `fac58b2`)
- **Issue #9**: Model file path resolution now CWD-independent — tries
  `Path.GetFullPath(configPath)` → `AppContext.BaseDirectory + path` →
  5-level walk-up search; eliminates the `dotnet run` symlink workaround
  (commit `a4195f5`)
- **Issue #11**: ASP.NET Core `Microsoft.AspNetCore.*` middleware INFO chatter
  silenced via `Serilog.MinimumLevel.Override` table (Phase 13-01 commit
  `4f04c88`)
- **Issue #12**: `model_version` stale post-retrain because
  `ML.makeApplyML` captured strings at DI factory time. Fix takes
  `IModelVersionProvider` and reads `CurrentVersion`/`CanaryVersion` live per
  call so `RetrainingService.Update` flows through to next routing decision
  (commit `debb94a`)
- ModelsTests `IEmbedder` resolution gap from Phase 12 split; migrated to
  `configureWithoutMl` + `IHealthProbe` stub (commit `748bb79`)
- HardCaseDatasetTests graceful-drain flake stabilized via
  `Thread.Sleep(200)` instead of `Thread.Yield()`; root cause was Phase 8
  ThreadPool contention exposing pre-existing Phase 7 flake
  (commit `dd1da7d`)

### Removed

- **Phase 12 — Heuristic Routing Removal**: `src/SmartRouter.Core/Heuristic.fs`
  deleted; `RoutingReason.Heuristic of score` DU case removed;
  `RoutingConfig.Keywords` + `RoutingConfig.ComplexityThreshold` fields removed;
  `Routing.Algorithm` config key removed; `--routing-algorithm` CLI flag
  removed; `RoutingTests.fs` deleted entirely; 3 `MLRoutingTests` heuristic-
  related tests pruned; `scripts/check-routing-isolation.sh` deleted
- Historical snapshot at git branch `archive/heuristic-baseline` and tag
  `v0.5-heuristic-baseline` (preserved untouched per Phase 12 Q7)

### Test coverage

- 80 passed + 16 ignored + 0 failed (without ML embedding files)
- 87 passed + 9 ignored (with ML embedding files present)
- New test modules: `RoutingTests`, `StreamingTests`, `QueueTests`, `LoadTests`,
  `MLRoutingTests`, `MLEmbeddingTests`, `MLClassifierTests`, `LoggingTests`,
  `FailureDetectorTests`, `TeacherLabelerTests`, `HardCaseDatasetTests`,
  `RetrainingTests`, `CanaryTests`, `HealthFallbackTests`, `ModelsTests`,
  `LogRotationTests`, `ProductionDiTests`, `MLLiveVersionTests`

### Architectural invariants preserved

- **ARCH-01**: `SmartRouter.Core` is BCL-only — zero references to Serilog,
  HttpClient, ASP.NET Core, Microsoft.ML, FSharp.SystemTextJson; CI grep
  enforces
- **ARCH-02**: F# `task {}` only; no `async {}` literals
- **OBS-04**: Serilog → stderr only; stdout reserved for application output
- **PITFALL-26**: Expecto `rootTests` explicit list pattern; auto-discovery
  forbidden
- All test modules wrapped in `testSequenced` for Console.SetOut + temp dir
  hygiene

## [v0.5-heuristic-baseline] - 2026-05-08

Soft-pause snapshot of the heuristic routing implementation (commit `a4cfce1`).
Preserved on `archive/heuristic-baseline` branch as historical reference. ML
became the sole routing path in Phase 12.
