# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- **Quality fallback (35B → 122B retry) was broken for real model
  responses.** The `isBadResponse` heuristic was checking the raw
  OpenAI-compatible JSON envelope (which is always longer than 30
  characters and rarely contains literal `"TODO"` / `"I think"`),
  not the inner `choices[0].message.content`. As a result,
  length-based fallback never fired in production, and keyword-based
  fallback only fired when the model's content happened to embed the
  keyword string. Now parses the response and applies the heuristic
  to `message.content` directly. Malformed responses degrade safely
  (treated as empty content → fallback fires). (#13)

## [1.1.0] - 2026-05-10

Quality fallback release. The router now retries on 122B when 35B's
response is low-quality (non-streaming only), captures both responses in
an opt-in trace log for end-to-end debugging, and ships a cold-start
recovery path for corrupted models. README rewritten for operator focus.

### Added

#### Quality fallback (35B → 122B retry)
- Non-streaming requests routed to 35B are auto-retried on 122B when the
  35B response fails a quality heuristic (length below threshold, or a
  configurable bad-keyword match like `"TODO"` or `"I think"`). The
  client sees only the 122B response — the bad 35B response never leaks
- DecisionLog: `routing_reason="fallback_to_122b"` with `fallback_used=true`
- Streaming requests are intentionally exempt (chunks already shipped)
- Kill switch: `Routing.QualityFallback.Enabled=false`

#### Trace log (end-to-end debugging)
- `--trace-responses` CLI flag enables a per-request JSONL log at
  `logs/trace/YYYY-MM-DD.jsonl` (off by default; opt-in only)
- Each row joins by `prompt_uid` (first 12 hex of `prompt_hash`) and
  carries: `initial_target`, `initial_response_excerpt`, `fallback_kind`
  (`"quality"` | `"availability"` | null), `final_target`, `final_response_excerpt`
- Operator workflow: `jq 'select(.prompt_uid == "...")'` to see exactly
  what 35B said, why fallback fired (or didn't), and what 122B said

#### Cold-start recovery
- `--cold-start` CLI flag backs up `models/router.zip` and `datasets/*`
  with a timestamp suffix, then regenerates a fresh dummy classifier on
  the same startup. Recovery: rename the backup back into place

#### Configuration keys
- `Routing.QualityFallback.{Enabled, MinResponseLength, BadKeywords}`
- `Trace.{Enabled, Directory, ChannelCapacity}`

### Changed

- README condensed from 1310 to 669 lines. New features (quality
  fallback, trace log, cold-start, CLI flags) given dedicated sections;
  verbose architectural prose and per-endpoint duplicated examples
  trimmed. All 12 mandatory sections (config keys, endpoints, schemas,
  operations, etc.) preserved
- CHANGELOG rewritten from user perspective for v1.0.0 entry — internal
  phase references removed; entries organized by user-visible feature
  area

## [1.0.0] - 2026-05-10

First production release. F# .NET 10 gateway routing OpenAI-compatible chat
completion requests between Qwen 35B (latency-focused) and Qwen 122B
(quality-focused) running locally as `mlx_lm.server` instances.

### Added

#### Core routing
- 3-stage decision pipeline: explicit model override → task table → ML classifier
- 7 task types with model + priority mapping (`graph_indexing`,
  `compiler_debug`, `architecture_analysis`, `dependency_analysis`,
  `reasoning`, `retrieval`, `summary`)
- ML routing via bge-m3 int8 embeddings (multilingual; Korean + English) +
  ML.NET LbfgsLogisticRegression classifier; threshold tunable via
  `Routing.ML.Threshold` (default 0.5)
- First-run bootstrap auto-generates a dummy classifier when
  `models/router.zip` is missing — router never throws on cold start

#### Streaming + concurrency
- SSE streaming pass-through with mid-stream cancellation; the upstream call
  aborts within one chunk interval when the client disconnects
- Strategy-D `[DONE]` sentinel injection if upstream omits it
- Concurrency cap on 122B (`SemaphoreSlim(1)`) with two-level priority queue
  (high/low) + fairness counter; high-priority requests preempt low-priority
  ones in the queue
- 35B requests bypass the queue

#### Reliability
- Quality fallback: when 35B response fails a configurable heuristic, the
  router automatically retries the same prompt on 122B and forwards 122B's
  response (non-streaming only — streaming chunks already shipped to client
  cannot be retracted)
- Health-probe-based fallback: when 122B is unreachable, requests transparently
  reroute to 35B (`routing_reason: "fallback_to_35b"` in the decision log).
  Exception: `task: "graph_indexing"` returns HTTP 503 instead, never silently
  downgrading
- Per-upstream health probing every 10 seconds via `GET /v1/models`;
  configurable `ConsecutiveFailureThreshold`
- Transient retry policy on non-streaming requests (no retry on streaming —
  partial SSE output cannot be replayed)

#### Auto-retraining loop
- Background service detects failed routing decisions
  (`fallback_used=true`), asks a teacher model (122B by default) to relabel
  the prompt, accumulates labeled samples in `datasets/hard-cases.jsonl`,
  retrains the ML classifier, and atomically swaps in the new model when
  validation gates pass
- Classifier hot-swap via `PredictionEnginePool` with `watchForChanges:true`
  — in-flight requests complete on the previous model
- Daily cost cap on teacher calls (`TeacherLabeler.DailyCallCap`; default 1000)
- Manual offline retraining via `dotnet run -- --retrain`

#### Canary deployment
- When `models/router-canary.zip` is dropped into the models directory, 10%
  of traffic is routed to it (sticky per `correlation_id`); other 90% stays
  on the baseline
- Auto-rollback when canary fallback rate exceeds baseline by more than
  10% over a rolling 60-second window (`AutoRollbackEnabled`; default true)
- Manual promote / rollback / enable via `POST /canary/{promote,rollback,enable}`
- Decision log `model_version` distinguishes baseline (`ml-{sha}`) from
  canary (`ml-{sha}-canary`) so cohort comparison is a simple group-by

#### Endpoints
- `POST /v1/chat/completions` — main routing endpoint (OpenAI-compatible;
  streaming + non-streaming)
- `GET /v1/models` — deduplicated model list from both upstreams (returns
  HTTP 200 + empty array when both are down, never 503)
- `GET /health`, `GET /healthz` — per-upstream reachability + last probe
  timestamp
- `GET /stats` — queue depth, active counts, throughput, baseline + canary
  model versions, canary percentage, canary active flag (single-endpoint
  scrape for monitoring)
- `GET /canary`, `POST /canary/{promote,rollback,enable}` — canary admin
- `X-Correlation-Id` response header on every response (joinable to
  decision-log rows)

#### Logging
- Structured decision log at `logs/decisions/YYYY-MM-DD.jsonl` (one row
  per request; 12 fields including `correlation_id`, `prompt_hash`,
  `target`, `latency_ms`, `model_version`, `fallback_used`, `routing_reason`)
- Operational log: rolling daily files at `logs/operational/smart-router-{Date}.log`
  (50 MB cap, 30-day retention, automatic rotation)
- Trace log (opt-in via `--trace-responses`) at `logs/trace/YYYY-MM-DD.jsonl`
  with prompt + initial-response + final-response excerpts joinable by
  prompt UID for end-to-end debugging
- Auto-pruning background service for all three log streams +
  `datasets/teacher-cap-*.json` files

#### Configuration (`appsettings.json`)
- `Upstreams.{Model35B, Model122B}` — base URLs for the mlx_lm servers
- `Routing.ML.{ModelPath, EmbeddingModelPath, TokenizerPath, Threshold, MaxTokens}`
- `Routing.QualityFallback.{Enabled, MinResponseLength, BadKeywords}`
- `Routing.Health.{PollingIntervalSeconds, ConsecutiveFailureThreshold}`
- `Routing.TaskTable` (per-task model + priority)
- `Routing.ModelAliases` (`auto`, `35b`, `122b`)
- `Queue.{MaxConcurrent122B, FairnessK, PerRequestTimeoutSeconds}`
- `Canary.{CanaryModelPath, PercentageEnabled, RollingWindowSeconds, AutoRollbackThreshold, AutoRollbackEnabled, MinBaselineSampleSize}`
- `Logging.{Directory, RetentionDays}`
- `DecisionLog.{Directory, RetentionDays, ChannelCapacity}`
- `TeacherLabeler.{Endpoint, PromptPath, DailyCallCap, TimeoutSeconds}`
- `Serilog.MinimumLevel.{Default, Override}` (per-source filtering)

#### CLI flags
- `--port=N` — override listen port (1024..65535; default 4000)
- `--log-level=verbose|debug|information|warning|error|fatal` (or short
  aliases `info`, `warn`, `dbg`, `vrb`, `err`, `ftl`)
- `--trace-responses` — enable trace log
- `--cold-start` — back up existing model + dataset files with timestamp
  suffix and start fresh; recovery is `mv backup file` and restart
- `--retrain` — run offline retraining pipeline once and exit

#### Deployment
- launchd LaunchAgent definition at `deploy/com.ohama.smart-router.plist`
  with `KeepAlive`, `RunAtLoad`, and 30-second restart throttle
- `scripts/deploy.sh` — `dotnet publish -c Release` + asset copy to install dir
- `scripts/install-launchd.sh` — copies the plist to
  `~/Library/LaunchAgents/`; manual `launchctl load -w` documented
- `scripts/download-models.sh` — fetches bge-m3 int8 ONNX from
  HuggingFace via `hf` CLI

### Changed

- Listen port + bind address: `127.0.0.1` only (loopback) — no firewall
  surprises
- Model file paths resolve CWD-independently — falls back through binary
  location and parent-directory walk, so `dotnet run` works without a
  manual symlink
- ASP.NET Core middleware noise filtered to Warning by default; smart-router's
  own emissions stay at Information
- Hot-path routing-decision log emission demoted to Debug (the same data
  is always in the JSONL decision log)

### Fixed

- Every `/v1/chat/completions` request returning HTTP 500 due to a DI
  lifetime mismatch in the canary feature manager
- Build failure on .NET 10 SDK with `TreatWarningsAsErrors=true`
- Decision log `model_version` stale after in-process retraining swap;
  now reflects the new model on the very next request
- Model embedding file download script broken by `huggingface-cli`
  deprecation; switched to `hf` CLI and corrected the source path
- README missing the prerequisites section for the bge-m3 ONNX files

### Removed

- Heuristic routing — the ML classifier is the only stage-3 algorithm.
  Historical snapshot preserved at git branch `archive/heuristic-baseline`
  and tag `v0.5-heuristic-baseline`
- `Routing.Algorithm` configuration key — no replacement; ML always runs
- `--routing-algorithm` CLI flag — no replacement
- `--trace` boolean CLI flag — replaced by `--log-level=debug` (legacy
  flag now raises a clear migration error)

### Requirements

- macOS arm64 (Apple Silicon) — mlx_lm runs Metal kernels
- .NET 10 SDK
- Two `mlx_lm.server` instances (Qwen 3.6 35B at port 8000, Qwen 3.5 122B
  at port 8001)
- bge-m3 int8 ONNX files (~547 MB total) under `models/embed/` —
  fetched via `./scripts/download-models.sh` (requires Python
  `huggingface_hub`)

## [v0.5-heuristic-baseline] - 2026-05-08

Snapshot of the heuristic routing implementation, preserved as a historical
reference. ML routing replaced the heuristic in v1.0.0; this tag remains
on `archive/heuristic-baseline` for rollback or comparison.
