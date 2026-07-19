using System.Diagnostics;
using Cockpit.Infrastructure.Worktrees;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Worktrees;

/// <summary>
/// The worktree manager against a real git repository (AC-85). A fake git would prove nothing: what this promises
/// is about what git actually does with an existing branch, a dirty tree, a detached head — and about the
/// isolation two worktrees on one repository give each other.
/// </summary>
public sealed class WorktreeManagerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cockpit-worktree-{Guid.NewGuid():n}");
    private readonly string _repo;
    private readonly string _worktreesRoot;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly WorktreeManager _manager;

    public WorktreeManagerTests()
    {
        _repo = Path.Combine(_tempRoot, "repo");
        _worktreesRoot = Path.Combine(_tempRoot, "worktrees");
        var configPath = Path.Combine(_tempRoot, "cockpit.json");

        Directory.CreateDirectory(_repo);
        _Git(_repo, "init", "-b", "main");
        _Git(_repo, "config", "user.email", "test@example.com");
        _Git(_repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "first");

        _manager = new WorktreeManager(new WorktreeRegistryStore(configPath), _worktreesRoot);
    }

    [Fact]
    public async Task DetectRepositoryAsync_DirectoryOutsideAnyRepository_ReturnsNull()
    {
        var plain = Path.Combine(_tempRoot, "not-a-repo");
        Directory.CreateDirectory(plain);

        var info = await _manager.DetectRepositoryAsync(plain);

        info.Should().BeNull();
    }

    [Fact]
    public async Task DetectRepositoryAsync_Repository_ReportsRootBranchAndHead()
    {
        var info = await _manager.DetectRepositoryAsync(_repo);

        info.Should().NotBeNull();
        info!.Root.Should().Be(Path.GetFullPath(_Git(_repo, "rev-parse", "--show-toplevel")));
        info.CurrentBranch.Should().Be("main");
        info.IsDetachedHead.Should().BeFalse();
        info.HeadCommit.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DetectRepositoryAsync_DetachedHead_ReportsDetachedWithNoBranch()
    {
        _Git(_repo, "checkout", _Git(_repo, "rev-parse", "HEAD"));

        var info = await _manager.DetectRepositoryAsync(_repo);

        info.Should().NotBeNull();
        info!.IsDetachedHead.Should().BeTrue();
        info.CurrentBranch.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_NewBranch_CreatesWorktreeOutsideRepoAndRecordsIt()
    {
        const string branch = "cockpit/ac-85-work";

        var record = await _manager.CreateAsync(_sessionId, branch, _repo);

        Directory.Exists(record.Path).Should().BeTrue();
        record.Path.Should().StartWith(Path.GetFullPath(_worktreesRoot));
        record.Branch.Should().Be(branch);
        record.BaseCommit.Should().Be(_Git(_repo, "rev-parse", "HEAD"));

        (await _manager.ListAsync()).Should().ContainSingle().Which.Branch.Should().Be(branch);
        _Git(_repo, "branch", "--list", branch).Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_BranchThatAlreadyExists_FailsLoudly_WithoutResettingIt()
    {
        const string branch = "already-here";
        _Git(_repo, "branch", branch);

        var create = async () => await _manager.CreateAsync(_sessionId, branch, _repo);

        (await create.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*already exists*");
    }

    [Fact]
    public async Task CreateAsync_DirectoryNotARepository_Throws()
    {
        var plain = Path.Combine(_tempRoot, "plain");
        Directory.CreateDirectory(plain);

        var create = async () => await _manager.CreateAsync(_sessionId, "branch", plain);

        await create.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAsync_WorktreeRootInsideRepository_IsRefused()
    {
        // The state root is always outside the repo in production; this drives a manager whose root is inside it, to
        // prove the guard that a worktree is never checked out into the tree it is meant to keep clean.
        var manager = new WorktreeManager(new WorktreeRegistryStore(Path.Combine(_tempRoot, "inside.json")), Path.Combine(_repo, "nested"));

        var create = async () => await manager.CreateAsync(_sessionId, "branch", _repo);

        (await create.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*inside the repository*");
    }

    [Fact]
    public async Task IsCleanAsync_FreshWorktree_IsClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        (await _manager.IsCleanAsync(record)).Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithUncommittedChange_IsNotClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");

        (await _manager.IsCleanAsync(record)).Should().BeFalse();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithCommitAheadOfBase_IsNotClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");
        _Git(record.Path, "add", "-A");
        _Git(record.Path, "commit", "-m", "work");

        (await _manager.IsCleanAsync(record)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_CleanWorktree_RemovesFolderAndRegistryEntry_ButKeepsTheBranch()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        await _manager.RemoveAsync(record);

        Directory.Exists(record.Path).Should().BeFalse();
        (await _manager.ListAsync()).Should().BeEmpty();
        // Removing a worktree does not remove its branch — branch cleanup is the teardown policy's decision (F3),
        // not this primitive's, so a forced removal can still keep the commits on the branch.
        _Git(_repo, "branch", "--list", "wt").Should().NotBeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_DirtyWorktreeWithoutForce_IsRefused_ThenForceRemoves()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");

        var remove = async () => await _manager.RemoveAsync(record);
        await remove.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(record.Path).Should().BeTrue();

        await _manager.RemoveAsync(record, force: true);
        Directory.Exists(record.Path).Should().BeFalse();
        (await _manager.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_TwoSessionsOnOneRepository_GiveEachOtherIsolatedTrees()
    {
        var first = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/session-1", _repo);
        var second = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/session-2", _repo);

        first.Path.Should().NotBe(second.Path);
        first.Branch.Should().NotBe(second.Branch);
        (await _manager.ListAsync()).Should().HaveCount(2);

        File.WriteAllText(Path.Combine(first.Path, "only-in-first.txt"), "x\n");
        _Git(first.Path, "add", "-A");
        _Git(first.Path, "commit", "-m", "first-only work");

        File.Exists(Path.Combine(second.Path, "only-in-first.txt")).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static string _Git(string workingDirectory, params string[] arguments)
    {
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {standardError.Trim()}");
        }

        return standardOutput.Trim();
    }
}
