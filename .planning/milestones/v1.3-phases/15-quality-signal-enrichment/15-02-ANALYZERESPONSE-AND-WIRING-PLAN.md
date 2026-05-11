---
phase: 15-quality-signal-enrichment
plan: 02
type: execute
wave: 2
depends_on: ["15-01"]
files_modified:
  - src/SmartRouter.Cli/Adapters/QualityCheck.fs
  - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
  - src/SmartRouter.Cli/Adapters/TraceLogger.fs
  - src/SmartRouter.Cli/Endpoints/Stats.fs
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
autonomous: true

must_haves:
  truths:
    - "analyzeResponse evaluates checks in cheap-first cascade (finish_reason → effective length → entropy → keyword) and returns a Verdict; first-match wins"
    - "isBadResponse is preserved as a backward-compat wrapper: match analyzeResponse opts None body with Bad _ -> true | Good -> false (Phase 14 unit tests QF-03..QF-08 untouched)"
    - "ChatCompletions non-streaming branch extracts finish_reason from initialBody and passes it to analyzeResponse; the resulting Verdict drives both the fallback decision and the bad_reason trace field"
    - "TraceRecord has a 13th field bad_reason: string option that is None on Good and Some 'tag=value' on Bad; schema_version stays 1 (additive change)"
    - "QueueDispatcher implements IQualityCheckStats with 4 int64 counters (finish_reason, length, entropy, keyword) incremented via Interlocked.Increment in ChatCompletions after each Bad verdict"
    - "/stats endpoint exposes 4 new snake_case fields (quality_check_hits_finish_reason, quality_check_hits_length, quality_check_hits_entropy, quality_check_hits_keyword) populated from IStatsProvider.GetSnapshot()"
    - "IQualityCheckStats is registered unconditionally in BOTH configureRequestPipeline and configureWithoutMl (last-registration-wins pattern; QueueDispatcher concrete + interface alias)"
    - "Streaming branch in ChatCompletions is NOT touched by quality fallback — Phase 14 INTENTIONALLY SKIPPED comment + behavior preserved"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/QualityCheck.fs"
      provides: "analyzeResponse cheap-first cascade + isBadResponse wrapper"
      contains: "let analyzeResponse"
      contains2: "match analyzeResponse opts None body with Bad _ -> true | Good -> false"
    - path: "src/SmartRouter.Cli/Adapters/QueueDispatcher.fs"
      provides: "IQualityCheckStats interface + 4 int64 counters + StatsSnapshot extension"
      contains: "IQualityCheckStats"
      contains2: "QualityCheckHits"
    - path: "src/SmartRouter.Cli/Adapters/TraceLogger.fs"
      provides: "TraceRecord with bad_reason: string option"
      contains: "bad_reason"
    - path: "src/SmartRouter.Cli/Endpoints/Stats.fs"
      provides: "StatsWire with 4 new quality_check_hits_* fields"
      contains: "quality_check_hits_finish_reason"
      contains2: "quality_check_hits_keyword"
    - path: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      provides: "non-streaming branch using analyzeResponse + bad_reason serialization + IQualityCheckStats hit recording"
      contains: "extractFinishReason initialBody"
      contains2: "analyzeResponse"
      contains3: "bad_reason"
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "IQualityCheckStats DI registration in both composition paths"
      contains: "IQualityCheckStats"
  key_links:
    - from: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs (non-streaming branch)"
      to: "src/SmartRouter.Cli/Adapters/QualityCheck.fs analyzeResponse"
      via: "extractFinishReason initialBody piped into analyzeResponse"
      pattern: "analyzeResponse qualityFallbackOpts.*finishReason.*initialBody"
    - from: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs (Bad case match)"
      to: "src/SmartRouter.Cli/Adapters/QueueDispatcher.fs IQualityCheckStats"
      via: "ctx.RequestServices.GetService<IQualityCheckStats>() and RecordHit per BadReason tag"
      pattern: "IQualityCheckStats.*Record"
    - from: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs (TraceRecord construction)"
      to: "TraceLogger.fs TraceRecord.bad_reason field"
      via: "match verdict to format 'tag=value' and assign to record.bad_reason"
      pattern: "bad_reason\\s*=\\s*badReasonStr"
    - from: "src/SmartRouter.Cli/Endpoints/Stats.fs StatsWire"
      to: "src/SmartRouter.Cli/Adapters/QueueDispatcher.fs StatsSnapshot.QualityCheckHits"
      via: "snapshotToWireFields maps QualityCheckHits.{Finish,Length,Entropy,Keyword} to 4 snake_case fields"
      pattern: "quality_check_hits_finish_reason\\s*="
---

<objective>
Wire Phase 15's enriched detection into the live request path. This plan is the integration step: introduce `analyzeResponse` in QualityCheck.fs, replace the `isBadResponse` body with a backward-compat wrapper, propagate finish_reason from ChatCompletions through to the cascade, add the bad_reason trace field, and expose 4 quality-check-hit counters via /stats.

Purpose: Land all the wiring in one plan because every change is on the same call path — splitting QueueDispatcher counters from the ChatCompletions integration would leave half the code dead in tree (counters with no caller, or caller with no counters). The compile order (QualityCheck → TraceLogger → QueueDispatcher → ChatCompletions → Stats → CompositionRoot) means a single plan flowing top-down keeps each commit reviewable while preserving build-pass on every commit.

Output:
- New `analyzeResponse` function in QualityCheck.fs implementing the 4-stage cheap-first cascade
- `isBadResponse` rewritten as a 1-line backward-compat wrapper
- `IQualityCheckStats` interface in QueueDispatcher.fs + 4 int64 counters with Interlocked.Increment + StatsSnapshot extension
- `bad_reason: string option` added to TraceRecord (field 13; schema_version=1 unchanged)
- `quality_check_hits_finish_reason / _length / _entropy / _keyword` (4 fields) in StatsWire
- ChatCompletions non-streaming branch updated: extract finish_reason → analyzeResponse → match Verdict → record stat hit → emit bad_reason in trace → existing fallback flow unchanged
- IQualityCheckStats registered in both configureRequestPipeline and configureWithoutMl
- Streaming branch INTENTIONALLY UNTOUCHED (Phase 14 skip comment preserved)

**Dependencies on Plan 15-01:** Verdict + BadReason DUs, QualityFallbackOptions extended fields, helper functions (extractFinishReason, koreanRatio, effectiveLength, charEntropy, matchKeyword), normalizeQualityFallback defaults — all consumed unchanged from 15-01's commit.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/STATE.md
@.planning/phases/15-quality-signal-enrichment/15-CONTEXT.md
@.planning/phases/15-quality-signal-enrichment/15-RESEARCH.md
@.planning/phases/15-quality-signal-enrichment/15-01-SUMMARY.md
@src/SmartRouter.Cli/Adapters/QualityCheck.fs
@src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
@src/SmartRouter.Cli/Adapters/TraceLogger.fs
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
@src/SmartRouter.Cli/Endpoints/Stats.fs
@src/SmartRouter.Cli/CompositionRoot.fs
@CLAUDE.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Implement analyzeResponse cascade + isBadResponse compat wrapper in QualityCheck.fs</name>
  <files>src/SmartRouter.Cli/Adapters/QualityCheck.fs</files>
  <action>
**File:** `src/SmartRouter.Cli/Adapters/QualityCheck.fs` — replace the `isBadResponse` body and add `analyzeResponse` above it.

**Add `analyzeResponse` function. Cheap-first cascade per CONTEXT.md §검사 순서. The cascade reuses the helpers added in Plan 15-01.**

```fsharp
/// Phase 15 — Cheap-first cascade returning structured Verdict.
///
/// Stage order (CONTEXT.md §검사 순서):
///   1. finish_reason match (1 string compare per BadFinishReasons element)
///   2. effective length (Korean-aware; 1 single pass + multiplication)
///   3. Shannon entropy (O(n) char-count + log)
///   4. BadKeywords (Array.tryFind × IndexOf)
///
/// First match wins (early-exit). Good only when all 4 stages pass.
/// Enabled=false short-circuits to Good (kill switch).
///
/// Stage 1 runs without parsing JSON beyond what the caller already extracted.
/// Stages 2-4 share a single extractAssistantText call (Pitfall 3 — no double parse).
let analyzeResponse
    (opts: QualityFallbackOptions)
    (finishReason: string option)
    (responseBody: string)
    : Verdict =
    if not opts.Enabled then Good
    else
        // Stage 1: finish_reason
        let stage1 =
            match finishReason with
            | Some fr when not (obj.ReferenceEquals(opts.BadFinishReasons, null)) ->
                let hit =
                    opts.BadFinishReasons
                    |> Array.exists (fun r ->
                        not (String.IsNullOrEmpty(r))
                        && fr.Equals(r, StringComparison.OrdinalIgnoreCase))
                if hit then Some (Bad (FinishReasonMatch fr)) else None
            | _ -> None

        match stage1 with
        | Some v -> v
        | None ->
            // Stages 2-4 need the assistant content
            let content = extractAssistantText responseBody

            // Stage 2: effective length (Korean-aware)
            let effLen = effectiveLength content
            if effLen < opts.MinResponseLength then
                Bad (LengthBelow effLen)
            else
                // Stage 3: Shannon entropy
                let entropy = charEntropy content
                if opts.EntropyThreshold > 0.0 && entropy < opts.EntropyThreshold then
                    Bad (LowEntropy entropy)
                else
                    // Stage 4: BadKeywords (case-insensitive)
                    matchKeyword opts.BadKeywords content
```

**Rewrite `isBadResponse` as a thin compat wrapper.** This preserves the Phase 14 signature for QF-03..QF-08 unit tests:

```fsharp
/// Phase 14 backward-compat wrapper. Returns Bool from a Verdict-shaped result.
/// New callers should prefer `analyzeResponse` (returns structured Verdict).
/// Equivalent to: analyzeResponse opts None responseBody |> (function Bad _ -> true | Good -> false)
///
/// Phase 15 — finish_reason extraction is the new caller's responsibility;
/// this wrapper passes None so Phase 14 unit tests remain bit-stable.
let isBadResponse (opts: QualityFallbackOptions) (responseBody: string) : bool =
    match analyzeResponse opts None responseBody with
    | Bad _ -> true
    | Good  -> false
```

**Important behavior change for Phase 14 tests:** the wrapper now invokes the cheap-first cascade, which means:
- QF-03 (short content < MinResponseLength): still returns true (effective length on ASCII = length)
- QF-04 (keyword match in content): still returns true BUT now case-insensitive — this is EXPLICITLY the design (CONTEXT.md §"Case-sensitivity 뒤집기" reversal). Existing QF-04 fixture uses uppercase `"TODO"` keyword + uppercase content match, so both case-sensitive AND case-insensitive matchers return true. No regression.
- QF-05 (envelope-only keyword): still returns false (extractAssistantText still extracts content)
- QF-06 (malformed JSON): empty content → effective length 0 < 30 → Bad LengthBelow → true
- QF-07 (Enabled=false): short-circuits to Good → false
- QF-08 (extractAssistantText defensive): same content extraction, no behavior change

**New silent-enable behavior to confirm:** with `EntropyThreshold=2.5` default applied via 15-01's `normalizeQualityFallback`, a Phase 14 unit test fixture with normal text (entropy 4-5+) will still pass through to keyword/length checks. A test fixture with extremely low-entropy content might now flip — but Phase 14 tests use simple short content like `"Short."` (length-trigger fires first at stage 2) or `"TODO ..."` (passes length, hits keyword stage 4). No QF-* unit test should change verdict.

**If any QF-03..QF-08 test breaks under the rewritten wrapper:** STOP and document the breakage in plan SUMMARY before continuing. Do not silently update test fixtures — the QSE-06 backward-compat acceptance criterion requires Phase 14 tests to pass unchanged.
  </action>
  <verify>
    1. `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` exit 0, no warnings.
    2. `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~QualityFallbackTests"` returns: ALL QF-01..QF-08 pass (8/8 originally; 2 integration + 6 unit). If any fails, halt and document.
    3. `grep -c "let analyzeResponse" src/SmartRouter.Cli/Adapters/QualityCheck.fs` returns 1.
    4. `grep -c "let isBadResponse" src/SmartRouter.Cli/Adapters/QualityCheck.fs` returns 1.
    5. `grep -A 2 "let isBadResponse" src/SmartRouter.Cli/Adapters/QualityCheck.fs | grep -c "analyzeResponse"` returns >= 1 (proves wrapper delegates).
  </verify>
  <done>
analyzeResponse implements the 4-stage cascade with early-exit. isBadResponse is a 1-call wrapper preserving the Phase 14 signature. QF-03..QF-08 unit tests + QF-01/QF-02 integration tests pass unchanged.

**Commit:** `feat(15-02): add analyzeResponse cascade + isBadResponse backward-compat wrapper`
  </done>
</task>

<task type="auto">
  <name>Task 2: Add IQualityCheckStats + 4 counters to QueueDispatcher.fs + extend StatsSnapshot + propagate to Stats.fs StatsWire</name>
  <files>
    src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
    src/SmartRouter.Cli/Endpoints/Stats.fs
  </files>
  <action>
**Goal:** Expose 4 quality-check-hit counters via /stats. Counters track which detection dimension fired the most often, helping operators tune thresholds.

**File 1: `src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` (modify in place).**

**Step 1.1 — Add `IQualityCheckStats` interface AFTER the `StatsSnapshot` record (around current line 37) and BEFORE the `IStatsProvider` interface:**

```fsharp
/// Phase 15 — Quality-check hit counters exposed via /stats.
/// QueueDispatcher is the natural home (it already aggregates counters);
/// ChatCompletions resolves IQualityCheckStats and calls Record* on each Bad verdict.
/// Counters are process-lifetime (never reset) — same convention as failureCount.
type IQualityCheckStats =
    abstract member RecordFinishReasonHit : unit -> unit
    abstract member RecordLengthHit       : unit -> unit
    abstract member RecordEntropyHit      : unit -> unit
    abstract member RecordKeywordHit      : unit -> unit
    abstract member GetHits               : unit -> struct (int64 * int64 * int64 * int64)
    // returns (finish_reason, length, entropy, keyword) tuple
```

Use `struct` tuple to avoid tiny allocations on the /stats hot path (also matches existing F# performance idioms in this file).

**Step 1.2 — Extend `StatsSnapshot` record (around line 26-37) — add a single new nested field carrying the 4 counters:**

```fsharp
/// Phase 15 — Quality-check hit counters (process-lifetime).
type QualityCheckHits =
    { FinishReason : int64
      Length       : int64
      Entropy      : int64
      Keyword      : int64 }

type StatsSnapshot =
    { Timestamp           : DateTimeOffset
      Active122B          : int
      QueueDepth122BHigh  : int
      QueueDepth122BLow   : int
      Active35B           : int
      RequestsPerSec      : float
      AvgLatencyMs60s     : float
      FailureCountTotal   : int64
      FairnessPicksHigh   : int64
      FairnessPicksLow    : int64
      SemaphoreAvailable  : int
      QualityCheckHits    : QualityCheckHits }   // NEW Phase 15
```

**Step 1.3 — Inside `QueueDispatcher` type body, add 4 mutable counter fields next to `failureCount` (around line 98):**

```fsharp
let mutable qcHitFinishReason = 0L
let mutable qcHitLength       = 0L
let mutable qcHitEntropy      = 0L
let mutable qcHitKeyword      = 0L
```

**Step 1.4 — Implement `IQualityCheckStats` interface on QueueDispatcher. Add the implementation block AFTER the existing `interface IStatsProvider with` block (around line 419):**

```fsharp
interface IQualityCheckStats with
    member _.RecordFinishReasonHit () =
        Interlocked.Increment(&qcHitFinishReason) |> ignore
    member _.RecordLengthHit () =
        Interlocked.Increment(&qcHitLength) |> ignore
    member _.RecordEntropyHit () =
        Interlocked.Increment(&qcHitEntropy) |> ignore
    member _.RecordKeywordHit () =
        Interlocked.Increment(&qcHitKeyword) |> ignore
    member _.GetHits () =
        struct (
            Volatile.Read(&qcHitFinishReason),
            Volatile.Read(&qcHitLength),
            Volatile.Read(&qcHitEntropy),
            Volatile.Read(&qcHitKeyword))
```

**Step 1.5 — Update `IStatsProvider.GetSnapshot()` body (line 393-419) to include `QualityCheckHits` in the returned record:**

At the end of the GetSnapshot() implementation, just before the closing brace of the record literal, add:

```fsharp
              SemaphoreAvailable  = sem122b.CurrentCount
              QualityCheckHits =
                { FinishReason = Volatile.Read(&qcHitFinishReason)
                  Length       = Volatile.Read(&qcHitLength)
                  Entropy      = Volatile.Read(&qcHitEntropy)
                  Keyword      = Volatile.Read(&qcHitKeyword) } }
```

(Replace the existing trailing `SemaphoreAvailable  = sem122b.CurrentCount }` with the above expanded literal.)

**File 2: `src/SmartRouter.Cli/Endpoints/Stats.fs` (modify in place).**

**Step 2.1 — Extend `StatsWire` record (around line 21-36) with 4 new flat snake_case fields:**

```fsharp
type private StatsWire =
    { timestamp                          : string
      active_122b                        : int
      queue_depth_122b_high              : int
      queue_depth_122b_low               : int
      active_35b                         : int
      requests_per_sec                   : float
      avg_latency_ms_60s                 : float
      failure_count_total                : int64
      fairness_picks_high                : int64
      fairness_picks_low                 : int64
      semaphore_available                : int
      baseline_model_version             : string
      canary_model_version               : string option
      canary_percent                     : int
      canary_active                      : bool
      quality_check_hits_finish_reason   : int64    // NEW Phase 15
      quality_check_hits_length          : int64    // NEW Phase 15
      quality_check_hits_entropy         : int64    // NEW Phase 15
      quality_check_hits_keyword         : int64 }  // NEW Phase 15
```

**Step 2.2 — Update `snapshotToWireFields` (around line 38-53) — add the 4 fields to the literal:**

```fsharp
let private snapshotToWireFields (s: StatsSnapshot) : StatsWire =
    { timestamp                          = s.Timestamp.ToString("o")
      active_122b                        = s.Active122B
      queue_depth_122b_high              = s.QueueDepth122BHigh
      queue_depth_122b_low               = s.QueueDepth122BLow
      active_35b                         = s.Active35B
      requests_per_sec                   = s.RequestsPerSec
      avg_latency_ms_60s                 = s.AvgLatencyMs60s
      failure_count_total                = s.FailureCountTotal
      fairness_picks_high                = s.FairnessPicksHigh
      fairness_picks_low                 = s.FairnessPicksLow
      semaphore_available                = s.SemaphoreAvailable
      baseline_model_version             = ""
      canary_model_version               = None
      canary_percent                     = 0
      canary_active                      = false
      quality_check_hits_finish_reason   = s.QualityCheckHits.FinishReason
      quality_check_hits_length          = s.QualityCheckHits.Length
      quality_check_hits_entropy         = s.QualityCheckHits.Entropy
      quality_check_hits_keyword         = s.QualityCheckHits.Keyword }
```

The downstream `mapEndpoints` body (lines 58-85) consumes `baseFields` via `{ baseFields with ... }` — no further changes needed; the 4 new fields flow through automatically.
  </action>
  <verify>
    1. `dotnet build` exit 0, no warnings.
    2. `dotnet test --no-build` — Phase 14 baseline + 88 passed (no behavior change yet — counters are wired but not incremented because ChatCompletions isn't updated yet in this task).
    3. `grep -c "IQualityCheckStats" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` returns >= 2 (interface + implementation).
    4. `grep -c "QualityCheckHits" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` returns >= 2 (record + GetSnapshot population).
    5. `grep -c "quality_check_hits_finish_reason" src/SmartRouter.Cli/Endpoints/Stats.fs` returns >= 2 (StatsWire field + snapshotToWireFields literal).
    6. `grep -c "Volatile.Read.*qcHit" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` returns >= 4 (one per counter, used inside GetSnapshot or GetHits).
    7. `grep -c "Interlocked.Increment.*qcHit" src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` returns >= 4 (Record* implementations).
  </verify>
  <done>
QueueDispatcher implements IQualityCheckStats with 4 counters via Interlocked.Increment. StatsSnapshot carries QualityCheckHits. StatsWire exposes 4 snake_case fields. /stats hot path is allocation-free (struct tuple in GetHits; Volatile.Read in GetSnapshot). Counters are zero everywhere because no caller calls Record* yet — Task 3 closes that loop.

**Commit:** `feat(15-02): add IQualityCheckStats + 4 counters to QueueDispatcher; extend StatsWire`
  </done>
</task>

<task type="auto">
  <name>Task 3: Add bad_reason field to TraceRecord + wire ChatCompletions caller (analyzeResponse + stats hits + bad_reason emission) + register IQualityCheckStats DI in both composition paths</name>
  <files>
    src/SmartRouter.Cli/Adapters/TraceLogger.fs
    src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    src/SmartRouter.Cli/CompositionRoot.fs
  </files>
  <action>
**Goal:** Close the loop. Replace the Phase 14 `isBadResponse` call site in ChatCompletions with the new `analyzeResponse` flow that produces a `Verdict`, propagates `bad_reason` to TraceRecord, and increments the appropriate IQualityCheckStats counter.

**File 1: `src/SmartRouter.Cli/Adapters/TraceLogger.fs` (modify the TraceRecord type, lines 17-43).**

**Add `bad_reason` as the LAST field (Pitfall 7 — F# record field order matters; appending at the end avoids construction-site reordering):**

```fsharp
[<CLIMutable>]
type TraceRecord = {
    [<JsonPropertyName("schema_version")>]
    schema_version              : int
    [<JsonPropertyName("correlation_id")>]
    correlation_id              : string
    [<JsonPropertyName("prompt_uid")>]
    prompt_uid                  : string
    [<JsonPropertyName("prompt_hash")>]
    prompt_hash                 : string
    [<JsonPropertyName("prompt_excerpt")>]
    prompt_excerpt              : string
    [<JsonPropertyName("initial_target")>]
    initial_target              : string
    [<JsonPropertyName("initial_response_excerpt")>]
    initial_response_excerpt    : string option
    [<JsonPropertyName("fallback_kind")>]
    fallback_kind               : string option
    [<JsonPropertyName("final_target")>]
    final_target                : string
    [<JsonPropertyName("final_response_excerpt")>]
    final_response_excerpt      : string
    [<JsonPropertyName("total_latency_ms")>]
    total_latency_ms            : float
    [<JsonPropertyName("timestamp")>]
    timestamp                   : DateTimeOffset
    [<JsonPropertyName("bad_reason")>]
    bad_reason                  : string option   // NEW Phase 15 — null on Good, "tag=value" on Bad
}
```

**schema_version stays `1`** — additive field change is backward-compatible per the same convention as Phase 9 canary fields. JsonFSharpConverter (already registered on `jsonOpts` at line 90) serializes `string option` as null/value automatically.

**No changes to `writeOne` or `ExecuteAsync` — serialization is field-shape-agnostic.**

**File 2: `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` (modify the non-streaming branch, around lines 408-491).**

**Step 2.1 — Resolve `IQualityCheckStats` near the existing optional-DI resolves (around line 402-403):**

After:
```fsharp
let qualityFallbackOpts = ctx.RequestServices.GetRequiredService<QualityFallbackOptions>()
let traceLogger = ctx.RequestServices.GetService<ITraceLogger>()
```

Add:
```fsharp
let qualityCheckStats = ctx.RequestServices.GetRequiredService<IQualityCheckStats>()
```

Use `GetRequiredService` (not `GetService`) because IQualityCheckStats is registered unconditionally in both composition paths (Task 4 of this plan). Open `SmartRouter.Cli.Adapters.QueueDispatcher` is already at the top of ChatCompletions.fs (verify; if not, add `open SmartRouter.Cli.Adapters.QueueDispatcher`).

**Step 2.2 — Replace the existing `qualityFallbackTriggered` block (lines 415-417) with the new analyzeResponse flow.**

Current code:
```fsharp
let qualityFallbackTriggered =
    initialDecision.Target = Qwen35B
    && isBadResponse qualityFallbackOpts initialBody
```

Replace with:
```fsharp
// Phase 15 — extract finish_reason from initialBody and run the full cascade.
// Verdict is structured (Good | Bad of BadReason) so we can serialize bad_reason
// for the trace and increment the right /stats counter without re-parsing.
let initialFinishReason = extractFinishReason initialBody
let initialVerdict =
    if initialDecision.Target = Qwen35B then
        analyzeResponse qualityFallbackOpts initialFinishReason initialBody
    else
        Good   // 122B initial target — quality fallback never fires (no further escalation possible)

let qualityFallbackTriggered =
    match initialVerdict with
    | Bad _ -> true
    | Good  -> false

// Phase 15 — record the dimension that fired so /stats exposes it.
// Only counts the WINNING (first-match) reason per cheap-first cascade.
match initialVerdict with
| Bad (FinishReasonMatch _) -> qualityCheckStats.RecordFinishReasonHit()
| Bad (LengthBelow _)       -> qualityCheckStats.RecordLengthHit()
| Bad (LowEntropy _)        -> qualityCheckStats.RecordEntropyHit()
| Bad (KeywordMatch _)      -> qualityCheckStats.RecordKeywordHit()
| Good                      -> ()

// Phase 15 — serialize Verdict into bad_reason wire form ("tag=value").
// '=' separator matches operator jq workflow (`split("=")[0]`).
let badReasonStr =
    match initialVerdict with
    | Good                          -> None
    | Bad (LengthBelow n)           -> Some (sprintf "length=%d" n)
    | Bad (KeywordMatch kw)         -> Some (sprintf "keyword=%s" kw)
    | Bad (FinishReasonMatch fr)    -> Some (sprintf "finish_reason=%s" fr)
    | Bad (LowEntropy s)            -> Some (sprintf "entropy=%.2f" s)
```

**Step 2.3 — In the TraceRecord construction (lines 475-488), add the new `bad_reason` field as the last field:**

```fsharp
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
    bad_reason               = badReasonStr   // NEW Phase 15
})
```

**Step 2.4 — Verify the rest of the fallback flow (lines 419-451) is unchanged.** The `qualityFallbackTriggered: bool` variable is consumed downstream identically; the if/else branches that retry on 122B don't need changes — `IsFallback`, `Reason = FallbackTo122B`, etc. are set as before.

**Step 2.5 — Streaming branch (lines elsewhere, search for "INTENTIONALLY SKIPPED") MUST remain untouched.** Per QF-03 from Phase 14, streaming doesn't run quality fallback. Add a comment near the existing INTENTIONALLY SKIPPED note if it helps clarify Phase 15 doesn't change this:
```fsharp
// Phase 15 — streaming branch INTENTIONALLY SKIPPED for quality enrichment too.
// chunks already shipped to client; analyzeResponse cannot retract.
```

**File 3: `src/SmartRouter.Cli/CompositionRoot.fs` (modify both composition paths).**

**Step 3.1 — `configureRequestPipeline` (around line 290 area where `QualityFallbackOptions` is registered):**

After the existing `services.AddSingleton<QualityFallbackOptions>` block, add:

```fsharp
// Phase 15 — IQualityCheckStats unconditional registration.
// QueueDispatcher is registered (around line ~XXX of this file) as a singleton
// for IUpstreamClient + IStatsProvider. Resolve the same singleton instance
// here for the IQualityCheckStats interface.
services.AddSingleton<IQualityCheckStats>(fun sp ->
    sp.GetRequiredService<QueueDispatcher>() :> IQualityCheckStats) |> ignore
```

**Important:** `QueueDispatcher` must be registered as the concrete type via `AddSingleton<QueueDispatcher>` (not just as `IUpstreamClient`/`IStatsProvider`) so the alias resolution works. Inspect the existing registration; if it currently uses `AddSingleton<IUpstreamClient>(fun sp -> QueueDispatcher(...) :> _)` you must restructure to:
```fsharp
services.AddSingleton<QueueDispatcher>(fun sp -> QueueDispatcher(...))
services.AddSingleton<IUpstreamClient>(fun sp -> sp.GetRequiredService<QueueDispatcher>() :> IUpstreamClient)
services.AddSingleton<IStatsProvider>(fun sp -> sp.GetRequiredService<QueueDispatcher>() :> IStatsProvider)
services.AddSingleton<IQualityCheckStats>(fun sp -> sp.GetRequiredService<QueueDispatcher>() :> IQualityCheckStats)
```

Use `grep -n "AddSingleton.*QueueDispatcher\|AddSingleton<IUpstreamClient>" src/SmartRouter.Cli/CompositionRoot.fs` to find the existing registration and adapt this triple-alias pattern (consistent with the `DecisionLogWriter` triple-reg pattern already used elsewhere in the file).

**Step 3.2 — `configureWithoutMl` (around line 870 area):**

Repeat the same `IQualityCheckStats` triple-alias registration. The `--retrain` offline path doesn't actually call `Record*`, but the interface MUST be resolvable (Pitfall 10) so test fixtures that build the offline DI graph don't NRE.

If `configureWithoutMl` does NOT register `QueueDispatcher` at all (because --retrain doesn't need it), register a NoOp implementation:

```fsharp
// Phase 15 — IQualityCheckStats NoOp for offline path.
// The offline --retrain pipeline doesn't run ChatCompletions, but DI graph
// integrity tests need this resolvable (Pitfall 10).
let private noOpQualityCheckStats =
    { new IQualityCheckStats with
        member _.RecordFinishReasonHit () = ()
        member _.RecordLengthHit       () = ()
        member _.RecordEntropyHit      () = ()
        member _.RecordKeywordHit      () = ()
        member _.GetHits               () = struct (0L, 0L, 0L, 0L) }

// inside configureWithoutMl:
services.AddSingleton<IQualityCheckStats>(fun _ -> noOpQualityCheckStats) |> ignore
```

(Use `services.TryAddSingleton` if QueueDispatcher is already conditionally registered in some test paths — last-registration-wins, but the NoOp is the conservative default.)

Decide based on inspecting the existing `configureWithoutMl` body. The intent: from any DI container produced by either composition path, `GetRequiredService<IQualityCheckStats>()` succeeds.
  </action>
  <verify>
    1. `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` exit 0, no warnings.
    2. `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` exit 0, no warnings.
    3. `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build`: Phase 14 baseline (88 passed + 16 ignored + 0 failed) preserved. CRITICAL: QF-01 (35B good response — no fallback) and QF-02 (TODO triggers fallback) MUST pass unchanged. If QF-02 fails on the case-insensitive flip (uppercase fixture should still match uppercase keyword), STOP and document.
    4. `grep -c "bad_reason" src/SmartRouter.Cli/Adapters/TraceLogger.fs` returns >= 1 (field declaration with JsonPropertyName).
    5. `grep -c "extractFinishReason initialBody\|initialFinishReason" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` returns >= 1.
    6. `grep -c "RecordFinishReasonHit\|RecordLengthHit\|RecordEntropyHit\|RecordKeywordHit" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` returns >= 4.
    7. `grep -c "IQualityCheckStats" src/SmartRouter.Cli/CompositionRoot.fs` returns >= 2 (one in each composition path).
    8. `grep -c "INTENTIONALLY SKIPPED" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` returns >= 1 (Phase 14 streaming-skip comment preserved).
    9. Smoke test the build's --retrain path doesn't NRE on DI: `dotnet run --project src/SmartRouter.Cli/SmartRouter.Cli.fsproj -- --help` (or whichever flag the CLI uses to dump config without starting Kestrel) returns exit 0 — proves the DI graph builds without missing IQualityCheckStats.
  </verify>
  <done>
TraceRecord has 13 fields including bad_reason. ChatCompletions non-streaming branch uses analyzeResponse, increments the correct IQualityCheckStats counter on Bad, and emits bad_reason in TraceRecord. IQualityCheckStats is registered in both composition paths (concrete on QueueDispatcher in configureRequestPipeline; concrete or NoOp in configureWithoutMl). Streaming branch unchanged. Phase 14 test baseline (88+16) preserved.

**Commit:** `feat(15-02): wire analyzeResponse + bad_reason trace + IQualityCheckStats hits in ChatCompletions; register DI in both composition paths`
  </done>
</task>

</tasks>

<verification>
**Plan-level verification (run after all 3 tasks):**

1. `dotnet build` (root): exit 0, no warnings.
2. `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`: 88 passed + 16 ignored + 0 failed. **Critical regression gate: QF-01 / QF-02 / QF-03..QF-08 ALL pass unchanged.** Plan 15-03 will add new QSE-* tests on top of this baseline.
3. ARCH-01 grep:
   ```bash
   grep -E "(Microsoft\\.ML|Serilog|HttpClient|System\\.Net\\.Http)" src/SmartRouter.Cli/Adapters/QualityCheck.fs
   ```
   returns no matches.
4. F# task{} only (ARCH-02):
   ```bash
   bash scripts/check-no-async.sh
   ```
   returns exit 0.
5. CONTEXT.md decisions reflected:
   - Cheap-first cascade order: finish_reason → length (Korean-aware) → entropy → keyword
   - bad_reason format: `"tag=value"` with `=` separator
   - 4 quality_check_hits_* counters in /stats
   - schema_version=1 (additive change)
   - Streaming branch INTENTIONALLY SKIPPED preserved
6. DI integrity:
   ```bash
   grep -c "AddSingleton<IQualityCheckStats>" src/SmartRouter.Cli/CompositionRoot.fs
   ```
   returns 2 (configureRequestPipeline + configureWithoutMl).
7. Manual /stats spot-check (if upstream available):
   ```bash
   dotnet run --project src/SmartRouter.Cli &
   sleep 3
   curl -s localhost:4000/stats | jq '{quality_check_hits_finish_reason, quality_check_hits_length, quality_check_hits_entropy, quality_check_hits_keyword}'
   ```
   returns 4 keys with `0` values (no traffic = no hits). Plan 15-03 adds integration test that verifies non-zero values after triggering each dimension.
</verification>

<success_criteria>
- [ ] analyzeResponse 4-stage cheap-first cascade in QualityCheck.fs
- [ ] isBadResponse is now a 1-call wrapper around analyzeResponse with `None` finishReason
- [ ] TraceRecord has bad_reason field (13 fields total; schema_version stays 1)
- [ ] IQualityCheckStats interface in QueueDispatcher.fs implemented by QueueDispatcher
- [ ] StatsSnapshot.QualityCheckHits nested record populated in GetSnapshot
- [ ] StatsWire exposes 4 quality_check_hits_* snake_case fields
- [ ] ChatCompletions non-streaming branch: extractFinishReason → analyzeResponse → match Verdict → record stat hit → emit bad_reason
- [ ] IQualityCheckStats registered in BOTH configureRequestPipeline AND configureWithoutMl
- [ ] Streaming branch INTENTIONALLY SKIPPED — no changes
- [ ] Phase 14 baseline (88 passed + 16 ignored) preserved unchanged
- [ ] No new NuGet dependencies
- [ ] Three atomic commits with `feat(15-02): ...` format
</success_criteria>

<output>
After completion, create `.planning/phases/15-quality-signal-enrichment/15-02-SUMMARY.md` documenting:
- Three commit hashes
- Decision: stats interface = separate `IQualityCheckStats` (rationale: cleaner separation; QueueDispatcher implements; struct tuple for allocation-free GetHits)
- bad_reason wire format: "tag=value" with '=' separator (per CONTEXT.md examples; jq-friendly)
- Compile-order surprises (e.g., did `open SmartRouter.Cli.Adapters.QueueDispatcher` need to be added to ChatCompletions.fs?)
- Counter behavior: process-lifetime, never reset, Volatile.Read in /stats path
- DI changes in CompositionRoot: triple-alias for QueueDispatcher (concrete + IUpstreamClient + IStatsProvider + IQualityCheckStats) — confirm no circular DI
- /stats wire shape change: 4 new fields, snake_case, all int64
- Confirmation: `dotnet test` 88 passed + 16 ignored unchanged
- Open question for Plan 15-03: any new bad_reason values in QF-01/QF-02 trace files? (None expected — those tests use "stop" finish_reason and content that passes all 4 stages or fails at keyword stage with current "TODO")
</output>
</content>
</invoke>