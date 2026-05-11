module SmartRouter.Tests.RouterTests

open Expecto

/// Explicit list of test modules. Auto-discovery via [<Tests>] is forbidden —
/// it is unreliable in Expecto and burned 4 executors in blueCode. Every new
/// test module must (1) be added to SmartRouter.Tests.fsproj <Compile> list
/// BEFORE this file, and (2) be appended to rootTests below.
///
/// testSequenced rule (PITFALL-27): if a test module touches Console.SetOut
/// or Console.SetError, wrap its testList with `testSequenced`. Phase 1
/// routing tests are pure and do not need it; integration tests in later
/// phases may.
let rootTests : Test list =
    [
        SmartRouter.Tests.StreamingTests.tests
        SmartRouter.Tests.QueueTests.tests
        SmartRouter.Tests.LoadTests.tests   // pending tests, skipped by default
        SmartRouter.Tests.MLRoutingTests.tests
        SmartRouter.Tests.MLEmbeddingTests.tests
        SmartRouter.Tests.MLClassifierTests.tests
        SmartRouter.Tests.LoggingTests.tests
        SmartRouter.Tests.FailureDetectorTests.tests   // Phase 7
        SmartRouter.Tests.TeacherLabelerTests.tests    // Phase 7
        SmartRouter.Tests.HardCaseDatasetTests.tests   // Phase 7
        SmartRouter.Tests.RetrainingTests.tests        // Phase 8
        SmartRouter.Tests.CanaryTests.tests            // Phase 9
        SmartRouter.Tests.HealthFallbackTests.tests   // Phase 10
        SmartRouter.Tests.ModelsTests.tests           // Phase 11
        SmartRouter.Tests.LogRotationTests.tests      // Phase 13
        SmartRouter.Tests.ProductionDiTests.tests     // Issue #8
        SmartRouter.Tests.MLLiveVersionTests.tests    // Issue #12
        SmartRouter.Tests.QualityFallbackTests.tests  // Phase 14
        SmartRouter.Tests.QualitySignalEnrichmentTests.tests  // Phase 15
        SmartRouter.Tests.JudgeIntegrationTests.tests          // Phase 16
        SmartRouter.Tests.HardRulesTests.tests                 // Phase 17 (Plan 17-01)
        SmartRouter.Tests.ModeSwitchTests.tests                // Phase 17 (Plan 17-03)
        SmartRouter.Tests.SessionStoreTests.tests              // Phase 18 (Plan 18-03)
        SmartRouter.Tests.StickyEscalationTests.tests          // Phase 18 (Plan 18-03)
        SmartRouter.Tests.SelfRouterTests.selfRouterTests             // Phase 19 (Plan 19-03)
        SmartRouter.Tests.SelfRoutingIntegrationTests.selfRoutingIntegrationTests  // Phase 19 (Plan 19-03)
    ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "all" rootTests)
