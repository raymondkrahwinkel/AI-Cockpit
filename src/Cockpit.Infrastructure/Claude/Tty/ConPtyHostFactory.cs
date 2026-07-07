using Cockpit.Core.Abstractions.Claude;

namespace Cockpit.Infrastructure.Claude.Tty;

/// <summary>
/// Windows <see cref="IPtyHostFactory"/>: spawns the existing hand-rolled <see cref="ConPtyProcess"/>.
/// Registered only on Windows (<c>DependencyInjection.AddInfrastructure</c>) — behaviour unchanged
/// from before the Linux/macOS pty host (#9 cross-platform increment) existed.
/// </summary>
internal sealed class ConPtyHostFactory : IPtyHostFactory
{
    public IConPtyProcess Start(
        string executablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows) =>
        ConPtyProcess.Start(QuoteExecutable(executablePath), workingDirectory, environment, columns, rows);

    /// <summary>
    /// Wraps the executable path in quotes when it contains spaces (the bundled path lives under
    /// <c>%APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe</c>). <c>CreateProcessW</c> takes the
    /// whole command line as one string and parses argv itself — Unix's <c>execvp</c> takes the path
    /// as its own argv entry, so this quoting is a Windows-only concern.
    /// </summary>
    internal static string QuoteExecutable(string executablePath) =>
        executablePath.Contains(' ') && !executablePath.StartsWith('"')
            ? $"\"{executablePath}\""
            : executablePath;
}
