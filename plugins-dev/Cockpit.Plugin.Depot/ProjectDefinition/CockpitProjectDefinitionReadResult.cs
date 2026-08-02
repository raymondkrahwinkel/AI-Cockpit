using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>What came of reading <c>.cockpit/project.json</c> from a Depot project (AC-244) — reuses <see cref="PluginMcpToolCallOutcome"/> rather than inventing a parallel enum.</summary>
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
