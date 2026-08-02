using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Assistant;

namespace Cockpit.App.ViewModels;

// Drives the assistant indicator (AC-543): the chip that shows who is listening, what the assistant is doing,
// and the listening mode — reusable by construction (criterion 21, AC-238 puts the same component in the
// companion window). It does *not* bind to `Services.AssistantSessionHost` or
// `CockpitViewModel` directly; whoever hosts it feeds `Activity`, `UnavailableReason`
// and `ListeningMode` in and listens to `Clicked` / `ListeningModeSelected` to
// act on them. A direct binding would have tied this file to one host and made the companion-window reuse a
// second, drifting copy instead of the same control.
public partial class AssistantIndicatorViewModel : ViewModelBase
{
    // What the indicator reports — see `AssistantActivity` for the eight states.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(KeyHint))]
    [NotifyPropertyChangedFor(nameof(ColorClass))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsListening))]
    [NotifyPropertyChangedFor(nameof(IsListeningContinuously))]
    [NotifyPropertyChangedFor(nameof(IsThinking))]
    [NotifyPropertyChangedFor(nameof(IsSpeaking))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingOperator))]
    [NotifyPropertyChangedFor(nameof(IsDictating))]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    [NotifyPropertyChangedFor(nameof(IsMicIcon))]
    private AssistantActivity _activity = AssistantActivity.Unavailable;

    // Whether the assistant feature is switched on at all. `false` draws nothing: off means "no
    // chip" (AC-542's own wording for what off means), not a chip that reports being off.
    // Distinct from `AssistantActivity.Unavailable`, which is for an assistant that is switched
    // *on* and still cannot be reached — no profile set, or a start that failed. That one has something
    // worth saying and a reason to say it with; a feature the operator has deliberately turned off has neither,
    // and a permanent "unavailable" chip for it would be a standing complaint about a choice they made.
    //
    // Defaults to `true` so the component renders on its own — in a scene, a test, or AC-238's
    // companion window — without every consumer having to remember to switch it on.
    [ObservableProperty]
    private bool _isFeatureEnabled = true;

    // Why the assistant cannot be reached, set alongside `AssistantActivity.Unavailable`. Shown as the
    // chip's secondary line — an unavailable chip with no reason sends the operator into Options looking for a
    // setting that is not the problem (criterion 6/the ticket's own point 4).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _unavailableReason;

    // The provider/model the assistant runs on (e.g. what `ProfileDisplay` formats an
    // Assistant Profile as), shown as the chip's secondary line while `AssistantActivity.Ready` — the
    // one state whose second line is not fixed text, because it is the answer to "which model am I about to
    // talk to". Null (no subtitle) until the host has actually read the profile.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _profileLabel;

    // How much the assistant listens (criteria 17–19): off or always on. Fed in by the host — which derives it
    // from `VoiceSettings.OpenMicEnabled` rather than storing a second flag with the same meaning — so the
    // picker below always shows the mode actually in effect, never a locally-guessed one. This view model carries
    // no opinion on where the mode is persisted; it only displays and proposes it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListeningModeOff))]
    [NotifyPropertyChangedFor(nameof(IsListeningModeAlwaysOn))]
    private AssistantListeningMode _listeningMode = AssistantListeningMode.Off;

    // Whether the operator has already been told what `AssistantListeningMode.AlwaysOn` means and
    // costs — mirrors `AssistantSettings.AlwaysOnCostAcknowledged`. Fed in rather than owned here: the
    // setting is persisted by the host, and this view model only needs to know it to decide whether picking
    // AlwaysOn should ask first (criterion 18).
    [ObservableProperty]
    private bool _alwaysOnCostAcknowledged;

    // True while the one-time "this costs per utterance" explanation is showing, after the operator picked
    // `AssistantListeningMode.AlwaysOn` for the first time. Local UI state rather than something the
    // host tracks: it exists only for the moment between the pick and the operator's answer, and closes itself
    // (`CancelAlwaysOnConfirmation`) without needing a round trip through settings.
    [ObservableProperty]
    private bool _isAlwaysOnConfirmationPending;

    // The rail stand (criterion 6/19): sidebar collapsed to its icon-only strip. The chip keeps its colour and
    // activity ring but drops the label — there is no room for prose in a rail, and the colour alone already
    // answers "is something listening", which is the one question a glance at a collapsed sidebar has to answer.
    [ObservableProperty]
    private bool _isCollapsed;

    // Whether the operator has switched the consent bypass on for at least one source (#AC-575). Drawn as a
    // standing mark on the chip, in both the expanded and the collapsed stand.
    // It lives here rather than only in Options because it is the one setting in this feature that removes a
    // confirmation the operator would otherwise get, and criterion 5 asks that it be visible without opening
    // Options — the chip is the only surface that is always on screen while the assistant is on. Which sources,
    // and at what risk, is Options' question; this only answers "is anything being skipped right now".
    [ObservableProperty]
    private bool _isConsentBypassActive;

    // Raised when the chip (expanded or rail) is clicked — opens the chat window, or starts the assistant lazily if it has not run yet. The host decides what that means; this view model only reports the click.
    public event EventHandler? Clicked;

    // Raised once a listening-mode pick is final — after `AssistantListeningMode.AlwaysOn`'s one-time
    // confirmation, if it was needed. Where that lands is the host's decision, not this view model's — today
    // that means flipping `VoiceSettings.OpenMicEnabled`, since the mode itself is not stored anywhere; this
    // view model only lives on the surface the operator picks from.
    public event EventHandler<AssistantListeningMode>? ListeningModeSelected;

    // The primary line — what to call this state out loud. Distinct per state (criterion 6): "Dictating" reads nothing like "Listening" even before the colour is considered.
    public string Label => Activity switch
    {
        AssistantActivity.Ready => "Assistant",
        AssistantActivity.Listening => "Listening",
        AssistantActivity.ListeningContinuously => "Listening continuously",
        AssistantActivity.Thinking => "Thinking…",
        AssistantActivity.Speaking => "Speaking",
        // Phrased as the thing the operator has to do, not as the assistant's condition. "Awaiting operator" is
        // what the code calls it; on a chip in the corner of the screen, "needs you" is what makes someone look.
        AssistantActivity.AwaitingOperator => "Needs you",
        AssistantActivity.Dictating => "Dictating",
        AssistantActivity.Unavailable => "Assistant unavailable",
        _ => "Assistant",
    };

    // The secondary line, or null when there is nothing to show yet. `AssistantActivity.Ready` gets
    // `ProfileLabel` (which model it would talk to); every other assistant-side state names the
    // assistant, so the chip still reads "the assistant" once the model name has scrolled out of view.
    // `AssistantActivity.Dictating` spells out *who* is not listening (criterion 6 — the
    // question this indicator answers is "who is listening", not "is something listening") and
    // `AssistantActivity.Unavailable` carries `UnavailableReason`.
    public string? Detail => Activity switch
    {
        AssistantActivity.Ready => ProfileLabel,
        AssistantActivity.Listening => "Assistant",
        AssistantActivity.ListeningContinuously => "Assistant",
        AssistantActivity.Thinking => "Assistant",
        AssistantActivity.Speaking => "Assistant",
        AssistantActivity.AwaitingOperator => "Open the chat to answer",
        AssistantActivity.Dictating => "not the assistant",
        AssistantActivity.Unavailable => UnavailableReason,
        _ => null,
    };

    // The key hint shown at the right of the chip (mockup's `.key` badge) — only for the states that
    // actually have a key bound to them right now; `null` hides it rather than showing an empty
    // badge. `AssistantActivity.ListeningContinuously` gets none: it is a standing mode switched on
    // from the picker below, not a key held down, so there is no key to name.
    //
    // The key alone, not the mockup's "release F10" / "Esc to stop". Those were written for a 340px chip; in the
    // sidebar's ~164px the phrase took the width the label needed, and the label is what criterion 6 is about.
    // The verb was never carrying much anyway — a badge on a chip that is visibly listening reads as "this is
    // the key doing it" either way.
    public string? KeyHint => Activity switch
    {
        AssistantActivity.Ready => "F10",
        AssistantActivity.Listening => "F10",
        AssistantActivity.Speaking => "Esc",
        AssistantActivity.Dictating => "F9",
        _ => null,
    };

    // The class name the view applies to the chip/rail so `Theme.axaml` paints its border and ring per
    // state — one string rather than the view switching on `Activity` itself, so a test can assert
    // "every state has its own colour" against this alone instead of rendering the control.
    public string ColorClass => Activity switch
    {
        AssistantActivity.Ready => "ready",
        AssistantActivity.Listening => "listening",
        AssistantActivity.ListeningContinuously => "listeningContinuously",
        AssistantActivity.Thinking => "thinking",
        AssistantActivity.Speaking => "speaking",
        AssistantActivity.AwaitingOperator => "awaitingOperator",
        AssistantActivity.Dictating => "dictating",
        AssistantActivity.Unavailable => "unavailable",
        _ => "unavailable",
    };

    public bool IsReady => Activity == AssistantActivity.Ready;
    public bool IsListening => Activity == AssistantActivity.Listening;
    public bool IsListeningContinuously => Activity == AssistantActivity.ListeningContinuously;
    public bool IsThinking => Activity == AssistantActivity.Thinking;
    public bool IsSpeaking => Activity == AssistantActivity.Speaking;
    public bool IsAwaitingOperator => Activity == AssistantActivity.AwaitingOperator;
    public bool IsDictating => Activity == AssistantActivity.Dictating;
    public bool IsUnavailable => Activity == AssistantActivity.Unavailable;

    // Whether the mic glyph is the badge's icon — every state except Thinking, Speaking and Unavailable, which draw their own (a thought bubble, a speaker, an alert triangle).
    public bool IsMicIcon => Activity is AssistantActivity.Ready or AssistantActivity.Listening
        or AssistantActivity.ListeningContinuously or AssistantActivity.Dictating;

    public bool IsListeningModeOff => ListeningMode == AssistantListeningMode.Off;
    public bool IsListeningModeAlwaysOn => ListeningMode == AssistantListeningMode.AlwaysOn;

    [RelayCommand]
    private void Click() => Clicked?.Invoke(this, EventArgs.Empty);

    // Picks `AssistantListeningMode.Off` — always allowed, never asks for confirmation.
    [RelayCommand]
    private void SelectListeningModeOff()
    {
        IsAlwaysOnConfirmationPending = false;
        ListeningModeSelected?.Invoke(this, AssistantListeningMode.Off);
    }

    // Picks `AssistantListeningMode.AlwaysOn`. The first time, this only opens the inline
    // confirmation (criterion 18) instead of committing — `ConfirmAlwaysOn` is what actually raises
    // `ListeningModeSelected` then. Once `AlwaysOnCostAcknowledged` is true the
    // explanation has already been given and does not return: a warning that reappears every time trains the
    // operator to click through it unread, which is the failure criterion 18 exists to rule out.
    [RelayCommand]
    private void SelectListeningModeAlwaysOn()
    {
        if (AlwaysOnCostAcknowledged)
        {
            ListeningModeSelected?.Invoke(this, AssistantListeningMode.AlwaysOn);
            return;
        }

        IsAlwaysOnConfirmationPending = true;
    }

    // Answers the inline confirmation: acknowledges the cost and commits the pick, in that order — so a host reading the event already sees `AlwaysOnCostAcknowledged` as true and never re-asks.
    [RelayCommand]
    private void ConfirmAlwaysOn()
    {
        AlwaysOnCostAcknowledged = true;
        IsAlwaysOnConfirmationPending = false;
        ListeningModeSelected?.Invoke(this, AssistantListeningMode.AlwaysOn);
    }

    // Dismisses the inline confirmation without picking AlwaysOn — the listening mode stays whatever it already was.
    [RelayCommand]
    private void CancelAlwaysOnConfirmation() => IsAlwaysOnConfirmationPending = false;
}
