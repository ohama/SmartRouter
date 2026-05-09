module SmartRouter.Core.Ports

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.Domain

/// Upstream LLM server contract.
/// The adapter layer implements this; Core only calls it via RoutingDecision.Target.
/// For non-streaming calls, CompleteAsync returns the full body string.
/// For streaming calls, StreamAsync returns an async sequence of SSE chunks.
/// HttpResponseMessage never crosses this boundary (ARCH-05).
type IUpstreamClient =
    /// Non-streaming call: returns the full response body string.
    /// Takes the full RoutingDecision so adapters that wrap this port
    /// (QueueDispatcher) can dispatch on Target + Priority without a separate
    /// interface. Adapters that only care about Target read decision.Target.
    abstract member CompleteAsync :
        req      : RouterRequest
        -> decision : RoutingDecision
        -> ct       : CancellationToken
        -> Task<Result<string, RouterError>>

    /// Streaming call. Same RoutingDecision parameter — adapters wrap this
    /// to gate on decision.Target / decision.Priority.
    /// Sequence is lazy — each element is read as it arrives from the upstream server.
    /// The endpoint handler writes each chunk to HttpContext.Response as it arrives.
    abstract member StreamAsync :
        req      : RouterRequest
        -> decision : RoutingDecision
        -> ct       : CancellationToken
        -> IAsyncEnumerable<Result<string, RouterError>>

/// Clock abstraction — needed by Stats adapter to record timestamps.
/// Core does not currently call IClock, but it is defined here so adapters
/// can depend on it via DI without touching System.DateTime directly.
type IClock =
    abstract member UtcNow : unit -> DateTimeOffset

/// Upstream health probe — called by HealthAdapter, not by Core routing.
/// Defined in Ports.fs so it can be injected into the Health endpoint
/// without creating a dependency on the adapter assembly.
type IHealthProbe =
    /// Synchronous fast-path: reads cached probe state. No IO.
    /// Used by QueueDispatcher and ChatCompletions on the request hot path —
    /// blocking on a Task here would deadlock under load.
    abstract member IsReachable :
        target : ModelId
        -> bool

    /// Async variant for callers that prefer async context.
    abstract member IsReachableAsync :
        target : ModelId
        -> ct   : CancellationToken
        -> Task<bool>

    /// Timestamp of last probe attempt. Returns DateTimeOffset.MinValue if
    /// the probe has never run (startup grace period).
    abstract member LastProbedAt :
        target : ModelId
        -> DateTimeOffset
