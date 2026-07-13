using System.Diagnostics;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// Runs git, and tells you what it said (#69 workflow steps). The status reader runs git too, but only ever to
/// <em>read</em>; this is the half that changes things, and it is separate for that reason: a failure here is a
/// branch not cut, a commit not made — never something to swallow.
/// </summary>
internal static class GitCommand
{
    public static async Task<string> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (workingDirectory.Length == 0 || !Directory.Exists(workingDirectory))
        {
            throw new InvalidOperationException(
                workingDirectory.Length == 0
                    ? "This step has no working directory. Write one, or {directory} to take it from the step before."
                    : $"There is no directory '{workingDirectory}'.");
        }

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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
            throw new InvalidOperationException($"Could not run 'git' — is it installed and on PATH? ({exception.Message})", exception);
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            // git says why in words a person can act on ("a branch named 'x' already exists"), so that is what the
            // run shows — not "git exited with 128".
            var said = stderr.Trim();
            throw new InvalidOperationException(said.Length > 0 ? said : $"git exited with {process.ExitCode}.");
        }

        // git writes plenty of what it did to stderr (a push's summary, for one), and a step that reported nothing
        // because the interesting half went to the wrong stream would be a step you stop trusting.
        return (stdout.Trim() is { Length: > 0 } output ? output : stderr.Trim()).Trim();
    }

    /// <summary>The branch a repository is on, or empty in a detached head — which is a state a flow should not silently commit into.</summary>
    public static async Task<string> CurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var branch = await RunAsync(workingDirectory, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);

        return branch.Equals("HEAD", StringComparison.Ordinal) ? string.Empty : branch;
    }

    /// <summary>Whether anything is changed, staged or untracked — the question every step that touches a branch has to ask first.</summary>
    public static async Task<bool> HasChangesAsync(string workingDirectory, CancellationToken cancellationToken) =>
        (await RunAsync(workingDirectory, ["status", "--porcelain"], cancellationToken)).Length > 0;
}
