using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSettings` (AC-499): the one-time migration of a connection URL saved before `DepotUrlNormalizer`
// existed. Must run exactly once and persist — `Normalize` is not safe to re-run on its own output, so a
// second run over already-migrated data would wrongly strip a base that legitimately ends in `/mcp`.
public class DepotSettingsTests
{
    [Fact]
    public void Connections_LegacyUrlWithTrailingMcp_IsNormalizedOnRead_AndTheMigratedValueIsPersisted()
    {
        // Read and write in one exercise on purpose: a migration that only fixed the value on the way out would
        // keep re-running, and re-running is exactly what the third test below shows to be unsafe.
        var storage = new FakePluginStorage();
        storage.Set("connections", new List<DepotConnectionRegistration> { new("id-1", "Work", "https://depot.example.com/mcp") });
        var settings = new DepotSettings(storage);

        var url = settings.Connections.Single().Url;

        Assert.Equal("https://depot.example.com", url);
        Assert.Equal("https://depot.example.com", storage.Get<List<DepotConnectionRegistration>>("connections")!.Single().Url);
    }

    // The case that would break if migration ran on every read instead of exactly once: a base whose own
    // deployment path is /mcp (its real endpoint is .../mcp/mcp) is the CORRECT normalized form already. A second
    // pass of Normalize would wrongly strip that segment too.
    [Fact]
    public void Connections_AlreadyMigratedUrlEndingInMcp_IsNotStrippedAgainOnASecondRead()
    {
        var storage = new FakePluginStorage();
        storage.Set("connections", new List<DepotConnectionRegistration> { new("id-1", "Work", "https://host/mcp/mcp") });
        var settings = new DepotSettings(storage);

        _ = settings.Connections; // first read: migrates .../mcp/mcp -> .../mcp, marks migration done
        var secondReadUrl = settings.Connections.Single().Url; // second read: must not strip again

        Assert.Equal("https://host/mcp", secondReadUrl);
    }

    [Fact]
    public void Connections_AlreadyCleanUrl_IsUnaffectedByMigration()
    {
        var storage = new FakePluginStorage();
        storage.Set("connections", new List<DepotConnectionRegistration> { new("id-1", "Work", "https://depot.example.com") });
        var settings = new DepotSettings(storage);

        var url = settings.Connections.Single().Url;

        Assert.Equal("https://depot.example.com", url);
    }

    [Fact]
    public void Connections_NoStoredConnections_ReturnsEmpty()
    {
        var storage = new FakePluginStorage();
        var settings = new DepotSettings(storage);

        Assert.Empty(settings.Connections);
    }
}
