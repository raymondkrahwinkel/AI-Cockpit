using Cockpit.Core.Delegation;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="DelegationSettings"/> in the <c>delegation</c> section of <c>cockpit.json</c>.</summary>
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
