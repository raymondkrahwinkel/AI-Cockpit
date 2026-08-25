namespace Cockpit.Core.Terminal;

// One shell the operator can open as a plain terminal pane (#AC-25), distinct from an agent CLI
// (`ITtySessionProvider`): a shell has no options, no permissions and no MCP.
// `ExecutablePath` is resolved to an absolute, spawnable path at detection time.
public sealed record ShellDescriptor(
    string Id,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments);
