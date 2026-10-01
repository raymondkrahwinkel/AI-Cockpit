using Cockpit.Core.Profiles;

namespace Cockpit.Core.Sessions;

// AC-1378: everything a session's start hands the runtime, so the desktop pane and the backend launcher start a
// session through the same steps in the same order. AC-1449: in Core, as what `ISessionControl.StartAsync` takes.
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
    bool PreApproveAllTools = false);
