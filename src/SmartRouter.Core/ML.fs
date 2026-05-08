module SmartRouter.Core.ML

open SmartRouter.Core.Domain

/// Placeholder ML routing algorithm.
/// Phase 4: intentionally dumb — always picks Qwen35B with ML reason.
/// The value of this phase is the dispatch seam, not the algorithm.
/// Phase 6 will replace this with a real embedder + classifier.
/// MUST NOT import the Heuristic module (zero cross-imports enforced by ML-04).
let applyML (config: RoutingConfig) (req: RouterRequest) : RoutingDecision =
    ignore config
    ignore req
    { Target     = Qwen35B
      Priority   = Low
      Reason     = ML
      IsFallback = false }
