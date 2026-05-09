module SmartRouter.Cli.Adapters.LogRetentionService

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

[<CLIMutable>]
type LogRetentionOptions =
    { mutable OperationalDirectory    : string
      mutable OperationalRetentionDays: int
      mutable DecisionDirectory       : string
      mutable DecisionRetentionDays   : int
      mutable DatasetsDirectory       : string
      mutable TeacherCapRetentionDays : int
      mutable PollIntervalMinutes     : int }

type LogRetentionService(opts: IOptions<LogRetentionOptions>, logger: ILogger<LogRetentionService>) =
    inherit BackgroundService()

    // Parse YYYY-MM-DD (dashed) from a filename. Returns Some date or None.
    let datePatternDashed = Regex(@"(\d{4})-(\d{2})-(\d{2})", RegexOptions.Compiled)

    // Parse YYYYMMDD (Serilog rolling compact) from a filename. Returns Some date or None.
    let datePatternCompact = Regex(@"(\d{4})(\d{2})(\d{2})", RegexOptions.Compiled)

    let tryParseDate (pattern: Regex) (groupCount: int) (filename: string) : DateTimeOffset option =
        let m = pattern.Match(filename)
        if m.Success then
            try
                let y  = int m.Groups.[1].Value
                let mo = int m.Groups.[2].Value
                let d  = int m.Groups.[3].Value
                Some(DateTimeOffset(DateTime(y, mo, d), TimeSpan.Zero))
            with _ -> None
        else None

    /// Try dashed format first, then compact — operational logs use compact (YYYYMMDD);
    /// decision/dataset files use dashed (YYYY-MM-DD).
    let tryParseAnyDate (filename: string) : DateTimeOffset option =
        tryParseDate datePatternDashed 3 filename
        |> Option.orElseWith (fun () -> tryParseDate datePatternCompact 3 filename)

    let pruneFiles (dir: string) (pattern: string) (retainDays: int) =
        if Directory.Exists(dir) then
            let cutoff = DateTimeOffset.UtcNow.AddDays(-float retainDays)
            for path in Directory.EnumerateFiles(dir, pattern) do
                let name = Path.GetFileName(path)
                match tryParseAnyDate name with
                | Some d when d < cutoff ->
                    try
                        File.Delete(path)
                        logger.LogInformation(
                            "LogRetentionService: pruned {Path} (file_date={Date:yyyy-MM-dd}, cutoff={Cutoff:yyyy-MM-dd})",
                            path, d, cutoff)
                    with ex ->
                        logger.LogWarning(ex, "LogRetentionService: failed to delete {Path}", path)
                | _ -> ()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let o = opts.Value
            let interval = TimeSpan.FromMinutes(float o.PollIntervalMinutes)
            use timer = new PeriodicTimer(interval)
            try
                // Run once immediately on startup, then on each periodic tick.
                let runOnce () =
                    pruneFiles o.OperationalDirectory "smart-router-*.log" o.OperationalRetentionDays
                    pruneFiles o.DecisionDirectory    "*.jsonl"            o.DecisionRetentionDays
                    pruneFiles o.DatasetsDirectory    "teacher-cap-*.json" o.TeacherCapRetentionDays
                runOnce ()
                while not stoppingToken.IsCancellationRequested do
                    let! _ = timer.WaitForNextTickAsync(stoppingToken).AsTask()
                    if not stoppingToken.IsCancellationRequested then
                        runOnce ()
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogError(ex, "LogRetentionService: outer loop crashed")
        } :> Task

    override _.StopAsync(ct: CancellationToken) =
        logger.LogInformation("LogRetentionService stopping")
        base.StopAsync(ct)
