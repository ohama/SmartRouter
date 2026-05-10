module SmartRouter.Cli.Adapters.JudgeClient

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

// ── JudgeVerdict DU ──────────────────────────────────────────────────────────

/// Phase 16 — Result of a single 1-token judge query.
/// RouteYes:      judge says response is good for the question → caller returns 35B response as-is
/// RouteNo:       judge says response is bad → caller should fall back to 122B
/// JudgeSkipped:  prompt template missing or judge disabled — caller treats as RouteYes (fail-open)
/// JudgeFailed:   HTTP failure / unparseable / timeout — caller treats as RouteYes (fail-open;
///                we must not suppress good 35B responses on judge infrastructure errors)
type JudgeVerdict =
    | RouteYes
    | RouteNo
    | JudgeSkipped of reason: string
    | JudgeFailed  of err: string

// ── JudgeOptions ─────────────────────────────────────────────────────────────

/// Cli-only options bound from appsettings.json "Routing:Judge" section.
/// Endpoint defaults to "" — CompositionRoot resolves "" → Upstreams.Model122B
/// at registration time (OQ #4 resolution: empty = derive from 122B; avoids
/// requiring operator to update two config keys when 122B port changes).
[<CLIMutable>]
type JudgeOptions = {
    Endpoint        : string   // default "" → derive from Upstreams.Model122B in CompositionRoot
    PromptPath      : string   // default "prompts/judge-prompt.md"
    TimeoutSeconds  : int      // default 5 (1-token responses; NOT 30 like teacher — Pitfall 3)
    MaxCacheEntries : int      // default 10000
}

// ── Port interfaces ───────────────────────────────────────────────────────────

/// Phase 16 port — caller invokes judge after BorderlineClassifier returns Some _.
/// promptHash/responseHash form the LRU cache key; promptText/responseText are
/// inserted into the prompt template as {{QUESTION}}/{{RESPONSE}} on cache miss.
///
/// CRITICAL (researcher Anti-Pattern): Do NOT route through IUpstreamClient /
/// QueueDispatcher — would consume the SemaphoreSlim(1) gate protecting real 122B
/// inference traffic. This port uses the named "judge" HttpClient registered in
/// CompositionRoot (Plan 16-03) with its own AddResilienceHandler.
type IJudgeClient =
    abstract member VerdictAsync :
        promptHash: string * responseHash: string * promptText: string * responseText: string * ct: CancellationToken
        -> Task<JudgeVerdict>

/// Phase 16 — judge cache + call counters exposed via /stats (JDG-03).
/// struct tuple avoids tiny allocations on the /stats hot path
/// (mirrors Phase 15 IQualityCheckStats.GetHits pattern).
/// NOT extending IQualityCheckStats — judge stats are owned by JudgeClient,
/// not QueueDispatcher; separate interface per plan constraint.
type IJudgeStats =
    /// Returns struct (cacheHits, cacheMisses, callCount).
    /// callCount = number of upstream HTTP calls to the "judge" named client
    ///             (cache misses that didn't skip due to missing template).
    /// cacheHits + cacheMisses = total VerdictAsync invocations (excluding skips
    /// from missing template, which are counted separately as JudgeSkipped returns).
    abstract member GetJudgeStats : unit -> struct (int64 * int64 * int64)

// ── LRU cache types ──────────────────────────────────────────────────────────

type private CacheEntry = {
    Verdict           : JudgeVerdict
    mutable AccessSeq : int64
}

// ── Parser ───────────────────────────────────────────────────────────────────

/// Parse the model response into a JudgeVerdict.
/// Mirrors TeacherLabeler.parseContent: ROUTE_NO wins on collision (safety bias).
/// Substring contains check (NOT exact match) handles trailing punctuation,
/// hallucinated suffixes, prose echoes from the model.
///
/// Safety bias rationale: the judge is invoked only for borderline responses where
/// we're uncertain. On collision (model confused), erring toward RouteNo (triggering
/// 122B fallback) is correct — we'd rather spend extra on 122B than return a
/// potentially bad 35B response to the user. Mirrors Phase 7 ROUTE_122B-wins.
let private parseContent (content: string) : JudgeVerdict =
    let hasYes = content.Contains("ROUTE_YES")
    let hasNo  = content.Contains("ROUTE_NO")
    match hasNo, hasYes with
    | true,  _    -> RouteNo          // NO wins on collision — safety bias
    | false, true -> RouteYes
    | false, false ->
        let snippet =
            let max = min 100 content.Length
            content.Substring(0, max)
        JudgeFailed (sprintf "unparseable judge response: %s" snippet)

// ── Response extraction ──────────────────────────────────────────────────────

/// Extract choices[0].message.content from an OpenAI chat-completions response.
/// Returns None on any structural mismatch — caller maps None to JudgeFailed.
/// Uses TryGetProperty to avoid exceptions on well-formed-but-wrong-shape JSON.
let private tryReadResponseContent (body: string) : string option =
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
                    Some (c.GetString().Trim())
                | _ -> None
            | _ -> None
        | _ -> None
    with _ -> None

// ── JudgeClient ──────────────────────────────────────────────────────────────

/// Phase 16 judge — calls the named "judge" HttpClient with a 1-token prompt
/// asking "is this response good for this question?". Caches verdicts by
/// (promptHash, responseHash) so identical content pays the judge cost once.
///
/// RETRY POLICY: The named "judge" HttpClient registered by Plan 16-03's
/// CompositionRoot carries AddResilienceHandler with 2 retries at 200ms/400ms
/// exponential backoff (OQ #5 resolution: fewer retries than teacher's 3×1s/2s/4s
/// because judge is on the hot path of borderline 35B requests; transient errors
/// should retry quickly without blowing the latency budget).
///
/// FAIL-OPEN: JudgeFailed and JudgeSkipped are both treated as RouteYes by the
/// caller (Plan 16-03) — judge infrastructure errors must not suppress good 35B
/// responses. Only a definitive RouteNo verdict triggers a 122B fallback.
type JudgeClient(httpFactory: IHttpClientFactory, options: JudgeOptions, logger: ILogger<JudgeClient>) =

    // Prompt template cache: None = not yet read; Some "" = file missing (cached miss).
    let mutable promptTemplate : string option = None
    let promptLock = obj ()

    // Counters — int64; updated via Interlocked; read via Volatile.Read in GetJudgeStats.
    let mutable cacheHits   = 0L
    let mutable cacheMisses = 0L
    let mutable callCount   = 0L
    let mutable globalSeq   = 0L

    // LRU cache. Key: (promptHash, responseHash) — content hashes, NOT envelope hashes.
    // Cache key is content-based per design: mlx_lm.server envelopes include timestamps
    // that differ per call; caching by content hash correctly reuses verdicts for
    // identical responses (researcher Pattern 4, Pitfall — avoid envelope hash).
    let cache = ConcurrentDictionary<string * string, CacheEntry>()

    // Defensive option normalization — defence in depth on top of CompositionRoot normalization.
    let promptPath =
        if String.IsNullOrWhiteSpace(options.PromptPath) then "prompts/judge-prompt.md"
        else options.PromptPath
    let maxCacheEntries =
        if options.MaxCacheEntries <= 0 then 10000 else options.MaxCacheEntries

    // ── Prompt template loader (lock-on-first-read; cached miss returns None) ──────

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
                            "JudgeClient: prompt template not found at {Path}; will skip all calls",
                            promptPath)
                        promptTemplate <- Some ""  // cached miss
                        None)

    // ── Cache primitives ────────────────────────────────────────────────────────────

    let tryGetCached (key: string * string) : JudgeVerdict option =
        match cache.TryGetValue(key) with
        | true, entry ->
            // Update access sequence (non-atomic write OK — AccessSeq is used only for
            // LRU eviction ordering, not correctness; see TOCTOU note in setCached).
            entry.AccessSeq <- Interlocked.Increment(&globalSeq)
            Interlocked.Increment(&cacheHits) |> ignore
            Some entry.Verdict
        | _ ->
            Interlocked.Increment(&cacheMisses) |> ignore
            None

    let setCached (key: string * string) (verdict: JudgeVerdict) =
        // TOCTOU note (researcher Pitfall 5): two concurrent threads may both see
        // Count >= maxCacheEntries and both evict. Acceptable: cache may temporarily
        // hold maxEntries+1 entries. Not a correctness issue — invariant is
        // "approximately bounded", not "never exceeds maxEntries by even 1".
        if cache.Count >= maxCacheEntries then
            try
                let minKv = cache |> Seq.minBy (fun kv -> kv.Value.AccessSeq)
                cache.TryRemove(minKv.Key) |> ignore
            with _ -> ()  // empty cache race or concurrent eviction; ignore
        let entry = { Verdict = verdict; AccessSeq = Interlocked.Increment(&globalSeq) }
        cache.[key] <- entry

    // ── Request body construction ───────────────────────────────────────────────────

    /// Build the chat-completions request body.
    /// max_tokens=1: forces a single-token response (ROUTE_YES or ROUTE_NO).
    /// temperature=0.0: deterministic response — same input → same token.
    /// stream=false: judge is sync verification, not streaming.
    ///
    /// JsonFSharpConverter is REQUIRED here despite bodyDict being a plain
    /// Dictionary<string,obj>: the messages array elements ARE F# anonymous records
    /// ({| role; content |}), which System.Text.Json does not serialize correctly
    /// without the converter. Cross-reference: TeacherLabeler.fs::buildBody uses
    /// the identical pattern (same converter, same anonymous-record messages array).
    /// DO NOT remove this converter as "apparently unnecessary".
    let buildBody (template: string) (promptText: string) (responseText: string) : string =
        let systemContent =
            template
                .Replace("{{QUESTION}}", promptText)
                .Replace("{{RESPONSE}}", responseText)
        let bodyDict = Dictionary<string, obj>()
        bodyDict.["messages"] <-
            [|
                {| role = "system"; content = systemContent |} :> obj
            |]
        bodyDict.["max_tokens"]  <- 1     :> obj   // KEY: 1-token response (deterministic)
        bodyDict.["temperature"] <- 0.0   :> obj
        bodyDict.["stream"]      <- false :> obj
        let opts = JsonSerializerOptions()
        opts.Converters.Add(JsonFSharpConverter())
        JsonSerializer.Serialize(bodyDict, opts)

    // ── Single HTTP attempt ─────────────────────────────────────────────────────────
    // Retry is handled by the named "judge" HttpClient's AddResilienceHandler
    // (registered in CompositionRoot Plan 16-03: 2 retries, 200ms/400ms, 5xx + transient).
    // This function fires a single attempt and maps outcomes to JudgeVerdict.

    let attemptOnce (client: HttpClient) (body: string) (ct: CancellationToken) : Task<JudgeVerdict> =
        task {
            try
                use reqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                reqMsg.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                use! resp = client.SendAsync(reqMsg, ct)
                if not resp.IsSuccessStatusCode then
                    let! errBody = resp.Content.ReadAsStringAsync(ct)
                    let snippet = if errBody.Length > 200 then errBody.Substring(0, 200) else errBody
                    return JudgeFailed (sprintf "HTTP %d: %s" (int resp.StatusCode) snippet)
                else
                    let! responseJson = resp.Content.ReadAsStringAsync(ct)
                    match tryReadResponseContent responseJson with
                    | None ->
                        let snippet =
                            if responseJson.Length > 200 then responseJson.Substring(0, 200)
                            else responseJson
                        return JudgeFailed (sprintf "unparseable judge envelope: %s" snippet)
                    | Some content ->
                        return parseContent content
            with
            | :? HttpRequestException as ex ->
                return JudgeFailed (sprintf "judge HTTP request failed: %s" ex.Message)
            | :? TaskCanceledException as ex when ex.CancellationToken = ct ->
                return JudgeFailed "client cancelled"
            | :? TaskCanceledException ->
                return JudgeFailed "judge endpoint timeout"
        }

    // ── IJudgeClient interface ──────────────────────────────────────────────────────

    interface IJudgeClient with
        member _.VerdictAsync(promptHash, responseHash, promptText, responseText, ct) =
            task {
                let key = (promptHash, responseHash)

                // 1. Cache lookup — hit returns immediately, no HTTP call
                match tryGetCached key with
                | Some cached ->
                    return cached
                | None ->

                // 2. Prompt template required
                match getPromptTemplate () with
                | None ->
                    // Template missing → skip. NOT cached: operator may add template at runtime;
                    // we must not poison the cache with a "no template" verdict.
                    return JudgeSkipped "prompt template missing"
                | Some template ->

                // 3. Build request body and fire HTTP call
                Interlocked.Increment(&callCount) |> ignore
                let body = buildBody template promptText responseText
                let client = httpFactory.CreateClient("judge")
                let! verdict = attemptOnce client body ct

                // 4. Cache real verdicts only (RouteYes/RouteNo).
                // JudgeFailed may be transient (network blip); not worth caching.
                // JudgeSkipped is already handled above (unreachable here).
                match verdict with
                | RouteYes | RouteNo -> setCached key verdict
                | JudgeFailed _ | JudgeSkipped _ -> ()

                return verdict
            }

    // ── IJudgeStats interface ───────────────────────────────────────────────────────

    interface IJudgeStats with
        /// Returns struct (cacheHits, cacheMisses, callCount).
        /// Volatile.Read ensures fresh values on /stats endpoint without locking.
        /// Allocation-free struct return mirrors Phase 15 IQualityCheckStats.GetHits.
        member _.GetJudgeStats() =
            struct (
                Volatile.Read(&cacheHits),
                Volatile.Read(&cacheMisses),
                Volatile.Read(&callCount))
