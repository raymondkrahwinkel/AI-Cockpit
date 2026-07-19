using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cockpit.Infrastructure.Worktrees;

/// <summary>
/// Runs git for the worktree manager (AC-85). A thin wrapper over the git CLI rather than a library binding: the
/// same binary the operator's own shell uses, so git's refusals — "a branch named 'x' already exists", "contains
/// modified or untracked files" — are the ones the cockpit surfaces, and there is no second copy of git's rules
/// to keep in step with it.
/// </summary>
internal static class GitCli
{
    // Not a network timeout — a worktree add copies a full checkout, which is slow but bounded. It is a hang guard:
    // a git waiting on a credential prompt or a wedged index lock would otherwise stall session start forever. The
    // kill is by tree because git shells out (a credential helper, a submodule clone), and killing only the parent
    // leaves those running.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Extra environment for the child git only — how the clone path (AC-90) turns off interactive prompting
        // (GIT_TERMINAL_PROMPT=0) so a missing credential helper fails fast instead of hanging on an invisible
        // prompt. Set on the child's own Environment, never the cockpit's, and the seam a future in-memory token
        // injection (GIT_ASKPASS + the token in this child env only) would extend without touching argv or config.
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        // core.longpaths so a worktree checkout — the app state root plus a deep repository path (a nested Angular
        // component tree is enough) — does not trip Windows' 260-character path limit with "Filename too long".
        // Git for Windows then writes through the \\?\ extended-length API regardless of the OS registry switch.
        // A harmless no-op off Windows and for git commands that create no files. Set per-invocation so it never
        // depends on the operator's global config.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.longpaths=true");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not run 'git' — is it installed and on PATH? ({exception.Message})", exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        // Both streams are drained concurrently. A git that fills the stderr pipe while nothing reads it blocks on
        // the write and never exits, so reading stdout to the end before touching stderr can deadlock on a chatty
        // command. Starting both reads first and waiting on exit after gates correctly on end-of-stream.
        var readStandardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var readStandardError = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var standardOutput = await readStandardOutput.ConfigureAwait(false);
            var standardError = await readStandardError.ConfigureAwait(false);

            return new GitResult(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _Kill(process);
            throw new InvalidOperationException(
                $"git {_RedactArguments(arguments)} did not finish within {Timeout.TotalSeconds:F0}s and was stopped.");
        }
        catch (OperationCanceledException)
        {
            _Kill(process);
            throw;
        }
    }

    /// <summary>Runs git and returns its trimmed output, throwing what git said on a non-zero exit.</summary>
    public static async Task<string> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await RunAsync(workingDirectory, arguments, cancellationToken, environment).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // git says why in words a person can act on ("a branch named 'x' already exists"); that is what the
            // caller sees, not "git exited with 128" — but with the checkout progress ("Updating files: 42% …",
            // written to stderr and carriage-returned over itself) stripped out first, so a failed worktree add
            // surfaces the actual error instead of a hundred percent-lines.
            // git echoes the remote URL in its own failures ("fatal: unable to access 'https://user:token@host/…'"),
            // so redact any URL userinfo before this reaches the caller's dialog/log — the same binding rule the
            // display of the arguments follows. Belt and suspenders with GitCloneUrl stripping credentials up front.
            var said = RedactUrlCredentials(StripProgress(result.StandardError));
            throw new InvalidOperationException(said.Length > 0 ? said : $"git exited with {result.ExitCode}.");
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Drops git's transfer/checkout progress chatter ("Updating files:", "Receiving objects:", …) from
    /// <paramref name="standardError"/>, so an error surfaced to the operator is the diagnosis, not the progress bar
    /// that ran up to it. Splits on both line terminators because git overwrites progress in place with a carriage
    /// return. Falls back to the raw text if stripping would leave nothing, so a git that reports only via progress
    /// is never reduced to an empty message.
    /// </summary>
    internal static string StripProgress(string standardError)
    {
        var kept = standardError
            .Split('\r', '\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !_IsProgressLine(line))
            .ToList();

        return kept.Count > 0 ? string.Join(Environment.NewLine, kept) : standardError.Trim();
    }

    // Credentials embedded in an HTTP(S) URL argument (https://user:token@host/…) — never constructed by the
    // cockpit, but an operator can paste one — must not reach an exception message that ends up in a log. Blank the
    // userinfo before the arguments are joined for display. A binding rule: secret values never in argv/config/logs.
    private static readonly Regex _UrlUserInfo = new(@"://[^/@\s]+@", RegexOptions.Compiled);

    /// <summary>
    /// Blanks any URL userinfo (<c>https://user:token@host</c>) in <paramref name="text"/> bound for an exception
    /// message or a log. The same binding rule as <see cref="_RedactArguments"/>, applied to arbitrary text — git's
    /// own stderr echoes the remote URL in its failures, so a pasted token would otherwise ride a clone/fetch error
    /// straight into the dialog and the log.
    /// </summary>
    internal static string RedactUrlCredentials(string text) => _UrlUserInfo.Replace(text, "://***@");

    private static string _RedactArguments(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(RedactUrlCredentials));

    private static bool _IsProgressLine(string line) =>
        line.StartsWith("Updating files:", StringComparison.Ordinal)
        || line.StartsWith("Enumerating objects:", StringComparison.Ordinal)
        || line.StartsWith("Counting objects:", StringComparison.Ordinal)
        || line.StartsWith("Compressing objects:", StringComparison.Ordinal)
        || line.StartsWith("Receiving objects:", StringComparison.Ordinal)
        || line.StartsWith("Resolving deltas:", StringComparison.Ordinal);

    private static void _Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or unsignalable. The caller is about to see the cancellation either way; a failed kill
            // is not worth masking that with.
        }
    }
}
