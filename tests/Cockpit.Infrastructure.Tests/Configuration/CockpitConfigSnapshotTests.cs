using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

[CollectionDefinition(CockpitConfigSnapshotTests.Alone, DisableParallelization = true)]
public sealed class CockpitConfigSnapshotCollection;

/// <summary>
/// AC-1892: at start-up <c>cockpit.json</c> was parsed and decrypted 193 times, once for every read by every store.
/// These pin the one snapshot per write generation that replaced that, and that a write or an edit from outside
/// the process never leaves a read on the old one. Alone because the parse count is per process.
/// </summary>
[Collection(Alone)]
public sealed class CockpitConfigSnapshotTests : IDisposable
{
    public const string Alone = "Counts cockpit.json parses process-wide";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-snapshot-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_FiveTimesWithinOneWriteGeneration_ParsesTheFileOnce_AndHandsEachCallerItsOwnCopy()
    {
        Directory.CreateDirectory(_directory);
        await new CockpitConfigFileAccess(ConfigPath).UpdateAsync(
            file => (file.Plugins ??= [])["kept"] = new PluginRegistrationEntry(), CancellationToken.None);
        var before = CockpitConfigReads.Parses;

        var first = await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);
        first!.Plugins!.Clear();
        await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);
        await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);
        await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);
        var last = await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);

        Assert.Equal(1, CockpitConfigReads.Parses - before);
        Assert.Contains("kept", last!.Plugins!.Keys);
    }

    [Fact]
    public async Task ReadAsync_AfterAWriteAndAfterAnEditFromOutside_SeesTheNewContent()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(file => (file.Plugins ??= [])["first"] = new PluginRegistrationEntry(), CancellationToken.None);
        await access.ReadAsync(CancellationToken.None);

        await new CockpitConfigFileAccess(ConfigPath).UpdateAsync(
            file => file.Plugins!["written"] = new PluginRegistrationEntry(), CancellationToken.None);
        var afterWrite = await access.ReadAsync(CancellationToken.None);

        // Same length as what is there now, only the timestamp tells them apart: the edit an editor makes.
        var edited = (await File.ReadAllTextAsync(ConfigPath)).Replace("\"written\"", "\"outside\"");
        await File.WriteAllTextAsync(ConfigPath, edited);
        File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddMinutes(1));
        var afterEdit = await access.ReadAsync(CancellationToken.None);

        Assert.Equivalent(new[] { "first", "written" }, afterWrite!.Plugins!.Keys);
        Assert.Equivalent(new[] { "first", "outside" }, afterEdit!.Plugins!.Keys);
    }
}
