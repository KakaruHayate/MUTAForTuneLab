using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// A log sink for the synthesis layer.
///
/// The chain below the session is deliberately independent of the host: it is the part
/// worth measuring offline, and taking the host SDK out of it lets a probe drive it
/// directly. The plugin installs the host's logger here at startup.
/// </summary>
public static class MutaDiagnostics
{
    public static Action<string>? Sink { get; set; }

    public static void Log(string message) => Sink?.Invoke(message);
}
