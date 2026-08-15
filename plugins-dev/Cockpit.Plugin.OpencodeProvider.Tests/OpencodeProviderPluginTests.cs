namespace Cockpit.Plugin.OpencodeProvider.Tests;

// `OpencodeProviderPlugin`'s `SessionProviderRegistration`: the host builds its `PluginSessionDriverAdapter`
// from `SessionProviderRegistration.Capabilities`, never from the driver instance's own
// `IPluginSessionDriver.Capabilities` — a capability the driver supports but this registration does not
// declare is invisible to the host regardless of what the driver itself reports. Same regression test
// `Cockpit.Plugin.KimiProvider.Tests.KimiProviderPluginTests` runs for Kimi, verified against this plugin's
// own registration rather than assumed to be safe because Kimi's own equivalent passes.
public class OpencodeProviderPluginTests
{
    [Fact]
    public void Initialize_RegistersASessionProvider_WhoseCapabilities_DeclareSupportsLiveModelSwitch()
    {
        var host = new FakeCockpitHost();

        using var plugin = new OpencodeProviderPlugin();
        plugin.Initialize(host);

        Assert.NotNull(host.CapturedRegistration);
        Assert.True(host.CapturedRegistration!.Capabilities.SupportsLiveModelSwitch,
            "the host reads registration.Capabilities, not the driver instance's own Capabilities, to build the session adapter");
        Assert.True(host.CapturedRegistration.Capabilities.SupportsTools);
        Assert.True(host.CapturedRegistration.Capabilities.SupportsPermissions);
        Assert.Equal("opencode-provider.acp", host.CapturedRegistration.ProviderId);
    }
}
