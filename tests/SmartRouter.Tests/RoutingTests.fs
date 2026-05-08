module SmartRouter.Tests.RoutingTests

open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Core.Heuristic
open SmartRouter.Core.Routing

/// Default RoutingConfig for tests — mirrors the canonical task table + threshold=3 +
/// canonical keyword list (the same values appsettings.json ships with). Tests that need
/// a non-default config (e.g., to verify operator-edited TaskTable changes runtime
/// behavior) build their own RoutingConfig inline.
let private defaultConfig : RoutingConfig = defaultRoutingConfig

let private mkReq (task: string option) (model: string option) (content: string) (msgs: int) : RouterRequest =
    let messages =
        [ for _ in 1 .. msgs ->
            { Role = User; Content = content } ]
    { Messages      = messages
      ModelOverride  = model
      Task           = task
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      UnknownFields  = Map.empty }

/// Convenience: route with the default config. Every test in this suite uses this
/// unless it's specifically testing config-driven behavior.
let private route req = routeRequest defaultConfig applyHeuristic req

let tests =
    testList "routing" [

        // ── ROUT-01: explicit model override short-circuits ──────────
        testCase "model override 35b routes to Qwen35B with ExplicitModelOverride reason" <| fun () ->
            let req = mkReq None (Some "35b") "anything" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen35B "Target"
                match d.Reason with
                | ExplicitModelOverride alias -> Expect.equal alias "35b" "alias preserved"
                | r -> failtestf "wrong reason: %A" r
            | Error e -> failtestf "expected Ok, got Error %A" e

        testCase "model override 122b routes to Qwen122B" <| fun () ->
            let req = mkReq None (Some "122b") "x" 1
            match route req with
            | Ok d -> Expect.equal d.Target Qwen122B ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "model override aliases are case-insensitive" <| fun () ->
            let req = mkReq None (Some "QWEN-122B") "x" 1
            match route req with
            | Ok d -> Expect.equal d.Target Qwen122B ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "unknown model alias falls through to next stage (no error)" <| fun () ->
            // model=gpt-4o + task=retrieval → task table wins
            let req = mkReq (Some "retrieval") (Some "gpt-4o") "x" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen35B ""
                match d.Reason with
                | ExplicitTask Retrieval -> ()
                | r -> failtestf "expected ExplicitTask Retrieval, got %A" r
            | Error e -> failtestf "expected Ok, got %A" e

        // ── ROUT-02: explicit task table ──────────────────────────────
        testCase "task graph_indexing → Qwen122B High priority" <| fun () ->
            let req = mkReq (Some "graph_indexing") None "x" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen122B "Target"
                Expect.equal d.Priority High "Priority"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "task compiler_debug → Qwen122B High" <| fun () ->
            let req = mkReq (Some "compiler_debug") None "x" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen122B ""
                Expect.equal d.Priority High ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "task architecture_analysis → Qwen122B High" <| fun () ->
            let req = mkReq (Some "architecture_analysis") None "x" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen122B ""
                Expect.equal d.Priority High ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "task dependency_analysis → Qwen122B Low" <| fun () ->
            let req = mkReq (Some "dependency_analysis") None "x" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen122B ""
                Expect.equal d.Priority Low ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "task reasoning → Qwen122B Low" <| fun () ->
            let req = mkReq (Some "reasoning") None "x" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen122B ""
                Expect.equal d.Priority Low ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "task retrieval → Qwen35B" <| fun () ->
            let req = mkReq (Some "retrieval") None "x" 1
            match route req with
            | Ok d -> Expect.equal d.Target Qwen35B ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "task summary → Qwen35B" <| fun () ->
            let req = mkReq (Some "summary") None "x" 1
            match route req with
            | Ok d -> Expect.equal d.Target Qwen35B ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "task name is case-insensitive" <| fun () ->
            let req = mkReq (Some "GRAPH_INDEXING") None "x" 1
            match route req with
            | Ok d -> Expect.equal d.Target Qwen122B ""
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "unknown task returns UnsupportedTask error" <| fun () ->
            let req = mkReq (Some "foobar") None "x" 1
            Expect.equal (route req) (Error (UnsupportedTask "foobar")) ""

        // ── ROUT-03 + ROUT-04: heuristic fallback (35B-biased) ────────
        testCase "short hello-world → Qwen35B (heuristic, score below threshold)" <| fun () ->
            let req = mkReq None None "hello" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen35B ""
                match d.Reason with
                | Heuristic s -> Expect.isLessThan s 3 "score < threshold"
                | r -> failtestf "expected Heuristic, got %A" r
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "long complex prompt with keywords → Qwen122B (heuristic, score >= 3)" <| fun () ->
            // 500 * 44 chars ≈ 22000 chars → length tier +4; 4 keywords → +4; total 8 ≥ 3
            let longText = String.replicate 500 "recursive compiler architecture dependency "
            let req = mkReq None None longText 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen122B ""
                match d.Reason with
                | Heuristic s -> Expect.isGreaterThanOrEqual s 3 "score >= threshold"
                | r -> failtestf "expected Heuristic, got %A" r
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "code block (triple backtick) contributes +1 to heuristic score" <| fun () ->
            let withBackticks = "```\nfoo\n```"
            let withoutBackticks = "foo"
            let s1 = scoreComplexity defaultConfig (mkReq None None withBackticks 1)
            let s2 = scoreComplexity defaultConfig (mkReq None None withoutBackticks 1)
            Expect.equal (s1 - s2) 1 "code-block contributes exactly +1"

        // ── ROUT-05: config-driven dispatch (proves runtime path reads config) ──
        testCase "config-driven TaskTable: moving 'retrieval' to Qwen122B reroutes to 122B at runtime" <| fun () ->
            // Operator-edit simulation: take the default config and remap retrieval to 122B.
            // This is the unit-test counterpart to the Phase 1-03 live verification command
            // (edit appsettings.json, restart, observe new routing). Proves runtime dispatch
            // reads the config map, not a hardcoded F# match.
            let edited =
                { defaultConfig with
                    TaskTable = defaultConfig.TaskTable |> Map.add "retrieval" (Qwen122B, Low) }
            let req = mkReq (Some "retrieval") None "x" 1
            match routeRequest edited applyHeuristic req with
            | Ok d ->
                Expect.equal d.Target Qwen122B "retrieval should now route to 122B per edited config"
                match d.Reason with
                | ExplicitTask Retrieval -> ()
                | r -> failtestf "expected ExplicitTask Retrieval, got %A" r
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "config-driven threshold: lowering threshold to 1 promotes a single keyword to 122B" <| fun () ->
            let edited = { defaultConfig with ComplexityThreshold = 1 }
            let req = mkReq None None "recursive" 1   // 1 keyword → score 1
            match routeRequest edited applyHeuristic req with
            | Ok d -> Expect.equal d.Target Qwen122B "score >= threshold(1) should escalate"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "config-driven keywords: empty keyword list neutralizes heuristic" <| fun () ->
            let edited = { defaultConfig with Keywords = [] }
            let req = mkReq None None "recursive compiler architecture dependency" 1
            match routeRequest edited applyHeuristic req with
            | Ok d -> Expect.equal d.Target Qwen35B "no keyword hits → score 0 → 35B"
            | Error e -> failtestf "expected Ok, got %A" e

        // ── Override precedence (ROUT-01 beats ROUT-02 beats ROUT-03) ─
        testCase "model override beats task field" <| fun () ->
            let req = mkReq (Some "graph_indexing") (Some "35b") "x" 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen35B "model override should win"
                match d.Reason with
                | ExplicitModelOverride _ -> ()
                | r -> failtestf "expected ExplicitModelOverride, got %A" r
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "task beats heuristic (long prompt + retrieval task → 35B not 122B)" <| fun () ->
            let longText = String.replicate 500 "recursive compiler architecture dependency "
            let req = mkReq (Some "retrieval") None longText 1
            match route req with
            | Ok d ->
                Expect.equal d.Target Qwen35B "task table should beat heuristic"
                match d.Reason with
                | ExplicitTask Retrieval -> ()
                | r -> failtestf "expected ExplicitTask Retrieval, got %A" r
            | Error e -> failtestf "expected Ok, got %A" e

        // ── Tie-break (latency-first) ─────────────────────────────────
        testCase "score exactly at threshold-minus-one routes to 35B (latency-first bias)" <| fun () ->
            // tie-break: scores below 3 → 35B; verifies the < not <= boundary
            let twoKeywords = "recursive dependency"  // 2 keywords, short → score 2
            let req = mkReq None None twoKeywords 1
            match route req with
            | Ok d -> Expect.equal d.Target Qwen35B ""
            | Error e -> failtestf "expected Ok, got %A" e
    ]
