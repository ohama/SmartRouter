module SmartRouter.Cli.Adapters.CanaryState

open System

/// In-memory state for the canary deployment.
///   Percentage: 0..100; gate short-circuits to false when 0.
///   IsRolledBack: true after the most recent SetPercentage(0); cleared by SetPercentage(>0).
///   LastRollback: (timestamp, reason) pair recorded on the last SetPercentage(0) transition.
type ICanaryState =
    abstract member GetPercentage     : unit -> int
    abstract member SetPercentage     : percentage: int * reason: string -> unit
    abstract member IsRolledBack      : bool with get
    abstract member LastRollbackAt    : DateTimeOffset option with get
    abstract member LastRollbackReason: string option with get

/// Lock-guarded mutable state. Single shared `lock gate` — SemaphoreSlim is overkill
/// for synchronous reads/writes (no async I/O inside the critical section).
type CanaryState(initialPercentage: int) =
    let mutable percentage     = max 0 (min 100 initialPercentage)
    let mutable rolledBack     = (initialPercentage <= 0)
    let mutable lastRollbackAt : DateTimeOffset option = None
    let mutable lastReason     : string option         = None
    let gate = obj()

    interface ICanaryState with
        member _.GetPercentage() =
            lock gate (fun () -> percentage)
        member _.SetPercentage(newPct: int, reason: string) =
            let clamped = max 0 (min 100 newPct)
            lock gate (fun () ->
                percentage <- clamped
                if clamped <= 0 then
                    rolledBack     <- true
                    lastRollbackAt <- Some DateTimeOffset.UtcNow
                    lastReason     <- Some reason
                else
                    rolledBack <- false)
        member _.IsRolledBack       = lock gate (fun () -> rolledBack)
        member _.LastRollbackAt     = lock gate (fun () -> lastRollbackAt)
        member _.LastRollbackReason = lock gate (fun () -> lastReason)
