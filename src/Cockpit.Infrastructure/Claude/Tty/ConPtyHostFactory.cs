using System.Text;
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
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows) =>
        ConPtyProcess.Start(BuildCommandLine(executablePath, arguments), workingDirectory, environment, columns, rows);

    /// <summary>
    /// Builds the single command-line string <c>CreateProcessW</c> expects: the executable followed by
    /// each argument, quoted where needed. <c>CreateProcessW</c> parses argv out of one string — unlike
    /// Unix's <c>execvp</c>, which takes the path and argv array separately (see
    /// <see cref="PortaPtyProcess"/>'s <c>PtyOptions.CommandLine</c> usage) — so this is a Windows-only
    /// concern.
    /// </summary>
    internal static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(QuoteIfNeeded(executablePath));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ').Append(QuoteIfNeeded(argument));
        }

        return commandLine.ToString();
    }

    /// <summary>
    /// Wraps a token in quotes when it contains spaces (the bundled path lives under
    /// <c>%APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe</c>). The launch arguments TTY mode
    /// builds (mode/model/effort values) never contain spaces, so this naive check — not a full argv
    /// escaping algorithm — covers every token this factory ever quotes.
    /// </summary>
    internal static string QuoteIfNeeded(string value) =>
        value.Contains(' ') && !value.StartsWith('"') ? $"\"{value}\"" : value;
}
