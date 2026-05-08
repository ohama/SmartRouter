---
phase: 05-routing-decision-logging
verified: 2026-05-08T07:32:00Z
status: passed
score: 6/6 success criteria verified
re_verification: false
---

# Phase 5: Routing-Decision Logging Verification Report

**Phase Goal:** Every routing decision emits a structured JSONL log line with all 12 fields Loop B's retrainer needs. Thread-safe Channel-backed writer. Absorbs OBS-01 + OBS-03.
**Verified:** 2026-05-08T07:32:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | After a request completes, `logs/decisions/YYYY-MM-DD.jsonl` contains one line with all 12 fields | ✓ VERIFIED | Test "JSONL line has all 12 schema fields with correct types" passes; DecisionLogger.fs:44-55 has all 12 fields; JsonNamingPolicy.SnakeCaseLower in DecisionLogWriter.fs:44 |
| 2 | 100 concurrent identical requests produce 100 valid JSON lines, no IOException, no interleaved bytes | ✓ VERIFIED | Test "100 concurrent requests produce 100 valid JSON lines" passes (part of 49/49); Channel.CreateBounded + SingleReader=true in DecisionLogWriter.fs:33-38 |
| 3 | Same correlation_id in Serilog stderr, JSONL file, and SSE error body (LOG-04 three sources) | ✓ VERIFIED | Test "correlation_id in JSONL matches correlation_id in Serilog LogEvents" + "SSE error event body contains correlation_id" both pass; ChatCompletions.fs:242-244 includes correlation_id in SSE error |
| 4 | JSONL writer flushes on graceful shutdown (`app.StopAsync`) | ✓ VERIFIED | Test "graceful shutdown drains channel — no in-flight log loss" passes; DecisionLogWriter.fs:120-122 TryComplete + base.StopAsync + drain loop at lines 99-113 |
| 5 | All CI scripts pass | ✓ VERIFIED | check-no-async.sh: EXIT=0; check-routing-isolation.sh: EXIT=0 |
| 6 | Build clean, 49/49 tests pass (44 prior + 5 new), 2 ignored (load opt-in) | ✓ VERIFIED | `dotnet build` 0 warnings 0 errors; binary run: 49 passed, 2 ignored, 0 failed |

**Score:** 6/6 success criteria verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` | DecisionLog record (12 fields), IDecisionLogger interface, SHA-256 hash helper, Korean ratio helper | ✓ VERIFIED | 12 fields at lines 44-55; IDecisionLogger at line 58; computePromptHash at line 11; computeKoreanRatio at line 21 |
| `src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` | BackgroundService + Channel<DecisionLog> + BoundedChannelFullMode.DropWrite + daily UTC rotation + graceful drain | ✓ VERIFIED | `inherit BackgroundService()` line 27; Channel.CreateBounded line 33; DropWrite line 36; DateTime.UtcNow.Date lines 81+105; drain loop lines 99-113; StopAsync lines 120-122 |
| `src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs` | `correlationMiddleware` function, Guid.NewGuid().ToString("N"), HttpContext.Items + LogContext.PushProperty | ✓ VERIFIED | Function named `correlationMiddleware` at line 19; Guid.NewGuid().ToString("N") at line 21; Items at line 22; LogContext.PushProperty at line 23 |
| `src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs` | RoutingAlgorithmRegistration record | ✓ VERIFIED | File exists; `type RoutingAlgorithmRegistration` at line 22 |
| `src/SmartRouter.Cli/CompositionRoot.fs` | AddSingleton<RoutingAlgorithmRegistration>, AddSingleton<RoutingAlgorithm> back-compat alias, AddSingleton<IDecisionLogger>, AddHostedService<DecisionLogWriter> | ✓ VERIFIED | AddSingleton<RoutingAlgorithmRegistration> line 149; AddSingleton<RoutingAlgorithm> line 173; AddSingleton<IDecisionLogger> line 220; AddHostedService<DecisionLogWriter> line 224 (same singleton instance) |
| `src/SmartRouter.Cli/Program.fs` | correlationMiddleware registered BEFORE UseSerilogRequestLogging | ✓ VERIFIED | correlationMiddleware at line 90; UseSerilogRequestLogging at line 93; ORDER_OK confirmed |
| `src/SmartRouter.Cli/appsettings.json` | DecisionLog:Directory key | ✓ VERIFIED | Lines 46-49: `"DecisionLog": { "Directory": "logs/decisions", "ChannelCapacity": 10000 }` |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | ≥7 decisionLogger.Log call sites; ALL SSE error literals include correlation_id; escapeJsonString helper | ✓ VERIFIED | 8 `decisionLogger.Log` calls (>= 7 required); SSE error literal at line 242 includes correlation_id; escapeJsonString at line 88 |
| `tests/SmartRouter.Tests/LoggingTests.fs` | 5 tests, all wrapped in testSequenced, CapturingSink, no Console.SetError | ✓ VERIFIED | 5 testCase entries at lines 305, 384, 431, 480, 513; testSequenced at line 300; CapturingSink at line 30; no Console.SetError found |
| `tests/SmartRouter.Tests/StreamingTests.fs` | DecisionLog:Directory temp-dir override | ✓ VERIFIED | `KeyValuePair("DecisionLog:Directory", streamingTestsLogDir)` at line 160 |
| `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` | LoggingTests.fs in compile order (after StreamingTests.fs, before RouterTests.fs) | ✓ VERIFIED | Line 12: `<Compile Include="LoggingTests.fs" />` between StreamingTests.fs (line 8) and RouterTests.fs (line 13) |
| `tests/SmartRouter.Tests/RouterTests.fs` | rootTests list includes LoggingTests.tests | ✓ VERIFIED | Line 21: `SmartRouter.Tests.LoggingTests.tests` in rootTests |
| `.gitignore` | `logs/` entry | ✓ VERIFIED | Line 8: `logs/` |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Program.fs middleware pipeline | CorrelationMiddleware.correlationMiddleware | app.Use at line 90, BEFORE UseSerilogRequestLogging at line 93 | ✓ WIRED | ORDER_OK confirmed |
| ChatCompletions.fs endpoint | IDecisionLogger.Log | 8 call sites covering success + error paths | ✓ WIRED | grep -c = 8 |
| DecisionLogWriter.ExecuteAsync | channel.Reader.ReadAsync | BackgroundService consumer loop | ✓ WIRED | DecisionLogWriter.fs:80 |
| CompositionRoot.configureServices | AddHostedService<DecisionLogWriter> | Same singleton instance as IDecisionLogger | ✓ WIRED | CompositionRoot.fs:224 forwards GetRequiredService<DecisionLogWriter>() |
| ChatCompletions SSE error path | correlation_id in error body | escapeJsonString + correlationId variable in SSE format string | ✓ WIRED | ChatCompletions.fs:242-244 |
| LoggingTests CapturingSink | startTestRouter Serilog | Custom ILogEventSink installed as Log.Logger in test setup | ✓ WIRED | Tests pass (49/49); CapturingSink at line 30 |
| .fsproj compile order | CorrelationMiddleware → RoutingAlgorithm → QwenUpstreamClient → QueueDispatcher | Lines 14-17 in SmartRouter.Cli.fsproj | ✓ WIRED | Exact order confirmed |

### Requirements Coverage

| REQ-ID | Description | Status | Evidence |
|--------|-------------|--------|----------|
| OBS-01 | Structured JSONL decision log with all required fields | ✓ SATISFIED | 12-field DecisionLog, daily file rotation, all tests pass |
| OBS-03 | correlation_id in SSE error body | ✓ SATISFIED | ChatCompletions.fs:242 includes correlation_id in SSE error |
| LOG-01 | 12-field schema_version=1 JSONL schema | ✓ SATISFIED | DecisionLogger.fs:44-55; Test 1 validates all 12 fields |
| LOG-02 | Thread-safe Channel-backed writer (no File.AppendAllText) | ✓ SATISFIED | Channel.CreateBounded + DropWrite; grep for File.AppendAllText returns no matches |
| LOG-03 | Graceful shutdown flushes in-flight entries | ✓ SATISFIED | DecisionLogWriter.fs:99-122 drain loop + TryComplete; Test 4 passes |
| LOG-04 | Same correlation_id in Serilog stderr + JSONL + SSE error body | ✓ SATISFIED | CorrelationMiddleware PushProperty; Tests 3+5 verify end-to-end propagation |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `tests/SmartRouter.Tests/LoggingTests.fs` | 293 | `Task.Delay(50)` | ℹ️ Info | Inside `waitForLineCount` polling helper — acceptable file-poll delay, not a test synchronization workaround. The plan confirms this pattern at plan doc line 257. |

No blockers. No warnings. The single `Task.Delay` instance is in a legitimate file-polling utility, not as a synchronization hack.

### Note on DecisionLogWire

The verification request listed "DecisionLogWire snake_case wire shape" as a must_have. No separate `DecisionLogWire` type exists in the codebase — this is correct behavior. The PLAN's actual must_haves specify "snake_case JSON serialization (PropertyNamingPolicy.SnakeCaseLower)" which is fully implemented via `jsonOpts` in DecisionLogWriter.fs:44. The snake_case wire output is achieved without a redundant intermediate type.

---

### Gaps Summary

No gaps. All 6 success criteria verified. All 13 required artifacts exist, are substantive, and are wired. All 6 REQ-IDs satisfied.

---

_Verified: 2026-05-08T07:32:00Z_
_Verifier: Claude (gsd-verifier)_
