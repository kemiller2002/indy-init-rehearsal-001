// The .NET WebAssembly export for the F# engine: forwards one serialized Limen
// message and returns the serialized response. Marshalling plus the single
// engine-state cell (as in Limen's own guest); every decision is Shell's.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using IndyInitRootCauseInvestigator.Engine;

return;

[SupportedOSPlatform("browser")]
public static partial class RciEngine
{
    // The only mutable cell: the engine state between WASM calls.
    private static Shell.AppState state = Shell.initial;

    [JSExport]
    internal static string Dispatch(string messageJson)
    {
        // Entropy and time are supplied here, at the boundary, so Shell.handle stays pure.
        var stamped = Shell.withContext(Guid.NewGuid(), DateTimeOffset.UtcNow, state);
        var result = Shell.handle(stamped, messageJson);
        state = result.Item1;
        return result.Item2;
    }
}
