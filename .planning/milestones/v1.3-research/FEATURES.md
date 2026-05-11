# Feature Research

**Domain:** OpenAI-compatible LLM router / gateway (local, single-host, task-typed routing)
**Researched:** 2026-05-07
**Confidence:** HIGH

---

## Comparable Projects: Quick Reference

Before categorizing features, here is a factual snapshot of each comparable product — what it exposes, its concurrency model, streaming support, task-based routing, and priority queue support. These inform which features are table stakes (everyone has them), which are differentiators (nobody does them for our use case), and which are anti-features (everyone builds them, but they have no relevance here).

| Product | What it exposes | Concurrency model | Streaming | Task-based routing | Priority queue |
|---------|----------------|-------------------|-----------|--------------------|----------------|
| **LiteLLM proxy** | `/v1/chat/completions`, `/v1/models`, `/health`, `/metrics`; virtual keys, budget, guardrails, dashboards | Per-deployment `max_parallel_requests`; global cap via Redis; multi-instance via Redis queue | Yes — SSE pass-through | Semantic auto-router (utterance matching via embeddings; beta, opt-in); no explicit `task` field | [BETA] Redis-backed `priority` field passed via `extra_body`; polling at 3 ms; unstable in open issues |
| **OpenRouter** | `/v1/chat/completions` cloud proxy; `extra_body.provider` for provider routing preferences; `x-title` / `http-referer` headers | Cloud-managed; no user-visible semaphore; load-balanced across providers | Yes — SSE pass-through | No task field; provider routing is model-selection, not task-selection | No |
| **vLLM OpenAI server** | `/v1/chat/completions`, `/v1/models`, `/metrics`; vLLM-specific extras via `extra_body` (`top_k`, `priority`, `request_id`) | Continuous batching; `max_num_seqs` / `max_num_batched_tokens` limits; chunked-prefill default in v1 | Yes — SSE, streaming tool calls | No task routing — single model served | `priority` integer via `extra_body`; lower = earlier; internal scheduler only, not gateway-level |
| **llama.cpp server** | `/v1/chat/completions`, `/v1/models`; `--parallel N` for concurrent slots; `--cont-batching` | N parallel generation slots; memory-limited; no gateway-level queue | Yes — SSE; streaming tool calls added recently (limited) | No | No |
| **Ollama OpenAI shim** | `/v1/chat/completions`, `/v1/models`; Ollama-native extras via `extra_body.options` (e.g. `num_ctx`) | Internal request queue to model manager; models loaded/unloaded from memory per request | Yes — SSE; streaming tool calls 2025 | No | No |
| **Portkey** | Cloud gateway: `/v1/chat/completions`; virtual keys, configs, guardrails, fallback chains, canary deployments, observability | Cloud-managed; batching/queuing via config objects; no user-visible semaphore | Yes | Conditional routing via config (cost, latency, model tag) — not task-typed | No |
| **one-api / new-api** | Multi-provider aggregator; virtual keys; web dashboard; channel management; load balancing by channel weight | Per-channel rate limits; no semaphore | Yes | No task routing | No |

**Key pattern across all comparables:** Every product exposes an OpenAI-compatible `/v1/chat/completions` endpoint with SSE streaming. None implement an explicit `task` field for deterministic workload-class routing. None enforce a per-model `SemaphoreSlim(1)` at the gateway level for local models. Priority queues exist only in LiteLLM (beta/unstable, Redis-dependent) and vLLM (internal, not gateway-visible). Task-typed routing is an open problem in the space — comparables approximate it with semantic similarity or model-name selection, not explicit task declarations.

---

## Feature Landscape

### Table Stakes (Router Fails Its Job Without These)

These are the features any client expecting an OpenAI-compatible gateway will break without. They are non-negotiable for v1.

| Feature | Why Expected | Complexity | Dependencies | blueCode/graphify_prompt Commit |
|---------|--------------|------------|--------------|--------------------------------|
| `POST /v1/chat/completions` — parse and proxy | Every OpenAI SDK client issues this call; Hermes uses `client.chat.completions.create()` unconditionally | LOW | None | Both briefs; PROJECT.md §API surface |
| Streaming pass-through (SSE) | Hermes defaults `stream=True` (run_agent.py:6922-6941); Graphify spec §5; without it Hermes hangs | MEDIUM | HttpClient streaming pipeline | PROJECT.md §Streaming; graphify_prompt §5 |
| Request field preservation (pass-through of unknown fields) | Hermes sends `stream_options: {include_usage: true}` (run_agent.py:6925); stripping unknown fields breaks clients silently | LOW | None | PROJECT.md §Request parsing; graphify_prompt §2 |
| `GET /v1/models` | OpenAI SDK calls this for model discovery; LiteLLM, vLLM, Ollama, llama.cpp all expose it | LOW | Upstream health check | PROJECT.md §API surface |
| `GET /health` | Standard liveness probe; Graphify spec requires it; all comparables expose it | LOW | Backend reachability tracking | PROJECT.md §Operability; graphify_prompt §7 |
| Structured logging per request | All comparables log model selection, latency, and errors; without it routing decisions are undebuggable | LOW | Serilog (blueCode pattern) | PROJECT.md §Observability |
| Configurable routing rules (`appsettings.json`) | Thresholds and task→model mappings must be tunable without recompile; all production proxies expose config | LOW | None | PROJECT.md §Routing decision pipeline |
| HttpClient timeout (300s) | mlx_lm.server 122B cold-start up to 240s; without this the client gets a connection reset before the model answers | LOW | HttpClientFactory | blueCode hard-won operational knowledge; PROJECT.md §Constraints |
| Cancellation token propagation (client → router → upstream) | Hermes drops connections mid-stream on interrupt; orphaned upstream calls waste 122B capacity | MEDIUM | CancellationToken in streaming path | PROJECT.md §Streaming |
| Error responses in OpenAI error shape | Clients parse `{"error": {"message": "...", "type": "..."}}` — returning raw ASP.NET problem details breaks OpenAI SDK error handling | LOW | None | All comparables do this |
| Sampling-parameter defaults (temp=0.7, top_p=0.8, top_k=20) when client omits them | mlx_lm.server behaves incorrectly without explicit sampling params; blueCode discovered this | LOW | Request model enrichment | blueCode operational knowledge; PROJECT.md §Context |
| `model` field rewriting (HF-id trap defense) | mlx_lm.server overwrites the loaded tokenizer if you send the HF id; must send local path; `tryParseModelId` in blueCode | LOW | None | blueCode `QwenHttpClient.fs`; PROJECT.md §Context |

**What comparables tell us about table stakes:**
Every comparable (LiteLLM, vLLM, llama.cpp, Ollama, OpenRouter) exposes exactly this surface. The only one specific to our local mlx_lm setup is the HF-id trap defense and the 300s timeout — standard cloud proxies don't need either.

---

### Differentiators (Specific to Hermes + Graphify Pairing)

These features are the reason this router exists rather than pointing both clients at a stock LiteLLM instance. No comparable product does all of them for a local dual-model mlx_lm setup.

| Feature | Value Proposition | Complexity | Dependencies | blueCode/graphify_prompt Commit |
|---------|-------------------|------------|--------------|--------------------------------|
| **Explicit `task` field routing** — authoritative, deterministic | Graphify declares workload class at call time; no semantic similarity approximation needed; short prompts that need 122B reasoning (e.g. "build dependency graph") are never mis-routed | LOW | Task→model routing table in config | PROJECT.md §Routing; graphify_prompt §3A; PROJECT.md §Key Decisions |
| **Two-level priority queue for 122B** (high: graph_indexing/compiler_debug/architecture_analysis; low: dependency_analysis/reasoning) | Graphify graph indexing blocks downstream queries; it must preempt lighter 122B work; no comparable gateway implements gateway-level priority queuing for local model protection | MEDIUM | SemaphoreSlim(1) concurrency gate | PROJECT.md §Concurrency; graphify_prompt §Advanced Req 1 |
| **SemaphoreSlim(1) on 122B** — gateway-enforced serial execution | mlx_lm.server concurrent forward-pass contention serializes at the metal layer with worse latency than clean gateway serialization; prevents `[METAL] Insufficient Memory` crashes | LOW | None (but enables priority queue) | PROJECT.md §Concurrency; PROJECT.md §Context |
| **`graph_indexing` no-fallback rule** — must fail, not degrade | Graph indexing produces durable artifacts; a 35B-built index silently poisons every downstream retrieval for the index lifetime; loud failure is the correct operational signal | LOW | Backend health detection | PROJECT.md §Reliability; graphify_prompt §Advanced Req 2 |
| **Heuristic fallback for Hermes (no `task` field)** — keyword set + prompt length + code-block detection + message count | Hermes never sends `task`; without heuristics every Hermes call goes to 35B including F#/MLIR/compiler prompts; heuristic routes these to 122B correctly | MEDIUM | Complexity scorer, configurable thresholds | PROJECT.md §Routing; smart-router.md §Complexity Routing |
| **Aggressive 35B default for ambiguous heuristic cases** | Hermes is latency-sensitive interactive; when the heuristic is uncertain, 35B latency wins over 122B thoroughness; comparables do not model this bias explicitly | LOW | Complexity scorer threshold | PROJECT.md §Core Value; PROJECT.md §Key Decisions |
| **`GET /stats` endpoint** — queue depth, wait time, requests/sec, active model, failure count | Graphify spec requires queue monitoring; needed to detect 122B queue starvation in production; `/health` alone is insufficient | LOW | In-process counters (DI singletons) | graphify_prompt §Advanced Req 3; PROJECT.md §Observability |
| **Retry policy on transient upstream failures** with bounded backoff | mlx_lm.server returns 503 during cold-start; without retry the client sees an error and must implement retry itself; centralizing in the router removes the burden from both Hermes and Graphify | MEDIUM | Polly or manual retry in F# | PROJECT.md §Reliability; graphify_prompt §6 |
| **Backend health probing** (35B / 122B reachability tracked separately) | Required for fallback logic; Graphify spec §6; health probe state feeds both `/health` and fallback decisions; without it the router cannot distinguish "122B unavailable" from "122B slow" | LOW | Background probe task | PROJECT.md §Reliability |
| **Explicit `model` override** (`35b`/`122b` aliases) with logged routing reason | Operator debugging path; bypasses routing pipeline entirely; all routing decisions logged for threshold tuning via `/stats` | LOW | None | PROJECT.md §Key Decisions |

**What comparables tell us about differentiators:**

- LiteLLM's auto-router approximates task routing with semantic similarity embeddings (beta; requires an embedding model running, Redis, polling at 3ms, has open bugs). Our explicit `task` field is zero-overhead, deterministic, and cannot mis-route.
- LiteLLM's priority queue is Redis-dependent (cannot work loopback-only without running Redis) and beta/unstable. Our in-process SemaphoreSlim + priority queue is simpler, faster, and has no external dependencies.
- No comparable implements a per-model serial execution gate for local model protection. This is unique to the local mlx_lm constraint.
- No comparable implements a per-task no-fallback rule. This is specific to the graph_indexing quality-correctness requirement.

---

### Anti-Features (Deliberately Excluded, With Reasoning)

These are features competing products build that we should explicitly not build. In each case, the feature either has no use case in this deployment context or would introduce complexity that exceeds the value.

| Anti-Feature | Who builds it | Why they build it | Why we are NOT building it | What we do instead |
|---|---|---|---|---|
| **Auth / API keys / virtual keys** | LiteLLM, Portkey, one-api, OpenRouter | Multi-tenant public endpoints need access control | Loopback-only (`localhost:4000`); no external exposure; two known clients; zero abuse surface | Bind to loopback only; document in README |
| **Multi-tenant cost tracking / billing** | LiteLLM, Portkey, one-api | SaaS products monetize per-token across tenants | Single operator, single host, no billing relationship | `/stats` covers operational visibility for one operator |
| **Prometheus `/metrics` endpoint** | LiteLLM, vLLM | Prometheus scrapers in cloud deployments | No scraper exists in this deployment; adding the exposition format before a scraper arrives is premature | `/stats` JSON endpoint covers v1 needs; add Prometheus exposition only if a scraper actually arrives |
| **Rate limiting** | LiteLLM, Portkey, one-api | Prevent runaway cost / abuse from many clients | Single host, two known clients; no abuse vector; rate limiting on 122B would fight with the priority queue | SemaphoreSlim(1) + priority queue is the correct concurrency control for this model |
| **Prompt caching** | LiteLLM, OpenRouter (provider-level) | Reduce cost on cloud LLMs with KV cache | mlx_lm.server manages KV cache internally; the router has no visibility into or control over it | Pass requests through unchanged; mlx_lm handles caching |
| **Embeddings endpoint (`/v1/embeddings`)** | LiteLLM, vLLM | Many RAG pipelines need embeddings | Neither Hermes nor Graphify routes embeddings through this gateway; Graphify's graph indexing uses the chat completions endpoint | Omit; add only if a concrete consumer requires it |
| **Function-calling rewriting / tool-call normalization** | LiteLLM | Normalize tool-call formats across providers | Hermes handles its own tool-call parsing; the router's job is transparent proxy, not format rewriting; rewriting breaks Hermes's tool-call accumulation logic (run_agent.py:6952-6958) | Pass tool-call chunks through unchanged |
| **ML / learned routing** | LiteLLM auto-router (beta) | Approximate task inference without explicit field | Requires an embedding model running, adds latency on the hot path, is probabilistic; our explicit `task` field is already available for Graphify | Explicit `task` field + keyword heuristic covers both clients |
| **Circuit breaker as a distinct mechanism** | Many frameworks | Handle recurring failure modes with "open" state | Retry policy + health probing covers immediate failures; a circuit breaker adds state complexity before any recurring failure mode has been observed | Add only if a recurring failure mode appears that needs explicit "open" state |
| **Docker / container deployment** | Portkey, LiteLLM, graphify_prompt §Required Output** | Cloud and CI deployments | Mac-only, launchd plist deployment; mirrors blueCode operational pattern; no reason to run elsewhere in v1 | launchd plist (matches `com.ohama.qwen122b.plist`) |
| **Session state / conversation memory** | Some gateway products | Stateful multi-turn session management | Router is stateless per request; conversation memory lives in the client (Hermes agent loop, Graphify query engine) | DI-scoped singletons for in-flight counters only |
| **Channels / TPL Dataflow** | graphify_prompt suggests it | Fan-out pipelines in complex orchestration | v1 routing is single-hop: one request → one upstream; no fan-out; Dataflow abstraction tax exceeds v1 benefit | SemaphoreSlim + simple priority queue |
| **Provider abstraction implementations (Claude, OpenAI cloud, DeepSeek, Gemini)** | LiteLLM, OpenRouter | Multi-provider aggregation | Only Qwen 35B and 122B are in scope; adding live cloud integrations before they have a consumer creates untested dead code | Interfaces in place (extensibility seams), no live implementations |

**Note on graphify_prompt anti-features:** The graphify_prompt brief asked for Dockerfile, docker-compose, xUnit/FsUnit, and Channels/TPL Dataflow. PROJECT.md explicitly rejects all four with rationale. The brief was a generative prompt that over-scoped; PROJECT.md is the authoritative scope.

---

## The `task` Field Extension: Industry Convention

**Question:** Graphify will send a non-OpenAI `task` field. What is the convention in this space?

### What comparables do

| Product | How they pass non-standard fields | Mechanism |
|---------|-----------------------------------|-----------|
| **OpenRouter** | `extra_body.provider` — provider routing preferences nested under `provider` key | OpenAI SDK `extra_body` kwarg; top-level within the POST body |
| **vLLM** | `extra_body.priority`, `extra_body.top_k`, `extra_body.request_id` | OpenAI SDK `extra_body` kwarg; treated as top-level JSON fields by vLLM's server |
| **Ollama** | `extra_body.options.num_ctx` (context window), `extra_body.think` (thinking mode) | OpenAI SDK `extra_body` kwarg; nested under `options` or as top-level extra |
| **LiteLLM** | Any non-OpenAI param passed as a kwarg goes into the request body; `extra_body` for metadata/logging | OpenAI SDK `extra_body`; treated as provider-specific params |
| **Portkey** | Config object sent via headers (`x-portkey-config`) for routing; `extra_body` for provider-specific params | HTTP header for routing metadata |

### The industry convention

The OpenAI Python SDK's `extra_body` parameter merges additional JSON fields into the top-level request body. This is the de facto convention for non-standard extensions in the OpenAI-compatible ecosystem:

```python
# Graphify sends:
client.chat.completions.create(
    model="qwen-router",
    messages=[...],
    extra_body={"task": "graph_indexing"}
)
# Wire shape (POST body):
# {"model": "qwen-router", "messages": [...], "task": "graph_indexing"}
```

The `task` field lands as a **top-level field in the JSON body** — identical to how vLLM exposes `priority` and Ollama exposes `think`. This is the correct approach because:

1. It survives proxy middleware that strips `extra_body` — the field is already merged into the body by the SDK before sending.
2. ASP.NET Core's `System.Text.Json` deserializer with `JsonExtensionData` captures unknown top-level fields transparently.
3. Both LiteLLM and OpenRouter document this as the canonical pattern for provider-specific extensions.
4. HTTP headers are a viable alternative (used by Portkey for routing config) but require header injection on every call — harder to do from the OpenAI SDK's `chat.completions.create()` interface.

**Decision already committed in PROJECT.md:** `task` field is a non-OpenAI extension; top-level in the JSON body; authoritative when present. This aligns with industry convention.

**Hermes never sends `task`:** Confirmed from `run_agent.py` and `plugins/model-providers/custom/__init__.py`. The custom provider profile only adds `extra_body.options.num_ctx` (Ollama context window) and `extra_body.think` (reasoning disable). No `task` field, no routing metadata. Heuristic path is the correct design for Hermes.

---

## Feature Dependencies

```
[SemaphoreSlim(1) on 122B]
    └──enables──> [Priority queue for 122B]
                      └──enables──> [task=graph_indexing high-priority]
                      └──enables──> [task=compiler_debug high-priority]

[Backend health probing]
    └──enables──> [Fallback: 122B unavailable → 35B]
                      └──constrains──> [graph_indexing no-fallback rule]
                      └──feeds──> [/health endpoint]

[Explicit task field routing]
    └──requires──> [Task→model routing table in config]
    └──overrides──> [Heuristic fallback]
    └──is absent for──> [Hermes path → heuristic fallback active]

[Heuristic fallback]
    └──requires──> [Complexity scorer (keyword set, prompt length, code blocks, message count)]
    └──requires──> [Configurable thresholds in appsettings.json]

[SSE streaming pass-through]
    └──requires──> [Cancellation token propagation]
    └──requires──> [HttpClient streaming pipeline (no buffering)]

[/stats endpoint]
    └──requires──> [In-process counters (DI singletons): queue depth, wait time, requests/sec, failures]
    └──enhanced-by──> [SemaphoreSlim queue depth visibility]
```

### Dependency notes

- **Priority queue depends on SemaphoreSlim(1):** The queue only has meaning when there is a gate. Without the semaphore, queuing is irrelevant.
- **`graph_indexing` no-fallback depends on backend health probing:** The router must know 122B is unavailable (not just slow) before triggering the no-fallback error path.
- **Heuristic fallback does not conflict with task routing:** They occupy different branches of the decision pipeline. Task routing short-circuits; heuristic runs only when no task field is present.
- **`/stats` does not depend on any routing feature:** It can be built as a thin counter layer over the DI singletons, independently of routing complexity.

---

## MVP Definition

### Launch With (v1) — all of these are already committed in PROJECT.md

- [ ] `POST /v1/chat/completions` — parse, route, proxy, SSE pass-through — the product does not exist without this
- [ ] `GET /health`, `GET /v1/models`, `GET /stats` — required by Graphify spec; cheap at this stage; expensive to retrofit
- [ ] Routing pipeline: `model` override → `task` table → heuristic → 35B-default — core value of the router
- [ ] SemaphoreSlim(1) on 122B + two-level priority queue — required before Graphify sends concurrent graph_indexing calls
- [ ] `graph_indexing` no-fallback rule — must ship with graph_indexing routing; a silent quality regression is worse than no v1
- [ ] Backend health probing + retry policy — required to power the fallback rule and Graphify's reliability requirements
- [ ] Structured logging (Serilog → stderr), sampling-param defaults, HF-id trap defense — blueCode operational knowledge; must ship day one
- [ ] Cancellation token propagation — Hermes drops connections on interrupt; without this 122B capacity leaks

### Add After Validation (v1.x)

- [ ] **Prometheus `/metrics` exposition** — trigger: a scraper actually arrives in the operator's setup
- [ ] **Circuit breaker** — trigger: a recurring upstream failure mode is observed that needs explicit "open" state
- [ ] **ML / learned routing** — trigger: `/stats` + structured logs accumulate enough routing decision data to train against

### Future Consideration (v2+)

- [ ] **Additional provider implementations** (Claude, OpenAI cloud, DeepSeek) — trigger: a third consumer with different backend needs
- [ ] **Rate limiting** — trigger: a runaway-loop scenario or second operator host appears
- [ ] **Multi-level priority queue** (more than 2 levels) — trigger: starvation observed in production with the two-level design

---

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| `POST /v1/chat/completions` + SSE streaming | HIGH | MEDIUM | P1 |
| `task` field routing table | HIGH | LOW | P1 |
| SemaphoreSlim(1) + two-level priority queue | HIGH | LOW | P1 |
| `graph_indexing` no-fallback rule | HIGH | LOW | P1 |
| Heuristic fallback (Hermes path) | HIGH | MEDIUM | P1 |
| Backend health probing + retry | HIGH | MEDIUM | P1 |
| `GET /health`, `GET /v1/models`, `GET /stats` | MEDIUM | LOW | P1 |
| HF-id trap defense + sampling defaults | HIGH | LOW | P1 |
| Cancellation token propagation | MEDIUM | MEDIUM | P1 |
| Structured logging + routing reason | MEDIUM | LOW | P1 |
| Configurable routing rules (appsettings.json) | MEDIUM | LOW | P1 |
| `model` override (debug path) | LOW | LOW | P2 |
| Prometheus `/metrics` | LOW | LOW | P3 |
| Circuit breaker | LOW | MEDIUM | P3 |
| ML / learned routing | LOW | HIGH | P3 |

---

## Competitor Feature Analysis

| Feature | LiteLLM | OpenRouter | vLLM server | llama.cpp server | Ollama shim | Our approach |
|---------|---------|------------|-------------|-----------------|-------------|--------------|
| OpenAI-compatible `/v1/chat/completions` | Yes | Yes | Yes | Yes | Yes | Yes (table stakes) |
| SSE streaming | Yes | Yes | Yes | Yes | Yes | Yes — pass-through, no buffering |
| `/v1/models` | Yes | Yes | Yes | Yes | Yes | Yes — proxy upstream lists, deduped |
| Task-based routing (explicit field) | No (semantic similarity only) | No (model selection only) | No | No | No | **Yes — deterministic, zero-overhead** |
| Priority queue (gateway-level) | Beta / Redis-dependent / unstable | No | Internal only (not gateway-visible) | No | No | **Yes — in-process, 2-level FIFO** |
| Per-model serial execution gate | No | No | No | `--parallel 1` | Internal queue | **Yes — SemaphoreSlim(1) for 122B** |
| Per-task no-fallback rule | No | No | No | No | No | **Yes — graph_indexing must fail** |
| Heuristic routing fallback | Semantic similarity (beta) | No | No | No | No | Yes — keyword + length + code blocks |
| Auth / virtual keys | Yes | Yes | No | No | No | No (anti-feature for loopback) |
| Multi-tenant billing | Yes | Yes | No | No | No | No (anti-feature) |
| Prometheus `/metrics` | Yes | No | Yes | No | No | No in v1 (defer until scraper exists) |
| Rate limiting | Yes | Yes (cloud) | No | No | No | No (anti-feature for known clients) |
| Prompt caching | Yes (Redis) | Yes (provider) | Yes (KV cache) | Yes (KV cache) | Yes (model manager) | No (mlx_lm manages internally) |
| `extra_body` non-standard fields | Yes | Yes (provider routing) | Yes (top_k, priority) | Limited | Yes (options, think) | Yes — `task` field as top-level body field |
| launchd / local Mac deployment | No | N/A | No | No | No | **Yes — matches blueCode operational pattern** |

---

## Sources

- LiteLLM routing docs: https://docs.litellm.ai/docs/routing
- LiteLLM scheduler (beta priority queue): https://docs.litellm.ai/docs/scheduler
- LiteLLM auto-routing: https://docs.litellm.ai/docs/proxy/auto_routing
- LiteLLM provider-specific params: https://docs.litellm.ai/docs/completion/provider_specific_params
- OpenRouter API reference: https://openrouter.ai/docs/api/reference/overview
- OpenRouter provider routing (extra_body.provider): https://openrouter.ai/docs/guides/routing/provider-selection
- vLLM OpenAI-compatible server: https://docs.vllm.ai/en/stable/serving/openai_compatible_server/
- llama.cpp server README: https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md
- Ollama OpenAI compatibility: https://docs.ollama.com/api/openai-compatibility
- Portkey AI gateway features: https://portkey.ai/features/ai-gateway
- HuggingFace request queueing for LLM performance: https://huggingface.co/blog/tngtech/llm-performance-request-queueing
- Red Hat LLM semantic router: https://developers.redhat.com/articles/2025/05/20/llm-semantic-router-intelligent-request-routing
- Hermes custom provider plugin: ~/hermes-agent/plugins/model-providers/custom/__init__.py
- Hermes streaming call site: ~/hermes-agent/run_agent.py:6922-6941
- PROJECT.md: /Users/ohama/projs/smart-router/.planning/PROJECT.md
- smart-router.md: /Users/ohama/projs/smart-router/smart-router.md
- graphify_smart_router_prompt.md: /Users/ohama/projs/smart-router/graphify_smart_router_prompt.md

---
*Feature research for: OpenAI-compatible LLM router (local, task-typed, Hermes + Graphify)*
*Researched: 2026-05-07*
