using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Plugin.OpenRouterProvider.Tests;

public sealed class OpenRouterProviderPluginTests
{
    [Fact]
    public async Task Initialize_ReportsTheConfiguredModelAndOwnEndpoint()
    {
        var host = Substitute.For<ICockpitHost>();
        SessionProviderRegistration? registration = null;
        host.When(value => value.AddSessionProvider(Arg.Any<SessionProviderRegistration>()))
            .Do(call => registration = call.Arg<SessionProviderRegistration>());
        new OpenRouterProviderPlugin().Initialize(host);

        Assert.NotNull(registration);
        Assert.NotNull(registration.ReadModelIntelligenceAsync);
        var config = JsonSerializer.Serialize(new OpenAiCompatConfig("key", "Qwen3.8-27B", "https://hetzner.example/v1"));
        var reading = await registration.ReadModelIntelligenceAsync!(config, CancellationToken.None);

        Assert.True(reading.IsSelfHosted);
        var model = Assert.Single(reading.Models);
        Assert.Equal("Qwen3.8-27B", model.Model);
        Assert.Equal("Qwen3.8-27B", model.PublicModelRef);
    }
}
