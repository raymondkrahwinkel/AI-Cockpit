namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Spawns the OS-specific pseudo console/pty host behind <see cref="IConPtyProcess"/>. Registered
/// per platform (Windows → ConPTY, Linux/macOS → Porta.Pty) so <see cref="ITtyLauncher"/>
/// stays platform-agnostic — it only composes the executable path, arguments, environment and size.
/// </summary>
public interface IPtyHostFactory
{
    /// <summary>
    /// Starts <paramref name="executablePath"/> inside a fresh pseudo console/pty of the given size,
    /// in <paramref name="workingDirectory"/>, with exactly <paramref name="environment"/> as its
    /// environment. <paramref name="arguments"/> carries the launch-only start defaults the provider
    /// composed (see <see cref="ITtySessionProvider"/>); TTY mode never adds the headless/stream-json
    /// flags, so the genuine interactive TUI still runs.
    /// </summary>
    IConPtyProcess Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows);
}
