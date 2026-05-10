module SmartRouter.Cli.Adapters.TraceLogger

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// Phase 14 — trace JSONL row for end-to-end request tracing.
/// Prompt UID = first 12 hex of prompt_hash (Phase 5 LOG-01).
/// 16 fields total:
///   - 12 from Phase 14 (schema_version..timestamp)
///   - bad_reason (Phase 15, additive)
///   - judge_called / judge_verdict / judge_latency_ms (Phase 16, additive)
/// schema_version = 1 unchanged — additive-only extension per JDG-05.
[<CLIMutable>]
type TraceRecord = {
    [<JsonPropertyName("schema_version")>]
    schema_version              : int
    [<JsonPropertyName("correlation_id")>]
    correlation_id              : string
    [<JsonPropertyName("prompt_uid")>]
    prompt_uid                  : string
    [<JsonPropertyName("prompt_hash")>]
    prompt_hash                 : string
    [<JsonPropertyName("prompt_excerpt")>]
    prompt_excerpt              : string
    [<JsonPropertyName("initial_target")>]
    initial_target              : string
    [<JsonPropertyName("initial_response_excerpt")>]
    initial_response_excerpt    : string option
    [<JsonPropertyName("fallback_kind")>]
    fallback_kind               : string option
    [<JsonPropertyName("final_target")>]
    final_target                : string
    [<JsonPropertyName("final_response_excerpt")>]
    final_response_excerpt      : string
    [<JsonPropertyName("total_latency_ms")>]
    total_latency_ms            : float
    [<JsonPropertyName("timestamp")>]
    timestamp                   : DateTimeOffset
    [<JsonPropertyName("bad_reason")>]
    bad_reason                  : string option   // NEW Phase 15 — null on Good, "tag=value" on Bad
    [<JsonPropertyName("judge_called")>]
    judge_called                : bool            // NEW Phase 16 — true when judge was invoked
    [<JsonPropertyName("judge_verdict")>]
    judge_verdict               : string option   // NEW Phase 16 — "yes" | "no" | null
    [<JsonPropertyName("judge_latency_ms")>]
    judge_latency_ms            : float option    // NEW Phase 16 — null when judge not called
}

/// Options bound from the CompositionRoot Configure<TraceLoggerOptions> action.
/// Fields are mutable so the Configure<T>(Action<T>) mutation pattern works in F#.
[<CLIMutable>]
type TraceLoggerOptions = {
    mutable Directory       : string   // default "logs/trace"
    mutable ChannelCapacity : int      // default 1000
}

type ITraceLogger =
    /// Append a trace record via the internal Channel. Blocks under back-pressure
    /// (BoundedChannelFullMode.Wait); call sites are the request hot path after
    /// final response is sent, so brief blocking is acceptable.
    abstract member Log : TraceRecord -> unit

/// Channel + BackgroundService single-writer mirror of DecisionLogWriter.
/// Daily file rotation by UTC date in filename (logs/trace/YYYY-MM-DD.jsonl).
/// FileShare.None exclusive append per line — same as DecisionLogWriter pattern.
///
/// BoundedChannelFullMode.Wait: back-pressure rather than dropping records.
/// (DecisionLogWriter uses DropWrite because log drops are more tolerable;
///  trace records are lower-volume so Wait is the safer default here.)
type TraceLogger(opts: IOptions<TraceLoggerOptions>, logger: ILogger<TraceLogger>) =
    inherit BackgroundService()

    let options = opts.Value
    let directory =
        if String.IsNullOrWhiteSpace(options.Directory) then "logs/trace"
        else options.Directory
    let capacity =
        if options.ChannelCapacity <= 0 then 1000
        else options.ChannelCapacity

    let channel =
        let chanOpts = BoundedChannelOptions(capacity)
        chanOpts.FullMode    <- BoundedChannelFullMode.Wait
        chanOpts.SingleReader <- true
        chanOpts.SingleWriter <- false
        Channel.CreateBounded<TraceRecord>(chanOpts)

    // JSON options for JSONL serialization.
    // PropertyNamingPolicy.SnakeCaseLower: PascalCase F# names -> snake_case JSON keys.
    // JsonFSharpConverter: handles string option -> null/value (mirrors DecisionLogWriter).
    let jsonOpts =
        let o = JsonSerializerOptions(JsonSerializerDefaults.General)
        o.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        o.Converters.Add(JsonFSharpConverter())
        o

    let pathForToday () =
        let date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
        Path.Combine(directory, sprintf "%s.jsonl" date)

    let writeOne (record: TraceRecord) =
        Directory.CreateDirectory(directory) |> ignore
        let line = JsonSerializer.Serialize(record, jsonOpts)
        let path = pathForToday ()
        use stream =
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None)
        use writer = new StreamWriter(stream)
        writer.WriteLine(line)

    interface ITraceLogger with
        /// Enqueue a trace record. Blocks if the channel is full (Wait mode).
        /// Called after the response is fully written — brief blocking acceptable.
        member _.Log(record: TraceRecord) =
            channel.Writer.WriteAsync(record).AsTask().GetAwaiter().GetResult()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    let! hasItem = channel.Reader.WaitToReadAsync(stoppingToken).AsTask()
                    if hasItem then
                        let mutable record = Unchecked.defaultof<TraceRecord>
                        while channel.Reader.TryRead(&record) do
                            try
                                writeOne record
                            with ex ->
                                logger.LogError(
                                    ex,
                                    "TraceLogger: write failed for correlation_id={Cid}",
                                    record.correlation_id)
            with
            | :? OperationCanceledException -> ()
            | :? ChannelClosedException     -> ()
            | ex -> logger.LogError(ex, "TraceLogger: writer loop crashed")
        } :> Task

    override this.StopAsync(ct: CancellationToken) =
        // Signal channel writer as complete — unblocks WaitToReadAsync in ExecuteAsync.
        channel.Writer.TryComplete() |> ignore
        // Drain any records already in the channel before returning.
        let mutable record = Unchecked.defaultof<TraceRecord>
        while channel.Reader.TryRead(&record) do
            try
                writeOne record
            with ex ->
                logger.LogWarning(ex, "TraceLogger: drain write failed")
        // Delegate to BackgroundService.StopAsync to signal the hosted service lifecycle.
        base.StopAsync(ct)
