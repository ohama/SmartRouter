module SmartRouter.Cli.Adapters.ModelVersionProvider

open System
open SmartRouter.Core.RetrainingPorts

/// Mutable container for live model_version strings.
/// Singleton in DI. RetrainingService.runRetrain calls Update after each successful
/// router.zip write so DecisionLog.model_version flips without restart.
/// Phase 9: CanaryService updates CanaryVersion when models/router-canary.zip is
/// detected (file watcher event or startup scan).
///
/// Single shared `lock gate` for all four members — string field reads/writes are
/// atomic on .NET, but the lock is defensive against future field-shape changes.
type ModelVersionProvider(initial: string) =
    let mutable current = initial
    let mutable canary  = ""
    let gate = obj()

    interface IModelVersionProvider with
        member _.CurrentVersion =
            lock gate (fun () -> current)
        member _.CanaryVersion =
            lock gate (fun () -> canary)
        member _.Update(newVersion: string) =
            lock gate (fun () -> current <- newVersion)
        member _.UpdateCanary(newVersion: string) =
            lock gate (fun () -> canary <- newVersion)
