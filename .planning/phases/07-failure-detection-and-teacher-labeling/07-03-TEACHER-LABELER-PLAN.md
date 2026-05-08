---
phase: 07-failure-detection-and-teacher-labeling
plan: 03
type: execute
wave: 2
depends_on: ["07-01"]
files_modified:
  - src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
autonomous: true

must_haves:
  truths:
    - "TeacherLabeler.LabelAsync uses a named HttpClient retrieved via IHttpClientFactory.CreateClient(\"teacher\") — never goes through QueueDispatcher / IUpstreamClient (would starve the 122B SemaphoreSlim from real inference traffic)"
    - "Teacher prompt template loaded from configurable path (TeacherLabelerOptions.PromptPath, default 'prompts/teacher-prompt.md'); cached in memory after first read; missing file logs warning and returns Skipped (not crash)"
    - "Response parser extracts ROUTE_35B / ROUTE_122B substrings from chat completion response (case-sensitive contains, trimmed); returns Labeled on match, Unparseable on no-match (single retry then Unparseable returned), Failed on HTTP error after retries exhausted"
    - "Daily cost cap (FAIL-03): persistent file-backed counter at datasets/teacher-cap-YYYY-MM-DD.json (UTC date); incremented BEFORE each HTTP call; cap-hit returns Skipped immediately; counter rotates lazily at UTC midnight; survives router restarts"
    - "30s timeout (FAIL-02) configured via the named HttpClient.Timeout — every retry attempt has its own 30s budget; total worst-case ~90s on 3 retries; the named HttpClient is registered with AddResilienceHandler in Plan 07-05 (this plan does NOT touch CompositionRoot)"
    - "dotnet build succeeds with 0 warnings; existing 50 tests still pass + 10 ignored"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/TeacherLabeler.fs"
      provides: "TeacherLabeler(httpFactory, options) implements ITeacherLabeler with prompt template loader, response parser, and persistent daily cost cap"
      contains: "ROUTE_35B"
  key_links:
    - from: "TeacherLabeler.LabelAsync"
      to: "IHttpClientFactory.CreateClient(\"teacher\")"
      via: "named HttpClient — bypasses QueueDispatcher"
      pattern: "CreateClient.*teacher"
    - from: "TeacherLabeler.LabelAsync"
      to: "datasets/teacher-cap-{UTC-date}.json"
      via: "persistent counter file with UTC date rotation"
      pattern: "teacher-cap-"
    - from: "TeacherLabeler response parsing"
      to: "ROUTE_35B / ROUTE_122B sentinels"
      via: "string Contains check"
      pattern: "ROUTE_35B|ROUTE_122B"
---

<objective>
Replace the 07-01 stub body of `TeacherLabeler.LabelAsync` with the real implementation: prompt template loader (configurable path with caching), HTTP call to a named `"teacher"` HttpClient (which Plan 07-05 registers with retry policy + 30s timeout), persistent daily cost cap counter, response parser for `ROUTE_35B` / `ROUTE_122B` sentinels. Returns the appropriate `LabelResult` DU case for every code path. Constructor signature unchanged from Plan 07-01 stub.

Purpose: FAIL-02 + FAIL-03. The labeler MUST NOT route through `QueueDispatcher` / `IUpstreamClient` — that path would starve real 122B inference traffic of its SemaphoreSlim(1) slot. Named-HttpClient bypass is the locked design decision (Pitfall 5).

Output:
- src/SmartRouter.Cli/Adapters/TeacherLabeler.fs (replaces 07-01 stub body; preserves constructor signature `(IHttpClientFactory, TeacherLabelerOptions)` and the `TeacherLabelerOptions` record from 07-01)
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/07-failure-detection-and-teacher-labeling/07-CONTEXT.md
@.planning/phases/07-failure-detection-and-teacher-labeling/07-RESEARCH.md
@src/SmartRouter.Core/RetrainingPorts.fs
@src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Implement TeacherLabeler.LabelAsync (prompt loader + cost cap + HTTP call + response parser)</name>
  <files>
    src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
  </files>
  <action>
Replace the entire contents of `src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` (preserving the module name and the `TeacherLabelerOptions` record + constructor signature from Plan 07-01) with the real implementation:

```fsharp
module SmartRouter.Cli.Adapters.TeacherLabeler

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Serilog
open SmartRouter.Core.RetrainingPorts

/// Cli-only options bound from appsettings.json "TeacherLabeler" section.
/// Defaults applied at registration time in CompositionRoot (Plan 07-05).
[<CLIMutable>]
type TeacherLabelerOptions =
    { Endpoint        : string   // default "http://127.0.0.1:8001"
      PromptPath      : string   // default "prompts/teacher-prompt.md"
      DailyCallCap    : int      // default 1000
      TimeoutSeconds  : int      // default 30
      DatasetsDir     : string } // default "datasets"

// ── Persistent daily cost cap counter ──────────────────────────────────────────

/// Persistent counter file shape — one file per UTC date.
/// File path: {DatasetsDir}/teacher-cap-YYYY-MM-DD.json
/// Lazy rotation: a new date check on every call; if changed, write a fresh file with count=0.
/// Schema (snake_case on disk, per CONTEXT.md): { "date": "YYYY-MM-DD", "count": 42, "max": 1000 }
[<CLIMutable>]
type private CapCounter =
    { Date  : string   // "yyyy-MM-dd" (UTC)
      Count : int
      Max   : int }    // configured cap snapshot — per CONTEXT.md schema

let private capJsonOpts =
    // PropertyNamingPolicy = SnakeCaseLower so PascalCase F# fields write as
    // {"date":"...","count":42,"max":1000} on disk (matches CONTEXT.md schema).
    let o = JsonSerializerOptions(
                WriteIndented        = false,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
    o

/// Read the counter for today (UTC). If the file is missing or stale, returns
/// a fresh counter at 0. Never throws — corrupt files reset to fresh state.
/// Caller passes `cap` so freshly-rotated counter files carry the configured max.
let private readCounter (datasetsDir: string) (todayUtc: string) (cap: int) : CapCounter =
    try
        let path = Path.Combine(datasetsDir, sprintf "teacher-cap-%s.json" todayUtc)
        if File.Exists(path) then
            let json = File.ReadAllText(path)
            let counter = JsonSerializer.Deserialize<CapCounter>(json, capJsonOpts)
            if counter.Date = todayUtc then counter
            else { Date = todayUtc; Count = 0; Max = cap }
        else
            { Date = todayUtc; Count = 0; Max = cap }
    with ex ->
        Log.Warning(ex, "TeacherLabeler: failed to read cap counter file; resetting to 0")
        { Date = todayUtc; Count = 0; Max = cap }

let private writeCounter (datasetsDir: string) (counter: CapCounter) : unit =
    try
        Directory.CreateDirectory(datasetsDir) |> ignore
        let path = Path.Combine(datasetsDir, sprintf "teacher-cap-%s.json" counter.Date)
        let json = JsonSerializer.Serialize(counter, capJsonOpts)
        File.WriteAllText(path, json)
    with ex ->
        Log.Warning(ex, "TeacherLabeler: failed to persist cap counter")

// ── Response parsing ──────────────────────────────────────────────────────────

/// Extract choices[0].message.content from an OpenAI chat-completions response body.
/// Returns None on any structural mismatch.
let private tryReadResponseContent (body: string) : string option =
    try
        use doc = JsonDocument.Parse(body)
        let choices = doc.RootElement.GetProperty("choices")
        if choices.ValueKind <> JsonValueKind.Array || choices.GetArrayLength() = 0 then None
        else
            let msg = choices.[0].GetProperty("message")
            let content = msg.GetProperty("content").GetString()
            Some (content.Trim())
    with _ -> None

/// Parse the trimmed content string into a label decision.
/// Substring contains check (NOT exact match) — handles trailing periods, prose hallucinations.
/// "ROUTE_35B" wins over "ROUTE_122B" only if 35B substring appears AND 122B does not (mutually
/// exclusive in the well-formed case; if both appear, prefer 122B because that's the higher-stakes
/// decision and the prompt explicitly says "Otherwise → ROUTE_35B").
let private parseContent (content: string) : LabelResult =
    let has35  = content.Contains("ROUTE_35B")
    let has122 = content.Contains("ROUTE_122B")
    let excerpt =
        let max = min content.Length 200
        content.Substring(0, max)
    match has122, has35 with
    | true,  _    -> Labeled (Route122B, excerpt)
    | false, true -> Labeled (Route35B,  excerpt)
    | false, false -> Unparseable excerpt

// ── TeacherLabeler ────────────────────────────────────────────────────────────

/// Calls a teacher endpoint (default 122B) with the teacher prompt + a hard case's
/// prompt text. Parses ROUTE_35B / ROUTE_122B from the response. FAIL-02 + FAIL-03.
///
/// CRITICAL: Uses IHttpClientFactory.CreateClient("teacher") — NOT IUpstreamClient.
/// Routing through QueueDispatcher would starve real inference traffic of the 122B
/// SemaphoreSlim(1) slot. The "teacher" named client is registered in Plan 07-05's
/// CompositionRoot with AddResilienceHandler (3x retry on transient errors, 30s timeout).
type TeacherLabeler(httpFactory: IHttpClientFactory, options: TeacherLabelerOptions) =

    // Cache the prompt template after first successful read. None until loaded;
    // Some "" means we tried and the file was missing (cached miss → log once).
    let mutable promptTemplate : string option = None
    let promptLock = obj ()

    let datasetsDir =
        if String.IsNullOrWhiteSpace(options.DatasetsDir) then "datasets"
        else options.DatasetsDir

    let promptPath =
        if String.IsNullOrWhiteSpace(options.PromptPath) then "prompts/teacher-prompt.md"
        else options.PromptPath

    let dailyCap =
        if options.DailyCallCap <= 0 then 1000
        else options.DailyCallCap

    /// Returns the prompt template content, OR None if the file is missing.
    /// Thread-safe: lock around first read; subsequent reads return cached value.
    let getPromptTemplate () : string option =
        match promptTemplate with
        | Some "" -> None  // cached miss
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
                        Log.Warning(
                            "TeacherLabeler: prompt template not found at {Path}; will skip all calls",
                            promptPath)
                        promptTemplate <- Some ""  // mark as cached-missing
                        None)

    /// Build the chat-completions request body. Uses the teacher prompt as the
    /// system message (with {{PROMPT}} replaced inline) and the prompt text as
    /// the user message. max_tokens=10 keeps response cost predictable (the
    /// teacher prompt instructs the model to emit ROUTE_35B/ROUTE_122B only).
    let buildBody (template: string) (promptText: string) : string =
        // Replace {{PROMPT}} placeholder in the template; if absent, append at end.
        let systemContent =
            if template.Contains("{{PROMPT}}") then
                template.Replace("{{PROMPT}}", promptText)
            else
                template + "\n\nPrompt:\n" + promptText
        let bodyDict = Dictionary<string, obj>()
        bodyDict.["messages"] <-
            [|
                {| role = "system"; content = systemContent |} :> obj
                {| role = "user";   content = promptText     |} :> obj
            |]
        bodyDict.["max_tokens"]  <- 10 :> obj
        bodyDict.["temperature"] <- 0.0 :> obj
        bodyDict.["stream"]      <- false :> obj
        // Note: "model" field intentionally omitted — the teacher endpoint
        // (mlx_lm.server) accepts requests without an explicit model and uses
        // its loaded model. If a future operator points TeacherLabeler at a
        // different teacher (Claude API, OpenAI), they configure the model id
        // by pointing the named HttpClient at an endpoint that handles it.
        let opts = JsonSerializerOptions()
        opts.Converters.Add(JsonFSharpConverter())
        JsonSerializer.Serialize(bodyDict, opts)

    /// Single HTTP attempt — no retry. Caller (the resilience handler on the named
    /// HttpClient registered in CompositionRoot) handles retry. Returns LabelResult.
    let attemptOnce (client: HttpClient) (body: string) (ct: CancellationToken) : Task<LabelResult> =
        task {
            try
                use reqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                reqMsg.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                use! resp = client.SendAsync(reqMsg, ct)

                if not resp.IsSuccessStatusCode then
                    let! errBody = resp.Content.ReadAsStringAsync(ct)
                    let snippet = if errBody.Length > 200 then errBody.Substring(0, 200) else errBody
                    return Failed (sprintf "HTTP %d: %s" (int resp.StatusCode) snippet)
                else
                    let! responseJson = resp.Content.ReadAsStringAsync(ct)
                    match tryReadResponseContent responseJson with
                    | None ->
                        let snippet = if responseJson.Length > 200 then responseJson.Substring(0, 200) else responseJson
                        return Unparseable snippet
                    | Some content ->
                        return parseContent content
            with
            | :? HttpRequestException as ex ->
                return Failed (sprintf "HTTP request failed: %s" ex.Message)
            | :? TaskCanceledException as ex when ex.CancellationToken = ct ->
                return Failed "client cancelled"
            | :? TaskCanceledException ->
                return Failed "teacher endpoint timeout"
        }

    interface ITeacherLabeler with
        member _.LabelAsync(promptText: string, correlationId: string, ct: CancellationToken) : Task<LabelResult> =
            task {
                // 1. Cost cap pre-check (FAIL-03)
                let todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd")
                let counter = readCounter datasetsDir todayUtc dailyCap
                if counter.Count >= dailyCap then
                    Log.Warning(
                        "TeacherLabeler: daily cost cap hit ({Count}/{Cap} for {Date}); skipping correlation_id={Cid}",
                        counter.Count, dailyCap, todayUtc, correlationId)
                    return Skipped (sprintf "daily call cap %d reached for %s" dailyCap todayUtc)
                else

                // 2. Prompt template required for the call
                match getPromptTemplate () with
                | None ->
                    return Skipped (sprintf "prompt template missing at %s" promptPath)
                | Some template ->

                // 3. Build the request body
                let body = buildBody template promptText

                // 4. Increment counter BEFORE the call so cap accounting is robust to crashes
                let nextCounter = { Date = todayUtc; Count = counter.Count + 1; Max = dailyCap }
                writeCounter datasetsDir nextCounter

                // 5. Fire the HTTP call. The named HttpClient "teacher" (registered in
                //    CompositionRoot Plan 07-05) carries the 30s Timeout + AddResilienceHandler
                //    retry policy (3 attempts, exponential backoff, transient-only).
                let client = httpFactory.CreateClient("teacher")
                let! result = attemptOnce client body ct
                match result with
                | Labeled (label, _) ->
                    Log.Information(
                        "TeacherLabeler: labeled correlation_id={Cid} as {Label} (call {Count}/{Cap})",
                        correlationId, label, nextCounter.Count, dailyCap)
                | Unparseable raw ->
                    Log.Warning(
                        "TeacherLabeler: unparseable response for correlation_id={Cid}: {Raw}",
                        correlationId, raw)
                | Failed err ->
                    Log.Warning(
                        "TeacherLabeler: HTTP failure for correlation_id={Cid}: {Err}",
                        correlationId, err)
                | Skipped _ -> ()  // unreachable here — Skipped only returned by cap check above
                return result
            }
```

**Key design notes:**
- Constructor signature `(httpFactory: IHttpClientFactory, options: TeacherLabelerOptions)` matches Plan 07-01 stub — Plan 07-05 DI registration depends on this shape.
- The `TeacherLabelerOptions` record stays in this module (preserved from 07-01); CompositionRoot binds it from `appsettings.json` (Plan 07-05).
- The named HttpClient `"teacher"` is registered in Plan 07-05 CompositionRoot. This file just calls `CreateClient("teacher")` — never builds clients itself (PITFALL-15: don't cache HttpClient instances; let IHttpClientFactory manage them).
- 3x retry policy is configured on the named HttpClient via `AddResilienceHandler` in Plan 07-05 — this file makes one HTTP call per `LabelAsync`, the resilience handler retries transparently.
- 30s timeout = `client.Timeout` set on the named HttpClient in Plan 07-05; honored by the underlying socket.
- Counter file path uses `Path.Combine` — never hand-concat strings.
- Counter increment happens BEFORE the HTTP call to be robust to crashes (slight over-count is acceptable; we never want under-count to bypass the cap).
- Prompt-template caching uses double-checked locking: fast path on second-and-later calls returns the cached `Some s` without locking.

**No additional changes outside this file** — Cli.fsproj already has TeacherLabeler.fs registered (Plan 07-01). The named HttpClient + AddResilienceHandler land in Plan 07-05's CompositionRoot. No tests (Plan 07-06).
  </action>
  <verify>
- `dotnet build SmartRouter.slnx -nologo --tl:off` succeeds with 0 warnings.
- `grep -n "NotImplementedException" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` returns NO hits (stub fully replaced).
- `grep -n "CreateClient.*teacher" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` returns 1 hit (named HttpClient lookup).
- `grep -n "IUpstreamClient\|QueueDispatcher" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` returns NO hits (must NOT route through queue — Pitfall 5).
- `grep -n "ROUTE_35B\|ROUTE_122B" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` returns at least 2 hits (both sentinels referenced in parser).
- `grep -n "teacher-cap-" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` returns at least 1 hit (counter file naming).
- Existing 50 tests still pass + 10 ignored (no DI changes; tests come in Plan 07-06).
  </verify>
  <done>
TeacherLabeler real implementation ships: prompt template loader (with caching + missing-file graceful skip), persistent daily cost cap with UTC rotation, named HttpClient HTTP call (no QueueDispatcher path), response parser for both ROUTE sentinels, all four LabelResult cases produced. Constructor signature unchanged from 07-01 stub.
  </done>
</task>

</tasks>

<verification>
**Plan-level verification:**

1. **Build is clean:**
   ```bash
   dotnet build SmartRouter.slnx -nologo --tl:off
   ```
   Expected: 0 errors, 0 warnings.

2. **Stub fully replaced:**
   ```bash
   grep -n "NotImplementedException" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
   ```
   Expected: NO output.

3. **No QueueDispatcher path (Pitfall 5):**
   ```bash
   grep -nE "(IUpstreamClient|QueueDispatcher)" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
   ```
   Expected: NO output.

4. **Named HttpClient used:**
   ```bash
   grep -n "CreateClient" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
   ```
   Expected: 1 hit, with the literal string "teacher".

5. **Cost cap counter file logic:**
   ```bash
   grep -nE "(teacher-cap-|DailyCallCap)" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
   ```
   Expected: at least 2 hits (file path + cap field reference).

6. **Existing tests still pass:**
   ```bash
   dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj -nologo --tl:off
   ```
   Expected: 50 passed + 10 ignored.
</verification>

<success_criteria>
- TeacherLabeler real implementation ships; constructor signature unchanged
- All four LabelResult cases producible (Labeled / Unparseable / Skipped / Failed)
- ROUTE_35B and ROUTE_122B parsing logic present
- Persistent daily cost cap with UTC rotation
- Named HttpClient "teacher" used; QueueDispatcher path NOT used
- 0 build warnings (TreatWarningsAsErrors=true)
- All 50 existing tests still pass + 10 ignored
</success_criteria>

<output>
After completion, create `.planning/phases/07-failure-detection-and-teacher-labeling/07-03-SUMMARY.md` listing the file modified, any deviations from this plan, and the test count delta (expected: 50 → 50).
</output>
