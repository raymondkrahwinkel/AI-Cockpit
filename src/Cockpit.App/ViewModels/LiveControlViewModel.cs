using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// One control in the session header's generic live-control panel (#45 D4) — a plugin provider's model or reasoning
// effort, rendered from the provider's own `SessionLiveOption` so the header can offer controls it has no built-in
// vocabulary for.
public partial class LiveControlViewModel : ViewModelBase
{
    private readonly Func<string, string, Task> _apply;

    // The provider's key for this control, sent back to the driver on a switch.
    public string Key { get; }

    // What the operator reads next to the dropdown (e.g. "Model", "Effort").
    public string Label { get; }

    private readonly IReadOnlyDictionary<string, string>? _choiceLabels;

    // The values on offer.
    [ObservableProperty]
    private IReadOnlyList<string> _choices;

    // The choices as label/value pairs for the combo, so a provider that supplied friendly labels shows them while
    // `SelectedValue` still round-trips the raw value.
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

    // Only fills in a still-unset value, and — like the constructor — never fires a live switch back at the driver for
    // the value it just reported (AC-141).
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
