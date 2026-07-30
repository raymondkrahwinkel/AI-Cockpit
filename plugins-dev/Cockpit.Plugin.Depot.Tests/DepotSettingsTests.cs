using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// <see cref="DepotSettings"/> (AC-499): the one-time migration of a connection <see cref="DepotConnectionRegistration.Url"/>
/// saved before <see cref="DepotUrlNormalizer"/> existed. Must run exactly once and persist — <c>Normalize</c> is no
/// longer safe to re-run on output it already produced (see its own doc comment), so a second run over the same
/// migrated data would wrongly strip a base that legitimately ends in <c>/mcp</c>.
/// </summary>
public class DepotSettingsTests
{
    [Fact]
    public void Connections_LegacyUrlWithTrailingMcp_IsNormalizedOnRead()
    {
        var storage = new FakePluginStorage();
        storage.Set("connections", new List<DepotConnectionRegistration> { new("id-1", "Work", "https://depot.example.com/mcp") });
        var settings = new DepotSettings(storage);

        var url = settings.Connections.Single().Url;

        Assert.Equal("https://depot.example.com", url);
    }

    [Fact]
    public void Connections_LegacyUrlWithTrailingMcp_PersistsTheMigratedValue()
    {
        var storage = new FakePluginStorage();
        storage.Set("connections", new List<DepotConnectionRegistration> { new("id-1", "Work", "https://depot.example.com/mcp") });
        var settings = new DepotSettings(storage);
        _ = settings.Connections;

        var storedDirectly = storage.Get<List<DepotConnectionRegistration>>("connections")!.Single();

        Assert.Equal("https://depot.example.com", storedDirectly.Url);
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
