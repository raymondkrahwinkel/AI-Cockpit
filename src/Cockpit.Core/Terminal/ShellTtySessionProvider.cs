using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Terminal;

/// <summary>
/// Runs a plain shell in a terminal pane (#AC-25) through the same pty path as the agent CLIs. It is the thinnest
/// possible <see cref="ITtySessionProvider"/>: a shell has no options, no permissions, no MCP and no status to relay,
/// so <see cref="BuildLaunch"/> just names the shell's resolved executable and its interactive arguments and runs it
/// in the session's working directory. Everything Claude's provider does — trust marking, statusline relay,
/// <c>--mcp-config</c>, launch flags — is deliberately absent.
/// </summary>
/// <remarks>
/// Constructed per terminal session from a <see cref="ShellDescriptor"/> the <see cref="ShellCatalog"/> resolved, so
/// the executable path is already absolute and spawnable. Unlike the plugin providers it is not registered or resolved
/// through <see cref="ITtySessionProviderResolver"/> (a terminal has no profile); the terminal session hands it to the
/// launcher directly.
/// </remarks>
public sealed class ShellTtySessionProvider(ShellDescriptor shell) : ITtySessionProvider
{
    /// <summary>The provider id terminal panes launch under — not a real agent CLI, so it is its own reserved word.</summary>
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
