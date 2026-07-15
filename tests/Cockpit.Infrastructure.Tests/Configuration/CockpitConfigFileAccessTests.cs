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

    /// <summary>A reader must never wait on a writer: the rename is atomic, so it sees the whole old file or the whole new one.</summary>
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
