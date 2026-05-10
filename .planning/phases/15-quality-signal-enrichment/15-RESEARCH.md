# Phase 15: Quality Signal Enrichment - Research

**Researched:** 2026-05-10
**Domain:** F# BCL-only quality heuristic enrichment; DU-based verdict; trace/stats extension
**Confidence:** HIGH

---

## Summary

Phase 15 enriches `isBadResponse` in `src/SmartRouter.Cli/Adapters/QualityCheck.fs` with five new detection dimensions. All decisions are locked in 15-CONTEXT.md; this research maps each decision onto the exact code locations and patterns needed to implement it safely.

The current implementation (75 lines, Phase 14) is a pure function: `opts -> responseBody -> bool`. Phase 15 changes the signature to `opts -> finishReason: string option -> responseBody -> Verdict` where `Verdict` is a new F# DU. There is exactly one caller (`ChatCompletions.fs` line 417) so signature breakage is a controlled, atomic update. The trace record (`TraceLogger.fs`) gains one new `string option` field (`bad_reason`), keeping `schema_version=1`. The `/stats` endpoint and `QueueDispatcher.fs` gain four quality-hit counters.

**Primary recommendation:** Implement all changes in this order: (1) new types in QualityCheck.fs, (2) update ChatCompletions.fs caller, (3) add `bad_reason` to TraceRecord, (4) add counters to QueueDispatcher + Stats wire — this ordering matches fsproj compile order and avoids circular-dependency compiler errors.

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Text.Json (BCL) | net10.0 | `finish_reason` extraction from `choices[0]` | Already used in `extractAssistantText`; no new dependency |
| System | net10.0 | `StringComparison.OrdinalIgnoreCase`, `Math.Log2`, char range | BCL-only; ARCH-01 compliance |
| FSharp.SystemTextJson | 1.4.36 | `string option` → JSON null for `bad_reason` field | Already registered on `TraceLogger`'s JsonSerializerOptions |

### No New NuGet Packages Required
All five detection dimensions use BCL primitives only. ARCH-01 is preserved.

---

## Architecture Patterns

### Compile Order (fsproj lines 23-63)

```
Adapters/QualityCheck.fs      (line 23 — types + pure functions go here)
Adapters/TraceLogger.fs       (line 26 — TraceRecord gains bad_reason field)
Adapters/QueueDispatcher.fs   (line 33 — StatsSnapshot + counters go here)
Endpoints/ChatCompletions.fs  (line 58 — sole caller; updated last among these)
Endpoints/Stats.fs            (line 59 — StatsWire gains quality_check_hits)
CompositionRoot.fs            (line 63 — normalizeQualityFallback extended)
```

This compile order is the task sequencing constraint. A task touching `ChatCompletions.fs` must have `QualityCheck.fs` and `TraceLogger.fs` already updated (same or prior commit).

### Pattern 1: Verdict DU in QualityCheck.fs

All new types go into `QualityCheck.fs` before the existing `QualityFallbackOptions`. The `[<CLIMutable>]` attribute on `QualityFallbackOptions` stays unchanged.

```fsharp
// Source: 15-CONTEXT.md § Structured Verdict
type BadReason =
    | LengthBelow    of effectiveLen : int
    | KeywordMatch   of keyword      : string
    | FinishReasonMatch of value     : string
    | LowEntropy     of score        : float

type Verdict = Good | Bad of BadReason
```

Serialization of `BadReason` to `bad_reason` string: the caller (`ChatCompletions.fs`) formats it inline, not via FSharpJsonConverter. Pattern:

```fsharp
// In ChatCompletions.fs — format Verdict to string option for TraceRecord
let badReasonStr =
    match verdict with
    | Good -> None
    | Bad (LengthBelow n)        -> Some (sprintf "length=%d" n)
    | Bad (KeywordMatch kw)      -> Some (sprintf "keyword=%s" kw)
    | Bad (FinishReasonMatch fr) -> Some (sprintf "finish_reason=%s" fr)
    | Bad (LowEntropy s)         -> Some (sprintf "entropy=%.2f" s)
```

The `=` separator is consistent with 15-CONTEXT.md examples and is easy to `split("=")[0]` in jq.

### Pattern 2: QualityFallbackOptions Extension

`QualityFallbackOptions` in `QualityCheck.fs` gains two new mutable fields (CLIMutable record requires mutable for JSON binding):

```fsharp
[<CLIMutable>]
type QualityFallbackOptions = {
    mutable Enabled            : bool
    mutable MinResponseLength  : int
    mutable BadKeywords        : string array
    mutable BadFinishReasons   : string array   // NEW — default ["length","content_filter"]
    mutable EntropyThreshold   : float          // NEW — default 2.5
}
```

Wait — the existing Phase 14 record does NOT have `mutable` on its fields (lines 12-16 of QualityCheck.fs). It uses `[<CLIMutable>]` which makes the compiler generate a mutable version for DI binding without requiring explicit `mutable` keywords. The new fields follow the same pattern: no `mutable` keyword, just `[<CLIMutable>]` on the type.

### Pattern 3: normalizeQualityFallback Extension (CompositionRoot.fs)

The existing `normalizeQualityFallback` function (lines 127-134) handles null-check for the whole record and missing `BadKeywords`. The Phase 15 extension adds null/empty defaults for the two new arrays/floats:

```fsharp
let normalizeQualityFallback (opts: RoutingOptions) : QualityFallbackOptions =
    if obj.ReferenceEquals(opts.QualityFallback, null) then
        { Enabled            = false
          MinResponseLength  = 30
          BadKeywords        = [||]
          BadFinishReasons   = [| "length"; "content_filter" |]   // silent enable default
          EntropyThreshold   = 2.5 }
    else
        let qf = opts.QualityFallback
        { Enabled            = qf.Enabled
          MinResponseLength  = (if qf.MinResponseLength <= 0 then 30 else qf.MinResponseLength)
          BadKeywords        = (if obj.ReferenceEquals(qf.BadKeywords, null) then [||] else qf.BadKeywords)
          BadFinishReasons   = (if obj.ReferenceEquals(qf.BadFinishReasons, null)
                                   || qf.BadFinishReasons.Length = 0
                                then [| "length"; "content_filter" |]
                                else qf.BadFinishReasons)
          EntropyThreshold   = (if qf.EntropyThreshold <= 0.0 then 2.5 else qf.EntropyThreshold) }
```

CRITICAL: `EntropyThreshold = 0.0` is the CLIMutable default when the JSON key is absent. So the "zero means default" pattern (same as `MinResponseLength`) applies here. If operator explicitly sets `"EntropyThreshold": 0` they get `2.5` — this is acceptable because `0.0` entropy threshold means "flag everything" which is clearly not useful.

### Pattern 4: extractFinishReason helper in QualityCheck.fs

`finish_reason` must be extracted in `QualityCheck.fs` (the pure BCL-only adapter), NOT in `ChatCompletions.fs`. The CONTEXT.md says `isBadResponse` receives `finishReason: string option`, implying the caller extracts it. However the caller can extract it inline from `initialBody` using the same `JsonDocument.Parse` pattern as `extractAssistantText`:

```fsharp
// In ChatCompletions.fs — extract finish_reason before calling analyzeResponse
let extractFinishReason (responseBody: string) : string option =
    if String.IsNullOrEmpty(responseBody) then None
    else
        try
            use doc = JsonDocument.Parse(responseBody)
            let root = doc.RootElement
            match root.TryGetProperty("choices") with
            | true, choices when choices.ValueKind = JsonValueKind.Array
                                && choices.GetArrayLength() > 0 ->
                let first = choices.[0]
                match first.TryGetProperty("finish_reason") with
                | true, fr when fr.ValueKind = JsonValueKind.String ->
                    let s = fr.GetString()
                    if String.IsNullOrEmpty(s) then None else Some s
                | _ -> None
            | _ -> None
        with _ -> None
```

This helper belongs in `QualityCheck.fs` (same module, BCL-only) so that tests can exercise it independently. Placing it in `ChatCompletions.fs` would require either exposing it as `internal` or duplicating test logic.

### Pattern 5: Cheap-first cascade in analyzeResponse (or isBadResponse)

The function name is at Claude's discretion (CONTEXT.md). The cascade is:

```fsharp
let analyzeResponse (opts: QualityFallbackOptions) (finishReason: string option) (responseBody: string) : Verdict =
    if not opts.Enabled then Good
    else
        // Stage 1: finish_reason (cheapest — 1 string compare per array element)
        match finishReason with
        | Some fr ->
            let hit =
                not (obj.ReferenceEquals(opts.BadFinishReasons, null))
                && opts.BadFinishReasons
                   |> Array.exists (fun r ->
                       not (String.IsNullOrEmpty(r))
                       && fr.Equals(r, StringComparison.OrdinalIgnoreCase))
            if hit then Bad (FinishReasonMatch fr)
            else
                // Stage 2: effective length
                let content = extractAssistantText responseBody
                let effLen  = effectiveLength content
                if effLen < opts.MinResponseLength then Bad (LengthBelow effLen)
                else
                    // Stage 3: entropy
                    let entropy = charEntropy content
                    if opts.EntropyThreshold > 0.0 && entropy < opts.EntropyThreshold then
                        Bad (LowEntropy entropy)
                    else
                        // Stage 4: keywords
                        matchKeyword opts.BadKeywords content
        | None ->
            // No finish_reason available — skip stage 1, proceed with stages 2-4
            let content = extractAssistantText responseBody
            let effLen  = effectiveLength content
            if effLen < opts.MinResponseLength then Bad (LengthBelow effLen)
            else
                let entropy = charEntropy content
                if opts.EntropyThreshold > 0.0 && entropy < opts.EntropyThreshold then
                    Bad (LowEntropy entropy)
                else
                    matchKeyword opts.BadKeywords content
```

The pattern avoids calling `extractAssistantText` (JSON parse) until after the cheapest stage 1 completes. When `finishReason = Some "length"` the JSON is never parsed — maximum savings.

### Pattern 6: StatsSnapshot + Counter Extension (QueueDispatcher.fs)

The decision in 15-CONTEXT.md leaves "IStatsProvider extension vs separate IQualityStatsProvider" to the planner. Research finding: extending `StatsSnapshot` directly is lower friction.

Rationale:
- `IStatsProvider` is in `QueueDispatcher.fs` and consumed by `Stats.fs`. A separate `IQualityStatsProvider` would require a second DI registration in both `configureRequestPipeline` and `configureWithoutMl` plus a second `GetRequiredService` call in `Stats.fs`.
- `ChatCompletions.fs` (the hit source) already resolves `qualityFallbackOpts` via DI. Adding four `int64` counters to `QueueDispatcher`'s existing counter block and passing them via `IStatsProvider.GetSnapshot()` avoids introducing a second interface.
- The counters would be `int64` incremented via `Interlocked.Increment` (same pattern as `failureCount` at line 98).

However, `QualityCheck.fs` is a pure adapter — it does not receive or hold a reference to `IStatsProvider`. The counter increments must happen in `ChatCompletions.fs` after the verdict is known. This means `ChatCompletions.fs` needs a reference to the quality stats counter — either via a new thin interface (e.g., `IQualityCheckStats`) or by passing a recording callback.

Cleanest approach consistent with existing patterns: a new `IQualityCheckStats` interface defined in `QueueDispatcher.fs` (same file as `IStatsProvider`), implemented by `QueueDispatcher`, registered in both composition paths. `ChatCompletions.fs` resolves it via `ctx.RequestServices.GetService<IQualityCheckStats>()` (nullable — same pattern as `ITraceLogger`).

```fsharp
// In QueueDispatcher.fs — after StatsSnapshot record
type IQualityCheckStats =
    abstract member RecordHit : reason: string -> unit
    // reason is one of: "finish_reason" | "length" | "entropy" | "keyword"
```

`QueueDispatcher` implements it with four `int64` fields + `Interlocked.Increment`. `GetSnapshot()` on `IStatsProvider` includes the four counters in `StatsSnapshot`. `StatsWire` in `Stats.fs` adds `quality_check_hits` as a nested record or four flat fields.

### Pattern 7: TraceRecord bad_reason field

`TraceRecord` in `TraceLogger.fs` gains field 13:

```fsharp
[<JsonPropertyName("bad_reason")>]
bad_reason : string option
```

`string option` with `JsonFSharpConverter` → JSON null when `None`, string when `Some`. This is already how `initial_response_excerpt` and `fallback_kind` work (lines 32-34 of TraceLogger.fs). No change needed to `TraceLogger`'s serialization logic.

The `TraceRecord` construction in `ChatCompletions.fs` (lines 475-488) gains one additional field binding. The `bad_reason` value is `None` when `Verdict = Good`; it is `Some (badReasonStr)` when the fallback fires.

### Recommended Project Structure (unchanged)

```
src/SmartRouter.Cli/
├── Adapters/
│   ├── QualityCheck.fs       ← Phase 15: add DUs, 4 pure functions, extend opts
│   ├── TraceLogger.fs        ← Phase 15: add bad_reason field to TraceRecord
│   └── QueueDispatcher.fs    ← Phase 15: add IQualityCheckStats + 4 counters
└── Endpoints/
    ├── ChatCompletions.fs    ← Phase 15: update caller (1 call site), trace, stat hit
    └── Stats.fs              ← Phase 15: extend StatsWire with quality_check_hits
```

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Hangul range check | Custom Unicode table or regex | `c >= '가' && c <= '힣'` char compare | F# char literals are UTF-16; Hangul Syllables block is contiguous at U+AC00..U+D7A3; direct char compare is correct and zero-allocation |
| Shannon entropy | Probabilistic library | `Seq.countBy id` + `Math.Log2` | BCL-only; ARCH-01 requires no new NuGet; the formula is 5 lines and fully testable |
| Keyword case-insensitive search | Regex | `content.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0` | `OrdinalIgnoreCase` is BCL, allocation-free for non-matching, and the exact pattern specified in CONTEXT.md |
| JSON finish_reason extraction | Newtonsoft or custom parser | `JsonDocument.Parse` + `TryGetProperty` | Same pattern as `extractAssistantText` already in QualityCheck.fs; no new dependencies |

**Key insight:** All five heuristics are expressible in pure F# with BCL only. The existing `extractAssistantText` demonstrates the right pattern for safe-on-fail JSON parsing (`with _ -> defaultValue`).

---

## Common Pitfalls

### Pitfall 1: CLIMutable record — mutable keyword not needed

**What goes wrong:** Developer adds `mutable` keyword to individual fields of `QualityFallbackOptions`, causing compiler warnings or breaking the `{ ... with ... }` copy-and-update syntax.

**Why it happens:** The existing record has `[<CLIMutable>]` but no `mutable` field keywords. `[<CLIMutable>]` is a compiler attribute that generates mutable constructor/properties for DI binding — the F# source fields remain immutable.

**How to avoid:** Follow the existing pattern exactly. The two new fields `BadFinishReasons` and `EntropyThreshold` should be added as plain record fields with no `mutable` keyword.

**Warning signs:** If the compiler emits warnings about `mutable` in records or if copy-and-update (`{ opts with ... }`) stops compiling.

### Pitfall 2: EntropyThreshold CLIMutable default = 0.0 (not 2.5)

**What goes wrong:** Operator omits `"EntropyThreshold"` from `appsettings.json`. CLIMutable `float` fields default to `0.0`, not `2.5`. Without the `normalizeQualityFallback` fix, entropy check fires on every response (`entropy < 0.0` is never true but `entropy < 0.0` = false, so actually it would NOT fire — but if the normalize function doesn't apply the default, the value stays 0.0 and the condition `entropy < 0.0` would never be true).

**Actually the risk is inverse:** if `EntropyThreshold = 0.0` and the check is `entropy < opts.EntropyThreshold`, no response is ever flagged by entropy. The silent-enable default requires `normalizeQualityFallback` to apply `2.5` when the JSON value is absent/zero.

**How to avoid:** In `normalizeQualityFallback`, apply: `if qf.EntropyThreshold <= 0.0 then 2.5 else qf.EntropyThreshold`. The "zero means use default" pattern matches how `MinResponseLength` is handled (line 133 of CompositionRoot.fs).

**Warning signs:** Integration tests pass with EntropyThreshold absent in config but entropy-based bad responses are never caught.

### Pitfall 3: `extractAssistantText` called twice

**What goes wrong:** The cascade calls `extractAssistantText` at stage 2 and `charEntropy` also internally calls `extractAssistantText` — double JSON parse.

**How to avoid:** Extract `content` once after stage 1 exits and thread it through to stages 2, 3, 4. The cascade pseudocode in this document already does this correctly.

**Warning signs:** Two `JsonDocument.Parse` calls per request in profiling.

### Pitfall 4: String.IndexOf return value vs Contains

**What goes wrong:** `content.Contains(kw)` used for case-insensitive match — `Contains` on `string` does not accept `StringComparison`. Must use `content.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0`.

**Why it happens:** `string.Contains(value, comparison)` overload exists in .NET 5+ but it is easy to accidentally use `string.Contains(value)` (case-sensitive). Phase 14 used `content.Contains(kw)` (case-sensitive, line 74 of QualityCheck.fs).

**How to avoid:** The keyword match helper should use `IndexOf >= 0` pattern explicitly. Add a test case where the keyword differs only in case.

### Pitfall 5: Hangul Jamo vs Hangul Syllables

**What goes wrong:** Including Hangul Jamo (U+1100..U+11FF) or Hangul Compatibility Jamo (U+3130..U+318F) in the range check — these are decomposed consonant/vowel characters, not full syllables. The CONTEXT.md decision is to count only `'가'..'힣'` (Hangul Syllables block, U+AC00..U+D7A3).

**Why it happens:** Developer assumes "any Korean character" should count but Jamo appear rarely in NFC-normalized text and inflating the count would over-correct the length.

**How to avoid:** Use the exact range `c >= '가' && c <= '힣'` (char literals). Do not use Unicode category ranges.

### Pitfall 6: Expecto test list — explicit rootTests registration

**What goes wrong:** New test module not added to the explicit `rootTests` list in `Program.fs` or `Tests.fs`. Per CLAUDE.md PITFALL-26, Expecto uses explicit root list, not auto-discovery. Tests compile but never run.

**How to avoid:** After adding the new test module, add its `tests` value to the explicit `testList` or `rootTests` concatenation. Check existing test registration pattern.

### Pitfall 7: TraceRecord is a value type in F# — field ordering matters

**What goes wrong:** Adding `bad_reason` field at any position other than last causes the record construction in `ChatCompletions.fs` (lines 475-488) to fail to compile without updating every construction site. The TraceLogger `writeOne` function uses `JsonSerializer.Serialize(record, jsonOpts)` which is order-independent in output, but F# record construction requires all fields.

**How to avoid:** Add `bad_reason` as the last field in `TraceRecord`. Then update the one construction site in `ChatCompletions.fs` to include the new field.

### Pitfall 8: `Math.Log2` availability

**What goes wrong:** `Math.Log2` was added in .NET 5.0. This project targets net10.0 so it is available. Using `Math.Log(p, 2.0)` or `Math.Log(p) / Math.Log(2.0)` is an acceptable equivalent but `Math.Log2` is cleaner.

**How to avoid:** Use `Math.Log2(p)` directly. The target framework is net10.0 (confirmed in SmartRouter.Cli.fsproj line 4).

### Pitfall 9: `charEntropy` on empty string

**What goes wrong:** `s.Length = 0` → division by zero in `float c / float s.Length`. The entropy function must guard on empty string.

**How to avoid:**
```fsharp
let charEntropy (s: string) : float =
    if s.Length = 0 then 0.0   // empty → entropy 0 → does NOT fire (0 < 2.5 is true!)
    else ...
```

Wait — if `s = ""` and `EntropyThreshold = 2.5`, then `charEntropy("") = 0.0 < 2.5 = true` → `Bad (LowEntropy 0.0)`. But the length check (stage 2) fires first: `effectiveLength("") = 0 < 30 (MinResponseLength)` → `Bad (LengthBelow 0)`. So the cascade never reaches entropy for empty content. The guard is still needed to prevent division-by-zero if someone calls `charEntropy` directly.

### Pitfall 10: IQualityCheckStats is null when traceLogger is absent

**What goes wrong:** `ChatCompletions.fs` resolves `IQualityCheckStats` via `GetService` (nullable, like `ITraceLogger`). If the interface is registered unconditionally (it should be, since counters are useful regardless of trace mode), this is not an issue. But if it is mistakenly made conditional, the null check must be present.

**How to avoid:** Register `IQualityCheckStats` unconditionally in both `configureRequestPipeline` and `configureWithoutMl` (same as `IStatsProvider`).

---

## Code Examples

### koreanRatio and effectiveLength

```fsharp
// Source: 15-CONTEXT.md § 한글 길이 보정, quality-check-improvement-options.md §1-D
let private koreanRatio (s: string) : float =
    if s.Length = 0 then 0.0
    else
        let korCount = s |> Seq.filter (fun c -> c >= '가' && c <= '힣') |> Seq.length
        float korCount / float s.Length

let private effectiveLength (s: string) : int =
    let ratio = koreanRatio s
    int (float s.Length * (1.0 + ratio * 0.8))
```

Test case from CONTEXT.md: `"안녕하세요. 잘 지내고 있어요."` — 28 chars, mostly Hangul (~18 Hangul syllable chars out of 28 = ratio ~0.64), effectiveLength = `int(28 * 1.512)` = 42. Passes `MinResponseLength=30`.

### charEntropy

```fsharp
// Source: quality-check-improvement-options.md §2-A; 15-CONTEXT.md §QSE-05
let charEntropy (s: string) : float =
    if s.Length = 0 then 0.0
    else
        let n = float s.Length
        s
        |> Seq.countBy id
        |> Seq.sumBy (fun (_, count) ->
            let p = float count / n
            -p * Math.Log2(p))
```

Expected values (approximate):
- Normal English paragraph (500 chars): entropy ~4.5-5.0
- Repetitive loop `"the the the..."` (100+ chars, 4 distinct chars): entropy ~1.5-2.0
- Single character repeated `"aaaa..."`: entropy = 0.0

The `EntropyThreshold = 2.5` catches obvious loops while allowing any real content through.

### matchKeyword helper

```fsharp
// Source: 15-CONTEXT.md § Case-insensitive; quality-check-improvement-options.md §1-B
let private matchKeyword (keywords: string array) (content: string) : Verdict =
    if obj.ReferenceEquals(keywords, null) then Good
    else
        let hit =
            keywords
            |> Array.tryFind (fun kw ->
                not (String.IsNullOrEmpty(kw))
                && content.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
        match hit with
        | Some kw -> Bad (KeywordMatch kw)
        | None    -> Good
```

### extractFinishReason in QualityCheck.fs

```fsharp
// Source: 15-CONTEXT.md § QSE-01; existing extractAssistantText pattern
let extractFinishReason (responseBody: string) : string option =
    if String.IsNullOrEmpty(responseBody) then None
    else
        try
            use doc = JsonDocument.Parse(responseBody)
            let root = doc.RootElement
            match root.TryGetProperty("choices") with
            | true, choices when choices.ValueKind = JsonValueKind.Array
                                && choices.GetArrayLength() > 0 ->
                let first = choices.[0]
                match first.TryGetProperty("finish_reason") with
                | true, fr when fr.ValueKind = JsonValueKind.String ->
                    let s = fr.GetString()
                    if String.IsNullOrEmpty(s) then None else Some s
                | _ -> None
            | _ -> None
        with _ -> None
```

### ChatCompletions.fs caller update (line 415-417 area)

Current code:
```fsharp
let qualityFallbackTriggered =
    initialDecision.Target = Qwen35B
    && isBadResponse qualityFallbackOpts initialBody
```

Phase 15 replacement:
```fsharp
let finishReason  = extractFinishReason initialBody
let verdict       = analyzeResponse qualityFallbackOpts finishReason initialBody
let qualityFallbackTriggered =
    initialDecision.Target = Qwen35B
    && (match verdict with Bad _ -> true | Good -> false)
```

The `badReasonStr` (formatted for trace and stats) is computed once from `verdict` and reused in both the trace block and the stats hit.

### IQualityCheckStats (QueueDispatcher.fs)

```fsharp
// After StatsSnapshot record definition (~line 37)
type IQualityCheckStats =
    abstract member RecordFinishReasonHit : unit -> unit
    abstract member RecordLengthHit       : unit -> unit
    abstract member RecordEntropyHit      : unit -> unit
    abstract member RecordKeywordHit      : unit -> unit
    abstract member GetHits               : unit -> int64 * int64 * int64 * int64
    // returns (finish_reason, length, entropy, keyword) hit counts
```

`QueueDispatcher` adds four `int64` mutable fields and implements `IQualityCheckStats`. `GetSnapshot()` returns an extended `StatsSnapshot` with the four counters. `StatsWire` in `Stats.fs` adds them as:

```fsharp
quality_check_hits_finish_reason : int64
quality_check_hits_length        : int64
quality_check_hits_entropy       : int64
quality_check_hits_keyword       : int64
```

(Four flat fields rather than a nested object — consistent with existing `StatsWire` snake_case flat structure.)

Alternatively, a separate lightweight record can hold just the quality check hits and be referenced from `StatsSnapshot` and `StatsWire`. Either is acceptable; flat fields match the existing style.

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `content.Contains(kw)` (case-sensitive) | `content.IndexOf(kw, OrdinalIgnoreCase) >= 0` | Phase 15 | Phase 14 design reversal; operators no longer need to add case variants |
| `bool` return | `Verdict` DU | Phase 15 | Enables structured bad_reason in trace; compiler exhaustiveness on match |
| No finish_reason check | `BadFinishReasons` array config | Phase 15 | Catches truncated responses that pass length check (max_tokens hit) |
| Fixed length check | Korean-aware effectiveLength | Phase 15 | Reduces false positives on valid Korean responses |
| No repetition detection | Shannon entropy check | Phase 15 | Catches token-loop bad responses that pass length and keyword checks |

**Deprecated in Phase 15:**
- `isBadResponse (opts) (responseBody) : bool` — replaced by `analyzeResponse` returning `Verdict`. The old name can be kept as an alias returning `match ... with Bad _ -> true | Good -> false` for any unit tests that still use it, but the CONTEXT.md says "bool 반환 폐기" so rename is the right move.

---

## Focus Area Findings

### 1. isBadResponse Signature Change Impact

The sole caller is `ChatCompletions.fs` at the block starting line 415:

```fsharp
let qualityFallbackTriggered =
    initialDecision.Target = Qwen35B
    && isBadResponse qualityFallbackOpts initialBody   // ← LINE 417
```

The trace block starts at line 463 and writes `TraceRecord` at lines 475-488. The `bad_reason` field value is computed from `verdict` (available at line 415 area) and passed into the trace block. The planner should create a single task that updates both the call site (line 417) and the trace construction (lines 475-488) atomically — these are in the same task {} block and must compile together.

`finish_reason` extraction: The raw `initialBody` is available at line 409 (`Ok initialBody`). `extractFinishReason initialBody` can be called immediately after line 409, before the verdict evaluation. This avoids a second JSON parse.

### 2. TraceRecord bad_reason Pattern

`string option` follows the exact same pattern as `fallback_kind` and `initial_response_excerpt` fields (lines 32-34 of TraceLogger.fs). `JsonFSharpConverter` is already registered on `TraceLogger`'s `jsonOpts` (line 91). Adding one `string option` field requires:
- One field in `TraceRecord` (with `[<JsonPropertyName>]`)
- One field in the construction site in `ChatCompletions.fs`
- No change to `TraceLogger` serialization logic

`schema_version` stays `1` — field additions are backward-compatible with existing JSONL consumers.

### 3. IStatsProvider vs IQualityCheckStats Trade-off

Extending `IStatsProvider.GetSnapshot()` to include quality counters requires modifying `StatsSnapshot` (a record type), which requires updating every place that constructs `StatsSnapshot`. The only constructor is in `QueueDispatcher.GetSnapshot()` (line 393 area). The only consumer is `Stats.fs` via `snapshotToWireFields`. This is 2-3 lines of change in each file — low invasiveness.

A separate `IQualityCheckStats` requires a new interface, a new DI registration in two places (`configureRequestPipeline` and `configureWithoutMl`), and a new `GetRequiredService` in `Stats.fs`. More boilerplate but cleaner separation — `QueueDispatcher` doesn't conceptually own quality check metrics.

**Recommendation for planner:** Separate `IQualityCheckStats` in `QueueDispatcher.fs` file (same file, avoids new file), implemented by `QueueDispatcher` (which already tracks many counters). This keeps `StatsSnapshot` as the queue/concurrency snapshot it is, while `IQualityCheckStats.GetHits()` provides the quality dimensions. `Stats.fs` resolves both and merges into `StatsWire`.

### 4. Shannon Entropy F# BCL-only

`Seq.countBy id` on `string` iterates as `seq<char>` — each element is a UTF-16 `char`. For most content (ASCII + Hangul syllables), one code point = one char. Surrogate pairs (emoji, rare CJK) would be split into two chars and counted separately — this is acceptable for the entropy heuristic (the formula is approximate by design).

`Math.Log2` is available in .NET 5+ (net10.0 confirmed). The function is 6 lines including the empty-string guard. Performance on a 500-char excerpt: single pass over the string for `countBy`, one log per unique character. Typical ASCII/Korean content has ~40-60 unique chars, so this is ~50 float operations. Well within any latency budget.

### 5. Korean Length Correction Accuracy

The range `'가'..'힣'` (F# char literals) maps to Unicode Hangul Syllables block U+AC00..U+D7A3. This is the correct range for precomposed Hangul syllables used in modern Korean text (NFC normalized). The CONTEXT.md explicitly excludes:
- Hangul Jamo (U+1100..U+11FF) — `'ᄀ'..'ᇿ'`
- Hangul Compatibility Jamo (U+3130..U+318F) — `'ㄱ'..'ㆎ'`

In practice, mlx_lm responses in Korean use precomposed syllables (NFC), so the `'가'..'힣'` range catches all Korean content without false inclusion of Japanese kana or Chinese ideographs.

### 6. Check Cascade F# Implementation

The `Option.orElseWith` pattern is elegant but harder to read for a 4-stage cascade. `match`-with-when guards are the most idiomatic and exhaustiveness-checked. The nested `if-else` approach in the pseudocode above is the clearest because:
- `finish_reason` check needs both `None` and `Some` branches
- Each stage's "good" case falls through to the next
- The compiler can check that all `Verdict` DU cases are covered

A helper `matchKeyword` (shown above) avoids deep nesting.

### 7. Phase 14 Test Backward-Compat Analysis

**QF-03 through QF-08** (unit tests, lines 478-536) call `isBadResponse opts body` directly. If `isBadResponse` is renamed to `analyzeResponse` and returns `Verdict`, these tests must be updated. The update is:

```fsharp
// Before
Expect.isTrue (isBadResponse opts body) "..."
// After
Expect.isTrue (match analyzeResponse opts None body with Bad _ -> true | Good -> false) "..."
```

Or provide a compatibility wrapper:

```fsharp
let isBadResponse opts body = match analyzeResponse opts None body with Bad _ -> true | Good -> false
```

The `None` for `finishReason` is correct for these unit tests (they don't exercise finish_reason logic).

**QF-01/QF-02** (integration tests, lines 326-469) use fake-Kestrel. The test bodies include `finish_reason: "stop"` in the JSON (visible in `goodBody` at line 330: `"finish_reason":"stop"`). With the Phase 15 changes, `"stop"` is not in `BadFinishReasons` default `["length","content_filter"]`, so QF-01 and QF-02 remain unaffected. The new `bad_reason: null` field in the trace JSON must be handled — the tests currently read specific fields; they will not fail on unknown fields (using `JsonDocument.Parse` and `GetProperty` by name).

**New integration tests for finish_reason**: A QF-09 or QF-"finish_reason" test could use a fake upstream that returns `"finish_reason":"length"` with content long enough to pass the length check — verifying the finish_reason cascade fires. This would be a fake-Kestrel test. Value: HIGH (demonstrates the wiring works end-to-end).

### 8. CompositionRoot/appsettings Changes

`QualityFallbackOptions` extends with two new fields. `normalizeQualityFallback` provides defaults for both. The test fixture in `QualityFallbackTests.fs` (lines 121-125) binds `QualityFallback` config via in-memory collection. The new keys would need to be added to the test fixture if specific values are needed; if absent, `normalizeQualityFallback` applies the defaults (silent enable). This is a safe behavior — QF-01 and QF-02 still pass because their test content (`"stop"` finish_reason, good or keyword-triggering content) is unaffected by finish_reason and entropy defaults.

### 9. Test Strategy

**Unit tests** (pure function, no Kestrel):
- `koreanRatio`: ASCII=0.0, pure Hangul=1.0, mixed, empty
- `effectiveLength`: same cases, verify formula
- `charEntropy`: known-entropy strings, empty, single-char repeat, normal text
- `analyzeResponse` per dimension: each check in isolation (finish_reason match, length, entropy, keyword, `Enabled=false` kill-switch)
- Case-insensitive keyword: `"TODO"` keyword matches `"todo"` content

**Integration tests** (fake-Kestrel):
- QF-01, QF-02 regression (existing, must pass unchanged)
- QSE finish_reason test: fake 35B returns `finish_reason:"length"`, verify fallback fires + `bad_reason="finish_reason=length"` in trace
- Bad reason in trace: verify `bad_reason` field is `null` on good response, populated on fallback

**Distribution recommendation:** 70% unit (fast, independent, covers all 5 check dimensions plus edge cases), 30% integration (regression + 1-2 new end-to-end flows).

### 10. README Update Areas

Per CLAUDE.md sync rule, these sections require updates:

- **§5.5** "Quality fallback": Update "Trigger" bullets to list all 5 dimensions (finish_reason, length+Korean correction, entropy, keyword). Add note that case-insensitive matching is used since Phase 15.
- **§7 Routing.QualityFallback**: Add rows for `BadFinishReasons` (string[], default `["length","content_filter"]`) and `EntropyThreshold` (float, default 2.5). Update `BadKeywords` description to note case-insensitive matching and refusal pattern opt-in guidance.
- **§9.1 TraceLog schema**: Document new `bad_reason` field (string, nullable). Add example jq workflow.
- **§8 or §9.6** `/stats` response: Document 4 new `quality_check_hits_*` fields.
- **CHANGELOG**: Add `Changed` section entry per CONTEXT.md migration policy.

---

## Open Questions

1. **Function name: `analyzeResponse` vs keeping `isBadResponse`**
   - What we know: CONTEXT.md says "bool 반환 폐기"; one caller; name can change. The existing unit tests use `isBadResponse` by name.
   - What's unclear: Whether to rename (cleaner) or keep name with changed return type (less test churn).
   - Recommendation: Rename to `analyzeResponse` — the current name is semantically wrong for a function returning `Verdict`. Update unit tests in the same task.

2. **`bad_reason` separator: `=` vs `:`**
   - What we know: CONTEXT.md gives examples like `"length=12"`, `"keyword=TODO"`. Operator jq workflow uses `split("=")[0]`.
   - What's unclear: Whether colons (`:`) might appear in keyword values and confuse parsing.
   - Recommendation: Use `=` as documented. Since keyword values are operator-defined strings, document that keywords with `=` should be avoided (unlikely in practice).

3. **`IQualityCheckStats` counter scope: per-process lifetime vs resettable**
   - What we know: `failureCount` in QueueDispatcher is process-lifetime (never reset). The quality hit counters should follow the same pattern.
   - What's unclear: Whether the operator wants per-day or since-startup counts.
   - Recommendation: Process-lifetime (consistent with existing counters). If reset is needed, a future `/stats/reset` endpoint can be added.

---

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/QualityCheck.fs` — full file read; Phase 14 baseline
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/TraceLogger.fs` — full file read; TraceRecord 12-field structure
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/QueueDispatcher.fs` — full file read; IStatsProvider, StatsSnapshot, counter patterns
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — lines 1-80 and 360-500; caller site and trace construction
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Endpoints/Stats.fs` — full file; StatsWire snake_case pattern
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/CompositionRoot.fs` — lines 60-160, 280-340, 862-875; normalizeQualityFallback pattern
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/QualityFallbackTests.fs` — full file; 88-test baseline, fixture patterns
- `/Users/ohama/projs/smart-router/.planning/phases/15-quality-signal-enrichment/15-CONTEXT.md` — full read; locked decisions
- `/Users/ohama/projs/smart-router/.planning/docs/quality-check-improvement-options.md` — full read; Tier 1+2 design reference

### Secondary (MEDIUM confidence)
- `SmartRouter.Cli.fsproj` — compile order confirmed
- `README.md` — §5.5 and §7 current content confirmed for update scope

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — BCL-only, no new NuGet, all patterns already in codebase
- Architecture: HIGH — single caller, compile order confirmed, all files read
- Pitfalls: HIGH — sourced directly from codebase reading + known F# CLIMutable behavior
- Test strategy: HIGH — existing test infrastructure (fake-Kestrel, Expecto) understood in detail

**Research date:** 2026-05-10
**Valid until:** 2026-06-10 (stable — no fast-moving dependencies; all BCL)
