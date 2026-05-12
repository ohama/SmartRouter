# Smart Router

## What This Is

An F# .NET 10 OpenAI-compatible LLM gateway that fronts two local Qwen models
on a Mac and routes each request to the right one. It serves
`http://localhost:4000/v1/chat/completions` and forwards to Qwen 35B
(`localhost:8000`) for fast / lightweight work or Qwen 122B
(`localhost:8001`) for heavy reasoning. Two distinct consumers depend on it:

- **Hermes Agent** (Nous Research; sits **above** smart-router as the upper layer per v2.0 architecture) — latency-sensitive interactive use; will propagate session_id via header for sticky escalation continuity in v2.0.
- **Graphify** (yet to be built) — task-typed Graph-RAG / compiler / multi-file
  reasoning workloads; sends an explicit `task` field (`graph_indexing`,
  `retrieval`, `summary`, `reasoning`, `compiler_debug`,
  `architecture_analysis`, `dependency_analysis`); needs concurrency
  protection on 122B and quality-correct routing.

Both clients use the same OpenAI-compatible wire format.

**v1.x (shipped):** Pipeline was `explicit-model-override → explicit-task → ML classifier (primary) → 35B-aggressive default`. ML classifier (bge-m3 int8 + ML.NET LR) with closed-loop retraining, canary deployment, quality fallback, 122B-as-judge. v1.3.0 final.

**v2.0 (shipped 2026-05-12):** Selfrouting paradigm replaced ML in the routing path. Pipeline now: `Hard Rules (Stage 0 keywords) → explicit model override → explicit task → sticky session (X-Session-Id or opt-in fingerprint) → 35B self-classify (SAFE/UNSAFE 1-token, non-streaming only) → default 35B`. ML code retained but routing-path dormant; `Routing.Mode = "ml" | "selfrouting"` config switch enables one-config rollback. Hermes Agent integrates above via `X-Session-Id` header propagation for debugging continuity (Hermes-side PR tracked as HMRS-FUTURE-01).

## Core Value

**Route every request to the model best suited to it — fast 35B for simple
work, expensive 122B only when the task or signals justify it — while
protecting 122B from concurrent overload.**

When tradeoffs arise:
- For Hermes (no task field): bias toward 35B; latency wins over thoroughness.
- For Graphify (task field present): the task type wins. `graph_indexing` and
  `compiler_debug` always go to 122B even at the cost of queue wait;
  `retrieval` and `summary` always go to 35B.
- Across both: 122B concurrency is capped at 1; everyone else queues. Better
  to make a Hermes call wait 200ms than to thrash 122B and starve a Graphify
  graph index.

## Current Milestone: (none — v2.1 shipped 2026-05-12; awaiting next milestone via `/gsd:new-milestone`)

**Last shipped:** v2.1 Hermes-less Session Tiering — three-tier session-key cascade (header → sysprompt → content fingerprint) replacing v2.0's network IP+UA fingerprint; HMRS-02 fully deleted; new `/stats` extraction-source counters; both `Routing.Mode` values use the new tiers (verified by TC-7 executable DI assertion). 4 phases (21-24) / 7 plans / 187 passing tests. See `.planning/MILESTONES.md` (top entry) and `.planning/milestones/v2.1-ROADMAP.md` for full record.

**Session-key cascade as shipped in v2.1:**
1. `X-Session-Id` HTTP header (Tier 1; explicit always wins)
2. System-prompt regex extract (Tier 2; `^Session ID:[ \t]*(\S+)` against first System message; only fires when operator runs Hermes with `--pass-session-id`)
3. Content fingerprint (Tier 3; SHA-256(truncate(system) + "|||" + truncate(firstUser))[0..15]; deterministic; survives multi-turn / continuation)

IP+UA network fingerprint (v2.0 HMRS-02) is fully deleted — pre-deletion snapshot preserved at `archive/v2.0-network-fingerprint` branch + `v2.0-network-fingerprint` annotated tag at commit `d4797e7`.

## Requirements

### Validated

<!-- Shipped and confirmed valuable in v1.x. -->

**API surface (v1.0–v1.3)**

- ✓ OpenAI-compatible `POST /v1/chat/completions` on `localhost:4000` — v1.0
- ✓ Standard OpenAI field parsing + unknown field preservation — v1.0
- ✓ `GET /health`, `GET /v1/models`, `GET /stats` (21+ flat snake_case fields) — v1.0
- ✓ `GET /canary`, `POST /canary/{promote,rollback,enable}` — v1.0
- ✓ Optional non-OpenAI `task` field — v1.0

**Routing pipeline (v1.0–v1.3)**

- ✓ Three-stage decision pipeline (explicit model override → task table → ML classifier) — v1.0
- ✓ 7-task table (graph_indexing/compiler_debug/architecture_analysis/dependency_analysis/reasoning → 122B; retrieval/summary → 35B) — v1.0
- ✓ ML routing: bge-m3 int8 + ML.NET LbfgsLogisticRegression — v1.0
- ✓ `Routing.ML.Threshold` tunable — v1.0
- ✓ First-run bootstrap auto-generates dummy classifier — v1.0
- (✓ Heuristic routing retired Phase 12 — v1.0; preserved at `archive/heuristic-baseline` branch)

**Concurrency + Streaming (v1.0)**

- ✓ `SemaphoreSlim(1)` on 122B with two-level priority queue + fairness counter — v1.0
- ✓ 35B bypasses queue — v1.0
- ✓ SSE pass-through with mid-stream cancellation + `[DONE]` sentinel injection — v1.0
- ✓ Cancellation token propagation client → router → upstream — v1.0

**Reliability (v1.0–v1.1)**

- ✓ HealthService BackgroundService (10s probes; ConsecutiveFailureThreshold) — v1.0
- ✓ 122B-unreachable fallback to 35B (except `graph_indexing` → HTTP 503) — v1.0
- ✓ Transient retry policy (non-streaming only) — v1.0
- ✓ Quality fallback (35B → 122B retry on bad response, non-streaming) — v1.1
- ✓ `extractAssistantText` fix (issue #13) — v1.1.1

**Auto-retraining loop (v1.0)**

- ✓ FailureDetector + TeacherLabeler + DatasetMerger + Retrainer + Validator pipeline — v1.0
- ✓ PredictionEnginePool hot-swap (`watchForChanges:true`) — v1.0
- ✓ Daily cost cap on teacher calls — v1.0
- ✓ Manual offline retraining (`--retrain` CLI flag) — v1.0

**Canary deployment (v1.0)**

- ✓ `models/router-canary.zip` FileSystemWatcher arms canary — v1.0
- ✓ Sticky 10/90 split by `correlation_id` (ContextualTargetingFilter) — v1.0
- ✓ Auto-rollback watchdog (rolling-60s fallback rate; configurable threshold) — v1.0
- ✓ `model_version` distinguishes baseline vs canary in DecisionLog — v1.0

**Quality enrichment (v1.2)**

- ✓ Quality fallback 5-dimension cascade (finish_reason + case-insensitive keywords + Korean length boost + Shannon entropy + cheap-first cascade) — v1.2
- ✓ `bad_reason` TraceLog field with `tag=value` serialization — v1.2
- ✓ `/stats` `quality_check_hits_*` counters — v1.2

**Quality verification (v1.3)**

- ✓ 122B-as-judge for borderline cases (OPT-IN; `Routing.Judge.Enabled=false` default) — v1.3
- ✓ LRU cache by `(prompt_hash, response_hash)` — v1.3
- ✓ 3 trace fields (`judge_called`/`judge_verdict`/`judge_latency_ms`) — v1.3
- ✓ 3 `/stats` counters (`judge_cache_hits`/`_misses`/`_call_count`) — v1.3

**Observability (v1.0–v1.3)**

- ✓ Structured DecisionLog JSONL (12-field schema) — v1.0
- ✓ Operational log rolling Serilog file (50MB cap, 30-day retention) — v1.0
- ✓ Opt-in TraceLog JSONL with `--trace-responses` (16 fields v1.3) — v1.1
- ✓ Correlation ID propagation across all log streams — v1.0
- ✓ `--cold-start` CLI recovery flag — v1.1
- ✓ `--log-level` CLI flag with 6 levels + aliases — v1.0

**Architecture (v1.0)**

- ✓ Hexagonal: pure Core (BCL-only, ARCH-01) + Cli adapters — v1.0
- ✓ `task {}` only in Core (no `async {}`); ARCH-02 — v1.0
- ✓ Stateless service (DI-scoped singletons) — v1.0
- ✓ Extension seams for future providers — v1.0

**Operability (v1.0)**

- ✓ launchd plist + deploy + install scripts — v1.0
- ✓ README at repo root (operator-facing; ~669 lines after v1.1 condensation) — v1.0
- ✓ Loopback-only binding to `127.0.0.1:4000` — v1.0

**Testing (v1.0–v1.3)**

- ✓ 113 tests passing (62 plans worth of test coverage); routing pipeline + SSE streaming + concurrency gate + ML classifier + retraining + canary + health/fallback + deployment + quality fallback + trace + /stats wire — v1.3

**v2.0 Selfrouting + Session-Aware (2026-05-12)**

- ✓ `Routing.Mode = "selfrouting" | "ml"` config switch with fail-fast validation — v2.0 (MODE-01..04)
- ✓ Hard Rules Stage 0 keyword pre-routing (LLVM/MLIR/compiler/segfault/optimization/concurrency → 122B; case-insensitive; wins over explicit override) — v2.0 (HR-01..06)
- ✓ `RouterRequest.SessionId` Core field + `ISessionStore` adapter (ConcurrentDictionary + 122B-wins merge + LRU + PeriodicTimer TTL eviction) — v2.0 (SES-01..09)
- ✓ Sticky escalation cascade (Stage 3, after Hard Rules + explicit overrides, before self-classify) — v2.0 (SES-05/06)
- ✓ 35B self-classify routing (named "selfrouter" HttpClient + SAFE/UNSAFE 1-token + safety-biased parser + prompt-hash LRU cache + operator-tunable `prompts/self-router-prompt.md`) — v2.0 (SR-01..09)
- ✓ Streaming branch intentionally skips self-classify (latency budget; Hard Rules + sticky still apply) — v2.0 (SR-06)
- ✓ ML dormant integration test prevents silent drift (`MlDormantTests.fs`; skip-guarded by ONNX presence) — v2.0 (SR-09)
- ✓ `X-Session-Id` header opt-in (CorrelationMiddleware) — v2.0 (HMRS-01)
- ✓ Opt-in fingerprint fallback (`Routing.Session.FingerprintEnabled` default `false`; SHA-256(RemoteIp+UA)[0..15]) — v2.0 (HMRS-02)
- ✓ README §10 Hermes Integration rewritten for v2.0 paradigm (with NOT-SAFE-BEHIND-REVERSE-PROXIES warning) + `scripts/smoke-hermes-session.sh` operator E2E driver — v2.0 (HMRS-03/04)
- ✓ DecisionLog schema_version=1 preserved (additive `routing_reason` values: `hard_rule`, `sticky_to_122b`, `self_route`) — v2.0
- ✓ ARCH-01 preserved across v2.0 (`HardRules.fs` only new Core file; all other adapters in Cli) — v2.0
- ✓ 175 tests passing + 18 ignored (+62 new tests across Phases 17-20) — v2.0

**v2.1 Hermes-less Session Tiering (2026-05-12)**

- ✓ `SmartRouter.Cli.Adapters.HermesSessionExtract` module: Tier 2 system-prompt regex parse (`^Session ID:[ \t]*(\S+)` Multiline; module-level pre-compiled `Regex`; case-sensitive; `[ \t]*` chosen over `\s*` to prevent cross-line match) — v2.1 (HSP-01..04)
- ✓ `SmartRouter.Cli.Adapters.ContentFingerprint` module: Tier 3 SHA-256 prefix hash of `truncate(system) + "|||" + truncate(firstUser)`; 16-char lowercase hex; deterministic; >4000-char truncation; thread-safe per-call `use sha = SHA256.Create()` — v2.1 (CFP-01..04)
- ✓ Three-tier cascade in `ChatCompletions.fs resolveSessionCascade` (between `mapWireToRequest` and `routeRequest`): header → sysprompt → content fingerprint; first non-empty wins — v2.1 (TIER-01..05)
- ✓ Cascade fires in BOTH `Routing.Mode = "selfrouting"` and `"ml"` — verified by TC-7 executable DI assertion (Phase 24 gap closure; `SessionKeyCascadeTests.fs:264-281`) — v2.1 (TIER-04)
- ✓ `/stats` `session_extraction_source_header / _sysprompt / _content` flat Int64 fields backed by `SessionCascadeStats` (`Interlocked.Increment` + `Volatile.Read`); DI-registered unconditionally — v2.1 (OBS-01)
- ✓ HMRS-02 IP+UA fingerprint code fully deleted: `Routing.Session.FingerprintEnabled` config key, `CorrelationMiddleware.fs` SHA-256(RemoteIp+UA) block, `HermesFingerprintTests.fs` 8 tests, README §10 PROXY-01 callout, README §7 row — all gone — v2.1 (MIG-01..05)
- ✓ `archive/v2.0-network-fingerprint` branch + `v2.0-network-fingerprint` annotated tag at `d4797e7` preserve pre-deletion snapshot — v2.1 (MIG-06)
- ✓ README §10 rewritten for v2.1 paradigm with four operator `--pass-session-id` enablement options (CLI arg, shell alias, env var, wrapper script); §7 `FingerprintEnabled` row removed; §8 three new counter rows added (table + JSON example + jq monitoring snippet); §9.1 confirmed schema-unchanged (cosmetic "Phase 17–19" → "Phase 17–22" bump) — v2.1 (DOC-01..04)
- ✓ `SessionKeyCascadeTests.fs` 7 testCases (TC-1..TC-7) covering header-wins / sysprompt fallback / content fingerprint / determinism / sticky escalation continuity / counter increments / ml-mode DI resolution — v2.1
- ✓ 187 tests passing + 18 ignored + 0 failed (+12 net across Phases 21-24; +7 HSP + +7 CFP + +6 SessionKeyCascadeTests + 1 TC-7 − 8 HermesFingerprintTests − 1 unrelated drift) — v2.1
- ✓ `DecisionLog schema_version=1` unchanged in v2.1 (session_id flows through existing SES-04 channel; no new `routing_reason` values) — v2.1

### Active

<!-- Next milestone not yet started. Run /gsd:new-milestone to begin questioning → research → requirements → roadmap for the next version. -->

(Empty — pending next milestone scoping)

### Out of Scope

<!-- Explicit boundaries. Includes reasoning to prevent re-adding. -->

- **Circuit breaker as a distinct mechanism** — retry policy + health probing covers immediate failures. Add later only if a recurring failure mode is observed that needs explicit "open" state.
- **Rate limiting** — single host, two known clients, no abuse vector. Defer until a real abuse / runaway-loop scenario appears.
- **Auth / API keys** — loopback-only (`localhost:4000`); no external exposure, no auth surface.
- **Prometheus `/metrics` endpoint** — `/stats` covers v1 observability needs. Add Prometheus exposition only if a scraper actually arrives.
- ~~**ML / learned routing** — heuristics only for v1; revisit after `/stats` + structured logs accumulate enough decision data to train against.~~ **(Folded into v1 on 2026-05-08, Phases 4-9.)** Heuristic stays as permanent baseline + emergency fallback when ML breaks. ML algorithm is an additional option selected via `Routing.Algorithm` config key. See `~/projs/smart-router-distillation/docs/handoff-to-smart-router.md` for the integration strategy and the operator's decision rationale.
- **Concrete provider implementations beyond Qwen 35B / 122B** — Claude / OpenAI / DeepSeek / Gemini / Gemma / Llama get extension *seams* only, no live integrations.
- **Docker / docker-compose deployment** — Mac-only via launchd; matches blueCode operational pattern. Container packaging deferred until a reason to run elsewhere appears.
- **Windows support** — Mac-only; mirrors blueCode's Unix-path heuristic in `tryParseModelId`.
- **Persistence / session state** — router is stateless per request; conversation memory lives in the client (Hermes / Graphify).
- **xUnit / FsUnit** — test framework brief mentioned them; we use Expecto only (matches blueCode, single test framework across both projects).
- **Channels / TPL Dataflow as a baseline pattern** — graphify prompt suggested them; v1 uses a simple priority queue + SemaphoreSlim. Reach for Dataflow only if v2 introduces fan-out pipelines that justify it.

## Context

**Two consumers, one router.**

[Hermes Agent](https://github.com/NousResearch/hermes-agent) (local clone at
`~/hermes-agent`) connects through its `custom` provider plugin
(`plugins/model-providers/custom`) with `base_url=http://localhost:4000/v1`.
Hermes calls `chat.completions.create(stream=True, ...)` by default
(`run_agent.py:6784`) and never sends a `task` field — its routing path is
purely heuristic + `model`-override-aware.

Graphify is a Graph-RAG / compiler-reasoning system the operator plans to
build later. Per `graphify_smart_router_prompt.md`, it will send an explicit
`task` field on each request. Confirmed v1 routes:

| Task | Model |
|------|-------|
| `graph_indexing` | 122B (high prio; **never falls back** — must fail on 122B unavailable) |
| `compiler_debug` | 122B (high prio) |
| `architecture_analysis` | 122B (high prio) |
| `dependency_analysis` | 122B (low prio) |
| `reasoning` | 122B (low prio) |
| `retrieval` | 35B |
| `summary` | 35B |

Heuristic fallback (Hermes path, or Graphify with no `task`): complex-keyword
hits (recursive, dependency, lowering, MLIR, LLVM, compiler, architecture,
type inference, graph relation, closure conversion, cross-file, multi-file,
etc.) → 122B; long contexts → 122B; otherwise 35B.

**Why now.** Qwen 122B is the canonical heavy model in the operator's local
LLM rig, but it's slow (~240s cold-start, ~45 GB RSS, generation latency
dominates interactive sessions) and expensive to run concurrently. Qwen 35B is
already running on `localhost:8000` as the standby/rollback service. Both
launchd plists already exist
(`~/Library/LaunchAgents/com.ohama.qwen{35b,122b}.plist`). The router unlocks
"keep both loaded, route correctly between them" for two very different
workloads sharing the same host.

**Companion project: blueCode** (`/Users/ohama/projs/blueCode`). Existing F#
.NET 10 hexagonal codebase that already speaks to both Qwen ports. We **copy
adapters** from blueCode: `QwenHttpClient.fs` (plus its HF-id parsing trap
defenses), `Adapters/Json.fs`, `Adapters/Logging.fs`. Core gets rewritten
cleanly because blueCode's Core is an agent loop, not a router. Hard-won
operational knowledge from blueCode is load-bearing context:

- mlx_lm.server HF-fallback trap: send the local path (not the HF id) in the
  POST body's `model` field; otherwise the server overwrites the loaded
  Instruct tokenizer with a Base Coder one and responses become FIM-mode
  garbage. `tryParseModelId` in blueCode's `QwenHttpClient.fs` resolves this
  by preferring `data[0]` entries that start with `/`.
- Both servers launch with `--chat-template-args '{"enable_thinking": false}'`
  to suppress `<think>...</think>` tokens that would break strict-JSON
  parsing. The router doesn't need to do anything special here.
- HttpClient timeout: 300s (covers 122B cold-start up to 240s after
  `launchctl kickstart`).
- Sampling-parameter defaults Qwen 3.5 expects when client omits them:
  `temperature=0.7, top_p=0.8, top_k=20, presence_penalty=0.0` (non-thinking
  coding defaults). The router passes client values through; only fills these
  when client omits them.

**Code-style invariants from blueCode (load-bearing).**
- Pure Core: no Serilog / Spectre / HTTP client references in `*.Core/**`
- `task {}` not `async {}` in Core (blueCode CI grep enforces this; we mirror)
- Serilog → stderr, application output → stdout (separation matters for tests
  that capture stdout)
- Per-task atomic commits; `git add <file>` not `git add -A`
- Test discovery: explicit `rootTests` list pattern in the test entrypoint —
  Expecto auto-discovery is unreliable and burned multiple executors

**Why task-based routing beats prompt-length-only routing.** A short
prompt that says "build the dependency graph for this 30-file project" looks
trivial to a length heuristic but needs 122B's reasoning. The `task` field
lets Graphify declare intent the router can't otherwise infer cheaply.
Heuristic stays as fallback for clients (Hermes) that can't or won't
annotate.

**Why 122B concurrency must be capped at 1.** mlx_lm.server holds the model
weights in resident memory; concurrent generations contend for the same
forward-pass kernels and serialize at the metal layer with worse latency than
clean serialization at the application layer. RSS is already ~45 GB for one
generation; a second concurrent generation risks `[METAL] Insufficient Memory`
crashes (observed in blueCode's `~/llm-system/services/logs/122b.err`).
SemaphoreSlim(1) at the gateway is the cheapest place to enforce this.

**Why graph_indexing must fail on 122B unavailable.** Graph indexing produces
durable artifacts that downstream queries depend on. A 35B-built index would
be silently lower quality and would poison every retrieval that hit those
nodes for as long as the index lived. Loud failure beats silent quality
regression.

## Constraints

- **Tech stack**: F# / .NET 10 / ASP.NET Core Minimal API / HttpClientFactory / System.Text.Json — fixed by user decision; matches blueCode's `net10.0`
- **No Python** — explicit in original brief
- **Test stack**: Expecto + ASP.NET Core TestServer — single framework, matches blueCode
- **Logging**: Serilog → stderr — matches blueCode; stream separation matters for tests that capture stdout
- **Stateless**: no static mutable state, DI throughout (queue + semaphore + counters live in DI-scoped singletons)
- **Mac-only**: launchd plist deployment, Unix path conventions
- **Loopback-only**: binds to `localhost:4000`, no public exposure, no auth surface
- **122B concurrency cap = 1**: enforced by SemaphoreSlim regardless of caller
- **Aggressive 35B preference for heuristic-only path**: when in doubt, route to 35B (latency win for Hermes)
- **Task-routing table is authoritative when `task` is present**: heuristic does not override an explicit task
- **graph_indexing has no fallback**: 122B unavailable → return error; do not silently downgrade to 35B

## Key Decisions

<!-- Decisions that constrain future work. -->

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Test framework: **Expecto** (override briefs' xUnit/FsUnit) | Matches blueCode; user knows its quirks (`testSequenced`, explicit `rootTests` list, Console.SetOut races) | — Pending |
| Code reuse: **copy adapters** from blueCode (`QwenHttpClient.fs`, `Json.fs`, `Logging.fs`); rewrite Core cleanly | blueCode's Core is an agent loop — domain doesn't transfer. Adapters carry hard-won mlx_lm gotchas worth lifting verbatim. Avoids cross-project shared library refactor. | — Pending |
| Streaming: **pass-through SSE from v1** | Both Hermes (default `stream=true`) and Graphify (per spec) need streaming. Forwarding upstream chunks unchanged is the simplest correct option. | — Pending |
| `model` field: **honor explicit override (`35b`/`122b` aliases) + log decision** | Lets operator force a target for debugging while keeping default behavior heuristic. Logging captures data for later threshold tuning. | — Pending |
| `task` field: **non-OpenAI extension; authoritative when present** | Lets Graphify declare intent the router can't infer cheaply. Heuristic does not override explicit task — Graphify knows its own workload better than any keyword regex. | — Pending |
| Routing pipeline: **`model` override → `task` table → heuristic → 35B-default** | Single ordered decision chain; each stage has clear precedence; fully testable in isolation. | — Pending |
| 122B concurrency: **`SemaphoreSlim(1)` at the gateway** | Single chokepoint matches the underlying single-process model server constraint; cheaper than per-request mlx_lm pushback. | — Pending |
| Priority queue: **two-level (high/low) FIFO within level** for 122B | Matches Graphify's stated priority shape (graph_indexing high, summaries low). FIFO within level keeps it simple; revisit if starvation observed. | — Pending |
| Fallback policy: **122B-unavailable → 35B, except `task=graph_indexing` which must fail** | Wrong-model index is silently lower quality and durable; loud failure is the right operational signal. Hermes complex prompts can degrade to 35B safely. | — Pending |
| Deployment: **launchd plist** (mirror `com.ohama.qwen122b.plist`) | Auto-start + supervision in the same shape as upstream services; one operational pattern instead of two. | — Pending |
| Architecture: **hexagonal mirror of blueCode** (pure Core + Cli adapters, `task {}` only in Core) | Operator already maintains blueCode under these invariants; matching them keeps both projects on the same mental model and same CI patterns. | — Pending |
| Mac-only / loopback-only | Matches blueCode constraint and the actual deployment target; cuts auth, TLS, and cross-platform path handling out of v1 scope. | — Pending |
| Health/stats endpoints in v1 (was v2 in pre-Graphify draft) | Graphify spec needs `/health` for liveness, `/stats` for queue monitoring. Cheap to add at this stage; expensive to retrofit observability later. | — Pending |
| Retry policy + backend health detection in v1 (was v2 in pre-Graphify draft) | Required by graphify spec; backend health detection is also the input to fallback logic. | — Pending |
| Reject Channels / TPL Dataflow for v1 | Priority queue + semaphore covers v1 needs without the abstraction tax. Reach for Dataflow only if v2 fan-out pipelines justify it. | ✓ Good |
| **ML routing folded into v1** (was originally Out of Scope / v2). NEW Phases 4-9 ship the ML arc; old Phases 4 and 6 deferred to Phases 10-11. Old Phase 5 dissolved (OBS-01/03 → NEW Phase 5; TEST-01/02 retroactively Complete via Phases 1-3 tests). | Operator decision 2026-05-08 to fold the ML revisit forward after Phase 3 completion. The 3-layer integration strategy (code separation: `Heuristic.fs` + `ML.fs`; config selection: `Routing.Algorithm`; CLI override) keeps the heuristic baseline as the permanent fallback. Source: `~/projs/smart-router-distillation/docs/handoff-to-smart-router.md`. | — Pending |
| **Heuristic SOFT-PAUSED 2026-05-08** (was: "stays forever as first-class baseline") | Operator decision: ML is the primary path going forward (Phases 6-9). Heuristic code stays in the codebase (`src/SmartRouter.Core/Heuristic.fs`, `Routing.Algorithm` dispatch, `--routing-algorithm` CLI flag, `check-routing-isolation.sh`) as a **dormant emergency fallback** — usable when ML model file is missing/corrupt, for debugging ("how would heuristic decide this?"), or for rollback. NOT actively developed; no new heuristic features; no Phase 9 canary heuristic-vs-ML A/B (Phase 9 compares ML model versions to each other instead). Snapshot preserved at git branch `archive/heuristic-baseline` and tag `v0.5-heuristic-baseline` (commit `a4cfce1`). When Phase 6 ships real ML, `appsettings.json` `Routing.Algorithm` flips default to `"ml"`. | — Pending |
| **Embedding model: bge-m3 int8 quantized from Phase 6** (skipping bge-small MVP and FP32-default both) | Operator's traffic mixes Korean+English; bge-m3 chosen for multilingual; int8 quantized from start. | ✓ Good (v1.0-1.3; ~50ms p95 in production) |
| **v2.0 SELFROUTING PIVOT (operator 2026-05-11)**: Replace ML in routing path with `Hard Rules + 35B self-classify + sticky escalation`. ML code retained but routing-path dormant (mirrors Phase 12 heuristic retirement). Future `Routing.Mode = "ml" \| "selfrouting"` config switch preserved. | Per `.planning/docs/35b-selfrouting.md` §3,7: 35B self-route avoids separate inference server + KV cache pressure; SAFE-for-35B classification more stable than "simple"; same model serves routing + responses. Phase 17 ML QualityClassifier deferred. | ✓ Good (v2.0 shipped 2026-05-12; 32/32 reqs; 175 passing tests) |
| **v2.0 router model = 35B self-route** (NOT 7B Qwen2.5-Coder-7B separate server) | Local Mac M4 128GB constraint; doc §3 — extra server adds memory/scheduling burden; same 35B serving both is cheaper. Future upgrade path to dedicated 7B router preserved (doc §19). | ✓ Good (v2.0; DRT-01 retained as future option) |
| **v2.0 heuristic scope = Hard Rules ONLY** (NOT full Heuristic.fs revival) | Phase 12 heuristic was deleted; v2.0 brings back ONLY the keyword pre-routing layer (LLVM/MLIR/compiler/segfault/optimization/concurrency per doc §6,12). Prompt length / complexity score / message count etc. stay buried. | ✓ Good (v2.0; HR-01..06 satisfied; 16 HardRulesTests + 9 ModeSwitchTests) |
| **v2.0 architecture context**: Hermes Agent sits ABOVE smart-router (upper layer); smart-router gets session_id propagation downward for sticky escalation. Integration target `~/hermes-agent`. | Doc §18 architecture diagram + selfrouting doc §16 sticky escalation requires session continuity from a layer that knows the session — Hermes is that layer for v2.0. | ✓ Good (v2.0; HMRS-01..04 shipped; HMRS-FUTURE-01 Hermes-side PR tracked) |
| **v2.0 phase order locked by Domain.fs compile dependency** (operator/researcher 2026-05-11): Hard Rules (17) → Session Store (18) → SelfRouter (19) → Hermes Integration (20). | Architecture researcher HIGH confidence over Stack/Features researchers' SelfRouter-first proposal: `makeSelfRoutingAlgorithm` closure reads `req.SessionId` at sticky stage 4 before invoking ClassifyAsync — SessionStore must precede SelfRouter. | ✓ Good (v2.0; phases shipped in order; no rework) |
| **v2.0 streaming-skip for self-classify (SR-06)** | Mirrors Phase 14 quality-fallback streaming-skip — first-chunk latency budget cannot accommodate classify round-trip. Hard Rules (0ms) + sticky still apply. | ✓ Good (v2.0; explicit `if req.Stream then skip` comment in ChatCompletions.fs; structural zero ClassifyAsync calls verified by tests) |
| **v2.0 Hard Rules wins over explicit override (HR-06)** | Safety mechanism precedence — operator who wants Hard Rules disabled must source-edit + rebuild. README §5.5 documents source-edit requirement (HR-02). | ✓ Good (v2.0; corrected during 17-03 from misleading "bypasses" wording) |
| **v2.0 ML adapter unconditional DI registration (MODE-03)** | RetrainingService accumulates hard cases in BOTH `"selfrouting"` and `"ml"` modes so ML re-activation does not require retraining from scratch. | ✓ Good (v2.0; `MlDormantTests.fs` skip-guarded test confirms ML path still wires) |
| **v2.0 fingerprint fallback opt-in (HMRS-02)** | Network-level fingerprint (RemoteIp+UA) has NAT/loopback collision risk; default `false` preserves v1.x stateless behavior. README §10 carries explicit "NOT SAFE BEHIND REVERSE PROXIES" warning (PROXY-01 callout). | ⚠️ Superseded by v2.1 (HMRS-02 fully deleted in Phase 22 MIG-01..03; pre-deletion snapshot at `archive/v2.0-network-fingerprint` tag `d4797e7`) |
| **v2.1 Hermes-less Session Tiering (operator 2026-05-12)**: Replace v2.0 IP+UA fingerprint with three-tier extraction cascade — header (unchanged) → system-prompt regex parse of stock Hermes `--pass-session-id` line → SHA-256 content fingerprint of conversation prefix. Both `Routing.Mode` values use the new tiers. | Source: `~/projs/smart-router-distillation/idea/hermes-session-without-modification.md`. Operator chose Hermes-less path over HMRS-FUTURE-01 (Hermes-side PR) — works with stock Hermes today. Content fingerprint solves loopback collision risk PROXY-01 raised about IP+UA. | ✓ Good (v2.1 shipped 2026-05-12; 24/24 reqs; 187 passing tests; HMRS-02 fully deleted) |
| **v2.1 adapter placement: `HermesSessionExtract` + `ContentFingerprint` in `SmartRouter.Cli.Adapters`, NOT Core** | Hexagonal invariant is file placement, not BCL usage. Both adapters use BCL only (`System.Text.RegularExpressions`, `System.Security.Cryptography`) but live in Cli to match v2.0 pattern (`SessionStore.fs`, `SelfRouter.fs` in Cli; only `HardRules.fs` was Core-BCL). | ✓ Good (v2.1; ARCH-01 preserved across 24 phases) |
| **v2.1 HSP regex: `^Session ID:[ \t]*(\S+)` not `^Session ID:\s*(\S+)`** | `\s` matches `\n` enabling cross-line collapse and incorrect matching for HSP-04 case (e) malformed-line-no-value. `[ \t]*` constrains to same-line horizontal whitespace. Auto-fixed during Plan 21-01 execution. | ✓ Good (v2.1; HSP-04 case (e) returns None as required) |
| **v2.1 TIER-03 placement**: Tier 2 + Tier 3 resolution happens in `ChatCompletions.fs resolveSessionCascade` (between `mapWireToRequest` and `routeRequest`), NOT in `CorrelationMiddleware` itself | `CorrelationMiddleware` runs pre-body-parse and cannot access `req.Messages`. Tier 2/3 require parsed RouterRequest. Resolution placed in handler scope keeps middleware Tier-1-only and avoids double-body-read. | ✓ Good (v2.1; cascade fires deterministically; no middleware-vs-handler ordering bugs) |
| **v2.1 `SessionCascadeStats` DI registration unconditional** (registered in both `configureRequestPipeline` and `configureWithoutMl`; no NoOp pattern needed) | `GetRequiredService<ISessionCascadeStats>()` at call site guarantees startup failure if either branch missing the registration — both-modes guarantee structurally enforced. TC-7 (Phase 24) upgrades from structural to executable assertion. | ✓ Good (v2.1; closes TIER-04 evidence gap) |
| **v2.1 Plan 22-02 commit order reversed from MIG REQ numbering** (MIG-06 → MIG-03 → MIG-02 → MIG-01) | Every intermediate state must build. `HermesFingerprintTests.fs` uses `SessionOptions.FingerprintEnabled`; deleting the field first breaks test compile. Delete tests first, then middleware, then config. | ✓ Good (v2.1; every commit in Plan 22-02 builds and tests pass) |
| **v2.1 `archive/v2.0-network-fingerprint` annotated tag, not lightweight** | Per v2.0 milestone formality convention (`milestone-v1.3`, `milestone-v2.0` are annotated). Pre-deletion snapshot at `d4797e7` archaeologically reachable. | ✓ Good (v2.1; matches `archive/heuristic-baseline` + `v0.5-heuristic-baseline` project pattern) |
| **v2.1 TC-7 ml-mode DI test (Phase 24 gap closure)**: One-line executable assertion (`Expect.isNotNull (box stats)`) replaces structural-only DI proof for TIER-04 | TD-1 from v2.1-MILESTONE-AUDIT.md (status=tech_debt). Operator chose to close TD-1 before archive — upgrades evidence from "code-reading" to "CI-enforced". `Routing:ML` section omitted so `mlOpts=null` at `CompositionRoot.fs:342` skips ML bootstrap and avoids ONNX dependency in CI. | ✓ Good (v2.1; TC-7 passes; audit re-run promoted v2.1 status to `passed`) |
| **v2.1 SC-2 descoped per research Q11**: paired counter test running `resolveSessionCascade` end-to-end in ml-mode NOT added | `resolveSessionCascade` has no mode branch — byte-for-byte identical at runtime in `"selfrouting"` and `"ml"`. TC-1..TC-4 already exhaustively cover all four cascade branches; running them again under different DI provider config adds zero coverage. Audit's TD-1 ask is SC-1 (DI resolution) only. | ✓ Good (v2.1; avoided scope inflation; closed TD-1 in 1 plan / 2 task commits) |

---
*Last updated: 2026-05-12 after v2.1 milestone shipped (Hermes-less Session Tiering; 4 phases / 7 plans / 24 requirements / 187 passing tests). Previous: v2.0 ✅ SHIPPED 2026-05-12 (Self-Routing + Session-Aware; 32/32 reqs; 175 tests). Next milestone: TBD — run `/gsd:new-milestone` to scope.*
