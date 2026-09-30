using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// AC-1108: one Apply did a full <c>cockpit.json</c> read-modify-write per store touched — 60+ round-trips once
/// every plugin's own per-field commit is counted. These pin the batch that folds them into one.
/// </summary>
public sealed class CockpitConfigWriteBatchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-write-batch-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// <c>PluginStorage.Set</c> fires its write as <c>_ = store.SaveDataAsync(...)</c>, never awaited by the
    /// caller — which is how a write could still be in flight when an Apply "finished" and the dialog closed
    /// (AC-1085). Disposing the scope must still wait it out.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WaitsOutAnUpdateAsyncCallTheCallerNeverAwaited()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);

        await using (CockpitConfigWriteBatch.Begin())
        {
            _ = access.UpdateAsync(file => (file.Plugins ??= [])["fire-and-forget"] = new PluginRegistrationEntry(), CancellationToken.None);
        }

        var written = await access.ReadAsync(CancellationToken.None);
        Assert.Contains("fire-and-forget", written!.Plugins!.Keys);
    }

    /// <summary>
    /// A late apply — a fire-and-forget continuation resuming after its own scope already flushed — must be
    /// refused, not silently applied to a copy nobody writes again. Disposing from inside <c>Task.Run</c> mirrors
    /// how that happens for real: AsyncLocal changes made there don't propagate back to this method's own value,
    /// so the batch still looks current here even though it has already flushed on another flow.
    /// </summary>
    [Fact]
    public async Task TryApply_AfterAnotherFlowAlreadyFlushedTheBatch_IsRefusedRatherThanLost()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);

        var batch = CockpitConfigWriteBatch.Begin();
        await access.UpdateAsync(file => (file.Plugins ??= [])["early"] = new PluginRegistrationEntry(), CancellationToken.None);
        await Task.Run(async () => await batch.DisposeAsync());

        var accepted = CockpitConfigWriteBatch.TryApply(
            access, file => (file.Plugins ??= [])["late"] = new PluginRegistrationEntry(), CancellationToken.None, out _);
        Assert.False(accepted);

        var written = await access.ReadAsync(CancellationToken.None);
        Assert.Contains("early", written!.Plugins!.Keys);
        Assert.DoesNotContain("late", written.Plugins.Keys);
    }

}
