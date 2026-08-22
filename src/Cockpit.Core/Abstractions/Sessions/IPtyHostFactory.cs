namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Spawns the OS-specific pseudo console/pty host behind <see cref="IConPtyProcess"/>. Registered
/// per platform (Windows → ConPTY, Linux/macOS → Porta.Pty) so <see cref="ITtyLauncher"/>
/// stays platform-agnostic — it only composes the executable path, arguments, environment and size.
/// </summary>
public interface IPtyHostFactory
{
    /// <summary>
    /// Starts <paramref name="executablePath"/> in a fresh pseudo console/pty of the given size, in <paramref
    /// name="workingDirectory"/> with exactly <paramref name="environment"/>. <paramref name="arguments"/> is
    /// the provider's launch-only start defaults; TTY mode never adds headless/stream-json flags, so the real TUI runs.
    /// </summary>
    IConPtyProcess Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows);
}
