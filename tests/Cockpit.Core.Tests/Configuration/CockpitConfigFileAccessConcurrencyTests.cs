using System.Text.Json;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

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
            .Select(index => new SessionProfile($"profile-{index}", $"/home/someone/.claude-{index}", Purpose: new string('x', 400)))
            .ToList();

        var writers = Enumerable.Range(0, 24).Select(index =>
        {
            var access = new CockpitConfigFileAccess(ConfigPath);

            return index % 2 is 0
                ? access.UpdateAsync(config => config.Profiles = [.. longProfiles.Select(SessionProfileEntry.FromDomain)], CancellationToken.None)
                : access.UpdateAsync(config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("solo", "/home/someone/.claude"))], CancellationToken.None);
        });

        await Task.WhenAll(writers);

        var contents = await File.ReadAllTextAsync(ConfigPath);
        var parse = () => JsonSerializer.Deserialize<JsonDocument>(contents);

        parse.Should().NotThrow("a config the cockpit cannot read is a config the cockpit overwrites with an empty one");
    }

    [Fact]
    public async Task UpdateAsync_WhenTwoStoresUpdateDifferentSections_KeepsBoth()
    {
        // The whole promise of this class: each store mutates its own section and preserves the others. Without
        // serialization the read-modify-write of one silently drops the other's just-written section.
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateAsync(config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("seed", "/home/someone/.claude"))], CancellationToken.None);

        var profileWriter = new CockpitConfigFileAccess(ConfigPath);
        var boundsWriter = new CockpitConfigFileAccess(ConfigPath);

        await Task.WhenAll(
            profileWriter.UpdateAsync(
                config => config.Profiles = [SessionProfileEntry.FromDomain(new SessionProfile("written-by-the-profile-store", "/home/someone/.claude"))],
                CancellationToken.None),
            boundsWriter.UpdateAsync(
                config => config.WindowBounds = new WindowBoundsEntry { Width = 1280, Height = 820 },
                CancellationToken.None));

        var written = await new CockpitConfigFileAccess(ConfigPath).ReadAsync(CancellationToken.None);

        written.Should().NotBeNull();
        written!.WindowBounds.Should().NotBeNull("the bounds store wrote them");
        written.Profiles.Should().ContainSingle(profile => profile.Label == "written-by-the-profile-store", "the profile store wrote it");
    }
}
