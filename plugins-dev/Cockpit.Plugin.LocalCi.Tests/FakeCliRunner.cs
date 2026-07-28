using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Tests;

/// <summary>
/// Stands in for the real processes so the branches that matter can be tested: a Docker that is not installed, an
/// engine that does not answer, a pipe that never answers at all. None of those can be produced on demand on a
/// machine that has a working Docker.
/// </summary>
internal sealed class FakeCliRunner : ICliRunner
{
    private readonly Dictionary<string, CliResult> _byExecutable = new(StringComparer.OrdinalIgnoreCase);

    public List<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Calls { get; } = [];

    public FakeCliRunner Returns(string fileName, CliResult result)
    {
        _byExecutable[fileName] = result;
        return this;
    }

    public Task<CliResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((fileName, arguments, timeout));
        return Task.FromResult(_byExecutable.GetValueOrDefault(fileName, CliResult.NotStarted));
    }
}
