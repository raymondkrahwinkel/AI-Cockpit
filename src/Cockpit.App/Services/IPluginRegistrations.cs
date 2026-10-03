using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// What the loaded plugins registered that the desktop's dialogs and panes offer (AC-1441).
/// Local to the desktop: a remote frontend needs it over the line first (AC-1388).
/// </summary>
public interface IPluginRegistrations
{
    /// <summary>
    /// Every session provider a plugin registered.
    /// </summary>
    IReadOnlyList<SessionProviderRegistration> SessionProviders { get; }

    /// <summary>
    /// The session provider registered as <paramref name="providerId"/>, or null.
    /// </summary>
    SessionProviderRegistration? SessionProvider(string providerId);

    /// <summary>
    /// The terminal provider registered as <paramref name="providerId"/>, or null.
    /// </summary>
    TtyProviderRegistration? TtyProvider(string providerId);

    /// <summary>
    /// The conversation pickers plugins registered, in registration order.
    /// </summary>
    IReadOnlyList<ConversationPickerRegistration> ConversationPickers { get; }

    /// <summary>
    /// The project memory sources plugins registered, as a session start reads them.
    /// </summary>
    IReadOnlyList<ProjectMemorySource> MemorySources { get; }

    /// <summary>
    /// The project memory sources plugins registered, as the project dialog lists them.
    /// </summary>
    IReadOnlyList<ProjectMemorySourceRegistration> MemorySourceRegistrations { get; }

    /// <summary>
    /// The families the project memory sources are grouped in.
    /// </summary>
    IReadOnlyList<ProjectMemorySourceFamily> MemorySourceFamilies { get; }

    /// <summary>
    /// The extra project fields plugins registered.
    /// </summary>
    IReadOnlyList<ProjectFieldRegistration> ProjectFields { get; }

    /// <summary>
    /// Which plugin owns which host field of <paramref name="projectId"/>, or null when none claims it.
    /// </summary>
    IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? FieldOwnership(string projectId);

    /// <summary>
    /// The shared-project sources plugins registered.
    /// </summary>
    IReadOnlyList<ISharedProjectSource> SharedProjectSources { get; }
}
