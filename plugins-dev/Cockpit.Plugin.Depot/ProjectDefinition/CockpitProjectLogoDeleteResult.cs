using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// What came of deleting `.cockpit/logo.png` from a Depot project (AC-763).
public sealed record CockpitProjectLogoDeleteResult(PluginMcpToolCallOutcome Outcome, string? Error)
{
    public static CockpitProjectLogoDeleteResult AuthorizationRequired { get; } =
        new(PluginMcpToolCallOutcome.AuthorizationRequired, null);

    public static CockpitProjectLogoDeleteResult Success { get; } = new(PluginMcpToolCallOutcome.Success, null);

    public static CockpitProjectLogoDeleteResult Failed(string error) => new(PluginMcpToolCallOutcome.Failed, error);
}
