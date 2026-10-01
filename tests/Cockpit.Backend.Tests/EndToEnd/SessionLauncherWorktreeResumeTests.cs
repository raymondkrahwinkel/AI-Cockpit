using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Configuration;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.Journeys;

namespace Cockpit.Backend.Tests.EndToEnd;

// AC-1448, layer 2 (data loss): the backend launcher's worktree and resume, J3's and J7's halves until the desktop
// starts through it (F4.7). Alone with the other state-root tests, since the state root is the process environment's.
[Collection(BackendWithoutAppTests.Alone)]
public sealed class SessionLauncherWorktreeResumeTests : IDisposable
{
    private readonly string? _previousStateRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"launcher-{Guid.NewGuid():N}");

    public SessionLauncherWorktreeResumeTests()
    {
        Directory.CreateDirectory(_stateRoot);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _stateRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _previousStateRoot);

        try
        {
            foreach (var file in Directory.EnumerateFiles(_stateRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_stateRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temp folder the OS clears is fine.
        }
    }

    [Fact]
    public async Task AnIsolatedSession_RunsInItsOwnWorktree_AndStoppingItRemovesTheWorktreeAndItsBranch()
    {
        var driver = new EchoDriver();
        await using var services = _Build(driver);
        var repository = await _RepositoryAsync(Path.Combine(_stateRoot, "repository"));
        var launcher = services.GetRequiredService<ISessionLauncher>();

        var started = await launcher.StartSessionAsync(await _RequestAsync(launcher, repository) with { IsolateInWorktree = true });
        var worktree = (await services.GetRequiredService<IWorktreeManager>().ListAsync()).Single(record => record.SessionId == started?.PaneId);
        var branchWhileRunning = await _BranchesAsync(repository, worktree.Branch);
        await launcher.StopSessionAsync(worktree.SessionId);

        Assert.Equal(worktree.Path, driver.WorkingDirectory);
        Assert.NotEqual(Path.GetFullPath(repository), Path.GetFullPath(worktree.Path));
        Assert.NotEmpty(branchWhileRunning);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Empty(await _BranchesAsync(repository, worktree.Branch));
    }

    [Fact]
    public async Task AResumedSession_GetsItsConversationId_AndItsRecordedRowsBeforeTheFirstTurn()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_stateRoot, "project")).FullName;
        var first = new EchoDriver();
        string paneId;
        await using (var before = _Build(first))
        {
            var launcher = before.GetRequiredService<ISessionLauncher>();
            paneId = (await launcher.StartSessionAsync(await _RequestAsync(launcher, folder) with { Prompt = "hello" }))?.PaneId ?? "";
            await first.Answered;
            await launcher.StopSessionAsync(paneId);
        }

        var second = new EchoDriver();
        await using var after = _Build(second);
        var relaunched = after.GetRequiredService<ISessionLauncher>();
        await relaunched.StartSessionAsync(await _RequestAsync(relaunched, folder) with
        {
            PaneId = paneId,
            Resume = SessionResume.BySessionId(first.SessionId ?? ""),
        });
        var handle = (SessionHostHandle?)after.GetRequiredService<SessionRegistry>().Find(paneId);

        Assert.Equal(SessionResume.BySessionId("echo-conversation"), second.Resume);
        Assert.Contains(handle?.Rows ?? [], row => row.Text == "echo: hello");
    }

    private static ServiceProvider _Build(EchoDriver driver) =>
        CockpitBackend.Build(
            NullLoggerFactory.Instance,
            services => services.AddSingleton<ISessionDriverFactory>(new EchoDriverFactory(driver))).Services;

    private async Task<SessionLaunchRequest> _RequestAsync(ISessionLauncher launcher, string folder)
    {
        var desk = await launcher.RunExclusiveAsync(() =>
            launcher.Workspaces.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions))
            ?? await launcher.CreateSessionsWorkspaceAsync("Launcher");
        var profile = new SessionProfile("Echo", new ClaudeConfig(Path.Combine(_stateRoot, ".claude"))) { DefaultKind = ProfileSessionKind.Sdk };
        return new SessionLaunchRequest(desk.Id, profile, null, folder, null, PaneSessionKind.Sdk, null, null, null, false);
    }

    // A repository with one commit, the least a worktree can branch from.
    private static async Task<string> _RepositoryAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await _GitAsync(folder, "init", "-b", "main");
        await _GitAsync(folder, "-c", "user.name=Launcher", "-c", "user.email=launcher@localhost", "commit", "--allow-empty", "-m", "initial");
        return folder;
    }

    private static async Task<string> _BranchesAsync(string repository, string branch) =>
        (await _GitAsync(repository, "branch", "--list", branch)).Trim();

    private static async Task<string> _GitAsync(string folder, params string[] arguments)
    {
        var result = await GitCli.RunAsync(folder, arguments, CancellationToken.None);
        return result.ExitCode == 0 ? result.StandardOutput : throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.StandardError}");
    }
}
