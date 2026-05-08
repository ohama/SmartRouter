module SmartRouter.Cli.Adapters.RetrainLock

open System
open System.Threading

/// Shared lock between Phase 8 RetrainingService and Phase 9 CanaryService.
/// Both write models/router.zip; the SemaphoreSlim(1, 1) prevents the race
/// (Promote vs Retrain — RESEARCH §11 Pitfall 6, CONTEXT.md Lock 6).
///
/// TryAcquire(0): non-blocking try. Returns Some IDisposable on success (caller's
/// `use _ = ...` releases on scope exit) or None on contention (caller decides:
/// RetrainingService skips with Warning log; CanaryService returns HTTP 409).
type IRetrainLock =
    abstract member TryAcquire : timeoutMs: int -> IDisposable option

type RetrainLock() =
    let semaphore = new SemaphoreSlim(1, 1)

    interface IRetrainLock with
        member _.TryAcquire(timeoutMs: int) : IDisposable option =
            if semaphore.Wait(timeoutMs) then
                Some ({ new IDisposable with
                        member _.Dispose() = semaphore.Release() |> ignore })
            else
                None

    interface IDisposable with
        member _.Dispose() = semaphore.Dispose()
