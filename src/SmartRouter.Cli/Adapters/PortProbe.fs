module SmartRouter.Cli.Adapters.PortProbe

open System.Net
open System.Net.Sockets

/// PROBE-01: error record returned by `tryBind` when the port is in use.
/// `Port` is the integer port number that was probed.
/// `Address` is the IP-address string form (typically "127.0.0.1").
/// `Reason` is the underlying SocketException message (for diagnostic logging,
/// not for the user-facing 4-line ERROR block — that block is fixed-format).
type PortConflictError =
    { Port    : int
      Address : string
      Reason  : string }

/// PROBE-01: attempt to bind a TcpListener to (addr, port). Starts and
/// immediately stops the listener; returns Ok () if the bind succeeded.
/// On SocketException.AddressAlreadyInUse, returns Error with the conflict
/// details. Synchronous (BCL-only). Typical execution: <1 ms on free port,
/// <100 ms even on an in-use port (immediate refusal).
let tryBind (port: int) (addr: IPAddress) : Result<unit, PortConflictError> =
    let listener = new TcpListener(addr, port)
    try
        listener.Start()
        listener.Stop()
        Ok ()
    with
    | :? SocketException as ex when ex.SocketErrorCode = SocketError.AddressAlreadyInUse ->
        Error { Port = port; Address = addr.ToString(); Reason = ex.Message }
    | :? SocketException as ex ->
        // Other socket errors (permission denied on privileged ports, etc.) —
        // still return Error so Program.fs can surface them, but include the
        // raw reason. The user-facing message in Program.fs is fixed-format
        // and always mentions "already in use" — operators reading non-bind-conflict
        // errors will see the `Reason` field via debug logging if needed.
        Error { Port = port; Address = addr.ToString(); Reason = ex.Message }
