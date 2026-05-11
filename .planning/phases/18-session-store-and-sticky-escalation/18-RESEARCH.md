# Phase 18: Session Store + Sticky Escalation — Research

**Researched:** 2026-05-11
**Domain:** In-process session state + concurrent dictionary + ASP.NET Core middleware + F# BackgroundService
**Confidence:** HIGH

---

## Summary

Phase 18 is a pure infrastructure phase that adds in-process session tracking to the smart-router. Every component pattern in this phase is a direct clone or minor extension of existing Phase 5–17 patterns: `RouterRequest.SessionId` mirrors the Phase 9 `CorrelationId` field addition, `SessionStore.fs` mirrors the Phase 16 `JudgeClient.fs` LRU cache, `CorrelationMiddleware.fs` extension mirrors the existing `CorrelationIdKey` pattern, and the `SessionTtlEvictionService` mirrors the Phase 8 `RetrainingService` PeriodicTimer pattern. No new NuGet packages are required.

The cascade ordering conflict between the REQUIREMENTS.md spec and the SUMMARY.md analysis is resolved in favor of SUMMARY.md (HIGH confidence, derived from actual code): sticky escalation runs at Stage 3 (before self-classify at Stage 4), not after self-classify. This means Phase 18 wires sticky into the algorithm closure that the Phase 17 stub already occupies — the sticky check is the last thing the algorithm does before returning a decision, and self-classify (Phase 19) will be added before the sticky check inside that closure in Phase 19. For Phase 18 alone, the algorithm closure can short-circuit directly to sticky-or-default without a self-classify call.

The most important design invariant for correctness: `SessionStore.Update` MUST be called with `finalDecision.Target` AFTER quality fallback + judge cascade resolve (non-streaming Point B), not immediately after initial routing. Updating with the initially-routed model while quality fallback later escalates to 122B would record the wrong model and silently break sticky escalation for all continuation requests in that session.

**Primary recommendation:** Mirror JudgeClient.fs exactly for SessionStore — ConcurrentDictionary with Interlocked counters, AddOrUpdate with 122B-wins merge, bounded eviction. The only difference from JudgeClient is that eviction is TTL-based (per-entry LastAccessedAt DateTimeOffset + BackgroundService scan) rather than count-based (JudgeClient evicts by LRU when MaxCacheEntries exceeded).

---

## Standard Stack

All stack components are BCL or already-present NuGet.

### Core
| Component | Location | Purpose | Pattern Source |
|-----------|----------|---------|---------------|
| `ConcurrentDictionary<string, SessionState>` | BCL (net10.0) | Session store backing | JudgeClient.fs Phase 16 |
| `Interlocked.Increment` + `int64` AccessSeq | BCL | LRU ordering + atomic counters | JudgeClient.fs Phase 16 |
| `DateTimeOffset` | BCL | TTL eviction (LastAccessedAt per entry) | Phase 8 RetrainingService |
| `PeriodicTimer` | BCL (.NET 6+) | BackgroundService TTL sweep loop | Phase 8 RetrainingService |
| `ExceptionDispatchInfo.Capture` | BCL | OCE re-raise through task {} await points | Phase 8 pattern (ARCH-02) |
| `IHostedService` / `BackgroundService` | Microsoft.Extensions.Hosting | TTL cleanup registration | DecisionLogWriter, TraceLogger pattern |

### Config Keys to Add
| Key | Type | Default | Purpose |
|-----|------|---------|---------|
| `Routing:Session:TtlMinutes` | int | 30 | Session TTL; entries idle longer → evicted |
| `Routing:Session:MaxEntries` | int | 10000 | LRU cap; evict oldest-AccessSeq when exceeded |

**No new NuGet packages required.** BCL only.

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Manual `ConcurrentDictionary` + BackgroundService | `IMemoryCache` with `SlidingExpiration` | IMemoryCache adds background GC thread; inconsistent with existing JudgeClient pattern; not worth the deviation. |
| In-memory only | File-backed session persistence | Sticky escalation is a within-session concern, not durability concern. Restart-clears-sessions is acceptable and must be documented. |
| TTL via BackgroundService | Write-time TTL check | SUMMARY.md and REQUIREMENTS.md both specify BackgroundService (SES-08). Write-time check is used for JudgeClient (count-based), but session age requires a periodic sweep independent of write traffic. |

---

## Architecture Patterns

### Pattern 1: RouterRequest.SessionId Field Addition (SES-01)

**What:** Add `SessionId: string` as the 9th field of `RouterRequest` in `SmartRouter.Core/Domain.fs`, between `CorrelationId` and `UnknownFields`. Empty string = stateless request.

**Pattern reference:** Phase 9 added `CorrelationId: string` the exact same way. All test construction sites using record literal syntax (not spread) received `CorrelationId = ""`. Same pattern applies here: `SessionId = ""` sentinel everywhere.

**Construction sites that need `SessionId = ""` added:**

From codebase grep of `UnknownFields  =` (the last field before addition; all are record literals):

| File | Line context | Type |
|------|-------------|------|
| `ChatCompletions.fs` | `mapWireToRequest` return (line ~115) | Production |
| `ChatCompletions.fs` | null-body synthetic `emptyReq` (line ~216-224) | Production |
| `HardRulesTests.fs` | `mkReq` helper (line ~12-20) | Test helper |
| `HardRulesTests.fs` | inline record (line ~84) | Test inline |
| `MLRoutingTests.fs` | `mkReq` helper (line ~16-24) | Test helper |
| `MLLiveVersionTests.fs` | construction site (line ~64-72) | Test |
| `ModeSwitchTests.fs` | 3 inline records (lines ~231-238, 249-256, 270-278) | Test |
| `QueueTests.fs` | construction site (line ~31-39) | Test |
| `LoadTests.fs` | construction site (line ~24-32) | Test |

**Total: 10 construction sites** (2 production, 8 test). Compare: Phase 9 CorrelationId addition had 19 sites; test files have grown since then.

Note: `RetrainingTests.fs` uses `CorrelationId = sprintf "cid-%d" salt` but the construction is inside a record for `HardCaseEntry`, not `RouterRequest` — not affected.

**fsproj position:** Domain.fs is already position 1 in SmartRouter.Core.fsproj. No fsproj change needed for the field addition; Domain.fs is simply edited.

```fsharp
// SmartRouter.Core/Domain.fs — add SessionId after CorrelationId
type RouterRequest =
    { Messages       : Message list
      ModelOverride  : string option
      Task           : string option
      Stream         : bool
      Temperature    : float option
      TopP           : float option
      MaxTokens      : int option
      CorrelationId  : string
      SessionId      : string   // NEW Phase 18: "" = stateless (no session, sticky skipped)
      UnknownFields  : Map<string, System.Text.Json.JsonElement> }
```

### Pattern 2: RoutingReason.StickyEscalation DU Case (SES-06)

**What:** 8th case of `RoutingReason` DU in Domain.fs. No payload (target is always Qwen122B, reason string is always `"sticky_to_122b"`).

**Current DU (7 cases after Phase 17):**
```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string   // case 1
    | ExplicitTask          of taskType: TaskType       // case 2
    | Default                                           // case 3
    | ML                                                // case 4
    | FallbackTo35B                                     // case 5 (Phase 10)
    | FallbackTo122B                                    // case 6 (Phase 14)
    | HardRule                                          // case 7 (Phase 17)
    // StickyEscalation                               // ← Phase 18 adds case 8
```

**DecisionLogger.formatReason (current — 7 arms, exhaustive):**
```fsharp
let formatReason (reason: RoutingReason) : string =
    match reason with
    | ExplicitModelOverride alias -> sprintf "explicit_model:%s" alias
    | ExplicitTask taskType       -> sprintf "explicit_task:%A" taskType
    | Default                     -> "default"
    | ML                          -> "ml"
    | FallbackTo35B               -> "fallback_to_35b"
    | FallbackTo122B              -> "fallback_to_122b"
    | HardRule                    -> "hard_rule"
    // NEW: StickyEscalation      -> "sticky_to_122b"
```

**Compiler enforcement:** `TreatWarningsAsErrors=true` — adding `StickyEscalation` to the DU without adding the `formatReason` arm produces FS0025 (incomplete pattern match) which is treated as error. Build fails until all arms are added.

**Match sites that require updating after adding StickyEscalation:**
- `DecisionLogger.fs` — `formatReason` function (primary)
- `ChatCompletions.fs` — no direct match on RoutingReason; uses `formatReason` only; no change needed
- Any test that pattern-matches on `RoutingReason` DU directly (check: HardRulesTests.fs uses `Expect.equal d.Reason HardRule` which is value equality, not pattern match — safe)

Phase 17 added HardRule as case 7 with only a `formatReason` arm update. Same procedure here.

### Pattern 3: SessionState Record (SES-03)

**What:** New record type in `SmartRouter.Core/Domain.fs` (BCL-only; no Serilog, no HttpClient). ARCH-01 preserved.

```fsharp
// SmartRouter.Core/Domain.fs — add after RouterRequest type
type SessionState =
    { LastModel       : ModelId          // Qwen35B | Qwen122B — last model that served the session
      LastAccessedAt  : DateTimeOffset   // UTC; used for TTL eviction
      mutable LastAccessSeq : int64 }    // LRU ordering (Interlocked.Increment); mirrors JudgeClient CacheEntry
```

**Placement in Domain.fs:** Add after `RouterRequest` record, before `RouterError`. This is the natural domain type extension point for session-carrying fields.

**Why `mutable LastAccessSeq`:** `ConcurrentDictionary` stores reference-type entries. The `AccessSeq` field on `CacheEntry` in JudgeClient.fs is also `mutable` and updated in place without replacing the dictionary entry. Same pattern here. The mutable field is on the record itself (not wrapped), consistent with JudgeClient.

### Pattern 4: SessionStore Cli Adapter (SES-02 + SES-03)

**What:** New file `src/SmartRouter.Cli/Adapters/SessionStore.fs`. Mirrors JudgeClient.fs structure.

**fsproj insertion position:** After `JudgeClient.fs` (position 6 in current Cli.fsproj). Before `DecisionLogger.fs` (which is position 7). The current compile order in SmartRouter.Cli.fsproj:

```
Adapters/Json.fs
Adapters/Logging.fs
Adapters/ColdStart.fs
Adapters/QualityCheck.fs
Adapters/BorderlineClassifier.fs
Adapters/JudgeClient.fs            ← position 6 (reference for LRU pattern)
Adapters/DecisionLogger.fs         ← position 7
Adapters/DecisionLogWriter.fs
Adapters/TraceLogger.fs
Adapters/CorrelationMiddleware.fs  ← will be extended (Phase 18 adds SessionIdKey)
Adapters/RoutingAlgorithm.fs
... (ML adapters)
Endpoints/ChatCompletions.fs       ← consumes ISessionStore
CompositionRoot.fs                 ← registers SessionStore triple-reg
```

Insert `Adapters/SessionStore.fs` between `JudgeClient.fs` and `DecisionLogger.fs`. SessionStore has no dependency on DecisionLogger, but placing it before DecisionLogger is cleaner (leaf adapters before consumers).

**SessionStore public API:**

```fsharp
type ISessionStore =
    /// Returns None when sessionId is empty, unknown, or TTL-expired.
    abstract member TryGet   : sessionId: string -> SessionState option
    /// No-op when sessionId is empty string (stateless path).
    /// Uses AddOrUpdate with 122B-wins merge (Pitfall 4 prevention).
    abstract member Update   : sessionId: string -> model: ModelId -> unit

// ISessionStore definition placement: in SessionStore.fs (Cli adapter), NOT in Core Ports.fs.
// Rationale: ISessionStore is Cli-only infrastructure; Core has no need to reference it.
// Phase 19's SelfRouter closure will receive ISessionStore via DI injection in CompositionRoot,
// not via Core Ports. This mirrors JudgeClient (IJudgeClient lives in JudgeClient.fs, not Ports.fs).
```

**AddOrUpdate 122B-wins merge (SES-03 — critical for correctness):**

```fsharp
// Inside SessionStore.Update:
let newState = {
    LastModel      = model
    LastAccessedAt = DateTimeOffset.UtcNow
    LastAccessSeq  = Interlocked.Increment(&globalSeq)
}
store.AddOrUpdate(
    sessionId,
    addValue          = newState,
    updateValueFactory = fun _ oldState ->
        // 122B-wins: if either old or new is Qwen122B, keep Qwen122B.
        // Prevents a concurrent 35B write from overwriting a 122B escalation.
        let winningModel =
            if oldState.LastModel = Qwen122B || newState.LastModel = Qwen122B
            then Qwen122B
            else newState.LastModel
        { LastModel      = winningModel
          LastAccessedAt = DateTimeOffset.UtcNow
          LastAccessSeq  = Interlocked.Increment(&globalSeq) })
|> ignore
```

**LRU eviction (SES-02 — bounded ~10000 entries):**

```fsharp
// Same pattern as JudgeClient setCached:
if store.Count >= maxEntries then
    try
        let minKv = store |> Seq.minBy (fun kv -> kv.Value.LastAccessSeq)
        store.TryRemove(minKv.Key) |> ignore
    with _ -> ()   // concurrent eviction race is acceptable
```

Note: The `MaxEntries` LRU cap fires on Update (write path). TTL eviction is separate — fires on the BackgroundService timer tick. Both policies coexist.

**DI registration — triple-reg (SES-02 mirrors DecisionLogWriter/TraceLogger):**

```fsharp
// CompositionRoot.configureRequestPipeline — add UNCONDITIONALLY (not mode-gated):
services.AddSingleton<SessionStore>(fun sp ->
    SessionStore(
        maxEntries  = sessionMaxEntries,   // from Routing:Session:MaxEntries
        ttlMinutes  = sessionTtlMinutes,   // from Routing:Session:TtlMinutes
        sp.GetRequiredService<ILogger<SessionStore>>()))
|> ignore

services.AddSingleton<ISessionStore>(fun sp ->
    sp.GetRequiredService<SessionStore>() :> ISessionStore)
|> ignore

services.AddHostedService<SessionStore>(fun sp ->
    sp.GetRequiredService<SessionStore>())
|> ignore
```

**Why unconditional (not `Routing.Mode`-gated):** Session store is useful in both modes. If operator reverts to `Routing.Mode="ml"`, sticky escalation still provides debugging continuity. Gating on mode would break `Routing.Mode="ml"` sticky behavior unnecessarily.

**SessionStore config read pattern:** Use direct `config.["Routing:Session:TtlMinutes"]` + `int.Parse` or `config.GetSection("Routing:Session").Get<SessionOptions>()` with CLIMutable. The JudgeOptions binding uses `Configure<JudgeOptions>(config.GetSection("Routing:Judge"))` — use the same pattern.

```fsharp
// New CLIMutable binding type in CompositionRoot.fs (or SessionStore.fs):
[<CLIMutable>]
type SessionOptions = {
    mutable TtlMinutes : int    // default 30
    mutable MaxEntries : int    // default 10000
}
```

**Phase 13-05 + 14-02 lesson:** All `[<CLIMutable>]` types with `int` or `bool` fields MUST use `mutable` field declarations. F# record fields are immutable by default; the JSON binding infrastructure writes through property setters only available on mutable fields. Missing `mutable` causes silent defaults (0, false) rather than config values.

### Pattern 5: CorrelationMiddleware Extension (SES-04)

**What:** Extend `CorrelationMiddleware.fs` to read `X-Session-Id` header and store in `HttpContext.Items`. One-liner addition.

**Current middleware (full file, 38 lines):** Generates correlation ID, stores in Items, pushes to Serilog LogContext, registers OnStarting callback for response header.

**Extension pattern:**

```fsharp
[<Literal>]
let SessionIdKey = "SessionId"   // NEW: add alongside CorrelationIdKey

let correlationMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task =
    task {
        let cid = Guid.NewGuid().ToString("N")
        ctx.Items.[CorrelationIdKey] <- cid
        // NEW: read X-Session-Id header; empty string = stateless (Pitfall 7 prevention)
        let sessionId =
            match ctx.Request.Headers.TryGetValue("X-Session-Id") with
            | true, sv when sv.Count > 0 && not (System.String.IsNullOrWhiteSpace(sv.[0])) -> sv.[0]
            | _ -> ""
        ctx.Items.[SessionIdKey] <- sessionId
        use _ = LogContext.PushProperty("correlation_id", cid)
        ctx.Response.OnStarting(fun () ->
            ctx.Response.Headers.[CorrelationIdHeader] <- Microsoft.Extensions.Primitives.StringValues(cid)
            Task.CompletedTask) |> ignore
        do! next.Invoke(ctx)
    }
```

**Key invariant:** When header is absent OR whitespace-only, `sessionId = ""`. This prevents the v1.x backward-compat pitfall (Pitfall 7): if empty string were used as a real session key, all sessionless clients would share one sticky bucket.

### Pattern 6: ChatCompletions.fs Integration (SES-05 + SES-07)

**Point A — mapWireToRequest (SES-05 first part):**

```fsharp
// mapWireToRequest signature change: add sessionId parameter
let private mapWireToRequest (correlationId: string) (sessionId: string) (wire: RouterRequestWire) : RouterRequest =
    // ... existing mapping ...
    { Messages       = messages
      ModelOverride   = ...
      Task            = ...
      Stream          = ...
      Temperature     = ...
      TopP            = ...
      MaxTokens       = ...
      CorrelationId   = correlationId
      SessionId       = sessionId     // NEW
      UnknownFields   = unknownFields }
```

In the handler, extract `sessionId` from `ctx.Items`:

```fsharp
// In handler, alongside existing correlationId extraction (line ~207):
let sessionId =
    match ctx.Items.TryGetValue(SessionIdKey) with
    | true, (:? string as sid) when not (String.IsNullOrEmpty(sid)) -> sid
    | _ -> ""
let req = mapWireToRequest correlationId sessionId wireBody
```

**Point B — sticky cascade stage (SES-05 second part):**

The cascade stage check happens inside the algorithm closure (not in `routeRequest`). In Phase 18, the selfrouting algorithm stub in CompositionRoot returns `Qwen35B/Default`. Phase 18 replaces the stub with a closure that:
1. Calls `sessionStore.TryGet(req.SessionId)` 
2. If `Some { LastModel = Qwen122B }` → return `{ Target=Qwen122B; Priority=High; Reason=StickyEscalation; IsFallback=false; ModelVersion="selfrouting-v1" }`
3. Otherwise → return `Qwen35B/Default` (same as current stub)

This is the correct placement: inside the `RoutingAlgorithmRegistration.Algorithm` closure in CompositionRoot, NOT in `routeRequest` itself (which is a Core function). The sticky check reads from an `ISessionStore` singleton resolved via DI — Core cannot hold that reference.

**The cascade order for Phase 18 (Stage 3 = algorithm closure = sticky-or-default):**
```
Stage 0: HardRules.applyHardRules (keyword scan → 122B or None)
Stage 1: tryModelOverride (explicit model= field → decision or None)
Stage 2: tryTaskTable (explicit task= field → decision or None)
Stage 3: algorithm config req (closure: sticky-or-default)
         → sessionStore.TryGet(req.SessionId)
            | Some { LastModel=Qwen122B } → StickyEscalation → Qwen122B
            | _ → Default → Qwen35B
```

Phase 19 will INSERT self-classify BEFORE the sticky check inside the Stage 3 closure:
```
Stage 3: algorithm config req (closure: self-classify → sticky → default)
         → selfRouter.ClassifyAsync(...)  [Phase 19 inserts here]
         → sessionStore.TryGet(req.SessionId)  [sticky check remains]
         → Default → Qwen35B
```

**CASCADE ORDERING CONFLICT RESOLUTION:** SES-05 says sticky runs "AFTER Hard Rules + explicit overrides + self-classify" but Phase 19 has not shipped yet. For Phase 18, sticky runs AFTER Hard Rules + explicit overrides (Stages 0-2) and BEFORE QueueDispatcher — which is correct. When Phase 19 adds self-classify into Stage 3, it inserts BEFORE sticky within the Stage 3 closure. The SES-05 wording "after self-classify" describes the Phase 19 final state. For Phase 18 alone, sticky IS Stage 3. SUMMARY.md canonical order (Stage 3 = sticky before self-classify at Stage 4) is authoritative for the final Phase 19 state, not a conflict with SES-05.

**Point B — session store write after quality fallback (SES-07):**

In the non-streaming branch, after the full judge cascade resolves and `finalDecision` is known (line ~587 in current ChatCompletions.fs, immediately before `decisionLogger.Log` on line ~594):

```fsharp
// Point B: write session store — AFTER finalDecision is known (post-quality-fallback)
// req.SessionId = "" means stateless request; skip write (Pitfall 7 prevention)
if not (String.IsNullOrEmpty(req.SessionId)) then
    sessionStore.Update(req.SessionId, finalDecision.Target)

// Existing DecisionLog write (unchanged):
let okReason = formatReason finalDecision.Reason
decisionLogger.Log(...)
```

**For streaming branch:** Session store write goes immediately before `decisionLogger.Log` in the normal exit path (after the `if not sentDone` block and `do! enumerator.DisposeAsync()`, at approximately line ~384-385 in current ChatCompletions.fs). Streaming does NOT have quality fallback (intentionally skipped), so `finalDecision.Target = decision.Target` (the initial routing decision) for streaming.

**Both quality-fallback scenarios (SES-07) are covered by Point B placement:**
- Phase 15 `Bad` arm: `finalDecision = retryDecision` (Qwen122B); Point B writes Qwen122B.
- Phase 16 judge `RouteNo` arm with successful retry: `finalDecision = retryDecision` (Qwen122B); Point B writes Qwen122B.
- Phase 16 judge `RouteNo` arm with failed 122B retry: `finalDecision = initialDecision` (Qwen35B); Point B writes Qwen35B. Acceptable — retry failed so 35B is what the client received.

**ISessionStore injection into ChatCompletions handler:** Resolve via `ctx.RequestServices.GetService<ISessionStore>()` (nullable resolution, like `judgeClient`) so that if SessionStore is somehow not registered, the handler degrades gracefully rather than throwing. However, SessionStore should always be registered unconditionally, so `GetRequiredService` is also fine. Use the same null-safe pattern as judgeClient to be consistent.

### Pattern 7: SessionTtlEvictionService BackgroundService (SES-08)

**What:** `SessionStore` class itself implements `BackgroundService` (or the `SessionStore` + a separate `SessionTtlEvictionService` — see decision below).

**Decision: `SessionStore` implements `BackgroundService` directly** (same as `DecisionLogWriter`). Triple-reg pattern registers the concrete singleton for three interfaces. This keeps the eviction logic co-located with the dictionary it manages, avoids dependency injection cycles, and mirrors the existing DecisionLogWriter pattern exactly.

**PeriodicTimer pattern (mirrors Phase 8 RetrainingService):**

```fsharp
// Inside SessionStore.ExecuteAsync:
override _.ExecuteAsync(ct: CancellationToken) : Task =
    task {
        use timer = new PeriodicTimer(TimeSpan.FromMinutes(5.0))  // hardcoded interval; SES-08
        let mutable go = true
        while go && not ct.IsCancellationRequested do
            try
                let! _ = timer.WaitForNextTickAsync(ct)
                // Evict entries older than TtlMinutes
                let cutoff = DateTimeOffset.UtcNow.AddMinutes(float -ttlMinutes)
                for kv in store do
                    if kv.Value.LastAccessedAt < cutoff then
                        store.TryRemove(kv.Key) |> ignore
            with
            | :? OperationCanceledException ->
                go <- false
            | ex ->
                logger.LogError(ex, "SessionTtlEvictionService: unexpected error in eviction loop")
                // Continue — do not exit the loop on non-cancellation exceptions
    }
```

**ExceptionDispatchInfo.Capture pattern (SES-08):** The Phase 8 pattern captures OCE before an await point and re-raises it after to propagate through `task {}`. However, the simpler pattern used above (match on `OperationCanceledException` in `with` block) is equivalent and preferred when the `task {}` block is simple. The Phase 8 `ExceptionDispatchInfo.Capture` pattern is needed when the OCE must be re-thrown after an async binding (`do!`). For this eviction loop, catching and breaking the while loop is sufficient.

### Pattern 8: Config CLIMutable Type (SES-09)

**Session config binding:**

```fsharp
// In SessionStore.fs or CompositionRoot.fs:
[<CLIMutable>]
type SessionOptions = {
    mutable TtlMinutes : int    // Routing:Session:TtlMinutes; default 30
    mutable MaxEntries : int    // Routing:Session:MaxEntries; default 10000
}
```

**appsettings.json addition:**

```json
"Routing": {
  "Mode": "selfrouting",
  "Session": {
    "TtlMinutes": 30,
    "MaxEntries": 10000
  }
}
```

**Bind pattern (mirrors JudgeOptions):**

```fsharp
// In CompositionRoot:
services.Configure<SessionOptions>(config.GetSection("Routing:Session")) |> ignore
// Then resolve via:
let sessionOpts = sp.GetRequiredService<IOptions<SessionOptions>>().Value
let ttlMinutes  = if sessionOpts.TtlMinutes <= 0 then 30  else sessionOpts.TtlMinutes
let maxEntries  = if sessionOpts.MaxEntries  <= 0 then 10000 else sessionOpts.MaxEntries
```

### Anti-Patterns to Avoid

- **Anti-Pattern 1: Using `""` as a real session key** — Empty `SessionId` means stateless; NEVER call `sessionStore.Update("")` or `sessionStore.TryGet("")`. Guard: `if not (String.IsNullOrEmpty(req.SessionId)) then ...` before every session store call. This is Pitfall 7 from PITFALLS.md — all v1.x clients (no X-Session-Id header) would share one sticky bucket permanently escalating to 122B.

- **Anti-Pattern 2: Updating session store before quality fallback** — `sessionStore.Update` MUST use `finalDecision.Target`, not `initialDecision.Target`. If 35B was routed initially but 122B served (quality fallback), recording 35B breaks sticky for continuation. See ARCHITECTURE.md Anti-Pattern 4.

- **Anti-Pattern 3: Placing ISessionStore in Core Ports.fs** — SessionStore is Cli adapter infrastructure. Core has no dependency on in-process state machines. Interface definition belongs in SessionStore.fs (Cli), same as IJudgeClient is defined in JudgeClient.fs.

- **Anti-Pattern 4: Mode-gating SessionStore DI** — SessionStore should be registered unconditionally regardless of `Routing.Mode`. A user who reverts to `"ml"` mode still benefits from sticky escalation.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Thread-safe session dictionary | Custom lock-based map | `ConcurrentDictionary` (BCL) | TOCTOU-safe `AddOrUpdate` with factory; proven in JudgeClient |
| 122B-wins concurrent merge | Two separate read+write operations | `ConcurrentDictionary.AddOrUpdate` with merge function | Atomic read-modify-write; no window for race where 35B overwrites 122B |
| LRU bounded eviction | Complex eviction data structure | `Seq.minBy(_.AccessSeq)` + `TryRemove` | O(n) scan acceptable at n<10,000; same pattern as JudgeClient |
| TTL cleanup | Per-request TTL check | `PeriodicTimer` BackgroundService | Phase 8 proven pattern; decoupled from hot request path |
| Session ID header extraction | Custom middleware | Extend existing `CorrelationMiddleware.fs` | Keeps all header extraction in one file; reuses Items pattern |

**Key insight:** Every non-trivial piece of Phase 18 has an exact implementation template in the existing codebase. JudgeClient.fs is the SessionStore blueprint; CorrelationMiddleware.fs is the header-extraction blueprint; DecisionLogWriter is the BackgroundService triple-reg blueprint; RetrainingService is the PeriodicTimer blueprint.

---

## Common Pitfalls

### Pitfall 1: Empty SessionId Creates Session Store Entry (Stateless Clients Break)
**What goes wrong:** If `sessionStore.Update("")` is called, all requests without `X-Session-Id` header share one sticky bucket. First 122B routing permanently escalates every future sessionless request.
**Why it happens:** Missing null-guard before session store write.
**How to avoid:** Guard every session store call: `if not (String.IsNullOrEmpty(req.SessionId)) then ...`
**Warning signs:** `routing_reason="sticky_to_122b"` appearing on requests with no `X-Session-Id` in DecisionLog.

### Pitfall 2: Session Store Write Before Quality Fallback Resolves
**What goes wrong:** Session records Qwen35B even though quality fallback escalated to Qwen122B. Continuation requests route to 35B instead of 122B.
**Why it happens:** Point B placed before the judge cascade block instead of after.
**How to avoid:** Point B (session write) MUST be after the full `let! (finalDecision, finalBody, ...) = task { ... }` block, not inside it.
**Warning signs:** Integration test "quality-fallback-writes-session" fails — second request after quality fallback routes to 35B.

### Pitfall 3: CLIMutable mutable Missing on SessionOptions Fields
**What goes wrong:** `sessionOpts.TtlMinutes = 0` and `sessionOpts.MaxEntries = 0` at runtime even when appsettings.json has valid values. Session evicts immediately (TTL 0 min) and allows no entries (MaxEntries 0).
**Why it happens:** F# record fields without `mutable` cannot be set by JSON binding infrastructure.
**How to avoid:** All `[<CLIMutable>]` types must have `mutable` on every field. Phase 13-05 + 14-02 lesson.
**Warning signs:** All sessions evict immediately; store always empty.

### Pitfall 4: AddOrUpdate Without 122B-Wins Merge
**What goes wrong:** Two concurrent requests on same session — one routes 35B, one routes 122B. Both call `store.[sessionId] <- newState` (direct assignment). The 35B write races against the 122B write; 35B wins and overwrites 122B. Session shows 35B; next sticky check routes to 35B, losing escalation.
**Why it happens:** Using `store.[key] <- value` (assignment) instead of `AddOrUpdate`.
**How to avoid:** Always use `store.AddOrUpdate(key, newState, fun _ old -> if old.LastModel=Qwen122B || new.LastModel=Qwen122B then 122B-state else new-state)`.
**Warning signs:** Success Criterion 5 (concurrent-write 122B-wins) integration test fails.

### Pitfall 5: Cascade Stage Ordering — Sticky Applied Too Late or Too Early
**What goes wrong:** If sticky check runs AFTER self-classify (Phase 19), escalated sessions burn a classify token unnecessarily. If sticky runs BEFORE Hard Rules, a keyword-match prompt could be sent to 35B if the session is sticky-to-35B.
**Why it happens:** Misreading SES-05 wording ("after self-classify") out of context.
**How to avoid:** For Phase 18, sticky IS Stage 3 (the algorithm closure). Hard Rules is Stage 0 (in `routeRequest` — always fires first). When Phase 19 adds self-classify, it inserts BEFORE sticky inside Stage 3.
**Warning signs:** Hard Rules test passes but a sticky-to-35B session bypasses Hard Rules (sticky checked before Hard Rules — impossible given Stage 0 position, but verify).

### Pitfall 6: Session Store Not Injected Into ChatCompletions Handler
**What goes wrong:** `ISessionStore` is registered in DI but not resolved in `handler` or `mapEndpoints`. Sticky never fires; no session writes happen; all phase functionality silently absent.
**Why it happens:** Forgetting to add `ISessionStore` to the handler parameter list or resolve via `ctx.RequestServices`.
**How to avoid:** Use same pattern as `IJudgeClient` — resolve via `ctx.RequestServices.GetService<ISessionStore>()` (nullable) or `GetRequiredService<ISessionStore>()` (non-nullable) inside handler.
**Warning signs:** No `routing_reason="sticky_to_122b"` appears in DecisionLog even after first 122B session request.

---

## Code Examples

### SessionStore.fs Skeleton

```fsharp
// src/SmartRouter.Cli/Adapters/SessionStore.fs
module SmartRouter.Cli.Adapters.SessionStore

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SmartRouter.Core.Domain

type ISessionStore =
    abstract member TryGet : sessionId: string -> SessionState option
    abstract member Update  : sessionId: string -> model: ModelId -> unit

type SessionStore(maxEntries: int, ttlMinutes: int, logger: ILogger<SessionStore>) =
    let store    = ConcurrentDictionary<string, SessionState>()
    let mutable globalSeq = 0L

    let doUpdate (sessionId: string) (model: ModelId) =
        let newState = {
            LastModel      = model
            LastAccessedAt = DateTimeOffset.UtcNow
            LastAccessSeq  = Interlocked.Increment(&globalSeq) }
        // LRU cap (count-based eviction on write — mirrors JudgeClient setCached)
        if store.Count >= maxEntries then
            try
                let minKv = store |> Seq.minBy (fun kv -> kv.Value.LastAccessSeq)
                store.TryRemove(minKv.Key) |> ignore
            with _ -> ()
        // 122B-wins merge (Pitfall 4 prevention)
        store.AddOrUpdate(
            sessionId,
            addValue = newState,
            updateValueFactory = fun _ old ->
                let winning = if old.LastModel = Qwen122B || model = Qwen122B then Qwen122B else model
                { LastModel      = winning
                  LastAccessedAt = DateTimeOffset.UtcNow
                  LastAccessSeq  = Interlocked.Increment(&globalSeq) })
        |> ignore

    interface ISessionStore with
        member _.TryGet(sessionId) =
            if String.IsNullOrEmpty(sessionId) then None
            else
                match store.TryGetValue(sessionId) with
                | true, entry ->
                    entry.LastAccessSeq <- Interlocked.Increment(&globalSeq)  // LRU update
                    let age = DateTimeOffset.UtcNow - entry.LastAccessedAt
                    if age.TotalMinutes > float ttlMinutes then None   // TTL check
                    else Some entry
                | _ -> None

        member _.Update(sessionId, model) =
            if not (String.IsNullOrEmpty(sessionId)) then   // Pitfall 1 guard
                doUpdate sessionId model

    // BackgroundService TTL sweep (runs every 5 minutes; SES-08)
    interface IHostedService with
        member _.StartAsync(_ct) = Task.CompletedTask
        member _.StopAsync(_ct)  = Task.CompletedTask

    // BackgroundService ExecuteAsync — not using BackgroundService base class
    // because SessionStore is registered as concrete type + 3 interfaces.
    // Use IHostedService + a separate Task for the loop instead of inheriting.
    // (Alternatively, implement BackgroundService abstract class and cast.)
```

**Note:** The exact implementation shape (inheriting `BackgroundService` vs implementing `IHostedService` + separate loop) mirrors the DecisionLogWriter pattern in this codebase. DecisionLogWriter uses `BackgroundService` inheritance. SessionStore should do the same.

### CorrelationMiddleware Extension

```fsharp
// Add to CorrelationMiddleware.fs (after existing CorrelationIdKey constant):
[<Literal>]
let SessionIdKey = "SessionId"

// In correlationMiddleware (after ctx.Items.[CorrelationIdKey] <- cid):
let sessionId =
    match ctx.Request.Headers.TryGetValue("X-Session-Id") with
    | true, sv when sv.Count > 0 && not (String.IsNullOrWhiteSpace(sv.[0])) -> sv.[0]
    | _ -> ""
ctx.Items.[SessionIdKey] <- sessionId
```

### ChatCompletions.fs Sticky Cascade Stage

```fsharp
// In the Phase 18 selfrouting algorithm closure (CompositionRoot, replacing Phase 17 stub):
let makeStickyOrDefaultAlgorithm (sessionStore: ISessionStore) : RoutingAlgorithm =
    fun _config req ->
        // Stage 3: sticky check (Phase 18); self-classify (Phase 19) will insert before this
        match sessionStore.TryGet(req.SessionId) with
        | Some { LastModel = Qwen122B } ->
            { Target       = Qwen122B
              Priority     = High
              Reason       = StickyEscalation
              IsFallback   = false
              ModelVersion = "selfrouting-v1" }
        | _ ->
            { Target       = Qwen35B
              Priority     = Low
              Reason       = Default
              IsFallback   = false
              ModelVersion = "selfrouting-v1" }
```

### Point B Session Write (Non-Streaming)

```fsharp
// After: let! (finalDecision, finalBody, ...) = task { ... }
// Before: let okReason = formatReason finalDecision.Reason

// SES-07: write session store with actually-served model
if not (String.IsNullOrEmpty(req.SessionId)) then
    sessionStore.Update(req.SessionId, finalDecision.Target)

let okReason = formatReason finalDecision.Reason
decisionLogger.Log(...)
```

---

## State of the Art

| Old Approach | Current Approach | Changed | Impact |
|--------------|------------------|---------|--------|
| Stateless routing (v1.x) | Session-aware sticky escalation | Phase 18 (v2.0) | Debugging continuity across turns in same session |
| 7 RoutingReason cases (Phase 17) | 8 cases + StickyEscalation | Phase 18 | `routing_reason="sticky_to_122b"` in DecisionLog |
| Algorithm stub (Phase 17 selfrouting) | Sticky-or-default algorithm | Phase 18 | Real sticky behavior; Phase 19 adds self-classify before sticky |
| CorrelationMiddleware reads one header | Reads two headers (X-Correlation-Id, X-Session-Id) | Phase 18 | X-Session-Id propagated to req.SessionId |

---

## Open Questions

1. **Cascade ordering for Phase 18 standalone (resolved)**
   - What we know: SES-05 says "after self-classify"; SUMMARY.md says sticky is Stage 3, self-classify is Stage 4 (sticky BEFORE self-classify). Both are correct from different vantage points: SES-05 describes Phase 19 final state; Phase 18 alone has no self-classify; sticky IS Stage 3.
   - Recommendation: Phase 18 wires sticky as Stage 3 (algorithm closure). Phase 19 inserts self-classify before sticky in Stage 3. No conflict.

2. **SessionStore public API shape**
   - What we know: REQUIREMENTS.md SES-02/SES-03 specify `Update` and session read. ARCHITECTURE.md specifies `TryGet`, `Update`, `Cleanup`. SUMMARY.md says `TryGet + Update`.
   - Recommendation: `ISessionStore` with two methods: `TryGet(sessionId): SessionState option` and `Update(sessionId, model): unit`. `Cleanup` is internal to `SessionStore` (called by BackgroundService), not part of `ISessionStore`. Phase 19 SelfRouter will use the same two-method interface.

3. **configureWithoutMl backward-compat for SessionStore**
   - What we know: STATE.md mentions `configureWithoutMl` as a backward-compat alias for tests that don't use ML. SessionStore registration is unconditional (both modes). `configureWithoutMl` likely skips ML DI but should include SessionStore.
   - Recommendation: Add `SessionStore` triple-reg to both `configureRequestPipeline` and `configureWithoutMl` (if the latter exists as a separate function). Inspect `configureWithoutMl` during plan execution to confirm.
   - Note: STATE.md Pending Todos mentions "Remove configureServices backwards-compat alias after ModelsTests migration" — check if this alias still exists before Phase 18-02 execution.

4. **Sticky applies to streaming requests (resolved)**
   - What we know: ARCHITECTURE.md says "Hard Rules + sticky still apply to streaming." SR-06 says "Hard Rules + sticky still apply for streaming." Self-classify is skipped for streaming. Sticky read (Phase 18) is pre-response — no streaming concern.
   - Recommendation: For streaming branch in ChatCompletions.fs — the routing decision (including sticky check in Stage 3) fires before any SSE headers are written. Sticky applies to streaming. Session store write (Point B) for streaming fires after the SSE loop completes (before `decisionLogger.Log`).

5. **Session write for streaming branch — which model to record?**
   - What we know: Streaming has no quality fallback. The model served = the routing decision target. `finalDecision.Target` = `decision.Target` for streaming.
   - Recommendation: Record `decision.Target` (the routing decision) in session store for streaming, immediately before `decisionLogger.Log` in both normal exit path and OCE path. Skip in error path (stream_error) since the client may not have received a complete response.

---

## Plan Structure Recommendation

3 plans as projected in ROADMAP.md:

**18-01: Core domain + DU extension** (SES-01 + SES-06)
- Add `SessionId: string` to `RouterRequest` in Domain.fs
- Add `SessionState` record to Domain.fs
- Add `StickyEscalation` as 8th `RoutingReason` DU case
- Update `DecisionLogger.formatReason` to 8-arm exhaustive match (→ `"sticky_to_122b"`)
- Update all 10 RouterRequest construction sites: add `SessionId = ""`
- Update `defaultRoutingConfig` if needed (no change to RoutingConfig itself — SessionStore is Cli-only)

**18-02: SessionStore + middleware wiring** (SES-02 + SES-03 + SES-04 + SES-05 + SES-07 + SES-09 partial)
- New `SessionStore.fs` Cli adapter (ISessionStore + AddOrUpdate 122B-wins merge + LRU cap + TryGet with TTL check)
- `SessionOptions` CLIMutable type (TtlMinutes, MaxEntries)
- `appsettings.json` `Routing:Session` block added
- Extend `CorrelationMiddleware.fs` with `SessionIdKey` constant + X-Session-Id read
- `mapWireToRequest` signature change: add `sessionId` parameter; populate `req.SessionId`
- Handler: extract sessionId from `ctx.Items[SessionIdKey]`; pass to `mapWireToRequest`
- CompositionRoot: replace Phase 17 stub with `makeStickyOrDefaultAlgorithm` closure; triple-reg SessionStore
- Point B session write in non-streaming + streaming branches
- Handler ISessionStore injection

**18-03: TtlEvictionService + tests + README** (SES-08 + SES-09 docs + integration tests)
- `SessionTtlEvictionService`/BackgroundService ExecuteAsync in SessionStore.fs (PeriodicTimer 5min)
- `SessionStoreTests.fs` unit tests: concurrent-write 122B-wins, TTL expiry, LRU bound, empty-sessionId no-op, TryGet returns None for expired
- `StickyEscalationTests.fs` integration tests: sticky-after-hard-rule, stateless-no-header, quality-fallback-writes-session
- Add `SessionStoreTests` + `StickyEscalationTests` to `RouterTests.fs` `rootTests` list (PITFALL-26)
- README §5 routing pipeline: Stage 3 sticky documented, in-memory semantics (restart clears), X-Session-Id header opt-in
- README §7 config: `Routing:Session:TtlMinutes`, `Routing:Session:MaxEntries`
- README §9.1: `routing_reason="sticky_to_122b"` added; schema_version=1 unchanged

---

## Sources

### Primary (HIGH confidence — direct codebase reads)

- `src/SmartRouter.Core/Domain.fs` — RouterRequest record (10 fields); RoutingReason DU (7 cases after Phase 17); RoutingDecision; SessionId must be added between CorrelationId and UnknownFields
- `src/SmartRouter.Cli/Adapters/JudgeClient.fs` — canonical LRU cache pattern: ConcurrentDictionary, CacheEntry with mutable AccessSeq, Interlocked.Increment, TOCTOU-safe setCached, tryGetCached; direct template for SessionStore
- `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` — exact extension point; CorrelationIdKey + SessionIdKey pattern; HttpContext.Items dict; TryGetValue header read pattern
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — formatReason 7-arm match; StickyEscalation is case 8; requires 8th arm `"sticky_to_122b"`
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — mapWireToRequest (Point A); non-streaming judge cascade + Point B placement at line ~591-594; streaming Point B before decisionLogger.Log at line ~384; handler parameter list
- `src/SmartRouter.Cli/CompositionRoot.fs` — Phase 17 stub algorithm (lines ~470-477); triple-reg pattern for DecisionLogWriter (lines ~533-549); JudgeOptions binding pattern (lines ~597-672); where to insert SessionStore triple-reg + SessionOptions.Configure
- `src/SmartRouter.Cli/appsettings.json` — current structure; `Routing:Session` block position
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — compile order; SessionStore.fs insertion position between JudgeClient.fs and DecisionLogger.fs
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` — compile order; Domain.fs is position 1 (no fsproj change needed for field addition)
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests explicit list (PITFALL-26); SessionStoreTests + StickyEscalationTests must be added here

### Secondary (HIGH confidence — existing v2.0 research)

- `.planning/research/SUMMARY.md` — cascade ordering canonical spec; Phase 18 scope; sticky Stage 3 vs self-classify Stage 4 ordering
- `.planning/research/ARCHITECTURE.md` — Pattern 3 (SessionStore triple-reg); Pattern 4 (X-Session-Id header mechanism); Point A/B placement; Anti-Pattern 4 (session update timing)
- `.planning/research/PITFALLS.md` — Pitfall 4 (race condition + AddOrUpdate merge); Pitfall 5 (restart clears sessions); Pitfall 6 (unbounded growth → TTL); Pitfall 7 (null session key); Pitfall 8 (cascade ordering)
- `.planning/research/STACK.md` — SessionState record shape; write-time vs BackgroundService eviction tradeoffs; no new NuGet needed
- `.planning/REQUIREMENTS.md` — SES-01 through SES-09 full text
- `.planning/ROADMAP.md` — Phase 18 success criteria (5 criteria); plan breakdown (18-01/02/03)

---

## Metadata

**Confidence breakdown:**
- Domain field addition (SES-01): HIGH — exact Phase 9 CorrelationId mirror; 10 construction sites identified by grep
- SessionStore adapter (SES-02/03): HIGH — JudgeClient.fs is the direct template; patterns verified
- Middleware extension (SES-04): HIGH — 38-line file; one-liner addition
- ChatCompletions wiring (SES-05/07): HIGH — exact Point A/B line numbers identified in 654-line file
- DU extension (SES-06): HIGH — formatReason 7-arm exhaustive match verified; 8th arm is trivial
- BackgroundService eviction (SES-08): HIGH — Phase 8 RetrainingService pattern; PeriodicTimer; ExceptionDispatchInfo
- Config CLIMutable (SES-09): HIGH — Phase 13-05 lesson verified; mutable fields required
- Cascade ordering: HIGH — SUMMARY.md authoritative; conflict with SES-05 wording resolved
- Test fixture impact: HIGH — 10 construction sites identified (8 test files, 2 production); same as Phase 9 pattern

**Research date:** 2026-05-11
**Valid until:** 2026-06-11 (stable — no external API; BCL + existing project patterns only)
