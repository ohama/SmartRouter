# Stack Research

**Domain:** v2.0 Self-Routing + Session-Aware additions to smart-router
**Researched:** 2026-05-11
**Confidence:** HIGH

## Context

This is a SUBSEQUENT MILESTONE research document. v1.3.0 shipped the complete ML feedback arc (Phases 1–16). v2.0 replaces the ML path with `Hard Rules → 35B self-classify → sticky escalation`. The existing stack is fully validated; this document covers ONLY what is new or changed for v2.0.

**Validated stack (do not re-research):**
- F# .NET 10 + ASP.NET Core Minimal API
- HttpClientFactory + Polly (Microsoft.Extensions.Http.Resilience 10.5.0)
- System.Text.Json + FSharp.SystemTextJson 1.4.36
- Serilog 4.3.1 dual-sink
- ML.NET 5.0.0 + ONNX Runtime 1.25.1 (retained, dormant)
- Microsoft.FeatureManagement.AspNetCore 4.5.0
- Expecto 10.2.1 + testSequenced + explicit rootTests

---

## New Stack Components for v2.0

### Core Technologies

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| `ConcurrentDictionary<string, SessionState>` | BCL (net10.0) | Session store backing store | Already used in JudgeClient.fs (LRU cache) and HealthService.fs; no new NuGet dependency; thread-safe by design; `TryGetValue` / `AddOrUpdate` are lock-free for read-heavy workloads |
| Manual LRU eviction (`int64` AccessSeq + `Seq.minBy`) | BCL | Session store eviction | Identical pattern proven in JudgeClient.fs (MaxCacheEntries + globalSeq + Interlocked.Increment); O(n) eviction is acceptable for session counts (hundreds, not millions) |
| `Microsoft.Extensions.Caching.Memory` (`IMemoryCache`) | net10.0 built-in (no extra NuGet) | Alternative eviction provider with TTL | NOT recommended for session store — see Alternatives Considered. Noted here because it exists in the BCL ecosystem and will be asked about. |

### Session Store Design (Phase 19)

**Recommended:** `ConcurrentDictionary<string, SessionState>` with manual LRU, NOT `IMemoryCache`.

Rationale:
- `IMemoryCache.Set(key, value, MemoryCacheEntryOptions)` with `SlidingExpiration` is available without new NuGet but requires adding `services.AddMemoryCache()` DI registration, which pulls in `Microsoft.Extensions.Caching.Memory`. This package IS present in the BCL transitive graph for ASP.NET Core but is not yet referenced in Cli.fsproj. Adding it is a one-liner — but given that JudgeClient.fs already proves the manual pattern works (10,000-entry LRU, Interlocked counters, TOCTOU-safe approximate eviction), the manual `ConcurrentDictionary` pattern is preferred to stay consistent with existing adapter patterns and avoid `IMemoryCache`'s implicit background GC thread.
- `IMemoryCache` TTL-based expiry requires a background scan. The manual pattern defers eviction to write time, which is acceptable because sessions are written infrequently (one write per 122B decision that escalates, not per request).

**Session state record:**

```fsharp
// SmartRouter.Core — BCL-only; ARCH-01 preserved
type SessionState =
    { LastModel   : ModelId          // Qwen35B | Qwen122B
      UpdatedAt   : DateTimeOffset   // for TTL eviction
      mutable AccessSeq : int64 }    // LRU ordering (mirrors JudgeClient)
```

**Session store type:** `ConcurrentDictionary<string, SessionState>`

**Eviction policy:**
- TTL: entries older than `Session.TtlMinutes` (default 30) are evicted on write, not on a timer. Rationale: Hermes sessions are interactive; 30 min silence = session effectively ended. Proactive TTL eviction avoids a BackgroundService (one less moving part).
- Max entries: `Session.MaxEntries` (default 10,000). At max capacity, evict the LRU entry (lowest `AccessSeq`). Mirrors JudgeClient TOCTOU-safe "approximately bounded" invariant.
- Both policies applied on write (not read) — same pattern as JudgeClient `setCached`.

**Entry TTL choice — 30 minutes:** Hermes sessions map to interactive conversations. A 30-minute TTL means: if the user goes idle for 30 minutes and resumes, the next turn is re-classified as if it were a new session. This is acceptable — sticky escalation is a "within a hot debugging session" concern, not a "across days" concern. Operators can tune via `Session:TtlMinutes` in appsettings.json.

### 35B Self-Classify Wire Pattern (Phase 18)

**Recommended:** Separate named `"router"` HttpClient registered in CompositionRoot — do NOT reuse `"upstream35b"` or route through `IUpstreamClient` / `QueueDispatcher`.

Rationale: `IUpstreamClient` routes through `QueueDispatcher`, which holds the `SemaphoreSlim(1)` gate protecting 122B. Routing classify calls through `QueueDispatcher` would starve 122B inference traffic of its gate slot — the same pitfall documented in Phase 7 for TeacherLabeler (STATE.md 07-03: "Pitfall 5 enforced — TeacherLabeler uses IHttpClientFactory.CreateClient('teacher'); never IUpstreamClient/QueueDispatcher"). The `"router"` named client bypasses the gate entirely, targeting 35B directly.

**Named client:** `"router"` (mirrors `"teacher"` from Phase 7 / `"judge"` from Phase 16)

**HttpClient registration:**
```fsharp
// In CompositionRoot.configureRequestPipeline — mirrors judge registration
services.AddHttpClient("router")
    .ConfigureHttpClient(fun c ->
        c.BaseAddress <- Uri(upstreamOptsLazy.Model35B)  // same 35B port
        c.Timeout     <- TimeSpan.FromSeconds(float routerTimeoutSeconds))
    .AddResilienceHandler("router-pipeline", fun builder ->
        builder.AddRetry(buildRouterRetry ()) |> ignore)
    |> ignore
```

**Retry policy:** 1 retry at 100ms (NOT the upstream's 3×exponential). The router classify call must be fast; a slow router means the routing itself kills latency. On persistent failure, fail-open to 35B (safe default — err toward speed).

**Request body:** `max_tokens=4`, `temperature=0.0`, `stream=false`. The classify call targets the same 35B mlx_lm.server already running — no new server required (this is the locked design decision from STATE.md).

**Response parsing:** Look for `"SAFE"` or `"UNSAFE"` substring in `choices[0].message.content`. Safety bias: if neither token appears, treat as `"UNSAFE"` (route to 122B). Mirrors Phase 7 TeacherLabeler `ROUTE_122B wins on collision` and Phase 16 JudgeClient `ROUTE_NO wins on collision`.

**Classify cache:** Cache classify decisions by `promptHash` (SHA-256 of the user prompt text, same `computeContentHash` helper already in ChatCompletions.fs). Cache type: `ConcurrentDictionary<string, ClassifyVerdict>` with max 5,000 entries (smaller than judge's 10,000 because prompts repeat less than borderline response pairs). LRU eviction via `AccessSeq` pattern — identical to JudgeClient.

**SelfRouteVerdict DU:**
```fsharp
// SmartRouter.Cli.Adapters.SelfRouter — Cli-only; ARCH-01 preserved
type SelfRouteVerdict =
    | Route35B                     // SAFE — 35B can handle this
    | Route122B                    // UNSAFE — escalate to 122B
    | SelfRouteSkipped of string   // disabled or prompt-hash override
    | SelfRouteFailed  of string   // HTTP failure — fail-open to 35B
```

### Hard Rules Implementation (Phase 17)

**Recommended:** `List<string>.Exists(fun kw -> prompt.Contains(kw, StringComparison.OrdinalIgnoreCase))` — NOT regex.

Rationale:
- The keyword list is short (6 words: LLVM, MLIR, compiler, segfault, optimization, concurrency). `String.Contains` with `OrdinalIgnoreCase` is sufficient and avoids regex compilation overhead.
- `StringComparison.OrdinalIgnoreCase` is correct — all 6 keywords are ASCII; no locale sensitivity required. Case-insensitive because users write "Segfault" or "SEGFAULT" as often as "segfault".
- Configurable via `appsettings.json` `Routing:HardRules:Keywords` array (string array). Default: the 6 locked keywords.

**F# implementation:**
```fsharp
// In SmartRouter.Core — BCL-only; ARCH-01 preserved
// HardRulesConfig record (plain F# record; no IOptions)
type HardRulesConfig =
    { Keywords : string list }   // from appsettings via Cli composition

// Pure function — no IO, no mutation
let matchesHardRule (cfg: HardRulesConfig) (req: RouterRequest) : bool =
    let combined =
        req.Messages
        |> List.map (fun m -> m.Content)
        |> String.concat " "
    cfg.Keywords
    |> List.exists (fun kw ->
        combined.Contains(kw, StringComparison.OrdinalIgnoreCase))
```

**Where Hard Rules live in the pipeline:** Stage 0, BEFORE `tryModelOverride`. Hard rules override everything — even an explicit `model=35b` should yield to a Hard Rule keyword match to 122B. This is a safety mechanism, not a user convenience. The `routeRequest` function in `Routing.fs` gets a new stage prepended; the pipeline becomes: `Hard Rules → Model Override → Task Table → Self-Classify → Sticky Escalation`.

**appsettings.json shape:**
```json
"Routing": {
  "HardRules": {
    "Enabled": true,
    "Keywords": ["LLVM", "MLIR", "compiler", "segfault", "optimization", "concurrency"]
  },
  "SelfRouter": {
    "Enabled": true,
    "TimeoutSeconds": 5,
    "MaxCacheEntries": 5000,
    "PromptPath": "prompts/selfrouter-prompt.md"
  },
  "Session": {
    "TtlMinutes": 30,
    "MaxEntries": 10000
  }
}
```

### ML Dormancy Pattern (v2.0 pivot)

**Recommended:** Config-flag bypass, NOT DI removal.

Rationale: The locked decision (STATE.md line 19) is "Future option `Routing.Mode = 'ml' | 'selfrouting'` config switch preserved." The ML NuGet packages (ML.NET, ONNX Runtime) stay in Cli.fsproj. The `makeApplyML` factory and `BgeM3Embedder` / `MlNetClassifier` adapters stay compiled. The routing algorithm injected into the pipeline changes based on `Routing:Mode`:

```fsharp
// In CompositionRoot — routing algorithm selection
let algorithm : RoutingAlgorithm =
    match opts.Mode with
    | "ml" ->
        // existing ML path (Phase 6–9 code, unchanged)
        makeApplyML embedder baselineClassifier canaryClassifier canaryGate versionProvider
    | _ ->
        // v2.0 selfrouting path (new)
        makeSelfRouteAlgorithm selfRouterClient sessionStore hardRulesConfig
```

`RoutingAlgorithm` type alias is unchanged (`RoutingConfig -> RouterRequest -> RoutingDecision`). The `makeSelfRouteAlgorithm` factory returns a closure of the same shape. This is a zero-compile-order-change drop-in.

**`Routing.Mode` default for v2.0:** `"selfrouting"`. Operators who want to revert to ML routing set `Routing:Mode = "ml"` in appsettings.json (no redeploy required, restart only).

---

## New Files (v2.0 Phases 17–20)

The following new adapter files are projected. Compile order follows the existing pattern: leaf types first, consumers last.

| File | Position in fsproj | Depends On |
|------|--------------------|------------|
| `Adapters/HardRules.fs` | After `QualityCheck.fs` (position ~5) | `Domain.fs` only — BCL |
| `Adapters/SessionStore.fs` | After `HardRules.fs` | `Domain.fs` only — BCL |
| `Adapters/SelfRouter.fs` | After `QwenUpstreamClient.fs` | `HardRules.fs`, `SessionStore.fs`, `QwenUpstreamClient.fs` |
| `prompts/selfrouter-prompt.md` | n/a (content file) | — |

New Core files:

| File | Position in Core.fsproj | Depends On |
|------|------------------------|------------|
| `SelfRoutingPorts.fs` | After `RetrainingPorts.fs`, before `ML.fs` | BCL-only |

---

## Hermes Agent Integration (Phase 20)

**Wire format finding (verified from GitHub source):**

The Hermes Agent custom provider plugin (`plugins/model-providers/custom/__init__.py`) does NOT send session IDs in any form — no `X-Session-Id` header, no `session_id` body field, no `conversation_id`. The plugin's `extra_body` carries only `options.num_ctx` and `think: False`. Internal session state is local to the Hermes process (`self.session_id` for trajectory logging to disk).

**Implication for session store design:** Session continuity across Hermes turns cannot be derived from an explicit session identifier from the caller. The session store must derive session identity from something already present in the request. Two options:

| Option | Mechanism | Reliability |
|--------|-----------|-------------|
| A. IP + User-Agent fingerprint | Concatenate `HttpContext.Connection.RemoteIpAddress` + `User-Agent` header → SHA-256 key | HIGH for single-machine local setup (Hermes always runs on loopback → same IP; same UA) |
| B. Operator-supplied `X-Session-Id` header (future Hermes patch) | Custom header set by upstream client | HIGHEST — but requires Hermes Agent PR |
| C. Conversation-window heuristic (first N messages hash) | Hash the first 2 messages of the conversation | MEDIUM — breaks if Hermes rotates window |

**Recommendation for v2.0:** Option A (IP + User-Agent fingerprint) for Phase 19–20. This works correctly for the primary integration target (Hermes on loopback). Option B is the upgrade path documented in Phase 20, tracked as a future Hermes Agent PR. Option C is explicitly excluded — too fragile.

**Session key derivation:**
```fsharp
// In ChatCompletions.fs handler — derives session key from HttpContext
let private deriveSessionKey (ctx: HttpContext) : string =
    let ip = string ctx.Connection.RemoteIpAddress
    let ua = ctx.Request.Headers.UserAgent.ToString()
    let raw = ip + "|" + ua
    let bytes = System.Text.Encoding.UTF8.GetBytes(raw)
    use sha = System.Security.Cryptography.SHA256.Create()
    sha.ComputeHash(bytes)
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""
    |> fun h -> h.Substring(0, 16)   // 16-char prefix sufficient for local use
```

`computeContentHash` already exists in ChatCompletions.fs (Phase 16). The same SHA-256 helper pattern is reused here — no new utility function needed.

**`X-Session-Id` header support (opt-in):** If the request carries `X-Session-Id` header, prefer it over the fingerprint. This future-proofs Phase 20 for a Hermes patch that adds the header without requiring a smart-router code change.

```fsharp
let private resolveSessionKey (ctx: HttpContext) : string =
    match ctx.Request.Headers.TryGetValue("X-Session-Id") with
    | true, values when values.Count > 0 -> values.[0]
    | _ -> deriveSessionKey ctx
```

**Hermes streaming default:** Hermes calls with `stream=True` by default (confirmed from run_agent.py analysis). The session store write (after routing decision) must NOT block the SSE forward loop. Session writes are fire-and-forget (non-awaited `ConcurrentDictionary` update, no I/O). This is naturally satisfied by the in-memory design.

---

## Integration with Retained v1.x Infrastructure

### DI Registration (CompositionRoot.fs)

New registrations follow v1.x patterns exactly:

```fsharp
// Session store — singleton (process-lifetime; fits with IMemoryCache mental model but BCL only)
services.AddSingleton<SessionStore>() |> ignore
services.AddSingleton<ISessionStore>(fun sp -> sp.GetRequiredService<SessionStore>() :> ISessionStore) |> ignore

// SelfRouter — singleton (stateless service; lazy classify cache is process-lifetime; mirrors QwenUpstreamClient ARCH-06)
services.AddSingleton<SelfRouter>() |> ignore
services.AddSingleton<ISelfRouter>(fun sp -> sp.GetRequiredService<SelfRouter>() :> ISelfRouter) |> ignore
```

No new `AddHostedService` — neither SessionStore nor SelfRouter are BackgroundServices (no eviction timer; write-time eviction is synchronous).

### RoutingAlgorithmRegistration

`RoutingAlgorithmRegistration.Name` (used in DecisionLog `routing_algorithm` field) needs a new value for v2.0: `"selfrouting"`. The existing `regn.Name` field already carries this through to the JSONL log — no schema change, just a new string literal.

### DecisionLog / RoutingReason DU

New `RoutingReason` cases needed for v2.0:

```fsharp
// SmartRouter.Core.Domain — additions to existing DU
type RoutingReason =
    // ... existing cases (ExplicitModelOverride, ExplicitTask, Default, ML, FallbackTo35B, FallbackTo122B) ...
    | HardRule              // Phase 17: keyword match → immediate 122B
    | SelfRoute             // Phase 18: 35B self-classify returned SAFE/UNSAFE
    | StickyEscalation      // Phase 19: previous turn in session was 122B → stay 122B
```

Each new case requires a new arm in `formatReason` in `DecisionLogger.fs`. `TreatWarningsAsErrors=true` enforces exhaustive match (FS0025 will fail the build if any case is unhandled).

### Routing.fs pipeline changes

`routeRequest` gains a stage-0 check. Signature UNCHANGED (`RoutingConfig -> RoutingAlgorithm -> RouterRequest -> Result<RoutingDecision, RouterError>`). The `RoutingAlgorithm` closure passed in for v2.0 mode encapsulates self-classify + sticky escalation. Hard rules live at a new stage 0 inside `routeRequest`:

```fsharp
let routeRequest config algorithm req =
    // Stage 0 NEW — Hard Rules (immediate 122B, no further stages)
    match tryHardRule config req with
    | Some decision -> Ok decision
    | None ->
    // Stage 1 — existing model override
    match tryModelOverride req with
    | Some decision -> Ok decision
    | None ->
    // Stage 2 — existing task table
    match tryTaskTable config req with
    | Error e            -> Error e
    | Ok (Some decision) -> Ok decision
    // Stage 3 — pluggable algorithm (selfrouting or ml)
    | Ok None -> Ok (algorithm config req)
```

`RoutingConfig` gains `HardRules : HardRulesConfig` field. This is a breaking change to `RoutingConfig` construction sites (tests); all test construction sites must be updated (same pattern as Phase 9 `CanaryOptions` addition).

---

## Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| Session store | `ConcurrentDictionary<string, SessionState>` + manual LRU | `IMemoryCache` with `MemoryCacheEntryOptions(SlidingExpiration=30min)` | `IMemoryCache` needs `services.AddMemoryCache()` + imports `Microsoft.Extensions.Caching.Memory`; adds a background eviction thread; inconsistent with JudgeClient pattern already in codebase. Acceptable if operator wants richer TTL semantics — one DI line to swap. |
| Session store | In-memory `ConcurrentDictionary` | File-backed (`sessions.json` on disk) | File-backed sessions survive restart but require file I/O on every request hot path. Sticky escalation is a within-session UX concern, not a durability concern. Restart clears session state gracefully (next turn re-classifies). |
| Session identity | IP + User-Agent fingerprint | `X-Session-Id` header | Hermes currently sends no session header. Fingerprint works for single-machine local deployment. `X-Session-Id` is the upgrade path (Phase 20). |
| Hard Rules | `String.Contains(kw, OrdinalIgnoreCase)` | Regex compiled pattern | Regex is slower to initialize and overkill for 6 ASCII keyword exact-substring matches. `OrdinalIgnoreCase.Contains` is simpler, faster, and easier to test. |
| Self-classify | Named `"router"` HttpClient (direct to 35B) | Route through `IUpstreamClient` / `QueueDispatcher` | Routing through QueueDispatcher would consume the `SemaphoreSlim(1)` 122B gate — same pitfall as TeacherLabeler (STATE.md 07-03). |
| ML bypass | Config flag `Routing:Mode = "selfrouting"` | Remove ML NuGet packages and code | ML code stays for rollback safety (operator can flip to `"ml"` without code change). Packages stay compiled; binary is larger but operationally reversible. |

---

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| `QueueDispatcher` for classify calls | Consumes `SemaphoreSlim(1)` 122B gate — starves real inference traffic | Named `"router"` HttpClient direct to `Model35B` port |
| `IHostedService` / `PeriodicTimer` for session eviction | Unnecessary complexity; write-time eviction is simpler and proven (JudgeClient) | Write-time TTL check in `SessionStore.TryGetSession` + `SetSession` |
| `async {}` in any new code | ARCH-02: `async {}` literals forbidden; `scripts/check-no-async.sh` enforces | `task {}` exclusively |
| Regex for Hard Rules keyword matching | Overkill for 6 ASCII keywords; harder to test | `String.Contains(kw, StringComparison.OrdinalIgnoreCase)` |
| Global state / mutable module-level variables | ARCH-01: Core must be BCL-only pure records; no mutation in Core | F# records + DI singletons in Cli layer |

---

## No New NuGet Packages Required

All v2.0 features are implementable with the existing dependency set:

| Feature | BCL / Existing Dependency |
|---------|--------------------------|
| Session store | `ConcurrentDictionary<K,V>` (BCL) + `Interlocked` (BCL) |
| Hard Rules | `String.Contains(StringComparison)` (BCL) |
| Self-classify HTTP | `IHttpClientFactory` (Microsoft.Extensions.Http — already registered) |
| Self-classify retry | `Microsoft.Extensions.Http.Resilience` 10.5.0 — already in Cli.fsproj |
| SHA-256 session key | `System.Security.Cryptography.SHA256` (BCL — already used in ChatCompletions.fs) |
| Session TTL | `DateTimeOffset` (BCL) |
| Routing mode switch | `appsettings.json` string + `configureRequestPipeline` branching (existing pattern) |

The ML packages (ML.NET 5.0.0, ONNX Runtime 1.25.1) remain but are not invoked in selfrouting mode.

---

## Sources

- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/JudgeClient.fs` — LRU cache pattern (ConcurrentDictionary + AccessSeq + TOCTOU note) verified directly
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` — "router" named-client-not-IUpstreamClient pitfall (07-03) verified directly
- `/Users/ohama/projs/smart-router/.planning/STATE.md` — locked decisions (selfrouting pivot, hard rules scope, 35B self-route, Routing.Mode flag) read directly
- `/Users/ohama/projs/smart-router/.planning/docs/35b-selfrouting.md` — design doc §6,12 (hard rule keywords), §16 (sticky escalation logic)
- `/Users/ohama/projs/smart-router/.planning/docs/35b-selfrouting-prompt.md` — SAFE/UNSAFE wire format, max_tokens=4-16, temp=0.0, stream=false
- `https://raw.githubusercontent.com/NousResearch/hermes-agent/main/plugins/model-providers/custom/__init__.py` — confirmed: no X-Session-Id header, no session_id body field; extra_body carries only `options.num_ctx` and `think=False`
- `https://raw.githubusercontent.com/NousResearch/hermes-agent/main/run_agent.py` — confirmed: `self.session_id` is local-only (trajectory logging); no session propagation to provider endpoints; stream=True default

---
*Stack research for: v2.0 Self-Routing + Session-Aware additions to smart-router*
*Researched: 2026-05-11*
