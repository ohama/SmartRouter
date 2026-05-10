---
phase: 16-122b-as-judge-for-borderline-cases
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
autonomous: true

must_haves:
  truths:
    - "BorderlineClassifier.fs is pure F# (BCL only) — `open` declarations cite only `System`, `SmartRouter.Cli.Adapters.QualityCheck`; zero references to Serilog/HttpClient/Microsoft.ML/AspNetCore"
    - "`classifyBorderline opts content` returns `Some (UncertainEntropy score)` when entropy lies in the half-open band `[EntropyThreshold, EntropyThreshold + 1.0)` AND `EntropyThreshold > 0.0`"
    - "`classifyBorderline opts content` returns `Some (UncertainLength effLen)` when entropy is NOT borderline AND `effectiveLength content` lies in `[MinResponseLength, MinResponseLength * 1.5)`"
    - "`classifyBorderline opts content` returns `None` when content is clearly good (entropy >= upper bound AND effective length >= upper bound)"
    - "Entropy band is checked BEFORE length band (cheap-first cascade ordering preserved with Phase 15)"
    - "Keyword and finish_reason dimensions are deliberately EXCLUDED from borderline detection (binary signals — no natural partial zone); decision documented in module-level comment"
    - "`charEntropy` and `effectiveLength` are computed inline in BorderlineClassifier.fs (verbatim 5-line copies of Phase 15 helpers); QualityCheck.fs visibility is NOT changed (open question #2 resolution)"
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs"
      provides: "BorderlineKind DU + classifyBorderline function (Phase 16 borderline detection)"
      contains: "module SmartRouter.Cli.Adapters.BorderlineClassifier"
      contains2: "type BorderlineKind"
      contains3: "UncertainEntropy"
      contains4: "UncertainLength"
      contains5: "let classifyBorderline"
      min_lines: 50
    - path: "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
      provides: "BorderlineClassifier.fs registered in <Compile> list AFTER QualityCheck.fs and BEFORE ChatCompletions.fs"
      contains: "Adapters/BorderlineClassifier.fs"
  key_links:
    - from: "src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs"
      to: "src/SmartRouter.Cli/Adapters/QualityCheck.fs"
      via: "open SmartRouter.Cli.Adapters.QualityCheck (for QualityFallbackOptions type)"
      pattern: "open SmartRouter\\.Cli\\.Adapters\\.QualityCheck"
    - from: "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
      to: "BorderlineClassifier.fs"
      via: "<Compile Include> ordered after QualityCheck.fs and before ChatCompletions.fs"
      pattern: "Adapters/BorderlineClassifier\\.fs.*Endpoints/ChatCompletions\\.fs"
---

<objective>
Add `Adapters/BorderlineClassifier.fs` (pure F#, BCL only) as a separate file from QualityCheck.fs. Defines `BorderlineKind` DU (`UncertainEntropy of float | UncertainLength of int`) and `classifyBorderline : QualityFallbackOptions -> string -> BorderlineKind option` that classifies a Phase-15 Good response as either confidently good (None) or borderline (Some _) based on band-edge thresholds.

Purpose: Phase 16's first concern is "which 35B Good responses warrant a 1-token verification call to 122B?" Hard-coded bands (entropy `[T, T+1.0)`, length `[L, L*1.5)`) avoid the over-configuration trap (open question #1 resolution: hard-code for Phase 16). Separation from QualityCheck.fs preserves Phase 15's tested Verdict/BadReason types unchanged (researcher's primary architectural recommendation).

Output: A new BCL-only F# module that the wiring plan (16-03) will call between Phase 15's `analyzeResponse` and the existing 122B retry path. No call sites yet; this is the pure leaf adapter.
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
@src/SmartRouter.Cli/Adapters/QualityCheck.fs
@src/SmartRouter.Cli/SmartRouter.Cli.fsproj
</context>

<rationale>
This plan resolves researcher open questions #1 and #2:

**OQ #1 — Borderline band widths (hard-code vs config?):** HARD-CODE. Bands are `EntropyThreshold + 1.0` (top of entropy band) and `MinResponseLength * 1.5` (top of length band). Why: avoid over-configuration before empirical validation; Phase 14/15 precedent of shipping defaults first, exposing knobs only when operators demand them. If empirical data later shows operator tuning need, Phase 17 can introduce `Routing.Judge.EntropyBandWidth` / `Routing.Judge.LengthBandFactor`.

**OQ #2 — `charEntropy` / `effectiveLength` visibility:** RE-IMPLEMENT INLINE. Don't change QualityCheck.fs's `private` modifiers. Why: Phase 15's QualityCheck.fs has 102 passing tests; broadening public surface for two trivial 5-line BCL helpers risks zero new value. Inline copies are pure functions (Shannon entropy formula, Hangul ratio formula); divergence is not a concern at this scale.

**Architectural note (researcher's primary recommendation):** Do NOT extend `Verdict = Good | Bad of BadReason` to add a third `Borderline` case. That would force updates to all 6 existing pattern-match arms in `ChatCompletions.fs` (lines 433-448 + 471-481) plus `isBadResponse` wrapper. Borderline is a *qualifier on Good*, not a third verdict. Architecture: `analyzeResponse → if Good → classifyBorderline → if Some → judge → decide`.
</rationale>

<tasks>

<task type="auto">
  <name>Task 1: Create BorderlineClassifier.fs with BCL-only implementation</name>
  <files>src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs</files>
  <action>
Create a NEW file `src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs` with this structure:

```fsharp
module SmartRouter.Cli.Adapters.BorderlineClassifier

open System
open SmartRouter.Cli.Adapters.QualityCheck

/// Phase 16 — Reason a Good response was classified as "borderline".
/// Only entropy and length are checked (16-RESEARCH.md §"Pattern 1"):
///   - Keyword match is binary (present/absent) — no natural partial zone.
///   - finish_reason "length"/"content_filter" are decisive signals.
/// Each case carries the measured value for trace observability.
type BorderlineKind =
    | UncertainEntropy of score : float
    | UncertainLength  of effectiveLen : int

// ── Inline copies of Phase 15 helpers (re-implementation per OQ #2) ─────────────
// QualityCheck.fs keeps koreanRatio/effectiveLength as `private`; broadening
// visibility for two 5-line BCL helpers risks no new value. Pure functions —
// divergence not a concern at this scale. See 16-RESEARCH.md §"Pattern 1".

let private koreanRatio (s: string) : float =
    if s.Length = 0 then 0.0
    else
        let mutable korCount = 0
        for c in s do
            if c >= '가' && c <= '힣' then
                korCount <- korCount + 1
        float korCount / float s.Length

let private effectiveLength (s: string) : int =
    let ratio = koreanRatio s
    int (float s.Length * (1.0 + ratio * 0.8))

// charEntropy is module-public in QualityCheck.fs (Phase 15 made it `let charEntropy`,
// not `let private charEntropy`); reuse via `open SmartRouter.Cli.Adapters.QualityCheck`.

/// Phase 16 — classify a Phase-15 `Good` response as confidently-good (None) or
/// borderline (Some _). PRECONDITION: caller invoked `analyzeResponse` and
/// received `Verdict.Good`. If verdict was Bad, the existing quality fallback
/// fires; the judge is never consulted on Bad responses.
///
/// Cascade order (cheap-first; mirrors Phase 15):
///   1. Entropy band: [EntropyThreshold, EntropyThreshold + 1.0)
///        Skipped when EntropyThreshold <= 0.0 (Phase 15 zero-means-default
///        already coalesced; this is a defensive guard).
///   2. Length band:  [MinResponseLength, int(MinResponseLength * 1.5))
///
/// Hard-coded band widths (researcher OQ #1 resolution):
///   - Top of entropy band  = EntropyThreshold + 1.0
///   - Top of length band   = MinResponseLength * 1.5
/// Avoids over-configuration before empirical tuning data exists.
///
/// Excluded dimensions (researcher's recommendation):
///   - Keyword: binary match — no partial-zone semantics.
///   - finish_reason: decisive signals; not a "maybe bad" case.
let classifyBorderline
    (opts    : QualityFallbackOptions)
    (content : string)
    : BorderlineKind option =
    if String.IsNullOrEmpty(content) then None
    else
        // Stage 1: entropy band edge
        let entropy = charEntropy content
        let entropyUpper = opts.EntropyThreshold + 1.0
        if opts.EntropyThreshold > 0.0
           && entropy >= opts.EntropyThreshold
           && entropy < entropyUpper then
            Some (UncertainEntropy entropy)
        else
            // Stage 2: length band edge
            let effLen = effectiveLength content
            let lengthUpper = int (float opts.MinResponseLength * 1.5)
            if opts.MinResponseLength > 0
               && effLen >= opts.MinResponseLength
               && effLen < lengthUpper then
                Some (UncertainLength effLen)
            else
                None
```

NOTES:
- `charEntropy` is module-public in QualityCheck.fs (line 125: `let charEntropy`, not `let private charEntropy`). Use it directly via the `open` import.
- `koreanRatio` and `effectiveLength` ARE private in QualityCheck.fs — re-implement inline (5-line copies; documented as deliberate).
- Module-level comment cites OQ #1 and OQ #2 resolutions for future readers.
- `String.IsNullOrEmpty` early-out avoids a bogus `UncertainLength 0` when content is empty (defensive; analyzeResponse would have returned `Bad (LengthBelow 0)` already, but belt-and-suspenders).
- Entropy upper bound is `EntropyThreshold + 1.0` not `+ 0.5` — wider band gives the judge more borderline cases to verify. Hard-coded width 1.0 documented.
- Length upper bound is `int (float opts.MinResponseLength * 1.5)` — `int` truncates; e.g. `30 * 1.5 = 45.0 → 45`. Consistent with Phase 15's `int (float s.Length * (1.0 + ratio * 0.8))` cast pattern.
  </action>
  <verify>
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — fails initially (file not in fsproj yet — Task 2 fixes); after Task 2, must succeed with 0 warnings under `TreatWarningsAsErrors=true`
- `grep -E "open Serilog|open System.Net.Http|open Microsoft.ML|open Microsoft.AspNetCore" src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs` — must return ZERO lines (BCL-only invariant)
- `grep -E "^module SmartRouter\.Cli\.Adapters\.BorderlineClassifier$" src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs` — exactly 1 line
- `grep -c "UncertainEntropy\|UncertainLength" src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs` — at least 4 occurrences (DU declaration + 2 constructor calls + comments)
  </verify>
  <done>
File exists at `src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs`, ~50-80 lines, defines `BorderlineKind` DU + `classifyBorderline` function, BCL-only imports, ready to be wired in Task 2.
  </done>
</task>

<task type="auto">
  <name>Task 2: Register BorderlineClassifier.fs in fsproj compile order</name>
  <files>src/SmartRouter.Cli/SmartRouter.Cli.fsproj</files>
  <action>
Edit `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` to add a `<Compile Include="Adapters/BorderlineClassifier.fs" />` entry.

PLACEMENT: must come AFTER `<Compile Include="Adapters/QualityCheck.fs" />` (line 23 currently) and BEFORE `<Compile Include="Endpoints/ChatCompletions.fs" />` (line 58 currently). The Phase 15 pattern places quality-cascade adapters early in the compile order; BorderlineClassifier consumes `QualityFallbackOptions` and `charEntropy` from QualityCheck.fs, so it must come after.

Recommended position: immediately AFTER QualityCheck.fs entry (line 23+24), with a Phase 16 comment marker:

```xml
    <Compile Include="Adapters/QualityCheck.fs" />        <!-- Phase 14: isBadResponse heuristic + QualityFallbackOptions (BCL only) -->
    <Compile Include="Adapters/BorderlineClassifier.fs" /> <!-- Phase 16: borderline detection (BCL only) -->
```

Do NOT touch any other `<Compile>` entries. Do NOT add any `<PackageReference>` (Phase 16 needs zero new NuGets — researcher confirmed).

Run `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` after the edit — must succeed with no warnings (Task 1's file has no consumers yet, so the build is purely a syntax/order check).
  </action>
  <verify>
- `grep -n "Adapters/BorderlineClassifier.fs" src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — exactly 1 line, between QualityCheck.fs line and ChatCompletions.fs line
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — exit 0; "Build succeeded"; 0 warnings, 0 errors
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | grep -E "FS\d+" | grep -v "Build succeeded"` — empty (no compiler warnings/errors)
  </verify>
  <done>
fsproj contains the new entry in the correct compile-order position; full Cli project builds cleanly under TreatWarningsAsErrors=true; no NuGet additions.
  </done>
</task>

</tasks>

<verification>
- BorderlineClassifier.fs exists, BCL-only, defines BorderlineKind + classifyBorderline
- fsproj registers it in correct compile order (after QualityCheck.fs, before ChatCompletions.fs)
- Full Cli project builds cleanly (`dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` → exit 0)
- Phase 15's QualityCheck.fs is NOT modified (visibility unchanged; researcher OQ #2)
- Tests project still builds (no test additions in this plan): `dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` → exit 0
- Test baseline preserved: `dotnet test --no-build` should pass 102+16+0 (no behavioral change yet — module is unused)
</verification>

<success_criteria>
- `src/SmartRouter.Cli/Adapters/BorderlineClassifier.fs` exists with `BorderlineKind` DU + `classifyBorderline` function
- File is BCL-only (no Serilog / HttpClient / ML.NET / AspNetCore imports)
- fsproj `<Compile>` order: QualityCheck.fs → BorderlineClassifier.fs → ... → ChatCompletions.fs
- `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` succeeds with 0 warnings
- `dotnet test --no-build` baseline (102+16+0) preserved
- Open questions #1 (hard-code bands) and #2 (re-implement helpers inline) resolved with rationale documented in module-level comment
</success_criteria>

<output>
After completion, create `.planning/phases/16-122b-as-judge-for-borderline-cases/16-01-SUMMARY.md` capturing:
- File created with line count
- fsproj edit position (line numbers before/after)
- Verification results (build output, test count)
- OQ #1 + OQ #2 resolutions documented
- Commit hash for `feat(16-01): add BorderlineClassifier.fs (pure F#, BCL only)`
</output>
