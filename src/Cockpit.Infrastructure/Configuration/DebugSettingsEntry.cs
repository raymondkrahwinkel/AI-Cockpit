using Cockpit.Core.Debugging;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="DebugSettings"/> in the <c>debug</c> section of <c>cockpit.json</c>.</summary>
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
