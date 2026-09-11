using Cockpit.App.Plugins;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The host's write side of the memory-source contract (AC-492): a note goes only to a source that says it can
/// take one, and only ever as one appended block — the caller is told beforehand, not by trying.
/// </summary>
public class ProjectMemoryNoteWriterTests
{
    private static ProjectMemoryNoteWriter _Writer(params ProjectMemorySourceRegistration[] sources)
    {
        var registry = new ProjectMemorySourceRegistry();
        foreach (var source in sources)
        {
            Assert.True(registry.Register(source));
        }

        return new ProjectMemoryNoteWriter(registry);
    }

    private static Project _Project(string? memoryRef) => new("p1", "Project") { MemoryRef = memoryRef };

    [Fact]
    public async Task ASourceWithoutAppendNoteAsync_IsNotWritable_AndIsNeverCalled()
    {
        // A third-party plugin built before this member exists reads as null: it keeps loading, and the host must
        // say "no" up front rather than let the operator type a note that has nowhere to go.
        var writer = _Writer(new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there."));
        var project = _Project("depot:cockpit");

        Assert.False(writer.CanAppend(project));
        var result = await writer.AppendAsync(project, "hello", CancellationToken.None);

        Assert.Equal(ProjectMemoryAppendOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void ABareFolderPath_IsNotWritable()
    {
        // A path is a valid memory reference today, but v1 writes through the plugin contract only — no plugin
        // owns a bare path, so a Windows path must not be misread as scheme "C" either.
        var writer = _Writer(new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.")
        {
            AppendNoteAsync = (_, _, _) => Task.FromResult(ProjectMemoryAppendResult.Success),
        });

        Assert.False(writer.CanAppend(_Project(@"C:\notes\project")));
        Assert.False(writer.CanAppend(_Project(null)));
    }

    [Fact]
    public async Task AWritableSource_ReceivesTheBareValue_AndOnlyAStampedBlock()
    {
        // The block is the whole of what a plugin gets — never existing content — so the API itself, not an
        // agreement, is what makes replacing impossible. The stamp carries a UTC offset so the file reads outside
        // Cockpit half a year later.
        string? receivedValue = null;
        string? receivedBlock = null;
        var writer = _Writer(new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.")
        {
            AppendNoteAsync = (value, block, _) =>
            {
                receivedValue = value;
                receivedBlock = block;
                return Task.FromResult(ProjectMemoryAppendResult.Success);
            },
        });
        var project = _Project("Depot:cockpit");

        Assert.True(writer.CanAppend(project));
        var result = await writer.AppendAsync(project, "  call Olaf back  ", CancellationToken.None);

        Assert.Equal(ProjectMemoryAppendOutcome.Success, result.Outcome);
        Assert.Equal("cockpit", receivedValue);
        Assert.Matches(@"^\n## \d{4}-\d{2}-\d{2}T\d{2}:\d{2}[+-]\d{2}:\d{2}\n\ncall Olaf back\n$", receivedBlock);
        Assert.Equal("\n## 2026-09-11T10:12+02:00\n\nnote\n", ProjectMemoryNoteWriter.Stamp("note", new DateTimeOffset(2026, 9, 11, 10, 12, 30, TimeSpan.FromHours(2))));
    }
}
