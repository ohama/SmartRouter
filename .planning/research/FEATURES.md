# Feature Research

**Domain:** LLM router — selfrouting + sticky session + Hermes Agent integration (v2.0 milestone)
**Researched:** 2026-05-11
**Confidence:** HIGH

---

## Scope Note

This file covers only **new v2.0 features**. All v1.x features (ML routing, quality fallback, 122B-as-judge, canary, retraining loop, DecisionLog/TraceLog, health probing, launchd) are validated and retained. Where v2.0 features interact with retained v1.x infrastructure, dependencies are called out explicitly.

---

## Feature Landscape

### Table Stakes (Users Expect These)

Features required for selfrouting + session-awareness to work correctly. Missing any of these means the v2.0 paradigm is non-functional.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Hard Rules pre-routing (stage 0) | Without keyword gating, 35B will misclassify high-stakes compiler/LLVM requests as SAFE — doc §7 ("35B may become overconfident"). First line of defense. | LOW | Keyword list locked: LLVM, MLIR, compiler, segfault, optimization, concurrency. Case-insensitive. Implemented as string contains, not regex. Immediately routes to 122B; bypasses selfroute call entirely. Applies to ALL requests including streaming. No config key — hard-coded keyword list per v2.0 locked decision. |
| 35B self-classify call | The primary routing decision for requests not caught by Hard Rules. 35B asks "SAFE for me?". Returns SAFE/UNSAFE (1-2 tokens). Replaces ML classifier in routing path. | MEDIUM | POST to 35B at :8000 with routing prompt. `max_tokens=4–8, temperature=0, stream=false`. Prompt: SAFE-for-35B framing per `.planning/docs/35b-selfrouting-prompt.md`. Response parse: look for SAFE token → Qwen35B; anything else (UNSAFE, or parse failure) → Qwen122B. parse failure must default to UNSAFE (safe bias, mirrors Phase 7 ROUTE_122B-wins pattern). |
| Prompt-hash cache for self-classify result | Identical prompts (exact same messages array) must not re-invoke 35B twice — that wastes tokens and adds latency. | LOW | SHA-256 hash of serialized messages content → string key. LRU cache, bounded size (e.g., 10,000 entries). Cache invalidation: TTL-based or no TTL (prompt content is deterministic; same prompt = same SAFE/UNSAFE judgment). Cache hit skips 35B call entirely. Log cache_hit in DecisionLog v2.0 extension field. |
| Sticky session escalation | If the previous response in a session came from 122B, stay on 122B. Reasoning/debugging coherence requires model continuity; switching mid-session breaks context. | MEDIUM | In-memory `ConcurrentDictionary<session_id, ModelId>`. Keyed by session_id (see Hermes integration below). Sticky logic: if stored value = Qwen122B → return 122B decision immediately, bypassing selfroute. Write to store when any request resolves to 122B (regardless of stage that decided it: hard rules, selfroute, task table, or quality fallback upgrade). TTL per session entry to prevent unbounded growth (e.g., 30 min idle expiry). |
| Session_id extraction from request | No sticky session without session identity. Hermes will propagate session_id; existing v1.x clients won't. | LOW | Primary: `X-Session-Id` HTTP header. Secondary: `session_id` body field (for clients that can't set headers). Fallback: no session_id → treat as stateless (no sticky; selfroute applies normally). Session_id is OPTIONAL for backward-compat — v1.x Hermes calls without session_id must still work (selfroute applies, no sticky). |
| ML routing path dormant (not deleted) | Operator wants `Routing.Mode = "ml"` config switch preserved for future re-activation. Deleting ML code now makes v2.0 irreversible. | LOW | Follow Phase 12 heuristic retirement pattern exactly: ML code stays in `src/SmartRouter.Core/ML.fs` and `src/SmartRouter.Cli/Adapters/MlNetClassifier.fs`; just not wired into the request path when `Routing.Mode = "selfrouting"` (v2.0 default). `configureServices` ML branch still compiles; DI registration guarded by mode check. |
| DecisionLog extension for v2.0 routing reasons | Operators must be able to distinguish hard-rules hits, selfroute-safe, selfroute-unsafe, sticky-session, and cache-hit decisions in JSONL. Without this, the new routing path is unobservable. | LOW | Extend `RoutingReason` DU with new cases: `HardRules`, `SelfRouteClassify`, `StickySession`. Extend `RoutingDecision` or `DecisionLog` record with new optional fields: `selfroute_cache_hit: bool`, `session_id: string option`. Bump `schema_version` in DecisionLog. README §9.1 must be updated. |

### Differentiators (Competitive Advantage)

Features specific to selfrouting that provide advantages the v1.x ML approach did not.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| SAFE-for-35B framing (not "simple/complex") | More stable classification. "SAFE for a fast 35B coding model" is a binary safety check; "is this simple?" invites subjective reasoning and over-confidence. Per `.planning/docs/35b-selfrouting-prompt.md` §1–2: reduces false-simple rate. | LOW | Already locked in prompt doc. 35B responds SAFE when shallow reasoning, summaries, formatting, boilerplate, YAML/JSON. Responds UNSAFE when debugging, architecture redesign, continuation-heavy (retry/fix/continue), type inference, multi-step planning. Prompt is operator-tunable via `Routing.SelfRoute.PromptPath` config key (mirrors `Routing.Judge.PromptPath` pattern from Phase 16). |
| Continuation-aware routing | Short follow-up prompts ("continue", "retry", "fix this") carry hidden reasoning state from prior turns. ML classifier had no continuation signal. Selfrouting prompt explicitly lists these as UNSAFE examples. | LOW | Handled entirely by prompt design (doc §7). No extra code needed beyond the prompt: "retry/fix/continue workflows" in UNSAFE examples block. Sticky session logic reinforces: if prior turn was 122B (debugging chain), continuation is already sticky-122B regardless of selfroute verdict. |
| Shared KV cache benefit | 35B is already loaded and warmed for its primary workload. Using it for 1-2 token routing classification reuses the existing KV cache and model weights, adding ~10ms latency vs ML classifier's ~50ms with no memory overhead. | LOW | Architectural benefit, no code needed. Documented in selfrouting doc §4: "No additional server, no additional GPU memory, no additional KV cache pressure." |
| Operator-tunable routing prompt | ML classifier required retraining to change routing behavior. Selfrouting prompt can be edited in a text file, then router reloads (hot-swap or restart). Lower barrier to routing quality adjustments. | LOW | Follow Phase 16 judge-prompt pattern: `prompts/selfroute-prompt.md` loaded at startup, path configurable via `Routing.SelfRoute.PromptPath`. If operator edits the prompt file, router restart picks up the change. No hot-reload needed for v2.0. |
| Selfroute confidence field (optional enrichment) | If 35B returns JSON `{"route":"35b","confidence":0.95,"reason":"..."}` instead of bare SAFE/UNSAFE token, the reason and confidence can be logged in TraceLog for debugging. | MEDIUM | Optional enrichment only. Primary mode MUST be 1-2 token SAFE/UNSAFE (faster, more deterministic). JSON mode is operator-opt-in via `Routing.SelfRoute.JsonMode=true`. When disabled (default), bare token parsing. When enabled, parse JSON, log `selfroute_reason` and `selfroute_confidence` to TraceLog. See selfrouting-prompt.md §3 for JSON output format. |
| Hermes Agent integration via session_id | Hermes is the primary interactive consumer. Propagating session_id from Hermes into smart-router enables debugging-chain continuity: a 122B session that starts a debugging chain stays on 122B for the entire conversation, not per-request. | MEDIUM | Requires: (1) Hermes to send `X-Session-Id` header (coordination with Hermes plugin config or operator adding custom header). (2) Smart-router to extract session_id and write to session store. (3) Smoke test against `~/hermes-agent` to confirm end-to-end. |

### Anti-Features (Commonly Requested, Often Problematic)

Features that seem to improve selfrouting but introduce more problems than they solve. These are explicitly out of scope per the reference design.

| Feature | Why Requested | Why Problematic | Alternative |
|---------|---------------|-----------------|-------------|
| 35B reasoning before routing (CoT in routing prompt) | More deliberation should mean better routing accuracy | If the router starts reasoning, it has already failed (selfrouting doc §10). Reasoning adds latency, token cost, and unpredictability; the router must be tiny, deterministic, fast. A long chain-of-thought response is indistinguishable from a regular answer and cannot be parsed as SAFE/UNSAFE. | SAFE-for-35B binary framing with short UNSAFE example list. The examples do the reasoning so the model doesn't have to. `max_tokens=4–8` enforces brevity. |
| Speculative routing (35B drafts while router evaluates) | Could reduce end-to-end latency by overlapping draft generation with routing decision | Enormous complexity: requires cancelling partially-generated 35B response if router decides 122B, managing two concurrent server-side generations, and dealing with half-generated SSE streams. Especially problematic with streaming (Phase 14 quality fallback intentionally skipped streaming for same reason). | Stick with sequential Hard Rules → selfroute (10ms) → dispatch. The 10ms routing overhead is acceptable vs speculative complexity. Mark as Phase 21 optional, only if latency profiling proves a problem. |
| Separate 7B router server | Dedicated router models are more deterministic (selfrouting doc §5) | On a Mac M4 128GB running 35B + 122B simultaneously, a third server adds memory/scheduling pressure. The whole point of the v2.0 pivot is avoiding a separate inference server. KV cache is shared with the 35B production server. | 35B self-classify with `max_tokens=4–8, temperature=0`. Locked decision (STATE.md). Future upgrade path preserved per selfrouting doc §19. |
| Selfroute result as training signal (retraining loop integration) | Selfroute decisions could feed back into ML retraining pipeline | ML pipeline is dormant. Wiring selfroute decisions into HardCaseDatasetWriter, TeacherLabeler, and RetrainingService re-activates dormant ML infrastructure and creates an active dependency on something the pivot is trying to sideline. | DecisionLog captures routing reasons including SelfRouteClassify; operator can analyze JSONL offline. No live feedback loop in v2.0. |
| Per-message classification (every message, not just the latest) | Routing on the full conversation might catch edge cases where early messages indicate complexity | Multiple 35B calls per request multiplies token cost and latency. One classification call on the concatenated context (or just the last user message) is the correct scope. | Single self-classify call per request on the last user message content (or a truncated window). Sticky session handles the continuity case: once 122B for this session, always 122B. |
| Persistent session store (Redis, SQLite) | Session continuity should survive router restarts | Over-engineering for a single-host loopback service. The session store only needs to outlive a single interactive session (~30 min). Router restarts during active sessions are rare operational events, not a design target. State is recoverable: next request gets selfrouted fresh. | In-memory `ConcurrentDictionary` with TTL-based expiry. Survives within a process lifetime. On restart, sessions start fresh (acceptable: Hermes agent is interactive, not a long-lived batch pipeline). |
| Session_id required (hard reject without it) | Forces all clients to propagate session_id, ensuring consistent sticky behavior | Breaks backward-compat with existing v1.x Hermes clients that don't yet send session_id. Also breaks Graphify (task-field client, no session concept needed). | Session_id is optional. Absent → no sticky, selfroute applies normally. Present → sticky check applies. Hermes migration to sending session_id is a separate Hermes-side concern, not a router hard requirement. |
| Hard Rules as a configurable keyword list in appsettings.json | Operators might want to add/remove hard-rule keywords without code changes | Configurability sounds good but adds ambiguity: what happens if operator accidentally clears the list? Keyword list is a safety mechanism, not a routing preference. Changing it should require deliberate code change and test update. | Hard-code the keyword list in `HardRules.fs`. Document the list in README §5 (routing pipeline). Operator who needs to change it edits the source and redeploys (same pattern as Phase 12 heuristic retirement). |
| Selfroute call for streaming requests | Might seem inconsistent to classify non-streaming but not streaming | Phase 14 quality fallback intentionally skips streaming (chunks already shipped; cannot retract). However, selfrouting is PRE-routing, not post-routing — it runs before any dispatch. So selfroute CAN apply to streaming: it classifies, decides 35B or 122B, then the streaming response is sent from the chosen model. Hard Rules ALSO apply to streaming (pre-dispatch, same as non-streaming). | Hard Rules and selfroute both apply to streaming requests. Quality fallback (post-routing) is still intentionally skipped for streaming per Phase 14 decision. This is the correct layering. |

---

## Feature Dependencies

```
[Hard Rules — stage 0]
    └──must precede──> [35B self-classify — stage 1]
                           └──must precede──> [Sticky session check — stage 2]
                                                  └──must precede──> [Task table — stage 3 (existing)]
                                                                         └──must precede──> [Model override — stage 4 (existing)]

[Prompt-hash cache]
    └──enhances──> [35B self-classify]
                       (cache hit skips 35B call)

[Session_id extraction (middleware)]
    └──required by──> [Sticky session check]
                          └──writes to──> [In-memory session store]

[DecisionLog extension (new RoutingReason DU cases)]
    └──required by──> [Hard Rules observability]
    └──required by──> [SelfRouteClassify observability]
    └──required by──> [StickySession observability]

[Hermes Agent session_id propagation]
    └──enables──> [Sticky session check] (for Hermes sessions)
    └──requires──> [Session_id extraction]

[Quality fallback — Phase 14-16, retained]
    └──runs AFTER──> [Selfrouting pipeline completes]
    └──may upgrade──> [35B decision to 122B]
    └──should write to──> [Session store when upgrade happens]
        (if quality fallback upgrades to 122B, next request in session should be sticky-122B)

[ML routing — Phase 6-11, retained but dormant]
    └──bypassed by──> Routing.Mode = "selfrouting" (v2.0 default)
    └──reactivatable via──> Routing.Mode = "ml" config switch
```

### Dependency Notes

- **Hard Rules must precede selfroute:** Selfroute cannot be trusted to reliably detect LLVM/compiler/segfault keywords — 35B may classify these as "probably manageable" (doc §7, overconfidence). Hard Rules gate these before selfroute is invoked. Order is non-negotiable.

- **Sticky session must precede selfroute call (or short-circuit it):** If session is already escalated to 122B, there is no need to invoke 35B for classification. The sticky check can short-circuit to 122B without burning tokens on a routing call. Check sticky BEFORE calling 35B.

- **Quality fallback (Phase 14) should write to session store:** If a 35B response fails quality check and is retried on 122B (Phase 14 FallbackTo122B), the session should be marked as 122B for continuity. This is a cross-cutting dependency between the retained quality fallback and the new session store.

- **Session_id extraction must be a middleware concern:** Similar to `CorrelationMiddleware` (Phase 5), session_id extraction should run early in the pipeline. A separate `SessionMiddleware` (or extension to CorrelationMiddleware) reads `X-Session-Id` header and stores the value in `HttpContext.Items["SessionId"]`. ChatCompletions endpoint reads from Items, not from the header directly, maintaining the same pattern.

- **Pipeline ordering (v2.0 complete):**
  `model override → task table → Hard Rules → selfroute (with cache) → sticky check → dispatch`
  Wait — this is NOT the right order. The task table is an explicit client declaration and must win over selfrouting. Hard Rules can catch things the task table doesn't cover. Correct order:
  `model override (bypass all) → task table (bypass selfroute/hard-rules if present) → Hard Rules → selfroute → sticky → 35B-default`
  The `task` field and `model` override remain authoritative — they bypass the new selfrouting pipeline entirely, consistent with locked decisions in PROJECT.md.

---

## Scoping Decisions

These answer the specific questions in the research brief:

### Should Hard Rules apply to streaming?

**YES.** Hard Rules are pre-dispatch (before any response is sent). They run in the routing decision phase, not the response phase. Phase 14's "intentionally skipped streaming" applies only to quality fallback (post-routing, post-response). Hard Rules and selfroute are pre-routing and apply identically to streaming and non-streaming requests.

### Sticky session: how does it interact with explicit `task` field?

**Task field wins — no sticky.** If a request carries an explicit `task` field (Graphify), it routes through the task table directly, bypassing both selfroute and sticky. The sticky store is ONLY written for requests that went through selfroute (i.e., Hermes-style requests without a `task` field). Graphify sessions are stateless by design — each request declares its own task type.

### 35B self-classify caching: how does cache invalidate?

**No TTL invalidation for classification results.** The SAFE/UNSAFE verdict for a given prompt is deterministic (temperature=0). The same prompt will always get the same verdict. Cache invalidation is needed only when the routing prompt template changes (operator edit). In that case, the cache is cleared on startup (cache is in-memory, so restart = cleared). For v2.0, no hot-reload of cache on prompt file change; restart is required. This is acceptable given the selfrouting doc's conservative stance.

### Hermes session_id: required or optional?

**Optional — absent = stateless.** v1.x Hermes clients that don't send `X-Session-Id` must work unchanged. Absent session_id → selfroute applies, no sticky check, request proceeds normally. When Hermes is configured to propagate session_id (Hermes-side config change, outside smart-router's scope for v2.0), sticky session activates automatically. No router-side breaking change.

### Cascade order with quality fallback (Phase 14-16)?

```
Request arrives
    │
    ▼
[1] Model override? → YES → dispatch (bypass all)
    │ NO
    ▼
[2] Task field? → YES → task table → dispatch (bypass selfroute, sticky)
    │ NO
    ▼
[3] Hard Rules (keyword match) → HIT → route 122B, skip stages 4-5
    │ MISS
    ▼
[4] Sticky session? (session_id present + session store has 122B entry) → YES → route 122B
    │ NO
    ▼
[5] 35B self-classify (with cache) → SAFE → route 35B | UNSAFE → route 122B
    │
    ▼
[6] Dispatch to chosen model
    │
    ▼
[7] Quality fallback (non-streaming only, Phase 14-16)
    │   Response from 35B fails quality check → retry on 122B
    │   If upgrade happens → write session_id → 122B in session store
    │
    ▼
[8] Return response
```

Stages 1-5 are pre-routing (pure Core logic). Stage 7 is post-routing (Cli adapter, Phase 14-16). The cascade is explicit and ordered.

---

## MVP Definition

### Launch With (v2.0 milestone)

Minimum viable features for selfrouting paradigm to be functional and observable:

- [x] Hard Rules layer (stage 0) — keyword pre-routing, applies to all requests including streaming
- [x] 35B self-classify (1-token SAFE/UNSAFE, `max_tokens=4–8, temperature=0`) — non-streaming call to :8000
- [x] Prompt-hash cache (in-memory, bounded LRU) — skip duplicate selfroute calls
- [x] Sticky session escalation (in-memory `ConcurrentDictionary`, TTL-based expiry)
- [x] Session_id extraction middleware (`X-Session-Id` header + optional body field)
- [x] DecisionLog extension (new RoutingReason DU cases: `HardRules`, `SelfRouteClassify`, `StickySession`; schema_version bump)
- [x] ML routing path dormant (`Routing.Mode = "selfrouting"` default, `"ml"` preserved)
- [x] Quality fallback writes to session store on 122B upgrade (cross-cutting integration)

### Add After Validation (v2.x)

- [ ] Hermes Agent smoke test — requires Hermes-side session_id header addition (Hermes plugin config); manual UAT against `~/hermes-agent`
- [ ] `selfroute_reason` / `selfroute_confidence` in TraceLog — JSON mode opt-in (operator asks "why did selfroute decide SAFE?")
- [ ] Operator-tunable prompt path via `Routing.SelfRoute.PromptPath` config key

### Future Consideration (v3+)

- [ ] Speculative routing (Phase 21 optional) — only if latency profiling proves selfroute adds unacceptable overhead
- [ ] Dedicated 7B router model — selfrouting doc §19 upgrade path; only if 35B classification quality degrades under observed traffic
- [ ] Persistent session store — only if operator reports problematic session loss on router restarts

---

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| Hard Rules layer | HIGH — prevents catastrophic misrouting | LOW — keyword match, no ML | P1 |
| 35B self-classify | HIGH — replaces ML as primary routing decision | MEDIUM — HTTP call to :8000, response parsing, fallback-to-UNSAFE on parse error | P1 |
| Prompt-hash cache | MEDIUM — latency + token cost reduction | LOW — LRU cache, SHA-256 key | P1 |
| Sticky session store | HIGH — debugging continuity, core v2.0 promise | MEDIUM — ConcurrentDictionary + TTL, session_id threading | P1 |
| Session_id extraction | HIGH — enables sticky (no sticky without it) | LOW — header/body extraction, HttpContext.Items pattern | P1 |
| DecisionLog extension | HIGH — observability of new routing path is mandatory | LOW — new DU cases, optional fields, schema_version bump | P1 |
| ML path dormant | MEDIUM — future flexibility | LOW — mode-guarded DI, mirrors Phase 12 pattern | P1 |
| Quality fallback → session store | MEDIUM — sticky correctness after FallbackTo122B | LOW — one-line write to session store in ChatCompletions | P1 |
| Hermes smoke test | HIGH — validates end-to-end integration | MEDIUM — depends on Hermes-side changes | P2 |
| Selfroute JSON mode (TraceLog) | LOW — debugging aid for operator | MEDIUM — JSON parse path, additional trace fields | P2 |
| Operator-tunable prompt path | LOW — nice-to-have flexibility | LOW — config key, matches existing judge-prompt pattern | P2 |
| Speculative routing | LOW — marginal latency gain | HIGH — async cancellation, streaming cancellation complexity | P3 |

---

## v1.x Infrastructure Dependencies

The following v1.x components are **directly used** by v2.0 features with no modification needed, only wiring:

| v1.x Component | Used By (v2.0 feature) | Notes |
|----------------|----------------------|-------|
| `CorrelationMiddleware` (Phase 5) | Session_id extraction runs alongside or after correlation middleware | Session_id extraction is an additive middleware concern; `correlation_id` continues to flow through all logs unchanged |
| `QueueDispatcher` (Phase 3) | Sticky session check result feeds into dispatch decision, same as current routing decisions | `RoutingDecision.Target` remains the dispatch signal; sticky just pre-determines the target |
| `DecisionLogWriter` (Phase 5) | New DecisionLog fields for routing reason extension | Schema_version bump required; `RoutingReason` DU extension triggers exhaustive match updates across all callsites |
| `QwenUpstreamClient` (Phase 1) | 35B self-classify HTTP call reuses the existing :8000 client | Self-classify is a non-streaming `CompleteAsync` call to 35B; shares the same `IUpstreamClient` implementation. Key difference: the self-classify call must NOT go through `QueueDispatcher` (35B has no concurrency gate — only 122B does). Must call `QwenUpstreamClient` directly for the routing call. |
| `HealthService` (Phase 10) | Hard Rules + selfroute should still respect 122B reachability | If selfroute decides 122B but 122B is unreachable, existing `FallbackTo35B` logic in QueueDispatcher/ChatCompletions handles it — no change needed |
| `TraceLogger` (Phase 14) | Selfroute JSON mode (P2 feature) adds new TraceLog fields | Opt-in; no change to existing trace fields |
| `IModelVersionProvider` (Phase 8) | ML dormant path retains `CurrentVersion`; v2.0 routing decisions set `ModelVersion = "selfroute-v2.0"` or similar | Needs research on whether to repurpose ModelVersion field for selfrouting cohort or leave it blank |

**Critical wiring note — selfroute HTTP call must bypass QueueDispatcher:** The selfroute call is a short classification call (max_tokens=4–8) to 35B. QueueDispatcher wraps 35B for production requests but 35B has NO semaphore gate (only 122B does). The selfroute call can use `QwenUpstreamClient.CompleteAsync` directly (the unwrapped client). This avoids any risk of the routing call contending with production 35B traffic at the application layer. The infrastructure for direct-client access is already in the DI graph; just inject `QwenUpstreamClient` (concrete) alongside `IUpstreamClient` (the QueueDispatcher-wrapped version).

---

## Sources

- `.planning/docs/35b-selfrouting.md` — primary design doc, selfrouting architecture (§3 recommended strategy, §6-7 hard rules rationale, §10 router-must-not-think principle, §16 sticky escalation, §17 speculative routing)
- `.planning/docs/35b-selfrouting-prompt.md` — routing prompt design, SAFE-for-35B framing, continuation-aware routing, JSON output format
- `src/SmartRouter.Core/Domain.fs` — existing `RoutingReason` DU, `RouterRequest`, `RoutingDecision` — DU extension analysis
- `src/SmartRouter.Core/Routing.fs` — existing pipeline stages (tryModelOverride → tryTaskTable → algorithm) — v2.0 stages insert before/after
- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — session_id extraction middleware pattern
- `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` — IUpstreamClient wrapping pattern; confirms 35B has no semaphore gate
- `src/SmartRouter.Cli/Adapters/JudgeClient.fs` — Phase 16 judge pattern for bounded LRU cache, prompt path config — selfroute cache follows same pattern
- `src/SmartRouter.Cli/appsettings.json` — `Routing.Judge.PromptPath`, `Routing.Judge.MaxCacheEntries` as reference for selfroute config shape
- `.planning/PROJECT.md` — locked decisions for v2.0: 35B self-route, Hard Rules ONLY keyword scope, Hermes as upper layer, session_id propagation
- `.planning/STATE.md` — v2.0 design decisions locked 2026-05-11; projected phase sequence
- `.planning/MILESTONES.md` — v1.3 shipped features confirmed as baseline

---
*Feature research for: smart-router v2.0 Self-Routing + Session-Aware milestone*
*Researched: 2026-05-11*
