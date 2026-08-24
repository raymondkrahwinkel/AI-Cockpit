namespace Cockpit.Core.Abstractions.Sessions;

// AC-1013: Everything a provider needs to get its CLI running in a pty. Produced by an
// ITtySessionProvider, consumed by ITtyLauncher (the only caller of IPtyHostFactory). Trimmed:
// EnvironmentOverlay only adds to the scrubbed base env (null removes a var, fixing a real CLAUDE_CONFIG_DIR bug); SessionScopedFiles/StatusFile are deleted with the session so credentials don't outlive it.
public sealed record TtyLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentOverlay,
    string WorkingDirectory,
    IReadOnlyList<string> SessionScopedFiles,
    string? StatusFile = null);
