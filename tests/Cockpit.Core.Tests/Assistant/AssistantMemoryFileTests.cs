using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// The assistant's memory file (AC-595): what it was told to keep survives the session, and a machine that never
/// remembered anything still starts.
/// </summary>
public class AssistantMemoryFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly string _statePath;

    private readonly string _machinePath;

    public AssistantMemoryFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "assistant-memory.md");
        _statePath = Path.Combine(_tempDir, "assistant-state.md");
        _machinePath = Path.Combine(_tempDir, "assistant-machine.md");
    }

    [Fact]
    public async Task NothingHasEverBeenRemembered_ReadsAsEmpty_RatherThanFailing()
    {
        var memory = new AssistantMemoryFile(_filePath, _statePath);

        Assert.Equal(string.Empty, await memory.ReadAsync(AssistantMemoryScope.Behaviour));
        Assert.False(File.Exists(_filePath));
    }

    [Theory]
    [InlineData("machine", new[] { "local" }, true, false, true, true)]
    [InlineData("behaviour", null, false, true, false, false)]
    [InlineData(null, null, false, false, true, false)]
    public async Task Remember_RequiresAScope_AndKeepsTheExistingBehaviourFileByteIdenticalWhenItDoesNotTargetIt(
        string? scope,
        string[]? machines,
        bool isInMachineMemory,
        bool isInBehaviourMemory,
        bool behaviourIsUnchanged,
        bool machineFileExists)
    {
        await File.WriteAllTextAsync(_filePath, "# What the operator asked me to remember\n\n- existing behaviour\n");
        var before = await File.ReadAllBytesAsync(_filePath);
        var memory = new AssistantMemoryFile(_filePath, _statePath, _machinePath);
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = JsonNode.Parse(await new AssistantAgentMcpTools(
            Substitute.For<IAssistantAgentGateway>(), memory).RememberAsync("Git Bash lives here.", scope, machines))!;

        Assert.Equal(scope is not null, result["ok"]!.GetValue<bool>());
        Assert.Equal(isInMachineMemory, (await memory.ReadAsync(AssistantMemoryScope.Machine)).Contains("Git Bash lives here.", StringComparison.Ordinal));
        Assert.Equal(isInBehaviourMemory, (await memory.ReadAsync(AssistantMemoryScope.Behaviour)).Contains("Git Bash lives here.", StringComparison.Ordinal));
        var after = await File.ReadAllBytesAsync(_filePath);
        Assert.Equal(behaviourIsUnchanged, before.SequenceEqual(after));
        Assert.Equal(machineFileExists, File.Exists(_machinePath));
        Assert.Equal(scope is null, result.ToJsonString().Contains("machine knowledge is learned by doing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TwoThingsToldAnHourApart_BothSurvive()
    {
        // The half that is easy to ship broken: a memory that rewrote itself would look right in the conversation
        // that set it and quietly lose everything said before.
        var memory = new AssistantMemoryFile(_filePath, _statePath);

        await memory.RememberAsync("The operator is called Raymond.", AssistantMemoryScope.Behaviour);
        await memory.RememberAsync("\"Prod\" means the release desk.", AssistantMemoryScope.Behaviour);

        var contents = await memory.ReadAsync(AssistantMemoryScope.Behaviour);
        Assert.Contains("The operator is called Raymond.", contents, StringComparison.Ordinal);
        Assert.Contains("\"Prod\" means the release desk.", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMultiLineThing_IsStoredAsOneEntry_SoTheFileStaysPrunable()
    {
        var memory = new AssistantMemoryFile(_filePath, _statePath);

        await memory.RememberAsync("Answer in Dutch.\nAnd keep it short.", AssistantMemoryScope.Behaviour);

        var lines = (await memory.ReadAsync(AssistantMemoryScope.Behaviour)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines, line => line.StartsWith("- ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RememberingNothing_IsRefused_RatherThanStoredAsABlankLine(string blank)
    {
        var memory = new AssistantMemoryFile(_filePath, _statePath);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => memory.RememberAsync(blank, AssistantMemoryScope.Behaviour));
        Assert.Equal(string.Empty, await memory.ReadAsync(AssistantMemoryScope.Behaviour));
    }

    [Fact]
    public async Task AFileEditedByHand_IsWhatTheNextReadReturns()
    {
        // There is no forget tool: pruning is the operator opening this file, so a hand-edited one has to be read
        // back as it stands rather than repaired into the shape the writer would have produced.
        var memory = new AssistantMemoryFile(_filePath, _statePath);
        await memory.RememberAsync("The operator is called Raymond.", AssistantMemoryScope.Behaviour);

        await File.WriteAllTextAsync(_filePath, "# Notes\n\n- Only this one survived the pruning.\n");

        Assert.Equal("# Notes\n\n- Only this one survived the pruning.", await memory.ReadAsync(AssistantMemoryScope.Behaviour));
    }

    [Fact]
    public async Task TheCurrentState_IsReplacedByEachNote_AndLeavesTheRememberedLinesAlone()
    {
        // AC-596. The two writes have opposite jobs — one accumulates, one is the latest picture — which is why
        // they are separate files: a state that appended would be the transcript the restart exists to shed.
        var memory = new AssistantMemoryFile(_filePath, _statePath);
        await memory.RememberAsync("The operator is called Raymond.", AssistantMemoryScope.Behaviour);

        await memory.NoteCurrentStateAsync("We are on AC-592.");
        await memory.NoteCurrentStateAsync("The tests went green; they want to hear about the merge.");

        var state = await memory.ReadCurrentStateAsync();
        Assert.DoesNotContain("AC-592", state, StringComparison.Ordinal);
        Assert.Contains("they want to hear about the merge.", state, StringComparison.Ordinal);
        Assert.Contains("The operator is called Raymond.", await memory.ReadAsync(AssistantMemoryScope.Behaviour), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAssistantThatNeverNotedItsState_ReadsAsEmpty()
    {
        var memory = new AssistantMemoryFile(_filePath, _statePath);
        await memory.RememberAsync("The operator is called Raymond.", AssistantMemoryScope.Behaviour);

        Assert.Equal(string.Empty, await memory.ReadCurrentStateAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
