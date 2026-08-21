using System.Diagnostics;
using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Worktrees;

internal readonly record struct DockerCliResult(int ExitCode, string StandardOutput, string StandardError);

// The seam the worktree teardown's docker cleanup (AC-1010) runs against, so a test can fake docker's answers
// instead of needing a real daemon (which a CI box or a sandboxed session may not have running at all).
internal interface IDockerCli
{
    Task<DockerCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

// `IDockerCli` backed by the real `docker` CLI via `Process` — the same binary an operator's own shell would use.
// Deliberately its own copy rather than a reference to the Docker plugin's equivalent: worktree teardown is core
// session lifecycle and must keep working whether or not that plugin is installed (the layering rule in CLAUDE.md).
internal sealed class DockerCli : IDockerCli, ISingletonService
{
    // A hang guard, not a network timeout: `docker ps`/`rm`/`volume rm` are normally instant, so a daemon that is
    // installed but wedged or unreachable must not stall a worktree removal indefinitely.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<DockerCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
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
            throw new InvalidOperationException($"Could not run 'docker' — is it installed and on PATH? ({exception.Message})", exception);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        var readStandardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var readStandardError = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new DockerCliResult(process.ExitCode, await readStandardOutput.ConfigureAwait(false), await readStandardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _Kill(process);
            throw new InvalidOperationException($"docker {string.Join(' ', arguments)} did not finish within {Timeout.TotalSeconds:F0}s and was stopped.");
        }
        catch (OperationCanceledException)
        {
            _Kill(process);
            throw;
        }
    }

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
            // Already gone, or unsignalable — the caller is about to see the cancellation either way.
        }
    }
}
