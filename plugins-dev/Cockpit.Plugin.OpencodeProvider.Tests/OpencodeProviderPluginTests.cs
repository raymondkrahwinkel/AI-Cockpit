namespace Cockpit.Plugin.OpencodeProvider.Tests;

// The host builds its driver adapter from registration.Capabilities, never the driver instance's own —
// same regression test KimiProviderPluginTests runs for Kimi, verified independently here.
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
