using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// AC-233 provider-declared usage signal: null follows its higher-level default without host-specific knowledge.
public sealed partial class UsageThresholdRowViewModel : ObservableObject
{
    public UsageThresholdRowViewModel(string signalKey, string label, string? description, double declared, double? current)
    {
        SignalKey = signalKey;
        Label = string.IsNullOrWhiteSpace(description) ? label : description;
        Declared = declared;
        _threshold = current;
    }

    // Which signal this row sets, as the provider named it.
    public string SignalKey { get; }

    // What the operator reads — the signal's description where it has one, else its short label.
    public string Label { get; }

    // AC-805 placeholder shows the provider or Assistant default, making a null override visibly inherit it.
    public double Declared { get; }

    // The hint under the field, naming the value that applies when this is left empty.
    public string FollowsLabel => $"Follows the provider ({Declared:0}%)";

    // The operator's own number, or null to follow the level above. Null is stored as an absence rather than a
    // copy of the current value, so a later change to the provider's default still carries.
    [ObservableProperty]
    private double? _threshold;
}
