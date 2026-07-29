using Cockpit.Core.Backup;

namespace Cockpit.Core.Tests.Backup;

/// <summary>
/// What goes into a backup and what does not (#70). The models are the reason this class exists: a backup that weighs
/// two gigabytes because it swept up the Whisper weights is a backup you make once and never again.
/// </summary>
public class BackupContentsTests
{
    [Theory]
    [InlineData("cockpit.json", true)]
    [InlineData("plugins/youtrack/plugin.json", true)]
    [InlineData("mcp-permission.json", true)]
    [InlineData("delegation-audit.jsonl", true)]
    [InlineData("models/ggml-large-v3.bin", false)]
    [InlineData("models\\piper\\nl.onnx", false)]
    [InlineData("logs/cockpit.log", false)]
    public void TheModelsAndTheLogs_StayOut_EverythingElseGoesIn(string path, bool included) =>
        Assert.Equal(included, BackupContents.Includes(path));

    [Fact]
    public void AFileMerelyNamedLikeAnExcludedFolder_IsNotMistakenForOne() =>
        Assert.True(BackupContents.Includes("models.json"));

    [Fact]
    public void ABackupTakesNoCredentialsUnlessAsked() =>
        Assert.Equivalent(new BackupOptions(IncludeCredentials: false, IncludeProfileConfigs: false), new BackupOptions());

    [Fact]
    public void ABackupFromAFutureCockpit_IsRefused_RatherThanHalfApplied()
    {
        var manifest = new BackupManifest(
            BackupManifest.CurrentSchema + 1,
            "9.0.0",
            DateTimeOffset.UtcNow,
            IncludesCredentials: false,
            RemovedSecrets: [],
            ProfileConfigDirectories: new Dictionary<string, string>(),
            Plugins: new Dictionary<string, string>());

        Assert.False(manifest.CanRestore);
    }
}
