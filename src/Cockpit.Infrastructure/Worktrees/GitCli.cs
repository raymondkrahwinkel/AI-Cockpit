using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Cockpit.Infrastructure.Worktrees;

// Runs git for the worktree manager (AC-85). A thin wrapper over the git CLI, not a library binding: git's own
// refusals ("a branch named 'x' already exists") are what the cockpit surfaces, with no second copy of git's
// rules to keep in step with it.
internal static class GitCli
{
    // Hang guard, not a network timeout: a worktree add is slow but bounded, so this catches a git stuck on a
    // credential prompt or a wedged index lock. Kill is by tree since git shells out to helpers/submodules.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // git writes paths as UTF-8 on every platform; left unset, .NET decodes via the console code page,
            // mangling non-ASCII paths so a pathspec built from them matches nothing — and IsCleanAsync then reads
            // a worktree with unmerged work as clean. No-op where the console is already UTF-8.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Extra environment for the child git only — how the clone path (AC-90) turns off interactive prompting
        // (GIT_TERMINAL_PROMPT=0) so a missing credential helper fails fast. Set on the child's Environment only,
        // never the cockpit's.
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        // core.longpaths so a worktree checkout does not trip Windows' 260-character path limit. Harmless no-op
        // off Windows. Set per-invocation so it never depends on the operator's global config.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.longpaths=true");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Checked up front rather than inferred from the Process.Start() failure below: a missing directory and a
        // missing git binary both surface as the same Win32Exception shape, and a caller (AC-507) needs the two
        // told apart.
        if (!Directory.Exists(workingDirectory))
        {
            throw new InvalidOperationException($"Could not run git — the working directory does not exist: '{workingDirectory}'.");
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

        // A caller on the session-start path can ask for a shorter guard than the default: waiting two minutes on a
        // fetch that is never going to answer would itself be the delay the fallback exists to avoid (AC-349).
        var guard = timeout ?? DefaultTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(guard);

        // Both streams are drained concurrently. A git that fills the stderr pipe while nothing reads it blocks on
        // the write and never exits, so reading stdout to the end before touching stderr can deadlock on a chatty
        // command. Starting both reads first and waiting on exit after gates correctly on end-of-stream.
        var readStandardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var readStandardError = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var standardOutput = await readStandardOutput.ConfigureAwait(false);
            var standardError = await readStandardError.ConfigureAwait(false);

            return new GitResult(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _Kill(process);
            throw new InvalidOperationException(
                $"git {_RedactArguments(arguments)} did not finish within {guard.TotalSeconds:F0}s and was stopped.");
        }
        catch (OperationCanceledException)
        {
            _Kill(process);
            throw;
        }
    }

    // Runs git and returns its trimmed output, throwing what git said on a non-zero exit.
    public static async Task<string> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await RunAsync(workingDirectory, arguments, cancellationToken, environment).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(DescribeFailure(result));
        }

        return result.StandardOutput.Trim();
    }

    // What git refused with, in the words it used — not "git exited with 128" — with checkout progress chatter
    // stripped first so a failed worktree add shows the actual error. URL userinfo is redacted too, since git
    // echoes the remote URL in its own failures.
    internal static string DescribeFailure(GitResult result)
    {
        var said = RedactUrlCredentials(StripProgress(result.StandardError));
        return said.Length > 0 ? said : $"git exited with {result.ExitCode}.";
    }

    // Drops git's transfer/checkout progress chatter from `standardError` so the surfaced error is the diagnosis,
    // not the progress bar. Splits on both line terminators since git overwrites progress with a carriage return.
    // Falls back to the raw text if stripping would leave nothing.
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

    // Blanks any URL userinfo (`https://user:token@host`) in `text` bound for an exception message or log — git's
    // stderr echoes the remote URL in its failures, so a pasted token would otherwise ride along.
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
