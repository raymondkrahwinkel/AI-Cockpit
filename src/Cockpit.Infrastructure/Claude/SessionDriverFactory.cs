using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// <see cref="ISessionDriverFactory"/> that resolves a fresh driver per session from the container. It is
/// an orchestrator building a runtime-parameterized child (the driver chosen by the profile's provider),
/// which is the sanctioned use of <see cref="IServiceProvider"/> (Code.md §2) — both built-in drivers are
/// transient, so each call yields a new instance for the new session. A <see cref="SessionProvider.Plugin"/>
/// profile grows one more arm (#45): the registered plugin's own driver factory is resolved from
/// <see cref="IPluginProviderRegistry"/> and wrapped in a <see cref="PluginSessionDriverAdapter"/>.
/// </summary>
internal sealed class SessionDriverFactory(IServiceProvider services, IPluginProviderRegistry pluginProviderRegistry) : ISessionDriverFactory, ISingletonService
{
    public ISessionDriver Create(ClaudeProfile? profile)
    {
        if (profile is null)
        {
            return services.GetRequiredService<ClaudeCliSession>();
        }

        return profile.Provider switch
        {
            SessionProvider.Ollama or SessionProvider.LmStudio => services.GetRequiredService<OpenAiCompatSessionDriver>(),
            SessionProvider.Plugin => _CreatePluginDriver(profile),
            _ => services.GetRequiredService<ClaudeCliSession>(),
        };
    }

    private ISessionDriver _CreatePluginDriver(ClaudeProfile profile)
    {
        if (profile.ProviderConfig is not PluginProviderConfig pluginConfig)
        {
            throw new InvalidOperationException($"A {nameof(SessionProvider.Plugin)} profile must carry a {nameof(PluginProviderConfig)}.");
        }

        var registration = pluginProviderRegistry.Resolve(pluginConfig.ProviderId)
            ?? throw new InvalidOperationException($"No plugin session provider is registered for '{pluginConfig.ProviderId}'.");

        var driver = registration.CreateDriverFactory(services).Create(pluginConfig.ConfigJson);
        return new PluginSessionDriverAdapter(driver, registration.Capabilities);
    }
}
