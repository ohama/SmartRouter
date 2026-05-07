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
    abstract member CompleteAsync :
        req    : RouterRequest
        -> target : ModelId
        -> ct     : CancellationToken
        -> Task<Result<string, RouterError>>

    /// Streaming call: returns a sequence of raw SSE chunks as strings.
    /// Sequence is lazy — each element is read as it arrives from the upstream server.
    /// The endpoint handler writes each chunk to HttpContext.Response as it arrives.
    abstract member StreamAsync :
        req    : RouterRequest
        -> target : ModelId
        -> ct     : CancellationToken
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
    abstract member IsReachableAsync :
        target : ModelId
        -> ct   : CancellationToken
        -> Task<bool>
