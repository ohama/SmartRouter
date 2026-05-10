---
phase: 15-quality-signal-enrichment
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Cli/Adapters/QualityCheck.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/appsettings.json
autonomous: true

must_haves:
  truths:
    - "QualityFallbackOptions exposes BadFinishReasons (string array) and EntropyThreshold (float) config fields readable from appsettings.json:Routing.QualityFallback"
    - "BadReason DU + Verdict DU exist in QualityCheck.fs and serialize cleanly to bad_reason string via planner-specified format (key=value, '=' separator)"
    - "Pure helper functions (koreanRatio, effectiveLength, charEntropy, matchKeyword, extractFinishReason) exist in QualityCheck.fs and are unit-testable as BCL-only F# functions"
    - "normalizeQualityFallback in CompositionRoot applies safe defaults (BadFinishReasons=['length','content_filter'], EntropyThreshold=2.5) when JSON keys are absent or zero"
    - "appsettings.json:Routing.QualityFallback has BadFinishReasons array + EntropyThreshold key with documented defaults; existing BadKeywords default ['TODO','I think'] is unchanged (CONTEXT.md refusal-default decision)"
    - "Solution builds with TreatWarningsAsErrors=true; existing 88-pass + 16-ignored test baseline still passes (no behavior changes yet — only new types and helpers)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/QualityCheck.fs"
      provides: "BadReason/Verdict DUs, koreanRatio, effectiveLength, charEntropy, matchKeyword, extractFinishReason, extended QualityFallbackOptions"
      contains: "type Verdict"
      contains2: "type BadReason"
      contains3: "extractFinishReason"
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "normalizeQualityFallback extended with two new fields + safe defaults"
      contains: "BadFinishReasons"
      contains2: "EntropyThreshold"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "Routing.QualityFallback section with BadFinishReasons + EntropyThreshold defaults"
      contains: "BadFinishReasons"
  key_links:
    - from: "appsettings.json:Routing.QualityFallback.BadFinishReasons"
      to: "QualityFallbackOptions.BadFinishReasons"
      via: "Configuration.GetSection().Get<RoutingOptions>() binding through normalizeQualityFallback"
      pattern: "normalizeQualityFallback.*BadFinishReasons"
    - from: "appsettings.json:Routing.QualityFallback.EntropyThreshold"
      to: "QualityFallbackOptions.EntropyThreshold"
      via: "JSON binding + zero-means-default normalization (mirrors MinResponseLength pattern)"
      pattern: "EntropyThreshold.*<=.*0\\.0.*2\\.5"
---

<objective>
Extend the Phase 14 quality-fallback domain layer with the configuration surface and pure helper functions needed for Phase 15's five new detection dimensions: finish_reason matching, case-insensitive keyword matching, Korean-aware effective length, Shannon entropy, and structured Verdict return type.

Purpose: Land the foundation (types, helpers, config wiring, defaults) in a single small commit set so that Plan 15-02 (the actual `analyzeResponse` cascade and caller-site update) compiles cleanly. Splitting domain types away from the cascade implementation keeps each commit reviewable and atomic, and lets the test plan (15-03) exercise the helpers via unit tests independently of the integration wiring.

Output:
- Two new DUs (`BadReason`, `Verdict`) in `Adapters/QualityCheck.fs`
- Five new pure helpers (`koreanRatio`, `effectiveLength`, `charEntropy`, `matchKeyword`, `extractFinishReason`) in the same module
- `QualityFallbackOptions` extended with `BadFinishReasons: string array` + `EntropyThreshold: float`
- `normalizeQualityFallback` in `CompositionRoot.fs` extended with safe defaults for the two new fields
- `appsettings.json:Routing.QualityFallback` extended with the two new keys + documented defaults
- `analyzeResponse` and `isBadResponse` are NOT yet rewritten (Plan 15-02 owns the cascade) — Plan 15-01 adds only types + helpers; the existing `isBadResponse` body remains unchanged so the test baseline stays green

**Resolved planner decisions (documented for downstream):**

1. **Function naming (researcher Q1):** Plan 15-02 will introduce `analyzeResponse : opts -> finishReason -> body -> Verdict` AS A NEW FUNCTION while preserving `isBadResponse` as a thin backward-compat wrapper (`match analyzeResponse opts None body with Bad _ -> true | Good -> false`). Rationale: zero churn on QF-03..QF-08 unit tests; Verdict-aware caller path uses `analyzeResponse` directly. This deviates from researcher's "recommend rename" — the wrapper costs 3 lines and saves rewriting 6 unit tests. CONTEXT.md says "호출자 한 곳이라 영향 적음" — even less impact when the legacy name keeps working.

2. **Default BadKeywords (CONTEXT.md vs ROADMAP SC#3):** CONTEXT.md §"Refusal pattern default 정책" explicitly states refusal patterns MUST NOT be added to default BadKeywords. ROADMAP SC#3 / QSE-03 wording proposes adding them. CONTEXT.md is locked planner-level decision; ROADMAP wording predates discuss-phase. **Plan 15-01 honors CONTEXT.md** — default BadKeywords stays `["TODO", "I think"]`. README §7 will document operator opt-in guidance for refusal patterns (Plan 15-03). QSE-03 acceptance criterion is satisfied by capability (case-insensitive keyword match supports refusal patterns once operator adds them) rather than silent default expansion. This is recorded in plan SUMMARY for verifier.
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
@src/SmartRouter.Cli/Adapters/QualityCheck.fs
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/appsettings.json
@CLAUDE.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Add BadReason/Verdict DUs + extend QualityFallbackOptions + add pure helpers in QualityCheck.fs</name>
  <files>src/SmartRouter.Cli/Adapters/QualityCheck.fs</files>
  <action>
**File:** `src/SmartRouter.Cli/Adapters/QualityCheck.fs` (modify in place; current 75 lines).

**Add at the top (after `open System.Text.Json`, before `[<CLIMutable>] type QualityFallbackOptions`):**

```fsharp
/// Phase 15 — Structured verdict returned by analyzeResponse.
///
/// Each Bad case carries a single piece of evidence so the caller can
/// serialize a `bad_reason` trace field without parsing strings.
/// Format on the wire (caller-side): "{tag}={value}", e.g.
///   "length=12", "keyword=TODO", "finish_reason=length", "entropy=1.85"
///
/// '=' separator chosen so operators can `jq -r '.bad_reason | split("=")[0]'`.
type BadReason =
    | LengthBelow         of effectiveLen : int
    | KeywordMatch        of keyword      : string
    | FinishReasonMatch   of value        : string
    | LowEntropy          of score        : float

type Verdict =
    | Good
    | Bad of BadReason
```

**Extend `QualityFallbackOptions` — add two fields. Do NOT add `mutable` keyword to individual fields (Pitfall 1 in RESEARCH.md — `[<CLIMutable>]` already generates the mutable backing for DI binding).**

```fsharp
[<CLIMutable>]
type QualityFallbackOptions = {
    Enabled            : bool
    MinResponseLength  : int
    BadKeywords        : string array
    BadFinishReasons   : string array      // NEW Phase 15 — default ["length","content_filter"]
    EntropyThreshold   : float             // NEW Phase 15 — default 2.5; <=0 means "use default" in normalizer
}
```

**Add five new helper functions AFTER `extractAssistantText` and BEFORE `isBadResponse`. Each is `let private` with a few exceptions (`extractFinishReason` and `charEntropy` exposed for unit testing — make those `let`, not `let private`):**

```fsharp
/// Phase 15 — Extract `choices[0].finish_reason` from a non-streaming
/// OpenAI-compatible chat-completion response body. Mirrors extractAssistantText
/// pattern: safe-on-fail returns None.
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

/// Phase 15 — Ratio of Hangul Syllables (U+AC00..U+D7A3) characters in s.
/// Returns 0.0 for empty or pure ASCII; 1.0 for pure Korean.
/// Excludes Hangul Jamo and Compatibility Jamo by design (Pitfall 5).
let private koreanRatio (s: string) : float =
    if s.Length = 0 then 0.0
    else
        let mutable korCount = 0
        for c in s do
            if c >= '가' && c <= '힣' then
                korCount <- korCount + 1
        float korCount / float s.Length

/// Phase 15 — Korean-aware effective length.
///   effectiveLength = int (length × (1 + koreanRatio × 0.8))
/// Pure ASCII ⇒ ratio=0 ⇒ effective = length (backward-compat).
/// Multiplier 0.8 is fixed; not exposed as config in Phase 15.
let private effectiveLength (s: string) : int =
    let ratio = koreanRatio s
    int (float s.Length * (1.0 + ratio * 0.8))

/// Phase 15 — Shannon entropy of character distribution in s.
/// Empty string ⇒ 0.0 (caller's length check fires first; safe).
/// Uses Math.Log2 (available net5.0+; net10.0 confirmed).
/// Typical values: normal text 4.0-5.0; "the the the..." loop 1.5-2.0;
/// single-char repeat 0.0.
let charEntropy (s: string) : float =
    if s.Length = 0 then 0.0
    else
        let n = float s.Length
        s
        |> Seq.countBy id
        |> Seq.sumBy (fun (_, count) ->
            let p = float count / n
            -p * Math.Log2(p))

/// Phase 15 — Case-insensitive keyword scan. Returns Bad on first match;
/// Good when no keyword matches (or the array is null/empty).
/// Uses IndexOf(StringComparison.OrdinalIgnoreCase) (Pitfall 4 — string.Contains
/// without StringComparison overload is case-sensitive in F# usage).
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

**Note:** `Math.Log2` requires `open System` (already at top of file).

**LEAVE the existing `isBadResponse` body UNCHANGED.** Plan 15-02 will replace it with a call to `analyzeResponse`. Phase 14 unit tests (QF-03..QF-08) must keep passing in this commit. The new types and helpers are additive only.

Add doc comments referencing 15-CONTEXT.md and 15-RESEARCH.md sections.

**Compile-order constraint:** `QualityCheck.fs` is compile pos 23. No upstream changes needed. `Math.Log2` is BCL-only — no new NuGet.

**ARCH-01 compliance:** all new types and helpers use BCL only (System.Text.Json, Math, char compare). No Microsoft.ML, no Serilog, no HttpClient.
  </action>
  <verify>
    1. `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` returns exit 0 with no warnings (TreatWarningsAsErrors=true).
    2. `grep -c "type Verdict" src/SmartRouter.Cli/Adapters/QualityCheck.fs` returns 1.
    3. `grep -c "BadFinishReasons" src/SmartRouter.Cli/Adapters/QualityCheck.fs` returns >= 1.
    4. `grep -c "extractFinishReason" src/SmartRouter.Cli/Adapters/QualityCheck.fs` returns >= 1.
    5. `grep -c "charEntropy" src/SmartRouter.Cli/Adapters/QualityCheck.fs` returns >= 1.
    6. `grep -c "koreanRatio\|effectiveLength" src/SmartRouter.Cli/Adapters/QualityCheck.fs` returns >= 2.
    7. `grep -E "(Microsoft\\.ML|Serilog|HttpClient)" src/SmartRouter.Cli/Adapters/QualityCheck.fs` returns no matches (ARCH-01 spot-check).
    8. CI grep `scripts/check-no-async.sh` passes (no `async {}` introduced).
  </verify>
  <done>
QualityCheck.fs contains BadReason DU, Verdict DU, extended QualityFallbackOptions (5 fields), and 5 new helper functions (koreanRatio, effectiveLength, charEntropy, matchKeyword, extractFinishReason). Solution builds clean. Existing isBadResponse body is unchanged. ARCH-01 invariant preserved.

**Commit:** `feat(15-01): add Verdict DU + helpers + extend QualityFallbackOptions in QualityCheck.fs`
  </done>
</task>

<task type="auto">
  <name>Task 2: Extend normalizeQualityFallback + appsettings.json defaults</name>
  <files>
    src/SmartRouter.Cli/CompositionRoot.fs
    src/SmartRouter.Cli/appsettings.json
  </files>
  <action>
**File 1: `src/SmartRouter.Cli/CompositionRoot.fs` (modify lines 124-134 — `normalizeQualityFallback` function).**

Current implementation:
```fsharp
let normalizeQualityFallback (opts: RoutingOptions) : QualityFallbackOptions =
    if obj.ReferenceEquals(opts.QualityFallback, null) then
        { Enabled = false; MinResponseLength = 30; BadKeywords = [||] }
    else
        let qf = opts.QualityFallback
        { Enabled           = qf.Enabled
          MinResponseLength = (if qf.MinResponseLength <= 0 then 30 else qf.MinResponseLength)
          BadKeywords       = (if obj.ReferenceEquals(qf.BadKeywords, null) then [||] else qf.BadKeywords) }
```

Replace with extended version that handles two new fields. Apply silent-enable defaults per CONTEXT.md §"Silent enable":

```fsharp
let normalizeQualityFallback (opts: RoutingOptions) : QualityFallbackOptions =
    let defaultBadFinishReasons = [| "length"; "content_filter" |]
    let defaultEntropyThreshold = 2.5

    if obj.ReferenceEquals(opts.QualityFallback, null) then
        { Enabled            = false
          MinResponseLength  = 30
          BadKeywords        = [||]
          BadFinishReasons   = defaultBadFinishReasons
          EntropyThreshold   = defaultEntropyThreshold }
    else
        let qf = opts.QualityFallback
        { Enabled            = qf.Enabled
          MinResponseLength  = (if qf.MinResponseLength <= 0 then 30 else qf.MinResponseLength)
          BadKeywords        = (if obj.ReferenceEquals(qf.BadKeywords, null) then [||] else qf.BadKeywords)
          BadFinishReasons   =
            if obj.ReferenceEquals(qf.BadFinishReasons, null) || qf.BadFinishReasons.Length = 0
            then defaultBadFinishReasons
            else qf.BadFinishReasons
          EntropyThreshold   =
            if qf.EntropyThreshold <= 0.0 then defaultEntropyThreshold
            else qf.EntropyThreshold }
```

**Critical:** the `EntropyThreshold <= 0.0` check is the same "zero means default" pattern as `MinResponseLength` (Pitfall 2 in RESEARCH.md). CLIMutable float defaults to 0.0 when JSON key absent.

`opts.QualityFallback` is typed as `QualityFallbackOptions` (from `RoutingOptions` field — see CompositionRoot.fs line 80). After Task 1 extends `QualityFallbackOptions` with the two new fields, the JSON binder will populate them from `appsettings.json` automatically (or leave as default 0.0 / null when keys are absent).

**File 2: `src/SmartRouter.Cli/appsettings.json` (modify the `Routing.QualityFallback` block, currently lines 44-48).**

Current:
```json
"QualityFallback": {
  "Enabled": true,
  "MinResponseLength": 30,
  "BadKeywords": [ "TODO", "I think" ]
}
```

Replace with:
```json
"QualityFallback": {
  "Enabled": true,
  "MinResponseLength": 30,
  "BadKeywords": [ "TODO", "I think" ],
  "BadFinishReasons": [ "length", "content_filter" ],
  "EntropyThreshold": 2.5
}
```

**DO NOT add refusal patterns to BadKeywords** — CONTEXT.md §"Refusal pattern default 정책" locks this. Operator-opt-in pattern; documented in README in Plan 15-03.

**Backward-compat note for verifier:** if an operator's existing `appsettings.json` does NOT have `BadFinishReasons` / `EntropyThreshold` keys, `normalizeQualityFallback` applies the new defaults silently. Per CONTEXT.md §"Silent enable" this is the intended migration tone. CHANGELOG entry in Plan 15-03 documents the behavior change.
  </action>
  <verify>
    1. `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` returns exit 0.
    2. `dotnet test --no-build` against the existing test baseline returns 88 passed + 16 ignored + 0 failed (no behavior changes — only schema extension; `analyzeResponse` is not yet wired by Plan 15-01).
    3. `grep -c "BadFinishReasons" src/SmartRouter.Cli/CompositionRoot.fs` returns >= 2 (default + override branches).
    4. `grep -c "EntropyThreshold" src/SmartRouter.Cli/CompositionRoot.fs` returns >= 2.
    5. `python3 -c "import json; d=json.load(open('src/SmartRouter.Cli/appsettings.json')); qf=d['Routing']['QualityFallback']; assert qf['BadFinishReasons']==['length','content_filter']; assert qf['EntropyThreshold']==2.5; assert qf['BadKeywords']==['TODO','I think']; print('OK')"` prints `OK`.
    6. `dotnet build` passes for both Cli AND Tests projects (Tests project must still compile against the extended record).
  </verify>
  <done>
normalizeQualityFallback handles both old (Phase 14) and new (Phase 15) JSON shapes. appsettings.json has 5 keys under Routing.QualityFallback. Existing test baseline (88 passed + 16 ignored) is unchanged because isBadResponse body is unchanged. Solution builds clean.

**Commit:** `feat(15-01): extend normalizeQualityFallback + appsettings.json defaults for finish_reason + entropy`
  </done>
</task>

</tasks>

<verification>
**Plan-level verification (run after all tasks):**

1. `dotnet build` (root): exit 0, no warnings.
2. `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build`: 88 passed + 16 ignored + 0 failed (Phase 14 baseline preserved).
3. ARCH-01 grep:
   ```bash
   grep -E "(Microsoft\\.ML|Serilog|HttpClient|System\\.Net)" src/SmartRouter.Cli/Adapters/QualityCheck.fs
   ```
   returns no matches.
4. CONTEXT.md decisions reflected in code:
   - `BadFinishReasons` default is `["length","content_filter"]` (Silent enable)
   - `EntropyThreshold` default is `2.5` (Silent enable)
   - Default `BadKeywords` is `["TODO","I think"]` (no refusal patterns added — CONTEXT.md §refusal-default policy honored over ROADMAP SC#3 wording)
5. Verdict DU and BadReason DU exist with planner-spec'd cases (LengthBelow/KeywordMatch/FinishReasonMatch/LowEntropy).
</verification>

<success_criteria>
- [ ] QualityCheck.fs has Verdict + BadReason DUs with 4 BadReason cases
- [ ] QualityCheck.fs has koreanRatio, effectiveLength, charEntropy, matchKeyword, extractFinishReason helpers
- [ ] QualityFallbackOptions has 5 fields (3 existing + BadFinishReasons + EntropyThreshold)
- [ ] CompositionRoot.normalizeQualityFallback handles all 5 fields with safe defaults
- [ ] appsettings.json:Routing.QualityFallback has all 5 keys with documented defaults
- [ ] Phase 14 test baseline (88 passed + 16 ignored) unchanged
- [ ] No new NuGet dependencies (BCL only)
- [ ] ARCH-01 invariant preserved
- [ ] Two atomic commits with `{type}(15-01): ...` format
</success_criteria>

<output>
After completion, create `.planning/phases/15-quality-signal-enrichment/15-01-SUMMARY.md` documenting:
- Two new commits with hashes
- Decision rationale: function-rename via wrapper (researcher Q1) + refusal-default policy (CONTEXT.md vs ROADMAP)
- Helper signatures for Plan 15-02 to consume
- Test baseline unchanged confirmation
- Files modified list
- Any compile-order surprises (not expected — single file family)
</output>
</content>
</invoke>