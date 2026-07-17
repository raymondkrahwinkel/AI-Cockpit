namespace Cockpit.Core.Diagnostics;

/// <summary>
/// Which graphics backend the cockpit is drawing with (AC-58), the single most useful line for AC-57: it shows at
/// a glance whether macOS is on Metal (the leak suspect) or an override moved it to OpenGL/Software.
/// <para>
/// The App layer fills this in — it is the only one that references Avalonia. Avalonia exposes no public API for
/// the <em>live</em> backend, so <see cref="Mode"/> reports the configured preference and says so honestly rather
/// than claiming an active backend it cannot observe; <see cref="Detail"/> carries the platform default that
/// preference resolves to.
/// </para>
/// </summary>
public sealed record RenderingInfo(string Mode, string Detail)
{
    public static readonly RenderingInfo Unknown = new("Unknown", "The rendering backend could not be determined.");
}
