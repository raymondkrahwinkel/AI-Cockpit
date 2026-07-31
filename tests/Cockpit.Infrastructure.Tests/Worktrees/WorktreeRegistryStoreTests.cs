using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Tests.Worktrees;

/// <summary>
/// The registry's persistence to <c>cockpit.json</c> (AC-85), against a real temporary config file rather than the
/// operator's own. The registry, not the folders on disk, is the source of truth for cleanup, so surviving a
/// restart — a fresh store instance reading what an earlier one wrote — is the property that matters.
/// </summary>
public sealed class WorktreeRegistryStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configPath;

    public WorktreeRegistryStoreTests()
    {
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task ListAsync_NoConfigFile_IsEmpty()
    {
        var store = new WorktreeRegistryStore(_configPath);

        Assert.Empty((await store.ListAsync()));
    }

    [Fact]
    public async Task AddAsync_ThenListFromAFreshStore_SurvivesTheRestart()
    {
        var record = _Record("/repo", "/wt/one", "cockpit/one");
        await new WorktreeRegistryStore(_configPath).AddAsync(record);

        var reloaded = await new WorktreeRegistryStore(_configPath).ListAsync();

        Assert.Equivalent(record, Assert.Single(reloaded));
    }

    [Fact]
    public async Task AddAsync_SameWorktreePathTwice_ReplacesRatherThanDuplicates()
    {
        var store = new WorktreeRegistryStore(_configPath);
        await store.AddAsync(_Record("/repo", "/wt/one", "cockpit/one"));
        await store.AddAsync(_Record("/repo", "/wt/one", "cockpit/one-again"));

        var records = await store.ListAsync();

        Assert.Equal("cockpit/one-again", Assert.Single(records).Branch);
    }

    [Fact]
    public async Task RemoveAsync_DropsOnlyTheMatchingEntry()
    {
        var store = new WorktreeRegistryStore(_configPath);
        await store.AddAsync(_Record("/repo", "/wt/one", "cockpit/one"));
        await store.AddAsync(_Record("/repo", "/wt/two", "cockpit/two"));

        await store.RemoveAsync("/wt/one");

        Assert.Equal(Path.GetFullPath("/wt/two"), Assert.Single((await store.ListAsync())).Path);
    }

    [Fact]
    public async Task ListAsync_OldEntryWithoutTheAgentCreatedField_ReadsAsSessionOwnAndProtected()
    {
        // AC-520 fix 5: a record written before IsAgentCreated existed has no such property in its JSON at all —
        // it must deserialize to false, read as "session-own, protected", rather than silently disabling the guard
        // that field exists to loosen.
        var json = """
            {
              "Worktrees": [
                {
                  "SessionId": "old-session",
                  "RepositoryRoot": "/repo",
                  "Path": "/wt/old",
                  "Branch": "cockpit/old",
                  "BaseCommit": "0123456789abcdef0123456789abcdef01234567",
                  "CreatedAt": "2026-01-01T00:00:00+00:00",
                  "IsLocked": true,
                  "IsRetained": false
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(_configPath, json);
        var store = new WorktreeRegistryStore(_configPath);

        var record = Assert.Single(await store.ListAsync());

        Assert.False(record.IsAgentCreated);
    }

    private static WorktreeRecord _Record(string repositoryRoot, string path, string branch) =>
        new(
            SessionId: Guid.NewGuid().ToString("N"),
            RepositoryRoot: Path.GetFullPath(repositoryRoot),
            Path: Path.GetFullPath(path),
            Branch: branch,
            BaseCommit: "0123456789abcdef0123456789abcdef01234567",
            CreatedAt: DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
