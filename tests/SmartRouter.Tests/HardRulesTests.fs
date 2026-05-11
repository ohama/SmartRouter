module SmartRouter.Tests.HardRulesTests

open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Core.HardRules
open SmartRouter.Core.Routing
open SmartRouter.Cli.Adapters.DecisionLogger

/// Helper: minimal RouterRequest with no model override, no task, no streaming.
/// Mirrors the construction pattern used in MLRoutingTests.fs.
let private mkReq content =
    { Messages       = [ { Role = User; Content = content } ]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      SessionId      = ""
      UnknownFields  = Map.empty }

/// Helper: stub algorithm that always returns Qwen35B/Default — used to verify
/// Stage 0 (Hard Rules) fires BEFORE Stage 3 (algorithm) is reached.
let private stubAlgorithm : RoutingAlgorithm =
    fun _config _req ->
        { Target = Qwen35B; Priority = Low; Reason = Default
          IsFallback = false; ModelVersion = "" }

let tests : Test =
    testList "HardRulesTests" [

        // ── Pure applyHardRules tests (HR-01, HR-02, HR-06 keyword coverage) ──

        testCase "LLVM keyword triggers HardRule → 122B" <| fun () ->
            let result = applyHardRules (mkReq "debug LLVM pass")
            Expect.isSome result "should match LLVM"
            let d = result.Value
            Expect.equal d.Target Qwen122B "target = 122B"
            Expect.equal d.Reason HardRule "reason = HardRule"
            Expect.equal d.Priority High "priority = High"
            Expect.isFalse d.IsFallback "Hard Rule is not a fallback"

        testCase "MLIR keyword triggers HardRule" <| fun () ->
            let result = applyHardRules (mkReq "MLIR dialect lowering")
            Expect.isSome result "should match MLIR"

        testCase "compiler keyword triggers HardRule" <| fun () ->
            let result = applyHardRules (mkReq "fix compiler error")
            Expect.isSome result "should match compiler"

        testCase "segfault keyword triggers HardRule" <| fun () ->
            let result = applyHardRules (mkReq "diagnose segfault in C++")
            Expect.isSome result "should match segfault"

        testCase "optimization keyword triggers HardRule" <| fun () ->
            let result = applyHardRules (mkReq "loop optimization techniques")
            Expect.isSome result "should match optimization"

        testCase "concurrency keyword triggers HardRule" <| fun () ->
            let result = applyHardRules (mkReq "concurrency bugs in Go")
            Expect.isSome result "should match concurrency"

        // ── Case-insensitivity (HR-06) ──

        testCase "lowercase llvm matches (case-insensitive)" <| fun () ->
            Expect.isSome (applyHardRules (mkReq "debug llvm pass")) "lowercase should match"

        testCase "mixed-case Compiler matches" <| fun () ->
            Expect.isSome (applyHardRules (mkReq "Compiler internal error")) "mixed case should match"

        testCase "ALL CAPS SEGFAULT matches" <| fun () ->
            Expect.isSome (applyHardRules (mkReq "SEGFAULT in kernel module")) "uppercase should match"

        // ── No-match passthrough (HR-01 negative) ──

        testCase "no keyword returns None — cascade continues" <| fun () ->
            Expect.isNone (applyHardRules (mkReq "what is 2+2")) "no match → None"

        testCase "empty content returns None" <| fun () ->
            Expect.isNone (applyHardRules (mkReq "")) "empty → None"

        testCase "keyword in second message also triggers" <| fun () ->
            let req = { mkReq "hello" with
                          Messages = [ { Role = User; Content = "hello" }
                                       { Role = Assistant; Content = "Sure, talk about LLVM?" } ] }
            Expect.isSome (applyHardRules req) "concatenated message content scanned"

        // ── Cascade ordering tests via routeRequest (HR-03 + resolved HR-06) ──
        // Per STATE.md decision 5: Hard Rules (Stage 0) wins over model override (Stage 1)
        // and task table (Stage 2). HR-06's "explicit override bypasses Hard Rules" is a
        // misworded requirement — the actual locked behavior is Hard Rules wins.

        testCase "Hard Rule beats model override (Stage 0 > Stage 1)" <| fun () ->
            let req = { mkReq "LLVM optimization" with ModelOverride = Some "35b" }
            match routeRequest defaultRoutingConfig stubAlgorithm req with
            | Ok d ->
                Expect.equal d.Target Qwen122B "Hard Rule must win — target = 122B not 35B"
                Expect.equal d.Reason HardRule "reason = HardRule not ExplicitModelOverride"
            | Error e -> failtestf "expected Ok, got Error %A" e

        testCase "Hard Rule beats explicit task (Stage 0 > Stage 2)" <| fun () ->
            let req = { mkReq "MLIR debugging" with Task = Some "retrieval" }
            match routeRequest defaultRoutingConfig stubAlgorithm req with
            | Ok d ->
                Expect.equal d.Target Qwen122B "Hard Rule must win over retrieval task"
                Expect.equal d.Reason HardRule "reason = HardRule not ExplicitTask"
            | Error e -> failtestf "expected Ok, got Error %A" e

        testCase "non-matching request falls through to algorithm (Stage 0 returns None)" <| fun () ->
            let req = mkReq "hello world"
            match routeRequest defaultRoutingConfig stubAlgorithm req with
            | Ok d ->
                Expect.equal d.Target Qwen35B "stub algorithm returns 35B"
                Expect.equal d.Reason Default "stub algorithm returns Default"
            | Error e -> failtestf "expected Ok, got Error %A" e

        // ── formatReason 7th arm (HR-04) ──

        testCase "DecisionLogger.formatReason emits hard_rule for HardRule" <| fun () ->
            let s = formatReason HardRule
            Expect.equal s "hard_rule" "DecisionLog string for HardRule"

    ]
