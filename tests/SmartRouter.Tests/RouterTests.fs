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
        SmartRouter.Tests.RoutingTests.tests
        // SmartRouter.Tests.IntegrationTests.tests   // <- added in later phases
    ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "all" rootTests)
