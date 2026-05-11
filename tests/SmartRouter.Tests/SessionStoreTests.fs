module SmartRouter.Tests.SessionStoreTests

open System
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Logging.Abstractions
open SmartRouter.Core.Domain
open SmartRouter.Cli.Adapters.SessionStore

/// Helper: construct a SessionStore with low TTL + small LRU cap to make
/// eviction visible inside a single test method execution.
let private mkStore (ttlMinutes: int) (maxEntries: int) : SessionStore * ISessionStore =
    let opts = { TtlMinutes = ttlMinutes; MaxEntries = maxEntries }
    let store = new SessionStore(opts, NullLogger<SessionStore>.Instance)
    store, (store :> ISessionStore)

let tests : Test =
    testList "SessionStoreTests" [

        // ── Empty-sessionId no-op (Pitfall 7 prevention) ──────────────────────

        testCase "Update with empty sessionId is no-op" <| fun () ->
            let store, iface = mkStore 30 1000
            iface.Update("", Qwen122B)
            Expect.equal store.Store.Count 0 "no entry created for empty sessionId"

        testCase "TryGet with empty sessionId returns None" <| fun () ->
            let _, iface = mkStore 30 1000
            Expect.isNone (iface.TryGet("")) "empty sessionId → None"

        // ── Basic write + read ─────────────────────────────────────────────────

        testCase "Update then TryGet returns the written state" <| fun () ->
            let _, iface = mkStore 30 1000
            iface.Update("sess-1", Qwen35B)
            let r = iface.TryGet("sess-1")
            Expect.isSome r "entry must exist"
            Expect.equal r.Value.LastModel Qwen35B "model recorded"

        testCase "TryGet on unknown sessionId returns None" <| fun () ->
            let _, iface = mkStore 30 1000
            iface.Update("sess-x", Qwen35B)
            Expect.isNone (iface.TryGet("sess-y")) "unknown session → None"

        // ── 122B-wins concurrent merge (non-negotiable correctness invariant) ──

        testCase "Concurrent Update: 122B wins over 35B regardless of race order" <| fun () ->
            let store, iface = mkStore 30 1000
            // Fire 100 35B writes + 100 122B writes concurrently against the same session.
            let writes =
                [| for _ in 1 .. 100 ->
                       Task.Run(fun () -> iface.Update("race", Qwen35B))
                   for _ in 1 .. 100 ->
                       Task.Run(fun () -> iface.Update("race", Qwen122B)) |]
            Task.WaitAll(writes)
            let r = iface.TryGet("race")
            Expect.isSome r "entry must exist"
            Expect.equal r.Value.LastModel Qwen122B
                "122B-wins merge: final LastModel must be Qwen122B"

        testCase "Update 122B then 35B keeps 122B (sequential merge)" <| fun () ->
            let _, iface = mkStore 30 1000
            iface.Update("sess-merge", Qwen122B)
            iface.Update("sess-merge", Qwen35B)
            let r = iface.TryGet("sess-merge")
            Expect.isSome r "entry must exist"
            Expect.equal r.Value.LastModel Qwen122B
                "122B stays even when newer write is 35B"

        // ── TTL eviction (SES-08 unit-level) ──────────────────────────────────
        // Test uses direct LastAccessedAt mutation (no sleep needed) — deterministic.
        // The PeriodicTimer eviction loop is the BackgroundService path; TryGet also
        // checks TTL in-band and returns None for stale entries.

        testCase "TryGet returns None for entries older than TtlMinutes" <| fun () ->
            let store, iface = mkStore 30 1000
            iface.Update("sess-stale", Qwen122B)
            // Mutate LastAccessedAt to a value far in the past (simulating elapsed time).
            match store.Store.TryGetValue("sess-stale") with
            | true, entry ->
                let stale = { entry with LastAccessedAt = DateTimeOffset.UtcNow.AddMinutes(-60.0) }
                // Force-update the entry to simulate elapsed time. TryUpdate atomically replaces
                // the value if the current value matches the expected (the entry we just read).
                store.Store.TryUpdate("sess-stale", stale, entry) |> ignore
                let r = iface.TryGet("sess-stale")
                Expect.isNone r "TTL-expired entry → None"
            | _ ->
                failtest "test setup failed: entry not created"

        // ── LRU bound (SES-02) ────────────────────────────────────────────────

        testCase "MaxEntries cap evicts oldest-AccessSeq entry on overflow" <| fun () ->
            let store, iface = mkStore 30 3   // cap = 3
            iface.Update("s1", Qwen35B)
            iface.Update("s2", Qwen35B)
            iface.Update("s3", Qwen35B)
            Expect.equal store.Store.Count 3 "at cap after 3 inserts"
            iface.Update("s4", Qwen35B)   // 4th insert triggers eviction
            Expect.isTrue (store.Store.Count <= 3) "cap not exceeded after 4th insert"
            Expect.isSome (iface.TryGet("s4")) "newest entry present"
            Expect.isNone (iface.TryGet("s1")) "oldest entry evicted"
    ]
