using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Tests;

// Only what run_local_checks's checkout resolution reads: the registered worktrees. Every other member of
// IWorktreeManager is unreached from the MCP tools and throws if a test ever exercises it by mistake.
internal sealed class FakeWorktreeManager : IWorktreeManager
{
    public List<WorktreeRecord> Records { get; } = [];

    public event Action<WorktreeSourceRefresh>? SourceRefreshed { add { } remove { } }

    public Task<IReadOnlyList<WorktreeRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorktreeRecord>>(Records);

    public Task<GitRepositoryInfo?> DetectRepositoryAsync(string directory, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<WorktreeRecord> CreateAsync(string sessionId, string branch, string directory, WorktreeSourceHandling handling = WorktreeSourceHandling.BringUpToDate, bool isAgentCreated = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<WorktreeRecord> CreateForSessionAsync(string sessionId, string? sessionLabel, string directory, WorktreeSourceHandling handling = WorktreeSourceHandling.BringUpToDate, bool isAgentCreated = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<WorktreeStatus>> GetStatusesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> IsCleanAsync(WorktreeRecord record, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> HasUncommittedChangesAsync(WorktreeRecord record, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<string?> RemoveAsync(WorktreeRecord record, bool force = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<WorktreeRecord?> ReattachAsync(string worktreePath, string newSessionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ReleaseOwnershipAsync(string worktreePath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ReconcileAsync(IReadOnlyCollection<string> liveSessionIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

// Stands in for act. The endings that matter — a tool that is not there, a job that fails, a run that is stopped
// halfway — are exactly the ones a real act on a real Docker will not produce on request.
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

    // Runs until cancelled, signalling `running` once it is under way.
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

// A checkout on disk with workflows in it — what the approval reads, so it cannot be faked away.
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

    // A job whose steps include the two actions the classifier allows — the setup half of a real run (AC-617).
    public const string JobWithSetupActions = """
        name: CI
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v7
              - uses: actions/setup-dotnet@v6
              - name: Build
                run: dotnet build
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
