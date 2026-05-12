module SmartRouter.Tests.PortProbeTests

open System.Diagnostics
open System.Net
open System.Net.Sockets
open Expecto
open SmartRouter.Cli.Adapters.PortProbe

/// Pick a likely-free high port. Not a guarantee — tests retry once on collision.
let private rng = System.Random()
let private pickPort () : int = rng.Next(20000, 60000)

let tests : Test =
    testSequenced (testList "PortProbe" [

        // PROBE-04 case (a): free port returns Ok ()
        testCase "tryBind returns Ok () for a free port" <| fun _ ->
            let port = pickPort ()
            match tryBind port IPAddress.Loopback with
            | Ok () -> ()  // pass
            | Error err ->
                // Retry once in case of bad luck
                let port2 = pickPort ()
                match tryBind port2 IPAddress.Loopback with
                | Ok () -> ()
                | Error err2 ->
                    failtestf "Expected Ok () on free port, got Error %A and retry Error %A" err err2

        // PROBE-04 case (b): port already bound returns Error _
        testCase "tryBind returns Error when port is already bound" <| fun _ ->
            let port = pickPort ()
            let blocker = new TcpListener(IPAddress.Loopback, port)
            blocker.Start()
            try
                match tryBind port IPAddress.Loopback with
                | Ok () ->
                    failtestf "Expected Error on already-bound port %d, got Ok ()" port
                | Error _ -> ()  // pass
            finally
                blocker.Stop()

        // PROBE-04 case (c): Error.Port matches the probed port number
        testCase "Error.Port matches the probed port" <| fun _ ->
            let port = pickPort ()
            let blocker = new TcpListener(IPAddress.Loopback, port)
            blocker.Start()
            try
                match tryBind port IPAddress.Loopback with
                | Error err ->
                    Expect.equal err.Port port "Error.Port should match the probed port"
                    Expect.equal err.Address "127.0.0.1" "Error.Address should be the loopback dotted-quad form"
                | Ok () ->
                    failtestf "Expected Error on already-bound port %d, got Ok ()" port
            finally
                blocker.Stop()

        // PROBE-04 case (d): probe completes in <100 ms even on conflict
        testCase "tryBind completes in <100ms on conflict" <| fun _ ->
            let port = pickPort ()
            let blocker = new TcpListener(IPAddress.Loopback, port)
            blocker.Start()
            try
                let sw = Stopwatch.StartNew()
                let _result = tryBind port IPAddress.Loopback
                sw.Stop()
                Expect.isLessThan sw.ElapsedMilliseconds 100L
                    "tryBind on in-use port should return in <100ms"
            finally
                blocker.Stop()
    ])
