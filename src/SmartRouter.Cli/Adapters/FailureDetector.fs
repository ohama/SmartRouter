module SmartRouter.Cli.Adapters.FailureDetector

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.DecisionLogger

/// Reads logs/decisions/*.jsonl files, filters fallback_used=true records,
/// returns hard-case correlation_ids and surrounding metadata. FAIL-01.
///
/// Pure-ish — file I/O is the only side effect. Returns Task<HardCase list>
/// (not IAsyncEnumerable) — JSONL files are small (one line per request,
/// hundreds per day at v1 scale).
///
/// Phase 10 forward-link: when fallback_used flips to true, this code immediately
/// produces real hard cases — no Phase 7 changes needed. Until then, the empty
/// result is logged at Information level so Phase 8's empty-loop is visible.
type FailureDetector(logsDirectory: string, logger: ILogger<FailureDetector>) =

    // JSON options must match DecisionLogWriter's serializer:
    //   - PropertyNamingPolicy.SnakeCaseLower → matches snake_case JSONL field names
    //   - JsonFSharpConverter → handles `string option` (task_type field) round-trip
    let jsonOpts =
        let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
        o.Converters.Add(JsonFSharpConverter())
        o

    let toHardCase (entry: DecisionLog) : HardCase =
        { CorrelationId         = entry.correlation_id
          PromptHash            = entry.prompt_hash
          PromptKoreanCharRatio = entry.prompt_korean_char_ratio
          RoutingAlgorithm      = entry.routing_algorithm
          Target                = entry.target
          // PromptText: LOG-01 stores prompt_hash only (privacy decision). The CLI
          // --retrain offline path cannot recover prompt text from logs; Phase 8's
          // in-process BackgroundService will set this from RouterRequest.Messages,
          // and the synthetic seed script writes Some directly to hard-cases.jsonl.
          // The detector itself returns None — downstream callers handle the gap.
          PromptText            = None }

    // Parse one JSONL line. Returns None for blank lines, malformed JSON, or
    // non-fallback entries. Errors are logged at Warning (not Error) — a single
    // bad line should not derail the entire scan.
    let tryParseLine (line: string) : HardCase option =
        if String.IsNullOrWhiteSpace(line) then None
        else
            try
                let entry = JsonSerializer.Deserialize<DecisionLog>(line, jsonOpts)
                if entry.fallback_used then Some (toHardCase entry)
                else None
            with ex ->
                logger.LogWarning(ex, "FailureDetector: skipping malformed JSONL line in {Dir}", logsDirectory)
                None

    interface IFailureDetector with
        member _.ExtractHardCases(_ct: CancellationToken) : Task<HardCase list> =
            task {
                if not (Directory.Exists logsDirectory) then
                    logger.LogInformation(
                        "FailureDetector: directory {Dir} does not exist; returning 0 hard cases",
                        logsDirectory)
                    return []
                else
                    let files = Directory.GetFiles(logsDirectory, "*.jsonl")
                    let hardCases =
                        files
                        |> Array.toList
                        |> List.collect (fun path ->
                            try
                                File.ReadAllLines(path)
                                |> Array.toList
                                |> List.choose tryParseLine
                            with ex ->
                                logger.LogWarning(ex, "FailureDetector: failed to read {Path}", path)
                                [])

                    if List.isEmpty hardCases then
                        logger.LogInformation(
                            "FailureDetector: 0 hard cases found in {Dir} across {N} JSONL file(s); fallback_used is always false until Phase 10 ships. Run scripts/seed-hard-cases.fsx to seed synthetic data.",
                            logsDirectory, files.Length)
                    else
                        logger.LogInformation(
                            "FailureDetector: extracted {Count} hard case(s) from {N} JSONL file(s) in {Dir}",
                            List.length hardCases, files.Length, logsDirectory)

                    return hardCases
            }
