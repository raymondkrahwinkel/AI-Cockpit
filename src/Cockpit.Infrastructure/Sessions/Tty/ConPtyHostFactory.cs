using System.Text;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

// Windows `IPtyHostFactory`: spawns the existing hand-rolled `ConPtyProcess`.
// Registered only on Windows (`DependencyInjection.AddInfrastructure`) — behaviour unchanged
// from before the Linux/macOS pty host (#9 cross-platform increment) existed.
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

    // Builds the single command-line string `CreateProcessW` expects: the executable followed by
    // each argument, quoted where needed. `CreateProcessW` parses argv out of one string — unlike
    // Unix's `execvp`, which takes the path and argv array separately (see
    // `PortaPtyProcess`'s `PtyOptions.CommandLine` usage) — so this is a Windows-only
    // concern.
    internal static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(QuoteArgument(executablePath));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ').Append(QuoteArgument(argument));
        }

        return commandLine.ToString();
    }

    // Escapes a single token the way `CommandLineToArgvW` (which is how the child parses argv back
    // out of the one string `CreateProcessW` receives) expects — Microsoft's canonical algorithm.
    // A token is left bare when it needs no quoting; otherwise it is wrapped in quotes, embedded
    // `"` are escaped as `\"`, and any run of backslashes that precedes a quote (or the
    // closing quote) is doubled. This is not optional prettiness: TTY arguments now include
    // `--settings &lt;json&gt;` (the statusline relay) and `--append-system-prompt`, whose
    // values carry spaces *and* double quotes. The old "quote only when it has a space" check
    // split that JSON at its first space and handed the child broken argv, which exited on the spot.
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
