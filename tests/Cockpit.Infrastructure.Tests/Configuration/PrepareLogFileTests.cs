using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// The log is truncated per run so the live one is readable, which also meant the only run anyone could ever read
/// was the one still going. A cockpit that had disappeared was therefore unanswerable by design: starting it again
/// to go looking was itself what destroyed the evidence. Three generations are kept alongside now, and that is what
/// these hold shut.
/// </summary>
public sealed class PrepareLogFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-log-{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_directory, "logs", "cockpit.log");

    private string PreviousLogPath => LogPath + CredentialFileHousekeeping.PreviousLogSuffix;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>The run that ended survives the start of the one that goes looking for it.</summary>
    [Fact]
    public void PrepareLogFile_WithALogFromTheRunBefore_KeepsItAndStartsTheNewOneEmpty()
    {
        CredentialFileHousekeeping.PrepareLogFile(LogPath);
        File.AppendAllText(LogPath, "the run that vanished");

        CredentialFileHousekeeping.PrepareLogFile(LogPath);

        Assert.Equal("the run that vanished", File.ReadAllText(PreviousLogPath));
        Assert.Equal(string.Empty, File.ReadAllText(LogPath));
    }

    /// <summary>
    /// AC-1113: three generations, and no more — two quick restarts after a freeze used to discard the very run
    /// that froze. Bounded is still the point: the alternative is a rolling policy nobody asked for.
    /// </summary>
    [Fact]
    public void PrepareLogFile_RunAfterRun_KeepsThreeGenerationsAndDropsTheFourth()
    {
        foreach (var run in new[] { "oldest", "older", "old", "newest" })
        {
            CredentialFileHousekeeping.PrepareLogFile(LogPath);
            File.AppendAllText(LogPath, run);
        }

        CredentialFileHousekeeping.PrepareLogFile(LogPath);

        Assert.Equal("newest", File.ReadAllText(CredentialFileHousekeeping.KeptLogPath(LogPath, 1)));
        Assert.Equal("old", File.ReadAllText(CredentialFileHousekeeping.KeptLogPath(LogPath, 2)));
        Assert.Equal("older", File.ReadAllText(CredentialFileHousekeeping.KeptLogPath(LogPath, 3)));

        // The live file plus exactly three kept generations — "oldest" is gone, nothing accumulates.
        Assert.Equal(4, Directory.GetFiles(Path.GetDirectoryName(LogPath)!).Length);
    }

    /// <summary>A first run has nothing to keep, and must not fail for the lack of it.</summary>
    [Fact]
    public void PrepareLogFile_OnAFirstRun_CreatesTheLogWithoutAKeptCopy()
    {
        CredentialFileHousekeeping.PrepareLogFile(LogPath);

        Assert.True(File.Exists(LogPath));
        Assert.False(File.Exists(PreviousLogPath));
    }
}
