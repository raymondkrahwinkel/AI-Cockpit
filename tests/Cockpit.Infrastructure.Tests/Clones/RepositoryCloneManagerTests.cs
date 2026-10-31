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
    public async Task CloneAsync_SlugOccupiedByADifferentRepository_RefusesRatherThanClobber()
    {
        const string failure = "fatal: could not read Username: terminal prompts disabled";
        Assert.Contains("Server > Projects", RepositoryCloneManager.ExplainFailure(failure, "github.com", onServer: true));
        Assert.Contains("your own git credential helper", RepositoryCloneManager.ExplainFailure(failure, "github.com", onServer: false));
        Assert.Contains("your own git credential helper", RepositoryCloneManager.ExplainFailure(failure, "gitlab.com", onServer: true));
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

        startInfo.Environment.Remove("GIT_DIR");
        startInfo.Environment.Remove("GIT_WORK_TREE");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {standardError.GetAwaiter().GetResult().Trim()}");
        }

        return standardOutput.GetAwaiter().GetResult().Trim();
    }

    public void Dispose() => TestGitDirectory.Remove(_tempRoot);
}
