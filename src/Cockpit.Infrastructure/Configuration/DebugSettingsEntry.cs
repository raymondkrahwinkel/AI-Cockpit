using Cockpit.Core.Debugging;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `DebugSettings` in the `debug` section of `cockpit.json`.
internal sealed class DebugSettingsEntry
{
    public bool ShowDebugControls { get; set; }
    public bool LogDiagnosticSnapshots { get; set; }
    public int UiFreezeThresholdSeconds { get; set; } = 5;
    public int RenderStallThresholdSeconds { get; set; } = 15;

    public static DebugSettingsEntry FromDomain(DebugSettings settings) => new()
    {
        ShowDebugControls = settings.ShowDebugControls,
        LogDiagnosticSnapshots = settings.LogDiagnosticSnapshots,
        UiFreezeThresholdSeconds = settings.UiFreezeThresholdSeconds,
        RenderStallThresholdSeconds = settings.RenderStallThresholdSeconds,
    };

    public DebugSettings ToDomain() => new()
    {
        ShowDebugControls = ShowDebugControls,
        LogDiagnosticSnapshots = LogDiagnosticSnapshots,
        UiFreezeThresholdSeconds = UiFreezeThresholdSeconds > 1 ? UiFreezeThresholdSeconds : 5,
        RenderStallThresholdSeconds = RenderStallThresholdSeconds > 0 ? RenderStallThresholdSeconds : 15,
    };
}
