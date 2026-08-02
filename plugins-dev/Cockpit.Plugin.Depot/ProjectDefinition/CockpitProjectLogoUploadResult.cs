using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>What came of uploading <c>.cockpit/logo.png</c> to a Depot project (AC-244).</summary>
public sealed record CockpitProjectLogoUploadResult(PluginMcpToolCallOutcome Outcome, string? Error)
{
    public static CockpitProjectLogoUploadResult AuthorizationRequired { get; } =
        new(PluginMcpToolCallOutcome.AuthorizationRequired, null);

    public static CockpitProjectLogoUploadResult Success { get; } = new(PluginMcpToolCallOutcome.Success, null);

    public static CockpitProjectLogoUploadResult Failed(string error) => new(PluginMcpToolCallOutcome.Failed, error);
}
