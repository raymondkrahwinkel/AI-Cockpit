using Cockpit.Core.Delegation;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `DelegationSettings` in the `delegation` section of `cockpit.json`.
internal sealed class DelegationSettingsEntry
{
    public bool McpEnabled { get; set; } = true;

    public static DelegationSettingsEntry FromDomain(DelegationSettings settings) => new()
    {
        McpEnabled = settings.McpEnabled,
    };

    public DelegationSettings ToDomain() => new()
    {
        McpEnabled = McpEnabled,
    };
}
