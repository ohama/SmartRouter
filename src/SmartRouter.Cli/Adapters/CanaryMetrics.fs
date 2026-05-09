module SmartRouter.Cli.Adapters.CanaryMetrics

open System
open System.Collections.Concurrent

/// Per-cohort rolling fallback rate metric. Lock-free producer (ConcurrentQueue.Enqueue);
/// consumer trims-by-age then snapshots (ToArray) for the rate calculation. RESEARCH §7.1.
type ICanaryMetrics =
    abstract member Record       : isCanary: bool * isFallback: bool -> unit
    abstract member FallbackRate : isCanary: bool * windowSeconds: float -> float * int
    /// Returns (rate, sampleCount). sampleCount is the number of events in the rolling window AFTER trim.

type private RollingCounter() =
    let queue = ConcurrentQueue<struct (DateTimeOffset * bool)>()

    member _.Record(isFallback: bool) =
        queue.Enqueue(struct (DateTimeOffset.UtcNow, isFallback))

    /// Trim entries older than (now - windowSeconds), then snapshot and compute rate.
    member _.RateAndCount(windowSeconds: float) : float * int =
        let cutoff = DateTimeOffset.UtcNow.AddSeconds(-windowSeconds)
        let mutable keep_trimming = true
        while keep_trimming do
            match queue.TryPeek() with
            | true, struct (ts, _) when ts < cutoff ->
                queue.TryDequeue() |> ignore
            | _ ->
                keep_trimming <- false
        let entries = queue.ToArray()
        if entries.Length = 0 then 0.0, 0
        else
            let fallbacks =
                entries
                |> Array.filter (fun struct (_, fb) -> fb)
                |> Array.length
            (float fallbacks / float entries.Length), entries.Length

type CanaryMetrics() =
    let baseline = RollingCounter()
    let canary   = RollingCounter()

    interface ICanaryMetrics with
        member _.Record(isCanary, isFallback) =
            if isCanary then canary.Record(isFallback)
            else baseline.Record(isFallback)
        member _.FallbackRate(isCanary, windowSeconds) =
            if isCanary then canary.RateAndCount(windowSeconds)
            else baseline.RateAndCount(windowSeconds)

/// No-op ICanaryMetrics — TryAddSingleton fallback for contexts without canary
/// infrastructure (offline retrain path, tests). Record is a swallow; FallbackRate
/// returns (0.0, 0). This lets ChatCompletions resolve ICanaryMetrics universally —
/// the production CanaryMetrics overrides this via plain AddSingleton inside
/// configureRequestPipeline (TryAddSingleton fallback + AddSingleton override pattern;
/// Microsoft.Extensions.DI last-registration-wins semantics for GetRequiredService<T>).
type NoOpCanaryMetrics() =
    interface ICanaryMetrics with
        member _.Record(_, _)             = ()
        member _.FallbackRate(_, _)       = 0.0, 0
