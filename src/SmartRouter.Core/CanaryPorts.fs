module SmartRouter.Core.CanaryPorts

open System.Threading
open System.Threading.Tasks

/// Canary gate port — answers "is this correlation_id in the canary cohort?"
/// BCL-only signature: keeps Core ARCH-01 invariant (no Microsoft.FeatureManagement reference).
/// Plan 09-02 implements FeatureManagementCanaryGate (Cli) and NullCanaryGate (Cli, no-op fallback for non-canary contexts).
///
/// Returns true → route to canary classifier; false → route to baseline classifier.
/// Implementations MUST short-circuit to false when:
///   - correlationId = "" (non-HTTP construction site; tests)
///   - canary file not present on disk (Plan 09-02 adds File.Exists check)
///   - in-memory percentage gate is 0 (Plan 09-02 adds ICanaryState check)
type ICanaryGate =
    abstract member IsCanaryAsync :
        correlationId: string * ct: CancellationToken
        -> Task<bool>
