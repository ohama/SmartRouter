module SmartRouter.Core.Domain

/// The two local Qwen models the router can target.
/// DU forces exhaustive match — adding a third model is a compile error in
/// all downstream functions until they handle the new case.
type ModelId =
    | Qwen35B   // localhost:8000, fast, lower quality
    | Qwen122B  // localhost:8001, slow, higher quality; concurrency cap = 1

/// Request priority for the 122B queue.
/// High: graph_indexing, compiler_debug, architecture_analysis
/// Low: dependency_analysis, reasoning, and heuristic-routed requests
type Priority =
    | High
    | Low

/// Graphify task identifiers. DU membership is the authoritative task list.
/// Adding a task requires updating the routing table in Routing.fs — exhaustive match.
type TaskType =
    | GraphIndexing
    | CompilerDebug
    | ArchitectureAnalysis
    | DependencyAnalysis
    | Reasoning
    | Retrieval
    | Summary

/// Why the routing decision was made. Carried in RoutingDecision for logging
/// and stats; purely informational — adapters log it, Core produces it.
/// Note: RoutingReason references TaskType, so TaskType must be declared first.
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Default
    | ML
    | FallbackTo35B   // Phase 10: 122B unavailable, rerouted to 35B
    | FallbackTo122B  // Phase 14: 35B response failed quality check, retried on 122B
    | HardRule        // Phase 17: keyword match (LLVM/MLIR/compiler/segfault/optimization/concurrency) → immediate 122B
    | StickyEscalation // Phase 18: session previously routed to 122B → continuation also routes to 122B

/// LLM wire message (same shape as blueCode; needed by IUpstreamClient port).
type MessageRole = System | User | Assistant

type Message = { Role: MessageRole; Content: string }

/// Incoming request from a consumer (Hermes / Graphify).
/// All fields are optional except Messages.
/// CorrelationId is "" for internal/non-HTTP paths; ChatCompletions sets it from
/// HttpContext.Items[CorrelationIdKey]. ML.fs canary gate consumes it for sticky bucketing.
/// UnknownFields carries any unrecognized JSON keys so the adapter can
/// forward them upstream verbatim (PROJECT.md: "Preserve unknown fields").
type RouterRequest =
    { Messages       : Message list
      ModelOverride  : string option   // "35b" | "122b" | any alias
      Task           : string option   // raw task string; Routing.fs parses to TaskType
      Stream         : bool
      Temperature    : float option
      TopP           : float option
      MaxTokens      : int option
      CorrelationId  : string          // NEW (Phase 9): "" for non-HTTP construction; per-request HttpContext correlation_id otherwise
      SessionId      : string          // Phase 18: "" = stateless (no session, sticky skipped); set from X-Session-Id header in CorrelationMiddleware
      UnknownFields  : Map<string, System.Text.Json.JsonElement> }

/// Phase 18 — session state carried across requests sharing an X-Session-Id header.
/// Stored in-memory by SmartRouter.Cli.Adapters.SessionStore (18-02). BCL-only here
/// so the Phase 19 self-routing algorithm closure can pattern-match on LastModel
/// without dragging Cli dependencies into Core (ARCH-01).
///
/// LastAccessSeq is mutable because the SessionStore updates LRU ordering in-place
/// via Interlocked.Increment on every read (mirrors Phase 16 JudgeClient.CacheEntry).
/// Mutation is performed in the Cli adapter, not in Core — ARCH-01 preserved.
type SessionState =
    { LastModel       : ModelId                    // Qwen35B | Qwen122B — last model that served the session
      LastAccessedAt  : System.DateTimeOffset      // UTC; used for TTL eviction by SessionTtlEvictionService (18-03)
      mutable LastAccessSeq : int64 }              // LRU ordering; bumped by SessionStore.TryGet on every read

/// Errors the Core routing layer can produce.
/// Does NOT include HTTP errors — those are adapter-side (UpstreamError).
type RouterError =
    | InvalidRequest    of detail: string
    | UnsupportedTask   of raw: string
    | ModelUnavailable  of ModelId * detail: string
    | GraphIndexingMustFail   // graph_indexing with 122B unavailable: loud fail required

/// Complete routing decision returned by Core.
/// Priority is included because it is a *property of the decision*, not an
/// adapter concern. The QueueDispatcher reads Priority to place the request
/// in the correct queue tier. Core decides Priority; adapter enforces it.
/// ModelVersion (Phase 9): cohort label.
///   ""                — non-ML stage (override / task table / heuristic); ChatCompletions falls back to IModelVersionProvider.CurrentVersion
///   "ml-{sha8}"       — ML baseline cohort
///   "ml-{sha8}-canary" — ML canary cohort
type RoutingDecision =
    { Target       : ModelId
      Priority     : Priority
      Reason       : RoutingReason
      /// true = 122B unavailable and we fell back to 35B.
      /// false = normal routing.
      /// Never true for graph_indexing (that path must error).
      IsFallback   : bool
      ModelVersion : string }   // NEW (Phase 9)

/// Operator-tunable routing configuration.
/// Plain F# record — no IOptions<T>, no ASP.NET, no JSON binding.
/// The Cli layer (plan 01-03) constructs this from appsettings.json and
/// injects it into routeRequest at composition time (ARCH-01 preserved).
type RoutingConfig =
    { TaskTable   : Map<string, ModelId * Priority>
      /// ML routing threshold: P(Qwen122B) ≥ this value → 122B (Phase 6).
      /// ML reads this; routes to 122B when P >= MlThreshold.
      MlThreshold : float32 }

/// Function type for pluggable routing algorithms.
/// ML.applyML conforms to this shape.
/// Returns RoutingDecision (NOT Result) — error paths owned by tryTaskTable upstream.
type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision
