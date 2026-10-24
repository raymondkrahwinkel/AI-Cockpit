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
            BackupService.StagingIn(CockpitConfigPath.Root),
            StringComparison.Ordinal);

    /// <summary>
    /// The regression: the archive being written is under the tree being walked, so the walk must skip it.
    /// </summary>
    [Fact]
    public void TheArchiveBeingWritten_IsNotSweptIntoItself()
    {
        var staged = Path.Combine(BackupService.StagingIn(CockpitConfigPath.Root), $"cockpit-backup-{Guid.NewGuid():n}.zip");

        Assert.False(BackupContents.Includes(Path.GetRelativePath(CockpitConfigPath.Root, staged)));
    }
}
