using Cockpit.Infrastructure.Backup;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>
/// "Create backup…" ended in "The process cannot access the file … because it is being used by another process" and
/// no backup at all. The archive is built in a staging file and moved into place, and that move lands microseconds
/// after the zip's own handle closes — the moment a virus scanner opens a new .zip to unpack and scan it. The move
/// has to outlast that rather than give up on it.
/// <para>
/// Windows only, and not by preference: a rename on Unix does not care that a file is open, so there is no failure
/// to reproduce there. The bug and its fix are both real only where the tests run.
/// </para>
/// </summary>
public class BackupMoveIntoPlaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-backup-move", Guid.NewGuid().ToString("n"));

    public BackupMoveIntoPlaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A scanner still holding something in there is exactly what this file is about; it is not a failure.
        }
    }

    [WindowsFact("Only Windows fails a move on a file another process holds open, so elsewhere there is nothing to reproduce.")]
    public async Task AMoveBlockedByAnotherProcess_GoesThroughOnceThatProcessLetsGo()
    {
        var (staging, destination) = await _StagedAndChosenAsync();

        var holder = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(300);
            holder.Dispose();
        });

        await BackupService.MoveIntoPlaceAsync(staging, destination, CancellationToken.None);
        await release;

        Assert.Equal("the finished archive", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(staging), "the staging file is consumed by the move, not left behind");
    }

    [WindowsFact("Only Windows fails a move on a file another process holds open, so elsewhere there is nothing to reproduce.")]
    public async Task AChosenFileHeldPastTheWindow_RefusesNamingThatFile()
    {
        var (staging, destination) = await _StagedAndChosenAsync();

        using var holder = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var refusal = await Assert.ThrowsAsync<IOException>(() => BackupService.MoveIntoPlaceAsync(
            staging,
            destination,
            CancellationToken.None,
            contentionWindow: TimeSpan.FromMilliseconds(200)));

        // This one the operator can act on, so it is the one worth naming — File.Move's own
        // UnauthorizedAccessException for a held destination carries no path at all.
        Assert.Contains(destination, refusal.Message);
    }

    /// <summary>A built archive waiting in staging, and an earlier backup at the path the operator picked.</summary>
    private async Task<(string Staging, string Destination)> _StagedAndChosenAsync()
    {
        var staging = Path.Combine(_root, $"cockpit-backup-{Guid.NewGuid():n}.zip");
        var destination = Path.Combine(_root, "chosen-backup.zip");

        await File.WriteAllTextAsync(staging, "the finished archive");
        await File.WriteAllTextAsync(destination, "an earlier backup, to be replaced");

        return (staging, destination);
    }
}
