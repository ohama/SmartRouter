module SmartRouter.Cli.Adapters.CanaryGate

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.FeatureManagement
open Microsoft.FeatureManagement.FeatureFilters
open SmartRouter.Core.CanaryPorts
open SmartRouter.Cli.Adapters.CanaryState

[<Literal>]
let CanaryFeatureName = "Canary"

/// No-op canary gate — used in non-canary contexts (offline retrain path, tests)
/// where there is no ML classifier or canary infrastructure. Always returns false.
type NullCanaryGate() =
    interface ICanaryGate with
        member _.IsCanaryAsync(_correlationId, _ct) =
            Task.FromResult(false)

/// FeatureManagement-backed canary gate. Three-layer guard before consulting the
/// hash bucketing:
///   1. correlation_id must be present (no canary for unattributed traffic).
///   2. File.Exists check on canary model path (RESEARCH §4.3 Strategy A-Better).
///   3. ICanaryState.GetPercentage > 0 (Lock 8) — instant rollback without IConfiguration write.
///   4. IVariantFeatureManager hash bucketing — sticky per correlation_id via TargetingFilter.
///
/// **DI lifetime contract (issue #2):** This gate is registered as Singleton in
/// CompositionRoot, but `IVariantFeatureManager` from `Microsoft.FeatureManagement` is
/// Scoped. Resolving a scoped service from a singleton's captured `IServiceProvider`
/// fails ASP.NET Core's `ValidateScopes` check (Development default). The fix: the
/// production constructor accepts `IServiceScopeFactory` (itself singleton) and
/// resolves `IVariantFeatureManager` from a fresh per-call scope. The legacy
/// constructor taking `IVariantFeatureManager` directly is retained for tests
/// (which control the FM lifetime explicitly).
type FeatureManagementCanaryGate
    internal
    (
        checkFeature   : string -> CancellationToken -> Task<bool>,
        canaryState    : ICanaryState,
        canaryModelPath: string
    ) =

    /// Test-friendly: caller provides an `IVariantFeatureManager` directly. Used by
    /// CanaryTests where the FM is constructed in-test and not subject to DI scoping.
    new (featureManager: IVariantFeatureManager, state: ICanaryState, path: string) =
        let check (correlationId: string) (ct: CancellationToken) =
            task {
                let ctx = TargetingContext(UserId = correlationId, Groups = [||])
                let! enabled = featureManager.IsEnabledAsync<TargetingContext>(CanaryFeatureName, ctx, ct)
                return enabled
            }
        FeatureManagementCanaryGate(check, state, path)

    /// Production: takes `IServiceScopeFactory` (singleton-safe) and creates a fresh
    /// scope per `IsCanaryAsync` call. The scoped `IVariantFeatureManager` is resolved
    /// inside that scope and disposed when the call returns. This is the correct
    /// pattern for a singleton consumer of scoped services.
    new (scopeFactory: IServiceScopeFactory, state: ICanaryState, path: string) =
        let check (correlationId: string) (ct: CancellationToken) =
            task {
                use scope = scopeFactory.CreateScope()
                let fm = scope.ServiceProvider.GetRequiredService<IVariantFeatureManager>()
                let ctx = TargetingContext(UserId = correlationId, Groups = [||])
                let! enabled = fm.IsEnabledAsync<TargetingContext>(CanaryFeatureName, ctx, ct)
                return enabled
            }
        FeatureManagementCanaryGate(check, state, path)

    interface ICanaryGate with
        member _.IsCanaryAsync(correlationId, ct) =
            task {
                if String.IsNullOrEmpty(correlationId) then
                    return false
                elif not (File.Exists(canaryModelPath)) then
                    return false
                elif canaryState.GetPercentage() <= 0 then
                    return false
                else
                    let! enabled = checkFeature correlationId ct
                    return enabled
            }
