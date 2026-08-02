using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// What came of writing `.cockpit/project.json` to a Depot project (AC-244). A `baseChecksum` mismatch surfaces as `PluginMcpToolCallOutcome.Failed` with Depot's own error text — see `CockpitProjectDefinitionStore`'s remarks on why this does not add a dedicated conflict outcome.
public sealed record CockpitProjectDefinitionWriteResult(PluginMcpToolCallOutcome Outcome, string? Checksum, string? Error)
{
    public static CockpitProjectDefinitionWriteResult AuthorizationRequired { get; } =
        new(PluginMcpToolCallOutcome.AuthorizationRequired, null, null);

    public static CockpitProjectDefinitionWriteResult Success(string checksum) =>
        new(PluginMcpToolCallOutcome.Success, checksum, null);

    public static CockpitProjectDefinitionWriteResult Failed(string error) =>
        new(PluginMcpToolCallOutcome.Failed, null, error);
}
