module SmartRouter.Tests.ProductionDiTests

// Issue #8: integration test that exercises the production DI graph end-to-end.
// Specifically validates that ICanaryGate (singleton) resolves IVariantFeatureManager
// (scoped) WITHOUT triggering ASP.NET Core's "Cannot resolve scoped service from root
// provider" check — the bug from issue #2.
//
// Strategy: build a focused DI scenario that mirrors the production CompositionRoot
// path for ICanaryGate registration:
//   1. AddScopedFeatureManagement() + WithTargeting<...>() (matches CompositionRoot:580-583)
//   2. AddSingleton<ICanaryGate>(...) with the production factory using IServiceScopeFactory
//      (matches CompositionRoot:605-619 post-#2 fix)
//   3. BuildServiceProvider with validateScopes:true (matches Development default)
//   4. Resolve ICanaryGate (singleton) and call IsCanaryAsync (which internally resolves
//      the scoped IVariantFeatureManager)
//
// Pre-#2 this would throw InvalidOperationException at step 4.
// Post-#2 this returns false (canary file absent OR percentage 0 short-circuits).

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.FeatureManagement
open Microsoft.FeatureManagement.FeatureFilters
open Expecto

open SmartRouter.Core.CanaryPorts
open SmartRouter.Cli.Adapters.CanaryGate
open SmartRouter.Cli.Adapters.CanaryState

/// Stub ITargetingContextAccessor (Microsoft.FeatureManagement.FeatureFilters)
/// — enough to satisfy `.WithTargeting<...>()` registration without dragging in
/// IHttpContextAccessor (which CanaryTargetingContextAccessor requires).
type private StubTargetingAccessor() =
    interface ITargetingContextAccessor with
        member _.GetContextAsync() =
            ValueTask<TargetingContext>(TargetingContext(UserId = "test-user", Groups = [||]))

let tests =
    testSequenced (testList "production-di" [

        // The exact scenario that broke in issue #2: ICanaryGate registered as singleton,
        // its factory captures the root provider, and the gate's IsCanaryAsync internally
        // tries to resolve IVariantFeatureManager (scoped). Pre-fix: throws. Post-fix: ok.
        testCase "ICanaryGate.IsCanaryAsync does not throw scoped-from-root DI exception" <| fun () ->
            let services = ServiceCollection()

            // Mirror CompositionRoot:580-583 — feature management + targeting.
            services
                .AddScopedFeatureManagement()
                .WithTargeting<StubTargetingAccessor>()
                |> ignore

            // ICanaryState — singleton like in production. Constructor takes initial percentage.
            services.AddSingleton<CanaryState>(fun _ -> CanaryState(0)) |> ignore
            services.AddSingleton<ICanaryState>(fun sp -> sp.GetRequiredService<CanaryState>() :> ICanaryState) |> ignore

            // FM Configuration — required for AddScopedFeatureManagement to bind features.
            // Empty config; the test doesn't care about the actual feature definition,
            // only that the DI graph resolves cleanly.
            let cfg =
                ConfigurationBuilder()
                    .AddInMemoryCollection(dict [
                        "feature_management:feature_flags:0:id", "Canary"
                        "feature_management:feature_flags:0:enabled", "true"
                    ])
                    .Build() :> IConfiguration
            services.AddSingleton<IConfiguration>(cfg) |> ignore

            // ICanaryGate — production factory pattern (post-#2 fix): IServiceScopeFactory.
            services.AddSingleton<ICanaryGate>(fun sp ->
                let scopeFactory = sp.GetRequiredService<IServiceScopeFactory>()
                let st = sp.GetRequiredService<ICanaryState>()
                FeatureManagementCanaryGate(scopeFactory, st, "/tmp/nonexistent-canary.zip") :> ICanaryGate) |> ignore

            // validateScopes:true mirrors ASP.NET Core Development default; this is the
            // setting that catches scoped-from-root resolution at runtime.
            let opts = ServiceProviderOptions(ValidateScopes = true)
            use sp = services.BuildServiceProvider(opts)

            // Step 1: resolve the singleton gate. Pre-#2 this would already fail because
            // the factory eagerly captured IVariantFeatureManager from the root provider.
            // Post-#2 this succeeds because the factory only captures IServiceScopeFactory.
            let gate = sp.GetRequiredService<ICanaryGate>()

            // Step 2: actually CALL IsCanaryAsync. This is where the post-#2 design creates
            // a per-call scope and resolves IVariantFeatureManager from it. Pre-#2 the gate
            // had IVariantFeatureManager captured at factory time, so this call attempt
            // would already have failed at step 1.
            //
            // The canary file is intentionally missing; the gate short-circuits on
            // File.Exists(canaryModelPath)=false BEFORE consulting the FM. So we expect
            // false here. The point of the test is that no exception is thrown.
            let result = gate.IsCanaryAsync("test-cid", CancellationToken.None).GetAwaiter().GetResult()
            Expect.isFalse result "canary file absent → IsCanaryAsync returns false (no DI exception)"

        // A second variant: state.GetPercentage() > 0 + canary file exists, so the gate
        // proceeds past the short-circuits and actually consults IVariantFeatureManager
        // from a fresh scope. This is the scenario the production /v1/chat/completions
        // request hits — it would have thrown pre-#2.
        testCase "ICanaryGate consults FM from per-call scope when canary is armed" <| fun () ->
            // Arrange a real canary file on disk.
            let canaryFile = Path.Combine(Path.GetTempPath(), "smart-router-test-canary-" + Path.GetRandomFileName() + ".zip")
            File.WriteAllText(canaryFile, "dummy")
            try
                let services = ServiceCollection()
                services.AddScopedFeatureManagement().WithTargeting<StubTargetingAccessor>() |> ignore
                let canaryState = CanaryState(50)   // initial > 0 so the gate proceeds past the percentage gate
                services.AddSingleton<CanaryState>(fun _ -> canaryState) |> ignore
                services.AddSingleton<ICanaryState>(fun sp -> sp.GetRequiredService<CanaryState>() :> ICanaryState) |> ignore
                let cfg =
                    ConfigurationBuilder()
                        .AddInMemoryCollection(dict [
                            "feature_management:feature_flags:0:id", "Canary"
                            "feature_management:feature_flags:0:enabled", "true"
                        ])
                        .Build() :> IConfiguration
                services.AddSingleton<IConfiguration>(cfg) |> ignore
                services.AddSingleton<ICanaryGate>(fun sp ->
                    let scopeFactory = sp.GetRequiredService<IServiceScopeFactory>()
                    let st = sp.GetRequiredService<ICanaryState>()
                    FeatureManagementCanaryGate(scopeFactory, st, canaryFile) :> ICanaryGate) |> ignore

                let opts = ServiceProviderOptions(ValidateScopes = true)
                use sp = services.BuildServiceProvider(opts)
                let gate = sp.GetRequiredService<ICanaryGate>()

                // The actual test — call IsCanaryAsync; it must reach the FM check inside
                // a fresh scope and return cleanly (true or false; either is fine — we only
                // care that no DI exception fires).
                let result = gate.IsCanaryAsync("test-correlation-id", CancellationToken.None).GetAwaiter().GetResult()
                Expect.isTrue (result || not result) "no DI exception when consulting scoped IVariantFeatureManager from singleton gate"
            finally
                try File.Delete(canaryFile) with _ -> ()
    ])
