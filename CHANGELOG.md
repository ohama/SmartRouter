# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

Phase 17 — Hard Rules layer + Routing.Mode switch. v2.0 paradigm pivot:
the routing primary path becomes keyword Hard Rules → (Phase 18 sticky) →
(Phase 19 35B self-classify); the v1.x ML classifier remains compiled and
re-activatable with a one-line `appsettings.json` edit + restart.

### Added

- **Stage 0 Hard Rules pre-routing.** A pure keyword scan (`LLVM`, `MLIR`, `compiler`, `segfault`, `optimization`, `concurrency` — case-insensitive) runs **before** model override, task table, and the routing algorithm. Any keyword match routes immediately to Qwen 122B with `routing_reason="hard_rule"` and `priority=High`. Applies to both streaming and non-streaming branches. The keyword list is hardcoded in `src/SmartRouter.Core/HardRules.fs` — not operator-configurable, by design (safety mechanism). DecisionLog `routing_reason` gains the additive value `"hard_rule"`; schema_version=1 unchanged.
- **`Routing.Mode` config key (`appsettings.json`).** New string key with values `"selfrouting"` (v2.0 default) or `"ml"` (v1.x rollback). Invalid values fail startup with `InvalidOperationException` before Kestrel binds. Operator can flip modes via config edit + `launchctl kickstart -k gui/$(id -u)/com.ohama.smart-router` — no rebuild required.
- **`routing_algorithm="selfrouting"` value in DecisionLog.** New value alongside `"ml"` / `"ml-canary"`; emitted when `Routing.Mode="selfrouting"` is active. `model_version="selfrouting-v1"` for the Phase 17 stub; Phase 19 will adopt a prompt-hash-derived version.

### Changed

- **Default routing paradigm flipped from ML to selfrouting.** With `Routing.Mode="selfrouting"` (the new default), Phase 17 ships a STUB algorithm that returns Qwen 35B / `routing_reason="default"` for prompts that miss Hard Rules + model override + task table. Phase 19 replaces the stub with the real 35B SAFE/UNSAFE self-classify call. Operators who want v1.x ML routing behavior in the interim should set `Routing.Mode="ml"` in `appsettings.json`. ML adapters (`BgeM3Embedder`, `MlNetClassifier`, `RetrainingService`, `CanaryService`) remain DI-registered and running in **both** modes — `RetrainingService` continues accumulating hard cases so ML can be re-activated without retraining from scratch.
- **Routing pipeline grew from 3 stages to 4.** `Routing.routeRequest` now invokes Stage 0 Hard Rules before the existing model override + task table + algorithm stages. The cascade order is `Hard Rules → model override → task table → algorithm` (Phase 18 inserts sticky escalation in subsequent work).

### Notes

- DecisionLog `schema_version` remains **1**. All Phase 17 additions are additive enum values (`hard_rule`, `selfrouting`) on existing string fields — no field removals, no type changes.
- `configureServices` backwards-compat alias is preserved and inherits the new `Routing.Mode` behavior unchanged.
- Phase 14 quality fallback (35B → 122B retry on quality-bad responses), Phase 15 quality signal enrichment, and Phase 16 borderline judge (`Routing.Judge.Enabled`) all work unchanged in both `selfrouting` and `ml` modes.

---

### Added (Phase 18 — Session Store + Sticky Escalation)

- **`X-Session-Id` HTTP request header opt-in for session-aware routing.** Requests sharing
  a session ID get debugging continuity: once any request in the session routes to Qwen 122B
  (via initial routing, Hard Rule, OR quality-fallback escalation), subsequent requests in
  the same session route to 122B with `routing_reason="sticky_to_122b"`. Empty or absent
  header preserves v1.x stateless behavior — no sticky bucket is created.
- **`RouterRequest.SessionId : string` Core domain field.** 10th field; `""` sentinel means
  stateless (Phase 9 `CorrelationId` cascade pattern). Set from `X-Session-Id` header in
  `CorrelationMiddleware`; null/whitespace coalesces to empty string (Pitfall 7: never let
  stateless clients share one sticky bucket).
- **`SmartRouter.Core.Domain.SessionState`** BCL-only record — `LastModel`, `LastAccessedAt`,
  mutable `LastAccessSeq`. Stored in Cli adapter; Core domain-only so Phase 19 self-routing
  closure can pattern-match on `LastModel` without dragging Cli deps into Core (ARCH-01).
- **`SmartRouter.Cli.Adapters.SessionStore`** adapter — `ConcurrentDictionary`-backed store
  with `AddOrUpdate` 122B-wins concurrent merge (non-negotiable correctness invariant: a
  racing 35B write can never overwrite a 122B escalation), LRU cap on write, and TTL-aware
  `TryGet`.
- **`SessionTtlEvictionService` BackgroundService** (PeriodicTimer 5-minute sweep) removing
  entries older than `Routing.Session.TtlMinutes`. Triple-reg pattern (concrete singleton +
  `ISessionStore` alias + `AddHostedService`) mirrors `DecisionLogWriter`.
- **`Routing.Session.TtlMinutes`** and **`Routing.Session.MaxEntries`** `appsettings.json`
  keys (defaults 30 minutes, 10000 entries). CLIMutable binding; defensive defaults applied
  at consumption (`<= 0` → 30/10000).
- **`RoutingReason.StickyEscalation`** 8th DU case → DecisionLog `routing_reason=
  "sticky_to_122b"` (`schema_version=1` unchanged — additive enum value).
- **`CorrelationMiddleware` extension** reading `X-Session-Id` into
  `HttpContext.Items[SessionIdKey]`; null/whitespace coalesces to empty string sentinel.
- **Point B write** in `ChatCompletions` — `finalDecision.Target` written to session store
  after the full cascade (quality fallback + judge) resolves, so a 35B→122B escalation
  is correctly recorded and the next request in the session stickies to 122B (SES-07).
- **`SessionStoreTests.fs`** (unit) and **`StickyEscalationTests.fs`** (DI-integration)
  cover all 5 Phase 18 ROADMAP Success Criteria.

### Notes (Phase 18)

- DecisionLog `schema_version` stays at `1`. `sticky_to_122b` is an additive enum value
  on the existing `routing_reason` string field — no schema migration required.
- Session store is in-memory only. Restart clears all sessions. See §5.6.
- `X-Session-Id` header propagation from Hermes Agent is future work (HMRS-FUTURE-01;
  Phase 20 ships smart-router-side machinery + an opt-in fingerprint fallback).

---

### Added (Phase 19 — 35B Self-Routing, Stage 4 self-classify)

- **v2.0 self-routing paradigm (Stage 4 self-classify).** For non-streaming requests that
  reach the routing default stage without being decided by Hard Rules, an explicit override,
  or sticky session, the router now calls the 35B model itself via a dedicated
  `"selfrouter"` named HttpClient (5s timeout, 1 retry at 200ms, `max_tokens=8`,
  `temperature=0`) to classify the prompt as SAFE (route to 35B) or UNSAFE (escalate to
  122B). Streaming requests skip Stage 4 entirely — Hard Rules (Stage 0) + sticky
  escalation (Stage 3) still apply to streaming. Operators can rollback to v1.x ML routing
  by setting `Routing.Mode="ml"` in `appsettings.json` + restart (no rebuild required).
- **Operator-tunable classify prompt.** `prompts/self-router-prompt.md` shipped with the
  repo. Operator edits `{{PROMPT}}`-based template to refine SAFE/UNSAFE criteria; changes
  take effect on next restart. See README §5.7 and §7.
- **Prompt-hash LRU cache.** Identical prompts hit a 10,000-entry per-process cache
  (keyed by SHA-256 of the full conversation content); cache hits skip the HTTP round-trip.
  Bounded by `Routing.SelfRouter.MaxCacheEntries` (default 10000). Restart clears the cache.
- **DecisionLog enum values:** `routing_reason="self_route"` (Stage 4 verdict);
  `routing_algorithm="selfrouting"` (v2.0 cascade). `model_version` is set to
  `"selfrouting-{hex8}"` for self-routed decisions, where `{hex8}` is the first 8 hex chars
  of SHA-256 of `prompts/self-router-prompt.md` at startup — operators can detect
  prompt-template drift between restarts by watching this field in the DecisionLog.
  **`schema_version=1` unchanged** — all Phase 19 additions are additive enum values only;
  no field removals, no type changes.
- **`/stats` endpoint fields:** `selfrouter_cache_hits`, `selfrouter_cache_misses`,
  `selfrouter_call_count`, `selfrouter_skipped` (all `int64`, process-lifetime). Returns 0
  for all four when `Routing.Mode="ml"`. See README §8.
- **Configuration keys.** `Routing.SelfRouter.Endpoint` (default `""` → derives from
  `Upstreams.Model35B`), `Routing.SelfRouter.PromptPath` (default
  `"prompts/self-router-prompt.md"`), `Routing.SelfRouter.TimeoutSeconds` (default `5`),
  `Routing.SelfRouter.MaxCacheEntries` (default `10000`). All in `appsettings.json`; restart
  required after changes. See README §7.
- **ML dormant integration test.** `tests/SmartRouter.Tests/MlDormantTests.fs` boots
  `Routing.Mode="ml"` and asserts `RoutingAlgorithmRegistration.Name="ml"` — prevents silent
  v1.x ML-path regression across v2.x phases. Skip-guarded on hosts without ONNX embedding
  files (W4 pattern; confirmed by `File.Exists` guard).

### Notes (Phase 19)

- DecisionLog `schema_version` stays at `1`. All Phase 19 additions (`self_route` routing
  reason, `selfrouting` routing algorithm) are additive enum values on existing string fields.
  Readers that ignore unknown `routing_reason` / `routing_algorithm` values remain
  forward-compatible.
- Stage 4 self-classify uses the same 35B model as inference. The `"selfrouter"` named
  HttpClient has its own connection pool + timeout — it does not compete with the 122B
  `SemaphoreSlim(1)` gate or the 300s inference timeout.
- Fail-open: any classify failure (HTTP timeout, template missing, ambiguous response) falls
  through to Stage 5 default (35B). No request is dropped; `selfrouter_skipped` or
  `selfrouter_call_count` movements in `/stats` signal failures.

## [1.3.0] - 2026-05-11

122B-as-judge release. Borderline 35B responses (entropy/length band
edge) can now be verified by a 1-token call to 122B before falling
through to a full retry. Disabled by default — operators opt in after
inspecting Phase 1.2.0's `quality_check_hits_*` counters to see whether
the heuristic is firing too often or not often enough.

### Added

- **122B-as-Judge for Borderline Cases (OPT-IN).** When `Routing.Judge.Enabled = true`, 35B responses that pass the heuristic but fall in the entropy/length band edge get a 1-token verification call to 122B (`ROUTE_YES`/`ROUTE_NO`). Cached by `(prompt_hash, response_hash)` LRU (default 10000 entries). Streaming responses bypass the judge entirely. **Default OFF** — judge adds a 122B network call on every borderline case, so opt in after evaluating borderline rate via the existing `quality_check_hits_*` /stats counters.
- TraceLog fields `judge_called` / `judge_verdict` / `judge_latency_ms` (schema_version=1 unchanged — additive).
- `/stats` fields `judge_cache_hits` / `judge_cache_misses` / `judge_call_count` (all int64; process-lifetime; resolved null-safe so /stats keeps working when judge is disabled).
- Config block `Routing.Judge.*` with 5 keys: `Enabled` (bool, default `false`), `Endpoint` (string, default `""` — derives from `Upstreams.Model122B`), `PromptPath` (default `prompts/judge-prompt.md`), `TimeoutSeconds`, `MaxCacheEntries`.
- Operator-tunable judge prompt at `prompts/judge-prompt.md` with `{{QUESTION}}` and `{{RESPONSE}}` placeholders + `ROUTE_YES`/`ROUTE_NO` sentinels.

## [1.2.0] - 2026-05-10

Quality signal enrichment release. The fallback heuristic now reads
five signals instead of two — most notably `finish_reason="length"`
(catches truncated responses) and Shannon entropy (catches token loops).
Existing config still works; new behaviors activate silently with
sensible defaults.

### Changed

- **Quality fallback now triggers on 5 dimensions instead of 2 (silent enable).** Existing `Routing.QualityFallback` config (`Enabled`, `MinResponseLength`, `BadKeywords`) is unchanged. Two new config keys with defaults activate automatically:
  - `finish_reason="length"` or `"content_filter"` now triggers fallback (new Stage 1). Operators on mlx_lm will see more 35B→122B retries when 35B hits its token limit.
  - Shannon entropy detection (default threshold 2.5) catches token-loop responses like `"the the the..."` (new Stage 3).
  - Korean-aware effective length: Hangul-syllable content is inflated by `koreanRatio × 0.8` before comparing to `MinResponseLength` — Korean responses are less likely to false-positive as "too short" (Stage 2 refinement).
  - `BadKeywords` matching is now **case-insensitive** (was case-sensitive in Phase 14). The keyword `"TODO"` now matches `"todo"`, `"TODO"`, `"Todo"`, etc.
  - Detection cascade is cheap-first (finish_reason → length → entropy → keyword) with early exit at first match.
- Operators wanting Phase 14's narrower trigger behavior can restore it by setting:
  ```jsonc
  "Routing": { "QualityFallback": { "BadFinishReasons": [], "EntropyThreshold": 0.01 } }
  ```
  (`BadFinishReasons: []` disables finish_reason checks; `EntropyThreshold: 0.01` requires near-zero entropy to fire — effectively disabled.)

### Added

- TraceLog field `bad_reason` (string | null) — records which quality check fired and why. Format: `"tag=value"` (e.g. `"finish_reason=length"`, `"length=12"`, `"entropy=1.85"`, `"keyword=TODO"`). `null` when response judged good or fallback was availability-driven. Operator jq: `jq -r 'select(.bad_reason != null) | .bad_reason | split("=")[0]'` to aggregate by detection tag.
- `/stats` endpoint exposes 4 new process-lifetime counters (all `int64`): `quality_check_hits_finish_reason`, `quality_check_hits_length`, `quality_check_hits_entropy`, `quality_check_hits_keyword`. Use to identify which check dominates in production.
- Config keys `Routing.QualityFallback.BadFinishReasons` (string[]; default `["length","content_filter"]`) and `Routing.QualityFallback.EntropyThreshold` (float; default `2.5`).

## [1.1.1] - 2026-05-10

Patch release fixing a critical bug in the v1.1.0 quality fallback path.

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
