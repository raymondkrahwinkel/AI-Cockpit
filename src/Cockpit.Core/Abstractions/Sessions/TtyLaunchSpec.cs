namespace Cockpit.Core.Abstractions.Sessions;

// Everything a provider needs to say to get its CLI running in a pty: which program, with which
// arguments, and what it needs on top of the host's environment. Produced by an
// `ITtySessionProvider`, consumed by `ITtyLauncher` — which is the only
// place that talks to `IPtyHostFactory`.
//
// `ExecutablePath`: The program to run. The provider resolves it: only it knows where its CLI lives.
// `Arguments`: Launch-only start defaults, in the provider's own CLI syntax.
// `EnvironmentOverlay`:
// Laid over the host's base environment (`Cockpit.Core.Sessions.Tty.TtyEnvironment.BuildBase`),
// never in place of it. A provider adds what its CLI needs; it does not get to decide what the host strips,
// because the scrub of inherited credentials is a security rule and belongs in one place.
// A `null` value removes the variable from the base map — clearing an inherited
// `CLAUDE_CONFIG_DIR` is the fix for a real bug, so removal has to be expressible, not just assignment.
// `WorkingDirectory`: Absolute path the pty child runs in.
// `SessionScopedFiles`:
// Files written for this one session — an MCP config carrying bearer headers, a status snapshot. The launcher
// deletes them when the session is disposed, so a credential never outlives the thing that needed it.
// `StatusFile`:
// Optional path the session writes its own status to (context window, rate limits) for the header to read.
// Also deleted with the session. Null when the provider has nothing to report — the header then shows no limits.
public sealed record TtyLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentOverlay,
    string WorkingDirectory,
    IReadOnlyList<string> SessionScopedFiles,
    string? StatusFile = null);
