namespace Cockpit.Plugin.KimiProvider.Tests;

// `KimiProviderPlugin`'s `SessionProviderRegistration` (P1-4): the host builds its
// `PluginSessionDriverAdapter` from `SessionProviderRegistration.Capabilities`, never from the
// driver instance's own `IPluginSessionDriver.Capabilities` — a capability the driver supports but
// this registration does not declare is invisible to the host regardless of what the driver itself reports.
public class KimiProviderPluginTests
{
    [Fact]
    public void Initialize_RegistersASessionProvider_WhoseCapabilities_DeclareSupportsLiveModelSwitch()
    {
        var host = new FakeCockpitHost();

        using var plugin = new KimiProviderPlugin();
        plugin.Initialize(host);

        Assert.NotNull(host.CapturedRegistration);
        Assert.True(host.CapturedRegistration!.Capabilities.SupportsLiveModelSwitch,
            "the host reads registration.Capabilities, not the driver instance's own Capabilities, to build the session adapter");
    }
}
