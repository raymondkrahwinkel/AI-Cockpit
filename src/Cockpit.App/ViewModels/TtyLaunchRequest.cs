using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// TTY launch payload: a record keeps its many panel/view context values named instead of positional.
// #44 narrows MCP servers to the dialog selection; AC-165 plugins and AC-218's project use scoped context.
// Options are launch-only because the hosted TUI owns subsequent changes.
public sealed record TtyLaunchRequest(
    ITtyLauncher Launcher,
    ITtySessionProvider Provider,
    SessionProfile? Profile,
    IReadOnlyDictionary<string, string> Options,
    string? WorkingDirectory,
    SessionResume? Resume,
    IReadOnlySet<string>? EnabledMcpServerNames = null,
    SessionResources? Contributed = null,
    string? ProjectId = null);
