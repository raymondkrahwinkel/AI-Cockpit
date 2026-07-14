using System.Text;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

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
        var commandLine = new StringBuilder(QuoteArgument(executablePath));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ').Append(QuoteArgument(argument));
        }

        return commandLine.ToString();
    }

    /// <summary>
    /// Escapes a single token the way <c>CommandLineToArgvW</c> (which is how the child parses argv back
    /// out of the one string <c>CreateProcessW</c> receives) expects — Microsoft's canonical algorithm.
    /// A token is left bare when it needs no quoting; otherwise it is wrapped in quotes, embedded
    /// <c>"</c> are escaped as <c>\"</c>, and any run of backslashes that precedes a quote (or the
    /// closing quote) is doubled. This is not optional prettiness: TTY arguments now include
    /// <c>--settings &lt;json&gt;</c> (the statusline relay) and <c>--append-system-prompt</c>, whose
    /// values carry spaces <em>and</em> double quotes. The old "quote only when it has a space" check
    /// split that JSON at its first space and handed the child broken argv, which exited on the spot.
    /// </summary>
    internal static string QuoteArgument(string value)
    {
        // A non-empty token with nothing the parser treats specially needs no quoting at all.
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var backslashes = 0;
            while (i < value.Length && value[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (i == value.Length)
            {
                // Backslashes just before the closing quote are doubled so they stay literal.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (value[i] == '"')
            {
                // Double the run and add one more to escape the quote itself.
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                // Backslashes not before a quote are literal; leave the run untouched.
                builder.Append('\\', backslashes).Append(value[i]);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
