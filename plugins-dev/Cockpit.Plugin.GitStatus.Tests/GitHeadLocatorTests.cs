using FluentAssertions;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// Locating HEAD against a real repository. What is worth proving is the two cases a naive
/// <c>&lt;dir&gt;/.git/HEAD</c> gets wrong: a working directory that is a subdirectory of the repo (HEAD lives at
/// the root, not next to the session), and a directory that is not a repository at all.
/// </summary>
public class GitHeadLocatorTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"cockpit-head-{Guid.NewGuid():n}");

    public GitHeadLocatorTests()
    {
        Directory.CreateDirectory(_repo);
        _Git("init", "-b", "main");
    }

    public void Dispose()
    {
        if (Directory.Exists(_repo))
        {
            Directory.Delete(_repo, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ResolvesTheHeadFileAtTheRepositoryRoot()
    {
        var head = await GitHeadLocator.ResolveHeadFileAsync(_repo, CancellationToken.None);

        head.Should().NotBeNull();
        File.Exists(head).Should().BeTrue();
        Path.GetFileName(head).Should().Be("HEAD");
    }

    [Fact]
    public async Task ResolvesTheSameHeadFileFromASubdirectory()
    {
        var nested = Path.Combine(_repo, "src", "nested");
        Directory.CreateDirectory(nested);

        var fromRoot = await GitHeadLocator.ResolveHeadFileAsync(_repo, CancellationToken.None);
        var fromSubdirectory = await GitHeadLocator.ResolveHeadFileAsync(nested, CancellationToken.None);

        fromRoot.Should().NotBeNull();
        fromSubdirectory.Should().Be(fromRoot);
    }

    [Fact]
    public async Task ReturnsNullOutsideARepository()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"cockpit-plain-{Guid.NewGuid():n}");
        Directory.CreateDirectory(plain);
        try
        {
            var head = await GitHeadLocator.ResolveHeadFileAsync(plain, CancellationToken.None);

            head.Should().BeNull();
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    private string _Git(params string[] arguments) =>
        GitCommand.RunAsync(_repo, arguments, CancellationToken.None).GetAwaiter().GetResult();
}
