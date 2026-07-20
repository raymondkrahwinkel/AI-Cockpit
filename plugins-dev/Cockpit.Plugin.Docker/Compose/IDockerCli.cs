namespace Cockpit.Plugin.Docker.Compose;

/// <summary>
/// Runs the plain <c>docker</c> CLI for the few operations that are far simpler through it than the Engine API —
/// <c>build</c> (a build context to tar and stream), <c>cp</c> (tar in/out of a container), and <c>push</c> (registry
/// auth and progress) — the same "drive it through the CLI" choice the compose tools make. Behind an interface so the
/// MCP tools that carry the consent gate are testable without spawning a real process; arguments are passed as argv
/// (never a shell string), so an agent-supplied path or tag cannot inject a second command.
/// </summary>
internal interface IDockerCli
{
    /// <summary>Runs <c>docker &lt;args&gt;</c> and returns its exit code and output.</summary>
    Task<DockerCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>The result of a plain <c>docker</c> CLI invocation.</summary>
internal sealed record DockerCliResult(int ExitCode, string Stdout, string Stderr);
