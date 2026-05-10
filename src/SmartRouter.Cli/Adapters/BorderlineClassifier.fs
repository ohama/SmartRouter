module SmartRouter.Cli.Adapters.BorderlineClassifier

open System
open SmartRouter.Cli.Adapters.QualityCheck

/// Phase 16 — Reason a Good response was classified as "borderline".
/// Only entropy and length are checked (16-RESEARCH.md §"Pattern 1"):
///   - Keyword match is binary (present/absent) — no natural partial zone.
///   - finish_reason "length"/"content_filter" are decisive signals.
/// Each case carries the measured value for trace observability.
type BorderlineKind =
    | UncertainEntropy of score        : float
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
