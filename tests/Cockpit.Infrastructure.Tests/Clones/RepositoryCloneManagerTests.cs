using System.Diagnostics;
using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Clones;
using Cockpit.TestSupport;

namespace Cockpit.Infrastructure.Tests.Clones;

/// <summary>
/// The clone manager against real git (AC-90), cloning from a local <c>file://</c> source rather than the network —
/// so the clone, the de-duplicated reuse, the refusal to clobber a different repository, and the fail-soft on a bad
/// URL are all exercised end to end without a network dependency (the live network clone is Raymond's own verify).
/// </summary>
public sealed class RepositoryCloneManagerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cockpit-clone-{Guid.NewGuid():n}");
    private readonly string _clonesRoot;
    private readonly string _source;
    private readonly string _sourceUrl;
    private readonly RepositoryCloneManager _manager;
    private readonly RepositoryCloneRegistryStore _registry;

    public RepositoryCloneManagerTests()
    {
        _clonesRoot = Path.Combine(_tempRoot, "clones");
        _source = Path.Combine(_tempRoot, "source");
        var configPath = Path.Combine(_tempRoot, "cockpit.json");

        Directory.CreateDirectory(_source);
        _Git(_source, "init", "-b", "main");
        _Git(_source, "config", "user.email", "test@example.com");
        _Git(_source, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_source, "README.md"), "hello\n");
        _Git(_source, "add", "-A");
        _Git(_source, "commit", "-m", "first");

        _sourceUrl = new Uri(_source).AbsoluteUri;
        _registry = new RepositoryCloneRegistryStore(configPath);
        _manager = new RepositoryCloneManager(_registry, _clonesRoot);
    }

    [Fact]
    public async Task CloneAsync_ClonesIntoManagedRootAndRegistersIt()
    {
        var record = await _manager.CloneAsync(_sourceUrl);

        Assert.StartsWith(Path.GetFullPath(_clonesRoot), record.Path);
        Assert.True(Directory.Exists(Path.Combine(record.Path, ".git")));
        Assert.True(File.Exists(Path.Combine(record.Path, "README.md")));

        var registered = await _registry.ListAsync();
        Assert.Equal(record.Path, Assert.Single(registered).Path);
    }

    [Fact]
    public async Task CloneAsync_ExplicitTargetPath_ClonesThereAndRegistersThatPath()
    {
        var target = Path.Combine(_tempRoot, "chosen", "my-repo");

        var record = await _manager.CloneAsync(_sourceUrl, target);

        Assert.Equal(Path.GetFullPath(target), record.Path);
        Assert.True(Directory.Exists(Path.Combine(target, ".git")));
        Assert.True(File.Exists(Path.Combine(target, "README.md")));
        Assert.Equal(Path.GetFullPath(target), Assert.Single((await _registry.ListAsync())).Path);
    }

    [Fact]
    public async Task CloneAsync_BlankTargetPath_FallsBackToManagedDefault()
    {
        var record = await _manager.CloneAsync(_sourceUrl, "   ");

        var root = await _manager.GetEffectiveClonesRootAsync();
        Assert.Equal(_manager.BuildClonePath(root, _sourceUrl), record.Path);
        Assert.StartsWith(Path.GetFullPath(_clonesRoot), record.Path);
    }

    [Fact]
    public async Task BuildClonePath_ReturnsManagedSlugPath_OrNullForAnUnparseableUrl()
    {
        var root = await _manager.GetEffectiveClonesRootAsync();

        Assert.StartsWith(Path.GetFullPath(_clonesRoot), _manager.BuildClonePath(root, _sourceUrl));
        Assert.Null(_manager.BuildClonePath(root, "   "));
    }

    [Fact]
    public async Task GetEffectiveClonesRootAsync_UsesTheConfiguredOverride_WhenSet()
    {
        // The production constructor resolves the root through the settings store (AC-90): a saved override wins over
        // the state-root default, and is returned as a full path so the dialog shows an absolute folder.
        var settings = new CloneSettingsStore(Path.Combine(_tempRoot, "cockpit-override.json"));
        var custom = Path.Combine(_tempRoot, "custom-clones");
        await settings.SaveAsync(new CloneSettings { Root = custom });

        var manager = new RepositoryCloneManager(_registry, settings);

        Assert.Equal(Path.GetFullPath(custom), (await manager.GetEffectiveClonesRootAsync()));
    }

    [Fact]
    public async Task CloneAsync_AlreadyCloned_ReusesRatherThanCloningAgain()
    {
        var first = await _manager.CloneAsync(_sourceUrl);

        // A local edit that a fresh clone would not have: it surviving proves the second call reused the checkout
        // rather than re-cloning over it.
        var marker = Path.Combine(first.Path, "local-only.txt");
        File.WriteAllText(marker, "kept");

        var second = await _manager.CloneAsync(_sourceUrl);

        Assert.Equal(first.Path, second.Path);
        Assert.True(File.Exists(marker));
        Assert.Single((await _registry.ListAsync()));
    }

    [Fact]
    public async Task CloneAsync_SlugOccupiedByADifferentRepository_RefusesRatherThanClobber()
    {
        var first = await _manager.CloneAsync(_sourceUrl);

        // Replace the checkout with a different git repository (no matching origin) at the same managed slug. The
        // clone must refuse rather than overwrite whatever is there — it might be work.
        TestGitDirectory.Remove(first.Path);
        Directory.CreateDirectory(first.Path);
        _Git(first.Path, "init", "-b", "main");
        var untouched = Path.Combine(first.Path, "someone-elses-work.txt");
        File.WriteAllText(untouched, "do not delete");

        var act = () => _manager.CloneAsync(_sourceUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.True(File.Exists(untouched));
    }

    [Fact]
    public async Task ReconcileAsync_ForgetsAVanishedClone_ButKeepsOneStillOnDisk()
    {
        var present = await _manager.CloneAsync(_sourceUrl);
        await _registry.AddAsync(new RepositoryClone(
            "github.com/org/gone",
            "https://github.com/org/gone",
            Path.Combine(_clonesRoot, "github.com", "org", "gone"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        await _manager.ReconcileAsync();

        var remaining = await _registry.ListAsync();
        Assert.Equal(present.Path, Assert.Single(remaining).Path);
        // Never deletes disk: the surviving clone's folder is left exactly as it was.
        Assert.True(Directory.Exists(present.Path));
    }

    [Fact]
    public async Task CloneAsync_UnreachableSource_FailsSoftWithoutRegistering()
    {
        var missingUrl = new Uri(Path.Combine(_tempRoot, "does-not-exist")).AbsoluteUri;

        var act = () => _manager.CloneAsync(missingUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Empty((await _registry.ListAsync()));
    }

    [Fact]
    public async Task CloneAsync_BlankUrl_ThrowsFormatError()
    {
        var act = () => _manager.CloneAsync("   ");

        await Assert.ThrowsAsync<FormatException>(act);
    }

    private static string _Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return standardOutput.Trim();
    }

    public void Dispose() => TestGitDirectory.Remove(_tempRoot);
}
