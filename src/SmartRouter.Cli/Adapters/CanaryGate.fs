module SmartRouter.Cli.Adapters.CanaryGate

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.FeatureManagement
open Microsoft.FeatureManagement.FeatureFilters
open SmartRouter.Core.CanaryPorts
open SmartRouter.Cli.Adapters.CanaryState

[<Literal>]
let CanaryFeatureName = "Canary"

/// No-op canary gate — used in heuristic mode where there is no ML classifier and
/// no canary infrastructure. Always returns false.
type NullCanaryGate() =
    interface ICanaryGate with
        member _.IsCanaryAsync(_correlationId, _ct) =
            Task.FromResult(false)

/// FeatureManagement-backed canary gate. Two-layer:
///   1. File.Exists check (RESEARCH §4.3 Strategy A-Better) — short-circuits before the pool
///      is asked to predict on a missing file.
///   2. ICanaryState.GetPercentage > 0 (Lock 8) — instant rollback without IConfiguration write.
///   3. IVariantFeatureManager hash bucketing — sticky per correlation_id via TargetingFilter.
type FeatureManagementCanaryGate(
    featureManager : IVariantFeatureManager,
    canaryState    : ICanaryState,
    canaryModelPath: string) =
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
                    let ctx = TargetingContext(UserId = correlationId, Groups = [||])
                    let! enabled = featureManager.IsEnabledAsync<TargetingContext>(CanaryFeatureName, ctx, ct)
                    return enabled
            }
