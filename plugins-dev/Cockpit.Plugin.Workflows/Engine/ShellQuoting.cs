using System.Text;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Quotes one value so it survives as a single inert argument when a resolved placeholder is spliced into the
/// command a step runs (AC-39). The command step runs an operator's template through a real shell — <c>/bin/sh -c</c>
/// or <c>cmd.exe /c</c> — and only the values substituted from earlier steps (trigger text, fetched content) are
/// untrusted. Quoting <em>those</em>, not the template, keeps the operator's own shell features (pipes, <c>;</c>,
/// redirects) working while a value like <c>; rm -rf ~</c> can no longer break out of its argument.
/// </summary>
internal static class ShellQuoting
{
    /// <summary>The quoter matching the shell the command step actually launches on this platform.</summary>
    public static Func<string, string> ForCurrentShell() =>
        OperatingSystem.IsWindows() ? QuoteCmd : QuotePosix;

    /// <summary>
    /// POSIX single-quoting: inside single quotes every character is literal, so wrap the value in them and, for the
    /// one character single quotes cannot contain, close the quote, add an escaped quote, and reopen (<c>'\''</c>).
    /// Fully injection-proof for <c>/bin/sh</c> (and macOS, which takes the same path).
    /// </summary>
    public static string QuotePosix(string value) => $"'{value.Replace("'", "'\\''")}'";

    /// <summary>
    /// cmd.exe has no single-quote equivalent, so the value is carried through cmd's own parse by caret-escaping the
    /// metacharacters that would otherwise chain, group or redirect a command — the injection-to-RCE vector. Residual,
    /// stated honestly: cmd expands <c>%VAR%</c> and (with delayed expansion) <c>!VAR!</c> in an earlier phase that a
    /// caret cannot reliably suppress in a <c>/c</c> string, so a value containing <c>%SOMEVAR%</c> may still expand —
    /// an information disclosure, not command execution. The RCE separators are neutralised on every platform.
    /// </summary>
    public static string QuoteCmd(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (character is '^' or '&' or '|' or '<' or '>' or '(' or ')' or '"')
            {
                builder.Append('^');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
