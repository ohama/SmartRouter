module SmartRouter.Cli.Adapters.HardCaseDatasetWriter

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SmartRouter.Core.RetrainingPorts

/// Cli-only options bound from appsettings.json "HardCaseDataset" section.
[<CLIMutable>]
type HardCaseDatasetOptions =
    { Path            : string   // default "datasets/hard-cases.jsonl"
      ChannelCapacity : int }    // default 1000

/// BackgroundService that drains Channel<HardCaseEntry> and appends to
/// datasets/hard-cases.jsonl. Single consumer — no locking on file handle.
///
/// Mirrors DecisionLogWriter (Phase 5) pattern with three diffs:
///   1. Single fixed-path file (no daily rotation — training datasets accumulate forever).
///   2. BoundedChannelFullMode.Wait (NOT DropWrite) — losing training data is unacceptable;
///      back-pressure is fine because writes are infrequent (one per labeled hard case).
///   3. Append-time dedupe by (CorrelationId, PromptHash) via in-memory HashSet seeded
///      from existing file at first iteration. Prevents double-labeling on rerun.
type HardCaseDatasetWriter(options: HardCaseDatasetOptions, logger: ILogger<HardCaseDatasetWriter>) =
    inherit BackgroundService()

    // Defensive defaults — CompositionRoot in Plan 07-05 also applies these,
    // but defending here lets ad-hoc construction (e.g., scripts) work without options.
    let path =
        if String.IsNullOrWhiteSpace(options.Path) then "datasets/hard-cases.jsonl"
        else options.Path

    let capacity =
        if options.ChannelCapacity <= 0 then 1000
        else options.ChannelCapacity

    // Bounded channel — Wait on overflow (per CONTEXT.md FAIL-04 design).
    // SingleWriter = false: many producers may call AppendAsync concurrently.
    // SingleReader = true: only this BackgroundService consumes.
    let channel =
        Channel.CreateBounded<HardCaseEntry>(
            BoundedChannelOptions(
                capacity,
                FullMode    = BoundedChannelFullMode.Wait,   // diff #2 from DecisionLogWriter
                SingleWriter = false,
                SingleReader = true))

    // JSON options for JSONL serialization. Same shape as DecisionLogWriter:
    //   - SnakeCaseLower: PascalCase F# fields → snake_case JSON keys.
    //   - JsonFSharpConverter: handles `string option` (TeacherResponseExcerpt) → null.
    let jsonOpts =
        let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
        o.Converters.Add(JsonFSharpConverter())
        o

    /// Build the dedupe key from an entry — kept opaque (string) so the HashSet
    /// is BCL-only and uses default string hashing.
    let dedupeKey (entry: HardCaseEntry) : string =
        sprintf "%s|%s" entry.CorrelationId entry.PromptHash

    /// Seed the dedupe HashSet from an existing hard-cases.jsonl file (if present).
    /// Called once at start of ExecuteAsync. Malformed lines are logged-and-skipped.
    let seedDedupe (set: HashSet<string>) : unit =
        if File.Exists(path) then
            try
                File.ReadAllLines(path)
                |> Array.iter (fun line ->
                    if not (String.IsNullOrWhiteSpace(line)) then
                        try
                            let entry = JsonSerializer.Deserialize<HardCaseEntry>(line, jsonOpts)
                            set.Add(dedupeKey entry) |> ignore
                        with ex ->
                            logger.LogWarning(ex, "HardCaseDatasetWriter: skipping malformed line during seed"))
            with ex ->
                logger.LogWarning(ex, "HardCaseDatasetWriter: failed to seed dedupe from {Path}", path)

    /// AppendAsync — fire-and-forget from the producer's perspective. Returns
    /// after the entry is written into the channel (back-pressure honored on Wait).
    /// The BackgroundService consumer flushes to disk on its own loop.
    interface IHardCaseDatasetWriter with
        member _.AppendAsync(entry: HardCaseEntry, ct: CancellationToken) : Task<unit> =
            task {
                do! channel.Writer.WriteAsync(entry, ct).AsTask()
            }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // Ensure parent directory exists before any open. Idempotent.
            let parent = Path.GetDirectoryName(path)
            if not (String.IsNullOrEmpty(parent)) then
                Directory.CreateDirectory(parent) |> ignore

            // Seed dedupe set from existing file (rerun-safety per FAIL-04).
            let dedupe = HashSet<string>()
            seedDedupe dedupe
            logger.LogInformation(
                "HardCaseDatasetWriter: seeded dedupe set with {N} existing entries from {Path}",
                dedupe.Count, path)

            let mutable writer : StreamWriter option = None
            // Open the writer with FileShare.None for single-writer guarantee.
            let openWriter () =
                writer |> Option.iter (fun w -> try w.Flush(); w.Dispose() with _ -> ())
                let stream =
                    new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.None)
                let sw = new StreamWriter(stream, Encoding.UTF8)
                sw.AutoFlush <- false   // explicit per-line flush for atomicity
                writer <- Some sw
                sw

            // Lazy: open on first write so an empty consumer never touches the file.
            let getWriter () =
                match writer with
                | Some w -> w
                | None   -> openWriter ()

            try
                while not stoppingToken.IsCancellationRequested do
                    let! entry = channel.Reader.ReadAsync(stoppingToken)
                    let key = dedupeKey entry
                    if dedupe.Contains(key) then
                        logger.LogDebug(
                            "HardCaseDatasetWriter: dedupe hit; skipping correlation_id={Cid} prompt_hash={Hash}",
                            entry.CorrelationId, entry.PromptHash)
                    else
                        let sw = getWriter ()
                        try
                            let line = JsonSerializer.Serialize(entry, jsonOpts)
                            sw.WriteLine(line)
                            sw.Flush()   // per-line OS write; atomic for short lines under PIPE_BUF
                            dedupe.Add(key) |> ignore
                        with ex ->
                            logger.LogError(ex, "HardCaseDatasetWriter: write failed for correlation_id={Cid}", entry.CorrelationId)
            with
            | :? OperationCanceledException -> ()   // graceful shutdown via stoppingToken
            | :? ChannelClosedException     -> ()   // graceful shutdown via TryComplete()
            | ex -> logger.LogError(ex, "HardCaseDatasetWriter: writer loop crashed")

            // Drain remaining items after cancellation (Pitfall P2 mirror).
            let mutable more = true
            while more do
                match channel.Reader.TryRead() with
                | true, entry ->
                    let key = dedupeKey entry
                    if dedupe.Contains(key) then ()
                    else
                        try
                            let sw = getWriter ()
                            sw.WriteLine(JsonSerializer.Serialize(entry, jsonOpts))
                            sw.Flush()
                            dedupe.Add(key) |> ignore
                        with ex ->
                            logger.LogWarning(ex, "HardCaseDatasetWriter: drain write failed")
                | false, _ -> more <- false

            // Dispose the StreamWriter cleanly (Pitfall P3 — no handle leak).
            // NOTE: F# parsing pitfall: `fun w -> try w.Flush() with _ -> (); w.Dispose()`
            // would only call Dispose() in the exception arm (semicolon binds inside with-clause).
            // Explicit multi-line form ensures Dispose() is always called regardless of Flush result.
            writer |> Option.iter (fun w ->
                try w.Flush() with _ -> ()
                try w.Dispose() with _ -> ())
        }

    /// Signal end-of-stream so ExecuteAsync's drain phase runs.
    override _.StopAsync(cancellationToken: CancellationToken) =
        channel.Writer.TryComplete() |> ignore
        base.StopAsync(cancellationToken)
