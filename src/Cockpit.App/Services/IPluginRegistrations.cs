using Cockpit.Core.Projects;
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
}
