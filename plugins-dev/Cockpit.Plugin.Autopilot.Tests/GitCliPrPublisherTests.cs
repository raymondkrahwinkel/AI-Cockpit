namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1337 (review): gh refuses a PR whose base does not exist on the remote yet ("Base ref must be a branch",
// reproduced against a real GitHub repo) — an epic's first sub, before any collection branch was ever pushed. A
// fake publisher cannot answer whether the real one actually creates that branch first; only real git can.
public sealed class GitCliPrPublisherTests : IDisposable
{
    private readonly string _origin = Path.Combine(Path.GetTempPath(), $"ac1337-pub-origin-{Guid.NewGuid():N}");
    private readonly string _clone = Path.Combine(Path.GetTempPath(), $"ac1337-pub-clone-{Guid.NewGuid():N}");
    private readonly GitCliPrPublisher _publisher = new();

    public GitCliPrPublisherTests()
    {
        Directory.CreateDirectory(_origin);
        _Run(_origin, "init", "--bare");

        Directory.CreateDirectory(_clone);
        _Run(_clone, "init", "-b", "main");
        _Run(_clone, "config", "user.name", "Test");
        _Run(_clone, "config", "user.email", "test@example.com");
        _Run(_clone, "config", "commit.gpgsign", "false");
        _Run(_clone, "remote", "add", "origin", _origin);
        File.WriteAllText(Path.Combine(_clone, "readme.md"), "seed");
        _Run(_clone, "add", "-A");
        _Run(_clone, "commit", "-m", "seed commit");
        _Run(_clone, "push", "-u", "origin", "main");
        _Run(_origin, "symbolic-ref", "HEAD", "refs/heads/main");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_WithABaseBranch_EnsuresItExistsOnTheRemoteBeforeOpeningThePr(bool baseAlreadyExists)
    {
        const string baseBranch = "epic/ac-1337";
        if (baseAlreadyExists)
        {
            _Run(_clone, "push", "origin", $"main:refs/heads/{baseBranch}");
        }

        var mainTip = _Run(_origin, "rev-parse", "main");
        _Run(_clone, "checkout", "-b", "autopilot/run");
        File.WriteAllText(Path.Combine(_clone, "work.txt"), "the sub's work");
        _Run(_clone, "add", "-A");
        _Run(_clone, "commit", "-m", "AC-1337 - the sub's work");

        await _publisher.PublishAsync(new AutopilotPrRequest(_clone, "autopilot/run", "title", "body", baseBranch), createPullRequest: true);

        var afterwards = _Run(_origin, "for-each-ref", $"refs/heads/{baseBranch}", "--format=%(objectname)");
        Assert.Equal(mainTip.Trim(), afterwards.Trim());
    }

    public void Dispose()
    {
        _TryDelete(_origin);
        _TryDelete(_clone);
    }

    private static void _TryDelete(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // A throwaway directory under the system temp folder.
        }
    }

    private static string _Run(string directory, params string[] arguments)
    {
        var result = GitCommandLine.RunAsync("git", arguments, directory).GetAwaiter().GetResult();
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.StdOut;
    }
}
