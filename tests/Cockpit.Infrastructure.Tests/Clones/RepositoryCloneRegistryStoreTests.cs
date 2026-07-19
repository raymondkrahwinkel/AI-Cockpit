using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Clones;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Clones;

/// <summary>
/// The clone registry's persistence to <c>cockpit.json</c> (AC-90), against a real temporary config file rather than
/// the operator's own. The registry is the source of truth for reuse and reconciliation, so surviving a restart — a
/// fresh store reading what an earlier one wrote — is the property that matters.
/// </summary>
public sealed class RepositoryCloneRegistryStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configPath;

    public RepositoryCloneRegistryStoreTests()
    {
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task ListAsync_NoConfigFile_IsEmpty()
    {
        var store = new RepositoryCloneRegistryStore(_configPath);

        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AddAsync_ThenListFromAFreshStore_SurvivesTheRestart()
    {
        var record = _Record("github.com/org/repo", "/clones/github.com/org/repo");
        await new RepositoryCloneRegistryStore(_configPath).AddAsync(record);

        var reloaded = await new RepositoryCloneRegistryStore(_configPath).ListAsync();

        reloaded.Should().ContainSingle().Which.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task AddAsync_SamePathTwice_ReplacesRatherThanDuplicates()
    {
        var store = new RepositoryCloneRegistryStore(_configPath);
        await store.AddAsync(_Record("github.com/org/repo", "/clones/one", remoteUrl: "https://github.com/org/repo"));
        await store.AddAsync(_Record("github.com/org/repo", "/clones/one", remoteUrl: "https://github.com/org/repo2"));

        var records = await store.ListAsync();

        records.Should().ContainSingle().Which.RemoteUrl.Should().Be("https://github.com/org/repo2");
    }

    [Fact]
    public async Task RemoveAsync_DropsOnlyTheMatchingEntry()
    {
        var store = new RepositoryCloneRegistryStore(_configPath);
        await store.AddAsync(_Record("github.com/org/one", "/clones/one"));
        await store.AddAsync(_Record("github.com/org/two", "/clones/two"));

        await store.RemoveAsync("/clones/one");

        (await store.ListAsync()).Should().ContainSingle().Which.Path.Should().Be(Path.GetFullPath("/clones/two"));
    }

    private static RepositoryClone _Record(string slug, string path, string remoteUrl = "https://github.com/org/repo") =>
        new(
            Slug: slug,
            RemoteUrl: remoteUrl,
            Path: Path.GetFullPath(path),
            CreatedAt: DateTimeOffset.UtcNow,
            LastUsedAt: DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
