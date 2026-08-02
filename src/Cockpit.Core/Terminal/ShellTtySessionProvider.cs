using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Terminal;

// Runs a plain shell in a terminal pane (#AC-25) through the same pty path as the agent CLIs. It is the thinnest
// possible `ITtySessionProvider`: a shell has no options, no permissions, no MCP and no status to relay,
// so `BuildLaunch` just names the shell's resolved executable and its interactive arguments and runs it
// in the session's working directory. Everything Claude's provider does — trust marking, statusline relay,
// `--mcp-config`, launch flags — is deliberately absent.
// Constructed per terminal session from a `ShellDescriptor` the `ShellCatalog` resolved, so
// the executable path is already absolute and spawnable. Unlike the plugin providers it is not registered or resolved
// through `ITtySessionProviderResolver` (a terminal has no profile); the terminal session hands it to the
// launcher directly.
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
