using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.App.Services;

// AC-1995: the slot the Assistant Profile dialog edits, when that slot is a server's. It reads the profile as the server
// hands it out, each secret masked, and writes it back whole; the server puts each masked secret back and restarts.
public sealed class ServerAssistantSlotStore(string server, IAssistantAdministration assistant) : IAssistantProfileStore
{
    // The page's own words for a key the server no longer takes for this, so the dialog and the page say the same.
    public static string RefusedDetail => "This key may have been revoked or lost its admin rights. Nothing was changed.";

    // What the server answered the last save with, so the page can show what it then holds.
    public RemoteAssistantSettings? Saved { get; private set; }

    // Whether the server refused the key on a save; the page then reads as read-only, as after any refusal.
    public bool IsRefused { get; private set; }

    public async Task<AssistantProfileSlot> LoadAsync(CancellationToken cancellationToken = default) =>
        _Slot(await assistant.GetAsync(cancellationToken));

    public async Task<AssistantProfileSlot> RepointAsync(SessionProfile record, bool replacesStandingInstruction, CancellationToken cancellationToken = default)
    {
        try
        {
            Saved = await assistant.SaveProfileAsync(record, replacesStandingInstruction, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            IsRefused = true;
            throw new UnauthorizedAccessException($"Refused by {server}. {RefusedDetail}", exception);
        }

        return _Slot(Saved);
    }

    // Emptying the slot is the server's own decision; nothing on this connection does it.
    public Task<AssistantProfileSlot> UnsetAsync(string reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A server's assistant slot is emptied on the server.");

    private static AssistantProfileSlot _Slot(RemoteAssistantSettings settings) =>
        new(settings.Profile?.Full, settings.UnsetReason, settings.ReplacesStandingInstruction);
}
