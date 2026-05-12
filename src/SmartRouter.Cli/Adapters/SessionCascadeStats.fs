module SmartRouter.Cli.Adapters.SessionCascadeStats

open System.Threading

// ── ISessionCascadeStats port (OBS-01) ────────────────────────────────────────
//
// Phase 22 — three-tier session-key cascade observability port.
//
// Pattern mirrors Phase 19 SR-05 (SelfRouter.fs:64-71 + 328-339): explicit
// Record* methods (NOT a single RecordSource(string) string-dispatch method —
// see 22-RESEARCH.md §2 alternative). Interlocked.Increment at the call site
// (in ChatCompletions.fs handler, after resolveSessionCascade returns).
// Volatile.Read in the GetStats path ensures fresh values on /stats without
// locking. struct tuple avoids tiny allocations on the /stats hot path.
//
// Counters: each request increments EXACTLY ONE of the three counters based on
// which tier resolved its session key (22-RESEARCH.md §8 Pitfall 9 — counter
// must increment once per request, not once per DI resolution). The cascade is
// total: every non-null-body request goes through exactly one Record* call.
type ISessionCascadeStats =
    /// Tier 1 hit: X-Session-Id header was present and non-empty.
    abstract member RecordHeader    : unit -> unit
    /// Tier 2 hit: header absent, HermesSessionExtract.extractFromSystemPrompt returned Some.
    abstract member RecordSysprompt : unit -> unit
    /// Tier 3 hit: header absent AND sysprompt absent — fell through to ContentFingerprint.compute.
    abstract member RecordContent   : unit -> unit
    /// Returns struct (headerCount, syspromptCount, contentCount).
    /// Field order matches the StatsWire field declaration order (header → sysprompt → content).
    abstract member GetStats : unit -> struct (int64 * int64 * int64)

// ── SessionCascadeStats concrete (OBS-01) ────────────────────────────────────
//
// Singleton owner of the three Int64 counters. Registered as
//   AddSingleton<SessionCascadeStats>() + AddSingleton<ISessionCascadeStats>(...)
// in BOTH configureRequestPipeline AND configureWithoutMl (TIER-04: cascade
// applies in both Routing.Mode values; /stats must respond non-500 in offline
// --retrain path; concrete class is cheap so no NoOp variant is needed).
type SessionCascadeStats() =
    let mutable headerCount    = 0L
    let mutable syspromptCount = 0L
    let mutable contentCount   = 0L

    interface ISessionCascadeStats with
        member _.RecordHeader()    = Interlocked.Increment(&headerCount)    |> ignore
        member _.RecordSysprompt() = Interlocked.Increment(&syspromptCount) |> ignore
        member _.RecordContent()   = Interlocked.Increment(&contentCount)   |> ignore
        member _.GetStats() =
            struct (
                Volatile.Read(&headerCount),
                Volatile.Read(&syspromptCount),
                Volatile.Read(&contentCount))
