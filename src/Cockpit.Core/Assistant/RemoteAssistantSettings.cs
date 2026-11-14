using Cockpit.Core.Profiles;

namespace Cockpit.Core.Assistant;

// AC-1475: a server's assistant as an admin key reads it. `UnavailableReason` is the host's own wording, `IsStoodDown`
// whether a key holds the line; the profile is AC-1473's wire form, and `Instructions` holds the brain.
public sealed record RemoteAssistantSettings(
    bool IsEnabled,
    bool IsAvailable,
    string? UnavailableReason,
    bool IsStoodDown,
    RemoteProfile? Profile,
    string? UnsetReason,
    string? Instructions,
    bool ReplacesStandingInstruction,
    bool ProviderInstalled,
    RemoteConsentBypass ConsentBypass,
    string? AssistantRole = null);

// AC-1475: the administration summary; editable consent preferences use the typed Options contract.
public sealed record RemoteConsentBypass(bool All, IReadOnlyList<string> Sources);

// AC-1475: what one change to the slot names; a field left null stays as the server has it.
public sealed record RemoteAssistantProfilePatch
{
    public string? CopyFromLabel { get; init; }

    public string? Label { get; init; }

    public string? AssistantRole { get; init; }

    public string? Instructions { get; init; }

    public bool? ReplacesStandingInstruction { get; init; }

    public RemoteProfilePatch? Profile { get; init; }
}
