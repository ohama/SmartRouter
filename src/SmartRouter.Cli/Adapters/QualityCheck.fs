module SmartRouter.Cli.Adapters.QualityCheck

open System
open System.Text.Json

/// Phase 15 — Structured verdict returned by analyzeResponse.
///
/// Each Bad case carries a single piece of evidence so the caller can
/// serialize a `bad_reason` trace field without parsing strings.
/// Format on the wire (caller-side): "{tag}={value}", e.g.
///   "length=12", "keyword=TODO", "finish_reason=length", "entropy=1.85"
///
/// '=' separator chosen so operators can `jq -r '.bad_reason | split("=")[0]'`.
///
/// See 15-CONTEXT.md §"검사 우선순위 + 디버그가능성" for rationale.
type BadReason =
    | LengthBelow         of effectiveLen : int
    | KeywordMatch        of keyword      : string
    | FinishReasonMatch   of value        : string
    | LowEntropy          of score        : float

type Verdict =
    | Good
    | Bad of BadReason

/// Phase 14 — quality-fallback heuristic.
///
/// distillation 디자인 (auto-retraining-code.md §3 isBadResponse) 의 패턴을
/// appsettings.json 으로 tunable 하게 외부화한 형태. operator 가 재빌드 없이
/// MinResponseLength / BadKeywords 를 조정 가능.
///
/// Phase 15 extends with BadFinishReasons + EntropyThreshold (QSE-01..02).
[<CLIMutable>]
type QualityFallbackOptions = {
    Enabled            : bool
    MinResponseLength  : int
    BadKeywords        : string array
    BadFinishReasons   : string array      // Phase 15 — default ["length","content_filter"]
    EntropyThreshold   : float             // Phase 15 — default 2.5; <=0 means "use default" in normalizer
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

/// Phase 15 — Extract `choices[0].finish_reason` from a non-streaming
/// OpenAI-compatible chat-completion response body. Mirrors extractAssistantText
/// pattern: safe-on-fail returns None.
///
/// See 15-CONTEXT.md §"Phase 14 → 15 디자인 변경 정책" — finish_reason "length"
/// indicates max_tokens truncation; "content_filter" indicates moderation.
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
/// Excludes Hangul Jamo and Compatibility Jamo by design (15-CONTEXT.md §"한글 길이 보정 범위").
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
/// Multiplier 0.8 is fixed; not exposed as config in Phase 15 (15-CONTEXT.md §deferred).
let private effectiveLength (s: string) : int =
    let ratio = koreanRatio s
    int (float s.Length * (1.0 + ratio * 0.8))

/// Phase 15 — Shannon entropy of character distribution in s.
/// Empty string ⇒ 0.0 (caller's length check fires first; safe).
/// Uses Math.Log2 (available net5.0+; net10.0 confirmed).
/// Typical values: normal text 4.0-5.0; "the the the..." loop 1.5-2.0;
/// single-char repeat 0.0.
///
/// See 15-CONTEXT.md §"Claude's Discretion" — `Seq.countBy id` on string
/// iterates as seq<char>; standard F# library; no new NuGet.
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
/// Uses IndexOf(StringComparison.OrdinalIgnoreCase) — explicit StringComparison
/// avoids the case-sensitive default that Phase 14 relied on (15-CONTEXT.md
/// §"Case-sensitivity 뒤집기").
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
/// Phase 14 case-sensitive keyword match preserved here — Plan 15-02 replaces
/// this body with analyzeResponse cascade (which is case-insensitive).
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
