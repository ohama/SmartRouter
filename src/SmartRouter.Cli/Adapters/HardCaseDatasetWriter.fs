module SmartRouter.Cli.Adapters.HardCaseDatasetWriter

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open SmartRouter.Core.RetrainingPorts

/// Cli-only options bound from appsettings.json "HardCaseDataset" section.
[<CLIMutable>]
type HardCaseDatasetOptions =
    { Path            : string   // default "datasets/hard-cases.jsonl"
      ChannelCapacity : int }    // default 1000

/// Channel-backed BackgroundService that drains HardCaseEntry items and appends
/// them to datasets/hard-cases.jsonl. Mirrors DecisionLogWriter pattern.
///
/// Stub at Wave 1 — Plan 07-04 replaces AppendAsync + ExecuteAsync + StopAsync
/// bodies with the real Channel + single-writer logic. Constructor signature is
/// final: (options: HardCaseDatasetOptions). Inherits BackgroundService so the DI
/// registration AddHostedService<HardCaseDatasetWriter> in Plan 07-05 works.
type HardCaseDatasetWriter(options: HardCaseDatasetOptions) =
    inherit BackgroundService()

    interface IHardCaseDatasetWriter with
        member _.AppendAsync(_entry: HardCaseEntry, _ct: CancellationToken) : Task<unit> =
            raise (NotImplementedException "HardCaseDatasetWriter.AppendAsync is a Wave 1 stub; Plan 07-04 fills this in")

    override _.ExecuteAsync(_stoppingToken: CancellationToken) : Task =
        // Stub returns immediately — no Channel, no consumer loop. Plan 07-04 replaces.
        Task.CompletedTask
