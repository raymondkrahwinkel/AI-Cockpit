using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// <see cref="ISessionDriverFactory"/> that resolves a fresh driver per session from the container. It is
/// an orchestrator building a runtime-parameterized child (the driver chosen by the profile's provider),
/// which is the sanctioned use of <see cref="IServiceProvider"/> (Code.md §2) — both drivers are
/// transient, so each call yields a new instance for the new session.
/// </summary>
internal sealed class SessionDriverFactory(IServiceProvider services) : ISessionDriverFactory, ISingletonService
{
    public ISessionDriver Create(ClaudeProfile? profile) => profile?.Provider switch
    {
        SessionProvider.Ollama or SessionProvider.LmStudio => services.GetRequiredService<OpenAiCompatSessionDriver>(),
        _ => services.GetRequiredService<ClaudeCliSession>(),
    };
}
