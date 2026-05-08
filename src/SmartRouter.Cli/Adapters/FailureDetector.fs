module SmartRouter.Cli.Adapters.FailureDetector

open System
open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.RetrainingPorts

/// Reads logs/decisions/*.jsonl files, filters fallback_used=true records,
/// and returns hard-case entries. FAIL-01.
///
/// Stub at Wave 1 — Plan 07-02 replaces ExtractHardCasesAsync body with the
/// real JSONL reader. Constructor signature is final: (logsDirectory: string).
type FailureDetector(logsDirectory: string) =

    interface IFailureDetector with
        member _.ExtractHardCases(_ct: CancellationToken) : Task<HardCase list> =
            raise (NotImplementedException "FailureDetector.ExtractHardCases is a Wave 1 stub; Plan 07-02 fills this in")
