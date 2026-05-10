module SmartRouter.Cli.Adapters.DecisionLogger

open System
open System.Security.Cryptography
open System.Text
open SmartRouter.Core.Domain

/// Compute SHA-256 hex of concatenated message content.
/// Full conversation, not just last user message.
/// (Pitfall P8: use sha per call — SHA256 is not thread-safe.)
let computePromptHash (messages: Message list) : string =
    let text = messages |> List.map (fun m -> m.Content) |> String.concat ""
    use sha = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(text)
    let hash  = sha.ComputeHash(bytes)
    hash |> Array.map (sprintf "%02x") |> String.concat ""

/// Ratio of Hangul Syllables block characters in all messages.
/// [가-힣] — does NOT include Jamo or Compatibility Jamo.
/// Returns 0.0 for empty input. Used for Phase 9 canary cohort comparison.
let computeKoreanRatio (messages: Message list) : float =
    let text = messages |> List.map (fun m -> m.Content) |> String.concat ""
    if text.Length = 0 then 0.0
    else
        let n = text |> Seq.filter (fun c -> c >= '가' && c <= '힣') |> Seq.length
        float n / float text.Length

/// Human-readable routing reason — avoids F# DU %A reflection strings.
/// Hand-rolled to produce stable, operator-readable strings in JSONL output.
let formatReason (reason: RoutingReason) : string =
    match reason with
    | ExplicitModelOverride alias -> sprintf "explicit_model:%s" alias
    | ExplicitTask taskType       -> sprintf "explicit_task:%A" taskType
    | Default                     -> "default"
    | ML                          -> "ml"
    | FallbackTo35B               -> "fallback_to_35b"
    | FallbackTo122B              -> "fallback_to_122b"   // NEW Phase 14

/// One JSONL line per routing decision.
/// Cli-only — pure F# record, no Core references beyond Domain.
/// Schema version 1. 12 fields including schema_version and prompt_korean_char_ratio.
/// [<CLIMutable>] required for JsonSerializer deserialization in tests.
[<CLIMutable>]
type DecisionLog =
    { schema_version           : int
      correlation_id           : string
      prompt_hash              : string
      prompt_korean_char_ratio : float
      routing_algorithm        : string
      routing_reason           : string
      target                   : string
      latency_ms               : float
      fallback_used            : bool
      model_version            : string
      task_type                : string option
      timestamp                : DateTimeOffset }

/// Injected into the endpoint. Enqueues a log entry; never blocks the hot request path.
type IDecisionLogger =
    abstract member Log : DecisionLog -> unit
