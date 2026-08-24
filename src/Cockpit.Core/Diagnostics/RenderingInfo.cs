namespace Cockpit.Core.Diagnostics;

// AC-1013 (AC-58): Which graphics backend the cockpit draws with — the single most useful line for AC-57, since
// it shows at a glance whether macOS is on Metal (the leak suspect). Filled in by the App layer (the only layer
// referencing Avalonia); `Mode` reports the configured preference honestly since Avalonia exposes no live-backend API.
public sealed record RenderingInfo(string Mode, string Detail)
{
    public static readonly RenderingInfo Unknown = new("Unknown", "The rendering backend could not be determined.");
}
