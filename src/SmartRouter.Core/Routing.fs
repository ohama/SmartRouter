module SmartRouter.Core.Routing

open SmartRouter.Core.Domain

// ── Stage 1: explicit model override ─────────────────────────────────────────

/// Parses a model alias string to ModelId.
/// Returns Some ModelId on match, None on unknown alias.
/// Pure; no mutation.
let tryParseModelAlias (s: string) : ModelId option =
    match s.ToLowerInvariant() with
    | "35b" | "qwen35b" | "qwen-35b" -> Some Qwen35B
    | "122b" | "qwen122b" | "qwen-122b" -> Some Qwen122B
    | _ -> None

/// Stage 1: if the request carries a recognizable model alias, return a
/// decision immediately. Short-circuits stages 2 and 3.
let tryModelOverride (req: RouterRequest) : RoutingDecision option =
    req.ModelOverride
    |> Option.bind tryParseModelAlias
    |> Option.map (fun model ->
        { Target     = model
          Priority   = Low
          Reason     = ExplicitModelOverride (req.ModelOverride |> Option.defaultValue "")
          IsFallback = false })

// ── Stage 2: explicit task table ─────────────────────────────────────────────

/// Parse raw task string to TaskType. Returns None on unknown task.
/// Case-insensitive. Pure; no mutation. NO catch-all on DU.
let tryParseTaskType (raw: string) : TaskType option =
    match raw.ToLowerInvariant() with
    | "graph_indexing"        -> Some GraphIndexing
    | "compiler_debug"        -> Some CompilerDebug
    | "architecture_analysis" -> Some ArchitectureAnalysis
    | "dependency_analysis"   -> Some DependencyAnalysis
    | "reasoning"             -> Some Reasoning
    | "retrieval"             -> Some Retrieval
    | "summary"               -> Some Summary
    | _                       -> None

/// Exhaustive task→routing-decision table.
/// COMPILE-TIME CONTRACT: must cover every TaskType DU case. NEVER add | _ ->.
/// NOT called by routeRequest at runtime — it is a validation/test utility only.
/// Referenced by canonicalTaskTable (which tests use) and by the plan 01-03
/// startup validator that diffs the JSON-loaded TaskTable against this baseline.
let taskToDecision : TaskType -> RoutingDecision =
    function
    | GraphIndexing ->
        { Target = Qwen122B; Priority = High
          Reason = ExplicitTask GraphIndexing; IsFallback = false }
    | CompilerDebug ->
        { Target = Qwen122B; Priority = High
          Reason = ExplicitTask CompilerDebug; IsFallback = false }
    | ArchitectureAnalysis ->
        { Target = Qwen122B; Priority = High
          Reason = ExplicitTask ArchitectureAnalysis; IsFallback = false }
    | DependencyAnalysis ->
        { Target = Qwen122B; Priority = Low
          Reason = ExplicitTask DependencyAnalysis; IsFallback = false }
    | Reasoning ->
        { Target = Qwen122B; Priority = Low
          Reason = ExplicitTask Reasoning; IsFallback = false }
    | Retrieval ->
        { Target = Qwen35B; Priority = Low
          Reason = ExplicitTask Retrieval; IsFallback = false }
    | Summary ->
        { Target = Qwen35B; Priority = Low
          Reason = ExplicitTask Summary; IsFallback = false }

/// Stage 2: config-driven runtime dispatch.
/// Reads config.TaskTable (a Map<string, ModelId * Priority>) keyed by
/// the lowercased raw task string. taskToDecision is NOT called here.
///
/// - req.Task = None → Ok None (fall through to heuristic)
/// - req.Task = Some raw, unknown string → Error (UnsupportedTask raw)
/// - req.Task = Some raw, known string, in config map → Ok (Some decision)
/// - req.Task = Some raw, known string, NOT in config map → Error (InvalidRequest ...)
let tryTaskTable (config: RoutingConfig) (req: RouterRequest) : Result<RoutingDecision option, RouterError> =
    match req.Task with
    | None -> Ok None
    | Some raw ->
        match tryParseTaskType raw with
        | None -> Error (UnsupportedTask raw)
        | Some t ->
            match Map.tryFind (raw.ToLowerInvariant()) config.TaskTable with
            | Some (model, prio) ->
                Ok (Some { Target = model; Priority = prio; Reason = ExplicitTask t; IsFallback = false })
            | None ->
                Error (InvalidRequest $"task '{raw}' has no config entry")

// ── Stage 3: heuristic ───────────────────────────────────────────────────────

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

// ── Pipeline entry point ──────────────────────────────────────────────────────

/// Three-stage pure routing pipeline parameterized by RoutingConfig.
/// Returns Ok RoutingDecision or Error RouterError.
/// No IO. No logging. No clock.
/// Signature: RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError>
let routeRequest (config: RoutingConfig) (req: RouterRequest) : Result<RoutingDecision, RouterError> =
    match tryModelOverride req with
    | Some decision -> Ok decision
    | None ->
        match tryTaskTable config req with
        | Error e          -> Error e
        | Ok (Some decision) -> Ok decision
        | Ok None          -> Ok (applyHeuristic config req)

// ── Helpers for tests + startup validation ────────────────────────────────────

/// Canonical task table derived from the exhaustive taskToDecision match.
/// Used by (a) tests as a known-good RoutingConfig, (b) plan 01-03's startup
/// validator as the baseline to diff against the JSON-loaded TaskTable.
let canonicalTaskTable : Map<string, ModelId * Priority> =
    [ "graph_indexing"        , taskToDecision GraphIndexing        |> fun d -> d.Target, d.Priority
      "compiler_debug"        , taskToDecision CompilerDebug        |> fun d -> d.Target, d.Priority
      "architecture_analysis" , taskToDecision ArchitectureAnalysis |> fun d -> d.Target, d.Priority
      "dependency_analysis"   , taskToDecision DependencyAnalysis   |> fun d -> d.Target, d.Priority
      "reasoning"             , taskToDecision Reasoning            |> fun d -> d.Target, d.Priority
      "retrieval"             , taskToDecision Retrieval            |> fun d -> d.Target, d.Priority
      "summary"               , taskToDecision Summary              |> fun d -> d.Target, d.Priority ]
    |> Map.ofList

let canonicalKeywords : string list =
    [ "recursive"; "dependency"; "lowering"; "mlir"; "llvm"; "compiler"
      "architecture"; "type inference"; "graph relation"; "closure conversion"
      "cross-file"; "multi-file"; "reasoning"; "inference"; "optimization"
      "refactor"; "redesign"; "abstract"; "formal"; "proof" ]

/// Default RoutingConfig (used by tests + as the baseline the JSON validator diffs against).
let defaultRoutingConfig : RoutingConfig =
    { ComplexityThreshold = 3
      Keywords            = canonicalKeywords
      TaskTable           = canonicalTaskTable }
