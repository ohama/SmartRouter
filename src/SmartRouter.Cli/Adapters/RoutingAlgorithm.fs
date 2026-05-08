module SmartRouter.Cli.Adapters.RoutingAlgorithm

open SmartRouter.Core.Domain

// ── RoutingAlgorithmRegistration ────────────────────────────────────────────
//
// Phase 4 registered a bare RoutingAlgorithm function. Phase 5 needs to know
// which algorithm ran (for DecisionLog.routing_algorithm) and what model_version
// to log (for DecisionLog.model_version). This record carries all three together.
//
// Algorithm   : the chosen function — Heuristic.applyHeuristic OR ML.applyML
//               (RoutingAlgorithm is a function-type alias defined in
//               SmartRouter.Core.Domain — open above brings it into scope.)
// Name        : "heuristic" | "ml" — appears in JSONL routing_algorithm field
// ModelVersion: "heuristic-v1" | "ml-v0-placeholder" — appears in JSONL model_version field
// Phase 6 will redefine ModelVersion for ML to include router.zip's short hash.
//
// This type lives in its own file (rather than inside CompositionRoot.fs) so
// that ChatCompletions.fs (compile pos 14) can reference it: F# compile order
// requires the type's defining module to compile BEFORE every consumer, and
// CompositionRoot.fs sits at compile pos 16 — too late for ChatCompletions.fs.
type RoutingAlgorithmRegistration =
    { Algorithm    : RoutingAlgorithm
      Name         : string
      ModelVersion : string }
