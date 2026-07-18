namespace Cockpit.Plugin.Docker.Compose;

/// <summary>
/// Runs the <c>docker compose</c> CLI (AC-84 §12 — compose is driven through the CLI, not the Engine API). Kept
/// behind an interface so the MCP tools that carry the consent gate are testable without spawning a real process.
/// Arguments are passed as argv (never a shell string), so an agent-supplied service name or path cannot inject a
/// second command.
/// </summary>
internal interface IComposeCli
{
    /// <summary>
    /// Runs <c>docker compose &lt;args&gt;</c> in <paramref name="workingDirectory"/> and returns its exit code and output.
    /// </summary>
    Task<ComposeResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>The result of a <c>docker compose</c> invocation.</summary>
internal sealed record ComposeResult(int ExitCode, string Stdout, string Stderr);
