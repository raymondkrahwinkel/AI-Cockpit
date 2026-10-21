using Cockpit.Infrastructure.Assistant;

namespace Cockpit.Infrastructure.Tests.Assistant;

// Assistant threads: the closed set survives a restart beside the transcript log, and a file that is missing or
// cannot be read leaves every thread open rather than failing the start.
public sealed class AssistantThreadStateFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    private string _Path => Path.Combine(_root, "transcripts", "cockpit-assistant.threads.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TheClosedThreads_ComeBackAfterARestart()
    {
        await new AssistantThreadStateFile(_Path).SaveClosedAsync(["waymark", "depot"]);

        Assert.Equal(["depot", "waymark"], await new AssistantThreadStateFile(_Path).LoadClosedAsync());
    }

    [Fact]
    public async Task NoFile_OrOneThatCannotBeRead_LeavesEveryThreadOpen()
    {
        Assert.Empty(await new AssistantThreadStateFile(_Path).LoadClosedAsync());

        Directory.CreateDirectory(Path.GetDirectoryName(_Path)!);
        await File.WriteAllTextAsync(_Path, "{ not json");
        Assert.Empty(await new AssistantThreadStateFile(_Path).LoadClosedAsync());
    }
}
