using Cockpit.Infrastructure.Assistant;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The loose export/import for just the two assistant memory files (AC-657) — a lighter, separate path from a full
/// cockpit backup. Round trip, what happens when one or both files are missing, and that a restore never deletes
/// what it replaces.
/// </summary>
public sealed class AssistantMemoryBackupTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("assistant-memory-backup-tests").FullName;

    private string _MemoryPath => Path.Combine(_directory, "assistant-memory.md");

    private string _StatePath => Path.Combine(_directory, "assistant-state.md");

    private string _ArchivePath => Path.Combine(_directory, "export.zip");

    [Fact]
    public void Write_ThenRestore_RoundTripsBothFiles()
    {
        File.WriteAllText(_MemoryPath, "- remembered thing");
        File.WriteAllText(_StatePath, "where we stood");

        var written = AssistantMemoryBackup.Write(_ArchivePath, _MemoryPath, _StatePath);
        Assert.Equal(["assistant-memory.md", "assistant-state.md"], written);

        File.Delete(_MemoryPath);
        File.Delete(_StatePath);

        var restored = AssistantMemoryBackup.Restore(_ArchivePath, _MemoryPath, _StatePath);

        Assert.Equal(["assistant-memory.md", "assistant-state.md"], restored);
        Assert.Equal("- remembered thing", File.ReadAllText(_MemoryPath));
        Assert.Equal("where we stood", File.ReadAllText(_StatePath));
    }

    [Fact]
    public void Write_OnlyIncludesWhicheverFileExists()
    {
        File.WriteAllText(_MemoryPath, "- remembered thing");

        var written = AssistantMemoryBackup.Write(_ArchivePath, _MemoryPath, _StatePath);

        Assert.Equal(["assistant-memory.md"], written);
    }

    [Fact]
    public void Write_WithNeitherFile_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => AssistantMemoryBackup.Write(_ArchivePath, _MemoryPath, _StatePath));
    }

    [Fact]
    public void Restore_CopiesWhatItReplaces_Aside_RatherThanDeletingIt()
    {
        File.WriteAllText(_MemoryPath, "- old memory");
        AssistantMemoryBackup.Write(_ArchivePath, _MemoryPath, _StatePath);

        File.WriteAllText(_MemoryPath, "- live memory nobody backed up yet");
        AssistantMemoryBackup.Restore(_ArchivePath, _MemoryPath, _StatePath);

        Assert.Equal("- old memory", File.ReadAllText(_MemoryPath));

        var aside = Directory.EnumerateFiles(_directory, "assistant-memory.md.replaced-*").Single();
        Assert.Equal("- live memory nobody backed up yet", File.ReadAllText(aside));
    }

    [Fact]
    public void Restore_FromAnArchiveWithNeitherFile_Throws()
    {
        var emptyArchive = Path.Combine(_directory, "empty.zip");
        using (System.IO.Compression.ZipFile.Open(emptyArchive, System.IO.Compression.ZipArchiveMode.Create))
        {
        }

        Assert.Throws<InvalidOperationException>(() => AssistantMemoryBackup.Restore(emptyArchive, _MemoryPath, _StatePath));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
