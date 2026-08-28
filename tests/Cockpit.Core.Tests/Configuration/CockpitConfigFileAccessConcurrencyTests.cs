using System.Text.Json;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// Seventeen stores each construct their own <see cref="CockpitConfigFileAccess"/> over the same
/// <c>cockpit.json</c> — the profile store, the window bounds, the plugins' storage, the rest. They write
/// whenever their own section changes, which means they write at the same time as each other, and nothing
/// serialized them.
/// <para>
/// This is what damaged Raymond's real config on 2026-07-14: valid JSON followed by the tail of a longer
/// document. The rename was atomic; the sidecar it renamed was not.
/// </para>
/// </summary>
public class CockpitConfigFileAccessConcurrencyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-config-race-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public CockpitConfigFileAccessConcurrencyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UpdateAsync_WhenTwoStoresWriteAtOnce_LeavesTheFileReadable()
    {
        // One writer makes the document long, the other short. Interleaved on a shared sidecar, the short
        // document lands first and the tail of the long one survives behind it — the file then parses up to a
        // point and is garbage after it, which is exactly the shape the operator's config was found in.
        var longProfiles = Enumerable.Range(0, 400)
            .Select(index => new SessionProfile($"profile-{index}", new ClaudeConfig($"/home/someone/.claude-{index}"), Purpose: new string('x', 400)))
            .ToList();

        var writers = Enumerable.Range(0, 24).Select(index =>
        {
            var access = new CockpitConfigFileAccess(ConfigPath);

            return index % 2 is 0
                ? access.UpdateAsync(config => config.Profiles = [.. longProfiles.Select(SessionProfileEntry.FromDomain)], CancellationToken.None)
                : access.UpdateAsync(config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("solo", new ClaudeConfig("/home/someone/.claude")))], CancellationToken.None);
        });

        await Task.WhenAll(writers);

        var contents = await File.ReadAllTextAsync(ConfigPath);
        var parse = () => JsonSerializer.Deserialize<JsonDocument>(contents);

        parse();
    }

    [Fact]
    public async Task UpdateAsync_WhenTwoStoresUpdateDifferentSections_KeepsBoth()
    {
        // The whole promise of this class: each store mutates its own section and preserves the others. Without
        // serialization the read-modify-write of one silently drops the other's just-written section.
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("seed", new ClaudeConfig("/home/someone/.claude")))], CancellationToken.None);

        var profileWriter = new CockpitConfigFileAccess(ConfigPath);
        var boundsWriter = new CockpitConfigFileAccess(ConfigPath);

        await Task.WhenAll(
            profileWriter.UpdateAsync(
                config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("written-by-the-profile-store", new ClaudeConfig("/home/someone/.claude")))],
                CancellationToken.None),
            boundsWriter.UpdateAsync(
                config => config.WindowBounds = new Dictionary<string, WindowBoundsEntry> { ["main"] = new() { Width = 1280, Height = 820 } },
                CancellationToken.None));

        var written = await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);

        Assert.NotNull(written);
        Assert.NotNull(written.WindowBounds);
        Assert.Single(written.Profiles, profile => profile.Label == "written-by-the-profile-store");
    }

    [Fact]
    public async Task UpdateAsync_WhileAnotherStoreIsReading_StillWrites()
    {
        // AC-1047: readers do not take the write gate, and `File.Replace` refuses a destination somebody else
        // has open — so a save landing while any store reloads threw, and that section was silently lost.
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(
            config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("seed", new ClaudeConfig("/home/someone/.claude")))],
            CancellationToken.None);

        using var readers = new CancellationTokenSource();
        var reading = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!readers.IsCancellationRequested)
            {
                await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);
            }
        })).ToArray();

        for (var index = 0; index < 40; index++)
        {
            var label = $"written-{index}";
            await access.UpdateAsync(
                config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile(label, new ClaudeConfig("/home/someone/.claude")))],
                CancellationToken.None);
        }

        await readers.CancelAsync();
        await Task.WhenAll(reading);

        var written = await access.ReadAsync(CancellationToken.None);

        Assert.NotNull(written);
        Assert.Single(written.Profiles, profile => profile.Label == "written-39");
    }

    [SkippableFact]
    public async Task UpdateAsync_WhileReaderHoldsDestination_WaitsAndWrites()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only Windows takes a mandatory sharing lock on the replacement destination.");

        // Unix `rename`, used by File.Replace, replaces the directory entry while this reader retains
        // the old inode; only Windows' mandatory sharing lock makes the writer wait.

        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(
            config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("seed", new ClaudeConfig("/home/someone/.claude")))],
            CancellationToken.None);

        using var reader = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var write = Task.Run(() => access.UpdateAsync(
            config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("written", new ClaudeConfig("/home/someone/.claude")))],
            CancellationToken.None));

        await Task.Delay(100);
        Assert.False(write.IsCompleted, "the reader holds the replacement destination");

        reader.Dispose();
        await write;

        var written = await access.ReadAsync(CancellationToken.None);
        Assert.Single(written!.Profiles, profile => profile.Label == "written");
    }

    [Fact]
    public async Task UpdateAsync_WhenManyWritersMutateAtOnce_KeepsEveryMutation()
    {
        // AC-1047 criterion 3. Each writer appends its own profile to what it read, which is the read-modify-write
        // the Options dialog's Apply does across sections: a writer that reads a document another has already
        // moved on from writes that staleness back, and the setting it overwrote is gone with no sign of it.
        var writers = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            new CockpitConfigFileAccess(ConfigPath).UpdateAsync(
                config => config.Profiles =
                [
                    .. config.Profiles,
                    SessionProfileEntry.FromDomain(new SessionProfile($"writer-{index}", new ClaudeConfig($"/home/someone/.claude-{index}"))),
                ],
                CancellationToken.None)));

        await Task.WhenAll(writers);

        var written = await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);

        Assert.NotNull(written);
        Assert.Equal(
            [.. Enumerable.Range(0, 16).Select(index => $"writer-{index}").Order()],
            written.Profiles.Select(profile => profile.Label).Order());
    }
}
