---
phase: 16-122b-as-judge-for-borderline-cases
plan: 02
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Cli/Adapters/JudgeClient.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - prompts/judge-prompt.md
autonomous: true

must_haves:
  truths:
    - "`prompts/judge-prompt.md` exists and uses `{{QUESTION}}` and `{{RESPONSE}}` placeholders; instructs the model to emit `ROUTE_YES` or `ROUTE_NO` as the entire response"
    - "`Adapters/JudgeClient.fs` defines `IJudgeClient` port with `VerdictAsync(promptHash, responseHash, promptText, responseText, ct) : Task<JudgeVerdict>`"
    - "`JudgeVerdict` DU has 4 cases: `RouteYes`, `RouteNo`, `JudgeSkipped of reason: string`, `JudgeFailed of err: string`"
    - "`JudgeOptions` record has 4 fields: `Endpoint`, `PromptPath`, `TimeoutSeconds`, `MaxCacheEntries`; CLIMutable for IOptions binding"
    - "`JudgeClient` class implements `IJudgeClient` AND `IJudgeStats` (single instance carries both interfaces)"
    - "Parser in JudgeClient: `ROUTE_NO` wins over `ROUTE_YES` on substring collision (safety bias — judge errs toward suppressing 35B response)"
    - "Request body: `max_tokens=1`, `temperature=0.0`, `stream=false` (1-token deterministic verification)"
    - "LRU cache: `ConcurrentDictionary<(string*string), CacheEntry>`; eviction on `cache.Count >= MaxCacheEntries` evicts entry with minimum `AccessSeq`"
    - "`IJudgeStats.GetJudgeStats()` returns `struct (cacheHits, cacheMisses, callCount)` int64 tuple (allocation-free; mirrors Phase 15 IQualityCheckStats.GetHits)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/JudgeClient.fs"
      provides: "IJudgeClient + IJudgeStats + JudgeClient implementation + LRU cache + named HttpClient consumer"
      contains: "module SmartRouter.Cli.Adapters.JudgeClient"
      contains2: "type IJudgeClient"
      contains3: "type IJudgeStats"
      contains4: "type JudgeVerdict"
      contains5: "type JudgeOptions"
      contains6: "ConcurrentDictionary"
      contains7: "ROUTE_NO"
      contains8: "ROUTE_YES"
      min_lines: 200
    - path: "prompts/judge-prompt.md"
      provides: "operator-tunable judge prompt template; ROUTE_YES/ROUTE_NO sentinel"
      contains: "{{QUESTION}}"
      contains2: "{{RESPONSE}}"
      contains3: "ROUTE_YES"
      contains4: "ROUTE_NO"
    - path: "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
      provides: "JudgeClient.fs registered in <Compile> list AFTER BorderlineClassifier.fs and BEFORE ChatCompletions.fs"
      contains: "Adapters/JudgeClient.fs"
  key_links:
    - from: "src/SmartRouter.Cli/Adapters/JudgeClient.fs"
      to: "IHttpClientFactory.CreateClient(\"judge\")"
      via: "named HttpClient lookup (NOT IUpstreamClient — would consume QueueDispatcher's SemaphoreSlim(1))"
      pattern: "CreateClient\\(\"judge\"\\)"
    - from: "src/SmartRouter.Cli/Adapters/JudgeClient.fs"
      to: "prompts/judge-prompt.md"
      via: "File.ReadAllText(promptPath) cached on first read with promptLock obj"
      pattern: "File\\.ReadAllText.*promptPath"
    - from: "src/SmartRouter.Cli/Adapters/JudgeClient.fs cache key"
      to: "ChatCompletions.fs response_hash + prompt_hash callers (Plan 16-03 will pass these in)"
      via: "VerdictAsync signature: (promptHash, responseHash, promptText, responseText, ct)"
      pattern: "VerdictAsync.*promptHash.*responseHash"
---

<objective>
Add `Adapters/JudgeClient.fs` implementing the judge port (`IJudgeClient`), the LRU cache, the `IJudgeStats` interface for /stats exposure, and the prompt template. Mirrors Phase 7 `TeacherLabeler.fs` pattern almost exactly: named HttpClient consumer (caller-side; the `"judge"` HttpClient is registered in Plan 16-03's CompositionRoot edits), prompt template caching, deterministic 1-token request body, parser with safety bias.

Purpose: Phase 16's judge call is a 1-token verification ("Is this response good for this question? YES/NO") on borderline 35B outputs. The cache (`(prompt_hash, response_hash) → JudgeVerdict`) avoids re-asking 122B about identical content. `IJudgeStats` exposes cache hit/miss/call counters via /stats so operators can monitor judge effectiveness.

Output: A new BCL+ASP.NET-Core file (Cli adapter; ARCH-01 Core boundary preserved) that the wiring plan (16-03) will instantiate and consume.

Parallelism: This plan has NO depends_on (wave 1, parallel with 16-01). Different files (`JudgeClient.fs` vs `BorderlineClassifier.fs`); fsproj edits are at different `<Compile>` lines and DO NOT collide. Plan 16-03 (wave 2) waits for both.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/16-122b-as-judge-for-borderline-cases/16-RESEARCH.md
@src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
@src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
@src/SmartRouter.Cli/SmartRouter.Cli.fsproj
@prompts/teacher-prompt.md
</context>

<rationale>
This plan resolves researcher open questions #4 and #5 (and creates the prompt template per autonomous decision B):

**OQ #4 — `JudgeOptions.Endpoint` default:** EMPTY STRING means "derive from `Upstreams.Model122B`". Why: judge calls go to 122B; if operator moves 122B to a different port, they shouldn't have to update two places. Empty string in config + normalize-at-DI-time pattern. The endpoint resolution lives in CompositionRoot (Plan 16-03), so JudgeOptions itself just declares `Endpoint : string` defaulting to "" via CLIMutable.

**OQ #5 (researcher's #5) — Judge retry count:** 2 RETRIES at 200ms / 400ms exponential backoff. Why: judge is on the hot path of borderline 35B requests; transient mlx_lm errors should retry quickly. Teacher's 3-retry / 1s-2s-4s pattern is too slow for a 1-token call (worst case 7s on 5s timeout = budget blown). 2 retries × 200ms+400ms ≈ 600ms backoff overhead + 5s timeout per attempt = 15s worst-case absolute ceiling. NOTE: the resilience handler itself lives in CompositionRoot (Plan 16-03) — this plan only documents the policy in the JudgeClient module-level comment so the wiring plan implements it correctly.

**Autonomous decision B — Judge prompt content:** Use the exact template from `quality-check-improvement-options.md` § Tier 3-A. Two placeholders: `{{QUESTION}}` (the original prompt text), `{{RESPONSE}}` (35B's answer). Instructs the model to emit `ROUTE_YES` or `ROUTE_NO` as the first token and nothing else.

**LRU cache rationale (researcher Pattern 3):** `ConcurrentDictionary` + monotonic `AccessSeq` counter. Eviction = O(n) `Seq.minBy` scan. At MaxCacheEntries=10000, scan is ~microseconds — negligible compared to the 122B HTTP call it saves. Full doubly-linked-list LRU is over-engineering. TOCTOU race in eviction is acceptable (cache may temporarily hold maxEntries+1; not a correctness issue — researcher Pitfall 5).

**Cache key:** `(prompt_hash : string, response_hash : string)` tuple. F# tuples implement structural equality + GetHashCode correctly via the runtime-compiled tuple type — no custom IEqualityComparer needed (researcher Pitfall 4 verified).

**ARCH-01 invariant:** JudgeClient.fs lives in `SmartRouter.Cli.Adapters.*` namespace. References Microsoft.Extensions.Logging + System.Net.Http + System.Threading.Channels (already in Cli adapters). No edits to `SmartRouter.Core`. Preserves Core BCL-only invariant.

**Anti-pattern guards (researcher §"Anti-Patterns"):**
- Do NOT route judge calls through `IUpstreamClient` / `QueueDispatcher` — would consume the SemaphoreSlim(1) gate that protects real 122B inference traffic. Judge uses its own named HttpClient bypass.
- Do NOT register JudgeClient as `BackgroundService` — it's a plain singleton with interfaces; no internal loop.
- Do NOT cache by raw response body hash — mlx_lm envelopes include timestamps/IDs that differ per call. Hash `extractAssistantText body` content only (this is the responsibility of the CALLER in 16-03; JudgeClient receives the already-extracted content).
</rationale>

<tasks>

<task type="auto">
  <name>Task 1: Create prompts/judge-prompt.md</name>
  <files>prompts/judge-prompt.md</files>
  <action>
Create a NEW file `prompts/judge-prompt.md` mirroring the structure of `prompts/teacher-prompt.md` but for binary YES/NO quality verification:

```markdown
You are a quality judge for an AI assistant.

Your job is to decide whether a response correctly and helpfully answers a question.

Question:
{{QUESTION}}

Response:
{{RESPONSE}}

Is the response correct and helpful for the question above? Answer ONLY:

ROUTE_YES
or
ROUTE_NO
```

NOTES:
- Two placeholders: `{{QUESTION}}` for the original prompt text (concatenated message contents), `{{RESPONSE}}` for the 35B response content (already extracted from envelope by caller).
- ROUTE_YES / ROUTE_NO sentinels match the parser pattern in JudgeClient.fs Task 2 (mirrors TeacherLabeler's ROUTE_35B / ROUTE_122B convention).
- The "Answer ONLY:" wording elicits a single-token response — combined with `max_tokens=1` in the request body, the model emits exactly one of the two sentinels.
- File is operator-tunable via `Routing.Judge.PromptPath` (default `"prompts/judge-prompt.md"`).
- Plain markdown (UTF-8, LF line endings, no BOM).
  </action>
  <verify>
- `cat prompts/judge-prompt.md` shows the template with both placeholders and both sentinels
- `grep -c "{{QUESTION}}\|{{RESPONSE}}\|ROUTE_YES\|ROUTE_NO" prompts/judge-prompt.md` returns at least 4 (one for each)
- `file prompts/judge-prompt.md` reports UTF-8 text (no BOM)
- `wc -l prompts/judge-prompt.md` is between 10 and 30 lines
  </verify>
  <done>
File exists, contains both placeholders, both sentinels, and "Answer ONLY:" instruction. Operator can edit this file without rebuilding.
  </done>
</task>

<task type="auto">
  <name>Task 2: Create JudgeClient.fs (port + cache + HTTP consumer + IJudgeStats)</name>
  <files>src/SmartRouter.Cli/Adapters/JudgeClient.fs</files>
  <action>
Create a NEW file `src/SmartRouter.Cli/Adapters/JudgeClient.fs`. Mirror `TeacherLabeler.fs` structure for the HTTP/prompt portions; add the LRU cache and IJudgeStats counters as new code.

REQUIRED MODULE STRUCTURE:

```fsharp
module SmartRouter.Cli.Adapters.JudgeClient

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

/// Phase 16 — Result of a single 1-token judge query.
/// RouteYes:       judge says response is good for the question
/// RouteNo:        judge says response is bad → caller should fall back to 122B
/// JudgeSkipped:   prompt template missing or judge disabled (treat as RouteYes by caller)
/// JudgeFailed:    HTTP failure / unparseable / timeout (treat as RouteYes — fail-open;
///                 we don't want to suppress good 35B responses on judge infrastructure errors)
type JudgeVerdict =
    | RouteYes
    | RouteNo
    | JudgeSkipped of reason: string
    | JudgeFailed  of err: string

/// Cli-only options bound from appsettings.json "Routing:Judge" section.
/// Endpoint defaults to "" — CompositionRoot resolves "" → Upstreams.Model122B
/// at registration time (open question #4 resolution: empty = derive from 122B).
[<CLIMutable>]
type JudgeOptions = {
    Endpoint        : string   // default "" → derive from Upstreams.Model122B
    PromptPath      : string   // default "prompts/judge-prompt.md"
    TimeoutSeconds  : int      // default 5 (1-token responses; not 30 like teacher)
    MaxCacheEntries : int      // default 10000
}

// ── Port + IJudgeStats interfaces ────────────────────────────────────────────

/// Phase 16 port — caller invokes judge after BorderlineClassifier returns Some _.
/// promptHash/responseHash form the LRU cache key; promptText/responseText are
/// inserted into the prompt template as {{QUESTION}}/{{RESPONSE}} on cache miss.
type IJudgeClient =
    abstract member VerdictAsync :
        promptHash    : string *
        responseHash  : string *
        promptText    : string *
        responseText  : string *
        ct            : CancellationToken
        -> Task<JudgeVerdict>

/// Phase 16 — judge cache + call counters exposed via /stats.
/// struct tuple avoids tiny allocations on the /stats hot path
/// (mirrors Phase 15 IQualityCheckStats.GetHits pattern).
type IJudgeStats =
    /// Returns (cacheHits, cacheMisses, callCount). callCount counts upstream
    /// HTTP calls to the named "judge" client; cacheHits + cacheMisses + skips
    /// reach VerdictAsync. callCount = cacheMisses minus skips (no template, etc.).
    abstract member GetJudgeStats : unit -> struct (int64 * int64 * int64)

// ── LRU cache types ──────────────────────────────────────────────────────────

type private CacheEntry = {
    Verdict           : JudgeVerdict
    mutable AccessSeq : int64
}

// ── Parser ───────────────────────────────────────────────────────────────────

/// Mirrors TeacherLabeler.parseContent: NO wins on collision (safety bias).
/// Substring contains check (NOT exact match) handles trailing punctuation,
/// hallucinated suffixes, prose echoes.
let private parseContent (content: string) : JudgeVerdict =
    let hasYes = content.Contains("ROUTE_YES")
    let hasNo  = content.Contains("ROUTE_NO")
    match hasNo, hasYes with
    | true,  _    -> RouteNo
    | false, true -> RouteYes
    | false, false ->
        let snippet =
            let max = min 100 content.Length
            content.Substring(0, max)
        JudgeFailed (sprintf "unparseable judge response: %s" snippet)

// ── Response extraction ──────────────────────────────────────────────────────

/// Extract `choices[0].message.content` from an OpenAI chat-completions response.
/// Returns None on any structural mismatch — caller maps None to JudgeFailed.
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
                | true, c when c.ValueKind = JsonValueKind.String -> Some (c.GetString().Trim())
                | _ -> None
            | _ -> None
        | _ -> None
    with _ -> None

// ── JudgeClient ──────────────────────────────────────────────────────────────

/// Calls the named "judge" HttpClient (registered in CompositionRoot Plan 16-03)
/// with a 1-token prompt asking "is this response good?". Caches verdicts by
/// (promptHash, responseHash) so identical content pays the judge cost once.
///
/// CRITICAL (researcher Anti-Pattern 1): Uses IHttpClientFactory.CreateClient("judge")
/// — NOT IUpstreamClient / QueueDispatcher. Routing through QueueDispatcher would
/// consume the SemaphoreSlim(1) slot that protects real 122B inference traffic.
/// The "judge" named client has its own AddResilienceHandler (2 retries 200ms/400ms;
/// see Plan 16-03 CompositionRoot wiring).
type JudgeClient(httpFactory: IHttpClientFactory, options: JudgeOptions, logger: ILogger<JudgeClient>) =

    // Prompt template cache: None = not yet read; Some "" = file missing (cached miss).
    let mutable promptTemplate : string option = None
    let promptLock = obj ()

    // Counters — int64; read via Volatile.Read in GetJudgeStats.
    let mutable cacheHits   = 0L
    let mutable cacheMisses = 0L
    let mutable callCount   = 0L
    let mutable globalSeq   = 0L

    // LRU cache.
    let cache = ConcurrentDictionary<string * string, CacheEntry>()

    // Defensive option normalization (defaults applied here too — defence in depth
    // on top of CompositionRoot normalization in Plan 16-03).
    let promptPath =
        if String.IsNullOrWhiteSpace(options.PromptPath) then "prompts/judge-prompt.md"
        else options.PromptPath
    let maxCacheEntries =
        if options.MaxCacheEntries <= 0 then 10000 else options.MaxCacheEntries

    // ── Prompt template loader (lock-on-first-read; cached miss returns None) ──
    let getPromptTemplate () : string option =
        match promptTemplate with
        | Some "" -> None
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
                        promptTemplate <- Some ""
                        None)

    // ── Cache primitives ────────────────────────────────────────────────────────

    let tryGetCached (key: string * string) : JudgeVerdict option =
        match cache.TryGetValue(key) with
        | true, entry ->
            entry.AccessSeq <- Interlocked.Increment(&globalSeq)
            Interlocked.Increment(&cacheHits) |> ignore
            Some entry.Verdict
        | _ ->
            Interlocked.Increment(&cacheMisses) |> ignore
            None

    let setCached (key: string * string) (verdict: JudgeVerdict) =
        // TOCTOU note (researcher Pitfall 5): two concurrent threads may both
        // see Count >= maxCacheEntries and both evict. Acceptable: cache may
        // temporarily hold maxEntries+1 entries. Not a correctness issue.
        if cache.Count >= maxCacheEntries then
            try
                let minKv = cache |> Seq.minBy (fun kv -> kv.Value.AccessSeq)
                cache.TryRemove(minKv.Key) |> ignore
            with _ -> ()  // empty cache or concurrent eviction; ignore
        let entry = { Verdict = verdict; AccessSeq = Interlocked.Increment(&globalSeq) }
        cache.[key] <- entry

    // ── Request body construction ──────────────────────────────────────────────

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
        bodyDict.["max_tokens"]  <- 1   :> obj   // KEY: 1-token response
        bodyDict.["temperature"] <- 0.0 :> obj   // deterministic
        bodyDict.["stream"]      <- false :> obj
        let opts = JsonSerializerOptions()
        opts.Converters.Add(JsonFSharpConverter())
        JsonSerializer.Serialize(bodyDict, opts)

    // ── Single HTTP attempt (retry handled by named-client's AddResilienceHandler) ──

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
                        let snippet = if responseJson.Length > 200 then responseJson.Substring(0, 200) else responseJson
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

    // ── Public interfaces ───────────────────────────────────────────────────────

    interface IJudgeClient with
        member _.VerdictAsync(promptHash, responseHash, promptText, responseText, ct) =
            task {
                let key = (promptHash, responseHash)
                match tryGetCached key with
                | Some cached ->
                    return cached
                | None ->
                    match getPromptTemplate () with
                    | None ->
                        let v = JudgeSkipped "prompt template missing"
                        // Don't cache Skipped — operator may add the template at runtime.
                        return v
                    | Some template ->
                        Interlocked.Increment(&callCount) |> ignore
                        let body = buildBody template promptText responseText
                        let client = httpFactory.CreateClient("judge")
                        let! verdict = attemptOnce client body ct
                        // Cache RouteYes/RouteNo only — failures may be transient.
                        match verdict with
                        | RouteYes | RouteNo -> setCached key verdict
                        | JudgeFailed _ | JudgeSkipped _ -> ()
                        return verdict
            }

    interface IJudgeStats with
        member _.GetJudgeStats() =
            struct (
                Volatile.Read(&cacheHits),
                Volatile.Read(&cacheMisses),
                Volatile.Read(&callCount))
```

KEY POINTS:
- `JudgeFailed` is treated as fail-open by the caller (Plan 16-03) — judge infrastructure error must NOT suppress good 35B responses.
- `JudgeSkipped` is NOT cached (template might be added at runtime; never poison the cache with a "no template" verdict).
- `RouteYes` and `RouteNo` ARE cached (these are real verdicts about specific content; deterministic).
- `globalSeq` is a single int64 counter; `Interlocked.Increment(&globalSeq)` returns the new value and is thread-safe.
- `attemptOnce` does NO retry — the named "judge" HttpClient (registered by Plan 16-03) wraps it with `AddResilienceHandler` carrying 2 retries (researcher OQ #5).
- CTOR signature `(IHttpClientFactory, JudgeOptions, ILogger<JudgeClient>)` — exact mirror of TeacherLabeler — ready for DI registration in Plan 16-03.
  </action>
  <verify>
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — fails initially (file not in fsproj — Task 3 fixes); after Task 3, must build cleanly under `TreatWarningsAsErrors=true`
- `grep -c "ROUTE_NO\|ROUTE_YES\|JudgeVerdict\|JudgeSkipped\|JudgeFailed\|RouteYes\|RouteNo" src/SmartRouter.Cli/Adapters/JudgeClient.fs` — at least 12 occurrences
- `grep -c "ConcurrentDictionary\|Interlocked\|Volatile\.Read" src/SmartRouter.Cli/Adapters/JudgeClient.fs` — at least 4
- `grep "max_tokens.*1 " src/SmartRouter.Cli/Adapters/JudgeClient.fs` — exactly 1 line (1-token request body)
- `grep "CreateClient(\"judge\")" src/SmartRouter.Cli/Adapters/JudgeClient.fs` — exactly 1 occurrence (named-client lookup)
- `grep -c "open SmartRouter.Core" src/SmartRouter.Cli/Adapters/JudgeClient.fs` — must return 0 (Cli adapter; no Core dep)
  </verify>
  <done>
File exists, ~200-300 lines, defines all four types (JudgeVerdict, JudgeOptions, IJudgeClient, IJudgeStats) and the JudgeClient class. ROUTE_NO wins parser. 1-token request body. LRU cache with O(n) eviction. ARCH-01 invariant preserved (no Core changes).
  </done>
</task>

<task type="auto">
  <name>Task 3: Register JudgeClient.fs in fsproj compile order</name>
  <files>src/SmartRouter.Cli/SmartRouter.Cli.fsproj</files>
  <action>
Edit `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` to add a `<Compile Include="Adapters/JudgeClient.fs" />` entry.

PLACEMENT: must come AFTER `<Compile Include="Adapters/BorderlineClassifier.fs" />` (registered by Plan 16-01) and BEFORE `<Compile Include="Endpoints/ChatCompletions.fs" />` (line ~58 currently). JudgeClient does NOT depend on BorderlineClassifier (they're independent); ordering is for clarity only — both must precede ChatCompletions.fs.

Recommended position: immediately AFTER BorderlineClassifier.fs entry:

```xml
    <Compile Include="Adapters/QualityCheck.fs" />        <!-- Phase 14: ... -->
    <Compile Include="Adapters/BorderlineClassifier.fs" /> <!-- Phase 16: borderline detection (BCL only) -->
    <Compile Include="Adapters/JudgeClient.fs" />          <!-- Phase 16: judge port + LRU cache + IJudgeStats -->
```

PARALLEL-EXECUTION CAVEAT: Plan 16-01 also edits this fsproj to add `BorderlineClassifier.fs`. Both edits target the same file. The execute-plan workflow runs wave-1 plans in parallel — if 16-01 and 16-02 race on this file, the second writer's edit will conflict.

MITIGATION: Both edits insert NEW lines (no overlap on existing lines), AND both edits only ADD content (no deletes). The Edit tool reads the current file state before writing, so even if 16-01 commits first, 16-02's executor will read the post-16-01 state and add JudgeClient.fs as the second new line. Use the Edit tool (not Write) to preserve 16-01's edit when both run in parallel. If a conflict still happens (rare), the second-running plan's executor must re-read the file and re-apply.

Run `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` after the edit — must succeed cleanly.
  </action>
  <verify>
- `grep -n "Adapters/JudgeClient.fs" src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — exactly 1 line, between BorderlineClassifier.fs (Plan 16-01) and ChatCompletions.fs lines
- `grep -E "Adapters/(QualityCheck|BorderlineClassifier|JudgeClient)\.fs|Endpoints/ChatCompletions\.fs" src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — must list lines in order: QualityCheck → BorderlineClassifier → JudgeClient → ChatCompletions
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — exit 0; "Build succeeded"; 0 warnings, 0 errors
  </verify>
  <done>
fsproj contains JudgeClient.fs entry in correct compile-order position; full Cli project builds cleanly. Plan 16-01's BorderlineClassifier.fs entry preserved.
  </done>
</task>

</tasks>

<verification>
- `prompts/judge-prompt.md` exists with both placeholders + both ROUTE_YES/NO sentinels
- `src/SmartRouter.Cli/Adapters/JudgeClient.fs` exists, defines IJudgeClient + IJudgeStats + JudgeVerdict + JudgeOptions + JudgeClient class
- ROUTE_NO wins over ROUTE_YES on collision (safety bias)
- LRU cache uses ConcurrentDictionary + monotonic AccessSeq + O(n) min-scan eviction
- 1-token deterministic request body (max_tokens=1, temperature=0.0, stream=false)
- ARCH-01 preserved: no Core changes; only Cli adapter additions
- fsproj compile order: QualityCheck → BorderlineClassifier → JudgeClient → ChatCompletions
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` clean (0 warnings under TreatWarningsAsErrors)
- Test baseline preserved: `dotnet test --no-build` 102+16+0 (no behavioral change yet — IJudgeClient has no consumers until Plan 16-03)
</verification>

<success_criteria>
- All 3 artifacts created: prompts/judge-prompt.md, src/SmartRouter.Cli/Adapters/JudgeClient.fs, fsproj entry
- IJudgeClient port + IJudgeStats interface ready for Plan 16-03 to wire
- Cli project builds cleanly
- Test baseline preserved
- OQ #4 (Endpoint default empty), OQ #5 (2 retries — documented in module comment, implemented by 16-03), autonomous decision B (prompt content) all resolved with rationale
</success_criteria>

<output>
After completion, create `.planning/phases/16-122b-as-judge-for-borderline-cases/16-02-SUMMARY.md` capturing:
- Files created (line counts)
- fsproj edit position (line numbers; confirm 16-01's entry preserved)
- Verification results
- OQ #4, OQ #5, decision B resolutions
- Commit hashes for `feat(16-02): add prompts/judge-prompt.md template`, `feat(16-02): add JudgeClient.fs (IJudgeClient + IJudgeStats + LRU cache)`, `feat(16-02): register JudgeClient.fs in fsproj`
</output>
