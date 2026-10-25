using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Cockpit.App.Services;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.App.ViewModels;

// One consent request waiting on a session (#AC-47), bound to the inline banner in the pane. Renders the request
// as it is — the literal action verbatim, never a summary — and answers the broker when the operator chooses.
// Approve carries `Remember`, which the broker honours only for a low-risk, rememberable prompt.
public sealed partial class ConsentPromptViewModel : ViewModelBase
{
    private readonly IConsentPrompts _consent;

    public ConsentPromptViewModel(ConsentQuestion prompt, IConsentPrompts consent)
    {
        _consent = consent;
        Id = prompt.Id;
        Title = prompt.Request.Title;
        Action = prompt.Request.Action;
        SourceLabel = prompt.Request.Source.Label;
        CanRemember = prompt.CanRemember;
        IsDangerous = prompt.Request.Risk == ConsentRisk.Dangerous;
        Choices = [.. (prompt.Request.Choices ?? []).Select(choice => new ConsentChoiceViewModel(choice))];
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

    // AC-1714: the questions the operator answers by picking; empty for a plain Approve/Deny card.
    public IReadOnlyList<ConsentChoiceViewModel> Choices { get; }

    public bool HasChoices => Choices.Count > 0;

    [RelayCommand]
    private void Approve()
    {
        if (HasChoices)
        {
            _consent.Respond(Id, ConsentOutcome.Approved, Remember, Choices.ToDictionary(choice => choice.Key, choice => choice.Picked()));
            return;
        }

        _consent.Respond(Id, ConsentOutcome.Approved, Remember);
    }

    [RelayCommand]
    private void Deny() => _consent.Respond(Id, ConsentOutcome.Denied, remember: false);
}

// One question on a card with choices (AC-1714): a list to pick one from, or checkboxes when more than one may be picked.
public sealed partial class ConsentChoiceViewModel : ViewModelBase
{
    public ConsentChoiceViewModel(ConsentChoice choice)
    {
        Key = choice.Key;
        Label = choice.Label;
        AllowMultiple = choice.AllowMultiple;
        Options = [.. choice.Options.Select(option => new ConsentOptionViewModel(option))];
        _selectedOption = Options.FirstOrDefault(option => option.IsPicked && option.Enabled);
    }

    public string Key { get; }

    public string Label { get; }

    public bool AllowMultiple { get; }

    public bool IsSingle => !AllowMultiple;

    public IReadOnlyList<ConsentOptionViewModel> Options { get; }

    // The single-pick list's selection; the checkboxes of a multiple choice use each option's IsPicked instead.
    [ObservableProperty]
    private ConsentOptionViewModel? _selectedOption;

    public IReadOnlyList<string> Picked() => AllowMultiple
        ? [.. Options.Where(option => option.IsPicked).Select(option => option.Value)]
        : SelectedOption is { } selected ? [selected.Value] : [];
}

public sealed partial class ConsentOptionViewModel(ConsentOption option) : ViewModelBase
{
    public string Value { get; } = option.Value;

    public string Label { get; } = option.Label;

    public string? Detail { get; } = option.Detail;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool Enabled { get; } = option.Enabled;

    [ObservableProperty]
    private bool _isPicked = option.Selected;
}
