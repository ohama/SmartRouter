---
phase: 11-deployment-documentation
plan: 03
type: execute
wave: 2
depends_on: [11-01]
files_modified:
  - README.md
autonomous: true

must_haves:
  truths:
    - "README.md exists at the repo root"
    - "A new operator who has never seen smart-router can use README.md alone to: (a) switch routing algorithm between heuristic and ML, (b) tune the heuristic threshold and keyword list, (c) interpret a DecisionLog JSONL row field-by-field, (d) connect Hermes (base_url, no task field), (e) connect Graphify (with task field including graph_indexing semantics), (f) run the canary promote/rollback workflow, (g) follow the launchd restart procedure"
    - "README.md documents all 8 endpoints (POST /v1/chat/completions, GET /v1/models, GET /health, GET /stats, GET /canary, POST /canary/promote, POST /canary/rollback, POST /canary/enable) with method, path, example curl, and example response"
    - "README.md describes both feedback loops: Loop A (real-time fallback signal → fallback_used → DecisionLog) and Loop B (FailureDetector → TeacherLabeler → HardCaseDataset → RetrainingService → ModelVersionProvider) with the path through which a hard case becomes a re-trained model"
    - "README.md documents the 7 Graphify task types (graph_indexing, compiler_debug, architecture_analysis, dependency_analysis, reasoning, retrieval, summary) with their target model and priority"
    - "README.md explains the graph_indexing no-fallback rule (122B-down → 503 model_unavailable, NEVER shadow-rebind to 35B)"
    - "README.md links the launchd plist file (deploy/com.ohama.smart-router.plist) and the two deploy scripts (scripts/deploy.sh, scripts/install-launchd.sh)"
    - "README.md is between 500 and 1500 lines of markdown (target ~800)"
  artifacts:
    - path: "README.md"
      provides: "Single operator-facing document covering architecture, configuration, endpoints, debugging, integrations, operations, troubleshooting"
      min_lines: 500
      contains:
        - "smart-router"
        - "Routing Pipeline"
        - "ML Feedback Loop"
        - "Configuration"
        - "Endpoints"
        - "DecisionLog"
        - "Hermes"
        - "Graphify"
        - "graph_indexing"
        - "launchctl load -w"
        - "deploy/com.ohama.smart-router.plist"
        - "scripts/deploy.sh"
        - "scripts/install-launchd.sh"
        - "/v1/chat/completions"
        - "/v1/models"
        - "/health"
        - "/stats"
        - "/canary"
        - "Routing.Algorithm"
        - "ComplexityThreshold"
        - "fallback_used"
        - "model_version"
        - "Canary"
        - "ConsecutiveFailureThreshold"
        - "AutoRollbackEnabled"
  key_links:
    - from: "README.md (Operations / launchd section)"
      to: "deploy/com.ohama.smart-router.plist"
      via: "explicit relative-path link from prose to the plist artifact"
      pattern: "deploy/com\\.ohama\\.smart-router\\.plist"
    - from: "README.md (Operations / launchd section)"
      to: "scripts/deploy.sh + scripts/install-launchd.sh"
      via: "explicit relative-path link from prose to the deploy scripts"
      pattern: "scripts/(deploy|install-launchd)\\.sh"
    - from: "README.md (Endpoints / Models)"
      to: "Endpoints/Models.fs (shipped by 11-01)"
      via: "GET /v1/models documented as 'returns deduplicated entries from both upstreams'"
      pattern: "GET /v1/models.*deduplicated|/v1/models.*both upstreams"
---

<objective>
Ship a single comprehensive `README.md` at the repo root that lets a new
operator (someone who has never seen smart-router) configure, run, debug,
and integrate the router with Hermes and Graphify — without having to read
any planning docs, source files, or summaries.

Purpose: ROADMAP success criterion #4 explicitly says: "a new operator can
follow the steps without asking for clarification." Phases 1–10 produced
working software but ZERO operator-facing documentation. Without this
README, the operator would have to reverse-engineer routing behavior from
source code or planning artifacts. With it, smart-router is operationally
turnkey.

Output:
- `README.md` at the repo root, ~800 lines of markdown, containing all 13
  sections enumerated below.

NO source-code changes. NO new tests. NO Core changes.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/STATE.md
@.planning/phases/11-deployment-documentation/11-CONTEXT.md
@.planning/phases/11-deployment-documentation/11-RESEARCH.md

# Key source files to consult when writing README content (read for accuracy, not for copying)
@src/SmartRouter.Cli/appsettings.json
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
@src/SmartRouter.Cli/Endpoints/Health.fs
@src/SmartRouter.Cli/Endpoints/Stats.fs
@src/SmartRouter.Cli/Endpoints/Canary.fs
@src/SmartRouter.Core/Routing.fs
@src/SmartRouter.Core/Heuristic.fs

# Phase summaries (rich domain context — distill into README sections)
# Read selectively; do NOT copy verbatim. Extract the operator-facing decisions.
@.planning/phases/06-real-ml-routing/
@.planning/phases/07-failure-detection-and-teacher-labeling/
@.planning/phases/08-retraining-loop/
@.planning/phases/09-canary-deployment/
@.planning/phases/10-health-fallback-and-graph-indexing-no-fallback/
</context>

<tasks>

<task type="auto">
  <name>Task 1: Author README.md with all 13 required sections (~800 lines)</name>
  <files>README.md</files>
  <action>
**Step 1.1 — Read source-of-truth files** to anchor the README content
against the actual codebase. Do this BEFORE writing any prose. Specifically:

1. `src/SmartRouter.Cli/appsettings.json` — every key that ends up in the
   "Configuration Reference" section.
2. `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — request/response
   shape, fallback pre-flight at lines ~229-268.
3. `src/SmartRouter.Cli/Endpoints/Health.fs` — /health response body shape.
4. `src/SmartRouter.Cli/Endpoints/Stats.fs` — /stats response body shape.
5. `src/SmartRouter.Cli/Endpoints/Canary.fs` — /canary GET/POST routes.
6. `src/SmartRouter.Cli/Endpoints/Models.fs` — /v1/models response (shipped
   by Plan 11-01; read it during execution to confirm response shape).
7. `src/SmartRouter.Core/Routing.fs` + `Heuristic.fs` — the actual
   routing pipeline order and heuristic logic.
8. `.planning/REQUIREMENTS.md` if it exists — to lift accurate requirement
   IDs (REL-01..04, ROUT-01..07, etc.) into the architecture section.

This is a documentation task; accuracy beats invention. If the source says
the heuristic threshold key is `Routing:ComplexityThreshold`, the README
must use exactly that name — not `Routing:Threshold` or
`HeuristicThreshold`.

**Step 1.2 — Write `README.md`** with exactly these 13 sections in this
order (matches research §7). Each section's required content is described
below; expand each into prose + code blocks + tables as appropriate.

Path: `/Users/ohama/projs/smart-router/README.md`

```markdown
# smart-router

[1-2 sentence elevator pitch.]

## Table of Contents
[link to all 13 sections]

---

## 1. What This Is

[3-4 sentences: F# .NET 10 gateway, two local Qwen models (35B + 122B),
intelligent task routing using heuristic + ML, canary deployment, real-time
feedback loop. State the problem it solves: clients (Hermes, Graphify) want
ONE OpenAI-compatible endpoint that auto-picks the right model.]

## 2. Architecture

[ASCII diagram showing:

   Hermes / Graphify  ──HTTP──▶  smart-router :4000
                                       │
                       ┌───────────────┴───────────────┐
                       ▼                               ▼
                qwen36-35b :8000              qwen122b :8001

Then briefly describe each layer:

- Hexagonal F# (Core has zero infra refs — Microsoft.ML, Serilog, HttpClient,
  AspNetCore, FSharp.SystemTextJson all confined to the Cli project).
- Two routing algorithms: heuristic (keyword + complexity threshold) and
  ML (bge-m3 int8 + ML.NET LbfgsLogisticRegression). Switch via
  `Routing.Algorithm` config key.
- Two feedback loops:
  - Loop A — real-time: any request that hits 122B-down falls back to 35B,
    sets `fallback_used=true` in the per-request DecisionLog.
  - Loop B — periodic: BackgroundService scans DecisionLog for
    fallback_used=true rows, asks the teacher (122B) to label them, writes
    them into hard-case dataset, retrains classifier, atomic File.Move
    swap.
- Canary deployment: 10% sticky cohort routed to `router-canary.zip`;
  watchdog auto-rollback on rolling-60s fallback delta > 10%.
]

## 3. Requirements

- macOS arm64 (Apple Silicon)
- .NET 10 SDK (`dotnet --version` must report 10.x)
- Two `mlx_lm.server` instances:
  - Qwen 3.6 35B at `http://127.0.0.1:8000`
  - Qwen 3.5 122B at `http://127.0.0.1:8001`
  (See [`local_llm_rig`](...) memory for setup; not covered here.)
- Optional for ML routing: bge-m3 int8 ONNX model files in `models/embed/`
  (run `scripts/download-models.sh` and `scripts/export-bge-m3-int8.sh`).

## 4. Quickstart

[8-step walkthrough — clone → restore → run → curl health → curl chat → see
DecisionLog row → install launchd → kill -9 + verify auto-restart.]

```bash
git clone <repo>
cd smart-router
dotnet restore
dotnet run --project src/SmartRouter.Cli &
curl http://127.0.0.1:4000/health
curl -X POST http://127.0.0.1:4000/v1/chat/completions \
    -H 'Content-Type: application/json' \
    -d '{"model":"35b","messages":[{"role":"user","content":"hi"}]}'
tail -1 logs/decisions/$(date +%F).jsonl | jq .
# Then: scripts/deploy.sh + scripts/install-launchd.sh + manual launchctl load
```

## 5. Routing Pipeline

### 5.1 Three-stage decision

[Describe in order: model override → task table → heuristic-OR-ML.]

### 5.2 Task table (Graphify-aligned)

| task                  | target | priority |
|-----------------------|--------|----------|
| graph_indexing        | 122B   | high     |
| compiler_debug        | 122B   | high     |
| architecture_analysis | 122B   | high     |
| dependency_analysis   | 122B   | low      |
| reasoning             | 122B   | low      |
| retrieval             | 35B    | low      |
| summary               | 35B    | low      |

### 5.3 Heuristic vs ML

[State `Routing.Algorithm` switches between them. Heuristic is
default-dormant fallback; ML is primary. Show how to flip via appsettings or
CLI flag `--routing-algorithm=ml`. State the heuristic logic concisely:
keyword match in `Routing.Keywords` OR character count > `ComplexityThreshold`
→ 122B; else 35B.]

### 5.4 Tuning

[How to add a keyword: edit `Routing.Keywords` array in appsettings.json,
restart router (or hot-reload on file change). How to lower the complexity
threshold: lower `Routing.ComplexityThreshold` integer (default 3 → routes
more requests to 122B; raise to send more to 35B).]

## 6. ML Feedback Loop

### 6.1 Loop A — real-time fallback signal

[Describe: ChatCompletions pre-flight checks IHealthProbe.IsReachable. If
122B is down AND task != graph_indexing, shadow-rebind to 35B with
fallback_used=true. (graph_indexing never falls back — see §11.) Every
request emits one DecisionLog JSONL row.]

### 6.2 Loop B — retraining loop

[Describe: RetrainingService is a BackgroundService with two PeriodicTimers
(daily check + hard-case-count threshold). When triggered: FailureDetector
extracts hard cases (rows with fallback_used=true), TeacherLabeler asks the
122B teacher to label each ("ROUTE_35B" or "ROUTE_122B"), writes to
`datasets/hard-cases.jsonl`, DatasetMerger blends 70/30 with the original
training set, Retrainer trains a new ML.NET model, Validator checks
`fallback_rate ≤ threshold`, atomic `File.Move(overwrite=true)` swaps in
the new model, ModelVersionProvider updates so subsequent DecisionLog rows
record the new model_version.]

### 6.3 Canary deployment lane

[Describe: `router-canary.zip` is detected via FileSystemWatcher. When
present, 10% of correlation_ids are routed to the canary classifier
(sticky via ContextualTargetingFilter — same correlation_id always lands
on the same cohort). CanaryWatchdog monitors rolling-60s fallback rate
delta; if delta > 0.10 with `AutoRollbackEnabled=true`, watchdog
auto-rollbacks. POST /canary/promote moves canary → primary; POST
/canary/rollback sets percentage to 0 (idempotent).]

## 7. Configuration Reference

[Walk through every key in appsettings.json. Suggested table form:]

| Section          | Key                              | Type    | Default | Description |
|------------------|----------------------------------|---------|---------|-------------|
| Upstreams        | Model35B                         | string  | http://127.0.0.1:8000 | 35B base URL |
| Upstreams        | Model122B                        | string  | http://127.0.0.1:8001 | 122B base URL |
| Routing          | Algorithm                        | string  | "ml"    | "heuristic" or "ml" |
| Routing          | ComplexityThreshold              | int     | 3       | Heuristic char-count threshold |
| Routing          | Keywords                         | string[]| [...]   | Keywords that force 122B |
| Routing          | TimeoutSeconds                   | int     | 300     | Per-request timeout |
| Routing          | TaskTable                        | object  | (above) | Task → model + priority map |
| Routing          | Health.PollingIntervalSeconds    | int     | 10      | HealthService probe period |
| Routing          | Health.ConsecutiveFailureThreshold| int    | 1       | Failures before unreachable |
| Queue            | MaxConcurrent122B                | int     | 1       | 122B concurrency cap |
| Queue            | FairnessK                        | int     | 10      | Anti-starvation high-priority quota |
| Queue            | PerRequestTimeoutSeconds         | int     | 60      | Queue wait timeout |
| ML               | ModelPath                        | string  | models/router.zip | Trained classifier |
| ML               | EmbedderModelPath                | string  | models/embed/bge-m3-int8.onnx | bge-m3 ONNX |
| ML               | ConfidenceThreshold              | float   | 0.5     | Above → 122B |
| Retrain          | TimerHours                       | int     | 24      | Retrain check period |
| Retrain          | HardCaseThreshold                | int     | 50      | Trigger on N new hard cases |
| Canary           | Percentage                       | int     | 10      | Cohort split |
| Canary           | AutoRollbackEnabled              | bool    | true    | Auto-rollback on watchdog trigger (Phase 10 flipped from false) |
| Canary           | RollingWindowSeconds             | int     | 60      | Watchdog rolling window |
| Canary           | FallbackDeltaThreshold           | float   | 0.10    | Auto-rollback delta gate |
| DecisionLog      | Directory                        | string  | logs/decisions | JSONL output dir |
| DecisionLog      | ChannelCapacity                  | int     | 1000    | In-memory buffer |
| TeacherLabeler   | Endpoint                         | string  | http://127.0.0.1:8001 | 122B URL for teacher |
| TeacherLabeler   | DailyCostCapUSD                  | float   | (n/a — local) | Cost cap (no-op locally) |

[Don't enumerate every key from research §7's "35 keys" — list the
operator-tunable ones above with the right names from
src/SmartRouter.Cli/appsettings.json. Skip internal keys like Logging
sub-trees.]

## 8. Endpoints

[Table with method + path + description, then per-endpoint subsection with
example curl + example response.]

| Method | Path                    | Description |
|--------|-------------------------|-------------|
| POST   | /v1/chat/completions    | Main routing endpoint (OpenAI-compatible) |
| GET    | /v1/models              | Deduplicated model list from both upstreams |
| GET    | /health                 | Per-upstream reachability + last probe timestamp |
| GET    | /stats                  | Queue depth, active count, average wait |
| GET    | /canary                 | Canary state (model version, percentage) |
| POST   | /canary/promote         | Promote canary → primary |
| POST   | /canary/rollback        | Rollback canary (set percentage 0) |
| POST   | /canary/enable          | Enable canary at given percentage |

[For each endpoint, show one curl example + one response example. Use
fenced code blocks. The /v1/models example MUST show the deduplicated
data array containing entries from both upstreams.]

## 9. Debugging

### 9.1 DecisionLog schema

[Table: every field in a DecisionLog JSONL row.]

| Field                      | Type    | Description |
|----------------------------|---------|-------------|
| schema_version             | int     | LOG schema version (currently 2) |
| correlation_id             | string  | UUID from CorrelationMiddleware |
| timestamp                  | string  | ISO 8601 UTC |
| route                      | string  | "qwen35b" or "qwen122b" |
| target                     | string  | "Qwen35B" or "Qwen122B" (DU repr) |
| reason                     | string  | model_override / task_table / heuristic / ml / fallback_to_35b |
| routing_algorithm          | string  | "heuristic" or "ml" |
| latency_ms                 | int     | End-to-end |
| model_version              | string  | Active classifier version (e.g., "v3" or "v3-canary") |
| fallback_used              | bool    | true when 122B-down → 35B shadow-rebind |
| prompt_text                | string  | Full prompt (used by Loop B) |
| prompt_hash                | string  | SHA-256 of prompt |
| prompt_korean_char_ratio   | float   | 0.0-1.0 |
| ml_confidence              | float?  | Present when ML decided |
| upstream_status            | string  | success / upstream_error / stream_error |

### 9.2 /health and /stats interpretation

[Show example responses; explain each field; show how to confirm 122B is
reachable; show how to read queue depth.]

### 9.3 Logs

- Operator host (launchd): `~/llm-system/services/logs/smart-router.log`
  (StandardOutPath) and `smart-router.err` (StandardErrorPath).
- Local dev (`dotnet run`): stderr (Serilog console sink).
- DecisionLog: `logs/decisions/YYYY-MM-DD.jsonl` relative to
  `WorkingDirectory`. Operator host path:
  `/Users/ohama/llm-system/services/smart-router/logs/decisions/`.

## 10. Hermes Integration

[Hermes Agent at ~/hermes-agent. Hermes does not send a `task` field — its
prompts are general-purpose. Routing falls through model_override (rare) →
task_table (skipped) → heuristic-OR-ML.]

```jsonc
// hermes config
{
  "openai": {
    "base_url": "http://localhost:4000/v1",
    "model": "auto"   // or one of "35b" / "122b" to force
  }
}
```

[Note: `model: "auto"` is the recommended setting; the router will pick.
`model: "35b"` or `model: "122b"` forces the choice. Streaming (`stream:
true`) is fully supported including mid-stream cancellation.]

## 11. Graphify Integration

[Graphify sends a `task` field for every request. The task table maps it to
a target model and priority. Graphify's primary task is `graph_indexing`,
which has a SPECIAL no-fallback rule: if 122B is down, the request returns
HTTP 503 + `{error: {type: "model_unavailable"}}` instead of shadow-
rebinding to 35B (which would corrupt the graph index).]

```jsonc
{
  "model": "auto",
  "task": "graph_indexing",   // → 122B, no fallback ever
  "messages": [...]
}
```

[Concurrency: 122B is gated to 1 in-flight request via SemaphoreSlim;
graph_indexing requests are high-priority and queue-jump non-graph_indexing
122B requests.]

## 12. Operations

### 12.1 launchd setup

[Reference: `deploy/com.ohama.smart-router.plist` (the LaunchAgent) and the
two scripts. Walk through the install flow:]

```bash
# 1. Publish + install assets (idempotent)
./scripts/deploy.sh

# 2. Install plist into ~/Library/LaunchAgents/ (does NOT auto-load)
./scripts/install-launchd.sh

# 3. Bring service up (manual — gives operator review opportunity)
launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist

# 4. Verify
curl http://127.0.0.1:4000/health

# 5. Take service down
launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist

# 6. View logs
tail -f ~/llm-system/services/logs/smart-router.log
```

[Document: ThrottleInterval=30 means restart attempts wait at minimum 30s;
KeepAlive=true means launchd restarts on any exit; RunAtLoad=true means it
starts immediately on `launchctl load`.]

### 12.2 Switch routing algorithm

[Show: edit `appsettings.json` → `Routing.Algorithm: "heuristic"` or
`"ml"`, then `launchctl unload + load` to restart. OR use CLI flag
`--routing-algorithm=heuristic` for one-off override (modify
ProgramArguments in plist temporarily for ad-hoc).]

### 12.3 Canary workflow

[promote / rollback / enable, with concrete curl examples. Reference:
existing CanaryService FileSystemWatcher detects `router-canary.zip` in
ML.ModelPath's directory.]

```bash
# Drop a new canary model
cp /path/to/new-model.zip ~/llm-system/services/smart-router/models/router-canary.zip

# Watcher arms; cohort routing kicks in within ~200ms.
curl http://127.0.0.1:4000/canary

# Promote canary → primary
curl -X POST http://127.0.0.1:4000/canary/promote

# Rollback (idempotent)
curl -X POST http://127.0.0.1:4000/canary/rollback

# Manually adjust split percentage
curl -X POST 'http://127.0.0.1:4000/canary/enable?percentage=20'
```

### 12.4 Manual retrain

[How to invoke the offline retrain CLI: `dotnet SmartRouter.dll --retrain`
(or `dotnet run -- --retrain` in dev). Show what it does.]

### 12.5 Tune ConsecutiveFailureThreshold

[Default 1 = mark unreachable after 1 failed probe. Raise to 2 in
production if probe-blip flapping is observed; the trade-off is slower
fallback activation.]

## 13. Troubleshooting

[Each issue: symptom + diagnostic command + fix.]

### model_unavailable returned for graph_indexing
- Symptom: Graphify gets 503 with `{error: {type: "model_unavailable"}}`.
- Diagnostic: `curl http://127.0.0.1:4000/health` — 122B reachable=false.
- Fix: Restart `mlx_lm.server` for 122B (`launchctl unload + load
  ~/Library/LaunchAgents/com.ohama.qwen122b.plist`).

### Fallback flapping (fallback_used flickers true/false)
- Symptom: Loop B retraining triggered too eagerly.
- Diagnostic: `tail logs/decisions/$(date +%F).jsonl | jq '.fallback_used'`
  shows alternation.
- Fix: Raise `Routing.Health.ConsecutiveFailureThreshold` from 1 → 2 or
  3.

### HF-id trap (wrong model id rejected)
- Symptom: Upstream returns "model not found" for what looks like a valid
  model id.
- Diagnostic: Check the actual id mlx_lm advertises:
  `curl http://127.0.0.1:8000/v1/models | jq '.data[].id'`. mlx_lm uses
  the local filesystem path as id (e.g.,
  `/Users/ohama/llm-system/models/qwen36-35b`).
- Fix: Use exactly the path-form id from `/v1/models`. The router's
  `tryParseModelId` heuristic prefers path-like ids — but if you
  explicitly send a HF-style id (e.g., `Qwen/Qwen3-35B`), mlx_lm 422s.

### Canary auto-rollback cascade (canary keeps getting rolled back)
- Symptom: Every promote → ~60s later, auto-rollback fires.
- Diagnostic: `curl http://127.0.0.1:4000/canary` — percentage drops to 0
  shortly after promotion. Check logs for "AUTO-ROLLBACK" Serilog event.
- Fix: The canary classifier is genuinely worse than the primary
  (fallback delta > threshold). Either re-train with more data or raise
  `Canary.FallbackDeltaThreshold` (default 0.10).

### dotnet not found in launchd context
- Symptom: `~/llm-system/services/logs/smart-router.err` shows
  "command not found".
- Diagnostic: `which dotnet` differs from `/opt/homebrew/bin/dotnet`.
- Fix: Update `ProgramArguments[0]` in
  `deploy/com.ohama.smart-router.plist` to the real path; re-run
  `scripts/install-launchd.sh`; `launchctl unload + load`.

### Gatekeeper quarantine on macOS
- Symptom: Service exits silently shortly after launch.
- Diagnostic: `xattr -lr ~/llm-system/services/smart-router/ | grep
  com.apple.quarantine`.
- Fix: `xattr -dr com.apple.quarantine ~/llm-system/services/smart-router/`.
```

**Step 1.3 — Validate the README**:

```bash
# Length sanity check
wc -l /Users/ohama/projs/smart-router/README.md
# expected: 500 ≤ lines ≤ 1500 (target ~800)

# All required terms present
P=/Users/ohama/projs/smart-router/README.md
for s in 'Routing Pipeline' 'ML Feedback Loop' 'Configuration' 'Endpoints' \
         'DecisionLog' 'Hermes' 'Graphify' 'graph_indexing' \
         'launchctl load -w' 'deploy/com.ohama.smart-router.plist' \
         'scripts/deploy.sh' 'scripts/install-launchd.sh' \
         '/v1/chat/completions' '/v1/models' '/health' '/stats' '/canary' \
         'Routing.Algorithm' 'ComplexityThreshold' 'fallback_used' \
         'model_version' 'Canary' 'ConsecutiveFailureThreshold' \
         'AutoRollbackEnabled'; do
    grep -q "$s" "$P" || { echo "MISSING: $s"; exit 1; }
done
```

**Implementation discretion**: the section content blocks above are
LOCKED for shape and required terms (verified by the grep checks). The
exact prose, table widths, sentence count per section, and code-example
specifics are at the executor's discretion. Aim for clarity and concrete
example values over abstract descriptions. Operators read READMEs for
"how do I do X" — answer that question per section, then move on.

NOTE on tone: this is an internal solo-dev project, not enterprise
documentation. No "stakeholder" voice. No "best practices" preambles. Get
to the curl example. Use second-person ("you") for instructions and
imperative for commands.
  </action>
  <verify>
```bash
# README exists at repo root
test -f /Users/ohama/projs/smart-router/README.md

# Length within bounds (500..1500 lines; target ~800)
LC=$(wc -l < /Users/ohama/projs/smart-router/README.md | tr -d ' ')
[ "$LC" -ge 500 ] && [ "$LC" -le 1500 ] || { echo "README length out of range: $LC"; exit 1; }

# All 13 section headers present (## 1. … through ## 13.)
P=/Users/ohama/projs/smart-router/README.md
for n in 1 2 3 4 5 6 7 8 9 10 11 12 13; do
    grep -qE "^## ${n}\.|^# ${n}\." "$P" || { echo "MISSING section heading $n"; exit 1; }
done

# Required terms (verified against must_haves.contains list above)
for s in 'smart-router' 'Routing Pipeline' 'ML Feedback Loop' 'Configuration' \
         'Endpoints' 'DecisionLog' 'Hermes' 'Graphify' 'graph_indexing' \
         'launchctl load -w' 'deploy/com.ohama.smart-router.plist' \
         'scripts/deploy.sh' 'scripts/install-launchd.sh' \
         '/v1/chat/completions' '/v1/models' '/health' '/stats' '/canary' \
         'Routing.Algorithm' 'ComplexityThreshold' 'fallback_used' \
         'model_version' 'Canary' 'ConsecutiveFailureThreshold' \
         'AutoRollbackEnabled'; do
    grep -q "$s" "$P" || { echo "MISSING term: $s"; exit 1; }
done

# All 7 Graphify task types named
for t in graph_indexing compiler_debug architecture_analysis \
         dependency_analysis reasoning retrieval summary; do
    grep -q "$t" "$P" || { echo "MISSING task type: $t"; exit 1; }
done

# All 8 endpoints documented
for e in '/v1/chat/completions' '/v1/models' '/health' '/stats' '/canary' \
         '/canary/promote' '/canary/rollback' '/canary/enable'; do
    grep -q "$e" "$P" || { echo "MISSING endpoint: $e"; exit 1; }
done

# Loop A + Loop B both described
grep -q 'Loop A' "$P"
grep -q 'Loop B' "$P"

# graph_indexing no-fallback rule mentioned (REL-04 — 122B-down → 503 model_unavailable)
grep -q 'model_unavailable' "$P"
grep -E -q '503' "$P"

# launchctl unload (operator must be able to take it down)
grep -q 'launchctl unload' "$P"

# No source-code changes (this is a docs-only plan — except for adding README itself)
git diff --name-only -- 'src/' 'tests/' 'deploy/' 'scripts/' | wc -l | tr -d ' '   # → 0
```
  </verify>
  <done>
- `README.md` exists at the repo root.
- 500–1500 lines (target ~800).
- All 13 section headings present.
- All 25 required terms present (per `must_haves.contains` list).
- All 7 Graphify task types named.
- All 8 endpoints documented with method + path.
- Both feedback loops (A and B) described.
- graph_indexing no-fallback rule documented (503 model_unavailable).
- launchctl load AND launchctl unload both shown.
- Zero non-README file changes (`git diff --name-only -- src/ tests/
  deploy/ scripts/` → empty).
  </done>
</task>

</tasks>

<verification>
Phase 11-03 verification — single check (already inside the task verify):

```bash
# README exists
test -f /Users/ohama/projs/smart-router/README.md

# Length sanity
LC=$(wc -l < /Users/ohama/projs/smart-router/README.md | tr -d ' ')
[ "$LC" -ge 500 ] && [ "$LC" -le 1500 ] && echo "README length OK ($LC lines)"

# Pure-docs invariant — no other file edits
[ "$(git diff --name-only -- 'src/' 'tests/' 'deploy/' 'scripts/' | wc -l | tr -d ' ')" -eq 0 ] && echo "Pure-docs OK"
```
</verification>

<success_criteria>
- [ ] `README.md` shipped at repo root, 500-1500 lines.
- [ ] All 13 sections present in correct order (What This Is →
      Troubleshooting).
- [ ] All locked terms grep-verified (Routing.Algorithm,
      ComplexityThreshold, fallback_used, model_version, AutoRollbackEnabled,
      ConsecutiveFailureThreshold, …).
- [ ] All 7 Graphify task types named.
- [ ] All 8 endpoints documented.
- [ ] graph_indexing no-fallback rule (503 model_unavailable) described.
- [ ] Loop A + Loop B both described with the artifact chain
      (DecisionLog → FailureDetector → TeacherLabeler → HardCaseDataset
      → RetrainingService → ModelVersionProvider).
- [ ] launchd setup section references both `deploy/com.ohama.smart-router.plist`
      and the two scripts (`scripts/deploy.sh`, `scripts/install-launchd.sh`).
- [ ] Pure-docs invariant: zero edits to `src/`, `tests/`, `deploy/`,
      `scripts/`.
</success_criteria>

<output>
After completion, create `.planning/phases/11-deployment-documentation/11-03-README-SUMMARY.md`
covering:
- Final README line count.
- Confirmation each of the 13 sections is present.
- Confirmation of pure-docs invariant (`git diff` outside README is empty).
- Any sections that diverged from the planned outline (and why).
- Any cross-references to deeper docs that the operator would benefit
  from in v2 (`documentation/operations/` sub-docs, deferred per L16).
</output>
