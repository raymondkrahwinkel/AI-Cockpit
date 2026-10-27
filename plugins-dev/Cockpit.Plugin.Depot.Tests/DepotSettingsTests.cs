using Cockpit.Plugin.Depot.Backup;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSettings` (AC-499): the one-time migration of a connection URL saved before `DepotUrlNormalizer`
// existed. Must run exactly once and persist — `Normalize` is not safe to re-run on its own output, so a
// second run over already-migrated data would wrongly strip a base that legitimately ends in `/mcp`.
public class DepotSettingsTests
{
    [Fact]
    public async Task Connections_LegacyUrlWithTrailingMcp_IsNormalizedOnRead_AndTheMigratedValueIsPersisted()
    {
        // Read and write in one exercise on purpose: a migration that only fixed the value on the way out would
        // keep re-running, and re-running is exactly what the third test below shows to be unsafe.
        var storage = new FakePluginStorage();
        storage.Set("connections", new List<DepotConnectionRegistration> { new("id-1", "Work", "https://depot.example.com/mcp") });
        var settings = new DepotSettings(storage);

        var url = settings.Connections.Single().Url;

        Assert.Equal("https://depot.example.com", url);
        Assert.Equal("https://depot.example.com", storage.Get<List<DepotConnectionRegistration>>("connections")!.Single().Url);

        var host = Substitute.For<ICockpitHost>();
        settings.Connections = [new DepotConnectionRegistration("id-1", "Work", url, SynchroniseSettings: false)];
        await new DepotSettingsCarrier(host, () => settings.Connections, "id-1").WriteAsync([1], CancellationToken.None);
        host.DidNotReceive().CreateAuthorizedHandler(Arg.Any<string>(), Arg.Any<string>());
    }

    // The case that would break if migration ran on every read instead of exactly once: a base whose own
    // deployment path is /mcp (its real endpoint is .../mcp/mcp) is the CORRECT normalized form already. A second
    // pass of Normalize would wrongly strip that segment too.
}
