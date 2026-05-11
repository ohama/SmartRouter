module SmartRouter.Cli.Adapters.SelfRouter

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

// ── SelfRouteVerdict DU (SR-03) ──────────────────────────────────────────────

/// Phase 19 — Result of a single self-classify query to the 35B model.
/// RouteSafe:     35B classified the prompt as safe for itself → route to 35B
/// RouteUnsafe:   35B classified the prompt as needing 122B → escalate
/// RouteSkipped:  prompt template missing or feature not applicable — caller treats as RouteSafe (fail-open)
/// RouteFailed:   HTTP failure / unparseable / timeout — caller treats as RouteSafe (fail-open;
///                we must not suppress 35B on classifier infrastructure errors)
type SelfRouteVerdict =
    | RouteSafe
    | RouteUnsafe
    | RouteSkipped of reason: string
    | RouteFailed  of error: string

// ── SelfRouterOptions ─────────────────────────────────────────────────────────

/// Cli-only options bound from appsettings.json "Routing:SelfRouter" section.
/// Endpoint defaults to "" — CompositionRoot resolves "" → Upstreams.Model35B at
/// registration time (same pattern as JudgeOptions resolves to 122B).
[<CLIMutable>]
type SelfRouterOptions = {
    mutable Endpoint        : string   // default "" → derive from Upstreams.Model35B in CompositionRoot
    mutable PromptPath      : string   // default "prompts/self-router-prompt.md"
    mutable TimeoutSeconds  : int      // default 5 (classify must be quick — NOT 300 like inference)
    mutable MaxCacheEntries : int      // default 10000
}

// ── Port interfaces ───────────────────────────────────────────────────────────

/// Phase 19 self-routing port — caller invokes self-classify for non-streaming requests
/// that passed Hard Rules + explicit overrides + sticky session without match.
///
/// promptHash is the SHA-256 of the full prompt (via computePromptHash in DecisionLogger.fs).
/// promptText is the raw text inserted into the {{PROMPT}} placeholder.
///
/// CRITICAL: Do NOT route through IUpstreamClient / QueueDispatcher — would block on the
/// 122B SemaphoreSlim(1) gate protecting real inference traffic. Uses named "selfrouter"
/// HttpClient registered in CompositionRoot (Plan 19-02).
///
/// PromptVersion: SHA-256 hex8 prefix of the prompt file contents at construction time.
/// Threaded into DecisionLog's model_version field by Plan 19-03. Required for SC-1.
type ISelfRouter =
    abstract member ClassifyAsync :
        promptHash: string * promptText: string * ct: CancellationToken
        -> Task<SelfRouteVerdict>
    abstract member PromptVersion : string   // "selfrouting-{sha8}" or "selfrouting-v1" fallback

/// Phase 19 — self-router cache + call counters exposed via /stats.
/// struct tuple avoids tiny allocations on the /stats hot path
/// (mirrors Phase 16 IJudgeStats.GetJudgeStats pattern).
type ISelfRouterStats =
    /// Returns struct (cacheHits, cacheMisses, callCount, skipped).
    /// callCount = number of upstream HTTP calls to the "selfrouter" named client.
    /// cacheHits + cacheMisses = total ClassifyAsync invocations (excluding skips from missing template).
    abstract member GetSelfRouterStats : unit -> struct (int64 * int64 * int64 * int64)

// ── LRU cache types ──────────────────────────────────────────────────────────

type private CacheEntry = {
    Verdict           : SelfRouteVerdict
    mutable AccessSeq : int64
}

// ── Parser (SAFETY-BIASED, SR-03) ────────────────────────────────────────────

/// Parse the model response into a SelfRouteVerdict.
/// CRITICAL — PITFALL #1 (RESEARCH §9): UNSAFE substring check MUST run BEFORE SAFE check.
/// "UNSAFE" contains "SAFE" as a substring. If hasSafe is tested first, a model response of
/// "UNSAFE" matches SAFE and incorrectly routes to 35B — silent correctness failure on SC-2.
///
/// Safety bias: on ambiguous output (neither SAFE nor UNSAFE recognized) → RouteFailed,
/// which callers treat as fail-open (RouteSafe). On collision (both substrings present) →
/// RouteUnsafe wins — erring toward 122B is correct when 35B is uncertain.
let private parseContent (content: string) : SelfRouteVerdict =
    let hasUnsafe = content.Contains("UNSAFE", StringComparison.OrdinalIgnoreCase)
    let hasSafe   = content.Contains("SAFE",   StringComparison.OrdinalIgnoreCase)
    // UNSAFE check FIRST — load-bearing order (SAFE ⊂ UNSAFE)
    match hasUnsafe, hasSafe with
    | true,  _    -> RouteUnsafe         // UNSAFE wins on collision (safety bias)
    | false, true -> RouteSafe
    | false, false ->
        let snippet = content.Substring(0, min 100 content.Length)
        RouteFailed (sprintf "unparseable self-router response: %s" snippet)

// ── Response extraction ───────────────────────────────────────────────────────

/// Extract choices[0].message.content from an OpenAI chat-completions response.
/// Returns Ok content on success, Error reason on any structural mismatch.
/// Mirrors JudgeClient.tryReadResponseContent exactly.
let private tryReadResponseContent (body: string) : Result<string, string> =
    try
        use doc = JsonDocument.Parse(body)
        let root = doc.RootElement
        match root.TryGetProperty("choices") with
        | true, choices when choices.ValueKind = JsonValueKind.Array
                              && choices.GetArrayLength() > 0 ->
            let first = choices.[0]
            match first.TryGetProperty("message") with
            | true, msg ->
                match msg.TryGetProperty("content") with
                | true, c when c.ValueKind = JsonValueKind.String ->
                    Ok (c.GetString().Trim())
                | _ -> Error "missing or non-string choices[0].message.content"
            | _ -> Error "missing choices[0].message"
        | _ -> Error "missing or empty choices array"
    with ex -> Error (sprintf "JSON parse error: %s" ex.Message)

// ── SelfRouter ────────────────────────────────────────────────────────────────

/// Phase 19 self-router — calls the named "selfrouter" HttpClient with a classify
/// prompt asking the 35B model "is this safe for me?". Caches verdicts by
/// promptHash (SHA-256 of full prompt) so identical prompts pay the classify cost once.
///
/// RETRY POLICY: The named "selfrouter" HttpClient registered by Plan 19-02's
/// CompositionRoot carries AddResilienceHandler (mirrors JudgeClient pattern).
/// This class fires a single attempt per invocation; resilience is in the transport layer.
///
/// FAIL-OPEN: RouteFailed and RouteSkipped are both treated as RouteSafe by the
/// caller (Plan 19-03) — classifier infrastructure errors must not suppress fast 35B
/// responses. Only a definitive RouteUnsafe verdict triggers escalation to 122B.
type SelfRouter(httpFactory: IHttpClientFactory, options: SelfRouterOptions, logger: ILogger<SelfRouter>) =

    // Prompt template cache: None = not yet read; Some "" = file missing (cached miss).
    let mutable promptTemplate : string option = None
    let promptLock = obj ()

    // Counters — int64; updated via Interlocked; read via Volatile.Read in GetSelfRouterStats.
    let mutable cacheHits   = 0L
    let mutable cacheMisses = 0L
    let mutable callCount   = 0L
    let mutable skippedCnt  = 0L
    let mutable globalSeq   = 0L

    // LRU cache. Key: promptHash — SHA-256 of full prompt content.
    // Single-string key (vs JudgeClient's tuple) because self-classify has no response to hash.
    let cache = ConcurrentDictionary<string, CacheEntry>()

    // Defensive option normalization (mirrors JudgeClient).
    let promptPath =
        if String.IsNullOrWhiteSpace(options.PromptPath) then "prompts/self-router-prompt.md"
        else options.PromptPath
    let maxCacheEntries =
        if options.MaxCacheEntries <= 0 then 10000 else options.MaxCacheEntries

    // ── PromptVersion (SR-01 / SC-1): SHA-256 of prompt file at construction time ───

    /// SHA-256 hex8 prefix of prompt file contents — threaded into DecisionLog.model_version
    /// by Plan 19-03 so operators can correlate routing decisions with prompt template versions.
    /// Falls back to "selfrouting-v1" if the file is absent at construction time.
    let promptVersion : string =
        try
            if File.Exists(promptPath) then
                use sha = SHA256.Create()
                let bytes = File.ReadAllBytes(promptPath)
                let hash  = sha.ComputeHash(bytes)
                let hex   = hash |> Array.map (sprintf "%02x") |> String.concat ""
                sprintf "selfrouting-%s" (hex.Substring(0, 8))
            else
                "selfrouting-v1"    // fallback when prompt file absent; operator must add it
        with _ -> "selfrouting-v1"

    // ── Prompt template loader (lock-on-first-read; mirrors JudgeClient.getPromptTemplate) ──

    /// Returns the prompt template content, OR None if the file is missing.
    /// Thread-safe: lock around first read; subsequent reads return the cached value.
    /// Missing file is cached as Some "" to log the warning exactly once.
    let getPromptTemplate () : string option =
        match promptTemplate with
        | Some "" -> None   // cached miss — logged once at first attempt
        | Some s  -> Some s
        | None ->
            lock promptLock (fun () ->
                match promptTemplate with
                | Some "" -> None
                | Some s  -> Some s
                | None ->
                    if File.Exists(promptPath) then
                        let content = File.ReadAllText(promptPath)
                        promptTemplate <- Some content
                        Some content
                    else
                        logger.LogWarning(
                            "SelfRouter: prompt template not found at {Path}; will skip all calls",
                            promptPath)
                        promptTemplate <- Some ""  // cached miss
                        None)

    // ── Cache primitives (mirrors JudgeClient; single-string key) ─────────────────────

    let tryGetCached (key: string) : SelfRouteVerdict option =
        match cache.TryGetValue(key) with
        | true, entry ->
            entry.AccessSeq <- Interlocked.Increment(&globalSeq)
            Interlocked.Increment(&cacheHits) |> ignore
            Some entry.Verdict
        | _ ->
            Interlocked.Increment(&cacheMisses) |> ignore
            None

    let setCached (key: string) (verdict: SelfRouteVerdict) =
        // TOCTOU note (mirrors JudgeClient setCached): two concurrent threads may both
        // see Count >= maxCacheEntries and both evict. Acceptable — invariant is
        // "approximately bounded", not "never exceeds maxEntries by even 1".
        if cache.Count >= maxCacheEntries then
            try
                let minKv = cache |> Seq.minBy (fun kv -> kv.Value.AccessSeq)
                cache.TryRemove(minKv.Key) |> ignore
            with _ -> ()  // empty cache race or concurrent eviction; ignore
        let entry = { Verdict = verdict; AccessSeq = Interlocked.Increment(&globalSeq) }
        cache.[key] <- entry

    // ── Request body construction ─────────────────────────────────────────────────────

    /// Build the chat-completions request body for the self-classify call.
    /// max_tokens=8: absorbs whitespace/punctuation drift while keeping classify fast (SR-02;
    ///   researcher recommends 8 over 4 — RESEARCH §9 PITFALL #5).
    /// temperature=0.0: deterministic response — same input → same token.
    /// stream=false: self-classify is sync verification, not streaming.
    ///
    /// JsonFSharpConverter is REQUIRED here: the messages array elements ARE F# anonymous
    /// records ({| role; content |}), which System.Text.Json does not serialize correctly
    /// without the converter. Cross-reference: JudgeClient.fs::buildBody uses identical
    /// pattern (lines 229-232). DO NOT remove this converter as "apparently unnecessary".
    let buildBody (promptText: string) : string =
        let filled =
            match getPromptTemplate () with
            | Some t -> t.Replace("{{PROMPT}}", promptText)
            | None   -> promptText  // fallback: raw prompt (template already logged as missing)
        let bodyDict = Dictionary<string, obj>()
        bodyDict.["messages"] <-
            [|
                {| role = "user"; content = filled |} :> obj
            |]
        bodyDict.["max_tokens"]  <- 8     :> obj   // SR-02: 8 tokens absorbs whitespace/punctuation drift
        bodyDict.["temperature"] <- 0.0   :> obj
        bodyDict.["stream"]      <- false :> obj
        let opts = JsonSerializerOptions()
        opts.Converters.Add(JsonFSharpConverter())
        JsonSerializer.Serialize(bodyDict, opts)

    // ── Single HTTP attempt ───────────────────────────────────────────────────────────
    // Retry is handled by the named "selfrouter" HttpClient's AddResilienceHandler
    // (registered in CompositionRoot Plan 19-02). This function fires a single attempt.

    let attemptOnce (body: string) (ct: CancellationToken) : Task<Result<string, string>> =
        task {
            try
                // CRITICAL: factory.CreateClient("selfrouter") — NOT IUpstreamClient.
                // IUpstreamClient routes through QueueDispatcher which holds the 122B SemaphoreSlim(1).
                // Using it here would block inference traffic on the classifier (RESEARCH §9 PITFALL #2).
                let client = httpFactory.CreateClient("selfrouter")
                use reqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                reqMsg.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                use! resp = client.SendAsync(reqMsg, ct)
                if not resp.IsSuccessStatusCode then
                    let! errBody = resp.Content.ReadAsStringAsync(ct)
                    let snippet = if errBody.Length > 200 then errBody.Substring(0, 200) else errBody
                    return Error (sprintf "selfrouter HTTP %d: %s" (int resp.StatusCode) snippet)
                else
                    let! responseJson = resp.Content.ReadAsStringAsync(ct)
                    return tryReadResponseContent responseJson
            with
            | :? HttpRequestException as ex ->
                return Error (sprintf "selfrouter HTTP request failed: %s" ex.Message)
            | :? TaskCanceledException as ex when ex.CancellationToken = ct ->
                return Error "selfrouter: client cancelled"
            | :? TaskCanceledException ->
                return Error "selfrouter: endpoint timeout"
        }

    // ── ISelfRouter interface ─────────────────────────────────────────────────────────

    interface ISelfRouter with
        member _.PromptVersion = promptVersion

        member _.ClassifyAsync(promptHash, promptText, ct) =
            task {
                // 1. Cache lookup — hit returns immediately, no HTTP call (SR-04)
                match tryGetCached promptHash with
                | Some cached ->
                    return cached
                | None ->

                // 2. Prompt template required
                match getPromptTemplate () with
                | None ->
                    // Template missing → skip. NOT cached: operator may add template at runtime;
                    // caching a "no template" verdict would prevent recovery without restart.
                    Interlocked.Increment(&skippedCnt) |> ignore
                    return RouteSkipped (sprintf "self-router prompt template missing at %s" promptPath)
                | Some _ ->

                // 3. Build request body and fire HTTP call
                Interlocked.Increment(&callCount) |> ignore
                let body = buildBody promptText
                let! result = attemptOnce body ct

                match result with
                | Ok content ->
                    let verdict = parseContent content
                    // 4. Cache only RouteSafe / RouteUnsafe — never cache transient failures.
                    // RouteFailed may be a network blip; caching it would poison the LRU for
                    // the entire TTL period. RouteSkipped is already handled above.
                    // Mirrors JudgeClient lines 310-313.
                    match verdict with
                    | RouteSafe | RouteUnsafe -> setCached promptHash verdict
                    | RouteFailed _ | RouteSkipped _ -> ()
                    return verdict
                | Error e ->
                    return RouteFailed e
            }

    // ── ISelfRouterStats interface ────────────────────────────────────────────────────

    interface ISelfRouterStats with
        /// Returns struct (cacheHits, cacheMisses, callCount, skipped).
        /// Volatile.Read ensures fresh values on /stats endpoint without locking.
        /// Allocation-free struct return mirrors Phase 16 IJudgeStats.GetJudgeStats.
        member _.GetSelfRouterStats() =
            struct (
                Volatile.Read(&cacheHits),
                Volatile.Read(&cacheMisses),
                Volatile.Read(&callCount),
                Volatile.Read(&skippedCnt))
