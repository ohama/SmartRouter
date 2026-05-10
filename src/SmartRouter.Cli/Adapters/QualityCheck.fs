module SmartRouter.Cli.Adapters.QualityCheck

open System
open System.Text.Json

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

/// Extract the assistant content (`choices[0].message.content`) from a
/// non-streaming OpenAI-compatible chat-completion response body.
///
/// Returns "" on parse error, missing field, or empty payload — the empty
/// string degrades safely: a length check sees 0 chars (likely below
/// MinResponseLength), so a malformed upstream response triggers fallback
/// rather than silently passing as "good".
///
/// Issue #13 — heuristic must check the assistant text, NOT the raw JSON
/// envelope. The envelope is always >80 chars and rarely contains literal
/// keyword strings, making length-based fallback unreachable in production
/// and keyword-based fallback only coincidentally functional.
let extractAssistantText (responseBody: string) : string =
    if String.IsNullOrEmpty(responseBody) then ""
    else
        try
            use doc = JsonDocument.Parse(responseBody)
            let root = doc.RootElement
            match root.TryGetProperty("choices") with
            | true, choices when choices.ValueKind = JsonValueKind.Array
                                && choices.GetArrayLength() > 0 ->
                let first = choices.[0]
                match first.TryGetProperty("message") with
                | true, msg ->
                    match msg.TryGetProperty("content") with
                    | true, c when c.ValueKind = JsonValueKind.String -> c.GetString()
                    | _ -> ""
                | _ -> ""
            | _ -> ""
        with _ -> ""

/// Pure F# (BCL only). Returns true when the response is "bad" by the configured
/// heuristic. Returns false when QualityFallback is disabled regardless of content.
///
/// Heuristic checks the assistant content (extractAssistantText), NOT the raw
/// JSON envelope — see issue #13 for the production bug this fix addresses.
///
/// Defaults applied at the option-binding layer (CompositionRoot):
///   - Enabled: true
///   - MinResponseLength: 30
///   - BadKeywords: ["TODO", "I think"]
///
/// Case-sensitive keyword match. Operator can add lowercase variants
/// ("todo", "i think") to the array for broader coverage.
let isBadResponse (opts: QualityFallbackOptions) (responseBody: string) : bool =
    if not opts.Enabled then
        false
    else
        let content = extractAssistantText responseBody
        if content.Length < opts.MinResponseLength then
            true
        elif obj.ReferenceEquals(opts.BadKeywords, null) then
            false
        else
            opts.BadKeywords
            |> Array.exists (fun kw ->
                not (String.IsNullOrEmpty(kw)) && content.Contains(kw))
