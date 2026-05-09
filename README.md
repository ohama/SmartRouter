# smart-router

An F# .NET 10 gateway that routes every OpenAI-compatible request to the right local model — fast Qwen 35B for simple work, powerful Qwen 122B only when the task or prompt complexity warrants it — and retrains its own classifier from live feedback.

## Table of Contents

1. [What This Is](#1-what-this-is)
2. [Architecture](#2-architecture)
3. [Requirements](#3-requirements)
4. [Quickstart](#4-quickstart)
5. [Routing Pipeline](#5-routing-pipeline)
6. [ML Feedback Loop](#6-ml-feedback-loop)
7. [Configuration Reference](#7-configuration-reference)
8. [Endpoints](#8-endpoints)
9. [Debugging](#9-debugging)
10. [Hermes Integration](#10-hermes-integration)
11. [Graphify Integration](#11-graphify-integration)
12. [Operations](#12-operations)
13. [Troubleshooting](#13-troubleshooting)

---

## 1. What This Is

smart-router is a local-only HTTP gateway that sits in front of two `mlx_lm.server` instances (Qwen 3.6 35B at port 8000 and Qwen 3.5 122B at port 8001) and presents a single OpenAI-compatible endpoint at `http://127.0.0.1:4000`. Clients — Hermes (general coding assistant) and Graphify (graph-indexing pipeline) — send requests exactly as they would to OpenAI; the router picks the model, enforces a concurrency cap on 122B, logs every decision to a JSONL file, and periodically retrains its ML classifier from that log.

The problem it solves: 122B is expensive in compute and slow to respond; 35B is fast but less capable on complex multi-file reasoning tasks. Without a router, clients either always hit 122B (slow, hot GPU) or always hit 35B (missed quality for hard tasks). smart-router eliminates that trade-off by making the pick automatically, per request.

---

## 2. Architecture

```
  Hermes Agent          Graphify Pipeline
  (~/hermes-agent)      (graph_indexing + compiler_debug …)
        │                       │
        └───────────┬───────────┘
                    │  HTTP POST /v1/chat/completions
                    ▼
         ┌──────────────────────┐
         │   smart-router :4000  │
         │                      │
         │  CorrelationMiddleware│  ← injects correlation_id UUID per request
         │  ┌────────────────┐  │
         │  │ Routing.fs     │  │  ← 3-stage pure pipeline (no IO)
         │  │  1. model ovr  │  │
         │  │  2. task table │  │
         │  │  3. ML         │  │
         │  └────────────────┘  │
         │  QueueDispatcher     │  ← SemaphoreSlim cap: 1 in-flight 122B
         │  CanaryService       │  ← FileSystemWatcher on router-canary.zip
         │  DecisionLogger      │  ← async channel → JSONL file
         └──────┬───────────────┘
                │
     ┌──────────┴──────────┐
     ▼                     ▼
  qwen36-35b           qwen122b
  :8000                :8001
  (mlx_lm.server)      (mlx_lm.server)
```

### Hexagonal architecture

Core (`src/SmartRouter.Core/`) has zero infrastructure references. Microsoft.ML, Serilog, HttpClient, ASP.NET Core, and FSharp.SystemTextJson are all confined to the Cli project (`src/SmartRouter.Cli/`). The hexagonal boundary is enforced at the project level — `SmartRouter.Core.fsproj` has no NuGet dependencies.

### Routing algorithm

Stage 3 always runs the ML algorithm — embeds the prompt with bge-m3 int8 (ONNX) and feeds the 1024-dim vector to an ML.NET `LbfgsLogisticRegression` classifier trained on historical decisions. Confidence ≥ `Routing.ML.Threshold` (default `0.5`) routes to 122B; otherwise 35B. Requires `models/router.zip` and the ONNX files under `models/embed/`.

A single-shape `RoutingAlgorithm` function-type alias (`RoutingConfig -> RouterRequest -> RoutingDecision`) lives in Core for future extensibility; only the ML implementation ships in v1. Heuristic routing was retired in Phase 12 — historical snapshot at `archive/heuristic-baseline` branch + `v0.5-heuristic-baseline` tag.

### Two feedback loops

**Loop A — real-time fallback signal**
Every request that is routed to 122B but finds it unreachable gets transparently rerouted to 35B (`fallback_used=true`). A `DecisionLog` JSONL row is written for every request regardless of which path it took. `fallback_used=true` rows accumulate in `logs/decisions/YYYY-MM-DD.jsonl`.

**Loop B — background retraining**
A `BackgroundService` (`RetrainingService`) runs two `PeriodicTimer` loops — a daily timer and a count-threshold check every few minutes. When either fires: `FailureDetector` extracts rows with `fallback_used=true`, `TeacherLabeler` asks the 122B teacher model to label each one (`ROUTE_35B` or `ROUTE_122B`), the labeled examples are written to `datasets/hard-cases.jsonl`, `DatasetMerger` blends them 70/30 with the original training set, `Retrainer` trains a new ML.NET model, a `Validator` checks the held-out fallback rate, and an atomic `File.Move(overwrite=true)` swaps in the new `models/router.zip`. `ModelVersionProvider` increments the version string so subsequent `DecisionLog` rows carry the new `model_version`.

### Canary deployment

When `models/router-canary.zip` is dropped into the models directory, a `FileSystemWatcher` detects it and `CanaryService` arms the canary cohort. 10% of traffic (by `correlation_id` — sticky via `ContextualTargetingFilter`) is routed to the canary classifier; the other 90% uses the baseline. `CanaryWatchdog` monitors a rolling 60-second fallback-rate delta; if `canary_fallback_rate − baseline_fallback_rate > 0.10` and `AutoRollbackEnabled=true`, the watchdog automatically rolls back. Promote via `POST /canary/promote`; manual rollback via `POST /canary/rollback`.

---

## 3. Requirements

- macOS arm64 (Apple Silicon) — mlx_lm runs Metal kernels
- .NET 10 SDK (`dotnet --version` must report `10.x`)
- Two `mlx_lm.server` instances running:
  - Qwen 3.6 35B at `http://127.0.0.1:8000`
  - Qwen 3.5 122B at `http://127.0.0.1:8001`
- For ML routing (default): bge-m3 int8 ONNX files in `models/embed/`

The router does not start or manage the `mlx_lm.server` processes. They must be running independently (via launchd or manually) before the router starts probing them.

---

## 4. Quickstart

```bash
# 1. Clone and restore
git clone <repo-url>
cd smart-router
dotnet restore

# 2. Start the router (dev mode; Ctrl-C to stop)
dotnet run --project src/SmartRouter.Cli

# 3. Check upstream health
curl http://127.0.0.1:4000/health

# 4. Send a chat request
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "messages": [{"role": "user", "content": "hello"}]
  }'

# 5. Stream a response
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "stream": true,
    "messages": [{"role": "user", "content": "explain recursion"}]
  }'

# 6. See the routing decision
tail -1 logs/decisions/$(date +%F).jsonl | jq .

# 7. See available models from both upstreams
curl http://127.0.0.1:4000/v1/models | jq .

# 8. Install as a launchd service (see §12 for full details)
./scripts/deploy.sh
./scripts/install-launchd.sh
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist
```

The router listens on `http://127.0.0.1:4000` (loopback only). There is no TLS — this is a local service.

---

## 5. Routing Pipeline

### 5.1 Three-stage decision

Every `POST /v1/chat/completions` request passes through three pure stages in order. The first stage that produces a decision wins; later stages are skipped.

```
Request arrives
    │
    ▼
Stage 1: model override
    Does the request body carry a recognized model alias?
    ("35b", "qwen35b", "122b", "qwen122b", "auto" = no override)
    → Yes: use that model. DONE.
    → No:  continue.
    │
    ▼
Stage 2: task table
    Does the request carry a "task" field?
    → task = graph_indexing  → 122B high priority
    → task = compiler_debug  → 122B high priority
    → task = unknown string  → HTTP 400 UnsupportedTask
    → no task field          → continue.
    │
    ▼
Stage 3: ML classifier
    bge-m3 int8 embedding (1024-dim) → LbfgsLogisticRegression
    confidence ≥ Routing.ML.Threshold (default 0.5) → 122B; else 35B
```

### 5.2 Task table

All seven task types map to a model and a queue priority. Priority determines queue-jump behavior when 122B is at its concurrency cap (1 in-flight slot).

| task                  | target | priority | notes                         |
|-----------------------|--------|----------|-------------------------------|
| `graph_indexing`      | 122B   | high     | No fallback — 503 if 122B down |
| `compiler_debug`      | 122B   | high     |                               |
| `architecture_analysis` | 122B | high     |                               |
| `dependency_analysis` | 122B   | low      |                               |
| `reasoning`           | 122B   | low      |                               |
| `retrieval`           | 35B    | low      |                               |
| `summary`             | 35B    | low      |                               |

### 5.3 ML classifier

Stage 3 runs the ML pipeline:

1. **Embed** — Prompt text is concatenated and embedded by bge-m3 int8 (ONNX, loaded from `Routing.ML.EmbeddingModelPath`). The result is a 1024-dim L2-normalized float vector.
2. **Classify** — The vector is fed to an ML.NET `LbfgsLogisticRegression` classifier loaded from `Routing.ML.ModelPath` via `PredictionEnginePool` (with `watchForChanges:true` so retrained models are picked up atomically).
3. **Threshold** — `confidence ≥ Routing.ML.Threshold` (default: `0.5`) routes to 122B; otherwise 35B. Reason is `RoutingReason.ML`.

If `models/router.zip` is missing at startup, a dummy classifier with random 1024-dim weights is auto-generated so cold-start doesn't throw — Loop B (retraining) replaces it with a real model from the first labeled hard cases. Embedding model files (`models/embed/bge-m3-int8.onnx` and `models/embed/sentencepiece.bpe.model`) must be present; the router fails fast at startup if they are not.

### 5.4 Tuning ML routing

**Adjust the confidence threshold** (lower → more requests to 122B):
```json
"Routing": {
  "ML": { "Threshold": 0.4 }
}
```
Default `0.5` is a balanced split; lower to `0.4` if you observe Hermes/Graphify quality regressions on borderline prompts; raise to `0.6` to prefer 35B more aggressively for latency.

**Force a model per-request** — bypass stage 3 entirely:
```bash
curl -d '{"model": "122b", "messages": [...]}'   # always 122B
curl -d '{"model": "35b",  "messages": [...]}'   # always 35B
curl -d '{"task": "compiler_debug", "messages": [...]}'   # task table → 122B high-priority
```

**Watch the classifier improve over time** — Loop B retrains every `Retraining.IntervalMinutes` (default 60) using `fallback_used=true` records as hard cases. New models are validated against a held-out set before going live; rejected models are logged to `logs/retraining-rejections.jsonl`. The active `model_version` appears in every DecisionLog row — watch it bump after a successful retrain.

---

## 6. ML Feedback Loop

### 6.1 Loop A — real-time fallback signal

On every `POST /v1/chat/completions` request, `ChatCompletions.fs` runs a pre-flight check after routing completes but before opening the upstream connection:

1. If the routing decision targets 122B AND the task is **not** `graph_indexing` AND `IHealthProbe.IsReachable(Qwen122B)` returns `false` → shadow-rebind the decision to 35B, set `IsFallback = true`.
2. If the task **is** `graph_indexing` AND 122B is unreachable → return HTTP 503 + `{"error": {"type": "model_unavailable"}}`. Never reroute graph_indexing to 35B (that would corrupt the graph index).
3. After the response is written, emit one `DecisionLog` JSONL row with `fallback_used=true/false`.

Every request produces exactly one row in `logs/decisions/YYYY-MM-DD.jsonl`.

### 6.2 Loop B — background retraining

`RetrainingService` (a .NET `BackgroundService`) runs two timers:
- A daily timer (`Retraining.IntervalMinutes` default: 60 minutes, configurable lower for testing).
- A count-check timer every `Retraining.CountCheckIntervalMinutes` (default: 5 min) that triggers early if `datasets/hard-cases.jsonl` has grown by ≥ `Retraining.HardCaseCountTrigger` (default: 500) rows since the last retrain.

When a retrain triggers, the pipeline is:

```
DecisionLog (logs/decisions/*.jsonl)
    │
    ▼  FailureDetector
    │  extracts rows where fallback_used=true
    │
    ▼  TeacherLabeler
    │  sends each hard case to 122B at TeacherLabeler.Endpoint
    │  with the prompt template at prompts/teacher-prompt.md
    │  labels: "ROUTE_35B" | "ROUTE_122B"
    │  writes labeled rows to datasets/hard-cases.jsonl
    │
    ▼  DatasetMerger
    │  blends hard-cases.jsonl 70% + datasets/training-set.jsonl 30%
    │
    ▼  Retrainer (ML.NET LbfgsLogisticRegression)
    │  trains on blended dataset; holds out 20% for validation
    │  writes new model to a temp path
    │
    ▼  Validator
    │  computes fallback_rate on held-out set
    │  rejects if rate > baseline (writes rejection log to
    │  logs/retraining-rejections.jsonl)
    │
    ▼  File.Move(overwrite=true)
    │  moves current models/router.zip → models/router.zip.prev
    │  moves new model → models/router.zip
    │
    ▼  ModelVersionProvider
       increments CurrentVersion (e.g., "v1" → "v2")
       subsequent DecisionLog rows carry the new model_version
```

The `AutoRollbackEnabled` gate in `Canary.AutoRollbackEnabled` (not the retraining path) controls watchdog-triggered rollbacks separately. The retraining loop's own gate is the Validator: a model that performs worse than the baseline is rejected and discarded — no rollback needed because the primary `models/router.zip` was never overwritten.

### 6.3 Canary deployment lane

The canary lane lets you test a new classifier on 10% of real traffic before promoting it.

**Arming the canary:**
```bash
cp /path/to/new-model.zip \
   ~/llm-system/services/smart-router/models/router-canary.zip
```
A `FileSystemWatcher` detects the new file within ~200ms and `CanaryService` loads it. No restart required.

**Sticky cohort bucketing:**
`ContextualTargetingFilter` (from `Microsoft.FeatureManagement`) assigns each `correlation_id` to baseline or canary deterministically — the same `correlation_id` always lands in the same cohort for the lifetime of the canary. This prevents A/B bleed where the same client session sees both classifiers.

**Watchdog:**
`CanaryWatchdog` polls every `Canary.WatchdogPollIntervalSeconds` seconds. If the rolling 60-second canary fallback rate minus the baseline fallback rate exceeds `Canary.AutoRollbackThreshold` (default: `0.10`) and `Canary.AutoRollbackEnabled=true`, it automatically calls `RollbackAsync` and logs a `"AUTO-ROLLBACK"` Serilog event.

---

## 7. Configuration Reference

All keys live in `src/SmartRouter.Cli/appsettings.json`. The router reads them at startup; restart after any change.

### Upstreams

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Upstreams.Model35B` | string | `http://127.0.0.1:8000` | Base URL of the Qwen 35B mlx_lm.server instance |
| `Upstreams.Model122B` | string | `http://127.0.0.1:8001` | Base URL of the Qwen 122B mlx_lm.server instance |

### Routing

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Routing.TimeoutSeconds` | int | `300` | Per-request upstream timeout |
| `Routing.TaskTable` | object | (7 tasks) | Per-task model + priority mapping |
| `Routing.ModelAliases` | object | (auto/35b/122b) | Wire alias → ModelId mapping for stage-1 model override |

### Routing.ML

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Routing.ML.ModelPath` | string | `models/router.zip` | ML.NET trained classifier (primary) |
| `Routing.ML.EmbeddingModelPath` | string | `models/embed/bge-m3-int8.onnx` | bge-m3 int8 ONNX embedder |
| `Routing.ML.TokenizerPath` | string | `models/embed/sentencepiece.bpe.model` | Tokenizer for bge-m3 |
| `Routing.ML.Threshold` | float | `0.5` | ML confidence threshold; above → 122B |
| `Routing.ML.MaxTokens` | int | `512` | Max tokens fed to the embedder |

### Routing.Health

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Routing.Health.PollingIntervalSeconds` | int | `10` | How often HealthService probes each upstream |
| `Routing.Health.ConsecutiveFailureThreshold` | int | `1` | Failures before marking unreachable; raise to 2-3 to reduce flapping |

### Queue

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Queue.MaxConcurrent122B` | int | `1` | SemaphoreSlim cap on concurrent 122B requests |
| `Queue.FairnessK` | int | `10` | High-priority requests get at most K consecutive picks before low-priority gets a turn |
| `Queue.PerRequestTimeoutSeconds` | int | `300` | Time a request waits in the queue before HTTP 503 |

### DecisionLog

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `DecisionLog.Directory` | string | `logs/decisions` | Directory for JSONL log files (one file per day) |
| `DecisionLog.ChannelCapacity` | int | `10000` | In-memory async channel buffer size |

### TeacherLabeler

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `TeacherLabeler.Endpoint` | string | `http://127.0.0.1:8001` | 122B endpoint used as teacher |
| `TeacherLabeler.PromptPath` | string | `prompts/teacher-prompt.md` | Prompt template for labeling hard cases |
| `TeacherLabeler.DailyCallCap` | int | `1000` | Max teacher calls per day (local; cost cap is no-op) |
| `TeacherLabeler.TimeoutSeconds` | int | `30` | Per-call timeout to the teacher |

### HardCaseDataset

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `HardCaseDataset.Path` | string | `datasets/hard-cases.jsonl` | Accumulated labeled hard cases |

### Retraining

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Retraining.IntervalMinutes` | int | `60` | Periodic retrain interval |
| `Retraining.HardCaseCountTrigger` | int | `500` | Trigger early retrain after this many new hard cases |
| `Retraining.CountCheckIntervalMinutes` | int | `5` | How often to check the hard-case count |
| `Retraining.HardCasePath` | string | `datasets/hard-cases.jsonl` | Hard-case dataset read by Retrainer |
| `Retraining.TrainingSetPath` | string | `datasets/training-set.jsonl` | Base training set for 70/30 blend |
| `Retraining.ModelPath` | string | `models/router.zip` | Output path for the retrained model |
| `Retraining.PreviousModelPath` | string | `models/router.zip.prev` | Previous model backup (kept one generation) |
| `Retraining.HeldOutFraction` | float | `0.2` | Fraction of data withheld for Validator |

### Canary

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Canary.CanaryModelPath` | string | `models/router-canary.zip` | Path watched by FileSystemWatcher |
| `Canary.PercentageEnabled` | int | `10` | Initial cohort split percentage |
| `Canary.RollingWindowSeconds` | int | `60` | Watchdog rolling window for fallback rate |
| `Canary.WatchdogPollIntervalSeconds` | int | `10` | Watchdog poll frequency |
| `Canary.AutoRollbackThreshold` | float | `0.10` | Fallback delta that triggers auto-rollback |
| `Canary.AutoRollbackEnabled` | bool | `true` | Set false to disable watchdog auto-rollback |
| `Canary.MinBaselineSampleSize` | int | `50` | Minimum baseline samples before watchdog activates |

---

## 8. Endpoints

All endpoints bind to `http://127.0.0.1:4000` (loopback only).

| Method | Path | Description |
|--------|------|-------------|
| POST | `/v1/chat/completions` | Main routing endpoint — OpenAI-compatible |
| GET | `/v1/models` | Deduplicated model list from both upstreams |
| GET | `/health` | Per-upstream reachability + last probe timestamp |
| GET | `/stats` | Queue depth, active counts, throughput |
| GET | `/canary` | Canary state: model version, percentage |
| POST | `/canary/promote` | Promote canary classifier to primary |
| POST | `/canary/rollback` | Roll back canary (sets percentage to 0) |
| POST | `/canary/enable` | Set canary split percentage |

---

### POST /v1/chat/completions

The main routing endpoint. Accepts an OpenAI-compatible request body and proxies it to the selected upstream.

**Request:**
```bash
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "stream": false,
    "messages": [
      {"role": "system", "content": "You are a helpful assistant."},
      {"role": "user",   "content": "What is tail-call optimization?"}
    ]
  }'
```

**With task field (Graphify-style):**
```bash
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "task": "compiler_debug",
    "messages": [{"role": "user", "content": "why does this MLIR lowering fail?"}]
  }'
```

**Response (non-streaming):** Passes through the upstream response verbatim.

**Error responses:**
- `400` — routing error (unknown task, malformed body)
- `502` — upstream returned an error
- `503` — `graph_indexing` requested but 122B is unreachable

---

### GET /v1/models

Returns a deduplicated list of models from both upstreams. First-seen wins on duplicate `id`. If one upstream is down, returns the other's list. If both are down, returns `200 + {"object":"list","data":[]}`.

```bash
curl http://127.0.0.1:4000/v1/models | jq .
```

**Example response:**
```json
{
  "object": "list",
  "data": [
    {
      "id": "/Users/ohama/llm-system/models/qwen36-35b",
      "object": "model",
      "owned_by": "mlx_lm"
    },
    {
      "id": "/Users/ohama/llm-system/models/qwen122b",
      "object": "model",
      "owned_by": "mlx_lm"
    }
  ]
}
```

Note: mlx_lm advertises models by their local filesystem path, not a HuggingFace model ID.

---

### GET /health

Returns per-upstream reachability status and the timestamp of the last probe attempt.

```bash
curl http://127.0.0.1:4000/health | jq .
```

**Response:**
```json
{
  "qwen35b":  { "reachable": true,  "last_probed_at": "2026-05-09T03:40:00.000Z" },
  "qwen122b": { "reachable": false, "last_probed_at": "2026-05-09T03:39:50.000Z" }
}
```

`reachable: false` for 122B means the router is currently rerouting (fallback) all non-graph_indexing 122B-targeted requests to 35B.

---

### GET /stats

Returns live queue and throughput metrics. Updated on every request; no caching.

```bash
curl http://127.0.0.1:4000/stats | jq .
```

**Response:**
```json
{
  "timestamp":              "2026-05-09T03:40:00.000Z",
  "active_122b":            1,
  "queue_depth_122b_high":  0,
  "queue_depth_122b_low":   2,
  "active_35b":             3,
  "requests_per_sec":       4.2,
  "avg_latency_ms_60s":     1850.0,
  "failure_count_total":    5,
  "fairness_picks_high":    12,
  "fairness_picks_low":     44,
  "semaphore_available":    0
}
```

`semaphore_available: 0` means 122B is at its concurrency cap and new 122B-bound requests are queuing.

---

### GET /canary

Returns the current canary state.

```bash
curl http://127.0.0.1:4000/canary | jq .
```

**Response (canary armed):**
```json
{
  "active": true,
  "percentage": 10,
  "baseline_version": "v3",
  "canary_version": "v3-canary"
}
```

---

### POST /canary/promote

Promotes the canary classifier to primary. Copies `router-canary.zip` over `router.zip` and increments the model version.

```bash
curl -X POST http://127.0.0.1:4000/canary/promote | jq .
```

**Response (200):** `{"status": "promoted", "new_baseline_version": "v4"}`

**Error responses:**
- `404` — no canary file found
- `409` — retraining in progress; retry momentarily

---

### POST /canary/rollback

Sets canary percentage to 0 (idempotent). Does not delete `router-canary.zip`.

```bash
curl -X POST http://127.0.0.1:4000/canary/rollback | jq .
```

**Response:** `{"status": "rolled_back"}`

---

### POST /canary/enable

Sets or adjusts the canary split percentage (0–100).

```bash
curl -X POST 'http://127.0.0.1:4000/canary/enable?percentage=20' | jq .
```

**Response:** `{"status": "enabled", "percentage": 20}`

**Error (400):** `{"error": "percentage query parameter required, integer 0..100"}`

---

## 9. Debugging

### 9.1 DecisionLog schema

Every request produces one row in `logs/decisions/YYYY-MM-DD.jsonl`. The file rotates daily. Each row is a JSON object on a single line.

**Example row:**
```json
{
  "schema_version":         1,
  "correlation_id":         "a3f8c2d1e9b74a5f8c2d1e9b7a",
  "timestamp":              "2026-05-09T03:40:01.234Z",
  "target":                 "Qwen122B",
  "routing_reason":         "task_table",
  "routing_algorithm":      "ml",
  "latency_ms":             1924.5,
  "model_version":          "v3",
  "fallback_used":          false,
  "task_type":              "compiler_debug",
  "prompt_hash":            "sha256:abcdef1234567890abcdef1234567890",
  "prompt_korean_char_ratio": 0.12
}
```

**Field reference:**

| Field | Type | Description |
|-------|------|-------------|
| `schema_version` | int | Log schema version (currently 1) |
| `correlation_id` | string | UUID injected by `CorrelationMiddleware`; sticky key for canary bucketing |
| `timestamp` | string | ISO 8601 UTC — time the DecisionLog row was written |
| `target` | string | `"Qwen35B"` or `"Qwen122B"` — the model that actually served the request |
| `routing_reason` | string | `explicit_model:{alias}`, `explicit_task:{TaskType}`, `default`, `ml`, `fallback_to_35b`, or a compound like `ml;upstream_error`, `ml;cancelled`, `ml;stream_error` |
| `routing_algorithm` | string | `"ml"` (only valid value as of Phase 12; the seam is retained for future algorithm additions) |
| `latency_ms` | float | End-to-end time from request start to last byte written |
| `model_version` | string | Active classifier version (e.g., `"v3"` baseline, `"v3-canary"` for canary cohort) |
| `fallback_used` | bool | `true` if 122B was unreachable and the request was transparently rerouted to 35B |
| `task_type` | string or null | Value of the `task` field in the request body, if present |
| `prompt_hash` | string | SHA-256 of the concatenated message content; used by Loop B for deduplication |
| `prompt_korean_char_ratio` | float | Fraction of Korean characters (0.0–1.0); diagnostic for bilingual routing quality |

### 9.2 Reading model_version

- `"v1"`, `"v2"`, `"v3"` etc. — baseline classifier generation.
- `"v3-canary"` — request was served by the canary cohort using the v3-canary classifier.

When Loop B completes a retrain and the Validator accepts it, the version increments. You can watch `model_version` in the log to confirm a retrain took effect.

### 9.3 Reading fallback_used

`fallback_used: true` means:
1. The routing decision targeted 122B.
2. `IHealthProbe.IsReachable(Qwen122B)` returned `false` at request time.
3. The request was transparently served by 35B instead.
4. The task was **not** `graph_indexing` (that path gets HTTP 503 instead).

Sustained `fallback_used: true` rows → 122B is down. Check `/health`.

### 9.4 /health interpretation

```bash
# Is 122B reachable?
curl -s http://127.0.0.1:4000/health | jq '.qwen122b.reachable'
# → true  (122B up; normal routing)
# → false (122B down; fallback active for non-graph_indexing requests)
```

### 9.5 /stats for queue pressure

```bash
# Are requests queuing behind 122B?
curl -s http://127.0.0.1:4000/stats | jq '{depth_high: .queue_depth_122b_high, depth_low: .queue_depth_122b_low, active: .active_122b, semaphore: .semaphore_available}'
```

`semaphore_available: 0` and `queue_depth_122b_high > 0` = high-priority requests piling up. Consider raising `Queue.FairnessK` or adding a second 122B instance.

### 9.6 Log files (overview)

The router emits two parallel log streams plus a few small auxiliary files. All paths below are relative to the process WorkingDirectory (under launchd: `/Users/ohama/llm-system/services/smart-router/`).

| Path | Stream | Format | Audience |
|------|--------|--------|----------|
| stderr (Serilog Console sink) | Operational | text, structured | live `tail -f` in dev mode; launchd captures it to `~/llm-system/services/logs/smart-router.err` in production |
| `logs/decisions/YYYY-MM-DD.jsonl` | Decision | JSONL (12-field schema in §9.1) | FailureDetector / dashboards / Loop B retraining |
| `datasets/hard-cases.jsonl` | Training | JSONL (labeled samples) | Loop B input |
| `datasets/teacher-cap-YYYY-MM-DD.json` | Cap counter | JSON | TeacherLabeler daily cost-cap state |
| `logs/retraining-rejections.jsonl` | Validator audit | JSONL | post-mortem on rejected retrains |
| `~/llm-system/services/logs/smart-router.log` | launchd-captured stdout | (mostly empty — OBS-04 stream separation) | crash-time fallback only |
| `~/llm-system/services/logs/smart-router.err` | launchd-captured stderr | text (whatever Serilog writes) | post-mortem if no rolling file is configured yet |

**v1 logging stream model:** Serilog writes structured events to **stderr only** (OBS-04 invariant; stdout is reserved for the application). The launchd plist captures stderr to a flat file at `~/llm-system/services/logs/smart-router.err`. There is no built-in rotation in v1 — the file grows until the operator truncates it (`truncate -s 0 ~/llm-system/services/logs/smart-router.err`) or until Phase 13 ships rolling file sinks (planned: `logs/operational/smart-router-{Date}.log`, 50 MB cap, 30-day retention).

The DecisionLog stream is **rotated daily by filename** (UTC date in the name); there is no built-in retention in v1 — files accumulate until Phase 13's `LogRetentionService` ships (planned: 90-day retention).

### 9.7 Reading the operational log

Each line is a structured Serilog event written via the template `[{Level:u3}] {Message:lj}{NewLine}{Exception}`. Examples:

```
[INF] Now listening on: http://127.0.0.1:4000
[INF] HealthService starting
[INF] HealthService: Qwen35B reachable
[INF] HealthService: Qwen122B reachable
[INF] CanaryService: starting; canary_version=(none)
[INF] Routing target=Qwen122B reason=ML priority=Low stream=true
[WRN] HealthService: Qwen122B probe failed (consecutive=1)
[ERR] StreamAsync: unexpected error writing to response for Qwen122B
       System.Net.Http.HttpRequestException: Connection refused
```

The `correlation_id` (32-char hex) injected by `CorrelationMiddleware` is captured into Serilog's `LogContext` per request and is the join key with the JSONL DecisionLog rows. In v1 the operational template does not render `correlation_id` directly — to trace a single request, look up its `correlation_id` in the DecisionLog JSONL first, then grep the Serilog stream for any entries matching that value (Phase 13 expands the template to render `[{correlation_id}]` per line).

**Common operator queries:**

```bash
# Tail the live stderr stream (dev mode)
dotnet run --project src/SmartRouter.Cli 2>&1 | tee /tmp/smart-router.log

# Tail launchd-captured stderr (production)
tail -f ~/llm-system/services/logs/smart-router.err

# Find all warnings + errors today (launchd)
grep -E '\[(WRN|ERR)\]' ~/llm-system/services/logs/smart-router.err

# Trace a request across both streams
CID=$(jq -r 'select(.correlation_id == "abc12345...")' < logs/decisions/$(date +%F).jsonl | head -1 | jq -r .correlation_id)
echo "DecisionLog rows for $CID:"
grep "\"correlation_id\":\"$CID\"" logs/decisions/*.jsonl

# Count requests by target (last 24h)
jq -r '.target' < logs/decisions/$(date +%F).jsonl | sort | uniq -c

# Count fallback events (last 7 days)
for d in 0 1 2 3 4 5 6; do
  date=$(date -v -${d}d +%F 2>/dev/null || date -d "${d} days ago" +%F)
  count=$(jq 'select(.fallback_used == true)' < logs/decisions/${date}.jsonl 2>/dev/null | wc -l | tr -d ' ')
  echo "${date}: ${count} fallback events"
done
```

### 9.8 Log levels and filtering

Serilog's runtime minimum level is controlled by a `LoggingLevelSwitch`. In v1, the default level is `Information`; the legacy `--trace` boolean flag flips it to `Debug`. Phase 13 replaces `--trace` with `--log-level=enum` (valid values: `verbose | debug | information | warning | error | fatal`).

| Level | Volume | Used for |
|-------|--------|----------|
| Verbose / VRB | very low | per-token streaming events; off by default |
| Debug / DBG | low | per-request prompt-feature extraction; HealthService steady-state probes; queue ticket lifecycle |
| Information / INF | moderate | per-request routing decisions (note: hot-path); HealthService startup + state-changes; CanaryService promote/rollback; RetrainingService cycle start/end |
| Warning / WRN | bursty | upstream 502/timeouts (retries handle them); HealthService transitions to unreachable; queue depth high-water; cost cap thresholds; FileSystemWatcher rearm failures |
| Error / ERR | rare | unhandled exceptions in BackgroundService loops; SSE write failures; teacher labeler malformed responses after retries |
| Fatal / FTL | should never appear | reserved for unrecoverable host failures |

`appsettings.json:Serilog.MinimumLevel.Default` documents the intended config-driven default but is not yet wired to Serilog's startup binding in v1 (Phase 13 wires `.ReadFrom.Configuration(...)` so the appsettings value takes effect; v1 always boots at `Information` regardless of the appsettings value).

### 9.9 Log parameters reference

The following keys in `appsettings.json` control logging behavior:

| Key | Type | Default | Effect (v1) | Effect after Phase 13 |
|-----|------|---------|-------------|----------------------|
| `Serilog.MinimumLevel.Default` | string | `"Information"` | (v1: ignored — see §9.8) | active default level |
| `Serilog.MinimumLevel.Override.{Source}` | string | absent | (v1: ignored) | per-category filter (e.g., `Microsoft.AspNetCore: Warning`) |
| `Logging.Directory` | string | n/a (Phase 13) | (v1: stderr only) | rolling file sink location |
| `Logging.RetentionDays` | int | n/a (Phase 13) | n/a | days of operational logs to keep |
| `DecisionLog.Directory` | string | `"logs/decisions"` | output directory for JSONL DecisionLog | unchanged |
| `DecisionLog.ChannelCapacity` | int | `10000` | in-memory async-channel buffer for DecisionLog writer | unchanged |
| `DecisionLog.RetentionDays` | int | n/a (Phase 13) | n/a | days of decision JSONL to keep |
| CLI flag `--trace` | bool | absent | flips `LoggingLevelSwitch` to Debug | replaced by `--log-level=enum` |
| CLI flag `--log-level=...` | enum | n/a (Phase 13) | n/a | sets `LoggingLevelSwitch` for the lifetime of the process |

In dev mode (`dotnet run`), Serilog writes to the console. In the launchd deployment, it writes to whatever launchd captures stderr into (default: `smart-router.err`). Phase 13 introduces a parallel rolling file sink so post-mortem analysis doesn't require trawling a single growing file.

---

## 10. Hermes Integration

Hermes Agent (`~/hermes-agent`) is a general-purpose coding assistant. It does not send a `task` field. Every Hermes request passes through stages 1 and 2 of the routing pipeline without a match, and the decision is made entirely by stage 3 (the ML classifier).

**Point Hermes at the router:**
```jsonc
// hermes config (exact key name depends on your Hermes version)
{
  "openai": {
    "base_url": "http://localhost:4000/v1",
    "model": "auto"
  }
}
```

`model: "auto"` lets the router decide. You can force a model by setting `model: "35b"` or `model: "122b"` — this triggers stage-1 model override and bypasses routing entirely.

**Streaming:** Fully supported. Hermes uses `stream: true`; the router forwards SSE chunks token-by-token and injects `data: [DONE]` if the upstream omits it.

**Mid-stream cancellation:** If Hermes cancels the request, the router detects `OperationCanceledException`, disposes the upstream connection cleanly, and logs a `routing_reason` ending in `;cancelled`.

---

## 11. Graphify Integration

Graphify is a graph-indexing pipeline that sends a `task` field with every request. The `task` value routes directly through stage 2 (task table), bypassing the stage-3 ML classifier entirely.

**Standard Graphify request:**
```json
{
  "model": "auto",
  "task": "graph_indexing",
  "messages": [
    {"role": "user", "content": "Index this codebase: ..."}
  ]
}
```

```bash
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "task": "architecture_analysis",
    "messages": [{"role": "user", "content": "describe the module structure"}]
  }'
```

### graph_indexing no-fallback rule

`graph_indexing` is the only task with a hard no-fallback constraint. If the routing decision selects 122B for a `graph_indexing` request and 122B is currently unreachable, the router returns:

```
HTTP 503
Content-Type: application/json

{
  "error": {
    "message": "Task 'graph_indexing' requires Qwen122B which is currently unreachable; fallback policy does not apply for graph_indexing.",
    "type": "model_unavailable",
    "correlation_id": "a3f8c2d1e9b74a5f8c2d1e9b7a"
  }
}
```

This is intentional. Shadow-rebinding `graph_indexing` to 35B would produce an incomplete or incorrect graph index. Graphify should handle 503 by retrying once 122B is back up (check `/health`).

### Concurrency cap

122B is gated to 1 in-flight request via `SemaphoreSlim`. Graphify's `graph_indexing` and `compiler_debug` tasks are `high` priority, so they queue-jump `dependency_analysis` and `reasoning` requests (which are `low` priority). If you have parallel Graphify pipelines, they will queue; the queue timeout is `Queue.PerRequestTimeoutSeconds` (default: 300s).

---

## 12. Operations

### 12.1 launchd setup

The router ships with a launchd plist and two helper scripts:

- `deploy/com.ohama.smart-router.plist` — the LaunchAgent definition
- `scripts/deploy.sh` — publishes the .NET binary to `~/llm-system/services/smart-router/`
- `scripts/install-launchd.sh` — copies the plist to `~/Library/LaunchAgents/`

**First-time install:**
```bash
# 1. Publish the binary + assets (idempotent; safe to re-run)
./scripts/deploy.sh

# 2. Copy the plist to LaunchAgents (does NOT auto-load; gives you review opportunity)
./scripts/install-launchd.sh

# 3. Load and start the service
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist

# 4. Verify it started
curl http://127.0.0.1:4000/health
```

**Stop the service:**
```bash
launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist
```

**Restart after config change:**
```bash
launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist
```

**View logs:**
```bash
tail -f ~/llm-system/services/logs/smart-router.log
tail -f ~/llm-system/services/logs/smart-router.err
```

**Plist behavior:**
- `KeepAlive: true` — launchd restarts the process on any exit (including crashes).
- `RunAtLoad: true` — service starts immediately on `launchctl load`.
- `ThrottleInterval: 30` — restart attempts are throttled to at most one every 30 seconds.
- `WorkingDirectory: /Users/ohama/llm-system/services/smart-router` — relative paths in `appsettings.json` (e.g., `logs/decisions`, `models/router.zip`) resolve from here.

The plist install path is `~/Library/LaunchAgents/com.ohama.smart-router.plist` (LaunchAgent — runs as the logged-in user, not root).

### 12.2 Tune ML routing

Edit `appsettings.json` in the install directory:
```bash
# Adjust confidence threshold (lower → more 122B routing)
nano ~/llm-system/services/smart-router/appsettings.json
# Edit Routing.ML.Threshold (default 0.5)

# Restart
launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist
```

The router reads `Routing.ML.Threshold`, `Routing.ML.ModelPath`, and `Routing.ML.EmbeddingModelPath` at startup; restart after any change. Loop B can also retrain the model in-place — see § 6.2 and § 12.4.

### 12.3 Canary workflow

```bash
# Step 1: Train a new classifier and drop it as router-canary.zip
cp /path/to/new-model.zip \
   ~/llm-system/services/smart-router/models/router-canary.zip
# FileSystemWatcher arms the canary within ~200ms. No restart needed.

# Step 2: Confirm canary is active
curl http://127.0.0.1:4000/canary | jq .

# Step 3: Adjust the split (optional; default 10%)
curl -X POST 'http://127.0.0.1:4000/canary/enable?percentage=20'

# Step 4a: Promote if metrics look good
curl -X POST http://127.0.0.1:4000/canary/promote

# Step 4b: Roll back if metrics are bad (idempotent)
curl -X POST http://127.0.0.1:4000/canary/rollback

# Monitor fallback_used rate in the log
grep '"fallback_used":true' \
  ~/llm-system/services/smart-router/logs/decisions/$(date +%F).jsonl | wc -l
```

The watchdog auto-rollback fires if the canary fallback rate is more than 10% higher than the baseline over a 60-second rolling window. Set `Canary.AutoRollbackEnabled: false` to disable it.

### 12.4 Manual retrain

```bash
# Trigger a retrain from the CLI (dev mode)
dotnet run --project src/SmartRouter.Cli -- --retrain

# Or against the deployed binary
cd ~/llm-system/services/smart-router
dotnet SmartRouter.Cli.dll --retrain
```

This runs the full Loop B pipeline synchronously: FailureDetector → TeacherLabeler → DatasetMerger → Retrainer → Validator → File.Move. If the Validator rejects the new model, the current `models/router.zip` is unchanged and the rejection is logged to `logs/retraining-rejections.jsonl`.

**Seed hard cases before first retrain:**
```bash
dotnet fsi scripts/seed-hard-cases.fsx
dotnet run --project src/SmartRouter.Cli -- --retrain
```

### 12.5 Tune ConsecutiveFailureThreshold

`Routing.Health.ConsecutiveFailureThreshold` (default: `1`) controls how many consecutive failed health probes are needed before an upstream is marked unreachable.

- Default of `1` is aggressive: a single transient network blip marks 122B unreachable and triggers fallback for all in-flight non-graph_indexing requests.
- Raise to `2` or `3` in production if you observe spurious fallback activation (visible as brief `fallback_used: true` bursts with `reachable` immediately recovering).
- Trade-off: higher threshold means slower activation when 122B genuinely goes down.

---

## 13. Troubleshooting

### model_unavailable returned for graph_indexing

**Symptom:** Graphify gets HTTP 503 with `{"error": {"type": "model_unavailable"}}`.

**Diagnostic:**
```bash
curl http://127.0.0.1:4000/health | jq '.qwen122b.reachable'
# → false
```

**Fix:** 122B is down. Restart its mlx_lm.server:
```bash
launchctl unload ~/Library/LaunchAgents/com.ohama.qwen122b.plist
launchctl load -w ~/Library/LaunchAgents/com.ohana.qwen122b.plist
```
Then wait for `PollingIntervalSeconds` (default 10s) and check `/health` again. This is expected behavior — `graph_indexing` must not reroute to 35B.

---

### Fallback flapping (fallback_used alternates true/false rapidly)

**Symptom:** The DecisionLog shows `fallback_used` flickering between `true` and `false` even when 122B appears healthy.

**Diagnostic:**
```bash
tail -20 logs/decisions/$(date +%F).jsonl | jq '.fallback_used'
# → true, false, true, false, true ...
```

**Fix:** 122B is intermittently failing health probes (single-probe sensitivity). Raise `ConsecutiveFailureThreshold`:
```json
"Routing": {
  "Health": {
    "ConsecutiveFailureThreshold": 2
  }
}
```
Restart after editing.

---

### HF-id trap (upstream returns "model not found")

**Symptom:** The upstream returns an error like `"model not found"` or `"invalid model id"` when you explicitly send a model alias in the request body.

**Diagnostic:**
```bash
# Check the actual id mlx_lm advertises for the 35B instance
curl http://127.0.0.1:8000/v1/models | jq '.data[].id'
# → "/Users/ohama/llm-system/models/qwen36-35b"
```

**Fix:** mlx_lm uses the local filesystem path as the model id, not a HuggingFace-style id like `Qwen/Qwen3-35B`. The router's `tryParseModelAlias` maps common aliases (`"35b"`, `"122b"`, `"auto"`) to the correct model IDs internally. Only send those aliases in the `model` field, not raw HF ids.

If you have a client sending an explicit HF-style id, configure it to send `"auto"`, `"35b"`, or `"122b"` instead.

---

### Canary auto-rollback cascade (canary keeps getting rolled back)

**Symptom:** Every `POST /canary/promote` is followed ~60s later by an automatic rollback. `curl /canary` shows `percentage: 0` shortly after promotion.

**Diagnostic:**
```bash
tail -f ~/llm-system/services/logs/smart-router.log | grep AUTO-ROLLBACK
```

**Fix:** The canary classifier is genuinely worse than the primary (fallback delta > `Canary.AutoRollbackThreshold` over the rolling window). Options:
1. Retrain with more labeled data before promoting.
2. Raise `Canary.AutoRollbackThreshold` (e.g., `0.20`) if the delta is within acceptable range.
3. Set `Canary.AutoRollbackEnabled: false` and monitor manually.

---

### dotnet not found in launchd context

**Symptom:** `smart-router.err` shows `command not found: dotnet` or the service exits immediately.

**Diagnostic:**
```bash
which dotnet
# → /opt/homebrew/bin/dotnet
cat ~/Library/LaunchAgents/com.ohama.smart-router.plist | grep ProgramArguments -A 5
```

**Fix:** launchd does not inherit `~/.zshrc` PATH. The plist `ProgramArguments` must use an absolute path:
```xml
<key>ProgramArguments</key>
<array>
  <string>/opt/homebrew/bin/dotnet</string>
  <string>/Users/ohama/llm-system/services/smart-router/SmartRouter.Cli.dll</string>
</array>
```
Re-run `./scripts/install-launchd.sh` and restart the service.

---

### Gatekeeper quarantine blocks the service

**Symptom:** Service exits silently shortly after `launchctl load`. No crash in `.err`.

**Diagnostic:**
```bash
xattr -lr ~/llm-system/services/smart-router/ | grep com.apple.quarantine
```

**Fix:**
```bash
xattr -dr com.apple.quarantine ~/llm-system/services/smart-router/
launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist
```

---

## 14. Further Reading

### How-to guides

Internal development notes in `documentation/howto/` — written during implementation, useful if you're extending the router:

| File | Topic |
|------|-------|
| `build-priority-queue-on-semaphoreslim.md` | Phase 3: fair priority queue with SemaphoreSlim |
| `bypass-concurrency-gated-upstream-with-named-httpclient.md` | Named HttpClient patterns |
| `debug-kestrel-request-aborted.md` | RequestAborted cancellation token edge cases |
| `force-task-yield-in-fake-async-doubles.md` | Test doubles for async flows |
| `handle-fsharp-task-finally-disposal.md` | F# task {} disposal in finally blocks |
| `handle-fsharp-try-with-semicolon-trap.md` | F# try-with semicolon pitfall |
| `order-mlnet-traintest-split-before-fit.md` | ML.NET train/test split ordering |
| `propagate-cancellation-through-fsharp-task-trywith.md` | Cancellation propagation |
| `setup-aspnetcore-config-override-test.md` | Integration test config overrides |
| `use-semaphoreslim-not-mutex-for-async-idempotency.md` | SemaphoreSlim for async idempotency |
| `wire-fsharp-namedhttpclient-with-configurehttpclient.md` | Named HttpClient DI wiring |

### Planning artifacts

- `.planning/ROADMAP.md` — phase-by-phase goals and success criteria
- `.planning/REQUIREMENTS.md` — functional and non-functional requirements (REL-01..04, ROUT-01..07, etc.)
- `.planning/phases/` — per-phase CONTEXT.md, RESEARCH.md, and SUMMARY.md files

These are for developers extending smart-router, not for operators using it. Everything an operator needs to run and tune the router is in this README.
