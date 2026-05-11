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
        { Target       = model
          Priority     = Low
          Reason       = ExplicitModelOverride (req.ModelOverride |> Option.defaultValue "")
          IsFallback   = false
          ModelVersion = "" })

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
          Reason = ExplicitTask GraphIndexing; IsFallback = false; ModelVersion = "" }
    | CompilerDebug ->
        { Target = Qwen122B; Priority = High
          Reason = ExplicitTask CompilerDebug; IsFallback = false; ModelVersion = "" }
    | ArchitectureAnalysis ->
        { Target = Qwen122B; Priority = High
          Reason = ExplicitTask ArchitectureAnalysis; IsFallback = false; ModelVersion = "" }
    | DependencyAnalysis ->
        { Target = Qwen122B; Priority = Low
          Reason = ExplicitTask DependencyAnalysis; IsFallback = false; ModelVersion = "" }
    | Reasoning ->
        { Target = Qwen122B; Priority = Low
          Reason = ExplicitTask Reasoning; IsFallback = false; ModelVersion = "" }
    | Retrieval ->
        { Target = Qwen35B; Priority = Low
          Reason = ExplicitTask Retrieval; IsFallback = false; ModelVersion = "" }
    | Summary ->
        { Target = Qwen35B; Priority = Low
          Reason = ExplicitTask Summary; IsFallback = false; ModelVersion = "" }

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
                Ok (Some { Target = model; Priority = prio; Reason = ExplicitTask t; IsFallback = false; ModelVersion = "" })
            | None ->
                Error (InvalidRequest $"task '{raw}' has no config entry")

// ── Pipeline entry point ──────────────────────────────────────────────────────

/// Four-stage pure routing pipeline parameterized by RoutingConfig and a
/// pluggable algorithm (ML.applyML; future implementations conform to RoutingAlgorithm shape).
/// Returns Ok RoutingDecision or Error RouterError.
/// No IO. No logging. No clock.
/// Signature: RoutingConfig -> RoutingAlgorithm -> RouterRequest -> Result<RoutingDecision, RouterError>
let routeRequest
    (config    : RoutingConfig)
    (algorithm : RoutingAlgorithm)
    (req       : RouterRequest)
    : Result<RoutingDecision, RouterError> =
    // ── Stage 0: Hard Rules ───────────────────────────────────────────────────
    // Phase 17 (HR-03): keyword scan; bypasses ALL other stages.
    // Per STATE.md decision 5, Hard Rules wins over model override (HR-06 wording in
    // REQUIREMENTS.md is fixed in Plan 17-03). Safety mechanism: a request with
    // `model=35b` AND prompt containing "LLVM" still routes to 122B.
    match HardRules.applyHardRules req with
    | Some decision -> Ok decision
    | None ->
    // ── Stage 1: explicit model override ─────────────────────────────────────
    match tryModelOverride req with
    | Some decision -> Ok decision
    | None ->
        // ── Stage 2: explicit task table ─────────────────────────────────────
        match tryTaskTable config req with
        | Error e            -> Error e
        | Ok (Some decision) -> Ok decision
        | Ok None            -> Ok (algorithm config req)   // Stage 3 (algorithm)

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

/// Default RoutingConfig (used by tests + as the baseline the JSON validator diffs against).
let defaultRoutingConfig : RoutingConfig =
    { TaskTable   = canonicalTaskTable
      MlThreshold = 0.5f }
