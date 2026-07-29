using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Tests;

/// <summary>
/// Stands in for act. The endings that matter — a tool that is not there, a job that fails, a run that is stopped
/// halfway — are exactly the ones a real act on a real Docker will not produce on request.
/// </summary>
internal sealed class FakeStreamingCliRunner(Func<Action<string>, CancellationToken, Task<StreamedRun>> behaviour)
    : IStreamingCliRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)> Calls { get; } = [];

    public static FakeStreamingCliRunner Exiting(int exitCode, params string[] lines) =>
        new((onLine, _) =>
        {
            foreach (var line in lines)
            {
                onLine(line);
            }

            return Task.FromResult(new StreamedRun(Started: true, exitCode));
        });

    public static FakeStreamingCliRunner NeverStarts() =>
        new((_, _) => Task.FromResult(StreamedRun.NotStarted));

    /// <summary>Runs until cancelled, signalling <paramref name="running"/> once it is under way.</summary>
    public static FakeStreamingCliRunner Blocking(TaskCompletionSource running) =>
        new(async (_, cancellationToken) =>
        {
            running.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new StreamedRun(Started: true, ExitCode: 0);
        });

    public Task<StreamedRun> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments, workingDirectory));
        return behaviour(onLine, cancellationToken);
    }
}

internal sealed class FakeRunContainerCleanup : IRunContainerCleanup
{
    public List<string> Removed { get; } = [];

    public Task RemoveAsync(string runId, CancellationToken cancellationToken)
    {
        Removed.Add(runId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeLocalCiRuntime(LocalCiRuntimeStatus status) : ILocalCiRuntime
{
    public static FakeLocalCiRuntime Ready() =>
        new(new LocalCiRuntimeStatus(
            new DockerRuntimeStatus(DockerRuntimeState.Usable, "linux", "29.5.3"),
            new ActRuntimeStatus(IsInstalled: true, "0.2.89")));

    public static FakeLocalCiRuntime WithoutDocker() =>
        new(new LocalCiRuntimeStatus(DockerRuntimeStatus.NotInstalled, new ActRuntimeStatus(IsInstalled: true, "0.2.89")));

    public static FakeLocalCiRuntime WithoutAct() =>
        new(new LocalCiRuntimeStatus(
            new DockerRuntimeStatus(DockerRuntimeState.Usable, "linux", "29.5.3"),
            ActRuntimeStatus.NotInstalled));

    public Task<LocalCiRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(status);

    public void Invalidate()
    {
    }
}

/// <summary>A checkout on disk with workflows in it — what the approval reads, so it cannot be faked away.</summary>
internal sealed class TemporaryProject : IDisposable
{
    public const string OneLinuxJob = """
        name: CI
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo building
        """;

    public const string MatrixJob = """
        name: CI
        on: push
        jobs:
          spread:
            runs-on: ubuntu-latest
            strategy:
              matrix:
                os: [ubuntu-latest, windows-latest]
            steps:
              - run: echo building
        """;

    public TemporaryProject()
    {
        Root = Path.Combine(Path.GetTempPath(), "local-ci-run-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(Root, ".github", "workflows"));
    }

    public string Root { get; }

    public string AddWorkflow(string fileName, string yaml)
    {
        var path = Path.Combine(Root, ".github", "workflows", fileName);
        File.WriteAllText(path, yaml);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
