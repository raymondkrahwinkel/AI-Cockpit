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

    // Builds the single command-line string `CreateProcessW` expects: the executable followed by each argument,
    // quoted where needed. `CreateProcessW` parses argv out of one string, unlike Unix's `execvp` (path + argv
    // array separately, see `PortaPtyProcess`'s `PtyOptions.CommandLine`) — so this is Windows-only.
    internal static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(QuoteArgument(executablePath));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ').Append(QuoteArgument(argument));
        }

        return commandLine.ToString();
    }

    // Escapes a token the way `CommandLineToArgvW` expects (Microsoft's canonical algorithm): bare when it needs
    // no quoting, else quoted with embedded `"` as `\"` and runs of backslashes before a quote doubled. Not
    // optional prettiness — `--settings &lt;json&gt;` and `--append-system-prompt` values carry spaces and quotes.
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
