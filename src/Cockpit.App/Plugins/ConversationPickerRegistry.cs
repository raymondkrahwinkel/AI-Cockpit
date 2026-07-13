using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the conversation pickers plugins register (<c>ICockpitHost.AddConversationPicker</c>), so the
/// New-session dialog can offer to search for a conversation instead of asking for an id typed by hand. A
/// registry of its own rather than another collection on the cockpit view model: the dialog service would then
/// have to depend on the view model that depends on the dialog service.
/// </summary>
public interface IConversationPickerRegistry
{
    void Register(ConversationPickerRegistration picker);

    /// <summary>Every picker registered so far, in registration order. Empty is the normal case — no plugin that browses a provider's history is installed.</summary>
    IReadOnlyList<ConversationPickerRegistration> Pickers { get; }
}

internal sealed class ConversationPickerRegistry : IConversationPickerRegistry, ISingletonService
{
    private readonly List<ConversationPickerRegistration> _pickers = [];

    public IReadOnlyList<ConversationPickerRegistration> Pickers => _pickers;

    public void Register(ConversationPickerRegistration picker) => _pickers.Add(picker);
}
