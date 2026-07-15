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

    /// <summary>The values on offer.</summary>
    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    private string? _selectedValue;

    public LiveControlViewModel(SessionLiveOption option, Func<string, string, Task> apply)
    {
        Key = option.Key;
        Label = option.Label;
        Choices = option.Choices;
        _apply = apply;

        // Seed through the field, not the property: setting the current value must not fire a live switch back to
        // the driver for the value it already reported.
        _selectedValue = option.CurrentValue;
    }

    partial void OnSelectedValueChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _ = _apply(Key, value);
        }
    }
}
