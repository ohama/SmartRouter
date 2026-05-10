module SmartRouter.Cli.Adapters.ColdStart

open System
open System.IO
open Microsoft.Extensions.Logging

/// Phase 14 — `--cold-start` CLI flag handler.
///
/// Behavior: rename existing model and training-data files with a timestamp suffix
/// so the next startup phase (ensureDummyModel) sees no router.zip and generates a
/// fresh dummy model. Idempotent — files that don't exist are skipped, NOT errored.
///
/// Recovery: operator manually `mv` the backup file back to its original name and
/// restart the router.
///
/// rootDir: typically `Environment.CurrentDirectory`. Resolves the four candidate
/// paths relative to it. Production launchd path is the install dir; dev paths are
/// the repo root or src/SmartRouter.Cli/ (depending on how `dotnet run` was invoked).
let runColdStartBackup (logger: ILogger) (rootDir: string) : unit =
    let timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
    let candidates =
        [ Path.Combine(rootDir, "models/router.zip")
          Path.Combine(rootDir, "models/router.zip.prev")
          Path.Combine(rootDir, "datasets/hard-cases.jsonl")
          Path.Combine(rootDir, "datasets/training-set.jsonl") ]
    let backedUp =
        candidates
        |> List.choose (fun src ->
            if File.Exists(src) then
                let dst = sprintf "%s.cold-start-backup-%s" src timestamp
                File.Move(src, dst)
                Some dst
            else
                None)
    if List.isEmpty backedUp then
        logger.LogInformation(
            "Cold-start: no existing model or dataset files to backup; ensureDummyModel will generate a fresh router.zip on this startup")
    else
        logger.LogInformation(
            "Cold-start: backed up {Count} file(s) with timestamp={Timestamp}; files=[{Files}]",
            backedUp.Length,
            timestamp,
            String.concat ", " backedUp)
