using Cockpit.Core.Backup;

namespace Cockpit.Core.Tests.Backup;

/// <summary>
/// Choosing what goes into a backup and what comes back out of one (#70).
/// <para>
/// The rule underneath both: a plugin's binaries and everything it stored travel together. Archiving the folder and
/// leaving its settings behind — or the other way round — restores half a plugin, which works right up until it does
/// not, and then does so in a way nobody can explain.
/// </para>
/// </summary>
public class BackupSelectionTests
{
    [Fact]
    public void ABackupWithNoListOfPlugins_TakesThemAll_BecauseThatIsWhatABackupIs()
    {
        var options = new BackupOptions();

        Assert.True(options.Includes("youtrack"));
        Assert.True(options.Includes("anything-at-all"));
    }

    [Fact]
    public void AChosenPlugin_GoesIn_AndTheOthersDoNot()
    {
        var options = new BackupOptions(Plugins: ["youtrack", "workflows"]);

        Assert.True(options.Includes("youtrack"));
        Assert.True(options.Includes("WORKFLOWS"), "a plugin id is not case to argue over");
        Assert.False(options.Includes("git-status"));
    }

    [Fact]
    public void AnEmptyList_TakesNoPlugins_WhichIsNotTheSameAsNoListAtAll()
    {
        // "None" and "all" are different answers, and the difference is the whole reason the list is nullable.
        var options = new BackupOptions(Plugins: []);

        Assert.False(options.Includes("youtrack"));
    }

    [Fact]
    public void ARestore_TouchesOnlyWhatWasTicked()
    {
        var options = new RestoreOptions(Settings: false, Plugins: ["youtrack"]);

        Assert.False(options.Settings, "this cockpit's own profiles and settings stay exactly as they are");
        Assert.True(options.Includes("youtrack"));
        Assert.False(options.Includes("workflows"));
    }
}
