using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// Every section of <c>cockpit.json</c> is written by a different store through one read-modify-write, and each
/// of them writes the whole document. That only preserves the other sections if nothing changed one in between
/// — which nothing enforced, so a writer that read early and finished late silently restored someone else's
/// section to what it had been. That is how a plugin's freshly pinned hash disappeared and the plugin came back
/// asking for consent, and it is what these tests hold shut.
/// </summary>
public sealed class CockpitConfigFileAccessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-config-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// The bug, in the smallest shape that shows it: many writers, each touching only its own plugin, all at
    /// once. Every one of them must survive — losing any is the pin that vanished.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ManyWritersAtOnce_NoneLosesAnothersSection()
    {
        Directory.CreateDirectory(_directory);
        var ids = Enumerable.Range(0, 24).Select(index => $"plugin-{index}").ToList();

        await Task.WhenAll(ids.Select(id => Task.Run(async () =>
        {
            var access = new CockpitConfigFileAccess(ConfigPath);
            await access.UpdateAsync(
                file => (file.Plugins ??= [])[id] = new PluginRegistrationEntry { Enabled = true, PinnedSha256 = id },
                CancellationToken.None);
        })));

        var written = await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);
        written!.Plugins!.Keys.Should().BeEquivalentTo(ids);
        written.Plugins.Should().OnlyContain(entry => entry.Value.PinnedSha256 == entry.Key);
    }

    /// <summary>
    /// The same race across processes, which is the one Raymond actually hits: a development build starts beside
    /// the cockpit he already has open, and nothing has ever stopped the two writing over each other. Simulated
    /// by holding the lock file the way another process would.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_WhileAnotherProcessHoldsTheGate_WaitsRatherThanWritingOverIt()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(file => (file.Plugins ??= [])["kept"] = new PluginRegistrationEntry { PinnedSha256 = "kept" }, CancellationToken.None);

        var foreignHold = new FileStream(ConfigPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var write = Task.Run(async () =>
            await access.UpdateAsync(file => file.Plugins!["added"] = new PluginRegistrationEntry { PinnedSha256 = "added" }, CancellationToken.None));

        // It must not have gone through while the gate is held elsewhere.
        await Task.Delay(150);
        write.IsCompleted.Should().BeFalse("the gate is held by another writer");

        foreignHold.Dispose();
        await write;

        var written = await access.ReadAsync(CancellationToken.None);
        written!.Plugins!.Keys.Should().BeEquivalentTo("kept", "added");
    }

    /// <summary>A reader does not queue behind the write gate: it is not a writer, and it has no reason to wait for one to think.</summary>
    [Fact]
    public async Task ReadAsync_WhileTheGateIsHeld_IsNotBlocked()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(file => (file.Plugins ??= [])["there"] = new PluginRegistrationEntry(), CancellationToken.None);

        using var foreignHold = new FileStream(ConfigPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var read = await access.ReadAsync(CancellationToken.None);

        read!.Plugins!.Keys.Should().Contain("there");
    }

    /// <summary>
    /// The read the old claim said could not fail. "A rename is atomic, so a reader never waits on a writer" was
    /// true of the file's <em>content</em> and false of the read: the swap that publishes a write holds the
    /// destination for its duration, and a reader landing in that window is refused outright.
    /// <para>
    /// The test above cannot catch this and never could — it holds the <c>.lock</c> file, which readers ignore by
    /// design. This one holds <c>cockpit.json</c> itself, the way <c>File.Replace</c> does. On 2026-07-15 this
    /// raced at startup, and the caller that did not catch it — global push-to-talk — died silently and took F9
    /// with it for the whole session.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReadAsync_WhileTheFileItselfIsHeldForASwap_WaitsItOut_RatherThanFailing()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(file => (file.Plugins ??= [])["there"] = new PluginRegistrationEntry(), CancellationToken.None);

        var swap = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200);
            swap.Dispose();
        });

        var read = await access.ReadAsync(CancellationToken.None);

        await release;
        read!.Plugins!.Keys.Should().Contain("there", "a read that lands during a swap waits it out rather than throwing");
    }

    /// <summary>Waiting is not forgiving: a hold that outlasts any swap is not contention, and the read has to stop pretending otherwise.</summary>
    [Fact]
    public async Task ReadAsync_WhileTheFileIsHeldIndefinitely_GivesUp_RatherThanHangingForever()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(file => (file.Plugins ??= [])["there"] = new PluginRegistrationEntry(), CancellationToken.None);

        using var held = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var act = async () => await access.ReadAsync(CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
    }

    /// <summary>Each writer still only touches its own section — the gate serialises them, it does not merge them.</summary>
    [Fact]
    public async Task UpdateAsync_DifferentSections_KeepEachOther()
    {
        Directory.CreateDirectory(_directory);
        var access = new CockpitConfigFileAccess(ConfigPath);

        await access.UpdateAsync(file => (file.Plugins ??= [])["a"] = new PluginRegistrationEntry { PinnedSha256 = "a" }, CancellationToken.None);
        await access.UpdateAsync(file => file.Layout = new LayoutSettingsEntry { SingleSessionLayout = true }, CancellationToken.None);

        var written = await access.ReadAsync(CancellationToken.None);
        written!.Plugins!.Keys.Should().Contain("a");
        written.Layout!.SingleSessionLayout.Should().BeTrue();
    }
}
