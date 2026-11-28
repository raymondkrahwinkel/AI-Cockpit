using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// AC-1108: one Apply did a full <c>cockpit.json</c> read-modify-write per store touched — 60+ round-trips once
/// every plugin's own per-field commit is counted. These pin the batch that folds them into one.
/// </summary>
public sealed class CockpitConfigWriteBatchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-write-batch-{Guid.NewGuid():N}");

    // Only the failure path waits this long: every step is released by a gate, never by the clock.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

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
    /// (AC-1085). Disposing the scope must still wait it out. Outside a scope (AC-1343) FlushAsync is what a plugin
    /// store's DisposeAsync calls instead, and it must wait for a write still in flight, not just a finished one.
    /// </summary>
    [Fact]
    public async Task AnUpdateAsyncCallTheCallerNeverAwaited_IsWaitedOutByFlushAsyncAndByDisposingTheScope()
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

        await using (CockpitConfigWriteBatch.Begin())
        {
            _ = access.UpdateAsync(file => (file.Plugins ??= [])["fire-and-forget"] = new PluginRegistrationEntry(), CancellationToken.None);
        }

        var written = await access.ReadAsync(CancellationToken.None);
        Assert.Equivalent(new[] { "held", "fire-and-forget" }, written!.Plugins!.Keys);
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
    /// AC-2424 (category C): a read inside a batch whose first mutation is still loading — the gate already held by
    /// that very batch — used to poll the gate until it timed out, since only the batch's own flush releases it.
    /// It must wait for the batch's load instead. The load is held open by the key holder, so no timing is involved.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WhileTheOwnBatchIsStillLoading_WaitsForThatLoadInsteadOfItsOwnGate()
    {
        Directory.CreateDirectory(_directory);
        await new CockpitConfigFileAccess(ConfigPath).UpdateAsync(
            file => (file.Plugins ??= [])["seeded"] = new PluginRegistrationEntry(), CancellationToken.None);

        using var holder = new HoldingKeyHolder();
        var access = new CockpitConfigFileAccess(ConfigPath, holder);

        await using (CockpitConfigWriteBatch.Begin())
        {
            var mutation = Task.Run(() => access.UpdateAsync(
                file => (file.Plugins ??= [])["mutated"] = new PluginRegistrationEntry(), CancellationToken.None));
            await holder.Entered.Task.WaitAsync(Bound);

            var read = access.ReadAsync(CancellationToken.None);
            holder.Release.Set();

            Assert.Contains("seeded", (await read.WaitAsync(Bound))!.Plugins!.Keys);
            await mutation.WaitAsync(Bound);
        }
    }

    // Blocks the first Protector lookup — made by the batch's load while it holds the write gate — until released.
    private sealed class HoldingKeyHolder : ISecretKeyHolder, IDisposable
    {
        private int _armed = 1;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public ISecretProtector? Protector
        {
            get
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    Entered.SetResult();
                    Release.Wait(Bound);
                }

                return null;
            }
        }

        public SecretFields Fields => SecretFields.ByName;

        public event EventHandler? UnprotectedSecretsWritten
        {
            add { }
            remove { }
        }

        public void NoteUnprotectedSecretsWritten()
        {
        }

        public void Dispose() => Release.Dispose();
    }
}
