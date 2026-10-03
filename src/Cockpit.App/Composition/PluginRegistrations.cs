using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Composition;

// AC-1441: the in-proc plugin registries, read straight through; one that is not there registered nothing.
internal sealed class PluginRegistrations(
    IPluginProviderRegistry sessionProviders,
    IPluginTtyProviderRegistry? ttyProviders = null,
    IConversationPickerRegistry? conversationPickers = null,
    IProjectMemorySourceRegistry? memorySources = null,
    IProjectFieldRegistry? projectFields = null,
    IProjectOwnershipRegistry? projectOwnership = null,
    ISharedProjectSourceRegistry? sharedProjectSources = null) : IPluginRegistrations, ISingletonService
{
    public IReadOnlyList<SessionProviderRegistration> SessionProviders => sessionProviders.Registrations;

    public SessionProviderRegistration? SessionProvider(string providerId) => sessionProviders.Resolve(providerId);

    public TtyProviderRegistration? TtyProvider(string providerId) => ttyProviders?.Resolve(providerId);

    public IReadOnlyList<ConversationPickerRegistration> ConversationPickers => conversationPickers?.Pickers ?? [];

    public IReadOnlyList<ProjectMemorySource> MemorySources => memorySources?.Sources.ToMemorySources() ?? [];

    public IReadOnlyList<ProjectMemorySourceRegistration> MemorySourceRegistrations => memorySources?.Sources ?? [];

    public IReadOnlyList<ProjectMemorySourceFamily> MemorySourceFamilies => memorySources?.Families ?? [];

    public IReadOnlyList<ProjectFieldRegistration> ProjectFields => projectFields?.Fields ?? [];

    public IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? FieldOwnership(string projectId) => projectOwnership?.Resolve(projectId);

    public IReadOnlyList<ISharedProjectSource> SharedProjectSources => sharedProjectSources?.Sources ?? [];
}
