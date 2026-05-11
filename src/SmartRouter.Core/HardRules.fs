module SmartRouter.Core.HardRules

open SmartRouter.Core.Domain

/// Default keyword list (HR-02: hardcoded, NOT operator-configurable).
/// Safety mechanism — operators cannot misconfigure via appsettings.json.
/// Source: .planning/docs/35b-selfrouting.md §6,12.
/// Case-insensitive match — keywords are ASCII so OrdinalIgnoreCase is correct.
let private keywords =
    [ "LLVM"; "MLIR"; "compiler"; "segfault"; "optimization"; "concurrency" ]

/// Stage 0 pre-routing: case-insensitive keyword scan over all message content.
/// Returns Some (Qwen122B, High, HardRule) if any keyword matches; None otherwise.
/// Pure function — no IO, no DI, no mutable state. Synchronous (no task {}).
/// Applies to both streaming and non-streaming requests (HR-05): the cost is
/// O(prompt_length * 6) String.Contains comparisons; ~0ms for typical prompts.
let applyHardRules (req: RouterRequest) : RoutingDecision option =
    let combined =
        req.Messages
        |> List.map (fun m -> m.Content)
        |> String.concat " "
    let matched =
        keywords
        |> List.exists (fun kw ->
            combined.Contains(kw, System.StringComparison.OrdinalIgnoreCase))
    if matched then
        Some { Target       = Qwen122B
               Priority     = High
               Reason       = HardRule
               IsFallback   = false
               ModelVersion = "" }
    else
        None
