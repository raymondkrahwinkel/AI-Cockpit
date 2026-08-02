using Cockpit.Core.Debugging;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `DebugSettings` in the `debug` section of `cockpit.json`.
internal sealed class DebugSettingsEntry
{
    public bool ShowDebugControls { get; set; }

    public static DebugSettingsEntry FromDomain(DebugSettings settings) => new()
    {
        ShowDebugControls = settings.ShowDebugControls,
    };

    public DebugSettings ToDomain() => new()
    {
        ShowDebugControls = ShowDebugControls,
    };
}
