module SmartRouter.Tests.HermesSessionExtractTests

open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Cli.Adapters.HermesSessionExtract

// ── Test helpers ──────────────────────────────────────────────────────────

/// Build a RouterRequest with a single System message containing `content`.
/// All other fields default to the v2.0 stateless shape (no model override,
/// no task, no streaming, empty SessionId, empty UnknownFields).
let private mkReqWithSystem (systemContent: string) : RouterRequest =
    { Messages       = [ { Role = System; Content = systemContent } ]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      SessionId      = ""
      UnknownFields  = Map.empty }

/// Build a RouterRequest with no System message (User-only conversation).
let private mkReqUserOnly (userContent: string) : RouterRequest =
    { Messages       = [ { Role = User; Content = userContent } ]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      SessionId      = ""
      UnknownFields  = Map.empty }

/// Realistic Hermes emission: timestamp line + Session ID line + Model/Provider.
/// Mirrors run_agent.py:5764-5766 emission format exactly.
let private hermesSystemPrompt =
    "Conversation started: 2026-05-12T15:30:00+00:00\n"
    + "Session ID: 20260512T1530_a1b2c3\n"
    + "Model: qwen-35b\n"
    + "Provider: custom"

// ── Tests (HSP-04 (a)-(e) + HSP-03 case-sensitivity guard) ──────────────

let tests : Test =
    testList "HermesSessionExtractTests" [

        // (a) match-when-present — realistic Hermes emission
        testCase "extracts session id when Hermes Session ID line is present" <| fun () ->
            let req = mkReqWithSystem hermesSystemPrompt
            let result = extractFromSystemPrompt req
            Expect.isSome result "should match Session ID line"
            Expect.equal result.Value "20260512T1530_a1b2c3"
                "captured group must be the exact session id"

        // (b) no-match-when-absent — System message but no Session ID line
        testCase "returns None when system message has no Session ID line" <| fun () ->
            let req = mkReqWithSystem "You are a helpful assistant.\nNo session id here."
            Expect.isNone (extractFromSystemPrompt req)
                "no Session ID line → None"

        // (c) no-match-when-no-system-message — User-only conversation
        testCase "returns None when no system message exists" <| fun () ->
            let req = mkReqUserOnly "what is 2+2"
            Expect.isNone (extractFromSystemPrompt req)
                "no System role in Messages → None"

        // (d) multiline-still-matches — Session ID on a non-first line
        // The Hermes emission always puts Session ID on line ≥ 2; this test
        // verifies RegexOptions.Multiline is honored.
        testCase "matches Session ID even when on line 3 of system content" <| fun () ->
            let multilineSystem =
                "First line of system prompt.\n"
                + "Second line filler.\n"
                + "Session ID: 20260512T1530_xyz789\n"
                + "Fourth line trailer."
            let req = mkReqWithSystem multilineSystem
            let result = extractFromSystemPrompt req
            Expect.isSome result "Multiline ^ should match line-start anywhere"
            Expect.equal result.Value "20260512T1530_xyz789"
                "captured group on line 3 must extract correctly"

        // (e) malformed-line-no-value — Session ID: followed by only whitespace
        // \S+ requires ≥1 non-whitespace char; malformed line yields Success=false.
        testCase "returns None when Session ID line has no value after colon" <| fun () ->
            let req = mkReqWithSystem "Conversation started: foo\nSession ID:   \nModel: qwen-35b"
            Expect.isNone (extractFromSystemPrompt req)
                "Session ID: with only whitespace after colon → None"

        // (f) case-sensitivity guard (HSP-03)
        // Researcher Open Question 4 (21-RESEARCH.md): HSP-04 lists 5 cases
        // but no explicit case-insensitive non-match case. Add it here to
        // fully verify HSP-03's "case-sensitive" clause.
        testCase "case-sensitive: lowercase 'session id:' does not match" <| fun () ->
            let req = mkReqWithSystem "Conversation started: foo\nsession id: abc123\nModel: qwen-35b"
            Expect.isNone (extractFromSystemPrompt req)
                "lowercase variant must NOT match (HSP-03 case-sensitive)"
    ]
