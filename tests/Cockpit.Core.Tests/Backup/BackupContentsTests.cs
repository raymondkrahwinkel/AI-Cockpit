using Cockpit.Core.Backup;

namespace Cockpit.Core.Tests.Backup;

/// <summary>
/// What goes into a backup, what stays out, and which archives this build will read back (#70, AC-1276). The
/// list is what the class exists for: a backup that weighs two gigabytes because it swept up the Whisper
/// weights and every git worktree is a backup you make once and never again.
/// </summary>
public class BackupContentsTests
{
    [Theory]
    [InlineData("cockpit.json", true)]
    [InlineData("mcp-permission.json", true)]
    [InlineData("assistant-memory.md", true)]
    [InlineData("project-logos\\acme.png", true)]
    // The binaries come back out of their store, not out of the archive (AC-1275).
    [InlineData("plugins/youtrack/plugin.json", false)]
    [InlineData("models/ggml-large-v3.bin", false)]
    [InlineData("logs/cockpit.log", false)]
    // The two that walked in unannounced (AC-1276): gigabytes of checkouts nobody meant to archive.
    [InlineData("worktrees/cockpit/branch/file.cs", false)]
    [InlineData("cli/claude.exe", false)]
    // The point of an include list: a folder invented six months from now is out until someone names it.
    [InlineData("something-nobody-has-thought-of-yet/state.db", false)]
    // A file merely named like an included folder is not mistaken for one.
    [InlineData("plugins.json", false)]
    public void OnlyWhatIsNamed_GoesIn_AndAnythingNewStaysOut(string path, bool included) =>
        Assert.Equal(included, BackupContents.Includes(path));

    [Fact]
    public void ABackupTakesNoCredentialsUnlessAsked() =>
        Assert.Equivalent(new BackupOptions(IncludeCredentials: false, IncludeProfileConfigs: false), new BackupOptions());

    [Theory]
    [InlineData(BackupManifest.CurrentSchema - 1, "old layout")]
    [InlineData(BackupManifest.CurrentSchema, null)]
    [InlineData(BackupManifest.CurrentSchema + 1, "newer cockpit")]
    public void AnArchiveOfAnotherLayout_IsRefused_InWordsThatFitWhichWayItIsWrong(int schema, string? refusal)
    {
        var manifest = new BackupManifest(
            schema,
            "9.0.0",
            DateTimeOffset.UtcNow,
            IncludesCredentials: false,
            RemovedSecrets: [],
            ProfileConfigDirectories: new Dictionary<string, string>(),
            Plugins: new Dictionary<string, string>());

        Assert.Equal(refusal is null, manifest.CanRestore);
        Assert.Contains(refusal ?? string.Empty, manifest.RestoreRefusal ?? string.Empty, StringComparison.Ordinal);
    }
}
