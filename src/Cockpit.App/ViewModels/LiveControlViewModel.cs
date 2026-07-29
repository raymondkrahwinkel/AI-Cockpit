using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One control in the session header's generic live-control panel (#45 D4) — a plugin provider's model or
/// reasoning effort, rendered from the provider's own <see cref="SessionLiveOption"/> so the header can offer
/// controls it has no built-in vocabulary for. Picking a value forwards to the running session's driver through
/// <see cref="_apply"/>, which applies it to the next turn.
/// </summary>
public partial class LiveControlViewModel : ViewModelBase
{
    private readonly Func<string, string, Task> _apply;

    /// <summary>The provider's key for this control, sent back to the driver on a switch.</summary>
    public string Key { get; }

    /// <summary>What the operator reads next to the dropdown (e.g. "Model", "Effort").</summary>
    public string Label { get; }

    private readonly IReadOnlyDictionary<string, string>? _choiceLabels;

    /// <summary>The values on offer.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _choices;

    /// <summary>The choices as label/value pairs for the combo, so a provider that supplied friendly labels shows them while <see cref="SelectedValue"/> still round-trips the raw value.</summary>
    [ObservableProperty]
    private IReadOnlyList<SelectableChoice> _choiceItems;

    [ObservableProperty]
    private string? _selectedValue;

    // Set while SeedIfUnset applies a driver-reported value through the generated SelectedValue setter, so
    // OnSelectedValueChanged can tell "the operator picked this" from "the driver told us what it already is" and
    // skip echoing the latter back as a live switch.
    private bool _seeding;

    public LiveControlViewModel(SessionLiveOption option, Func<string, string, Task> apply)
    {
        Key = option.Key;
        Label = option.Label;
        _choiceLabels = option.ChoiceLabels;
        _choices = option.Choices;
        _choiceItems = [.. option.Choices.Select(value => new SelectableChoice(value, _choiceLabels?.GetValueOrDefault(value) ?? value))];
        _apply = apply;

        // Seed through the field, not the property: setting the current value must not fire a live switch back to
        // the driver for the value it already reported.
        _selectedValue = option.CurrentValue;
    }

    partial void OnSelectedValueChanged(string? value)
    {
        if (!_seeding && !string.IsNullOrWhiteSpace(value))
        {
            _ = _apply(Key, value);
        }
    }

    /// <summary>
    /// Seeds this control with a value the driver reported after launch (AC-141 — the SDK route's <c>init</c>
    /// event names the model a session actually started under, which is unknown at construction time for a
    /// session launched with no explicit choice). Only fills in a still-unset value, and — like the constructor —
    /// never fires a live switch back at the driver for the value it just reported.
    /// </summary>
    /// <remarks>
    /// A resolved model can be a pinned snapshot the suggestion list never offered (the same case
    /// <c>ClaudeSdkSessionDriver._BuildLiveOptions</c> handles for an explicitly-chosen one) — inserted into
    /// <see cref="Choices"/>/<see cref="ChoiceItems"/> too, or the combo would have a selected value with no
    /// matching item to show it against.
    /// </remarks>
    public void SeedIfUnset(string value)
    {
        if (string.IsNullOrEmpty(SelectedValue) && !string.IsNullOrEmpty(value))
        {
            if (!Choices.Contains(value))
            {
                Choices = [value, .. Choices];
                ChoiceItems = [new SelectableChoice(value, _choiceLabels?.GetValueOrDefault(value) ?? value), .. ChoiceItems];
            }

            _seeding = true;
            SelectedValue = value;
            _seeding = false;
        }
    }
}
