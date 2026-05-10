module SmartRouter.Cli.Adapters.QualityCheck

open System

/// Phase 14 — quality-fallback heuristic.
///
/// distillation 디자인 (auto-retraining-code.md §3 isBadResponse) 의 패턴을
/// appsettings.json 으로 tunable 하게 외부화한 형태. operator 가 재빌드 없이
/// MinResponseLength / BadKeywords 를 조정 가능.
[<CLIMutable>]
type QualityFallbackOptions = {
    Enabled            : bool
    MinResponseLength  : int
    BadKeywords        : string array
}

/// Pure F# (BCL only). Returns true when the response is "bad" by the configured
/// heuristic. Returns false when QualityFallback is disabled regardless of content.
///
/// Defaults applied at the option-binding layer (CompositionRoot):
///   - Enabled: true
///   - MinResponseLength: 30
///   - BadKeywords: ["TODO", "I think"]
///
/// Case-sensitive keyword match. Operator can add lowercase variants
/// ("todo", "i think") to the array for broader coverage.
let isBadResponse (opts: QualityFallbackOptions) (response: string) : bool =
    if not opts.Enabled then
        false
    elif response.Length < opts.MinResponseLength then
        true
    elif obj.ReferenceEquals(opts.BadKeywords, null) then
        false
    else
        opts.BadKeywords
        |> Array.exists (fun kw ->
            not (String.IsNullOrEmpty(kw)) && response.Contains(kw))
