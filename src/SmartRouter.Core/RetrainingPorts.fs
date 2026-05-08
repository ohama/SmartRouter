module SmartRouter.Core.RetrainingPorts

open System
open System.Threading
open System.Threading.Tasks

/// Teacher's binary label decision for a hard case.
/// Mirrors RoutingDecision.Target but lives in retraining domain — keep separate
/// from Domain.ModelId so the retraining vocabulary is independently evolvable.
type RoutingLabel =
    | Route35B
    | Route122B

/// Outcome of one teacher labeling call.
/// Labeled: parsed teacher response into a clean label.
/// Unparseable: teacher responded but response did not match ROUTE_35B / ROUTE_122B.
/// Skipped: cost cap hit (FAIL-03) or pre-flight check skipped this entry; no HTTP call made.
/// Failed: HTTP error after retries exhausted, or timeout.
type LabelResult =
    | Labeled    of label: RoutingLabel * teacherResponseExcerpt: string
    | Unparseable of rawResponse: string
    | Skipped    of reason: string
    | Failed     of error: string

/// One hard-case correlation_id surfaced by FailureDetector.
/// Carries enough context for downstream consumers (Phase 8 BackgroundService or
/// the --retrain CLI handler) to look up prompt text and feed TeacherLabeler.
/// prompt_text is None when extracted from logs (LOG-01 stores hash only); Some
/// when seeded synthetically OR when called inline by Phase 8's BackgroundService.
type HardCase =
    { CorrelationId          : string
      PromptHash             : string
      PromptKoreanCharRatio  : float
      RoutingAlgorithm       : string
      Target                 : string
      PromptText             : string option }

/// One entry written to datasets/hard-cases.jsonl.
/// Schema_version=1; Phase 8 reader branches on this field.
/// Cli's HardCaseDatasetWriter serializes this (with snake_case naming policy).
type HardCaseEntry =
    { SchemaVersion          : int
      CorrelationId          : string
      PromptHash             : string
      PromptText             : string
      Label                  : int    // 0 = Route35B, 1 = Route122B
      Source                 : string // "teacher" | "synthetic" | "operator"
      TeacherResponseExcerpt : string option
      LabeledAt              : DateTimeOffset
      PromptKoreanCharRatio  : float
      RoutingAlgorithm       : string
      Target                 : string }

/// Failure detector port — reads JSONL decision logs and returns hard cases.
/// FAIL-01: filter `fallback_used = true` records only.
/// Phase 10 will broaden the signal; FailureDetector code does not change.
/// Returns a list (not IAsyncEnumerable) — JSONL files are small enough.
type IFailureDetector =
    abstract member ExtractHardCases : ct: CancellationToken -> Task<HardCase list>

/// Teacher labeler port — calls 122B with prompt text + teacher prompt template,
/// parses ROUTE_35B / ROUTE_122B response. FAIL-02: 30s timeout, 3x retry on
/// transient HTTP failures. FAIL-03: enforces persistent daily cost cap before
/// each call; cap-hit returns Skipped without HTTP call.
type ITeacherLabeler =
    abstract member LabelAsync :
        promptText: string * correlationId: string * ct: CancellationToken
        -> Task<LabelResult>

/// Hard-case dataset writer port — appends entries to datasets/hard-cases.jsonl
/// via Channel + single-writer BackgroundService (mirrors DecisionLogWriter).
/// FAIL-04: dedupes on (correlation_id, prompt_hash); BoundedChannelFullMode.Wait
/// (back-pressure, never DropWrite — losing training data is unacceptable).
type IHardCaseDatasetWriter =
    abstract member AppendAsync :
        entry: HardCaseEntry * ct: CancellationToken
        -> Task<unit>

/// Hot-updatable model version. Phase 8 RetrainingService updates this after each
/// successful retrain so DecisionLog.model_version reflects the live model without
/// host restart. Cli adapter (ModelVersionProvider.fs) holds the mutable string field.
///
/// Why a port: ARCH-01 (Core BCL-only). The mutable state lives in Cli; Core only
/// names the contract. ChatCompletions.fs resolves this per-request and feeds
/// DecisionLog.model_version. RoutingAlgorithmRegistration.ModelVersion remains for
/// backward-compat at registration time but is no longer the load-bearing source.
type IModelVersionProvider =
    abstract member CurrentVersion : string with get
    abstract member Update         : newVersion: string -> unit
