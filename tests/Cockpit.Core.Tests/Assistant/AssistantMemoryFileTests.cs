using Cockpit.Infrastructure.Assistant;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// The assistant's memory file (AC-595): what it was told to keep survives the session, and a machine that never
/// remembered anything still starts.
/// </summary>
public class AssistantMemoryFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public AssistantMemoryFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "assistant-memory.md");
    }

    [Fact]
    public async Task NothingHasEverBeenRemembered_ReadsAsEmpty_RatherThanFailing()
    {
        var memory = new AssistantMemoryFile(_filePath);

        Assert.Equal(string.Empty, await memory.ReadAsync());
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public async Task TwoThingsToldAnHourApart_BothSurvive()
    {
        // The half that is easy to ship broken: a memory that rewrote itself would look right in the conversation
        // that set it and quietly lose everything said before.
        var memory = new AssistantMemoryFile(_filePath);

        await memory.RememberAsync("The operator is called Raymond.");
        await memory.RememberAsync("\"Prod\" means the release desk.");

        var contents = await memory.ReadAsync();
        Assert.Contains("The operator is called Raymond.", contents, StringComparison.Ordinal);
        Assert.Contains("\"Prod\" means the release desk.", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMultiLineThing_IsStoredAsOneEntry_SoTheFileStaysPrunable()
    {
        var memory = new AssistantMemoryFile(_filePath);

        await memory.RememberAsync("Answer in Dutch.\nAnd keep it short.");

        var lines = (await memory.ReadAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines, line => line.StartsWith("- ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RememberingNothing_IsRefused_RatherThanStoredAsABlankLine(string blank)
    {
        var memory = new AssistantMemoryFile(_filePath);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => memory.RememberAsync(blank));
        Assert.Equal(string.Empty, await memory.ReadAsync());
    }

    [Fact]
    public async Task AFileEditedByHand_IsWhatTheNextReadReturns()
    {
        // There is no forget tool: pruning is the operator opening this file, so a hand-edited one has to be read
        // back as it stands rather than repaired into the shape the writer would have produced.
        var memory = new AssistantMemoryFile(_filePath);
        await memory.RememberAsync("The operator is called Raymond.");

        await File.WriteAllTextAsync(_filePath, "# Notes\n\n- Only this one survived the pruning.\n");

        Assert.Equal("# Notes\n\n- Only this one survived the pruning.", await memory.ReadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
