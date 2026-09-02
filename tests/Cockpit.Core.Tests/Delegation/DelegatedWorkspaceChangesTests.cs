using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.TestSupport;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// AC-971: what a delegated task changed is told by the host, not by the task. The fork that set this ticket off
/// wrote 68 files and its closing summary mentioned none of them — it was found only because the delegating session
/// happened to run <c>git status</c> out of suspicion. These pin that the reading is taken by the cockpit, rides
/// with the result, and that changes made under a read-only scope fail the task rather than pass unremarked.
/// </summary>
public class DelegatedWorkspaceChangesTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), $"ac971-{Guid.NewGuid():N}");

    public void Dispose() => TestGitDirectory.Remove(_repository);

    // --- Parsing: git's own answer, read whole ---

    [Fact]
    public void ParsePorcelain_ReadsEveryChangedPath()
    {
        var paths = DelegatedWorkspaceChanges.ParsePorcelain(" M src/App.cs\0?? new/file.txt\0 D gone.txt\0");

        Assert.Equal(["gone.txt", "new/file.txt", "src/App.cs"], paths.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void ParsePorcelain_KeepsBothSidesOfARename_AndDoesNotReadTheOriginAsItsOwnRecord()
    {
        // In -z mode a rename's origin is a separate NUL-terminated field. Read as a record of its own it would be
        // truncated to garbage ("Old.cs" losing its first three characters); skipped entirely it would hide a path
        // the task really did touch.
        var paths = DelegatedWorkspaceChanges.ParsePorcelain("R  src/New.cs\0src/Old.cs\0?? third.txt\0");

        Assert.Equal(["src/New.cs", "src/Old.cs", "third.txt"], paths.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void ParsePorcelain_KeepsAPathWithSpacesAndAccentsWhole()
    {
        // The -z form is what makes this true: git's display form would quote and escape it, and a mangled path is
        // a change the report would silently misname.
        var paths = DelegatedWorkspaceChanges.ParsePorcelain("?? docs/café notes.md\0");

        Assert.Equal("docs/café notes.md", Assert.Single(paths));
    }

    [Fact]
    public void Added_TellsWhatThisTaskChangedFromWhatWasAlreadyDirty()
    {
        var before = DelegatedWorkspaceChanges.ParsePorcelain(" M already.txt\0");
        var after = DelegatedWorkspaceChanges.ParsePorcelain(" M already.txt\0?? written-by-the-task.txt\0");

        Assert.Equal(["written-by-the-task.txt"], DelegatedWorkspaceChanges.Added(before, after));
    }

    [Fact]
    public void Added_WithNothingToRead_IsNull_NotAnEmptyList()
    {
        // "Could not be established" and "changed nothing" are different answers, and reporting the first as the
        // second is exactly the false clean bill this feature exists to stop.
        Assert.Null(DelegatedWorkspaceChanges.Added(before: null, after: null));
    }

    [Fact]
    public async Task SnapshotAsync_OutsideAWorkTree_IsNull()
    {
        Directory.CreateDirectory(_repository);

        Assert.Null(await DelegatedWorkspaceChanges.SnapshotAsync(_repository));
    }

    [Fact]
    public async Task SnapshotAsync_InACleanWorkTree_IsEmpty_NotNull()
    {
        await _InitRepositoryAsync();

        var snapshot = await DelegatedWorkspaceChanges.SnapshotAsync(_repository);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    // --- End to end: the report rides with the task, and a read-only task that wrote is failed ---

    [Fact]
    public async Task AReadOnlyTaskThatChangedFiles_IsFailed_AndTheChangedPathsAreReported()
    {
        await _InitRepositoryAsync();
        var service = _ServiceWritingDuringTheTurn("design-notes.md");

        // No requested_permission: read-only, which is what a "read this and report back" task gets.
        var task = await service.DelegateAsync(new DelegationRequest("local", "read and report", WorkingDirectory: _repository));
        await _WaitUntilFinishedAsync(service, task.TaskId);

        var finished = service.GetTask(task.TaskId)!;
        Assert.Equal(DelegatedTaskStatus.Failed, finished.Status);
        Assert.Contains("Out of scope", finished.Error);
        Assert.Equal(["design-notes.md"], finished.ChangedPaths);
    }

    [Fact]
    public async Task ATaskDelegatedWithWritePermission_KeepsItsResult_AndStillReportsWhatItChanged()
    {
        await _InitRepositoryAsync();
        var service = _ServiceWritingDuringTheTurn("Feature.cs");

        var task = await service.DelegateAsync(
            new DelegationRequest("local", "write the feature", WorkingDirectory: _repository, RequestedPermission: "acceptEdits"));
        await _WaitUntilFinishedAsync(service, task.TaskId);

        // A task that was allowed to write is not in trouble for writing — but its caller still gets the list from
        // the cockpit rather than from the task's own account of itself.
        var finished = service.GetTask(task.TaskId)!;
        Assert.Equal(DelegatedTaskStatus.Completed, finished.Status);
        Assert.Equal(["Feature.cs"], finished.ChangedPaths);
        Assert.Equal("acceptEdits", finished.Permission);
    }

    [Fact]
    public async Task WhatTheDelegatingSessionLeftDirty_IsNotBlamedOnTheTask()
    {
        await _InitRepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(_repository, "already-dirty.txt"), "the caller's own work");
        var service = _ServiceWritingDuringTheTurn(fileName: null);

        var task = await service.DelegateAsync(new DelegationRequest("local", "just read", WorkingDirectory: _repository));
        await _WaitUntilFinishedAsync(service, task.TaskId);

        var finished = service.GetTask(task.TaskId)!;
        Assert.Equal(DelegatedTaskStatus.Completed, finished.Status);
        Assert.Empty(finished.ChangedPaths!);
    }

    private async Task _InitRepositoryAsync()
    {
        Directory.CreateDirectory(_repository);
        await GitCli.RunCheckedAsync(_repository, ["init", "--initial-branch=main"], CancellationToken.None);
        await GitCli.RunCheckedAsync(_repository, ["config", "user.email", "test@example.com"], CancellationToken.None);
        await GitCli.RunCheckedAsync(_repository, ["config", "user.name", "Test"], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_repository, "README.md"), "seed");
        await GitCli.RunCheckedAsync(_repository, ["add", "."], CancellationToken.None);
        await GitCli.RunCheckedAsync(_repository, ["commit", "-m", "seed"], CancellationToken.None);
    }

    // A delegation engine whose session writes `fileName` into the repository mid-turn — a tool call the host's own
    // gate did not catch (a shell command, say), which is precisely the case the report exists to make visible.
    private DelegationService _ServiceWritingDuringTheTurn(string? fileName)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_TurnWritingAsync(fileName));

        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, TimeoutMinutes: 0, AllowedWorkingDirs: [_repository], PermissionCeiling: "bypassPermissions"));

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);

        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        return new DelegationService(
            profileStore,
            new SessionManager(driverFactory),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMilliseconds(minutes * 30));
    }

    private async IAsyncEnumerable<SessionEvent> _TurnWritingAsync(string? fileName)
    {
        if (fileName is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(_repository, fileName), "written by the delegated task");
        }

        yield return new AssistantTextCompleted { SessionId = "s1", Text = "Here is my report." };
        yield return new TurnCompleted { SessionId = "s1", Subtype = "success", Result = "Here is my report.", IsError = false };
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private static async Task _WaitUntilFinishedAsync(DelegationService service, string taskId)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (service.GetTask(taskId) is { Status: DelegatedTaskStatus.Completed or DelegatedTaskStatus.Failed or DelegatedTaskStatus.Stopped })
            {
                return;
            }

            await Task.Delay(20);
        }
    }
}
