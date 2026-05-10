# smart-router

An F# .NET 10 gateway routing OpenAI-compatible requests between local Qwen 35B (fast) and Qwen 122B (powerful), with quality fallback, ML-based routing, and self-retraining from live feedback.

## Table of Contents

1. [What This Is](#1-what-this-is)
2. [Architecture](#2-architecture)
3. [Requirements](#3-requirements)
4. [Quickstart](#4-quickstart)
5. [Routing Pipeline](#5-routing-pipeline)
6. [ML Feedback Loop](#6-ml-feedback-loop)
7. [Configuration Reference](#7-configuration-reference)
8. [Endpoints](#8-endpoints)
9. [Logs and Debugging](#9-logs-and-debugging)
10. [Hermes / Graphify Integration](#10-hermes--graphify-integration)
11. [Operations](#11-operations)
12. [CLI Flags](#12-cli-flags)
13. [Troubleshooting](#13-troubleshooting)

---

## 1. What This Is

A local-only HTTP gateway at `http://127.0.0.1:4000` fronting two `mlx_lm.server` instances (Qwen 35B at :8000, Qwen 122B at :8001). Clients (Hermes, Graphify) send OpenAI-compatible requests; the router picks the model, enforces a 1-in-flight cap on 122B, optionally retries 35B's bad responses on 122B, logs every decision to JSONL, and retrains its classifier from that log.

**Why:** 122B is slow; 35B is fast but weaker on hard tasks. Without a router, you pay 122B's latency always or 35B's quality always. smart-router picks per request.

---

## 2. Architecture

```
  Hermes / Graphify
        │  POST /v1/chat/completions
        ▼
  smart-router :4000
    ├─ CorrelationMiddleware     → injects correlation_id
    ├─ Routing (3 stages, pure)  → 1. model override → 2. task table → 3. ML
    ├─ QueueDispatcher           → SemaphoreSlim(1) on 122B
    ├─ QualityFallback           → 35B response bad → retry on 122B (non-streaming)
    ├─ CanaryService             → FileSystemWatcher on router-canary.zip
    ├─ DecisionLogger            → async channel → JSONL
    └─ TraceLogger (opt-in)      → async channel → JSONL (--trace-responses)
        │
   ┌────┴────┐
   ▼         ▼
 Qwen35B   Qwen122B
 :8000     :8001
```

**Hexagonal:** `SmartRouter.Core` (pure, BCL-only — no Microsoft.ML, no HttpClient, no ASP.NET Core, no Serilog). Adapters live in `SmartRouter.Cli`. Project boundary enforced via `.fsproj` references.

**Routing algorithm:** Stage 3 always runs ML. bge-m3 int8 ONNX → 1024-dim L2-normalized vector → ML.NET `LbfgsLogisticRegression` → confidence ≥ `Routing.ML.Threshold` (default 0.5) routes to 122B, else 35B. Heuristic routing was retired; snapshot at `archive/heuristic-baseline` branch + `v0.5-heuristic-baseline` tag.

**Two feedback loops:**
- **Loop A (real-time):** 122B unreachable + non-graph_indexing → reroute to 35B (`fallback_used=true`, `routing_reason=fallback_to_35b`). Quality-bad 35B response → retry on 122B (`routing_reason=fallback_to_122b`). DecisionLog row written for every request.
- **Loop B (background):** `RetrainingService` periodically reads `fallback_used=true` rows, sends them to a teacher (122B), writes labels to `datasets/hard-cases.jsonl`, retrains ML.NET model, validates against held-out set, and atomically swaps `models/router.zip`.

**Canary:** Drop `models/router-canary.zip` into the models dir → FileSystemWatcher arms canary cohort. 10% of traffic (sticky by `correlation_id`) routes to canary. Watchdog auto-rolls-back if canary fallback rate exceeds baseline by >10% over a 60s rolling window.

---

## 3. Requirements

- macOS arm64 (Apple Silicon) — mlx_lm uses Metal kernels
- .NET 10 SDK
- Two `mlx_lm.server` instances running independently:
  - Qwen 3.6 35B at `http://127.0.0.1:8000`
  - Qwen 3.5 122B at `http://127.0.0.1:8001`
- bge-m3 int8 ONNX files in `models/embed/` (mandatory; router refuses to start without them)
- Python `huggingface_hub` for the `hf` CLI (model download script)

### 3.1 First-time setup — ML embedding files

```bash
pip install -U huggingface_hub
./scripts/download-models.sh
```

Downloads `Teradata/bge-m3` (~542 MB int8 ONNX + ~5 MB SentencePiece) to `models/embed/`. Required files: `bge-m3-int8.onnx`, `sentencepiece.bpe.model`. If absent at startup, the router exits with a fatal log.

### 3.2 Where `models/` lives

The router resolves model paths relative to its **process working directory**.

| Mode | CWD | `models/` location |
|---|---|---|
| `dotnet run --project src/SmartRouter.Cli` from repo root | `src/SmartRouter.Cli/` | symlink: `ln -s ../../models src/SmartRouter.Cli/models` |
| launchd-managed install | `~/llm-system/services/smart-router/` | `scripts/deploy.sh` copies `models/` into the install dir |

### 3.3 Smoke test

```bash
ls -lh models/embed/bge-m3-int8.onnx models/embed/sentencepiece.bpe.model
dotnet build -c Release src/SmartRouter.Cli/SmartRouter.Cli.fsproj
dotnet run --project src/SmartRouter.Cli
# Expected: "Now listening on: http://127.0.0.1:4000" within ~3s
```

---

## 4. Quickstart

```bash
git clone <repo-url> && cd smart-router && dotnet restore
dotnet run --project src/SmartRouter.Cli         # start router
curl http://127.0.0.1:4000/health                # check upstreams
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"auto","messages":[{"role":"user","content":"hello"}]}'
tail -1 logs/decisions/$(date +%F).jsonl | jq .  # see the routing decision
```

For deployment as a launchd service: see [§11 Operations](#11-operations).

---

## 5. Routing Pipeline

### 5.1 Three-stage decision

```
Stage 1: model override
   request.model in {"35b","qwen35b","122b","qwen122b"}? → use it. DONE.
   "auto" or absent → continue.

Stage 2: task table
   request.task ∈ known tasks? → use task → model + priority. DONE.
   unknown task → HTTP 400.
   no task → continue.

Stage 3: ML classifier
   bge-m3 embed → LbfgsLogisticRegression → confidence ≥ Threshold? → 122B; else 35B.
```

### 5.2 Task table

| task | target | priority | notes |
|---|---|---|---|
| `graph_indexing` | 122B | high | **No fallback** — 503 if 122B down |
| `compiler_debug` | 122B | high | |
| `architecture_analysis` | 122B | high | |
| `dependency_analysis` | 122B | low | |
| `reasoning` | 122B | low | |
| `retrieval` | 35B | low | |
| `summary` | 35B | low | |

`graph_indexing` is the only no-fallback task — rerouting to 35B would corrupt the graph index, so the router returns HTTP 503 instead.

### 5.3 ML classifier

If `models/router.zip` is missing, a dummy classifier with random 1024-dim weights is auto-generated at startup so cold-start doesn't throw — Loop B replaces it with a real model from accumulated hard cases. `models/embed/*.onnx` files must exist (no graceful fallback).

### 5.4 Tuning

- **Threshold:** `Routing.ML.Threshold` — lower → more 122B. Default 0.5.
- **Force per-request:** `{"model":"122b"}` or `{"task":"compiler_debug"}` bypasses stage 3.
- **Watch retrains:** `model_version` in DecisionLog increments when Loop B successfully retrains.

### 5.5 Quality fallback (35B → 122B retry) — non-streaming only

When a non-streaming request is routed to 35B and the response fails a quality check, the router automatically retries on 122B and forwards 122B's response.

**Prerequisite conditions (all must hold):**
- Stage 3 routes to 35B
- 35B returns HTTP 200
- `Routing.QualityFallback.Enabled = true` (default)
- Quality check fires (see trigger conditions below)
- 122B reachable per HealthService

**Trigger conditions** (Phase 15; cheap-first cascade — first match wins):

1. **finish_reason match** — upstream `choices[0].finish_reason` matches any value in `Routing.QualityFallback.BadFinishReasons` (default `["length", "content_filter"]`). Catches max-tokens-truncated and content-filtered responses regardless of content length or keywords. Case-insensitive.
2. **Effective length below threshold** — `effectiveLength = int(length × (1 + koreanRatio × 0.8))` is below `MinResponseLength` (default 30). Korean responses are inflated by their Hangul-syllable ratio so a 28-char Korean answer (effective ~50 chars) passes while a 28-char ASCII answer fails.
3. **Low Shannon entropy** — `charEntropy(content) < EntropyThreshold` (default 2.5). Catches token-loop responses (`"the the the..."`) that pass length and keyword checks. Normal text scores 4.0–5.0+; pathological loops score 1.0–2.0.
4. **Bad keyword present** — content contains any value in `Routing.QualityFallback.BadKeywords` (case-insensitive since Phase 15; defaults `["TODO", "I think"]`). Operators may add refusal patterns like `"I cannot"`, `"As an AI"`, `"I'm unable"` if appropriate for their traffic — these are NOT in the default to avoid false positives in Q&A about AI itself.

The quality check operates on `choices[0].message.content` (assistant text only), not the raw JSON envelope. Malformed upstream responses degrade safely — they are treated as empty content and trigger fallback.

The `bad_reason` field in the trace log (if `--trace-responses`) records which check fired: `"finish_reason=length"`, `"length=12"`, `"entropy=1.85"`, or `"keyword=TODO"`.

**On fire:** final response = 122B's. DecisionLog row: `target=Qwen122B`, `routing_reason=fallback_to_122b`, `fallback_used=true`. TraceLog row captures both 35B's bad response and 122B's response, joined by `prompt_uid`.

**Streaming requests are exempt** — chunks already shipped; cannot retract.

**Tuning:**

```jsonc
"Routing": {
  "QualityFallback": {
    "Enabled": true,
    "MinResponseLength": 30,
    "BadKeywords": ["TODO", "I think"],
    "BadFinishReasons": ["length", "content_filter"],
    "EntropyThreshold": 2.5
  }
}
```

To add refusal-pattern detection (operator opt-in only — not in default):

```jsonc
"Routing": {
  "QualityFallback": {
    "BadKeywords": ["TODO", "I think", "I cannot", "As an AI", "I'm unable"]
  }
}
```

**Cost note:** When fallback fires, total latency = 35B + 122B. Monitor via:

```bash
grep '"routing_reason":"fallback_to_122b"' logs/decisions/$(date +%F).jsonl | wc -l
```

---

## 6. ML Feedback Loop

### 6.1 Loop A — real-time fallback

Per request, after routing:
1. Decision targets 122B AND task ≠ `graph_indexing` AND 122B unreachable → reroute to 35B, `fallback_used=true`, `routing_reason=fallback_to_35b`.
2. Decision targets 122B AND task = `graph_indexing` AND 122B unreachable → HTTP 503.
3. Decision targets 35B AND quality check fails AND 122B reachable → retry on 122B, `fallback_used=true`, `routing_reason=fallback_to_122b` (see §5.5).
4. Always: write one DecisionLog JSONL row.

### 6.2 Loop B — background retraining

`RetrainingService` runs:
- A daily timer (`Retraining.IntervalMinutes`, default 60).
- A count-check timer every `Retraining.CountCheckIntervalMinutes` (default 5) — triggers early if `datasets/hard-cases.jsonl` grew by ≥ `Retraining.HardCaseCountTrigger` (default 500) since last retrain.

Pipeline: `FailureDetector` (extract `fallback_used=true`) → `TeacherLabeler` (122B labels each as `ROUTE_35B`/`ROUTE_122B`, writes to `hard-cases.jsonl`) → `DatasetMerger` (70/30 blend with `training-set.jsonl`) → `Retrainer` (ML.NET LbfgsLogisticRegression) → `Validator` (rejects if held-out fallback rate worse than baseline; logs to `logs/retraining-rejections.jsonl`) → atomic `File.Move` to `models/router.zip`. `ModelVersionProvider` increments the version string.

### 6.3 Canary

Drop `models/router-canary.zip` into the models dir. `FileSystemWatcher` detects within ~200ms; `CanaryService` arms the canary cohort. `ContextualTargetingFilter` makes cohort assignment sticky per `correlation_id` (no A/B bleed). `CanaryWatchdog` polls every `Canary.WatchdogPollIntervalSeconds`; if (canary fallback rate − baseline) > `Canary.AutoRollbackThreshold` (default 0.10) over the rolling window AND `Canary.AutoRollbackEnabled=true`, it rolls back automatically and logs `AUTO-ROLLBACK`.

---

## 7. Configuration Reference

All keys in `src/SmartRouter.Cli/appsettings.json`. The router reads at startup; restart after changes.

### Upstreams

| Key | Type | Default |
|---|---|---|
| `Upstreams.Model35B` | string | `http://127.0.0.1:8000` |
| `Upstreams.Model122B` | string | `http://127.0.0.1:8001` |

### Routing

| Key | Type | Default | Description |
|---|---|---|---|
| `Routing.TimeoutSeconds` | int | 300 | Per-request upstream timeout |
| `Routing.TaskTable` | object | (7 tasks) | Per-task model + priority |
| `Routing.ModelAliases` | object | auto/35b/122b | Stage-1 alias mapping |

### Routing.ML

| Key | Type | Default |
|---|---|---|
| `Routing.ML.ModelPath` | string | `models/router.zip` |
| `Routing.ML.EmbeddingModelPath` | string | `models/embed/bge-m3-int8.onnx` |
| `Routing.ML.TokenizerPath` | string | `models/embed/sentencepiece.bpe.model` |
| `Routing.ML.Threshold` | float | 0.5 |
| `Routing.ML.MaxTokens` | int | 512 |

### Routing.QualityFallback

| Key | Type | Default | Description |
|---|---|---|---|
| `Routing.QualityFallback.Enabled` | bool | `true` | Master kill switch. `false` disables all quality checks regardless of other settings. |
| `Routing.QualityFallback.MinResponseLength` | int | `30` | Minimum effective length (Korean-aware) below which the response is considered bad. `<= 0` resets to default. |
| `Routing.QualityFallback.BadKeywords` | string[] | `["TODO","I think"]` | Case-insensitive (since Phase 15) substring matches against assistant content. Operator may add refusal patterns like `"I cannot"`, `"As an AI"` — not in default to avoid false positives. |
| `Routing.QualityFallback.BadFinishReasons` | string[] | `["length","content_filter"]` | **Phase 15.** Match `choices[0].finish_reason` (case-insensitive). `"length"` = max-tokens truncation; `"content_filter"` = moderation rejection. Empty array disables this check. |
| `Routing.QualityFallback.EntropyThreshold` | float | `2.5` | **Phase 15.** Shannon character-entropy threshold. Content below this value is considered a repetition loop. Set to `0` or omit to use default; set to a very small value (e.g. `0.01`) to effectively disable entropy checks. |

### Routing.Health

| Key | Type | Default |
|---|---|---|
| `Routing.Health.PollingIntervalSeconds` | int | 10 |
| `Routing.Health.ConsecutiveFailureThreshold` | int | 1 |

### Queue

| Key | Type | Default |
|---|---|---|
| `Queue.MaxConcurrent122B` | int | 1 |
| `Queue.FairnessK` | int | 10 |
| `Queue.PerRequestTimeoutSeconds` | int | 300 |

### Logging / DecisionLog / Trace

| Key | Type | Default |
|---|---|---|
| `Logging.Directory` | string | `logs/operational` |
| `Logging.RetentionDays` | int | 30 |
| `DecisionLog.Directory` | string | `logs/decisions` |
| `DecisionLog.ChannelCapacity` | int | 10000 |
| `DecisionLog.RetentionDays` | int | 90 |
| `Trace.Enabled` | bool | false (also enabled by `--trace-responses`) |
| `Trace.Directory` | string | `logs/trace` |
| `Trace.ChannelCapacity` | int | 1000 |
| `Serilog.MinimumLevel.Default` | string | Information |
| `Serilog.MinimumLevel.Override.{Source}` | string | (filters host noise) |

### TeacherLabeler / Retraining / HardCaseDataset

| Key | Type | Default |
|---|---|---|
| `TeacherLabeler.Endpoint` | string | `http://127.0.0.1:8001` |
| `TeacherLabeler.PromptPath` | string | `prompts/teacher-prompt.md` |
| `TeacherLabeler.DailyCallCap` | int | 1000 |
| `TeacherLabeler.TimeoutSeconds` | int | 30 |
| `Retraining.IntervalMinutes` | int | 60 |
| `Retraining.HardCaseCountTrigger` | int | 500 |
| `Retraining.CountCheckIntervalMinutes` | int | 5 |
| `Retraining.HeldOutFraction` | float | 0.2 |
| `HardCaseDataset.Path` | string | `datasets/hard-cases.jsonl` |

### Canary

| Key | Type | Default |
|---|---|---|
| `Canary.CanaryModelPath` | string | `models/router-canary.zip` |
| `Canary.PercentageEnabled` | int | 10 |
| `Canary.RollingWindowSeconds` | int | 60 |
| `Canary.WatchdogPollIntervalSeconds` | int | 10 |
| `Canary.AutoRollbackThreshold` | float | 0.10 |
| `Canary.AutoRollbackEnabled` | bool | true |
| `Canary.MinBaselineSampleSize` | int | 50 |

---

## 8. Endpoints

All bind to `http://127.0.0.1:4000` (loopback only; no TLS).

| Method | Path | Purpose |
|---|---|---|
| POST | `/v1/chat/completions` | Main routing endpoint (OpenAI-compatible) |
| GET | `/v1/models` | Deduplicated model list from both upstreams |
| GET | `/health`, `/healthz` | Per-upstream reachability + last probe |
| GET | `/stats` | Queue depth, active counts, throughput, model versions |
| GET | `/canary` | Current canary state |
| POST | `/canary/promote` | Promote canary → primary; bumps `model_version` |
| POST | `/canary/rollback` | Set canary percentage to 0 (idempotent) |
| POST | `/canary/enable?percentage=N` | Set canary split (0–100) |

### POST /v1/chat/completions

Standard OpenAI body. Optional `task` field routes via the task table. Optional `correlation_id` honored if provided; else one is generated. Streaming via `stream:true` is fully supported (no quality fallback in streaming mode — see §5.5).

```bash
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"auto","task":"compiler_debug","messages":[{"role":"user","content":"why does this MLIR lowering fail?"}]}'
```

**Errors:** `400` malformed/unknown task · `502` upstream error · `503` `graph_indexing` + 122B down.

### GET /v1/models

Deduplicated; first-seen wins on duplicate `id`. If both upstreams down: `200 + {"object":"list","data":[]}` (never 503). mlx_lm advertises models by local filesystem path (e.g., `/Users/ohama/llm-system/models/qwen36-35b`), not HF id.

### GET /health

```json
{
  "qwen35b":  { "reachable": true,  "last_probed_at": "2026-05-09T03:40:00.000Z" },
  "qwen122b": { "reachable": false, "last_probed_at": "2026-05-09T03:39:50.000Z" }
}
```

### GET /stats

```json
{
  "active_122b": 1, "queue_depth_122b_high": 0, "queue_depth_122b_low": 2,
  "active_35b": 3, "requests_per_sec": 4.2, "avg_latency_ms_60s": 1850.0,
  "semaphore_available": 0, "baseline_version": "v3", "canary_version": null,
  "canary_percentage": 0, "canary_active": false,
  "quality_check_hits_finish_reason": 4,
  "quality_check_hits_length": 12,
  "quality_check_hits_entropy": 1,
  "quality_check_hits_keyword": 7
}
```

`semaphore_available: 0` = 122B at concurrency cap; new 122B-bound requests queue.

**Phase 15 — quality check hit counters** (all `int64`, process-lifetime, never reset):

| Field | Description |
|---|---|
| `quality_check_hits_finish_reason` | Fallbacks triggered by `finish_reason` match (e.g. `"length"`, `"content_filter"`) |
| `quality_check_hits_length` | Fallbacks triggered by effective-length-below-threshold (Korean-aware) |
| `quality_check_hits_entropy` | Fallbacks triggered by Shannon entropy below `EntropyThreshold` |
| `quality_check_hits_keyword` | Fallbacks triggered by case-insensitive `BadKeywords` match |

```bash
# Spot which check is dominant in production
curl -s http://127.0.0.1:4000/stats | \
  jq '{quality_check_hits_finish_reason, quality_check_hits_length, quality_check_hits_entropy, quality_check_hits_keyword}'
```

### GET /canary

```json
{ "active": true, "percentage": 10, "baseline_version": "v3", "canary_version": "v3-canary" }
```

### POST /canary/promote

Copies `router-canary.zip` over `router.zip` and bumps version. Returns `{"status":"promoted","new_baseline_version":"v4"}`. Errors: `404` (no canary file) · `409` (retraining in progress).

---

## 9. Logs and Debugging

### 9.1 DecisionLog schema (`logs/decisions/YYYY-MM-DD.jsonl`)

One row per request. Daily rotation by filename. Auto-pruned after `DecisionLog.RetentionDays` (default 90).

```json
{
  "schema_version": 1,
  "correlation_id": "a3f8c2d1e9b74a5f8c2d1e9b7a",
  "timestamp": "2026-05-09T03:40:01.234Z",
  "target": "Qwen122B",
  "routing_reason": "task_table",
  "routing_algorithm": "ml",
  "latency_ms": 1924.5,
  "model_version": "v3",
  "fallback_used": false,
  "task_type": "compiler_debug",
  "prompt_hash": "sha256:abcdef1234567890abcdef1234567890",
  "prompt_korean_char_ratio": 0.12
}
```

| Field | Meaning |
|---|---|
| `schema_version` | Currently 1 |
| `correlation_id` | UUID; sticky for canary bucketing; joinable with operational log + trace log |
| `target` | `Qwen35B` or `Qwen122B` — model that actually served |
| `routing_reason` | `explicit_model:{alias}`, `explicit_task:{task}`, `default`, `ml`, `fallback_to_35b` (122B unreachable), `fallback_to_122b` (35B response quality-bad), or compounds (`ml;upstream_error`, `ml;cancelled`, `ml;stream_error`) |
| `routing_algorithm` | `ml` (only valid value; seam preserved for future) |
| `model_version` | `v1`, `v2`, ... baseline; `v3-canary` for canary cohort |
| `fallback_used` | `true` if reroute happened (either direction) |
| `prompt_hash` | SHA-256 of concat'd message content; first 12 hex = `prompt_uid` joining to TraceLog |
| `prompt_korean_char_ratio` | 0.0–1.0; bilingual routing diagnostic |

### 9.2 Operational log (`logs/operational/smart-router-{yyyyMMdd}[_{NNN}].log`)

Rolling daily files; 50 MB size cap (`_NNN` suffix when exceeded); auto-pruned after `Logging.RetentionDays` (default 30). Mirrors to stderr (launchd captures to `~/llm-system/services/logs/smart-router.err`).

Output template: `{Timestamp} [{Level:u3}] {SourceContext} [{correlation_id}] {Message}`.

```
2026-05-09T14:32:11.456+09:00 [INF] SmartRouter.Cli.Endpoints.ChatCompletions [abc12345...] /v1/chat/completions request received
2026-05-09T14:32:11.789+09:00 [WRN] SmartRouter.Cli.Adapters.QueueDispatcher [abc12345...] 122B queue depth=8 (high water)
```

`[-]` = no associated request (background services, startup). `[abc12345...]` = correlation_id.

### 9.3 Trace log (opt-in via `--trace-responses`) (`logs/trace/YYYY-MM-DD.jsonl`)

For end-to-end debugging of routing + fallback. Disabled by default (file not created).

```json
{
  "schema_version": 1,
  "correlation_id": "...",
  "prompt_uid": "abcdef123456",
  "prompt_hash": "sha256:abcdef123456...",
  "prompt_excerpt": "explain recursion in Python",
  "initial_target": "Qwen35B",
  "initial_response_excerpt": "TODO: implement this",
  "fallback_kind": "quality",
  "final_target": "Qwen122B",
  "final_response_excerpt": "Recursion is a function...",
  "total_latency_ms": 2350.0,
  "timestamp": "2026-05-09T03:40:01.234Z",
  "bad_reason": "keyword=TODO"
}
```

| Field | Type | Description |
|---|---|---|
| `fallback_kind` | string \| null | `"quality"` (35B bad → 122B retry), `"availability"` (122B down → 35B reroute), or `null` |
| `bad_reason` | string \| null | **Phase 15.** Format `"tag=value"` when quality fallback fired; `null` when response was judged good or fallback was availability-driven. Tags: `finish_reason` (e.g. `"finish_reason=length"`), `length` (e.g. `"length=12"`), `entropy` (e.g. `"entropy=1.85"`), `keyword` (e.g. `"keyword=TODO"`). |

**`bad_reason` operator workflows:**

```bash
# Which quality check fires most often? (aggregate by tag prefix)
jq -r 'select(.bad_reason != null) | .bad_reason | split("=")[0]' \
  logs/trace/$(date -u +%F).jsonl | sort | uniq -c | sort -rn

# Show all requests where entropy detection fired
jq 'select(.bad_reason | startswith("entropy=")) | {prompt_excerpt, bad_reason, final_target}' \
  logs/trace/$(date -u +%F).jsonl
```

**Privacy:** prompts and excerpts stored in plaintext, truncated to 200/500 chars. Operator's responsibility to manage retention. No auto-cleanup currently.

### 9.4 Common operator queries

```bash
# Tail live operational log
tail -f logs/operational/smart-router-$(date +%Y%m%d).log

# Find errors today
grep -E '\[(WRN|ERR)\]' logs/operational/smart-router-$(date +%Y%m%d).log

# Trace one request across both streams
CID=abc12345
grep "\[$CID\]" logs/operational/smart-router-*.log
jq "select(.correlation_id == \"$CID\")" logs/decisions/*.jsonl

# Count requests by target (today)
jq -r '.target' < logs/decisions/$(date +%F).jsonl | sort | uniq -c

# Count quality fallbacks (today)
grep '"routing_reason":"fallback_to_122b"' logs/decisions/$(date +%F).jsonl | wc -l

# Trace by prompt UID — see what 35B vs 122B said
PROMPT="explain recursion in Python"
UID=$(echo -n "$PROMPT" | sha256sum | cut -c1-12)
jq "select(.prompt_uid == \"$UID\") | {initial_target, fallback_kind, initial_response_excerpt, final_target, final_response_excerpt}" \
  logs/trace/$(date +%F).jsonl
```

### 9.5 Log levels

Set via `--log-level=LEVEL` (or `Serilog.MinimumLevel.Default`). Values: `verbose|debug|information|warning|error|fatal` (also `vrb|dbg|info|warn|err|ftl`). Invalid → fail-fast at startup.

| Level | Used for |
|---|---|
| Debug | per-request prompt features; queue ticket lifecycle |
| Information (default) | health transitions; canary promote/rollback; retrain start/end; startup banner |
| Warning | upstream 502/timeouts; queue high-water; cost cap thresholds |
| Error | unhandled BackgroundService exceptions; SSE write failures; teacher labeler malformed responses |

---

## 10. Hermes / Graphify Integration

### Hermes (latency-sensitive; no `task` field)

```jsonc
// hermes config
{ "openai": { "base_url": "http://localhost:4000/v1", "model": "auto" } }
```

Every Hermes request passes stages 1–2 unmatched; stage 3 (ML classifier) decides. Streaming + mid-stream cancellation fully supported (router detects `OperationCanceledException`, disposes upstream cleanly, logs `routing_reason` ending in `;cancelled`).

### Graphify (concurrency-protected; sends `task`)

```json
{ "model": "auto", "task": "graph_indexing", "messages": [...] }
```

`task` routes through stage 2, bypassing the ML classifier. 122B is gated to 1 in-flight request via SemaphoreSlim — parallel Graphify pipelines queue.

**graph_indexing no-fallback rule:** if 122B unreachable, returns HTTP 503 (not 35B reroute) — rerouting would corrupt the graph index. Graphify should retry once `/health` shows 122B back up.

```json
{
  "error": {
    "message": "Task 'graph_indexing' requires Qwen122B which is currently unreachable; fallback policy does not apply for graph_indexing.",
    "type": "model_unavailable",
    "correlation_id": "a3f8c2d1e9b74a5f8c2d1e9b7a"
  }
}
```

---

## 11. Operations

### 11.1 launchd setup

```bash
./scripts/deploy.sh           # publishes binary + assets to ~/llm-system/services/smart-router/
./scripts/install-launchd.sh  # copies plist to ~/Library/LaunchAgents/
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist
curl http://127.0.0.1:4000/health
```

**Stop / restart:**
```bash
launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist
```

Plist runs as the logged-in user. `KeepAlive: true` (auto-restart on crash); `RunAtLoad: true`; `ThrottleInterval: 30` (max 1 restart per 30s); `WorkingDirectory: /Users/ohama/llm-system/services/smart-router` (relative paths in `appsettings.json` resolve from here).

### 11.2 Tune ML routing

Edit `~/llm-system/services/smart-router/appsettings.json` → restart. Common knob: `Routing.ML.Threshold` (lower → more 122B). Loop B retrains in-place — see §11.4.

### 11.3 Canary workflow

```bash
# 1. Drop new model
cp /path/to/new-model.zip ~/llm-system/services/smart-router/models/router-canary.zip
# FileSystemWatcher arms in ~200ms. No restart.

curl http://127.0.0.1:4000/canary | jq .                              # confirm armed
curl -X POST 'http://127.0.0.1:4000/canary/enable?percentage=20'      # adjust split
curl -X POST http://127.0.0.1:4000/canary/promote                     # promote if good
curl -X POST http://127.0.0.1:4000/canary/rollback                    # roll back if bad
```

Watchdog auto-rolls-back if (canary_fallback_rate − baseline) > 0.10 over 60s. Disable via `Canary.AutoRollbackEnabled: false`.

### 11.4 Manual retrain

```bash
dotnet run --project src/SmartRouter.Cli -- --retrain
# or in production:
cd ~/llm-system/services/smart-router && dotnet SmartRouter.Cli.dll --retrain
```

Runs the full Loop B pipeline synchronously. If Validator rejects, `models/router.zip` is unchanged and rejection is logged to `logs/retraining-rejections.jsonl`.

**Seed before first retrain:**
```bash
dotnet fsi scripts/seed-hard-cases.fsx
dotnet run --project src/SmartRouter.Cli -- --retrain
```

### 11.5 Cold start (recover from corrupted model)

```bash
dotnet run --project src/SmartRouter.Cli -- --cold-start
# Backs up models/router.zip and datasets/ files with timestamp suffix,
# then auto-generates a fresh dummy classifier on the same startup.
# Recovery: mv models/router.zip.cold-start-backup-YYYYMMDD-HHMMSS models/router.zip
```

### 11.6 Reduce flapping

Default `Routing.Health.ConsecutiveFailureThreshold: 1` is aggressive. Raise to `2`–`3` if you observe brief `fallback_used: true` bursts that immediately recover. Trade-off: slower activation when 122B genuinely goes down.

---

## 12. CLI Flags

| Flag | Effect |
|---|---|
| `--port=N` | Override listen port (1024..65535; default 4000) |
| `--log-level=LEVEL` | `verbose`/`debug`/`information`/`warning`/`error`/`fatal` (or short aliases). Invalid → fail-fast |
| `--trace-responses` | Enable trace log to `logs/trace/<date>.jsonl` (off by default) |
| `--cold-start` | Backup `models/router.zip` + `datasets/*` with timestamp; regenerate dummy classifier on this run |
| `--retrain` | Run offline retrain pipeline once and exit |

The legacy `--trace` boolean flag was removed — use `--log-level=debug`. Using `--trace` raises a fail-fast migration error.

---

## 13. Troubleshooting

### `model_unavailable` returned for `graph_indexing`

`/health` shows `qwen122b.reachable: false`. Restart 122B:
```bash
launchctl unload ~/Library/LaunchAgents/com.ohama.qwen122b.plist
launchctl load -w ~/Library/LaunchAgents/com.ohama.qwen122b.plist
```
Wait `PollingIntervalSeconds` (default 10), check `/health`. This is intentional — graph_indexing must not silently degrade.

### `fallback_used` flapping (alternates true/false)

122B intermittently failing single health probe. Raise threshold:
```jsonc
"Routing": { "Health": { "ConsecutiveFailureThreshold": 2 } }
```

### Quality fallback firing too often (35B → 122B retry erodes latency wins)

Check rate: `grep '"routing_reason":"fallback_to_122b"' logs/decisions/$(date +%F).jsonl | wc -l`. If high:
1. Inspect `initial_response_excerpt` in trace log — if 35B's responses look fine but trip your `BadKeywords`, tune the keyword list.
2. Lower `Routing.ML.Threshold` so borderline prompts pre-route to 122B (avoiding the wasted 35B call).
3. Set `Routing.QualityFallback.Enabled: false` as a kill switch while investigating.

### Canary auto-rollback cascade (every promote → rollback ~60s later)

Canary genuinely worse than baseline. Options: retrain with more labeled data; raise `Canary.AutoRollbackThreshold` to 0.20 if delta is acceptable; or `AutoRollbackEnabled: false` and monitor manually.

### HF-id trap (upstream returns "model not found")

mlx_lm advertises models by local filesystem path, not HF id. Send `"model": "auto"`/`"35b"`/`"122b"` — the router maps aliases internally. Don't send raw HF ids like `Qwen/Qwen3-35B`.

### `dotnet not found` in launchd context

launchd doesn't inherit `~/.zshrc` PATH. Plist `ProgramArguments` must use absolute path:
```xml
<array>
  <string>/opt/homebrew/bin/dotnet</string>
  <string>/Users/ohama/llm-system/services/smart-router/SmartRouter.Cli.dll</string>
</array>
```
Re-run `./scripts/install-launchd.sh` and reload.

### Gatekeeper quarantine blocks the service

Service exits silently after `launchctl load`; no crash log:
```bash
xattr -dr com.apple.quarantine ~/llm-system/services/smart-router/
launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist
```

---

## 14. Further Reading

- `documentation/howto/` — implementation notes (priority queue on SemaphoreSlim, ML.NET train/test ordering, F# `task {}` cancellation, launchd traps, etc.)
- `.planning/docs/` — design references (distillation fallback origin; quality fallback test walkthroughs)
- `.planning/ROADMAP.md`, `.planning/REQUIREMENTS.md` — phase-by-phase goals; functional/non-functional traceability
- `archive/heuristic-baseline` branch + `v0.5-heuristic-baseline` tag — pre-ML routing snapshot
