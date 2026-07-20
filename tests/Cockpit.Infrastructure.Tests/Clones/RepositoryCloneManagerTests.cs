using System.Diagnostics;
using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Clones;
using FluentAssertions;

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

        record.Path.Should().StartWith(Path.GetFullPath(_clonesRoot));
        Directory.Exists(Path.Combine(record.Path, ".git")).Should().BeTrue();
        File.Exists(Path.Combine(record.Path, "README.md")).Should().BeTrue();

        var registered = await _registry.ListAsync();
        registered.Should().ContainSingle().Which.Path.Should().Be(record.Path);
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

        second.Path.Should().Be(first.Path);
        File.Exists(marker).Should().BeTrue();
        (await _registry.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CloneAsync_SlugOccupiedByADifferentRepository_RefusesRatherThanClobber()
    {
        var first = await _manager.CloneAsync(_sourceUrl);

        // Replace the checkout with a different git repository (no matching origin) at the same managed slug. The
        // clone must refuse rather than overwrite whatever is there — it might be work.
        Directory.Delete(first.Path, recursive: true);
        Directory.CreateDirectory(first.Path);
        _Git(first.Path, "init", "-b", "main");
        var untouched = Path.Combine(first.Path, "someone-elses-work.txt");
        File.WriteAllText(untouched, "do not delete");

        var act = () => _manager.CloneAsync(_sourceUrl);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(untouched).Should().BeTrue();
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
        remaining.Should().ContainSingle().Which.Path.Should().Be(present.Path);
        // Never deletes disk: the surviving clone's folder is left exactly as it was.
        Directory.Exists(present.Path).Should().BeTrue();
    }

    [Fact]
    public async Task CloneAsync_UnreachableSource_FailsSoftWithoutRegistering()
    {
        var missingUrl = new Uri(Path.Combine(_tempRoot, "does-not-exist")).AbsoluteUri;

        var act = () => _manager.CloneAsync(missingUrl);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _registry.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CloneAsync_BlankUrl_ThrowsFormatError()
    {
        var act = () => _manager.CloneAsync("   ");

        await act.Should().ThrowAsync<FormatException>();
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

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            // git checks out read-only pack files on some platforms; clear the attribute so the recursive delete
            // does not trip over them.
            foreach (var file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception)
                {
                    // Best effort — a file we cannot re-attribute is not worth failing the cleanup over.
                }
            }

            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
