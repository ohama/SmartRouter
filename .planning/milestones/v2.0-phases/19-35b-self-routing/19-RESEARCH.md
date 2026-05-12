# Phase 19: 35B Self-Routing (Stage 5 Self-Classify) — Research

**Researched:** 2026-05-11
**Domain:** F# HttpClient adapter + LRU cache + cascade integration + DU extension
**Confidence:** HIGH

## Summary

Phase 19 wires the final piece of the v2.0 self-routing cascade: a named "selfrouter" HttpClient that calls the 35B model with a 1-token classify prompt before defaulting to 35B or escalating to 122B. All mechanical patterns for this phase are directly replicated from Phase 16 JudgeClient.fs (named HttpClient + LRU cache + fail-open DU) and Phase 14 QualityFallback streaming-skip (`if req.Stream then ...`). No new NuGet packages are required.

The cascade integration slot is precisely identified. After Phase 18, the "selfrouting" algorithm closure in CompositionRoot.fs (lines 507–527) is a `match sessionStore.TryGet(req.SessionId)` — a sticky-or-default. Phase 19 replaces the `| _ -> { Target = Qwen35B; Reason = Default }` default branch with a self-classify call. The selfrouter named HttpClient is registered in the same mode-guarded block as the algorithm closure (`| _ ->` selfrouting arm), not unconditionally. `ModelVersion` changes from the literal `"selfrouting-v1"` to a prompt-hash-based version string.

The ML dormant integration test mirrors ModeSwitchTests.fs exactly: minimal in-memory config with `Routing:Mode=ml` + W4 skip guard for ONNX file absence. The test resolves `RoutingAlgorithmRegistration` and verifies `regn.Name = "ml"` — no fake Kestrel needed; DI wiring verification is sufficient for SR-09.

**Primary recommendation:** Copy JudgeClient.fs as the SelfRouter.fs template. Replace (promptHash, responseHash) tuple key with a single `promptHash: string` key; replace `JudgeVerdict` with `SelfRouteVerdict`; replace `ROUTE_YES`/`ROUTE_NO` substring match with `SAFE`/`UNSAFE` match. All other structural patterns are identical.

---

## 1. Codebase Patterns to Mirror

### Phase 16 JudgeClient — Primary Template

**File:** `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/JudgeClient.fs`

Key lines to mirror:

| Pattern | JudgeClient lines | SelfRouter equivalent |
|---------|-------------------|-----------------------|
| DU definition with fail-open cases | 23–28 | `SelfRouteVerdict` DU |
| `CLIMutable` options record | 35–41 | `SelfRouterOptions` |
| Port interface declaration | 52–56 | `ISelfRouter` |
| Stats interface | 63–69 | `ISelfRouterStats` |
| `CacheEntry` with `mutable AccessSeq` | 73–76 | Same shape |
| Safety-biased parser (NO wins on collision) | 89–99 | UNSAFE wins on ambiguity |
| `tryReadResponseContent` envelope extractor | 106–122 | Same JSON shape (mlx_lm.server) |
| `getPromptTemplate` lock-on-first-read | 169–188 | Same pattern; path = "prompts/self-router-prompt.md" |
| `tryGetCached` / `setCached` with TOCTOU note | 192–214 | Single `string` key (prompt hash only) |
| `buildBody` with `JsonFSharpConverter()` required | 230–245 | `max_tokens=4`, `temperature=0.0`, `stream=false` |
| `attemptOnce` with task {} + exception arms | 252–279 | Identical; client name = "selfrouter" |
| `VerdictAsync` cache-check → template-check → HTTP | 283–316 | `ClassifyAsync` with same flow |
| `GetJudgeStats` via `Volatile.Read` | 320–327 | `GetSelfRouterStats` |

**Critical JudgeClient pitfall that applies to SelfRouter too** (lines 229–232):
```fsharp
// JsonFSharpConverter is REQUIRED here despite bodyDict being a plain
// Dictionary<string,obj>: the messages array elements ARE F# anonymous records
// ({| role; content |}), which System.Text.Json does not serialize correctly
// without the converter.
let opts = JsonSerializerOptions()
opts.Converters.Add(JsonFSharpConverter())
JsonSerializer.Serialize(bodyDict, opts)
```
Do NOT remove the `JsonFSharpConverter` from `buildBody`. This burned Phase 16 and will burn Phase 19 if forgotten.

**JudgeClient HttpClient registration in CompositionRoot** (lines 668–689):
```fsharp
services.AddHttpClient("judge", fun (c: System.Net.Http.HttpClient) ->
    c.BaseAddress <- Uri(effectiveEndpoint)
    c.Timeout     <- TimeSpan.FromSeconds(float effectiveTimeoutSec))
    .AddResilienceHandler("judge-pipeline", fun (builder: ...) ->
        let retryOpts = HttpRetryStrategyOptions()
        retryOpts.MaxRetryAttempts <- 2
        retryOpts.Delay            <- TimeSpan.FromMilliseconds(200.0)
        ...
        builder.AddRetry(retryOpts) |> ignore
        builder.AddTimeout(TimeSpan.FromSeconds(float effectiveTimeoutSec)) |> ignore)
```
**WARNING:** The 2-arg `AddHttpClient(name, fun c -> ...)` form is FORBIDDEN (silent BaseAddress failure in F#). Phase 16 uses the `AddHttpClient(name, fun c -> ...).AddResilienceHandler(...)` form which actually does work here. For the selfrouter client, mirror the same form exactly. The `upstream35b` and `upstream122b` production clients use `.AddHttpClient(name).ConfigureHttpClient(...)` chain form (lines 237–243) — either form works but be consistent with whichever is chosen.

### Phase 14 Streaming-Skip Pattern

**File:** `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`

Streaming branch is at **line 320**: `if req.Stream then`. The streaming skip comment at lines 323–330:
```fsharp
// Phase 14: Quality fallback (35B response → 122B retry) is INTENTIONALLY SKIPPED
// for streaming requests. Once the first SSE chunk has been
// FlushAsync'd to the client (typically within ~100ms), the response
// cannot be retracted.
```
Phase 19 self-classify skip goes in the SAME structural location — inside the `else` non-streaming branch at **line 438**. The skip happens BEFORE the self-classify HTTP call, not inside the adapter. The adapter is only called from the non-streaming branch. The comment template to use:
```fsharp
// Phase 19 (SR-06): Self-classify INTENTIONALLY SKIPPED for streaming requests.
// Latency budget cannot accommodate a classify round-trip before first SSE chunk.
// Hard Rules (Stage 0) + sticky escalation (Stage 3) still apply to streaming.
// Explicit skip mirrors Phase 14 quality-fallback streaming-skip pattern.
```

### Phase 5 Prompt-Hash Helper

**File:** `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/DecisionLogger.fs`

`computePromptHash` (lines 11–16):
```fsharp
let computePromptHash (messages: Message list) : string =
    let text = messages |> List.map (fun m -> m.Content) |> String.concat ""
    use sha = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(text)
    let hash  = sha.ComputeHash(bytes)
    hash |> Array.map (sprintf "%02x") |> String.concat ""
```
**Key details:**
- Concatenates ALL message content (all roles, not just last user message) with `String.concat ""`
- No separator between messages — `"hello" + "world"` = `"helloworld"`. This is the existing function; SelfRouter should reuse it rather than hand-rolling a new hash.
- `SHA256` is not thread-safe — `use sha = SHA256.Create()` creates a new instance per call (line 13). Do NOT share a SHA256 instance across calls.
- The same function is already used in ChatCompletions.fs for the judge cache key (line 463: `let promptHash = computePromptHash req.Messages`).
- For SelfRouter: the cache key is a single `string` (the prompt hash). JudgeClient uses a `string * string` tuple (promptHash + responseHash). SelfRouter simplifies to `string`.

**Cache key normalization decision:** Use `computePromptHash req.Messages` from `DecisionLogger` as the cache key — full conversation, all roles, no separator. This is the same hash already in the DecisionLog row, so it appears in logs naturally. No additional normalization (whitespace stripping, role filtering) is needed or recommended: be consistent with what the logging infrastructure already does.

### Phase 17 Hard Rules Cascade — Current `routeRequest` Structure

**File:** `/Users/ohama/projs/smart-router/src/SmartRouter.Core/Routing.fs`

Current `routeRequest` (lines 100–121):
```fsharp
let routeRequest (config: RoutingConfig) (algorithm: RoutingAlgorithm) (req: RouterRequest) =
    // Stage 0: Hard Rules
    match HardRules.applyHardRules req with
    | Some decision -> Ok decision
    | None ->
    // Stage 1: explicit model override
    match tryModelOverride req with
    | Some decision -> Ok decision
    | None ->
        // Stage 2: explicit task table
        match tryTaskTable config req with
        | Error e            -> Error e
        | Ok (Some decision) -> Ok decision
        | Ok None            -> Ok (algorithm config req)   // Stage 3 (algorithm)
```
Stage 4 self-classify is NOT inserted into `routeRequest`. It is inserted inside the `algorithm` closure in CompositionRoot. The `algorithm` closure receives `(config, req)` and is called at line 121. Self-classify is implemented as a sub-stage within the `"selfrouting"` branch of the `RoutingAlgorithmRegistration` factory.

**Stage 3 algorithm type signature:** `RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision` (Domain.fs line 116). The closure is synchronous by this type. Phase 19 must make the closure async — this requires changing the algorithm to `task {}` returning `Task<RoutingDecision>` OR making the classify call synchronous (not possible; it's HTTP). See "Cascade Integration Point" section below for resolution.

### Phase 18 Sticky Closure — Where SelfRouter Slots In

**File:** `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/CompositionRoot.fs`

The Phase 18 sticky-or-default closure (lines 507–527):
```fsharp
{ Algorithm = fun _config req ->
               match sessionStore.TryGet(req.SessionId) with
               | Some s when s.LastModel = Qwen122B ->
                   { Target = Qwen122B; Priority = High
                     Reason = StickyEscalation; IsFallback = false
                     ModelVersion = "selfrouting-v1" }
               | _ ->
                   // Phase 19 will replace this branch with self-classify call.
                   { Target = Qwen35B; Priority = Low
                     Reason = Default; IsFallback = false
                     ModelVersion = "selfrouting-v1" }
  Name = "selfrouting"
  ModelVersion = "selfrouting-v1" }
```
The comment at line 520 explicitly says "Phase 19 will replace this branch with self-classify call." SelfRouter slots in at the `| _ ->` branch only — sticky check (Stage 3) runs first and short-circuits to 122B if the session is already escalated.

**CRITICAL architectural issue:** `RoutingAlgorithm` is typed as `RoutingConfig -> RouterRequest -> RoutingDecision` (synchronous). Self-classify requires an async HTTP call. This mismatch must be resolved in Plan 19-01/19-03. Two options:
1. Change `RoutingAlgorithm` type alias to `RoutingConfig -> RouterRequest -> Task<RoutingDecision>` and update all callers (`routeRequest`, `ChatCompletions.fs`, all tests).
2. Keep `RoutingAlgorithm` synchronous and call the classify adapter separately in `ChatCompletions.fs` before invoking `routeRequest`, passing the result as context.
3. Keep `RoutingAlgorithm` synchronous but have the classify call happen in the non-streaming branch of `ChatCompletions.fs` AFTER `routeRequest` returns `Ok decision` with `Reason = Default`.

**Option 3 is the least disruptive and matches how QualityFallback works** — quality fallback also runs AFTER initial routing in ChatCompletions non-streaming branch. Self-classify becomes a "pre-routing enhancement" step: in the non-streaming branch, before dispatching to upstream, if `decision.Reason = Default` (no Hard Rule, no override, no task, no sticky), call SelfRouter, and update the decision accordingly. This avoids touching `RoutingAlgorithm` type or `routeRequest` Core function.

[ASSUMPTION TO VERIFY DURING PLANNING]: Option 3 (self-classify in ChatCompletions non-streaming branch post-routeRequest) vs. changing RoutingAlgorithm type. The ROADMAP says "cascade integration" but the type constraint makes in-closure insertion non-trivial. The planner should pick Option 3 or explicitly plan the type change. Option 3 is recommended by this researcher for minimal disruption.

---

## 2. SelfRouter Adapter Design

### Namespace and File Layout

```
src/SmartRouter.Cli/Adapters/SelfRouter.fs        (new; after SessionStore.fs in fsproj)
```

ARCH-01 invariant: SelfRouter is Cli-only. No changes to Core (Domain.fs gets the `SelfRoute` DU case and `formatReason` arm, but those are Core-only additions with no Cli/Serilog/HttpClient imports).

### HttpClient Registration Shape

Named client `"selfrouter"` — mirrors judge registration pattern (CompositionRoot lines 668–689):
- `BaseAddress` = `Upstreams.Model35B` (resolved from `upstreamOptsLazy.Model35B` at registration time, same pattern as `effectiveEndpoint` for judge)
- `Timeout` = 5s (NOT 300s; 300s is for inference, 5s is for classify)
- `AddResilienceHandler("selfrouter-pipeline", ...)` — 1 retry at 200ms (SR-01 says "1 retry at 200ms")
- `MaxRetryAttempts = 1` (judge uses 2; SR-01 specifies 1)

Registration in CompositionRoot is **mode-guarded** — only registered when `routingMode = "selfrouting"`. The judge registration is opt-in (config key); SelfRouter is mode-gated. Both are conditional registrations.

**SelfRouterOptions** (CLIMutable):
```fsharp
[<CLIMutable>]
type SelfRouterOptions = {
    mutable Endpoint        : string   // default "" → derive from Upstreams.Model35B
    mutable PromptPath      : string   // default "prompts/self-router-prompt.md"
    mutable TimeoutSeconds  : int      // default 5
    mutable MaxCacheEntries : int      // default 10000
}
```
Config key: `Routing:SelfRouter:*` in appsettings.json.

### SelfRouteVerdict DU

```fsharp
type SelfRouteVerdict =
    | RouteSafe
    | RouteUnsafe
    | RouteSkipped of reason: string
    | RouteFailed  of error: string
```

Safety-biased parser (mirrors JudgeClient lines 89–99, UNSAFE wins on ambiguity):
```fsharp
let private parseContent (content: string) : SelfRouteVerdict =
    let hasSafe   = content.Contains("SAFE",   StringComparison.OrdinalIgnoreCase)
    let hasUnsafe = content.Contains("UNSAFE", StringComparison.OrdinalIgnoreCase)
    match hasUnsafe, hasSafe with
    | true,  _    -> RouteUnsafe          // UNSAFE wins on collision — safety bias
    | false, true -> RouteSafe
    | false, false ->
        let snippet = content.Substring(0, min 100 content.Length)
        RouteFailed (sprintf "unparseable self-router response: %s" snippet)
```
**Why UNSAFE wins on collision:** `SAFE` is a substring of `UNSAFE`. If the model returns `"UNSAFE"`, both `hasSafe` and `hasUnsafe` are true. The collision arm routes to 122B (safe failure mode). This is load-bearing.

### ISelfRouter Port

```fsharp
type ISelfRouter =
    abstract member ClassifyAsync :
        promptHash: string * promptText: string * ct: CancellationToken
        -> Task<SelfRouteVerdict>
```
(Single string key vs JudgeClient's tuple key — no response content to hash.)

### Prompt Template Loading

- File path: `"prompts/self-router-prompt.md"` (default, operator-overridable via `Routing:SelfRouter:PromptPath`)
- Loading pattern: identical to JudgeClient `getPromptTemplate` (lock-on-first-read; cached miss = Some "")
- Template format: `{{PROMPT}}` placeholder replaced with concatenated message content at classify time
- `prompts/self-router-prompt.md` is a new file committed to git (SR-02); mirrors `prompts/judge-prompt.md` shape

**Prompt template design (from `.planning/docs/35b-selfrouting-prompt.md` §3):**
```
You are a routing classifier.

Your task is to determine whether a request is SAFE
for a fast 35B coding model.

A request is SAFE if:
- it requires only shallow reasoning
- no difficult debugging
- no architecture design
- no compiler expertise
- no deep continuation context
- no optimization reasoning
- no multi-step planning

SAFE examples:
- formatting
- summaries
- boilerplate generation
- simple explanations
- basic code snippets
- YAML/JSON generation

UNSAFE examples:
- LLVM / MLIR / compiler bugs
- debugging / segfault
- optimization / concurrency
- type inference / closure lowering
- architecture redesign
- retry/fix/continue workflows

Respond with ONLY the word SAFE or UNSAFE.

Request:
{{PROMPT}}
```
The doc recommends JSON output (`{"route": ...}`) but the REQUIREMENTS mandate `max_tokens=4–8`. JSON output cannot fit in 4 tokens. The final template should request plain `SAFE` or `UNSAFE` only. This matches SR-03 and the safety-biased parser above.

### max_tokens Decision

SR-02 says `max_tokens=4-8`. The design doc §9 says `max_tokens=4–8`. Use `max_tokens=8` (not 4) to allow for potential model prefix tokens (`<|im_start|>` etc.) before the verdict word while still being far below any reasoning threshold. The request body:
```fsharp
bodyDict.["max_tokens"]  <- 8     // KEY: short classify response; no reasoning possible at 8 tokens
bodyDict.["temperature"] <- 0.0
bodyDict.["stream"]      <- false
```

---

## 3. Cache Architecture

### Prompt-Hash LRU Cache

- **Key:** `string` — `computePromptHash req.Messages` (from DecisionLogger; same hash used in DecisionLog row)
- **Value:** `SelfRouteVerdict` (RouteSafe or RouteUnsafe only — never cache RouteSkipped or RouteFailed)
- **Backing structure:** `ConcurrentDictionary<string, CacheEntry>` where `CacheEntry = { Verdict: SelfRouteVerdict; mutable AccessSeq: int64 }`
- **Eviction:** write-time count check + `Seq.minBy (_.AccessSeq)` eviction (JudgeClient lines 209–214 exact pattern)
- **TOCTOU note:** two concurrent threads may both see Count >= max and both evict — acceptable (JudgeClient comment lines 205–208)
- **Max entries:** `maxCacheEntries` from options (default 10,000 — SR-04 says "bounded ~10000 entries")

**Cache in DI:** The SelfRouter class owns its cache as a private instance field (like JudgeClient). No separate DI registration for the cache itself.

**What gets cached:** `RouteSafe` and `RouteUnsafe` verdicts only. `RouteFailed` may be transient (network blip); `RouteSkipped` means template missing (operator may add it at runtime — do NOT cache miss). Mirrors JudgeClient lines 310–313.

**Cache key normalization:** No normalization beyond what `computePromptHash` already does (UTF-8 encode, SHA-256, hex). Do not strip whitespace or canonicalize roles — be consistent with the existing hash function. If two requests have the same messages (same content, same roles), they get the same hash. If roles differ (system vs user), they get different hashes.

### ModelVersion String for Self-Routing

The `"selfrouting-v1"` literal in the Phase 18 stub should be replaced in Phase 19 with a prompt-hash-derived version string. Options:
1. Hash the prompt template file content at startup: `"selfrouting-" + (first 8 chars of SHA-256 of prompt file)`
2. Keep `"selfrouting-v1"` and bump to `"selfrouting-v2"` when prompt changes (operator-controlled)

**Recommendation:** Compute a SHA-256 of the prompt template file content at adapter construction time and use `sprintf "selfrouting-%s" (hash.Substring(0,8))` as the `ModelVersion` in the `RoutingDecision`. This makes `model_version` in DecisionLog sensitive to prompt template changes — operators can detect prompt drift by watching `model_version` in the log.

[ASSUMPTION TO VERIFY DURING PLANNING]: Whether `ModelVersion` should be `"selfrouting-{promptHash8}"` (from prompt file) or a fixed version string. The ROADMAP Success Criterion 1 says "`model_version` reflects v2.0 selfrouter prompt hash" which confirms the prompt-hash approach.

---

## 4. Cascade Integration Point

### Where Self-Classify Inserts

The cleanest insertion point — Option 3 (see "Cascade integration" note in Section 1) — is in `ChatCompletions.fs` **non-streaming branch only** (after line 438 `else`):

After `Ok decision` from `routeRequest`, before dispatching to upstream, check:
```fsharp
| Ok decision ->
    // ... Phase 10 health + fallback rebind ...
    let sessionStore = ctx.RequestServices.GetRequiredService<ISessionStore>()

    if req.Stream then
        // streaming branch unchanged — no self-classify
    else
        // non-streaming branch
        // Phase 19 (SR-08): self-classify only when routing fell through to default
        // (Hard Rules, model override, task table, and sticky all short-circuit before here)
        let! finalDecisionBeforeUpstream =
            if decision.Reason = Default then
                task {
                    let selfRouter = ctx.RequestServices.GetService<ISelfRouter>()
                    if isNull (box selfRouter) then
                        return decision   // selfrouter not registered (ml mode or disabled)
                    else
                        let promptText = req.Messages |> List.map (fun m -> m.Content) |> String.concat " "
                        let promptHash = computePromptHash req.Messages
                        let! verdict = selfRouter.ClassifyAsync(promptHash, promptText, ctx.RequestAborted)
                        match verdict with
                        | RouteSafe   ->
                            return { decision with
                                       Target       = Qwen35B
                                       Reason       = SelfRoute
                                       ModelVersion = selfRouterVersion }  // from ISelfRouterStats or provider
                        | RouteUnsafe ->
                            return { decision with
                                       Target       = Qwen122B
                                       Priority     = High
                                       Reason       = SelfRoute
                                       ModelVersion = selfRouterVersion }
                        | RouteSkipped _ | RouteFailed _ ->
                            // SR-08: fail-open to existing default (35B)
                            return decision  // Reason = Default, Target = Qwen35B
                }
            else
                Task.FromResult(decision)  // already decided by Hard Rule / override / task / sticky
        let decision = finalDecisionBeforeUpstream
        // ... continue with upstream.CompleteAsync ...
```

**IMPORTANT:** `ISelfRouter` is resolved via `GetService<ISelfRouter>()` (nullable, like `IJudgeClient`), NOT `GetRequiredService`. When `Routing.Mode="ml"`, SelfRouter is not registered → null → skip. This is the same pattern as judge (ChatCompletions line 455: `let judgeClient = ctx.RequestServices.GetService<IJudgeClient>()`).

### RoutingDecision.ModelVersion for SelfRoute decisions

The SelfRouter adapter needs to expose its prompt version so `ChatCompletions` can stamp it on the `RoutingDecision`. Options:
1. A second interface `ISelfRouterVersion` with `member _.PromptVersion: string`
2. Include it in `ISelfRouterStats.GetStats()` return value
3. Expose it as a read-only property on the concrete `SelfRouter` class, resolved via concrete type

**Recommendation:** Keep it simple — the concrete `SelfRouter` class exposes `member _.PromptVersion: string` as a public property computed at construction time. ChatCompletions resolves `ISelfRouter` and casts to check version, OR the `ISelfRouter` interface includes a `PromptVersion` property. Adding to the interface is cleaner.

[ASSUMPTION TO VERIFY DURING PLANNING]: How to surface the prompt hash as `model_version` in RoutingDecision. The concrete class approach avoids interface bloat.

### configureWithoutMl Must Also Register ISelfRouterStats NoOp

Mirrors the Phase 16 pattern: `configureWithoutMl` registers `IJudgeStats` NoOp (lines 1118–1121). Phase 19 must add an `ISelfRouterStats` NoOp registration to `configureWithoutMl` so the `/stats` endpoint and DI graph remain intact when using the offline path. `ISelfRouter` is NOT registered in `configureWithoutMl` (same as `IJudgeClient` — not needed offline, null-safe resolve via `GetService`).

---

## 5. Stats Wiring

### IStatsWire Extension (Stats.fs)

Current `StatsWire` fields end with `judge_call_count : int64` (Stats.fs line 44). Phase 19 adds four snake_case fields (SR-05):

```fsharp
type private StatsWire =
    { ...
      judge_cache_hits                   : int64
      judge_cache_misses                 : int64
      judge_call_count                   : int64
      selfrouter_cache_hits              : int64    // NEW Phase 19
      selfrouter_cache_misses            : int64    // NEW Phase 19
      selfrouter_call_count              : int64    // NEW Phase 19
      selfrouter_skipped                 : int64 }  // NEW Phase 19
```

`selfrouter_skipped` counts `RouteSkipped` returns (streaming is skipped; also template missing).

### ISelfRouterStats Interface

```fsharp
type ISelfRouterStats =
    abstract member GetSelfRouterStats : unit -> struct (int64 * int64 * int64 * int64)
    // Returns struct (cacheHits, cacheMisses, callCount, skipped)
```

Mirrors `IJudgeStats.GetJudgeStats` returning `struct (int64 * int64 * int64)` (JudgeClient.fs lines 63–69).

### Counter Update

All counters use `Interlocked.Increment(&fieldName)` then `|> ignore` — same as `cacheHits` / `cacheMisses` / `callCount` in JudgeClient.fs lines 148–149. `mutable` field declarations with int64 type (JudgeClient lines 146–149).

### Stats.fs Handler Extension

In `mapEndpoints` (Stats.fs lines 73–110), after the judge stats resolution block (lines 90–95):
```fsharp
let selfRouterStats = ctx.RequestServices.GetService<ISelfRouterStats>()
let struct (srHits, srMisses, srCalls, srSkipped) =
    if isNull (box selfRouterStats) then struct (0L, 0L, 0L, 0L)
    else selfRouterStats.GetSelfRouterStats()
```
Then update the `wire` record with the new fields. Mirrors judge stats pattern exactly.

**When ISelfRouterStats is null:** When `Routing.Mode="ml"`, SelfRouter is not registered → `ISelfRouterStats` is null → all zeros. Must register `ISelfRouterStats` NoOp for `ml` mode (parallel to `IJudgeStats` NoOp for judge-disabled mode). This goes in the else-branch of the `"selfrouting"` mode registration.

---

## 6. Streaming-Skip Mechanism

The skip happens at the **call site in ChatCompletions.fs**, not inside the adapter. The adapter (`ISelfRouter.ClassifyAsync`) is never called for streaming requests.

The structural shape in ChatCompletions non-streaming branch:
```fsharp
if req.Stream then
    // SSE streaming branch
    // Phase 19: SelfRouter is not called here; Hard Rules + sticky already applied via routeRequest.
    // This preserves first-chunk latency (SR-06).
    ...
else
    // non-streaming branch
    // Phase 19: self-classify happens here (if decision.Reason = Default)
    ...
```

The streaming branch does NOT receive a self-classify call. The sticky session (`decision.Reason = StickyEscalation`) and Hard Rules (`decision.Reason = HardRule`) both arrive already-decided from `routeRequest`. Only `Default` reason means "no signal yet" and warrants a classify call — and only in the non-streaming branch.

`selfrouter_skipped` counter increments once per streaming request that had `Reason = Default` (no Hard Rule, override, task, sticky). This counts the classify calls that were skipped due to stream=true.

---

## 7. ML Dormant Integration Test (SR-09)

### What It Asserts

`MlDormantTests.fs` must verify that `Routing.Mode="ml"` still wires the ML algorithm correctly through DI, preventing silent drift in the ML branch as v2.x phases evolve.

**Minimum assertion:**
- `regn.Name = "ml"` — ML algorithm registered
- `regn.ModelVersion.StartsWith("ml-")` — version string from router.zip SHA

**Skip guard:** Same W4 pattern as ModeSwitchTests.fs line 212–218:
```fsharp
testCase "Routing.Mode=\"ml\" + SelfRoute DU case exists → ML path still boots cleanly" <| fun () ->
    if mlFilesPresent () then
        let regn = resolveRegistration (Some "ml")
        Expect.equal regn.Name "ml" "Name = ml"
        Expect.stringStarts regn.ModelVersion "ml-" "ModelVersion starts with ml-"
    else
        skiptest "Skipping ML dormant test: ONNX embedding files absent on this host"
```

### Why This Test Suffices

The risk is `RoutingReason` DU and `formatReason` exhaustive match drift: every new DU case added in v2.x forces a compile-time exhaustive match update (TreatWarningsAsErrors). The ML code (ML.fs, MlNetClassifier.fs) produces `RoutingDecision` values with existing DU cases — it never produces `SelfRoute` or `StickyEscalation`. The test just verifies ML DI wiring doesn't throw, which catches config/DI registration breakage, not DU breakage (the compiler handles that).

**No fake Kestrel needed:** ModeSwitchTests pattern — resolves `RoutingAlgorithmRegistration` from DI, asserts `.Name` and `.ModelVersion`. No HTTP calls. Lightweight.

### Test File Structure

New file `MlDormantTests.fs` placed after `StickyEscalationTests.fs` in fsproj (lines 42–44 pattern):
```xml
<Compile Include="StickyEscalationTests.fs" />
<Compile Include="SelfRouterTests.fs" />         <!-- new Phase 19 unit tests -->
<Compile Include="SelfRoutingIntegrationTests.fs" /> <!-- new Phase 19 integration tests -->
<Compile Include="MlDormantTests.fs" />          <!-- new Phase 19 ML dormant test -->
<Compile Include="RouterTests.fs" />             <!-- always last -->
```

Add to `rootTests` list in `RouterTests.fs` (lines 14–39 pattern) before the closing `]`.

---

## 8. Plan Structure Recommendation

ROADMAP proposes 4 plans (19-01 through 19-04). Based on dependency analysis:

### Wave 1 — Plan 19-01: Core DU + Adapter Skeleton

**Deliverables:**
- `Domain.fs`: Add `SelfRoute` as 9th `RoutingReason` DU case
- `DecisionLogger.fs`: Add 9th arm `| SelfRoute -> "self_route"` to `formatReason` exhaustive match
- `SelfRouter.fs`: Full adapter implementation (`SelfRouteVerdict` DU + parser + `ISelfRouter` + `ISelfRouterStats` + `SelfRouterOptions` + LRU cache + `buildBody` + `attemptOnce` + interface impl)
- `prompts/self-router-prompt.md`: New file with `{{PROMPT}}` template

**This plan can stand alone** — adapter is complete but not wired into the routing path. No `ChatCompletions.fs` changes yet.

**Compile dependency note:** Adding `SelfRoute` DU case causes a compile error in `formatReason` until the 9th arm is added. Same atomic-pair pattern as Phase 18 Plan 18-01 (Task 1 + Task 2 committed separately but in the same plan). The pattern from STATE.md line 130: "Task 1 leaves Cli in intentional broken state (FS0025 at formatReason); Task 2 resolves it."

### Wave 2 — Plan 19-02: Cache Stats + DI Wiring

**Deliverables:**
- `Stats.fs`: 4 new `StatsWire` fields + `ISelfRouterStats` resolve + stats handler extension
- `CompositionRoot.fs`: Register named "selfrouter" HttpClient; register `SelfRouter` concrete + `ISelfRouter` + `ISelfRouterStats` in `"selfrouting"` mode arm; register `ISelfRouterStats` NoOp in `"ml"` mode arm; register `ISelfRouterStats` NoOp in `configureWithoutMl`
- `appsettings.json`: Add `Routing.SelfRouter.*` config keys

**Depends on Plan 19-01** — `SelfRouter` type must exist to register it.

### Wave 3 — Plan 19-03: Cascade Integration + Streaming Skip + Tests

**Deliverables:**
- `ChatCompletions.fs`: Non-streaming branch self-classify call when `decision.Reason = Default`; streaming branch comment SR-06; `ISelfRouter.ClassifyAsync` call; `RouteSafe`/`RouteUnsafe`/fail-open branch
- `SelfRouterTests.fs`: Unit tests for parser safety bias, cache hit/miss, DU parsing
- `SelfRoutingIntegrationTests.fs`: Fake-Kestrel end-to-end (fake 35B classify endpoint)
- `MlDormantTests.fs`: ML dormant integration test (skip-guarded W4)
- `RouterTests.fs`: Add 3 new test modules to `rootTests` list; add fsproj `<Compile Include>` entries

**Depends on Plans 19-01 and 19-02** — needs DI wiring and adapter in place.

### Wave 4 — Plan 19-04: README + CHANGELOG + Verification

**Deliverables:**
- `README.md`: §5.5 routing pipeline cascade diagram; §7 `Routing.SelfRouter.*` config keys; §9.1 `routing_reason="self_route"` added; `routing_algorithm="selfrouting"` documented
- `CHANGELOG.md`: `[Unreleased] ### Added` entries for selfrouting paradigm
- `REQUIREMENTS.md`: SR-01..09 all marked satisfied
- `ROADMAP.md`: Phase 19 success criteria checkboxes
- Sticky+SelfRouter interplay verification test (hard-rule beats self-classify path)

**Depends on Plan 19-03** — can't document what isn't implemented.

### Dependency Graph (no parallel execution possible)

```
19-01 → 19-02 → 19-03 → 19-04   (strict linear sequence)
```
All four plans are strictly sequential. 19-01 must finish before 19-02 can register the type. 19-02 must finish before 19-03 can test the wiring. 19-03 must finish before 19-04 documents it.

The ROADMAP's 4-plan split is correct and the plan boundaries are right.

---

## 9. Pitfalls Specific to Self-Classify

### Pitfall 1: SAFE is a Substring of UNSAFE

**What goes wrong:** `content.Contains("SAFE")` returns `true` for both `"SAFE"` and `"UNSAFE"`. If you check `hasSafe` before `hasUnsafe`, `"UNSAFE"` routes to 35B.
**Root cause:** Substring matching on overlapping tokens.
**How to avoid:** Check `hasUnsafe` FIRST in the match expression:
```fsharp
match hasUnsafe, hasSafe with
| true,  _    -> RouteUnsafe   // UNSAFE wins even if "SAFE" also present
| false, true -> RouteSafe
| false, false -> RouteFailed "..."
```
**Warning signs:** Second integration test (UNSAFE → 122B) passes when the test fixture returns `"SAFE"` but fails when it returns `"UNSAFE"`.

### Pitfall 2: Self-Classify Must NOT Route Through QueueDispatcher

**What goes wrong:** Calling `IUpstreamClient.CompleteAsync` or `upstream.StreamAsync` for the classify call goes through `QueueDispatcher`, which holds the `SemaphoreSlim(1)` gate on the 122B queue (and the 35B tracking counter). For a non-streaming classify call to 35B, this starves real 35B requests.
**Root cause:** `IUpstreamClient` = `QueueDispatcher` in DI. Phase 7 TeacherLabeler documented the same pitfall.
**How to avoid:** Use the named "selfrouter" `IHttpClientFactory.CreateClient("selfrouter")` directly. Never resolve `IUpstreamClient` in the SelfRouter adapter.

### Pitfall 3: Streaming + Sticky Interplay — Hard Rules Must Still Win

**What goes wrong:** If sticky check is inside the algorithm closure (correct) but Hard Rules runs before the algorithm (correct), a streaming request with an LLVM keyword + existing sticky session should route to 122B via Hard Rules — NOT via sticky. This is already correct in the codebase (Hard Rules is Stage 0, returns before algorithm is invoked), but test coverage of the streaming+keyword path must verify it.
**How to avoid:** SelfRoutingIntegrationTests should include a test: `stream=true` + keyword → `routing_reason="hard_rule"`.

### Pitfall 4: Cache Poisoning on Ambiguous Parse (RouteFailed)

**What goes wrong:** `RouteFailed` result cached in the LRU. Every subsequent request with the same prompt hash pays no HTTP cost but always fails open to 35B (wrong routing for genuinely unsafe prompts that the model was confused about).
**Root cause:** Treating RouteFailed as a permanent verdict.
**How to avoid:** Do NOT cache `RouteFailed` or `RouteSkipped` — same as JudgeClient lines 310–313.

### Pitfall 5: Prompt Template Hot-Reload vs. Cache Staleness

**What goes wrong:** Operator edits `prompts/self-router-prompt.md` at runtime; the existing cache entries were computed with the old template. The classify cache does not invalidate on template change (JudgeClient also has this property — it's not a bug, it's by design: restart-to-invalidate). The `model_version` field in DecisionLog encodes the prompt hash, so log consumers can detect when the template version changed.
**Root cause:** File-on-first-read caching with no inotify.
**How to avoid:** Document in README §7 that template changes require a router restart. The `model_version` field in DecisionLog will change automatically after restart, signaling the operator that the new template is active.

### Pitfall 6: Continuation-Prompt Overconfidence (UNSAFE Examples Coverage)

**What goes wrong:** Short prompts like `"continue"`, `"retry"`, `"fix this"` get classified as SAFE (they look simple). These prompts imply deep debugging state.
**Root cause:** The SAFE/UNSAFE classifier needs explicit UNSAFE examples for continuation patterns.
**How to avoid:** Include `"retry/fix/continue workflows"` in the UNSAFE examples section of the prompt template (`.planning/docs/35b-selfrouting-prompt.md` §7 explicitly covers this). The RESEARCH doc flags this as HIGH priority.

### Pitfall 7: ISelfRouterStats Not Registered in configureWithoutMl Breaks /stats

**What goes wrong:** The `/stats` endpoint resolves `ISelfRouterStats` via `GetService<ISelfRouterStats>()`. In `ml` mode or `--retrain` offline mode, `SelfRouter` is not registered. If `ISelfRouterStats` NoOp is also not registered, `GetService` returns null → null-dereference in the stats handler.
**How to avoid:** Register `ISelfRouterStats` NoOp in BOTH the `"ml"` mode arm AND `configureWithoutMl`. Mirrors `IJudgeStats` NoOp pattern (CompositionRoot lines 715–722 and 1118–1121).

### Pitfall 8: KV Cache Contention on Same 35B Server

**What goes wrong:** The classify call to 35B happens on the same mlx_lm.server instance that serves real 35B inference. The classify call may evict the KV cache for an in-progress inference generation, causing quality degradation on concurrent 35B requests.
**Root cause:** mlx_lm.server is single-threaded; incoming requests queue. The classify call is a new request that goes to the back of the queue.
**How to avoid:** The 5s timeout on the selfrouter client means classify blocks at most 5s before failing open. Real-world impact: classify call adds one slot ahead of any queued inference. mlx_lm.server KV cache is per-context; a new context (classify) does not evict ongoing generation contexts — KV cache is per-sequence, not shared globally. This pitfall is lower risk than it sounds but should be monitored via `selfrouter_call_count` vs. latency.

### Pitfall 9: RoutingAlgorithm Type Synchronicity Constraint

**What goes wrong:** If a developer attempts to move self-classify inside the `RoutingAlgorithm` closure (the natural "cascade inside routeRequest" approach), they will hit a type mismatch: `RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision` is synchronous but `ISelfRouter.ClassifyAsync` returns `Task<SelfRouteVerdict>`. Calling `.GetAwaiter().GetResult()` synchronously blocks the ASP.NET Core thread pool and can cause deadlocks.
**How to avoid:** Keep self-classify in the `ChatCompletions.fs` non-streaming branch (Option 3 from Section 1). The adapter is async; the call site uses `task {}` CE. Do NOT change `RoutingAlgorithm` type to async — this cascades to 20+ test call sites.

---

## 10. README and CHANGELOG Affected Sections

### Phase 19 Plan 19-04 Must Update

| Section | Change |
|---------|--------|
| `README.md §5.5` | Add Stage 4 self-classify to the cascade diagram; note streaming-skip explicitly |
| `README.md §7` | Add `Routing.SelfRouter.Endpoint`, `Routing.SelfRouter.PromptPath`, `Routing.SelfRouter.TimeoutSeconds`, `Routing.SelfRouter.MaxCacheEntries` config keys |
| `README.md §9.1` | Add `routing_reason="self_route"` as a new valid value; add `routing_algorithm="selfrouting"` as the v2.0 algorithm name |
| `CHANGELOG.md [Unreleased]` | `### Added` — self-routing paradigm (Stage 4 self-classify, prompts/self-router-prompt.md, selfrouter_* /stats fields) |

### What Phase 19 Does NOT Own

- `README.md §5.6` (sticky session): already updated by Phase 18
- `README.md §10` (Hermes Integration): Phase 20 responsibility
- `README.md §2` (architecture): may need a diagram update if §2 shows the routing algorithm path — check current §2 for staleness

### §5.5 Cascade Diagram Shape (After Phase 19)

```
Stage 0: Hard Rules keyword scan (applies to ALL requests including streaming)
Stage 1: Explicit model override (model=35b/122b in request body)
Stage 2: Explicit task table (task=compiler_debug etc.)
Stage 3: Sticky session escalation (X-Session-Id header, non-streaming AND streaming)
Stage 4: 35B self-classify — SAFE → 35B / UNSAFE → 122B (NON-STREAMING ONLY)
Stage 5: Default 35B
```
The diagram must note the streaming-skip on Stage 4 explicitly.

---

## Code Examples

### SelfRouteVerdict DU (SelfRouter.fs)

```fsharp
type SelfRouteVerdict =
    | RouteSafe
    | RouteUnsafe
    | RouteSkipped of reason: string
    | RouteFailed  of error: string
```

### Safety-Biased Parser (SelfRouter.fs)

```fsharp
let private parseContent (content: string) : SelfRouteVerdict =
    let hasUnsafe = content.Contains("UNSAFE", StringComparison.OrdinalIgnoreCase)
    let hasSafe   = content.Contains("SAFE",   StringComparison.OrdinalIgnoreCase)
    match hasUnsafe, hasSafe with
    | true,  _    -> RouteUnsafe    // UNSAFE wins on collision (SAFE is substring of UNSAFE)
    | false, true -> RouteSafe
    | false, false ->
        RouteFailed (sprintf "unparseable: %s" (content.Substring(0, min 100 content.Length)))
```

### ISelfRouter Port (SelfRouter.fs)

```fsharp
type ISelfRouter =
    abstract member ClassifyAsync :
        promptHash: string * promptText: string * ct: CancellationToken
        -> Task<SelfRouteVerdict>
    abstract member PromptVersion : string   // prompt hash prefix; stamped as model_version
```

### RoutingReason.SelfRoute (Domain.fs, 9th case)

```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Default
    | ML
    | FallbackTo35B
    | FallbackTo122B
    | HardRule
    | StickyEscalation
    | SelfRoute              // Phase 19: self-classify verdict (both SAFE and UNSAFE decisions)
```

### formatReason 9th Arm (DecisionLogger.fs)

```fsharp
let formatReason (reason: RoutingReason) : string =
    match reason with
    | ...
    | StickyEscalation -> "sticky_to_122b"
    | SelfRoute        -> "self_route"     // Phase 19
```

### Named HttpClient Registration (CompositionRoot.fs, inside "selfrouting" arm)

```fsharp
// selfrouter — classify call to 35B; 5s timeout (NOT 300s); 1 retry at 200ms.
// Separate named client prevents interference with "upstream35b" (300s inference timeout).
// RegisterS before RoutingAlgorithmRegistration factory (must exist before factory lambda runs).
services.AddHttpClient("selfrouter", fun (c: System.Net.Http.HttpClient) ->
    c.BaseAddress <- Uri(upstreamOptsLazy.Model35B)
    c.Timeout     <- TimeSpan.FromSeconds(5.0))
    .AddResilienceHandler("selfrouter-pipeline", fun (builder: ...) ->
        let retryOpts = HttpRetryStrategyOptions()
        retryOpts.MaxRetryAttempts <- 1
        retryOpts.BackoffType      <- DelayBackoffType.Constant
        retryOpts.Delay            <- TimeSpan.FromMilliseconds(200.0)
        ...
        builder.AddRetry(retryOpts) |> ignore)
    |> ignore
```

### ChatCompletions.fs Self-Classify Call Site (non-streaming branch)

```fsharp
// Phase 19 (SR-08): self-classify for non-streaming Default-reason decisions.
// streaming branch skips this entirely (SR-06 — latency budget).
let! decision =
    if not req.Stream && decision.Reason = Default then
        task {
            let selfRouter = ctx.RequestServices.GetService<ISelfRouter>()
            if isNull (box selfRouter) then
                return decision
            else
                let promptHash = computePromptHash req.Messages
                let promptText = req.Messages |> List.map (fun m -> m.Content) |> String.concat " "
                let! verdict = selfRouter.ClassifyAsync(promptHash, promptText, ctx.RequestAborted)
                match verdict with
                | RouteSafe ->
                    return { decision with Target = Qwen35B; Priority = Low;
                                           Reason = SelfRoute; ModelVersion = selfRouter.PromptVersion }
                | RouteUnsafe ->
                    return { decision with Target = Qwen122B; Priority = High;
                                           Reason = SelfRoute; ModelVersion = selfRouter.PromptVersion }
                | RouteSkipped _ | RouteFailed _ ->
                    return decision  // fail-open to Default 35B
        }
    else
        Task.FromResult(decision)
```

---

## Open Questions

1. **RoutingAlgorithm sync constraint**
   - What we know: `RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision` (synchronous); self-classify is async
   - What's unclear: whether to change the type alias (high churn) or use Option 3 (ChatCompletions non-streaming branch call after routeRequest)
   - Recommendation: **Use Option 3** — add a `decision = ...` rebind in the non-streaming branch after `Ok decision ->`, checking `decision.Reason = Default`. Avoids touching Core type definition or 20+ test sites.

2. **ModelVersion stamping for SelfRoute decisions**
   - What we know: SR-01 says `ModelVersion` reflects v2.0 selfrouter prompt hash; current stub uses `"selfrouting-v1"`
   - What's unclear: whether `ISelfRouter` should expose `PromptVersion` as an interface member or separate interface
   - Recommendation: Add `PromptVersion: string` to `ISelfRouter` interface. Computed at construction time from SHA-256 of prompt template file content. Falls back to `"selfrouting-v1"` if file missing at construction time.

3. **selfrouter_skipped counter semantics**
   - What we know: SR-05 requires `selfrouter_skipped` counter
   - What's unclear: does it count streaming skips (many) or only template-missing/adapter-missing skips (few)?
   - Recommendation: Count only `RouteSkipped` returns from the adapter (template missing, adapter null). Do NOT count streaming skips — those would inflate the counter and obscure real skips. Streaming skip counter should be a separate field if needed for observability.

---

## Sources

### Primary (HIGH confidence — direct codebase reads)

- `src/SmartRouter.Cli/Adapters/JudgeClient.fs` — full implementation; SelfRouter template (lines 1–329)
- `src/SmartRouter.Cli/Adapters/SessionStore.fs` — DI triple-reg pattern; LRU + TTL eviction (lines 1–163)
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — streaming-skip location (line 320); non-streaming branch (line 438); judge call site pattern (lines 452–617); Point B session write (lines 632–633)
- `src/SmartRouter.Cli/CompositionRoot.fs` — Mode-gated registration (lines 392–527); judge registration pattern (lines 647–722); configureWithoutMl NoOp pattern (lines 1114–1121); triple-reg patterns throughout
- `src/SmartRouter.Core/Domain.fs` — `RoutingAlgorithm` type alias (line 116); `RoutingReason` DU (lines 31–39); `RouterRequest.SessionId` (line 61); `RoutingDecision` record (lines 93–101)
- `src/SmartRouter.Core/Routing.fs` — `routeRequest` four-stage pipeline (lines 100–121); where algorithm closure is invoked (line 121)
- `src/SmartRouter.Core/HardRules.fs` — `applyHardRules` pattern (BCL-only pure function)
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — `computePromptHash` (lines 11–16); `formatReason` 8-arm match (lines 30–39)
- `src/SmartRouter.Cli/Endpoints/Stats.fs` — `StatsWire` extension pattern (lines 23–44); judge stats resolve (lines 90–104)
- `tests/SmartRouter.Tests/ModeSwitchTests.fs` — DI test fixture shape; W4 skip guard pattern (lines 212–218)
- `tests/SmartRouter.Tests/StickyEscalationTests.fs` — DI test fixture for selfrouting mode (lines 1–116)
- `tests/SmartRouter.Tests/RouterTests.fs` — `rootTests` explicit list; compile order constraint
- `tests/SmartRouter.Tests/*.fsproj` — compile order (StickyEscalationTests at position 43; RouterTests last)
- `src/SmartRouter.Cli/appsettings.json` — current config structure; no `Routing.SelfRouter` section yet

### Secondary (HIGH confidence — planning artifacts)

- `.planning/STATE.md` — cascade order locked; Phase 18 execution decisions; algorithm closure comment at line 519–520
- `.planning/ROADMAP.md` — Phase 19 plan boundary definitions (lines 78–101); success criteria (lines 88–92)
- `.planning/docs/35b-selfrouting-prompt.md` — §3 recommended prompt; §7 continuation UNSAFE examples; §8 `temperature=0, max_tokens=16–32` (overridden by SR-02 `max_tokens=4–8`)
- `.planning/docs/35b-selfrouting.md` — §6,12 Hard Rules scope; §§3–4 same-model rationale
- `.planning/research/SUMMARY.md` — confirmed HIGH-confidence pitfalls (QueueDispatcher bypass, streaming skip, cascade ordering)

---

## Metadata

**Confidence breakdown:**
- Core DU + adapter mechanics: HIGH — JudgeClient.fs is a direct template; all patterns verified in source
- Cascade integration point: HIGH — routeRequest structure verified; Option 3 (ChatCompletions branch) confirmed by type constraint analysis
- Stats wiring: HIGH — Phase 16 pattern directly replicable
- Prompt template design: HIGH — `.planning/docs/35b-selfrouting-prompt.md` is the primary spec
- ML dormant test: HIGH — ModeSwitchTests.fs is the direct template
- Plan sequencing: HIGH — type dependencies verified; linear sequence is required

**Research date:** 2026-05-11
**Valid until:** 2026-06-11 (stable F# patterns; 30-day window)
