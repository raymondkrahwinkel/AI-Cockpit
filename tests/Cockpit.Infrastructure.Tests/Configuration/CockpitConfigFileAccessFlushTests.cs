using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// AC-1343: PluginRegistrationStore/PluginSecretFieldStore fire their writes as <c>_ = store.SaveDataAsync(...)</c>
/// — <c>IPluginStorage.Set</c> is a void callback, so they cannot await it — with nothing else waiting them out
/// before process exit. <see cref="CockpitConfigFileAccess.FlushAsync"/> is what their <c>DisposeAsync</c> calls
/// instead; this pins that it actually waits for a write still in flight, not just for one already finished.
/// </summary>
public sealed class CockpitConfigFileAccessFlushTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-config-flush-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task FlushAsync_WaitsOutAWriteTheCallerNeverAwaited()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);
        var release = new TaskCompletionSource();

        // Fired the way PluginRegistrationStore.SaveDataAsync's caller does: `_ =`, never awaited.
        _ = access.UpdateAsync(file =>
        {
            release.Task.GetAwaiter().GetResult();
            (file.Plugins ??= [])["held"] = new PluginRegistrationEntry();
        }, CancellationToken.None);

        var flush = access.FlushAsync();
        Assert.False(flush.IsCompleted);

        release.SetResult();
        await flush;

        var written = await access.ReadAsync(CancellationToken.None);
        Assert.Contains("held", written!.Plugins!.Keys);
    }
}
