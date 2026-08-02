using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.App.ViewModels;

// One consent request waiting on a session (#AC-47), bound to the inline banner in the pane. Renders the request
// as it is — the literal action verbatim, never a summary — and answers the broker when the operator chooses.
// Approve carries `Remember`, which the broker honours only for a low-risk, rememberable prompt.
public sealed partial class ConsentPromptViewModel : ViewModelBase
{
    private readonly IConsentBroker _broker;

    public ConsentPromptViewModel(ConsentPrompt prompt, IConsentBroker broker)
    {
        _broker = broker;
        Id = prompt.Id;
        Title = prompt.Request.Title;
        Action = prompt.Request.Action;
        SourceLabel = prompt.Request.Source.Label;
        CanRemember = prompt.CanRemember;
        IsDangerous = prompt.Request.Risk == ConsentRisk.Dangerous;
    }

    // Matches the broker's prompt, so the cockpit can clear this banner when the prompt is resolved elsewhere.
    public Guid Id { get; }

    public string Title { get; }

    // The literal action, shown verbatim in a read-only monospace block — the ground truth (see `ConsentRequest.Action`).
    public string Action { get; }

    public string SourceLabel { get; }

    // Whether to offer the "remember for this session" checkbox — true only for a rememberable low-risk prompt.
    public bool CanRemember { get; }

    // Whether this is a dangerous action — drives the amber (vs accent) edge and the warning glyph.
    public bool IsDangerous { get; }

    // Theme brush key for the banner's left edge (resolved by `StatusBrushConverter`): amber for dangerous, accent for low-risk.
    public string EdgeBrushKey => IsDangerous ? "CockpitStatusWaitingBrush" : "CockpitAccentBrush";

    // Icon next to the title, mirroring the sidebar's status markers.
    public MaterialIconKind Glyph => IsDangerous ? MaterialIconKind.AlertOutline : MaterialIconKind.RhombusOutline;

    // Two-way for the "remember for this session" checkbox; only shown/honoured when `CanRemember`.
    [ObservableProperty]
    private bool _remember;

    [RelayCommand]
    private void Approve() => _broker.Respond(Id, ConsentOutcome.Approved, Remember);

    [RelayCommand]
    private void Deny() => _broker.Respond(Id, ConsentOutcome.Denied, remember: false);
}
