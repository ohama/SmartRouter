module SmartRouter.Cli.Adapters.SessionStore

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SmartRouter.Core.Domain

// ── ISessionStore port ───────────────────────────────────────────────────────

/// Phase 18 — in-process session state lookup + write.
/// Cli adapter interface; NOT in Core Ports (ARCH-01: Core has no need for in-process
/// state machines). Phase 19's SelfRouter closure receives ISessionStore via DI in
/// CompositionRoot, mirroring how IJudgeClient is consumed today.
type ISessionStore =
    /// Returns None when sessionId is empty string, the entry is absent, or the
    /// entry's LastAccessedAt is older than TtlMinutes. Otherwise returns Some
    /// and bumps the LRU access sequence as a side effect.
    abstract member TryGet : sessionId: string -> SessionState option
    /// No-op when sessionId is empty string (stateless path; Pitfall 7).
    /// Uses ConcurrentDictionary.AddOrUpdate with a 122B-wins merge function
    /// (Pitfall 4): a concurrent 35B write must NEVER overwrite a 122B escalation.
    abstract member Update : sessionId: string * model: ModelId -> unit

// ── SessionOptions (config binding) ──────────────────────────────────────────

/// Phase 18 — appsettings.json:Routing.Session binding.
/// CLIMutable + explicit mutable on every field per Phase 13-05 + 14-02 lesson:
/// without 'mutable', JSON binding writes through property setters that don't exist
/// and the runtime silently keeps default values (0, false).
[<CLIMutable>]
type SessionOptions = {
    mutable TtlMinutes : int   // default 30 when <= 0
    mutable MaxEntries : int   // default 10000 when <= 0
}

// ── SessionStore class ───────────────────────────────────────────────────────

/// Phase 18 — in-process session store.
///
/// Storage: ConcurrentDictionary<string, SessionState> (BCL; same TOCTOU semantics
/// as JudgeClient.fs cache).
/// LRU: bounded MaxEntries via write-time count check + Seq.minBy(_.LastAccessSeq)
/// eviction. O(n) scan acceptable at n<=10000 (mirrors JudgeClient).
/// TTL: TryGet returns None for entries older than TtlMinutes. Periodic eviction
/// loop (BackgroundService ExecuteAsync) ships in Plan 18-03.
///
/// 122B-WINS MERGE (non-negotiable correctness invariant): the AddOrUpdate update
/// factory keeps Qwen122B on collision regardless of which write is "newer". This
/// prevents a racing 35B write from overwriting a 122B escalation — the failure
/// mode would silently lose sticky continuation for the rest of the session.
type SessionStore(opts: SessionOptions, logger: ILogger<SessionStore>) =
    inherit BackgroundService()

    let store = ConcurrentDictionary<string, SessionState>()
    let mutable globalSeq = 0L

    let ttlMinutes = if opts.TtlMinutes <= 0 then 30    else opts.TtlMinutes
    let maxEntries = if opts.MaxEntries <= 0 then 10000 else opts.MaxEntries

    // Internal — performs the actual update logic; called only when sessionId non-empty.
    let doUpdate (sessionId: string) (model: ModelId) =
        // LRU cap: count-based eviction on write. TOCTOU-safe (Pitfall 5 of JudgeClient):
        // two concurrent threads may both evict; store may transiently hold maxEntries+1.
        // Acceptable — the invariant is "approximately bounded", not "never exceeds".
        if store.Count >= maxEntries then
            try
                let minKv = store |> Seq.minBy (fun kv -> kv.Value.LastAccessSeq)
                store.TryRemove(minKv.Key) |> ignore
            with _ -> ()   // concurrent eviction race is acceptable

        // 122B-wins merge (Pitfall 4):
        // Two concurrent writes on the same session can race. The update factory MUST
        // keep Qwen122B if either old or new is 122B — never let a racing 35B
        // overwrite a 122B escalation.
        let buildNew () = {
            LastModel      = model
            LastAccessedAt = DateTimeOffset.UtcNow
            LastAccessSeq  = Interlocked.Increment(&globalSeq) }
        store.AddOrUpdate(
            sessionId,
            addValueFactory = (fun _ -> buildNew ()),
            updateValueFactory = fun _ oldState ->
                let winningModel =
                    if oldState.LastModel = Qwen122B || model = Qwen122B
                    then Qwen122B
                    else model
                { LastModel      = winningModel
                  LastAccessedAt = DateTimeOffset.UtcNow
                  LastAccessSeq  = Interlocked.Increment(&globalSeq) })
        |> ignore

    interface ISessionStore with
        member _.TryGet(sessionId) =
            if String.IsNullOrEmpty(sessionId) then None
            else
                match store.TryGetValue(sessionId) with
                | true, entry ->
                    // TTL check first — return None if expired, even before bumping LRU.
                    // (If expired, the entry still occupies a dictionary slot until the
                    // 18-03 BackgroundService sweep removes it. Treat as absent.)
                    let age = DateTimeOffset.UtcNow - entry.LastAccessedAt
                    if age.TotalMinutes > float ttlMinutes then
                        None
                    else
                        // LRU update: in-place mutation of mutable AccessSeq.
                        // No dictionary key churn; mirrors JudgeClient.tryGetCached.
                        entry.LastAccessSeq <- Interlocked.Increment(&globalSeq)
                        Some entry
                | _ -> None

        member _.Update(sessionId, model) =
            if not (String.IsNullOrEmpty(sessionId)) then   // Pitfall 7 guard
                doUpdate sessionId model

    /// Phase 18 (SES-08) — TTL eviction loop.
    /// PeriodicTimer 5-minute interval (hardcoded; SES-08); on each tick, scan the
    /// store and remove entries with LastAccessedAt older than ttlMinutes.
    ///
    /// Mirrors Phase 8 RetrainingService PeriodicTimer pattern: catch
    /// OperationCanceledException to exit cleanly on shutdown; log non-OCE exceptions
    /// but log-and-continue (do not exit the loop on transient errors).
    ///
    /// The simpler ":? OperationCanceledException -> go <- false" catch is sufficient
    /// here because there are no follow-up do! awaits after WaitForNextTickAsync —
    /// the eviction `for kv in store` block is synchronous. ExceptionDispatchInfo.Capture
    /// pattern (Phase 8) is only required when re-throwing OCE through a chain of awaits.
    override _.ExecuteAsync(ct: CancellationToken) : Task =
        task {
            logger.LogInformation(
                "SessionStore TTL eviction started; interval=5min ttlMinutes={Ttl}",
                ttlMinutes)
            use timer = new PeriodicTimer(TimeSpan.FromMinutes(5.0))
            let mutable go = true
            while go && not ct.IsCancellationRequested do
                try
                    let! _ = timer.WaitForNextTickAsync(ct)
                    let cutoff = DateTimeOffset.UtcNow.AddMinutes(float -ttlMinutes)
                    let mutable removed = 0
                    for kv in store do
                        if kv.Value.LastAccessedAt < cutoff then
                            if store.TryRemove(kv.Key) |> fst then
                                removed <- removed + 1
                    if removed > 0 then
                        logger.LogDebug(
                            "SessionStore TTL eviction removed {Removed} entries; remaining={Count}",
                            removed, store.Count)
                with
                | :? OperationCanceledException -> go <- false
                | ex ->
                    logger.LogError(ex, "SessionStore TTL eviction error; loop continues")
        }

    // Internal accessors for tests + future plans (18-03 eviction loop).
    // Not part of ISessionStore (which only exposes TryGet + Update to callers).
    member internal _.Store = store
    member internal _.TtlMinutes = ttlMinutes
    member internal _.MaxEntries = maxEntries
