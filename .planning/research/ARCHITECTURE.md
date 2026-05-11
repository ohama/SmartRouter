# Architecture Research: v2.0 Selfrouting Integration

**Domain:** LLM routing proxy — selfrouting layer integration onto hexagonal v1.x
**Researched:** 2026-05-11
**Confidence:** HIGH (derived entirely from primary source: the actual v1.x codebase)

---

## Standard Architecture

### System Overview (v2.0 target)

```
┌──────────────────────────────────────────────────────────────────┐
│         Clients: Telegram / Slack / VSCode                        │
└─────────────────────────┬────────────────────────────────────────┘
                           │ HTTP (session_id via X-Session-Id header)
┌─────────────────────────▼────────────────────────────────────────┐
│              Hermes Agent  ~/hermes-agent                         │
└─────────────────────────┬────────────────────────────────────────┘
                           │ POST /v1/chat/completions
                           │ X-Session-Id: <uuid>
┌─────────────────────────▼────────────────────────────────────────┐
│                       smart-router                                │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │              CorrelationMiddleware (existing)                │ │
│  │         reads X-Session-Id → HttpContext.Items              │ │
│  └───────────────────────────┬─────────────────────────────────┘ │
│                              │                                    │
│  ┌───────────────────────────▼─────────────────────────────────┐ │
│  │  ChatCompletions handler — routing cascade                   │ │
│  │                                                              │ │
│  │  Stage 0: HardRules.fs — keyword scan → 122B (bypass all)   │ │
│  │  Stage 1: explicit model override (unchanged)               │ │
│  │  Stage 2: explicit task field (unchanged)                   │ │
│  │  Stage 3: SelfRouter.fs — 35B "SAFE?" (1-token, cached)     │ │
│  │  Stage 4: Sticky — session.current_model==122B → 122B       │ │
│  │             (reads SessionStore.fs)                          │ │
│  │  ↓ final decision                                           │ │
│  │  QueueDispatcher (existing) → Qwen upstream                 │ │
│  │  ↓ response                                                  │ │
│  │  SessionStore.Update (actual model used)                    │ │
│  │  QualityFallback / JudgeCascade (existing, unchanged)       │ │
│  └─────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  ┌───────────────────────┐  ┌──────────────────────────────────┐  │
│  │  SessionStore          │  │  ML stack (dormant)              │  │
│  │  (NEW: in-process      │  │  BgeM3Embedder, MlNetClassifier, │  │
│  │  ConcurrentDictionary) │  │  RetrainingService, CanaryService│  │
│  │  Singleton + BgSvc     │  │  All wired; not in request path  │  │
│  └───────────────────────┘  └──────────────────────────────────┘  │
└──────────┬─────────────────────────────────────────────────────────┘
           │
    ┌──────┴──────┐
    │ Qwen 35B    │   port 8000
    │ Qwen 122B   │   port 8001
    └─────────────┘
```

### Component Responsibilities

| Component | Responsibility | Communicates With |
|-----------|---------------|-------------------|
| HardRules.fs (NEW, Core) | Keyword scan; immediate 122B decision | Routing.fs pipeline |
| SelfRouter.fs (NEW, Cli Adapter) | 1-token "SAFE?" call to 35B; LRU cache by prompt_hash | "selfrouter" named HttpClient |
| SessionStore.fs (NEW, Cli Adapter) | In-process session state; sticky escalation lookup; cleanup BackgroundService | ChatCompletions handler |
| CorrelationMiddleware.fs (existing) | Reads X-Session-Id from incoming header; stores in HttpContext.Items | ChatCompletions handler |
| Routing.fs (existing, extended) | Pure pipeline: Stage 0 HardRules → Stage 1 override → Stage 2 task table → Stage 3 self-classify result | ChatCompletions handler |
| ChatCompletions.fs (existing, extended) | Runs routing cascade; calls SelfRouter and SessionStore; updates session after response | Routing.fs, SelfRouter, SessionStore, QueueDispatcher |
| QueueDispatcher (existing) | Concurrency gate for 122B; IUpstreamClient implementation | Qwen HTTP clients |
| ML stack (existing, dormant) | Retained in DI; not in request path | configureRequestPipeline (already registered) |

---

## Recommended Project Structure (new files only)

```
src/SmartRouter.Core/
├── Domain.fs               # add: SelfRouteVerdict DU, SessionState record, SelfRoute RoutingReason case
├── Ports.fs                # add: ISelfRouter port, ISessionStore port
├── HardRules.fs            # NEW: pure keyword scan; no IO; no DI
└── Routing.fs              # extend: Stage 0 HardRules call before Stage 1

src/SmartRouter.Cli/Adapters/
├── SelfRouter.fs           # NEW: ISelfRouter impl; named "selfrouter" HttpClient; LRU cache
└── SessionStore.fs         # NEW: ISessionStore impl; ConcurrentDictionary + cleanup BackgroundService
```

### fsproj Compile Order (positions matter in F#)

Existing order (abbreviated):
```
QualityCheck.fs       (position 4)
BorderlineClassifier.fs  (position 5)
JudgeClient.fs        (position 6)
...
RoutingAlgorithm.fs   (position ~13)
...
ChatCompletions.fs    (position 14)
CompositionRoot.fs    (position 16)
Program.fs            (position 17)
```

New file insertions:

**SmartRouter.Core.fsproj** — Core files compile in DU dependency order:
```
Domain.fs             (existing — add SelfRouteVerdict DU, SessionState, SelfRoute reason)
Ports.fs              (existing — add ISelfRouter, ISessionStore)
HardRules.fs          (NEW — insert after Ports.fs; depends only on Domain.fs)
ML.fs                 (existing — unchanged; after HardRules.fs)
MLPorts.fs            (existing)
CanaryPorts.fs        (existing)
RetrainingPorts.fs    (existing)
Routing.fs            (existing — extend to call HardRules; must come after HardRules.fs)
```

**SmartRouter.Cli.fsproj** — Adapter order:
```
Adapters/Json.fs
Adapters/Logging.fs
Adapters/ColdStart.fs
Adapters/QualityCheck.fs
Adapters/BorderlineClassifier.fs
Adapters/JudgeClient.fs
Adapters/DecisionLogger.fs
Adapters/DecisionLogWriter.fs
Adapters/TraceLogger.fs
Adapters/CorrelationMiddleware.fs
Adapters/RoutingAlgorithm.fs
Adapters/SelfRouter.fs      ← NEW: insert here (after RoutingAlgorithm, before MlNetClassifier)
Adapters/SessionStore.fs    ← NEW: insert here (after SelfRouter.fs)
Adapters/MlNetClassifier.fs
...
Endpoints/ChatCompletions.fs  ← consumes ISelfRouter, ISessionStore; must come after both
CompositionRoot.fs            ← registers SelfRouter + SessionStore in DI
Program.fs
```

**Rationale for SelfRouter before MlNetClassifier:** SelfRouter depends only on Domain.fs
and the "selfrouter" named HttpClient. No dependency on ML types. Placing it early avoids
any cross-cutting compile issue when ChatCompletions (position 14) needs to open it.

---

## Architectural Patterns

### Pattern 1: Hard Rules — Pure Core Function, No DI

**What:** `HardRules.fs` in `SmartRouter.Core` is a single pure function
`applyHardRules : RouterRequest -> RoutingDecision option`. Returns `Some decision` if any
keyword matches the prompt content; `None` otherwise. Called as Stage 0 in `Routing.routeRequest`.

**Why this placement:** ARCH-01 mandates Core is BCL-only. Hard rules are keyword string
comparisons — no IO, no DI, no clock. The function belongs in Core where it can be
unit-tested without spinning up a host.

**What:** Calling convention in `Routing.fs`:
```fsharp
let routeRequest config algorithm req =
    match HardRules.applyHardRules req with
    | Some decision -> Ok decision          // Stage 0: bypass everything
    | None ->
        match tryModelOverride req with     // Stage 1 (unchanged)
        ...
        | Ok None -> Ok (algorithm config req)   // Stage 3: self-classify (via algorithm closure)
```

**Keyword list:** Stored as a `ReadOnlyMemory<string>` array or `Set<string>` constant in
HardRules.fs. Not configurable at runtime (locked decision: keyword list, simple match, NOT regex).
Operator modifies the list by editing the source; this is intentional. Keywords:
`LLVM`, `MLIR`, `compiler`, `segfault`, `optimization`, `concurrency` (and case-insensitive
variants per the project docs).

### Pattern 2: SelfRouter — Mirrors JudgeClient Pattern Exactly

**What:** `SelfRouter.fs` is the ISelfRouter adapter implementing the 35B self-classify call.
Architecture is a direct clone of Phase 16 JudgeClient:
- Named HttpClient `"selfrouter"` registered in CompositionRoot with `AddResilienceHandler`
- LRU cache keyed by `prompt_hash` (SHA-256 of concatenated message content — same `computePromptHash` helper already in ChatCompletions.fs)
- 1-token response: `max_tokens=4` (slightly more than JudgeClient's `1` to handle "SAFE"/"UNSAFE" as tokens)
- `temperature=0.0`, `stream=false`
- Parse: if response contains "SAFE" → `SelfRouteVerdict.Safe`; if "UNSAFE" or unrecognized → `SelfRouteVerdict.Unsafe` (safety bias: ambiguous → 122B)
- On HTTP failure or timeout: `SelfRouteVerdict.Unsafe` (fail-safe: misrouting to 122B is cheaper than misrouting to 35B on a hard task)

**Named HttpClient decision:** Use a **new** named client `"selfrouter"` rather than reusing `upstream35b`. Rationale:
- `upstream35b` has a 300-second timeout designed for inference; selfrouting classification must be fast (5-10s timeout or it defeats the purpose)
- `upstream35b` has 3-retry AddResilienceHandler; selfrouter wants fail-fast (1 retry max, 200ms) on the classification path — a slow selfrouter is worse than no selfrouter
- `upstream35b` is registered pointing at `Upstreams.Model35B`; selfrouter shares the same `BaseAddress` but with its own timeout and retry profile
- This exactly mirrors the judge/teacher split: separate named clients for separate latency/retry profiles

**Interface:**
```fsharp
// In SmartRouter.Core.Ports
type SelfRouteVerdict = Safe | Unsafe | SelfRouterFailed of reason: string

type ISelfRouter =
    abstract member ClassifyAsync :
        promptHash: string * promptText: string * ct: CancellationToken
        -> Task<SelfRouteVerdict>
```

**Registration:** Mirrors JudgeClient conditional registration pattern. If `Routing.SelfRouter.Enabled=true` (opt-in config flag; default true for v2.0):
- Register concrete `SelfRouter` singleton + `ISelfRouter` alias
- Register named "selfrouter" HttpClient with 5s timeout, 1 retry at 200ms

### Pattern 3: SessionStore — Singleton + Cleanup BackgroundService (Triple-Reg)

**What:** `SessionStore.fs` is the `ISessionStore` adapter. In-process state only: a
`ConcurrentDictionary<string, SessionState>` where the key is `session_id` from the
`X-Session-Id` header. No external storage (Redis, SQLite) for v2.0 — local Mac deployment
with no horizontal scaling requirement.

**SessionState record** (in `SmartRouter.Core.Domain`):
```fsharp
type SessionState = {
    CurrentModel  : ModelId          // last model that served a response
    LastActivityAt: DateTimeOffset   // for TTL eviction
}
```

**Interface** (in `SmartRouter.Core.Ports`):
```fsharp
type ISessionStore =
    abstract member TryGet   : sessionId: string -> SessionState option
    abstract member Update   : sessionId: string -> model: ModelId -> unit
    abstract member Cleanup  : maxAge: TimeSpan -> unit   // called by BackgroundService
```

**DI registration:** Triple-reg pattern mirrors `DecisionLogWriter`/`TraceLogger`/`HardCaseDatasetWriter`:
```fsharp
// Concrete singleton (owns the ConcurrentDictionary + Cleanup logic)
services.AddSingleton<SessionStore>(fun _sp -> SessionStore(ttl, logger))
// ISessionStore alias — what ChatCompletions resolves
services.AddSingleton<ISessionStore>(fun sp ->
    sp.GetRequiredService<SessionStore>() :> ISessionStore)
// BackgroundService leg — periodic Cleanup
services.AddHostedService<SessionStore>(fun sp ->
    sp.GetRequiredService<SessionStore>())
```

**SessionStore is NOT a Channel-backed writer.** DecisionLogWriter uses Channel + BackgroundService
because writes need to be fire-and-forget off the hot path. SessionStore.Update is a dictionary
write — cheap, synchronous, no need for a channel. The BackgroundService leg only handles
periodic TTL eviction (call `Cleanup` every N minutes), not write buffering.

**TTL:** Configurable via `Routing.Session.TtlMinutes` (default 60). Sessions idle longer than TTL are evicted by the BackgroundService. Cleanup runs every 10 minutes (hardcoded).

### Pattern 4: Session ID — Header Mechanism (Not Body Field)

**Recommendation: `X-Session-Id` header read in `CorrelationMiddleware.fs`.**

**Rationale:**
- `task` field (body convention) maps to a domain concept (TaskType DU). Session ID is cross-cutting infrastructure, not a domain field — it belongs in the header tier alongside `X-Correlation-Id`.
- CorrelationMiddleware already reads `HttpContext.Items` and populates cross-cutting state. Extending it to also read `X-Session-Id` is a one-line addition that keeps all header-extraction logic in one file.
- The `RouterRequestWire` body type uses `[<JsonExtensionData>]` to capture unknown fields. Adding `session_id` as a body field would force a schema change visible to all callers, including clients that don't understand sessions.
- Hermes integration: Hermes Agent controls request headers; adding a header to its outgoing requests is simpler than adding a body field (no JSON schema change required on either side).

**CorrelationMiddleware extension:**
```fsharp
let sessionIdMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task =
    task {
        let cid = Guid.NewGuid().ToString("N")
        ctx.Items.[CorrelationIdKey] <- cid
        // NEW: extract X-Session-Id; store in Items for ChatCompletions to read
        let sessionId =
            match ctx.Request.Headers.TryGetValue("X-Session-Id") with
            | true, sv when sv.Count > 0 && not (String.IsNullOrWhiteSpace(sv.[0])) -> sv.[0]
            | _ -> ""   // empty = no session (stateless request; sticky skipped)
        ctx.Items.[SessionIdKey] <- sessionId
        use _ = LogContext.PushProperty("correlation_id", cid)
        ...
    }
```

**Alternative considered:** Add `session_id` to `RouterRequestWire` body (mirrors `task` field).
**Why not:** Schema change required; header approach is cleaner for cross-cutting concerns; no
motivation to expose session state to the LLM upstream (session_id is router-internal).

### Pattern 5: ML Code — Gate Behind Config Flag (Not Delete)

**Recommendation: Gate ML routing via `Routing.Mode = "selfrouting" | "ml"` config key. Default: `"selfrouting"` for v2.0.**

**Rationale:**
- Deleting ML code removes the investment in Phases 6–9 (embedder, classifier, retraining, canary). The ML training dataset and model artifacts (`datasets/training-set.jsonl`, `models/router.zip`) represent operational history.
- The `RoutingAlgorithmRegistration` pattern already exists for swapping algorithms at startup. In v1.x it was heuristic vs ML; in v2.0 it becomes selfrouting vs ML.
- A config flag means rollback to ML is one `appsettings.json` edit + restart — no rebuild.
- The `makeApplyML` closure and all ML DI registrations remain in `configureRequestPipeline`. Only `RoutingAlgorithmRegistration.Algorithm` changes: when `Routing.Mode = "selfrouting"`, the `algorithm` function becomes the selfrouting closure (wrapping `ISelfRouter`) rather than `ML.makeApplyML`.

**CompositionRoot change:** In the `RoutingAlgorithmRegistration` factory lambda:
```fsharp
services.AddSingleton<RoutingAlgorithmRegistration>(
    Func<IServiceProvider, RoutingAlgorithmRegistration>(fun sp ->
        let mode = config.["Routing:Mode"] |> Option.ofObj |> Option.defaultValue "selfrouting"
        match mode.ToLowerInvariant() with
        | "ml" ->
            // existing ML closure (unchanged)
            { Algorithm = ML.makeApplyML ...; Name = "ml"; ModelVersion = baselineVersion }
        | _ ->  // "selfrouting" or anything else
            let selfRouter = sp.GetRequiredService<ISelfRouter>()
            let sessionStore = sp.GetRequiredService<ISessionStore>()
            { Algorithm = makeSelfRoutingAlgorithm selfRouter sessionStore
              Name = "selfrouting"
              ModelVersion = "selfrouting-v1" }))
```

**`makeSelfRoutingAlgorithm`** lives in a new `Adapters/SelfRoutingAlgorithm.fs` or is inlined
into `SelfRouter.fs`. It returns a `RoutingAlgorithm` (i.e., `RoutingConfig -> RouterRequest -> RoutingDecision`).
The Stage 4 sticky check is inside this closure (reads `sessionStore.TryGet req.SessionId`).

**Note:** When `Routing.Mode = "selfrouting"`, the ML wiring (BgeM3Embedder, PredictionEnginePool,
RetrainingService, CanaryService) is still registered and still runs. The ML model still retrains
on schedule from the teacher-labeled dataset. Only the `RoutingAlgorithmRegistration.Algorithm`
function differs — the ML machinery runs "behind the scenes" accumulating data for eventual
reactivation or offline analysis.

---

## Data Flow

### Selfrouting Request Flow (v2.0)

```
POST /v1/chat/completions
X-Session-Id: abc-123
{messages: [...], stream: false}
    ↓
CorrelationMiddleware
  - cid = new Guid
  - sessionId = "abc-123" from X-Session-Id header
  - ctx.Items[CorrelationIdKey] = cid
  - ctx.Items[SessionIdKey] = "abc-123"
    ↓
ChatCompletions.handler
  - parse body → RouterRequest
  - (RouterRequest does NOT carry sessionId — it's cross-cutting infra)
    ↓
Routing.routeRequest (in Core)
  - Stage 0: HardRules.applyHardRules req
      if "LLVM" in prompt → Ok { Target=Qwen122B; Reason=HardRule "LLVM"; ... }
  - Stage 1: tryModelOverride (unchanged)
  - Stage 2: tryTaskTable (unchanged)
  - Stage 3: algorithm config req
      (algorithm = makeSelfRoutingAlgorithm closure)
      → SelfRouter.ClassifyAsync(promptHash, promptText, ct)
          - LRU cache hit? → return cached verdict
          - miss → HTTP POST to selfrouter client (35B port 8000)
            body: {messages: [...system prompt...], max_tokens:4, temperature:0, stream:false}
          - parse response: "SAFE" → Safe | _ → Unsafe
      → if Safe  → { Target=Qwen35B; Reason=SelfRoute Safe; ... }
        if Unsafe → { Target=Qwen122B; Reason=SelfRoute Unsafe; ... }
  - Stage 4: sticky override (inside algorithm closure)
      sessionStore.TryGet(sessionId from ctx.Items) 
      → if Some { CurrentModel=Qwen122B } → override to { Target=Qwen122B; Reason=StickyEscalation }
    ↓
decision = Ok { Target=...; Reason=...; IsFallback=false; ModelVersion="selfrouting-v1" }
    ↓
Phase 10 health preflight (unchanged)
    ↓
QueueDispatcher → Qwen upstream (existing, unchanged)
    ↓
response body (non-streaming path)
    ↓
SessionStore.Update(sessionId, actualTarget)   ← NEW: update sticky state
    ↓
QualityFallback / JudgeCascade (existing, unchanged)
    ↓
DecisionLogger.Log (unchanged)
```

### Session ID Flow (where sessionId is read)

`ctx.Items[SessionIdKey]` is only read inside the `makeSelfRoutingAlgorithm` closure at Stage 4.
The closure captures the `ISessionStore` singleton at DI time. It reads `sessionId` from
`req.SessionId` or — better — from `req.CorrelationId`... but `CorrelationId` is already a
separate field. The cleanest design: **extend `RouterRequest` with a `SessionId: string` field**.

```fsharp
// In SmartRouter.Core.Domain (RouterRequest record):
type RouterRequest =
    { Messages       : Message list
      ModelOverride  : string option
      Task           : string option
      Stream         : bool
      Temperature    : float option
      TopP           : float option
      MaxTokens      : int option
      CorrelationId  : string
      SessionId      : string     // ← NEW: "" when X-Session-Id absent; sticky skipped when ""
      UnknownFields  : Map<string, System.Text.Json.JsonElement> }
```

This mirrors the existing `CorrelationId` pattern (Phase 9 added `CorrelationId` to `RouterRequest`
the same way). `mapWireToRequest` in `ChatCompletions.fs` populates it from
`ctx.Items[SessionIdKey]` (which CorrelationMiddleware extracted from the header).

---

## ChatCompletions Handler Integration

### Where Selfrouting Cascade Fits

The handler currently has this skeleton (line numbers approximate from the 673-line file):

```
Line 200: handler function begins
Line 206: correlationId from ctx.Items
Line 211: parse wire body
Line 239: routeRequest call (Stage 1-3)
Line 257: Ok decision branch begins
Line 285: Phase 10 health preflight
Line 300: if req.Stream then streaming branch else non-streaming branch
Line 408: non-streaming branch: quality fallback + judge cascade
```

**Selfrouting inserts at two points:**

**Point A — Before `routeRequest` call (line ~235):**
Extract sessionId from `ctx.Items` and populate `req.SessionId` in `mapWireToRequest`.
This is a zero-cost change: `mapWireToRequest` already reads from `ctx.Items` indirectly
via `correlationId`; extend the same pattern.

**Point B — After response is received, before `DecisionLogger.Log` (line ~592):**
```fsharp
// After finalBody is determined (after quality fallback / judge cascade):
let sessionId = 
    match ctx.Items.TryGetValue(SessionIdKey) with
    | true, (:? string as sid) when not (String.IsNullOrEmpty(sid)) -> sid
    | _ -> ""
if not (String.IsNullOrEmpty(sessionId)) then
    sessionStore.Update(sessionId, finalDecision.Target)
```

This is exactly where it belongs: the session store records the **actually-served model** (post
quality fallback), not the initially-routed model. If 35B was initially routed but quality
fallback escalated to 122B, the session stores 122B — so the next sticky check correctly
continues on 122B.

**Point B placement: after judge cascade (line ~585), before DecisionLogger.Log (line ~594).**

### Streaming Branch

The selfrouting classification runs pre-routing — before any chunks are dispatched. This means
selfrouting applies equally to streaming and non-streaming requests. There is no streaming
concern for Stage 0–4 routing decisions. This is simpler than the quality-fallback situation
(which intentionally skips streaming because chunks are already shipped).

For the session update after streaming: the streaming branch currently logs at line ~385. Insert
the `sessionStore.Update` call immediately before `decisionLogger.Log` in the streaming branch
(after the normal loop exit, before `do! enumerator.DisposeAsync()`). Same pattern as the
non-streaming Point B.

**Decision: selfrouting classification applies to streaming requests. The classification is
pre-response and costs no latency to the streaming path itself.**

---

## Integration Points

### Named HttpClients — v2.0 Complete Map

| Client Name | BaseAddress | Timeout | Retry | Purpose |
|-------------|-------------|---------|-------|---------|
| upstream35b | Model35B | 300s | 3x exponential 1s | Non-streaming inference |
| upstream122b | Model122B | 300s | 3x exponential 1s | Non-streaming inference |
| upstream35b-stream | Model35B | 300s | None | Streaming inference |
| upstream122b-stream | Model122B | 300s | None | Streaming inference |
| health-probe | (absolute URLs) | 5s | None | Health probe |
| teacher | :8001 (122B) | 30s | 3x exponential 1s | Dataset labeling |
| judge | :8001 (122B) | 5s | 2x exponential 200ms | Response quality judge |
| selfrouter | Model35B | 5s | 1x 200ms | Self-classify routing |

**selfrouter** shares `BaseAddress` with `upstream35b` but uses a 5-second timeout and minimal
retry. The 300-second inference timeout would defeat the latency budget for classification.
Do NOT reuse `upstream35b` — separate named clients for separate profiles is the established
pattern in this codebase (teacher and judge both demonstrate this).

### DI Registration Summary (new components)

```
configureRequestPipeline additions (in order):

1. Named "selfrouter" HttpClient
   → AddHttpClient("selfrouter").ConfigureHttpClient(...)
   → .AddResilienceHandler("selfrouter-pipeline", ...)
   (insert near the judge client registration, around line ~570)

2. SelfRouter concrete singleton + ISelfRouter alias
   → services.AddSingleton<SelfRouter>(...)
   → services.AddSingleton<ISelfRouter>(fun sp -> sp.GetRequiredService<SelfRouter>() :> ISelfRouter)
   (conditional: if Routing.Mode = "selfrouting"; always register if selfrouting is default)

3. SessionStore triple-reg
   → services.AddSingleton<SessionStore>(...)
   → services.AddSingleton<ISessionStore>(...)
   → services.AddHostedService<SessionStore>(...)
   (unconditional — session store is useful even if ML mode is active, for future reactivation)

4. RoutingAlgorithmRegistration factory update
   → add Routing.Mode branch: "selfrouting" arm creates makeSelfRoutingAlgorithm closure
   → "ml" arm unchanged
   (replaces existing single-arm factory, ~line 394)
```

### SelfRouter Config in appsettings.json

```json
"Routing": {
  "Mode": "selfrouting",
  "SelfRouter": {
    "Endpoint": "",            // "" → derive from Upstreams.Model35B (mirrors judge pattern)
    "PromptPath": "prompts/selfrouter-prompt.md",
    "TimeoutSeconds": 5,
    "MaxCacheEntries": 5000
  },
  "Session": {
    "TtlMinutes": 60
  }
}
```

---

## Anti-Patterns

### Anti-Pattern 1: Routing the SelfRouter Call Through QueueDispatcher

**What people do:** Register ISelfRouter to call `IUpstreamClient.CompleteAsync` (the existing
queue-gated path).

**Why it's wrong:** QueueDispatcher holds a `SemaphoreSlim(1)` gate on Qwen 122B requests.
Routing the selfrouter call through QueueDispatcher would consume the gate slot for a
1-token classification request, blocking real inference traffic. JudgeClient (Phase 16) and
TeacherLabeler (Phase 7) both demonstrate the established solution: use a separate named
HttpClient that bypasses QueueDispatcher entirely.

**Do this instead:** Named "selfrouter" HttpClient that posts directly to the 35B endpoint
with no semaphore involvement.

### Anti-Pattern 2: Storing SessionId in RouterRequest.UnknownFields

**What people do:** Pass session_id through `wire.extra` (the `[<JsonExtensionData>]` dictionary)
to avoid changing `RouterRequest`.

**Why it's wrong:** `UnknownFields` is for verbatim forward-pass of unknown JSON fields to
the upstream. Using it for session state couples session tracking to JSON parsing, makes
the field discoverable by the upstream LLM (it gets forwarded), and bypasses the explicit
`SessionId: string` field that makes the contract visible to Core.

**Do this instead:** Add `SessionId: string` to `RouterRequest` (mirrors Phase 9 `CorrelationId`
addition). `mapWireToRequest` populates it from `ctx.Items[SessionIdKey]`.

### Anti-Pattern 3: Placing HardRules in the Adapter Layer

**What people do:** Put HardRules as a ChatCompletions pre-check or a RoutingAlgorithm wrapper
in `SmartRouter.Cli`.

**Why it's wrong:** Hard rules are pure keyword logic — no IO, no DI. Keeping them in Core
means they are testable without ASP.NET scaffolding, and they apply uniformly regardless
of which routing algorithm is active (ML or selfrouting). Placing them in an adapter would
require the adapter to know about the routing pipeline stages.

**Do this instead:** `SmartRouter.Core/HardRules.fs` with a single pure function
`applyHardRules : RouterRequest -> RoutingDecision option`. `Routing.routeRequest` calls
it as the first step.

### Anti-Pattern 4: Updating SessionStore Before Quality Fallback Completes

**What people do:** Call `sessionStore.Update` immediately after the initial routing decision
(before quality fallback or judge cascade).

**Why it's wrong:** If 35B was initially routed but quality fallback escalated to 122B, the
session store would record 35B. The next sticky check would not escalate — defeating the
purpose of sticky escalation for continuation workflows.

**Do this instead:** Update session store after the final decision is known — after quality
fallback and judge cascade — using `finalDecision.Target`, not `initialDecision.Target`.

### Anti-Pattern 5: Deleting ML Code From CompositionRoot

**What people do:** Remove all ML DI registrations from `configureRequestPipeline` when
selfrouting is enabled.

**Why it's wrong:** The ML retraining pipeline (RetrainingService, TeacherLabeler, FailureDetector)
accumulates labeled data in `datasets/hard-cases.jsonl` regardless of which routing algorithm
is active. This data is valuable for future ML reactivation. Removing ML DI registrations also
removes the automatic model retraining — the ML model would not stay current with the
teacher-labeled data.

**Do this instead:** Keep all ML DI registrations unconditional. Only the `RoutingAlgorithmRegistration`
changes based on `Routing.Mode`. The ML machinery continues to run in the background.

---

## Scaling Considerations

| Scale | Architecture Adjustments |
|-------|--------------------------|
| Single Mac (current) | In-process SessionStore (ConcurrentDictionary); selfrouter shared with 35B serving |
| Multi-process (future) | SessionStore would need external backing (Redis / SQLite); not a v2.0 concern |
| High-traffic (not applicable) | mlx_lm.server is the bottleneck; router overhead is minimal |

---

## Phase Build Order Recommendation

Based on integration analysis, phases should ship in this order:

1. **Phase 17 — Hard Rules (Stage 0)** — Fully independent. No new DI, no new HttpClient.
   Only changes: `HardRules.fs` (new Core file), `Domain.fs` (new RoutingReason cases),
   `Routing.fs` (call HardRules as Stage 0), tests. Shippable in isolation; passes all
   existing tests unchanged.

2. **Phase 18 — Session Store + SessionId wiring** — Independent of selfrouter HTTP call.
   `SessionStore.fs` (new Cli adapter), `SessionId` field in `RouterRequest`, CorrelationMiddleware
   extension, triple-reg in CompositionRoot, ChatCompletions Point B update. Can ship without
   selfrouter (sticky escalation does nothing until selfrouter is active — but the infrastructure
   is in place). Tests: session store unit tests + integration test for sticky header passthrough.

3. **Phase 19 — SelfRouter (35B classify, Stage 3)** — Depends on Phase 18 (SessionId in
   RouterRequest). Adds "selfrouter" named HttpClient, SelfRouter.fs, ISelfRouter port,
   RoutingAlgorithmRegistration mode switch, selfrouter-prompt.md. Tests: SelfRouter unit tests
   with mock HttpClient, end-to-end routing with SAFE/UNSAFE mock responses.

4. **Phase 20 — Hermes Integration + Routing.Mode config** — Depends on all three above.
   Adds `Routing.Mode` config key, confirms Hermes sends X-Session-Id, updates README.md
   §5 routing pipeline, §7 configuration reference, §8 endpoints (no changes to endpoint
   surface — Hermes integration is a header convention, not a new endpoint).

---

## Sources

- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — authoritative handler structure
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — authoritative compile order
- `src/SmartRouter.Cli/CompositionRoot.fs` — DI registration patterns (judge, teacher, triple-reg)
- `src/SmartRouter.Core/Domain.fs` — RouterRequest structure, RoutingReason DU
- `src/SmartRouter.Core/Routing.fs` — routeRequest pipeline
- `src/SmartRouter.Core/Ports.fs` — IUpstreamClient, IHealthProbe port patterns
- `src/SmartRouter.Cli/Adapters/JudgeClient.fs` — 1-token LRU cache pattern (SelfRouter mirrors this)
- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — HttpContext.Items cross-cutting pattern
- `.planning/docs/35b-selfrouting.md` — selfrouting design rationale, §18 architecture diagram
- `.planning/docs/35b-selfrouting-prompt.md` — SAFE/UNSAFE prompt design, max_tokens recommendation

---
*Architecture research for: smart-router v2.0 selfrouting integration*
*Researched: 2026-05-11*
