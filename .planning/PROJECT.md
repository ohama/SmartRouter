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

**v2.0 pivot (operator 2026-05-11):** Selfrouting paradigm replaces ML in the routing path. New pipeline: `Hard Rules (keyword pre-routing) → 35B self-classify (SAFE/UNSAFE 1-token) → sticky session continuity → Qwen 35B/122B`. ML code retained in repo but routing-path dormant (mirrors Phase 12 heuristic retirement). Future `Routing.Mode = "ml" | "selfrouting"` config switch preserved as option.

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

## Current Milestone: v2.0 Self-Routing + Session-Aware

**Goal:** Replace ML-driven routing decision path with selfrouting paradigm: keyword hard rules → 35B asks itself "SAFE for me?" → sticky session continuity. Hermes Agent integrates upward via session_id propagation for debugging-continuity. ML code retained in repo but dormant.

**Target features:**
- Hard Rules pre-routing layer (stage 0; keyword-driven immediate-122B for LLVM/MLIR/compiler/segfault/optimization/concurrency)
- 35B self-classify routing (1-token SAFE/UNSAFE; cached by prompt_hash; max_tokens=4-8; temp=0)
- Sticky session escalation (if previous request in session was 122B → stay on 122B; debugging continuity)
- Hermes Agent integration (X-Session-Id header propagation; smoke test against `~/hermes-agent`)
- (optional) Speculative routing — 35B drafts while router evaluates complexity

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

### Active

<!-- v2.0 milestone scope. Will be detailed by /gsd:new-milestone Phase 8 (requirements). -->

(Will be populated by REQUIREMENTS.md after Phase 8 requirements gathering)

High-level v2.0 capabilities (to be decomposed into requirements):
- [ ] Hard Rules pre-routing (stage 0; before existing pipeline)
- [ ] 35B self-classify routing with operator-tunable prompt
- [ ] Sticky session escalation with session store
- [ ] Hermes Agent integration via `X-Session-Id` header
- [ ] ML code routing-path dormant (retained for future `Routing.Mode` switch)

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
| **v2.0 SELFROUTING PIVOT (operator 2026-05-11)**: Replace ML in routing path with `Hard Rules + 35B self-classify + sticky escalation`. ML code retained but routing-path dormant (mirrors Phase 12 heuristic retirement). Future `Routing.Mode = "ml" \| "selfrouting"` config switch preserved. | Per `.planning/docs/35b-selfrouting.md` §3,7: 35B self-route avoids separate inference server + KV cache pressure; SAFE-for-35B classification more stable than "simple"; same model serves routing + responses. Phase 17 ML QualityClassifier deferred. | — Pending |
| **v2.0 router model = 35B self-route** (NOT 7B Qwen2.5-Coder-7B separate server) | Local Mac M4 128GB constraint; doc §3 — extra server adds memory/scheduling burden; same 35B serving both is cheaper. Future upgrade path to dedicated 7B router preserved (doc §19). | — Pending |
| **v2.0 heuristic scope = Hard Rules ONLY** (NOT full Heuristic.fs revival) | Phase 12 heuristic was deleted; v2.0 brings back ONLY the keyword pre-routing layer (LLVM/MLIR/compiler/segfault/optimization/concurrency per doc §6,12). Prompt length / complexity score / message count etc. stay buried. | — Pending |
| **v2.0 architecture context**: Hermes Agent sits ABOVE smart-router (upper layer); smart-router gets session_id propagation downward for sticky escalation. Integration target `~/hermes-agent`. | Doc §18 architecture diagram + selfrouting doc §16 sticky escalation requires session continuity from a layer that knows the session — Hermes is that layer for v2.0. | — Pending |

---
*Last updated: 2026-05-11 after v1.3 milestone ✅ SHIPPED and v2.0 operator pivot to selfrouting paradigm. v1.x ML routing arc complete; v2.0 selfrouting milestone starting. ML code retained but routing-path dormant; future Routing.Mode config switch preserves re-activation option.*
