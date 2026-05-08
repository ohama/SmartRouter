module SmartRouter.Core.Heuristic

open SmartRouter.Core.Domain

/// Compute a numeric complexity score from the request.
/// Reads config.Keywords and config.ComplexityThreshold.
/// Pure: deterministic from input alone.
let scoreComplexity (config: RoutingConfig) (req: RouterRequest) : int =
    let allText =
        req.Messages
        |> List.map (fun m -> m.Content)
        |> String.concat " "
        |> fun s -> s.ToLowerInvariant()

    let totalChars = allText.Length
    let msgCount   = req.Messages |> List.length
    let hasCode    = allText.Contains("```")

    let keywordScore =
        config.Keywords
        |> List.filter allText.Contains
        |> List.length

    let lengthScore =
        if   totalChars > 8000 then 4
        elif totalChars > 4000 then 2
        elif totalChars > 2000 then 1
        else 0

    let msgScore  = if msgCount > 6 then 2 elif msgCount > 3 then 1 else 0
    let codeScore = if hasCode then 1 else 0

    keywordScore + lengthScore + msgScore + codeScore

/// Stage 3: config-driven heuristic routing.
/// Reads config.ComplexityThreshold (no hardcoded literal).
/// score >= threshold → 122B Low; else → 35B Low. Tie → 35B (latency-first).
let applyHeuristic (config: RoutingConfig) (req: RouterRequest) : RoutingDecision =
    let score  = scoreComplexity config req
    let target = if score >= config.ComplexityThreshold then Qwen122B else Qwen35B
    { Target     = target
      Priority   = Low
      Reason     = Heuristic score
      IsFallback = false }
