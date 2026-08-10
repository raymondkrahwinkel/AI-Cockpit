using System.IO.Compression;
using Cockpit.Core.Backup;
using Cockpit.Infrastructure.Backup;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>
/// "Create backup…" failed with "The process cannot access the file
/// '…\Cockpit\staging\cockpit-backup-{guid}.zip' because it is being used by another process" (AC-689), naming a
/// fresh guid every attempt.
/// <para>
/// It was not the virus scanner the earlier fix assumed. AC-45 moved staging out of the shared temp directory to
/// under the cockpit's own root — which is the directory a backup walks. So the archive is opened for writing in
/// `staging/`, and the walk that fills it then reaches that very file and tries to put it inside itself. The
/// writer holds it exclusively, so this is a sharing violation on every run, not a race that can be waited out.
/// </para>
/// </summary>
public class BackupExcludesItsOwnStagingTests
{
    [Fact]
    public void TheStagingDirectory_LivesInsideTheDirectoryThatGetsBackedUp() =>
        Assert.StartsWith(
            CockpitConfigPath.Root + Path.DirectorySeparatorChar,
            BackupService.StagingRoot,
            StringComparison.Ordinal);

    /// <summary>
    /// The regression: the archive being written is under the tree being walked, so the walk must skip it.
    /// </summary>
    [Fact]
    public void TheArchiveBeingWritten_IsNotSweptIntoItself()
    {
        var staged = Path.Combine(BackupService.StagingRoot, $"cockpit-backup-{Guid.NewGuid():n}.zip");

        Assert.False(BackupContents.Includes(Path.GetRelativePath(CockpitConfigPath.Root, staged)));
    }

    /// <summary>Why it must: this is the operator's exact message, reproduced without a scanner in sight.</summary>
    [Fact]
    public void ZippingTheArchiveThatIsStillBeingWritten_IsTheReportedSharingViolation()
    {
        var directory = Directory.CreateTempSubdirectory("cockpit-backup-self").FullName;

        try
        {
            var staged = Path.Combine(directory, $"cockpit-backup-{Guid.NewGuid():n}.zip");
            using var archive = ZipFile.Open(staged, ZipArchiveMode.Create);

            var violation = Assert.Throws<IOException>(
                () => archive.CreateEntryFromFile(staged, "cockpit/staging/itself.zip", CompressionLevel.Optimal));

            Assert.Contains(staged, violation.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
