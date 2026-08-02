using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// What came of reading `.cockpit/project.json` from a Depot project (AC-244) — reuses `PluginMcpToolCallOutcome` rather than inventing a parallel enum.
public sealed record CockpitProjectDefinitionReadResult(
    PluginMcpToolCallOutcome Outcome, CockpitProjectDefinition? Definition, string? Checksum, string? Error)
{
    public static CockpitProjectDefinitionReadResult AuthorizationRequired { get; } =
        new(PluginMcpToolCallOutcome.AuthorizationRequired, null, null, null);

    public static CockpitProjectDefinitionReadResult Success(CockpitProjectDefinition definition, string checksum) =>
        new(PluginMcpToolCallOutcome.Success, definition, checksum, null);

    public static CockpitProjectDefinitionReadResult Failed(string error) =>
        new(PluginMcpToolCallOutcome.Failed, null, null, error);
}
