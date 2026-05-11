---
phase: 14-quality-fallback-and-trace
plan: 04
type: execute
wave: 4
depends_on: ["14-03"]
files_modified:
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
autonomous: true

must_haves:
  truths:
    - "ChatCompletions.fs non-streaming branch (after `upstream.CompleteAsync` returns Ok body) checks `decision.Target = Qwen35B && QualityCheck.isBadResponse routingOpts.QualityFallback body`"
    - "When quality fallback triggers AND `IHealthProbe.IsReachable(Qwen122B) = true`, retries via `upstream.CompleteAsync` with new RoutingDecision { Target = Qwen122B; Reason = FallbackTo122B; IsFallback = true }; uses retry result as final response if Ok, otherwise falls back to 35B response"
    - "When quality fallback triggers BUT 122B is unreachable, logs Warning and returns the 35B response as-is (no infinite loop; no error to client)"
    - "DecisionLog row reflects FINAL decision: target = final_target, routing_reason = formatReason of final reason (\"fallback_to_122b\" if quality fallback fired and 122B succeeded; \"ml\" otherwise), fallback_used = final_decision.IsFallback"
    - "Streaming branch (req.Stream = true) is UNCHANGED — quality fallback skipped entirely with comment explaining why (chunks already shipped to client; retract impossible)"
    - "TraceLogger consumed via `ctx.RequestServices.GetService<ITraceLogger>()` (returns null when --trace-responses absent); when non-null, .Log called with full TraceRecord (initial_target, initial_response_excerpt if fallback fired, fallback_kind, final_target, final_response_excerpt, total_latency_ms)"
    - "prompt_uid = first 12 hex of computePromptHash result (Phase 5 helper; reused)"
    - "Excerpt truncate: 200 chars for prompt_excerpt, 500 chars for response excerpts; helper `truncate (n: int) (s: string) : string` defined inline or in Json.fs"
    - "dotnet build clean with TreatWarningsAsErrors=true"
    - "dotnet test green; existing 80 tests preserved (new test for this code path lives in 14-05)"
  artifacts:
    - path: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      provides: "Non-streaming branch with quality fallback (35B → 122B retry) + trace emission"
---

<objective>
Implement the actual quality fallback path: when a non-streaming 35B response fails the `isBadResponse` check (and 122B is reachable), retry the same request on 122B and forward 122B's response to the client. DecisionLog reflects final routing; TraceLogger (if enabled) captures both intermediate and final responses for grep-by-prompt_uid debugging.

Streaming requests skip this branch entirely — chunks already flushed to client; cannot retract.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/14-quality-fallback-and-trace/14-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Quality fallback branch in non-streaming path + trace emission</name>
  <files>src/SmartRouter.Cli/Endpoints/ChatCompletions.fs</files>
  <action>
Edit `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`. The non-streaming branch (currently around lines 365-395, after the streaming branch's `else` keyword) handles the upstream.CompleteAsync response. Add quality fallback logic AFTER the upstream call returns Ok body, BEFORE writing to ctx.Response.

**Helper additions at module top:**

```fsharp
// Phase 14 — string truncation for trace excerpts.
// Returns up to n characters, with "…" suffix when truncated.
let private truncate (n: int) (s: string) : string =
    if isNull s then ""
    elif s.Length <= n then s
    else s.Substring(0, n) + "…"
```

**Resolution at handler entry (already partly present):**

```fsharp
let routingConfig    = ctx.RequestServices.GetRequiredService<RoutingConfig>()
let routingOpts      = ctx.RequestServices.GetRequiredService<IOptions<RoutingOptions>>().Value
let regn             = ctx.RequestServices.GetRequiredService<RoutingAlgorithmRegistration>()
let versionProvider  = ctx.RequestServices.GetRequiredService<IModelVersionProvider>()
let decisionLogger   = ctx.RequestServices.GetRequiredService<IDecisionLogger>()
let healthProbe      = ctx.RequestServices.GetRequiredService<IHealthProbe>()
let upstream         = ctx.RequestServices.GetRequiredService<IUpstreamClient>()
// Phase 14 — optional trace logger; null when --trace-responses absent.
let traceLogger      = ctx.RequestServices.GetService<ITraceLogger>()
```

**Modified non-streaming branch:**

```fsharp
else
    // Non-streaming branch.
    Log.Information("Routing target={Target} reason={Reason} priority={Priority}",
                    decision.Target, decision.Reason, decision.Priority)
    
    let started = DateTimeOffset.UtcNow
    let initialDecision = decision
    let! initialResult = upstream.CompleteAsync req initialDecision ctx.RequestAborted
    
    match initialResult with
    | Error e ->
        // Existing error handling — write upstream error to client + DecisionLog.
        ...
    | Ok initialBody ->
        // Phase 14 — Quality fallback (35B → 122B retry on bad response).
        // Streaming branch skips this entirely; chunks already shipped.
        let qualityFallbackTriggered =
            initialDecision.Target = Qwen35B
            && QualityCheck.isBadResponse routingOpts.QualityFallback initialBody
        
        let! (finalDecision, finalBody) = task {
            if qualityFallbackTriggered then
                if not (healthProbe.IsReachable(Qwen122B)) then
                    Log.Warning(
                        "ChatCompletions: 35B response failed quality check but 122B is unreachable; returning 35B response as-is; cid={Cid}",
                        correlationId)
                    return (initialDecision, initialBody)
                else
                    Log.Information(
                        "ChatCompletions: 35B response failed quality check; retrying on 122B; cid={Cid}",
                        correlationId)
                    let retryDecision = {
                        initialDecision with
                            Target     = Qwen122B
                            Reason     = FallbackTo122B
                            IsFallback = true
                            ModelVersion = versionProvider.CurrentVersion   // live read; issue #12 pattern
                    }
                    let! retryResult = upstream.CompleteAsync req retryDecision ctx.RequestAborted
                    match retryResult with
                    | Ok retryBody ->
                        return (retryDecision, retryBody)
                    | Error _ ->
                        Log.Warning(
                            "ChatCompletions: quality-fallback retry to 122B also failed; returning 35B response as-is; cid={Cid}",
                            correlationId)
                        return (initialDecision, initialBody)
            else
                return (initialDecision, initialBody)
        }
        
        // Forward final response to client.
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(finalBody, ctx.RequestAborted)
        
        // DecisionLog — final decision wins.
        let okReason = formatReason finalDecision.Reason
        decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started 
                            (Some finalDecision) (sprintf "%A" finalDecision.Target) okReason finalDecision.IsFallback)
        
        // Phase 14 — Trace JSONL row (only if --trace-responses enabled).
        if not (isNull (box traceLogger)) then
            let promptHash = computePromptHash req.Messages
            let initialResponseExcerpt =
                if qualityFallbackTriggered then Some (truncate 500 initialBody) else None
            let fallbackKind =
                if qualityFallbackTriggered then Some "quality"
                elif finalDecision.IsFallback then Some "availability"
                else None
            let promptText =
                req.Messages
                |> List.map (fun m -> m.Content)
                |> String.concat " "
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
            })
        
        // Metrics — record cohort (existing pattern).
        let isCanary, isFb = metricCohort finalDecision okReason
        metrics.Record(isCanary, isFb)
```

**Streaming branch comment (no code change):**

Add a comment near the streaming branch's start (around line 270) explaining streaming is exempt:

```fsharp
// ── SSE streaming branch ──────────────────────────────────────────────
// Phase 14: Quality fallback (35B response → 122B retry) is INTENTIONALLY
// SKIPPED for streaming requests. Once the first SSE chunk has been
// FlushAsync'd to the client (typically within ~100ms), the response
// cannot be retracted. Streaming-quality fallback would require either
// per-chunk quality detection (not feasible — partial token streams have
// no semantic completeness) or full server-side buffering (defeats the
// latency advantage of streaming entirely). Operators who want quality
// fallback should send non-streaming requests (stream=false).
```

**Edge cases verified by code:**

- 122B unreachable AND 35B quality bad → return 35B response (graceful degradation; no infinite retry)
- 122B reachable, retry fails → return 35B response (122B errored mid-retry; better than nothing)
- `routingOpts.QualityFallback.Enabled = false` → `isBadResponse` returns false → fallback never triggers (operator kill-switch)
- 35B response is good → no fallback, no extra latency
- `traceLogger = null` → trace block skipped (no perf cost when --trace-responses off)
- graph_indexing path is upstream of this branch (Phase 10 503 returns earlier); never reaches quality fallback
  </action>
  <verify>
```bash
grep -c "FallbackTo122B\|QualityCheck.isBadResponse\|qualityFallbackTriggered" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# expected: >= 3
grep -c "traceLogger\.Log\|ITraceLogger" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# expected: >= 2
grep -c "INTENTIONALLY SKIPPED\|streaming.*Phase 14\|quality fallback.*skipped" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# expected: >= 1 (streaming exempt comment)
dotnet build 2>&1 | tail -3
# expected: Build succeeded with TreatWarningsAsErrors=true
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | grep "EXPECTO!" | tail -1
# expected: 80 passed (no test changes; behavior change only triggers when isBadResponse fires which requires specific response content)
```
  </verify>
</task>

</tasks>

<verification>
- [x] Non-streaming branch quality fallback: 35B + bad → 122B retry; 122B 미가용 시 graceful degradation
- [x] DecisionLog 가 final decision 반영 (target = final, routing_reason = "fallback_to_122b" or "ml")
- [x] TraceLogger 옵셔널 resolve (--trace-responses 안 켜면 null)
- [x] Streaming branch 무수정 + 명시적 주석
- [x] prompt_uid = prompt_hash[:12]
- [x] Excerpt truncate (prompt 200 / response 500)
- [x] 빌드 clean; 80 테스트 통과
</verification>
