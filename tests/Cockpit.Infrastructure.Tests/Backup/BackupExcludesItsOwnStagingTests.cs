using System.IO.Compression;
using Cockpit.Core.Backup;
using Cockpit.Infrastructure.Backup;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>AC-689: the archive being written must not sweep itself into its own contents.</summary>
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

    /// <summary>The operator's exact message, reproduced with no virus scanner in sight.</summary>
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
