using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

public sealed class PluginModelIntelligenceReaderTests
{
    // AC-1808: a provider built before the read existed leaves it null, and the host reads that as unknown, not as an
    // empty reading that would show as zero, and without throwing.
    [Fact]
    public async Task ReadAsync_ForAProviderWithoutTheRead_IsUnknown()
    {
        var registry = new PluginProviderRegistry();
        registry.Register(new SessionProviderRegistration("old-provider", "Old", _ => Substitute.For<IPluginSessionDriverFactory>(), new PluginSessionCapabilities(false, false, false)));
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("Old", new PluginProviderConfig("old-provider", "{}"))]);

        var reading = await new PluginModelIntelligenceReader(registry, profiles).ReadAsync("Old");

        Assert.Null(reading);
    }
}
