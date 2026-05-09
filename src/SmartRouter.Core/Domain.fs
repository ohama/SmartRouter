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
    | Heuristic             of score: int
    | Default
    | ML
    | FallbackTo35B   // NEW Phase 10: 122B unavailable, rerouted to 35B

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
      UnknownFields  : Map<string, System.Text.Json.JsonElement> }

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
    { ComplexityThreshold : int
      Keywords            : string list
      TaskTable           : Map<string, ModelId * Priority>
      /// ML routing threshold: P(Qwen122B) ≥ this value → 122B (Phase 6).
      /// Heuristic algorithm ignores this field; only ML reads it.
      MlThreshold         : float32 }

/// Function type for pluggable routing algorithms.
/// Both Heuristic.applyHeuristic and ML.applyML conform to this shape.
/// Returns RoutingDecision (NOT Result) — error paths owned by tryTaskTable upstream.
type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision
