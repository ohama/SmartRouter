---
phase: 16-122b-as-judge-for-borderline-cases
plan: 03
type: execute
wave: 2
depends_on: ["16-01", "16-02"]
files_modified:
  - src/SmartRouter.Cli/Adapters/TraceLogger.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/appsettings.json
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  - src/SmartRouter.Cli/Endpoints/Stats.fs
autonomous: true

must_haves:
  truths:
    - "TraceRecord has 16 fields after this plan: original 13 (Phase 14) + bad_reason (Phase 15) + judge_called + judge_verdict + judge_latency_ms (Phase 16)"
    - "TraceRecord.schema_version stays at int literal 1 (additive change only — JDG-05 compliance)"
    - "appsettings.json has new `Routing.Judge` block with 4 keys: Endpoint (\"\"), PromptPath (\"prompts/judge-prompt.md\"), TimeoutSeconds (5), MaxCacheEntries (10000)"
    - "appsettings.json `Routing.Judge.Enabled` key is `false` by default (autonomous decision A: opt-in — judge adds 122B HTTP latency on every borderline case)"
    - "CompositionRoot configureRequestPipeline registers IJudgeClient AND IJudgeStats only when `Routing.Judge.Enabled` is true; named \"judge\" HttpClient registered with 2 retries 200ms/400ms backoff (researcher OQ #5)"
    - "CompositionRoot configureWithoutMl registers IJudgeStats NoOp (returns struct (0L,0L,0L)) so /stats endpoint resolves cleanly in offline mode; IJudgeClient NOT registered in offline mode (caller treats null as 'judge disabled — borderline=good')"
    - "ChatCompletions.fs non-streaming branch resolves IJudgeClient via GetService<IJudgeClient>() (null-safe — null when judge disabled OR offline mode); IJudgeStats via GetService<IJudgeStats>() with NoOp default"
    - "When initialVerdict = Good AND judgeClient is non-null AND classifyBorderline returns Some _: judge.VerdictAsync called → if RouteNo AND 122B reachable → retry on 122B with FallbackTo122B reason; if RouteYes / JudgeSkipped / JudgeFailed → forward 35B response unchanged (fail-open)"
    - "When initialVerdict = Good AND classifyBorderline returns None (clearly good): judge is NOT called (judgeCallCount stays 0); fast path preserved"
    - "Streaming branch is NOT modified — Phase 14 INTENTIONALLY SKIPPED comment preserved (autonomous decision C: streaming inherits no-judge behavior)"
    - "fallback_kind = Some \"quality\" when judge fires AND returns RouteNo AND 122B retry succeeds (researcher OQ #6 resolution: keep \"quality\"; new judge_* fields disambiguate)"
    - "computePromptHash hoisted to a single call site BEFORE the analyzeResponse block (researcher OQ #3 resolution); both judge cache key and trace block share the result"
    - "Stats.fs StatsWire gains 3 new fields: judge_cache_hits (int64), judge_cache_misses (int64), judge_call_count (int64); resolved via GetService<IJudgeStats>() (null-safe → 0L,0L,0L when not registered)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/TraceLogger.fs"
      provides: "TraceRecord extended with 3 Phase 16 fields"
      contains: "judge_called"
      contains2: "judge_verdict"
      contains3: "judge_latency_ms"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "Routing.Judge configuration block (Enabled=false by default)"
      contains: "\"Judge\""
      contains2: "\"Enabled\": false"
      contains3: "\"PromptPath\": \"prompts/judge-prompt.md\""
      contains4: "\"MaxCacheEntries\": 10000"
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "Conditional IJudgeClient + IJudgeStats DI registration in configureRequestPipeline; IJudgeStats NoOp in configureWithoutMl; named 'judge' HttpClient with 2-retry resilience"
      contains: "AddHttpClient(\"judge\")"
      contains2: "AddSingleton<IJudgeClient>"
      contains3: "AddSingleton<IJudgeStats>"
    - path: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      provides: "Borderline → judge → fallback cascade wired into non-streaming branch; trace block emits 3 new judge fields"
      contains: "GetService<IJudgeClient>"
      contains2: "GetService<IJudgeStats>"
      contains3: "classifyBorderline"
      contains4: "judge_called"
      contains5: "judge_verdict"
      contains6: "judge_latency_ms"
    - path: "src/SmartRouter.Cli/Endpoints/Stats.fs"
      provides: "StatsWire gains 3 judge_* fields; mapEndpoints resolves IJudgeStats null-safely"
      contains: "judge_cache_hits"
      contains2: "judge_cache_misses"
      contains3: "judge_call_count"
      contains4: "GetService<IJudgeStats>"
  key_links:
    - from: "ChatCompletions.fs non-streaming Good arm"
      to: "BorderlineClassifier.classifyBorderline"
      via: "open SmartRouter.Cli.Adapters.BorderlineClassifier; called when initialVerdict = Good and judgeClient is non-null"
      pattern: "classifyBorderline\\s+qualityFallbackOpts"
    - from: "ChatCompletions.fs borderline arm"
      to: "IJudgeClient.VerdictAsync"
      via: "judgeClient.VerdictAsync(promptHash, responseHash, promptText, initialContent, ctx.RequestAborted)"
      pattern: "judgeClient\\.VerdictAsync"
    - from: "ChatCompletions.fs RouteNo arm"
      to: "upstream.CompleteAsync (122B retry)"
      via: "retryDecision with Target=Qwen122B, Reason=FallbackTo122B; mirrors existing Bad-verdict path"
      pattern: "Target\\s*=\\s*Qwen122B.*Reason\\s*=\\s*FallbackTo122B"
    - from: "Stats.fs mapEndpoints"
      to: "IJudgeStats.GetJudgeStats"
      via: "GetService<IJudgeStats>() null-safe; null → struct (0L,0L,0L)"
      pattern: "GetService<IJudgeStats>"
    - from: "TraceRecord.judge_called/judge_verdict/judge_latency_ms"
      to: "ChatCompletions trace block"
      via: "captured from in-task judgeCalledFlag/judgeVerdictStr/judgeLatencyMs values"
      pattern: "judge_called\\s*=.*judge_verdict\\s*=.*judge_latency_ms\\s*="
---

<objective>
Wire the new BorderlineClassifier (Plan 16-01) and JudgeClient (Plan 16-02) into the live request pipeline. This plan extends TraceRecord schema (additive, schema_version=1 unchanged), adds the `Routing.Judge` config block to appsettings.json, registers DI in both composition paths (production + offline), modifies ChatCompletions.fs non-streaming branch to insert the borderline → judge → fallback cascade between Phase 15's analyzeResponse and the existing 122B retry path, and extends /stats with 3 new judge counters.

Purpose: Phase 16's behavioral change happens here. After this plan, when `Routing.Judge.Enabled = true`:
1. 35B "Good" responses on the borderline (entropy or length band edge) get a 1-token verification call to 122B.
2. If judge returns RouteNo, the response is replaced via the existing FallbackTo122B mechanism.
3. If judge returns RouteYes / cache hit / failure, the 35B response is forwarded unchanged (fail-open).
4. Trace JSONL gains 3 new fields exposing judge activity (judge_called / judge_verdict / judge_latency_ms).
5. /stats exposes cache hit/miss/call counters.

When `Routing.Judge.Enabled = false` (the default — autonomous decision A):
- IJudgeClient is NOT registered.
- ChatCompletions resolves null and skips the borderline check entirely.
- Existing Phase 15 behavior is bit-stable preserved.
- /stats judge_* counters report 0L (NoOp IJudgeStats fallback).

Output: All Phase 16 source code is wired and active. Tests + docs follow in Plan 16-04.
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
@.planning/phases/16-122b-as-judge-for-borderline-cases/16-01-BORDERLINE-CLASSIFIER-PLAN.md
@.planning/phases/16-122b-as-judge-for-borderline-cases/16-02-JUDGE-ADAPTER-PLAN.md
@src/SmartRouter.Cli/Adapters/QualityCheck.fs
@src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
@src/SmartRouter.Cli/Adapters/TraceLogger.fs
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
@src/SmartRouter.Cli/Endpoints/Stats.fs
@src/SmartRouter.Cli/appsettings.json
</context>

<rationale>
This plan resolves researcher OQ #3 and #6, autonomous decisions A and C, and incorporates OQ #5 (judge retry count) at the wiring site:

**OQ #3 — `computePromptHash` hoisting:** HOIST. In current ChatCompletions.fs the prompt hash is computed inside the trace block at line 495 (only when traceLogger is non-null). Phase 16 needs it BEFORE the borderline check (for the cache key). Hoist `let promptHash = computePromptHash req.Messages` to immediately after `let initialDecision = decision` (line ~409), so both the judge cache key (when traceLogger is null) and the trace block (when non-null) share the value. Removes one duplicate hashing pass when both judge AND trace are enabled. Cost: zero (the cost was always paid when trace was enabled; now it's always paid but at a single site).

**OQ #6 — `fallback_kind` value when judge fires:** KEEP `"quality"`. The `judge_called=true` + `judge_verdict="no"` fields disambiguate without changing existing values. New value `"quality_judge"` would break downstream tooling that already does simple `fallback_kind == "quality"` matching (Hermes Agent operator scripts, jq filters in README §9.10). Backward-compat win.

**Autonomous decision A — `Routing.Judge.Enabled` default:** `false` (OPT-IN). Why: judge adds a 122B HTTP call latency on every borderline 35B response. Even at 1-token + 5s timeout, that's measurable user-facing latency. Phase 15's silent-enable was for purely-local heuristics (no extra cost). Judge is a network call. Operators must explicitly opt in after evaluating their borderline rate (visible via the new `quality_check_hits_*` /stats counters from Phase 15). Document in README §7 "OPT-IN" badge in Plan 16-04.

**Autonomous decision C — Streaming branch:** UNCHANGED. Phase 14 left a comment `// Phase 14: streaming branch INTENTIONALLY SKIPPED — chunks already shipped to client; retract impossible`. Phase 16 inherits this — streaming requests can never receive judge verification (fundamentally incompatible with SSE pass-through semantics). The comment must remain; no judge call inserted in the streaming branch.

**OQ #5 (carried from 16-02) — Judge retry count:** 2 retries at 200ms/400ms exponential. Implemented HERE in CompositionRoot's `AddResilienceHandler` for the named "judge" HttpClient. Mirrors teacher's retry policy structure but with shorter backoff (judge is hot path; teacher is offline labeler).

**Autonomous decision D — Cache key fingerprint:** `(prompt_hash, response_hash)` tuple. `prompt_hash` = `computePromptHash req.Messages` (existing helper, SHA-256 of concatenated message contents). `response_hash` = SHA-256 hex of `extractAssistantText initialBody` (NOT the envelope — researcher Pitfall §4 + Anti-Pattern 4). Helper for response_hash lives inside the wired call site (computed inline in ChatCompletions.fs).

**ARCH-01 invariant:** All edits in `SmartRouter.Cli`. No `SmartRouter.Core` changes.

**JDG-05 schema_version=1:** TraceRecord extension is purely additive (3 new fields after `bad_reason`). schema_version stays at 1 — readers ignoring unknown fields remain forward-compatible. No version bump.

**Compile-order dependency:** ChatCompletions.fs (compile pos 58) imports BorderlineClassifier (Plan 16-01 added at pos ~24) and JudgeClient (Plan 16-02 added at pos ~25). Both precede ChatCompletions in the fsproj. Compile-order safe.
</rationale>

<tasks>

<task type="auto">
  <name>Task 1: Extend TraceRecord with 3 Phase 16 fields + appsettings.json Routing.Judge block</name>
  <files>src/SmartRouter.Cli/Adapters/TraceLogger.fs, src/SmartRouter.Cli/appsettings.json</files>
  <action>
**EDIT 1: src/SmartRouter.Cli/Adapters/TraceLogger.fs (TraceRecord extension)**

Open the file and locate the `TraceRecord` definition (currently 13 fields ending with `bad_reason : string option` at line ~44). Add 3 new fields AFTER `bad_reason`. Update the doc comment from "13 fields" to "16 fields" and add Phase 16 note.

```fsharp
/// Phase 14 — trace JSONL row for end-to-end request tracing.
/// Prompt UID = first 12 hex of prompt_hash (Phase 5 LOG-01).
/// 16 fields total:
///   - 12 from Phase 14 (schema_version..timestamp)
///   - bad_reason (Phase 15, additive)
///   - judge_called / judge_verdict / judge_latency_ms (Phase 16, additive)
/// schema_version = 1 unchanged — additive-only extension per JDG-05.
[<CLIMutable>]
type TraceRecord = {
    [<JsonPropertyName("schema_version")>]
    schema_version              : int
    // ... existing fields unchanged ...
    [<JsonPropertyName("bad_reason")>]
    bad_reason                  : string option
    [<JsonPropertyName("judge_called")>]
    judge_called                : bool          // NEW Phase 16 — true when judge was invoked
    [<JsonPropertyName("judge_verdict")>]
    judge_verdict               : string option // NEW Phase 16 — "yes" | "no" | null
    [<JsonPropertyName("judge_latency_ms")>]
    judge_latency_ms            : float option  // NEW Phase 16 — null when judge not called
}
```

KEY POINTS:
- `judge_called : bool` — non-optional; defaults to false on every record (cheap to assign).
- `judge_verdict : string option` — None when judge_called=false; Some "yes" / Some "no" otherwise. NOT a DU — string keeps JSONL operator-friendly.
- `judge_latency_ms : float option` — None when judge_called=false; Some n when called. Operator computes p50/p95 in jq.
- JSON property order = field declaration order (System.Text.Json preserves it). The 3 new fields land at the END of each JSONL row (after `bad_reason`). Backward-compat: old log readers ignore unknown fields.

**EDIT 2: src/SmartRouter.Cli/appsettings.json (Routing.Judge block)**

Locate the `"Routing"` object. After the closing `}` of `"QualityFallback"` (currently line ~52), add a new `"Judge"` block. Keep field ordering: Enabled first (most operationally relevant), then Endpoint, PromptPath, TimeoutSeconds, MaxCacheEntries.

```jsonc
    "QualityFallback": {
      "Enabled": true,
      "MinResponseLength": 30,
      "BadKeywords": [ "TODO", "I think" ],
      "BadFinishReasons": [ "length", "content_filter" ],
      "EntropyThreshold": 2.5
    },
    "Judge": {
      "Enabled":         false,
      "Endpoint":        "",
      "PromptPath":      "prompts/judge-prompt.md",
      "TimeoutSeconds":  5,
      "MaxCacheEntries": 10000
    }
```

KEY POINTS:
- `Enabled = false` (autonomous decision A — opt-in).
- `Endpoint = ""` (researcher OQ #4 — empty derives from `Upstreams.Model122B`).
- `TimeoutSeconds = 5` (researcher Pitfall 3 — short for 1-token; not 30 like teacher).
- `MaxCacheEntries = 10000` (researcher Pattern 3 default).
- DO NOT add field `"Enabled"` to JudgeOptions in JudgeClient.fs — Enabled is read at the CompositionRoot level (Task 2) to gate registration entirely. JudgeOptions itself only carries the 4 operational fields.
  </action>
  <verify>
- `grep -c "judge_called\|judge_verdict\|judge_latency_ms" src/SmartRouter.Cli/Adapters/TraceLogger.fs` — at least 6 (3 JsonPropertyName attributes + 3 field declarations)
- `grep "schema_version              : int" src/SmartRouter.Cli/Adapters/TraceLogger.fs` — exactly 1 (schema_version field unchanged)
- `grep -c "schema_version *<- *1\|schema_version *= *1" src/SmartRouter.Cli/Adapters/TraceLogger.fs` — confirm 1 stays as integer literal (no version bump)
- `grep -A 6 '"Judge"' src/SmartRouter.Cli/appsettings.json` — shows Enabled=false, Endpoint="", PromptPath, TimeoutSeconds, MaxCacheEntries
- `python3 -c "import json; json.load(open('src/SmartRouter.Cli/appsettings.json'))"` — JSON parses cleanly (no trailing comma errors)
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tee /tmp/build16.log | grep -E "FS\d+|error"` — empty (TraceRecord change DOES break ChatCompletions.fs trace block; but the call site update is in Task 3; this task by itself will produce a build error — accepted; complete build verified after Task 3)
  </verify>
  <done>
TraceRecord has 16 fields with schema_version=1 unchanged. appsettings.json has new Routing.Judge block with Enabled=false default. Build will fail until Task 3 updates the trace call site — that is expected at this checkpoint; the next task closes the loop.
  </done>
</task>

<task type="auto">
  <name>Task 2: Register named "judge" HttpClient + IJudgeClient + IJudgeStats DI in both composition paths</name>
  <files>src/SmartRouter.Cli/CompositionRoot.fs</files>
  <action>
Open `src/SmartRouter.Cli/CompositionRoot.fs`. Make 4 edits:

**EDIT 1: Add `open SmartRouter.Cli.Adapters.JudgeClient` to the open block** (around line 50 where existing `open SmartRouter.Cli.Adapters.*` lines live; place AFTER `open SmartRouter.Cli.Adapters.QualityCheck`).

**EDIT 2: configureRequestPipeline — conditional IJudgeClient + IJudgeStats registration**

Place this block immediately AFTER the existing TraceLogger registration block (currently around lines 510-524, ending with the `if traceEnabled then ... |> ignore` block) and BEFORE the next phase comment line. Mirror the `traceEnabled` config-gated pattern.

```fsharp
    // ── Phase 16: Judge (optional; Routing.Judge.Enabled gates entire feature) ──
    // OPT-IN by default (Routing.Judge.Enabled=false in appsettings.json) — judge
    // adds 122B HTTP latency on every borderline case (autonomous decision A).
    // When enabled: triple-reg pattern for JudgeClient (concrete + IJudgeClient
    // alias + IJudgeStats alias). Single instance carries both interfaces so
    // the LRU cache and counters are shared across all callers.
    //
    // Endpoint normalization (researcher OQ #4): empty Endpoint string =
    // derive from Upstreams.Model122B at registration time.
    let judgeEnabled =
        let raw = config.["Routing:Judge:Enabled"]
        not (isNull raw) && raw.Equals("true", StringComparison.OrdinalIgnoreCase)
    if judgeEnabled then
        services.Configure<JudgeOptions>(config.GetSection("Routing:Judge")) |> ignore

        // Resolve effective endpoint: empty Endpoint → reuse Upstreams.Model122B.
        let upstreams = config.GetSection("Upstreams").Get<UpstreamOptions>()
        let judgeRaw  = config.GetSection("Routing:Judge").Get<JudgeOptions>()
        let effectiveEndpoint =
            if not (isNull (box judgeRaw)) && not (String.IsNullOrWhiteSpace(judgeRaw.Endpoint))
            then judgeRaw.Endpoint
            else upstreams.Model122B
        let effectiveTimeoutSec =
            if isNull (box judgeRaw) || judgeRaw.TimeoutSeconds <= 0 then 5
            else judgeRaw.TimeoutSeconds

        // Named "judge" HttpClient — separate from "teacher" and from the queue-gated
        // upstream clients. Mirrors teacher-pipeline retry shape but with shorter
        // backoff (researcher OQ #5: 2 retries at 200ms/400ms — judge is on the hot
        // path; teacher's 1s/2s/4s is too slow).
        services.AddHttpClient("judge", fun (c: System.Net.Http.HttpClient) ->
            c.BaseAddress <- Uri(effectiveEndpoint)
            c.Timeout     <- TimeSpan.FromSeconds(float effectiveTimeoutSec))
            .AddResilienceHandler("judge-pipeline", fun (builder: Polly.ResiliencePipelineBuilder<System.Net.Http.HttpResponseMessage>) ->
                let retryOpts = HttpRetryStrategyOptions()
                retryOpts.MaxRetryAttempts <- 2
                retryOpts.BackoffType      <- DelayBackoffType.Exponential
                retryOpts.Delay            <- TimeSpan.FromMilliseconds(200.0)
                retryOpts.ShouldHandle     <-
                    Func<RetryPredicateArguments<System.Net.Http.HttpResponseMessage>, System.Threading.Tasks.ValueTask<bool>>(
                        fun args ->
                            let retry =
                                match args.Outcome.Exception with
                                | :? System.Net.Http.HttpRequestException -> true
                                | :? System.Threading.Tasks.TaskCanceledException -> true
                                | null ->
                                    let resp = args.Outcome.Result
                                    not (isNull resp) && int resp.StatusCode >= 500
                                | _ -> false
                            System.Threading.Tasks.ValueTask.FromResult(retry))
                builder.AddRetry(retryOpts) |> ignore
                builder.AddTimeout(TimeSpan.FromSeconds(float effectiveTimeoutSec)) |> ignore)
            |> ignore

        // Triple-reg: concrete JudgeClient + IJudgeClient alias + IJudgeStats alias.
        // Single instance — LRU cache must be shared across requests.
        services.AddSingleton<JudgeClient>(fun sp ->
            let opts = sp.GetRequiredService<IOptions<JudgeOptions>>().Value
            // Defensive defaults if the section parsed to null fields.
            let normalized =
                { Endpoint        = effectiveEndpoint
                  PromptPath      = if String.IsNullOrWhiteSpace(opts.PromptPath)  then "prompts/judge-prompt.md" else opts.PromptPath
                  TimeoutSeconds  = effectiveTimeoutSec
                  MaxCacheEntries = if opts.MaxCacheEntries <= 0 then 10000 else opts.MaxCacheEntries }
            JudgeClient(
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                normalized,
                sp.GetRequiredService<ILogger<JudgeClient>>()))
            |> ignore

        services.AddSingleton<IJudgeClient>(fun sp ->
            sp.GetRequiredService<JudgeClient>() :> IJudgeClient)
            |> ignore

        services.AddSingleton<IJudgeStats>(fun sp ->
            sp.GetRequiredService<JudgeClient>() :> IJudgeStats)
            |> ignore
    else
        // Judge disabled — register IJudgeStats NoOp so /stats endpoint resolves
        // (judge_cache_hits/_misses/_call_count = 0L). IJudgeClient deliberately
        // NOT registered: ChatCompletions resolves null and skips borderline check.
        services.AddSingleton<IJudgeStats>(fun _ ->
            { new IJudgeStats with
                member _.GetJudgeStats() = struct (0L, 0L, 0L) })
            |> ignore
```

NOTES:
- `UpstreamOptions` is the existing CLIMutable record bound from the `Upstreams` section (search the file for "Model122B" — the type already exists).
- The `judgeEnabled` block runs only when the operator opts in; default behavior (Enabled=false) registers only the NoOp IJudgeStats.
- IJudgeStats NoOp is registered BOTH in the disabled-branch AND when judgeEnabled is true (latter via the JudgeClient triple-reg). last-registration-wins ensures the real IJudgeStats overrides when judgeEnabled.
- WAIT — actually, using `if/else` means only ONE branch runs, so there's no override conflict. The `else` branch registers NoOp; the `then` branch registers the real one. Both paths produce a resolvable IJudgeStats.

**EDIT 3: configureWithoutMl — IJudgeStats NoOp registration**

Locate the existing Phase 15 IQualityCheckStats NoOp block in configureWithoutMl (around line 903-914 currently). Immediately AFTER it, add:

```fsharp
    // Phase 16 — IJudgeStats NoOp for offline path. /stats may be invoked
    // even in offline mode (e.g. `--retrain` doesn't run /stats but DI graph
    // integrity requires this resolvable). IJudgeClient deliberately NOT
    // registered offline — borderline check is skipped (Phase 15 behavior).
    services.AddSingleton<IJudgeStats>(fun _ ->
        { new IJudgeStats with
            member _.GetJudgeStats() = struct (0L, 0L, 0L) })
        |> ignore
```

**EDIT 4: Add `open SmartRouter.Cli.Adapters.JudgeClient` to the open block at the top** (already mentioned in EDIT 1, but emphasizing — the type references `JudgeOptions`, `JudgeClient`, `IJudgeClient`, `IJudgeStats` all live in this module).

Run `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` after these edits. The build will STILL fail because Task 3 hasn't updated ChatCompletions.fs / Stats.fs yet — accepted at this checkpoint. The next task closes the loop.
  </action>
  <verify>
- `grep -n "open SmartRouter.Cli.Adapters.JudgeClient" src/SmartRouter.Cli/CompositionRoot.fs` — exactly 1 line
- `grep -n "AddHttpClient(\"judge\")" src/SmartRouter.Cli/CompositionRoot.fs` — exactly 1 line (only configureRequestPipeline registers it; configureWithoutMl does not)
- `grep -c "AddSingleton<IJudgeStats>" src/SmartRouter.Cli/CompositionRoot.fs` — exactly 3 (configureRequestPipeline judgeEnabled-true triple-reg, configureRequestPipeline judgeEnabled-false NoOp, configureWithoutMl NoOp)
- `grep -c "AddSingleton<IJudgeClient>" src/SmartRouter.Cli/CompositionRoot.fs` — exactly 1 (only when judgeEnabled=true in configureRequestPipeline)
- `grep "MaxRetryAttempts.*2\b\|MaxRetryAttempts <- 2" src/SmartRouter.Cli/CompositionRoot.fs` — exactly 1 line in judge-pipeline (mirroring researcher OQ #5)
- `grep "TimeSpan.FromMilliseconds(200.0)" src/SmartRouter.Cli/CompositionRoot.fs` — exactly 1 occurrence (judge-pipeline backoff)
  </verify>
  <done>
CompositionRoot wires the named judge HttpClient + JudgeClient + IJudgeClient + IJudgeStats triple-reg in configureRequestPipeline (gated on Routing.Judge.Enabled). configureWithoutMl gets only the IJudgeStats NoOp. 2-retry resilience handler with 200ms/400ms backoff installed (OQ #5).
  </done>
</task>

<task type="auto">
  <name>Task 3: Wire borderline → judge → fallback cascade in ChatCompletions.fs + extend Stats.fs StatsWire</name>
  <files>src/SmartRouter.Cli/Endpoints/ChatCompletions.fs, src/SmartRouter.Cli/Endpoints/Stats.fs</files>
  <action>
**EDIT 1: src/SmartRouter.Cli/Endpoints/ChatCompletions.fs**

Three sub-edits:

**1a — Imports.** At the top of the file, after the existing `open SmartRouter.Cli.Adapters.QualityCheck` line (~24), add:

```fsharp
open SmartRouter.Cli.Adapters.BorderlineClassifier   // Phase 16: classifyBorderline
open SmartRouter.Cli.Adapters.JudgeClient            // Phase 16: IJudgeClient + JudgeVerdict
```

**1b — Add a private SHA-256 helper near the existing `truncate` helper (around line 32).** Mirrors `computePromptHash` in DecisionLogger.fs but is local so we don't have to broaden DecisionLogger's surface.

```fsharp
/// Phase 16 — SHA-256 hex of arbitrary string for judge cache key.
/// Uses the assistant content (NOT the JSON envelope) so identical responses
/// across requests with different created/id timestamps share cache entries.
/// See 16-RESEARCH.md §"Pattern 4" — Cache key: content hash, not envelope hash.
let private computeContentHash (content: string) : string =
    let bytes = System.Text.Encoding.UTF8.GetBytes(content)
    use sha = System.Security.Cryptography.SHA256.Create()
    sha.ComputeHash(bytes)
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""
```

**1c — Hoist `computePromptHash` AND wire the borderline → judge cascade in the non-streaming branch.**

Find the existing block at ~line 405-490 in ChatCompletions.fs that resolves QualityFallbackOptions / traceLogger / qualityCheckStats and runs analyzeResponse. The block currently looks like:

```fsharp
let qualityFallbackOpts = ctx.RequestServices.GetRequiredService<QualityFallbackOptions>()
let traceLogger = ctx.RequestServices.GetService<ITraceLogger>()
let qualityCheckStats = ctx.RequestServices.GetRequiredService<IQualityCheckStats>()

let initialDecision = decision
let! initialResult = upstream.CompleteAsync req initialDecision ctx.RequestAborted
match initialResult with
| Ok initialBody ->
    let initialFinishReason = extractFinishReason initialBody
    let initialVerdict =
        if initialDecision.Target = Qwen35B then
            analyzeResponse qualityFallbackOpts initialFinishReason initialBody
        else
            Good
    // ... existing Bad-verdict 122B retry block ...
    let! (finalDecision, finalBody) = task {
        if qualityFallbackTriggered then
            // ... 122B retry on Bad ...
        else
            return (initialDecision, initialBody)
    }
    // ... trace block uses qualityFallbackTriggered, computes promptHash, etc ...
```

REWRITE THE BLOCK to:

1. Resolve `IJudgeClient` (null-safe) and `IJudgeStats` alongside existing services.
2. Hoist `computePromptHash` to the top of the Ok branch (researcher OQ #3).
3. Capture mutable judge result variables (judgeCalledFlag / judgeVerdictStr / judgeLatencyMs) in the outer scope.
4. After the existing Bad-verdict 122B retry handling, add a Good-verdict borderline check that runs only when `judgeClient` is non-null AND `classifyBorderline` returns Some.
5. On RouteNo (judge says bad) AND 122B reachable: do the same FallbackTo122B retry as the Bad path.
6. On RouteYes / JudgeSkipped / JudgeFailed: forward 35B response unchanged (fail-open).
7. Update fallback_kind logic: `Some "quality"` when (existing Bad triggered) OR (judge fired AND RouteNo AND retry succeeded). researcher OQ #6 — keep "quality" value; new judge_* fields disambiguate.
8. Pass judgeCalledFlag / judgeVerdictStr / judgeLatencyMs into the trace block.

CONCRETE BLOCK REPLACEMENT (preserving all existing decisionLogger.Log + metrics.Record + ctx.Response.WriteAsync calls):

```fsharp
let qualityFallbackOpts = ctx.RequestServices.GetRequiredService<QualityFallbackOptions>()
let traceLogger        = ctx.RequestServices.GetService<ITraceLogger>()
let qualityCheckStats  = ctx.RequestServices.GetRequiredService<IQualityCheckStats>()
let judgeClient        = ctx.RequestServices.GetService<IJudgeClient>()   // Phase 16: null when disabled

let initialDecision = decision
let! initialResult = upstream.CompleteAsync req initialDecision ctx.RequestAborted

match initialResult with
| Ok initialBody ->
    // Phase 16: hoist promptHash so it's shared between judge cache key and trace block.
    let promptHash = computePromptHash req.Messages

    let initialFinishReason = extractFinishReason initialBody
    let initialVerdict =
        if initialDecision.Target = Qwen35B then
            analyzeResponse qualityFallbackOpts initialFinishReason initialBody
        else
            Good

    let qualityFallbackTriggered =
        match initialVerdict with
        | Bad _ -> true
        | Good  -> false

    // Phase 15 — record the dimension that fired so /stats exposes it.
    match initialVerdict with
    | Bad (FinishReasonMatch _) -> qualityCheckStats.RecordFinishReasonHit()
    | Bad (LengthBelow _)       -> qualityCheckStats.RecordLengthHit()
    | Bad (LowEntropy _)        -> qualityCheckStats.RecordEntropyHit()
    | Bad (KeywordMatch _)      -> qualityCheckStats.RecordKeywordHit()
    | Good                      -> ()

    let badReasonStr =
        match initialVerdict with
        | Good                          -> None
        | Bad (LengthBelow n)           -> Some (sprintf "length=%d" n)
        | Bad (KeywordMatch kw)         -> Some (sprintf "keyword=%s" kw)
        | Bad (FinishReasonMatch fr)    -> Some (sprintf "finish_reason=%s" fr)
        | Bad (LowEntropy s)            -> Some (sprintf "entropy=%.2f" s)

    // Phase 16: judge cascade — only on Good 35B responses + judge enabled + borderline.
    // Returns judge metadata so the trace block can emit the 3 new fields.
    let! (finalDecision, finalBody, judgeCalled, judgeVerdictStr, judgeLatencyMs, judgeTriggeredFallback) = task {
        match initialVerdict with
        | Bad _ ->
            // Existing Phase 15 fast-fallback path — judge never called.
            if not (healthProbe.IsReachable(Qwen122B)) then
                logger.LogWarning(
                    "ChatCompletions: 35B response failed quality check but 122B unreachable; returning 35B response as-is; cid={Cid}",
                    correlationId)
                return (initialDecision, initialBody, false, None, None, false)
            else
                logger.LogInformation(
                    "ChatCompletions: 35B response failed quality check; retrying on 122B; cid={Cid}",
                    correlationId)
                let retryDecision = {
                    initialDecision with
                        Target       = Qwen122B
                        Reason       = FallbackTo122B
                        IsFallback   = true
                        ModelVersion = versionProvider.CurrentVersion
                }
                let! retryResult = upstream.CompleteAsync req retryDecision ctx.RequestAborted
                match retryResult with
                | Ok retryBody -> return (retryDecision, retryBody, false, None, None, false)
                | Error _ ->
                    logger.LogWarning(
                        "ChatCompletions: quality-fallback retry to 122B also failed; returning 35B response as-is; cid={Cid}",
                        correlationId)
                    return (initialDecision, initialBody, false, None, None, false)
        | Good ->
            // Phase 16: borderline → judge → optional FallbackTo122B
            if isNull (box judgeClient) then
                // Judge disabled (Routing.Judge.Enabled=false OR offline mode)
                return (initialDecision, initialBody, false, None, None, false)
            else
                // Extract content once — shared with response_hash and judge body
                let initialContent = extractAssistantText initialBody
                if initialDecision.Target <> Qwen35B then
                    // 122B initial target — skip judge (no escalation possible)
                    return (initialDecision, initialBody, false, None, None, false)
                else
                    match classifyBorderline qualityFallbackOpts initialContent with
                    | None ->
                        // Confidently good — no judge call
                        return (initialDecision, initialBody, false, None, None, false)
                    | Some _borderlineKind ->
                        let judgeStart = DateTimeOffset.UtcNow
                        let responseHash = computeContentHash initialContent
                        let promptText =
                            req.Messages
                            |> List.map (fun m -> m.Content)
                            |> String.concat " "
                        let! verdict =
                            judgeClient.VerdictAsync(
                                promptHash, responseHash,
                                promptText, initialContent,
                                ctx.RequestAborted)
                        let judgeMs = (DateTimeOffset.UtcNow - judgeStart).TotalMilliseconds
                        match verdict with
                        | RouteNo ->
                            // Judge says bad — fire quality fallback (same path as Phase 15 Bad)
                            if not (healthProbe.IsReachable(Qwen122B)) then
                                logger.LogWarning(
                                    "ChatCompletions: judge said NO but 122B unreachable; returning 35B response as-is; cid={Cid}",
                                    correlationId)
                                return (initialDecision, initialBody, true, Some "no", Some judgeMs, true)
                            else
                                logger.LogInformation(
                                    "ChatCompletions: judge said NO; retrying on 122B; cid={Cid}",
                                    correlationId)
                                let retryDecision = {
                                    initialDecision with
                                        Target       = Qwen122B
                                        Reason       = FallbackTo122B
                                        IsFallback   = true
                                        ModelVersion = versionProvider.CurrentVersion
                                }
                                let! retryResult = upstream.CompleteAsync req retryDecision ctx.RequestAborted
                                match retryResult with
                                | Ok retryBody ->
                                    return (retryDecision, retryBody, true, Some "no", Some judgeMs, true)
                                | Error _ ->
                                    logger.LogWarning(
                                        "ChatCompletions: judge-triggered 122B retry failed; returning 35B response as-is; cid={Cid}",
                                        correlationId)
                                    return (initialDecision, initialBody, true, Some "no", Some judgeMs, true)
                        | RouteYes ->
                            return (initialDecision, initialBody, true, Some "yes", Some judgeMs, false)
                        | JudgeSkipped reason ->
                            logger.LogDebug(
                                "ChatCompletions: judge skipped ({Reason}); forwarding 35B response; cid={Cid}",
                                reason, correlationId)
                            return (initialDecision, initialBody, true, None, Some judgeMs, false)
                        | JudgeFailed err ->
                            // Fail-open: judge infrastructure error must NOT suppress good 35B responses
                            logger.LogWarning(
                                "ChatCompletions: judge call failed ({Err}); fail-open forwarding 35B response; cid={Cid}",
                                err, correlationId)
                            return (initialDecision, initialBody, true, None, Some judgeMs, false)
    }

    // Forward final response to client.
    ctx.Response.ContentType <- "application/json"
    do! ctx.Response.WriteAsync(finalBody, ctx.RequestAborted)

    // DecisionLog — final decision wins (target = final model, reason = final reason).
    let okReason = formatReason finalDecision.Reason
    decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started (Some finalDecision) (sprintf "%A" finalDecision.Target) okReason finalDecision.IsFallback)

    // Phase 14/15/16 — Trace JSONL row (only when --trace-responses enabled).
    if not (isNull (box traceLogger)) then
        let promptText =
            req.Messages
            |> List.map (fun m -> m.Content)
            |> String.concat " "
        // qualityFallbackTriggered = true when initialVerdict was Bad (Phase 15)
        // judgeTriggeredFallback   = true when judge said NO and 122B retry actually fired (Phase 16)
        // Either one means initial response was unsatisfactory — record initial excerpt.
        let initialResponseExcerpt =
            if qualityFallbackTriggered || judgeTriggeredFallback then
                Some (truncate 500 initialBody)
            else None
        // OQ #6 resolution: keep fallback_kind = "quality" for both Phase-15-triggered
        // and judge-triggered quality fallbacks. judge_called + judge_verdict
        // disambiguate without breaking downstream tooling that does simple matching.
        let fallbackKind =
            if qualityFallbackTriggered || judgeTriggeredFallback then Some "quality"
            elif finalDecision.IsFallback then Some "availability"
            else None
        traceLogger.Log({
            schema_version           = 1
            correlation_id           = correlationId
            prompt_uid               = promptHash.Substring(0, min 12 promptHash.Length)
            prompt_hash              = promptHash
            prompt_excerpt           = truncate 200 promptText
            initial_target           = sprintf "%A" initialDecision.Target
            initial_response_excerpt = initialResponseExcerpt
            fallback_kind            = fallbackKind
            final_target             = sprintf "%A" finalDecision.Target
            final_response_excerpt   = truncate 500 finalBody
            total_latency_ms         = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            timestamp                = DateTimeOffset.UtcNow
            bad_reason               = badReasonStr
            judge_called             = judgeCalled            // NEW Phase 16
            judge_verdict            = judgeVerdictStr        // NEW Phase 16
            judge_latency_ms         = judgeLatencyMs         // NEW Phase 16
        })

    let isCanary, isFb = metricCohort finalDecision okReason
    metrics.Record(isCanary, isFb)

| Error e ->
    // ... existing Error arm UNCHANGED ...
```

KEY CONSIDERATIONS:
- The existing inner `let! (finalDecision, finalBody) = task { ... }` becomes `let! (finalDecision, finalBody, judgeCalled, judgeVerdictStr, judgeLatencyMs, judgeTriggeredFallback) = task { ... }`. F# handles the 6-tuple cleanly.
- DO NOT modify the streaming branch (`if req.Stream` block). Phase 14's INTENTIONALLY SKIPPED comment (autonomous decision C) is preserved.
- `computeContentHash` is called only on the borderline path (so we don't pay SHA-256 cost on every Good response).
- All existing decisionLogger.Log / metrics.Record / ctx.Response.WriteAsync calls run UNCHANGED — judge wiring is purely additive on the Good arm.
- `judgeClient.VerdictAsync` returns Task<JudgeVerdict> — F# match on the 4 cases is exhaustive (compiler enforces).

**EDIT 2: src/SmartRouter.Cli/Endpoints/Stats.fs**

Open Stats.fs. Make 3 sub-edits:

**2a — Add `open SmartRouter.Cli.Adapters.JudgeClient` to the imports** (after `open SmartRouter.Cli.Adapters.QueueDispatcher`).

**2b — Extend StatsWire with 3 new fields** (at the end of the existing record, after `quality_check_hits_keyword`):

```fsharp
type private StatsWire =
    { // ... existing 19 fields unchanged ...
      quality_check_hits_keyword         : int64
      judge_cache_hits                   : int64    // NEW Phase 16
      judge_cache_misses                 : int64    // NEW Phase 16
      judge_call_count                   : int64 }  // NEW Phase 16
```

Update `snapshotToWireFields` defaults:

```fsharp
let private snapshotToWireFields (s: StatsSnapshot) : StatsWire =
    { // ... existing fields unchanged ...
      quality_check_hits_keyword         = s.QualityCheckHits.Keyword
      judge_cache_hits                   = 0L      // overridden in mapEndpoints
      judge_cache_misses                 = 0L      // overridden in mapEndpoints
      judge_call_count                   = 0L }    // overridden in mapEndpoints
```

**2c — Update `mapEndpoints` to resolve IJudgeStats null-safely and populate the 3 fields:**

Inside the existing `app.MapGet("/stats", Func<HttpContext, Task>(fun ctx -> task { ... } ))` block, after the existing service resolutions (`stats`, `versionP`, `canarySt`), add:

```fsharp
            let judgeStats = ctx.RequestServices.GetService<IJudgeStats>()  // null-safe
            let struct (jHits, jMisses, jCalls) =
                if isNull (box judgeStats) then struct (0L, 0L, 0L)
                else judgeStats.GetJudgeStats()
```

Then update the `wire` object construction at the end of the handler to override the 3 judge fields:

```fsharp
            let wire =
                { baseFields with
                    baseline_model_version = versionP.CurrentVersion
                    canary_model_version   = canaryVer
                    canary_percent         = canaryPct
                    canary_active          = canaryActive
                    judge_cache_hits       = jHits         // NEW Phase 16
                    judge_cache_misses     = jMisses       // NEW Phase 16
                    judge_call_count       = jCalls }      // NEW Phase 16
```

NOTES on Stats.fs design:
- IJudgeStats is registered in BOTH composition paths (configureRequestPipeline always — judgeEnabled gates which implementation; configureWithoutMl always — NoOp). So GetService should never return null in production. We use GetService anyway (not GetRequiredService) for defense-in-depth — if a future test fixture forgets to register IJudgeStats, /stats degrades to 0L instead of crashing.

After these edits, run `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — must succeed with 0 warnings under TreatWarningsAsErrors=true. Then `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — must also succeed (test fixtures don't construct TraceRecord directly; they parse JSONL — backward-compat with the new fields is automatic since System.Text.Json round-trips additive fields without errors).

Then `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build -- --sequenced` — baseline 102+16+0 must hold. If a test fixture happens to construct `TraceRecord` literally (RouterTests / QualityFallbackTests / QSE), it must be updated to include the 3 new fields. CHECK and update if needed:

- `grep -rn "schema_version" tests/SmartRouter.Tests/*.fs` — find any TraceRecord literal constructions
- For each match, add `judge_called = false; judge_verdict = None; judge_latency_ms = None` to the record literal (Phase 14/15 tests don't exercise judge — these defaults preserve their assertions)
- This is the ONLY test edit this plan makes. Comprehensive Phase 16 test coverage lives in Plan 16-04.
  </action>
  <verify>
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — exit 0; "Build succeeded"; 0 warnings under TreatWarningsAsErrors=true
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — exit 0
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build -- --sequenced 2>&1 | tee /tmp/test16-03.log | tail -15` — must show "Passed: 102, Failed: 0, Skipped: 16" (or higher passed count if test fixtures got minor adjustments — never lower passed count, never any failed)
- `grep -c "open SmartRouter.Cli.Adapters.BorderlineClassifier\|open SmartRouter.Cli.Adapters.JudgeClient" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — exactly 2 (one for each module)
- `grep -c "judgeClient\|judgeCalled\|judgeVerdictStr\|judgeLatencyMs\|judgeTriggeredFallback\|computeContentHash\|classifyBorderline" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — at least 14 (cumulative across the new variable usages)
- `grep "INTENTIONALLY SKIPPED" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — exactly 1 line in the streaming branch (autonomous decision C: streaming preserved unchanged)
- `grep -c "judge_cache_hits\|judge_cache_misses\|judge_call_count" src/SmartRouter.Cli/Endpoints/Stats.fs` — at least 6 (3 wire fields + 3 in snapshotToWireFields/wire override)
  </verify>
  <done>
- Cli + Tests projects build cleanly under TreatWarningsAsErrors=true.
- Test baseline 102+16+0 preserved.
- Borderline → judge cascade is wired in non-streaming branch only.
- Streaming branch INTENTIONALLY SKIPPED comment preserved.
- TraceRecord 16 fields, schema_version=1.
- /stats has 3 new judge_* fields, null-safe IJudgeStats resolution.
- Default behavior identical to Phase 15 (Routing.Judge.Enabled=false → no IJudgeClient → no borderline check).
  </done>
</task>

</tasks>

<verification>
**Build verification:**
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` → 0 warnings, 0 errors under TreatWarningsAsErrors=true
- `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` → exit 0
- ARCH-01 invariant: `grep -rE "open Serilog|open System\.Net\.Http|open Microsoft\.ML|open Microsoft\.AspNetCore" src/SmartRouter.Core/` → ZERO lines (Core BCL-only preserved)

**Test baseline preservation (CRITICAL — quality gate):**
- `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build -- --sequenced` → "Passed: 102, Failed: 0, Skipped: 16" (or higher Passed if test fixtures auto-adjusted; never lower; never any Failed)
- Phase 14 QF-01..08 tests: all pass unchanged (default Routing.Judge.Enabled=false means judge never invoked in QF tests)
- Phase 15 QSE-01..06 tests: all pass unchanged (judge bypass on Bad verdicts; on Good non-borderline)
- Phase 13 #13 issue-fix tests: all pass unchanged

**Behavioral verification (manual + Plan 16-04 automated):**
- With Routing.Judge.Enabled=false (default): IJudgeClient not registered; ChatCompletions resolves null; behavior identical to Phase 15 (proven by test baseline)
- /stats wire integrity: `curl localhost:4000/stats | jq '.judge_cache_hits, .judge_cache_misses, .judge_call_count'` returns three int64 values (not undefined; not null-typed) — Plan 16-04 covers automated assertion
- TraceRecord JSONL: when --trace-responses enabled, every row has `judge_called`, `judge_verdict`, `judge_latency_ms` keys — Plan 16-04 covers automated assertion

**Schema invariants (JDG-05 compliance):**
- `grep "schema_version *<- *1\|schema_version *= *1\|schema_version *: *int" src/SmartRouter.Cli/Adapters/TraceLogger.fs` confirms schema_version still = 1 (additive change only)
- Trace fields are appended at the end of the record (declaration order = JSON property order); old log readers ignore unknown fields — forward-compat preserved
</verification>

<success_criteria>
- All 5 source files modified per task descriptions
- Cli + Tests build cleanly (0 warnings under TreatWarningsAsErrors=true)
- Test baseline 102+16+0 preserved (zero regression)
- Default behavior (Routing.Judge.Enabled=false): bit-stable identical to Phase 15
- Opt-in behavior (Routing.Judge.Enabled=true): IJudgeClient + IJudgeStats registered; ChatCompletions Good arm calls borderline → judge → fallback
- Streaming branch unchanged (INTENTIONALLY SKIPPED comment preserved — autonomous decision C)
- TraceRecord 16 fields, schema_version=1 unchanged (JDG-05)
- /stats wire has 3 new int64 fields
- All 5 researcher OQ resolutions + 4 autonomous decisions documented inline as comments at the relevant code sites
</success_criteria>

<output>
After completion, create `.planning/phases/16-122b-as-judge-for-borderline-cases/16-03-SUMMARY.md` capturing:
- Files modified with brief diff descriptions
- Test counts before/after (must be 102+ passed, never decrease)
- Build flags used
- All 5 OQ + 4 autonomous decisions cross-referenced to commit lines
- Commit hashes for atomic per-task commits:
  - `feat(16-03): extend TraceRecord with judge_called/judge_verdict/judge_latency_ms (schema_version=1 unchanged)`
  - `feat(16-03): add Routing.Judge config block to appsettings.json (Enabled=false default)`
  - `feat(16-03): register IJudgeClient + IJudgeStats DI in CompositionRoot (opt-in via Routing.Judge.Enabled)`
  - `feat(16-03): wire borderline → judge → fallback cascade in ChatCompletions.fs non-streaming branch`
  - `feat(16-03): extend /stats StatsWire with 3 judge_* fields (null-safe IJudgeStats resolution)`
</output>
