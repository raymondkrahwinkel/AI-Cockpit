using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Clones;

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
    public async Task AddAsync_ThenListFromAFreshStore_SurvivesTheRestart()
    {
        var record = _Record("github.com/org/repo", "/clones/github.com/org/repo");
        await new RepositoryCloneRegistryStore(_configPath).AddAsync(record);

        var reloaded = await new RepositoryCloneRegistryStore(_configPath).ListAsync();

        Assert.Equivalent(record, Assert.Single(reloaded));
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
