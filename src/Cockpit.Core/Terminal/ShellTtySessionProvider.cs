using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Terminal;

// Runs a plain shell in a terminal pane (#AC-25) through the same pty path as the agent CLIs. It is the thinnest
// possible `ITtySessionProvider`: a shell has no options, no permissions, no MCP and no status to relay.
// Unlike the plugin providers it is not resolved through `ITtySessionProviderResolver` (a terminal has no profile).
public sealed class ShellTtySessionProvider(ShellDescriptor shell) : ITtySessionProvider
{
    // The provider id terminal panes launch under — not a real agent CLI, so it is its own reserved word.
    public const string ProviderKey = "shell";

    private static readonly IReadOnlyDictionary<string, string?> _NoEnvironmentOverlay =
        new Dictionary<string, string?>();

    public string ProviderId => ProviderKey;

    public TtyLaunchSpec BuildLaunch(TtyLaunchContext context) =>
        new(
            shell.ExecutablePath,
            shell.Arguments,
            _NoEnvironmentOverlay,
            context.WorkingDirectory,
            SessionScopedFiles: []);
}
