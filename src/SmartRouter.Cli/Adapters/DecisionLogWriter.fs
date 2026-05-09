module SmartRouter.Cli.Adapters.DecisionLogWriter

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SmartRouter.Cli.Adapters.DecisionLogger

/// Options — bound from appsettings.json "DecisionLog" section.
/// Defaults applied at registration time in CompositionRoot.
[<CLIMutable>]
type DecisionLogOptions =
    { Directory      : string   // default "logs/decisions"
      ChannelCapacity : int }   // default 10000

/// BackgroundService that drains Channel<DecisionLog> and writes to YYYY-MM-DD.jsonl.
/// Single consumer — no locking on file handle. Rotates file on UTC date change.
///
/// Channel is bounded with BoundedChannelFullMode.DropWrite — newest entry dropped and
/// a Serilog warning emitted to stderr. Never blocks the request hot path.
type DecisionLogWriter(options: DecisionLogOptions, logger: ILogger<DecisionLogWriter>) =
    inherit BackgroundService()

    // Bounded channel — DropWrite on overflow (per LOG-02 constraint).
    // SingleWriter = false: many endpoint tasks produce concurrently.
    // SingleReader = true: only this BackgroundService consumes.
    let channel =
        Channel.CreateBounded<DecisionLog>(
            BoundedChannelOptions(
                options.ChannelCapacity,
                FullMode    = BoundedChannelFullMode.DropWrite,
                SingleWriter = false,
                SingleReader = true))

    // JSON options for JSONL serialization.
    // PropertyNamingPolicy.SnakeCaseLower: PascalCase F# names → snake_case JSON keys.
    // FSharp.SystemTextJson converter: handles string option → null (Pitfall P10).
    let jsonOpts =
        let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
        o.Converters.Add(JsonFSharpConverter())
        o

    /// Enqueue without blocking. Called from the hot request path.
    /// (Pitfall P4: log warning on overflow — never silent.)
    member _.Enqueue(entry: DecisionLog) : unit =
        if not (channel.Writer.TryWrite(entry)) then
            logger.LogWarning(
                "decision log channel full; dropped 1 decision; check disk I/O (correlation_id={CorrelationId})",
                entry.correlation_id)

    interface IDecisionLogger with
        member this.Log(entry) = this.Enqueue(entry)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            Directory.CreateDirectory(options.Directory) |> ignore

            let mutable currentDate = DateTime.MinValue
            let mutable writer : StreamWriter option = None

            // Open (or rotate to) a new dated file. Closes any open writer first.
            let openWriter (date: DateTime) =
                writer |> Option.iter (fun w -> try w.Flush(); w.Dispose() with _ -> ())
                let path = Path.Combine(options.Directory, date.ToString("yyyy-MM-dd") + ".jsonl")
                let sw = new StreamWriter(path, append = true, encoding = Encoding.UTF8)
                sw.AutoFlush <- false   // explicit per-line flush for atomicity (lines under PIPE_BUF)
                currentDate <- date
                writer <- Some sw
                sw

            // Main consumer loop — reads one entry at a time.
            // (Pitfall P2: stoppingToken passed to ReadAsync — throws OperationCanceledException on shutdown.)
            try
                while not stoppingToken.IsCancellationRequested do
                    let! entry = channel.Reader.ReadAsync(stoppingToken)
                    let today = DateTime.UtcNow.Date
                    let sw =
                        if today <> currentDate then openWriter today
                        else
                            match writer with
                            | Some w -> w
                            | None   -> openWriter today
                    try
                        let line = JsonSerializer.Serialize(entry, jsonOpts)
                        sw.WriteLine(line)
                        sw.Flush()   // per-line OS write; atomic for short lines under PIPE_BUF
                    with ex ->
                        logger.LogError(ex, "DecisionLogWriter: write failed for correlation_id={Cid}", entry.correlation_id)
            with
            | :? OperationCanceledException -> ()   // graceful shutdown via stoppingToken
            | :? ChannelClosedException     -> ()   // graceful shutdown via TryComplete()
            | ex -> logger.LogError(ex, "DecisionLogWriter: writer loop crashed")

            // Drain remaining items after cancellation (Pitfall P2 — flush in-flight entries).
            let mutable more = true
            while more do
                match channel.Reader.TryRead() with
                | true, entry ->
                    try
                        let today = DateTime.UtcNow.Date
                        let sw =
                            if today <> currentDate then openWriter today
                            else match writer with Some w -> w | None -> openWriter today
                        sw.WriteLine(JsonSerializer.Serialize(entry, jsonOpts))
                        sw.Flush()
                    with ex ->
                        logger.LogWarning(ex, "DecisionLogWriter: drain write failed")
                | false, _ -> more <- false

            // Dispose the StreamWriter cleanly (Pitfall P3 — no handle leak).
            writer |> Option.iter (fun w -> try w.Flush() with _ -> (); w.Dispose())
        }

    /// Signal the channel writer as complete — unblocks ReadAsync after drain.
    override _.StopAsync(cancellationToken: CancellationToken) =
        channel.Writer.TryComplete() |> ignore
        base.StopAsync(cancellationToken)
