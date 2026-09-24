using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1378: everything `SessionHost.StartAsync` hands the runtime, so the desktop pane and the backend launcher start a
// session through the same steps in the same order.
public sealed record SessionStart(
    SessionProfile? Profile,
    string? PermissionMode,
    string? Model,
    IReadOnlySet<string>? EnabledMcpServerNames,
    string? WorkingDirectory,
    SessionResume? Resume,
    IReadOnlyDictionary<string, string>? LaunchOptions,
    string? ProjectId,
    IReadOnlyList<string>? PreApprovedTools = null,
    bool PreApproveAllTools = false)
{
    // AC-13: the pane's own id rides along, which the provider's plugin turns into COCKPIT_PANE_ID for `set_status`.
    public static IReadOnlyDictionary<string, string> WithPaneId(IReadOnlyDictionary<string, string>? launchOptions, string paneId)
    {
        var merged = launchOptions is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(launchOptions, StringComparer.OrdinalIgnoreCase);
        merged[WellKnownPluginSessionOptions.PaneId] = paneId;
        return merged;
    }
}
