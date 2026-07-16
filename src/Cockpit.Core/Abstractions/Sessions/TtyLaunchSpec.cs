namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Everything a provider needs to say to get its CLI running in a pty: which program, with which
/// arguments, and what it needs on top of the host's environment. Produced by an
/// <see cref="ITtySessionProvider"/>, consumed by <see cref="ITtyLauncher"/> — which is the only
/// place that talks to <see cref="IPtyHostFactory"/>.
/// </summary>
/// <param name="ExecutablePath">The program to run. The provider resolves it: only it knows where its CLI lives.</param>
/// <param name="Arguments">Launch-only start defaults, in the provider's own CLI syntax.</param>
/// <param name="EnvironmentOverlay">
/// Laid over the host's base environment (<see cref="Cockpit.Core.Sessions.Tty.TtyEnvironment.BuildBase"/>),
/// never in place of it. A provider adds what its CLI needs; it does not get to decide what the host strips,
/// because the scrub of inherited credentials is a security rule and belongs in one place.
/// A <see langword="null"/> value removes the variable from the base map — clearing an inherited
/// <c>CLAUDE_CONFIG_DIR</c> is the fix for a real bug, so removal has to be expressible, not just assignment.
/// </param>
/// <param name="WorkingDirectory">Absolute path the pty child runs in.</param>
/// <param name="SessionScopedFiles">
/// Files written for this one session — an MCP config carrying bearer headers, a status snapshot. The launcher
/// deletes them when the session is disposed, so a credential never outlives the thing that needed it.
/// </param>
/// <param name="StatusFile">
/// Optional path the session writes its own status to (context window, rate limits) for the header to read.
/// Also deleted with the session. Null when the provider has nothing to report — the header then shows no limits.
/// </param>
public sealed record TtyLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentOverlay,
    string WorkingDirectory,
    IReadOnlyList<string> SessionScopedFiles,
    string? StatusFile = null);
