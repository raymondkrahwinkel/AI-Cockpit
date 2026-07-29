using Cockpit.TestSupport;

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
        // This fixture never commits, so it holds no read-only objects and the plain delete happened to work. It
        // goes through the helper anyway: the day it grows a commit is the day it would fail the way the
        // workflow-steps fixture did, and that is not a day anyone would connect to a new assertion.
        TestGitDirectory.Remove(_repo);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ResolvesTheHeadFileAtTheRepositoryRoot()
    {
        var head = await GitHeadLocator.ResolveHeadFileAsync(_repo, CancellationToken.None);

        Assert.NotNull(head);
        Assert.True(File.Exists(head));
        Assert.Equal("HEAD", Path.GetFileName(head));
    }

    [Fact]
    public async Task ResolvesTheSameHeadFileFromASubdirectory()
    {
        var nested = Path.Combine(_repo, "src", "nested");
        Directory.CreateDirectory(nested);

        var fromRoot = await GitHeadLocator.ResolveHeadFileAsync(_repo, CancellationToken.None);
        var fromSubdirectory = await GitHeadLocator.ResolveHeadFileAsync(nested, CancellationToken.None);

        Assert.NotNull(fromRoot);
        Assert.Equal(fromRoot, fromSubdirectory);
    }

    [Fact]
    public async Task ReturnsNullOutsideARepository()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"cockpit-plain-{Guid.NewGuid():n}");
        Directory.CreateDirectory(plain);
        try
        {
            var head = await GitHeadLocator.ResolveHeadFileAsync(plain, CancellationToken.None);

            Assert.Null(head);
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    private string _Git(params string[] arguments) =>
        GitCommand.RunAsync(_repo, arguments, CancellationToken.None).GetAwaiter().GetResult();
}
