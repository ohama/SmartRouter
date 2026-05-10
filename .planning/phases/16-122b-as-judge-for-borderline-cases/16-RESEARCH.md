# Phase 16: 122B-as-Judge for Borderline Cases — Research

**Researched:** 2026-05-10
**Domain:** F# quality verification pipeline — borderline classification, LRU cache, named HttpClient judge, trace schema extension
**Confidence:** HIGH

---

## Summary

Phase 16 adds a "lazy verification" layer between Phase 15's cheap-first heuristic cascade and the existing 122B retry fallback. Only responses that fall into a "borderline" band — not clearly Bad (would already trigger fallback), not clearly Good (confident pass) — get a 1-token YES/NO call to 122B asking "Is this response correct and helpful?" Clearly Bad responses keep the existing fast retry path; clearly Good responses never touch the judge.

The key structural finding is that **the cleanest implementation keeps `analyzeResponse` returning `Verdict = Good | Bad` unchanged** and adds `BorderlineClassifier.fs` as a separate new file with its own `classifyBorderline` function and a `BorderlineKind` DU. This avoids breaking the 6 pattern-match arms in `ChatCompletions.fs` that currently handle `Good | Bad of BadReason`. The cascade in `ChatCompletions.fs` becomes: analyzeResponse → if Good → classifyBorderline → if Borderline → judge → decide.

The `IJudgeClient` interface and `JudgeClient` implementation mirror Phase 7's `ITeacherLabeler` / `TeacherLabeler` pattern almost exactly: named HttpClient "judge" registered with `AddHttpClient("judge").ConfigureHttpClient(...).AddResilienceHandler(...)`, a prompt file at `prompts/judge-prompt.md`, and a parser looking for `ROUTE_YES` / `ROUTE_NO` in the first token. The LRU cache is implemented in-process with `ConcurrentDictionary` + a monotonic int64 access counter, bounded by `MaxCacheEntries` (default 10000). For Phase 16 at this scale, a simple "evict entry with minimum access counter when count exceeds limit" is correct and avoids the complexity of a full doubly-linked-list LRU.

The prompt hash is already computed per request by `computePromptHash` in `DecisionLogger.fs`. The response hash should be computed as the SHA-256 hex of `extractAssistantText body` (content only, not the JSON envelope) to maximize cache reuse across requests where the envelope differs by timestamp but the content is identical.

**Primary recommendation:** Add `BorderlineClassifier.fs` as a new separate file (not extending QualityCheck.fs), add `JudgeClient.fs` mirroring TeacherLabeler pattern, implement bounded LRU cache inside JudgeClient, and wire the cascade in ChatCompletions.fs non-streaming branch after the existing Good verdict check. Register `IJudgeClient` as optional in both composition paths (real in `configureRequestPipeline`, NoOp returning ROUTE_YES in `configureWithoutMl`).

---

## Standard Stack

No new NuGet packages required. All Phase 16 work uses BCL + already-present packages.

### Core (already present)
| Component | Source | Purpose |
|-----------|--------|---------|
| `System.Collections.Concurrent.ConcurrentDictionary` | BCL | Thread-safe LRU cache backing store |
| `System.Threading.Interlocked` | BCL | Lock-free access counter increment |
| `System.Security.Cryptography.SHA256` | BCL | Response hash for cache key |
| `System.Net.Http.IHttpClientFactory` | BCL / ASP.NET Core | Named "judge" HttpClient |
| `Microsoft.Extensions.Http.Resilience` | Already in `.fsproj` | `AddResilienceHandler` for judge pipeline |
| `FSharp.SystemTextJson` | Already in `.fsproj` | `JsonFSharpConverter` for option serialization in TraceRecord |

### No new packages needed
Phase 16 is purely in `SmartRouter.Cli`. `SmartRouter.Core` stays BCL-only (ARCH-01 enforced). The judge HTTP call is identical in shape to the teacher call — same OpenAI-compatible endpoint, same JSON body structure.

---

## Architecture Patterns

### Recommended File Structure (additions only)

```
src/SmartRouter.Cli/
├── Adapters/
│   ├── BorderlineClassifier.fs   # NEW — pure F#, BCL only; JDG-01
│   └── JudgeClient.fs            # NEW — IJudgeClient port + cache + HTTP; JDG-02 + JDG-03
├── Endpoints/
│   └── ChatCompletions.fs        # MODIFIED — cascade wiring; JDG-04 + JDG-05
│   └── Stats.fs                  # MODIFIED — judge cache fields; JDG-03
├── Adapters/
│   └── QueueDispatcher.fs        # MODIFIED — IJudgeStats or extend IQualityCheckStats; JDG-03
│   └── TraceLogger.fs            # MODIFIED — 3 new TraceRecord fields; JDG-05
├── CompositionRoot.fs             # MODIFIED — IJudgeClient DI wiring; JDG-02
├── appsettings.json               # MODIFIED — Routing.Judge section; JDG-02
prompts/
└── judge-prompt.md                # NEW — operator-tunable prompt template; JDG-02
```

**fsproj compile order:** `BorderlineClassifier.fs` must be added after `QualityCheck.fs` and before `ChatCompletions.fs`. `JudgeClient.fs` must be added after `BorderlineClassifier.fs` (or could be before it — they're independent) and before `ChatCompletions.fs`. Suggested order:

```xml
<Compile Include="Adapters/QualityCheck.fs" />
<Compile Include="Adapters/BorderlineClassifier.fs" />   <!-- NEW Phase 16 -->
<Compile Include="Adapters/JudgeClient.fs" />            <!-- NEW Phase 16 -->
```

### Pattern 1: Separate BorderlineClassifier (not extending QualityCheck)

**What:** `BorderlineClassifier.fs` defines a new `BorderlineKind` DU and `classifyBorderline` function. `QualityCheck.analyzeResponse` stays at `Good | Bad of BadReason` — no breaking change to the 6 `ChatCompletions.fs` pattern-match arms.

**Why separate file, not extending QualityCheck.fs:** Phase 15's `analyzeResponse` has a well-defined contract. Adding `Borderline` to the `Verdict` DU would require updating all 6 existing `match initialVerdict with` arms in ChatCompletions.fs plus `isBadResponse`. A separate file with its own `BorderlineKind` type makes Phase 16 an additive change, not a modification of Phase 15's types.

**Why separate from analyzeResponse:** "Borderline" only applies when `analyzeResponse` returned `Good`. Borderline means "Good by the heuristic but uncertain — worth asking 122B." It is not a third verdict alongside Good and Bad; it is a qualifier on Good. The architecture reflects this: first call `analyzeResponse` → if `Good` → call `classifyBorderline` → if `Some borderlineKind` → judge call.

```fsharp
// Source: design derived from quality-check-improvement-options.md §3-A
module SmartRouter.Cli.Adapters.BorderlineClassifier

open SmartRouter.Cli.Adapters.QualityCheck

type BorderlineKind =
    | UncertainEntropy of score : float      // entropy in [threshold, threshold + 1.0]
    | UncertainLength  of effectiveLen : int // length in [min, min * 1.5)

/// JDG-01: Classify a Good response as borderline or confident-good.
/// Returns None when response is clearly good (no judge call needed).
/// Returns Some BorderlineKind when the response falls in the uncertainty band.
///
/// PRECONDITION: analyzeResponse returned Verdict.Good.
/// If analyzeResponse returned Verdict.Bad, do NOT call this — quality fallback fires.
///
/// Band thresholds (to be confirmed by planner):
///   entropy:  [EntropyThreshold, EntropyThreshold + 1.0]  → UncertainEntropy
///   length:   [MinResponseLength, int(MinResponseLength * 1.5)]  → UncertainLength
///
/// Keyword dimension is deliberately excluded from borderline:
///   - A keyword match is binary (present or absent) — no natural "partial" zone.
///   - Case-insensitive match already provides partial coverage.
///   - "partial keyword match" semantics are not clearly defined and risk false positives.
///   - finish_reason is also excluded: "length"/"content_filter" are decisive signals, not uncertain ones.
let classifyBorderline
    (opts       : QualityFallbackOptions)
    (content    : string)    // extractAssistantText already called by analyzeResponse caller
    : BorderlineKind option =
    // Entropy band: [threshold, threshold + 1.0)
    let entropy = charEntropy content
    let entropyUpper = opts.EntropyThreshold + 1.0
    if opts.EntropyThreshold > 0.0
       && entropy >= opts.EntropyThreshold
       && entropy < entropyUpper then
        Some (UncertainEntropy entropy)
    else
        // Length band: [min, min * 1.5)
        let effLen = effectiveLength content   // if exposed; or re-compute inline
        let lengthUpper = int (float opts.MinResponseLength * 1.5)
        if effLen >= opts.MinResponseLength && effLen < lengthUpper then
            Some (UncertainLength effLen)
        else
            None
```

**Open question for planner:** The borderline band upper bounds (`EntropyThreshold + 1.0`, `MinResponseLength × 1.5`) are recommendations — the planner should decide whether to make them configurable as `Routing.Judge.EntropyBandWidth` / `Routing.Judge.LengthBandFactor` or hard-code them. Hard-coding simplifies the initial implementation; making them configurable adds operator flexibility. Recommendation: hard-code for Phase 16 (matching Phase 14/15 precedent of shipping sensible defaults before exposing knobs).

**Implementation note:** `charEntropy` and `effectiveLength` are currently `private` in `QualityCheck.fs`. If `BorderlineClassifier.fs` needs them, two options: (a) re-compute inline (copy the 5-line formulas — acceptable for BCL-only functions), or (b) make them `internal` in `QualityCheck.fs`. Option (a) is simpler and avoids any API surface change to a tested module.

### Pattern 2: JudgeClient — mirrors TeacherLabeler

**What:** `JudgeClient.fs` defines `IJudgeClient`, `JudgeVerdict` DU, `JudgeOptions`, and `JudgeClient` class implementing the interface with in-process LRU cache.

```fsharp
// Source: TeacherLabeler.fs pattern + quality-check-improvement-options.md §3-A
type JudgeVerdict = RouteYes | RouteNo | JudgeSkipped of reason: string | JudgeFailed of err: string

type IJudgeClient =
    abstract member VerdictAsync :
        promptHash    : string *
        responseHash  : string *
        promptText    : string *
        responseText  : string *
        ct            : CancellationToken
        -> Task<JudgeVerdict>

[<CLIMutable>]
type JudgeOptions =
    { Endpoint        : string   // default: reuse Upstreams.Model122B (http://127.0.0.1:8001)
      PromptPath      : string   // default "prompts/judge-prompt.md"
      TimeoutSeconds  : int      // default 5 (1-token response; much shorter than teacher's 30)
      MaxCacheEntries : int }    // default 10000
```

**Parser pattern — ROUTE_YES bias toward NO on collision:**

```fsharp
// Mirrors TeacherLabeler.parseContent pattern.
// "ROUTE_NO" wins when both substrings appear (conservative bias — don't suppress quality fallback).
let private parseContent (content: string) : JudgeVerdict =
    let hasYes = content.Contains("ROUTE_YES")
    let hasNo  = content.Contains("ROUTE_NO")
    match hasNo, hasYes with
    | true,  _    -> RouteNo          // NO wins on collision (safety bias)
    | false, true -> RouteYes
    | false, false -> JudgeFailed (sprintf "unparseable: %s" (content.Substring(0, min 100 content.Length)))
```

**1-token max_tokens in request body:**

```fsharp
bodyDict.["max_tokens"]  <- 1 :> obj   // force 1-token response
bodyDict.["temperature"] <- 0.0 :> obj  // deterministic
bodyDict.["stream"]      <- false :> obj
```

### Pattern 3: LRU Cache — ConcurrentDictionary + monotonic counter

**What:** Bounded in-process LRU cache using `ConcurrentDictionary<(string * string), CacheEntry>` where `CacheEntry = { Verdict: JudgeVerdict; mutable AccessSeq: int64 }`. When count exceeds `MaxCacheEntries`, find and evict the entry with the minimum `AccessSeq` value.

**BCL-only rationale:** No thread-safe ordered dictionary in BCL. A proper LRU with O(1) eviction requires a doubly-linked list + hash map, which introduces concurrency complexity. For `MaxCacheEntries = 10000`, O(n) eviction (scan all 10000 entries) is acceptable: eviction fires at most once per new entry added over the limit, and the scan of 10000 entries is negligible compared to the 122B HTTP call it saves.

```fsharp
// Source: BCL + derived from JDG-03 requirement
type private CacheEntry = {
    Verdict   : JudgeVerdict
    mutable AccessSeq : int64
}

let private cache = ConcurrentDictionary<string * string, CacheEntry>()
let mutable private globalSeq = 0L
let mutable cacheHits   = 0L
let mutable cacheMisses = 0L

let private tryGetCached (key: string * string) : JudgeVerdict option =
    match cache.TryGetValue(key) with
    | true, entry ->
        entry.AccessSeq <- Interlocked.Increment(&globalSeq)
        Interlocked.Increment(&cacheHits) |> ignore
        Some entry.Verdict
    | _ ->
        Interlocked.Increment(&cacheMisses) |> ignore
        None

let private setCached (key: string * string) (verdict: JudgeVerdict) (maxEntries: int) =
    if cache.Count >= maxEntries then
        // Evict minimum-AccessSeq entry (O(n) scan; acceptable at n=10000)
        let minKey =
            cache
            |> Seq.minBy (fun kv -> kv.Value.AccessSeq)
            |> fun kv -> kv.Key
        cache.TryRemove(minKey) |> ignore
    cache.[key] <- { Verdict = verdict; AccessSeq = Interlocked.Increment(&globalSeq) }
```

**Thread safety note:** The eviction strategy above has a TOCTOU race: two threads can both see `Count >= maxEntries` and both evict. This is acceptable: the result is double-eviction (cache slightly under limit), not corruption. `ConcurrentDictionary` itself is thread-safe; the `AccessSeq` field is `mutable` and written non-atomically, which is acceptable because it's used only for eviction heuristics, not correctness.

### Pattern 4: Cache Key — content hash, not envelope hash

**What:** `(promptHash, responseHash)` where `promptHash = computePromptHash req.Messages` (already computed by DecisionLogger) and `responseHash = SHA-256 hex of extractAssistantText body`.

**Why content, not envelope:** The raw JSON envelope from mlx_lm.server includes timestamps, request IDs, and usage counts that differ across calls even when the model generates identical content. Caching by envelope hash would produce zero hits for semantically identical responses. Caching by content hash correctly reuses verdicts when 35B produces the same text in response to the same prompt.

```fsharp
// Source: DecisionLogger.fs computePromptHash pattern
open System.Security.Cryptography

let private computeResponseHash (responseBody: string) : string =
    let content = extractAssistantText responseBody
    let bytes = System.Text.Encoding.UTF8.GetBytes(content)
    use sha = SHA256.Create()
    sha.ComputeHash(bytes)
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""
```

The prompt hash is already available in `ChatCompletions.fs` via `computePromptHash req.Messages` (called later in the trace block). For Phase 16, compute it earlier in the non-streaming branch so both the judge cache and the trace can share it.

### Pattern 5: ChatCompletions.fs Cascade Integration

**What:** After `analyzeResponse` returns `Good`, insert borderline classification and optional judge call before returning 35B's response.

**Insertion point:** Lines 424–482 in current `ChatCompletions.fs`. The existing `Good` branch (when `qualityFallbackTriggered = false`) goes directly to `return (initialDecision, initialBody)`. Phase 16 intercepts this path.

```fsharp
// After existing analyzeResponse block (lines 419-429 in ChatCompletions.fs)
// Phase 16: Borderline judge — only on Good 35B responses
let judgeClient = ctx.RequestServices.GetService<IJudgeClient>()
let initialContent = extractAssistantText initialBody  // reuse extracted content

let! (finalDecision, finalBody, judgeCalledFlag, judgeVerdictStr, judgeLatencyMs) = task {
    match initialVerdict with
    | Bad _ ->
        // Existing fast fallback path — judge never called
        if not (healthProbe.IsReachable(Qwen122B)) then
            return (initialDecision, initialBody, false, None, None)
        else
            // ... existing 122B retry logic ...
            return (retryDecision, retryBody, false, None, None)
    | Good ->
        // Phase 16: borderline check
        if isNull (box judgeClient) then
            // Judge disabled (configureWithoutMl NoOp) — pass Good as-is
            return (initialDecision, initialBody, false, None, None)
        else
            match classifyBorderline qualityFallbackOpts initialContent with
            | None ->
                // Clearly good — no judge call
                return (initialDecision, initialBody, false, None, None)
            | Some _borderlineKind ->
                let judgeStart = DateTimeOffset.UtcNow
                let promptHash = computePromptHash req.Messages
                let responseHash = computeResponseHash initialBody
                let promptText =
                    req.Messages
                    |> List.map (fun m -> m.Content)
                    |> String.concat " "
                let! verdict = judgeClient.VerdictAsync(promptHash, responseHash, promptText, initialContent, ctx.RequestAborted)
                let judgeMs = (DateTimeOffset.UtcNow - judgeStart).TotalMilliseconds
                match verdict with
                | RouteNo ->
                    // Judge says bad — trigger quality fallback
                    if not (healthProbe.IsReachable(Qwen122B)) then
                        return (initialDecision, initialBody, true, Some "no", Some judgeMs)
                    else
                        let retryDecision = { initialDecision with Target = Qwen122B; Reason = FallbackTo122B; IsFallback = true; ModelVersion = versionProvider.CurrentVersion }
                        let! retryResult = upstream.CompleteAsync req retryDecision ctx.RequestAborted
                        match retryResult with
                        | Ok retryBody -> return (retryDecision, retryBody, true, Some "no", Some judgeMs)
                        | Error _ ->     return (initialDecision, initialBody, true, Some "no", Some judgeMs)
                | RouteYes | JudgeSkipped _ | JudgeFailed _ ->
                    // Judge says good (or unavailable) — forward 35B response
                    return (initialDecision, initialBody, true, Some "yes", Some judgeMs)
}
```

**Simplification option:** The planner may choose to split this into a helper function rather than inline. Given `ChatCompletions.fs` already has the FS3511 suppression for a long task{} block, the inline approach should work but may make it longer. A helper `private let applyJudge (...)` extracted before the handler would be cleaner.

### Pattern 6: /stats New Fields

**What:** Add `judge_cache_hits`, `judge_cache_misses`, `judge_call_count` to `StatsWire` and `StatsSnapshot`.

**Separation decision — new `IJudgeStats` interface:**

Phase 15 established `IQualityCheckStats` on `QueueDispatcher` because QueueDispatcher was the natural aggregation point for request-path stats. Phase 16's judge stats are owned by `JudgeClient`, not `QueueDispatcher`. The cleanest pattern is a new `IJudgeStats` interface implemented by `JudgeClient`, resolved in `Stats.fs` alongside `IStatsProvider`.

```fsharp
// In JudgeClient.fs
type IJudgeStats =
    abstract member GetJudgeStats : unit -> struct (int64 * int64 * int64)
    // returns (cacheHits, cacheMisses, callCount)
```

`StatsWire` extension in `Stats.fs`:

```fsharp
type private StatsWire =
    { // ... existing 20 fields ...
      judge_cache_hits   : int64   // NEW Phase 16
      judge_cache_misses : int64   // NEW Phase 16
      judge_call_count   : int64   // NEW Phase 16
    }
```

`Stats.mapEndpoints` resolves `IJudgeStats` via `GetService<IJudgeStats>()` (null-safe, mirrors traceLogger pattern) and populates the three fields (defaulting to 0L when judge is disabled).

### Pattern 7: TraceRecord Extension (13 → 16 fields)

**What:** Add 3 new fields to `TraceRecord`. `schema_version = 1` stays unchanged (additive-only change per JDG-05).

```fsharp
// In TraceLogger.fs — add after bad_reason field
[<JsonPropertyName("judge_called")>]
judge_called        : bool          // NEW Phase 16 — true when judge was invoked
[<JsonPropertyName("judge_verdict")>]
judge_verdict       : string option // NEW Phase 16 — "yes" | "no" | null
[<JsonPropertyName("judge_latency_ms")>]
judge_latency_ms    : float option  // NEW Phase 16 — null when judge not called
```

JSON serialization order: fields are serialized in declaration order by `System.Text.Json` with `SnakeCaseLower` policy. The three new fields land at the end of each JSONL row, after `bad_reason`. Readers that don't know about Phase 16 ignore them (forward-compatible). Old log processors expecting 13 fields will see 16 fields — schema_version=1 remains unchanged because this is purely additive.

**ChatCompletions.fs construction site** — the `traceLogger.Log({...})` call at line 506–520 gets three new fields:

```fsharp
traceLogger.Log({
    // ... existing 13 fields ...
    judge_called        = judgeCalledFlag          // bool
    judge_verdict       = judgeVerdictStr          // string option
    judge_latency_ms    = judgeLatencyMs           // float option
})
```

### Pattern 8: DI Registration — both composition paths

**configureRequestPipeline (production):**

```fsharp
// Judge config
services.Configure<JudgeOptions>(config.GetSection("Routing:Judge")) |> ignore

// Named "judge" HttpClient — mirrors "teacher" registration pattern
services.AddHttpClient("judge", fun (c: HttpClient) ->
    let opts = config.GetSection("Routing:Judge").Get<JudgeOptions>()
    let endpoint =
        if String.IsNullOrWhiteSpace(opts.Endpoint)
        then config.GetSection("Upstreams").Get<UpstreamOptions>().Model122B  // default: reuse 122B
        else opts.Endpoint
    let timeoutSec = if opts.TimeoutSeconds <= 0 then 5 else opts.TimeoutSeconds
    c.BaseAddress <- Uri(endpoint)
    c.Timeout     <- TimeSpan.FromSeconds(float timeoutSec))
    .AddResilienceHandler("judge-pipeline", fun builder ->
        // Same 5xx-retry, no 4xx retry pattern as teacher-pipeline
        let retryOpts = HttpRetryStrategyOptions()
        retryOpts.MaxRetryAttempts <- 2    // Judge: fewer retries than teacher (latency budget)
        retryOpts.BackoffType      <- DelayBackoffType.Exponential
        retryOpts.Delay            <- TimeSpan.FromMilliseconds(200.0)
        retryOpts.ShouldHandle     <- Func<...>(fun args -> ... (* 5xx + HttpRequestException + TaskCanceledException *))
        builder.AddRetry(retryOpts) |> ignore)
    |> ignore

// JudgeClient — concrete singleton + IJudgeClient alias + IJudgeStats alias
services.AddSingleton<JudgeClient>(fun sp ->
    let opts = ... (* normalize defaults *)
    JudgeClient(sp.GetRequiredService<IHttpClientFactory>(), normalized,
                sp.GetRequiredService<ILogger<JudgeClient>>()))
    |> ignore

services.AddSingleton<IJudgeClient>(fun sp -> sp.GetRequiredService<JudgeClient>() :> IJudgeClient)
    |> ignore

services.AddSingleton<IJudgeStats>(fun sp -> sp.GetRequiredService<JudgeClient>() :> IJudgeStats)
    |> ignore
```

**configureWithoutMl (offline --retrain path):**

```fsharp
// IJudgeClient NoOp — borderline treated as Good (Phase 15 behavior preserved)
services.AddSingleton<IJudgeClient>(fun _ ->
    { new IJudgeClient with
        member _.VerdictAsync(_, _, _, _, _) =
            Task.FromResult(JudgeSkipped "judge disabled in offline mode") })
    |> ignore

// IJudgeStats NoOp
services.AddSingleton<IJudgeStats>(fun _ ->
    { new IJudgeStats with
        member _.GetJudgeStats() = struct (0L, 0L, 0L) })
    |> ignore
```

**ChatCompletions.fs consumer pattern (JDG-05):**

```fsharp
// GetService (nullable) — not GetRequiredService — so judge absence doesn't throw
let judgeClient = ctx.RequestServices.GetService<IJudgeClient>()
// null check: if isNull (box judgeClient) then borderline = good
```

### Pattern 9: Judge Prompt Template

The prompt should elicit `ROUTE_YES` or `ROUTE_NO` as the first (and only) token. Structure mirrors `teacher-prompt.md` — system message with placeholders, no user message needed.

```markdown
# prompts/judge-prompt.md

You are a quality judge for an AI assistant.

Question: {{QUESTION}}

Response: {{RESPONSE}}

Is this response correct and helpful for the question above? Answer ONLY:

ROUTE_YES
or
ROUTE_NO
```

**Placeholder substitution pattern** (same as `TeacherLabeler.buildBody`):

```fsharp
let systemContent =
    template
        .Replace("{{QUESTION}}", promptText)
        .Replace("{{RESPONSE}}", responseText)
```

**Note:** The judge prompt is operator-tunable via `Routing.Judge.PromptPath` (default `"prompts/judge-prompt.md"`). The template is cached in memory after first read (same `promptLock` + `mutable promptTemplate` pattern as `TeacherLabeler`).

### Anti-Patterns to Avoid

- **Extending `Verdict` DU to add `Borderline`:** Would require updating 6 existing pattern-match arms in `ChatCompletions.fs` plus `isBadResponse` wrapper. Phase 15's types have well-tested contracts. Keep them stable.
- **Routing judge calls through `IUpstreamClient` / `QueueDispatcher`:** Would consume the SemaphoreSlim(1) gate that protects real 122B inference traffic. Judge calls are separate, brief, and should bypass the queue entirely. Same pitfall as TeacherLabeler (documented in `CompositionRoot.fs` line 532 comment).
- **Using `GetRequiredService<IJudgeClient>()`:** Judge is optional. If judge is not registered (hypothetical future scenario where it's removed), the handler would throw. Use `GetService<IJudgeClient>()` with null check.
- **Caching by full response body hash:** The mlx_lm.server JSON envelope includes `created` (Unix timestamp) and `id` fields that differ per call. Two calls returning identical content would miss the cache. Hash `extractAssistantText body` only.
- **Blocking on `extractAssistantText` call count:** `extractAssistantText` is already called once in Phase 15's `analyzeResponse` block (line 188 of `QualityCheck.fs`). In `ChatCompletions.fs`, the caller extracts the content once and passes it to both `analyzeResponse` and `classifyBorderline`. Don't parse the JSON three times.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Named HttpClient timeout + retry | Custom `HttpClient` + manual retry loop | `AddHttpClient("judge").ConfigureHttpClient().AddResilienceHandler()` | Already established pattern; resilience handler handles 5xx, transport errors, per-attempt timeout correctly |
| Thread-safe LRU | Full doubly-linked-list + lock | `ConcurrentDictionary` + monotonic counter + O(n) eviction scan | At n=10000, scan takes ~microseconds; full LRU complexity buys nothing here |
| Prompt hash | Re-derive SHA-256 | `computePromptHash req.Messages` (already in `DecisionLogger.fs`) | Already computed; share the value |
| JSON content extraction | Re-parse the body | `extractAssistantText body` from `QualityCheck.fs` | Already tested; safe-on-fail; returns "" on malformed |
| SHA-256 | External library | `System.Security.Cryptography.SHA256` (BCL) | BCL, no NuGet, already used by `computePromptHash` |

---

## Common Pitfalls

### Pitfall 1: Named HttpClient 2-arg form in F#
**What goes wrong:** Using `services.AddHttpClient("judge", fun (c: HttpClient) -> ...)` (the 2-arg overload) silently fails to set `BaseAddress` in F# due to how the overload resolves with unit-returning lambdas. The client is registered but has no base address; every call fails with "Invalid URI."
**Why it happens:** F# overload resolution picks a different overload than expected. `CompositionRoot.fs` line 209 comment documents this explicitly.
**How to avoid:** Use `.AddHttpClient("judge").ConfigureHttpClient(fun c -> ...)` chain form (same as all existing named clients in Phase 10 block).
**Warning signs:** `HttpRequestException: An invalid request URI was provided.` at first judge call.

### Pitfall 2: Double-parse of response body
**What goes wrong:** `analyzeResponse` calls `extractAssistantText` internally (line 188 in QualityCheck.fs). The caller in `ChatCompletions.fs` then calls `extractAssistantText` again for the borderline classifier and again for the response hash. Three JSON parses of the same body per request.
**Why it happens:** `extractAssistantText` is called inside `analyzeResponse` — the caller doesn't have the result. The borderline classifier also needs the content.
**How to avoid:** Extract content once in `ChatCompletions.fs` before calling either function, and pass it into both. This may require a small refactor: expose a variant of `analyzeResponse` that accepts pre-extracted content, or accept the double-parse as acceptable (2 JSON parses of a ~500 byte string is negligible).
**Recommendation:** For Phase 16, accept one extra parse. Refactoring `analyzeResponse` signature is more disruptive than it's worth at this stage.

### Pitfall 3: Judge timeout too long
**What goes wrong:** Setting `TimeoutSeconds = 30` (same as teacher) makes the 1-token judge call wait up to 30s when 122B is slow. This defeats the purpose of the judge (should be much faster than a full 122B call).
**Why it happens:** Copy-paste from TeacherLabeler options without adjusting.
**How to avoid:** Default `Routing.Judge.TimeoutSeconds = 5`. 1-token responses from mlx_lm at 122B speed are typically <500ms. 5s gives a 10× safety margin. If the judge call takes >5s, something is wrong and the caller should fall back to treating borderline=good rather than waiting.

### Pitfall 4: Verdict DU equality in ConcurrentDictionary key
**What goes wrong:** Using `(string * string)` as `ConcurrentDictionary` key — F# tuples implement structural equality correctly, but the default `EqualityComparer` for `string * string` in .NET uses `Object.GetHashCode` on the tuple. F# compiled tuples DO implement value equality, so this works correctly. No custom `IEqualityComparer` is needed.
**Verification:** `("a", "b") = ("a", "b")` is `true` in F#; `("a", "b").GetHashCode() = ("a", "b").GetHashCode()` is true because F# tuples override `GetHashCode`.

### Pitfall 5: TOCTOU in cache size eviction
**What goes wrong:** Two concurrent threads both check `cache.Count >= maxEntries`, both attempt eviction, both add their new entries — resulting in the cache temporarily having `maxEntries + 1` entries.
**Why it happens:** The count check and eviction are not atomic.
**How to handle:** This is acceptable. The invariant is "cache stays approximately bounded," not "cache never exceeds maxEntries by even 1." A fully atomic eviction would require a global lock that defeats the purpose of `ConcurrentDictionary`. Document this in code as intentional.

### Pitfall 6: Expecto rootTests explicit list (PITFALL-26)
**What goes wrong:** Adding new test modules without adding them to `rootTests` in `Tests.fs`. Tests compile and run zero cases without error.
**How to avoid:** Always add new test list to `rootTests` in `tests/SmartRouter.Tests/Tests.fs`.

### Pitfall 7: AddHostedService for JudgeClient
**What goes wrong:** `JudgeClient` is NOT a `BackgroundService`. Do not register it with `AddHostedService`. It's a plain singleton with an interface. If the planner adds a background drain loop (not needed), that's a separate concern.
**Correct pattern:** `AddSingleton<JudgeClient>` + `AddSingleton<IJudgeClient>` + `AddSingleton<IJudgeStats>` — triple alias, no hosted service.

---

## Code Examples

### JudgeClient complete structure

```fsharp
// Source: TeacherLabeler.fs pattern adapted for judge
type JudgeClient(httpFactory: IHttpClientFactory, options: JudgeOptions, logger: ILogger<JudgeClient>) =

    let mutable promptTemplate : string option = None
    let promptLock = obj ()
    let cache = ConcurrentDictionary<string * string, CacheEntry>()
    let mutable globalSeq   = 0L
    let mutable cacheHits   = 0L
    let mutable cacheMisses = 0L
    let mutable callCount   = 0L

    let getPromptTemplate () : string option =
        // ... same lock-on-first-read pattern as TeacherLabeler ...

    let buildBody (template: string) (promptText: string) (responseText: string) : string =
        let systemContent =
            template
                .Replace("{{QUESTION}}", promptText)
                .Replace("{{RESPONSE}}", responseText)
        let bodyDict = Dictionary<string, obj>()
        bodyDict.["messages"]    <- [| {| role = "system"; content = systemContent |} :> obj |]
        bodyDict.["max_tokens"]  <- 1    :> obj   // KEY: 1 token only
        bodyDict.["temperature"] <- 0.0  :> obj
        bodyDict.["stream"]      <- false:> obj
        JsonSerializer.Serialize(bodyDict, jsonOpts)

    interface IJudgeClient with
        member _.VerdictAsync(promptHash, responseHash, promptText, responseText, ct) =
            task {
                let key = (promptHash, responseHash)
                match tryGetCached key with
                | Some cached -> return cached    // cache hit — no HTTP call
                | None ->
                    match getPromptTemplate () with
                    | None -> return JudgeSkipped "prompt template missing"
                    | Some template ->
                        Interlocked.Increment(&callCount) |> ignore
                        let body = buildBody template promptText responseText
                        let client = httpFactory.CreateClient("judge")
                        let! verdict = attemptOnce client body ct
                        setCached key verdict options.MaxCacheEntries
                        return verdict
            }

    interface IJudgeStats with
        member _.GetJudgeStats() =
            struct (Volatile.Read(&cacheHits), Volatile.Read(&cacheMisses), Volatile.Read(&callCount))
```

### appsettings.json addition

```jsonc
"Routing": {
  // ... existing QualityFallback section ...
  "Judge": {
    "Endpoint":        "",          // empty = reuse Upstreams.Model122B
    "PromptPath":      "prompts/judge-prompt.md",
    "TimeoutSeconds":  5,
    "MaxCacheEntries": 10000
  }
}
```

### Integration test — verify judge NOT called for clearly-good response

```fsharp
// Source: QualityFallbackTests.fs fake-Kestrel pattern
// Clearly good response: entropy ~4.5, length 200+, no keyword match
let mutable judgeCallCount = 0
let fakeJudge : IJudgeClient =
    { new IJudgeClient with
        member _.VerdictAsync(_, _, _, _, _) =
            Interlocked.Increment(&judgeCallCount) |> ignore
            Task.FromResult(RouteYes) }
// ... start router with fakeJudge injected ...
// ... send request with clearly-good 35B response ...
// Assert: judgeCallCount = 0
```

---

## State of the Art

| Old Approach | Current Approach (Phase 16) | Impact |
|--------------|----------------------------|--------|
| Binary Good/Bad verdict (Phase 14-15) | 3-way: Bad → fast fallback, Borderline → judge, Good → pass | Reduces false positives (35B responses that are Good but pass the heuristic are now verified) |
| No caching of quality judgments | `(promptHash, responseHash) → verdict` LRU cache | Identical 35B responses (e.g. repeated system prompts) pay judge cost once |
| All quality fallbacks cost 1 full 122B call | Borderline cases cost 1 1-token judge call (~50× cheaper than full inference) | Latency budget for borderline: <100ms typical vs 5-30s for full 122B call |

---

## Open Questions

1. **Borderline band upper bounds — hard-code or make configurable?**
   - What we know: `EntropyThreshold + 1.0` and `MinResponseLength × 1.5` are reasonable defaults
   - What's unclear: whether operators will need to tune these independently
   - Recommendation: hard-code for Phase 16; if operators need tuning in Phase 17, add `Routing.Judge.EntropyBandWidth` and `Routing.Judge.LengthBandFactor` then

2. **`charEntropy` and `effectiveLength` visibility in QualityCheck.fs**
   - What we know: they are currently `private` — `BorderlineClassifier.fs` cannot call them
   - Options: (a) re-implement inline in `BorderlineClassifier.fs` (5 lines each), (b) make `internal`, (c) make `let` (module-level = public in F# by default — but would add to the module's public API surface)
   - Recommendation: re-implement inline — these are trivial pure functions; staying private in QualityCheck keeps the tested module stable

3. **`computePromptHash` availability in ChatCompletions.fs**
   - What we know: `computePromptHash` is in `DecisionLogger.fs` and is already called inside `buildDecisionLog` which is private. The trace block at line 495 calls it directly: `let promptHash = computePromptHash req.Messages`.
   - What's unclear: whether Phase 16 should hoist `promptHash` computation earlier in the handler (before the quality fallback block) to share it between judge cache and trace log, or compute it twice.
   - Recommendation: hoist `promptHash` computation to immediately after `let initialDecision = decision` so it's available for both the judge cache key and the existing trace block. The prompt hash computation is O(n) SHA-256 on message text — negligible.

4. **JudgeOptions.Endpoint default — read from Upstreams or hard-code?**
   - What we know: judge calls go to 122B; `Upstreams.Model122B` is already in config
   - Options: (a) empty string = "use Upstreams.Model122B" (requires reading Upstreams in normalization), (b) hard-code "http://127.0.0.1:8001" as default
   - Recommendation: (a) empty = resolve from Upstreams at normalization time — avoids operator having to update two config entries when they move 122B to a different port

5. **Judge retry count — 2 or 3?**
   - What we know: teacher uses 3 retries with exponential 1s/2s/4s. For a 5s timeout, 3 retries + backoff could exceed 10s total.
   - Recommendation: 2 retries with 200ms/400ms backoff. Total worst-case: 5s × 3 attempts = 15s — still much less than a full 122B call. Or: 0 retries for the judge (the answer is either YES or NO; if 122B is struggling, treat as JudgeFailed and degrade to good).

6. **fallback_kind field in TraceRecord when judge fires**
   - What we know: currently `fallback_kind = Some "quality"` when Phase 15 bad reason triggered a retry, `None` when no fallback
   - What's unclear: when judge fires and returns NO (triggering a retry), should `fallback_kind = Some "quality"` (same as before) or `Some "quality_judge"` (new value)?
   - Recommendation: keep `Some "quality"` — the reason for the retry is still quality failure. The `judge_verdict = Some "no"` field distinguishes judge-triggered from heuristic-triggered. Introducing `"quality_judge"` as a new value would change downstream tooling expectations.

---

## Sources

### Primary (HIGH confidence — directly read from codebase)
- `src/SmartRouter.Cli/Adapters/QualityCheck.fs` — Phase 15 final state: Verdict DU, analyzeResponse, all helper functions
- `src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` — Judge HttpClient template: named client, parser pattern, prompt caching, buildBody
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — Current cascade insertion point, pattern match structure, trace block
- `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` — IQualityCheckStats pattern; counter types; IStatsProvider
- `src/SmartRouter.Cli/Endpoints/Stats.fs` — StatsWire extension pattern; snake_case fields
- `src/SmartRouter.Cli/Adapters/TraceLogger.fs` — TraceRecord 13-field schema; JsonPropertyName pattern
- `src/SmartRouter.Cli/CompositionRoot.fs` — Named HttpClient chain form (line 209 pitfall note); IQualityCheckStats NoOp pattern; configureWithoutMl structure
- `src/SmartRouter.Cli/appsettings.json` — Routing section structure; TeacherLabeler section pattern
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Compile order; confirmed no new NuGet needed
- `tests/SmartRouter.Tests/QualityFallbackTests.fs` — Fake-Kestrel pattern; startTestRouter; stub injection
- `tests/SmartRouter.Tests/QualitySignalEnrichmentTests.fs` — Phase 15 test pattern; mkOpts; counter assertion

### Primary (HIGH confidence — design documents)
- `.planning/docs/quality-check-improvement-options.md` §3-A — Tier 3 122B-as-judge design: flow diagram, trade-offs, cache strategy, trace fields
- `.planning/REQUIREMENTS.md` §"122B-as-Judge for Borderline Cases" — JDG-01..05 full text
- `README.md` §5.5 — Current quality fallback cascade documented (Phase 16 must extend)

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; all patterns derived from existing code
- Architecture: HIGH — BorderlineClassifier separation, JudgeClient/TeacherLabeler mirror, DI dual-path confirmed from reading configureRequestPipeline + configureWithoutMl
- LRU cache: HIGH — BCL ConcurrentDictionary pattern; O(n) eviction acceptable at 10000 entries
- Borderline thresholds: MEDIUM — `EntropyThreshold + 1.0` and `MinResponseLength × 1.5` are reasonable defaults but not empirically validated; planner should confirm
- Judge prompt: MEDIUM — format elicits YES/NO; ROUTE_YES/ROUTE_NO token prefix matches parser expectations; actual 122B response behavior is environment-dependent

**Research date:** 2026-05-10
**Valid until:** 2026-06-10 (stable domain; Phase 15 code just completed and is stable)
