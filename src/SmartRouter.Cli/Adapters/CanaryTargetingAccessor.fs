module SmartRouter.Cli.Adapters.CanaryTargetingAccessor

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.FeatureManagement.FeatureFilters
open SmartRouter.Cli.Adapters.CorrelationMiddleware

/// Provides correlation_id as the targeting UserId so SHA-256 hash bucketing is sticky
/// per request stream (RESEARCH §1.3, §1.4). When no HttpContext exists (out-of-request
/// background paths), returns a fresh GUID so background evaluations get random buckets.
///
/// Groups MUST be [||] (empty array) — TargetingEvaluator iterates Groups and NPEs on null
/// (RESEARCH §11 Pitfall 10).
type CanaryTargetingContextAccessor(httpContextAccessor: IHttpContextAccessor) =
    interface ITargetingContextAccessor with
        member _.GetContextAsync() =
            let ctx = httpContextAccessor.HttpContext
            let userId =
                if isNull ctx then
                    Guid.NewGuid().ToString("N")
                else
                    match ctx.Items.TryGetValue(CorrelationIdKey) with
                    | true, (:? string as cid) when not (String.IsNullOrEmpty(cid)) -> cid
                    | _ -> Guid.NewGuid().ToString("N")
            ValueTask<TargetingContext>(TargetingContext(UserId = userId, Groups = [||]))
