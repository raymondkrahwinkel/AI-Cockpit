using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// What came of downloading `.cockpit/logo.png` from a Depot project (AC-244).
public sealed record CockpitProjectLogoDownloadResult(PluginMcpToolCallOutcome Outcome, byte[]? Bytes, string? Error)
{
    public static CockpitProjectLogoDownloadResult AuthorizationRequired { get; } =
        new(PluginMcpToolCallOutcome.AuthorizationRequired, null, null);

    public static CockpitProjectLogoDownloadResult Success(byte[] bytes) =>
        new(PluginMcpToolCallOutcome.Success, bytes, null);

    public static CockpitProjectLogoDownloadResult Failed(string error) =>
        new(PluginMcpToolCallOutcome.Failed, null, error);
}
