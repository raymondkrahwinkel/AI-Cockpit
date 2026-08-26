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
    /// The regression guard. <c>ReplaceAtomicallyPrivate</c> keeps the file's pre-write content as <c>.bak</c> on
    /// every real write, so a second real write would leave <c>.bak</c> holding some but not all of the batched
    /// keys instead of the pristine pre-batch content — the round-trip count is observable that way.
    /// </summary>
    [Fact]
    public async Task Begin_FoldsEveryUpdateAsyncCallInTheScope_IntoOneRoundTrip()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(file => (file.Plugins ??= [])["seed"] = new PluginRegistrationEntry(), CancellationToken.None);
        var beforeBatch = await File.ReadAllTextAsync(ConfigPath);

        await using (CockpitConfigWriteBatch.Begin())
        {
            for (var i = 0; i < 20; i++)
            {
                var key = $"plugin-{i}";
                await access.UpdateAsync(file => (file.Plugins ??= [])[key] = new PluginRegistrationEntry(), CancellationToken.None);
            }
        }

        var written = await access.ReadAsync(CancellationToken.None);
        Assert.Equal(21, written!.Plugins!.Count);

        var backup = await File.ReadAllTextAsync(ConfigPath + ".bak");
        Assert.Equal(beforeBatch, backup);
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
    /// Why <c>ReadAsync</c> has to consult the batch too, not just <c>UpdateAsync</c>:
    /// <c>GlobalHotkeyCoordinator.ApplyAsync</c> re-reads a section a sibling save just wrote, mid-Apply, before
    /// the batch has flushed anything.
    /// </summary>
    [Fact]
    public async Task ReadAsync_MidBatch_SeesAnEarlierMutationInTheSameBatch_NotStaleDisk()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);

        await using (CockpitConfigWriteBatch.Begin())
        {
            await access.UpdateAsync(file => (file.Plugins ??= [])["screenshot"] = new PluginRegistrationEntry(), CancellationToken.None);

            var midBatch = await access.ReadAsync(CancellationToken.None);
            Assert.Contains("screenshot", midBatch!.Plugins!.Keys);
        }
    }

    /// <summary>A mutation that throws must not leave the write gate held — every other writer in the process
    /// would otherwise stall for its ten-second timeout, forever, since nothing releases it.</summary>
    [Fact]
    public async Task DisposeAsync_ReleasesTheWriteGate_EvenWhenAMutationThrows()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using (CockpitConfigWriteBatch.Begin())
            {
                await access.UpdateAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);
            }
        });

        // Hangs for CockpitConfigWriteGate's ten-second timeout if the gate leaked.
        await access.UpdateAsync(file => (file.Plugins ??= [])["after"] = new PluginRegistrationEntry(), CancellationToken.None);
        var written = await access.ReadAsync(CancellationToken.None);
        Assert.Contains("after", written!.Plugins!.Keys);
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

    /// <summary>
    /// A nested <c>Begin()</c> must join the outer batch rather than re-acquire the write gate, which
    /// <c>CockpitConfigWriteGate</c> is explicitly not built to hand out twice — that would stall for its own
    /// timeout and then throw.
    /// </summary>
    [Fact]
    public async Task Begin_Nested_JoinsTheOuterBatch_AndFlushesOnceOnTheOutermostDispose()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);

        await using (var outer = CockpitConfigWriteBatch.Begin())
        {
            await access.UpdateAsync(file => (file.Plugins ??= [])["outer"] = new PluginRegistrationEntry(), CancellationToken.None);

            await using (var inner = CockpitConfigWriteBatch.Begin())
            {
                Assert.Same(outer, inner);
                await access.UpdateAsync(file => (file.Plugins ??= [])["inner"] = new PluginRegistrationEntry(), CancellationToken.None);
            }

            await access.UpdateAsync(file => (file.Plugins ??= [])["after-inner"] = new PluginRegistrationEntry(), CancellationToken.None);
        }

        var written = await access.ReadAsync(CancellationToken.None);
        Assert.Equal(["after-inner", "inner", "outer"], written!.Plugins!.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }
}
