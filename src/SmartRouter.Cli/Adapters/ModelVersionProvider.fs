module SmartRouter.Cli.Adapters.ModelVersionProvider

open System
open SmartRouter.Core.RetrainingPorts

/// Mutable container for the live model_version string.
/// Singleton in DI. RetrainingService.runRetrain calls Update after each
/// successful router.zip write so DecisionLog.model_version flips without restart.
///
/// Why a class with `lock`: F#'s `mutable` field on a `let-bound` value would not
/// be reachable through an interface dispatch. Wrapping in a class gives us a
/// stable reference identity for DI and a private lock object for the field.
/// String reads/writes are atomic on .NET, but `lock` here is defensive against
/// future field-shape changes (e.g., adding a timestamp would break torn-write safety).
type ModelVersionProvider(initial: string) =
    let mutable current = initial
    let gate = obj()

    interface IModelVersionProvider with
        member _.CurrentVersion =
            lock gate (fun () -> current)
        member _.Update(newVersion: string) =
            lock gate (fun () -> current <- newVersion)
