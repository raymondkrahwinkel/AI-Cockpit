using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>What came of writing <c>.cockpit/project.json</c> to a Depot project (AC-244). A <c>baseChecksum</c> mismatch surfaces as <see cref="PluginMcpToolCallOutcome.Failed"/> with Depot's own error text — see <see cref="CockpitProjectDefinitionStore"/>'s remarks on why this does not add a dedicated conflict outcome.</summary>
public sealed record CockpitProjectDefinitionWriteResult(PluginMcpToolCallOutcome Outcome, string? Checksum, string? Error)
{
    public static CockpitProjectDefinitionWriteResult AuthorizationRequired { get; } =
        new(PluginMcpToolCallOutcome.AuthorizationRequired, null, null);

    public static CockpitProjectDefinitionWriteResult Success(string checksum) =>
        new(PluginMcpToolCallOutcome.Success, checksum, null);

    public static CockpitProjectDefinitionWriteResult Failed(string error) =>
        new(PluginMcpToolCallOutcome.Failed, null, error);
}
