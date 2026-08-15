using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One usage signal on a settings screen (AC-233): what the provider calls it, what it declared, and the number
// the operator put in its place — or nothing, which means "follow the level above".
//
// Rendered from the declaration rather than written per provider, so a provider that adds a signal appears here
// without a line of host code. The host still knows nothing about what any of them mean.
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

    // What applies if this field is left empty — the provider's own declaration for a provider row, or (AC-805)
    // whatever an ordinary session on that provider would already resolve to for an Assistant row, since a
    // provider override made on the same screen changes that too. Shown as the placeholder so "following" is
    // visible rather than implied.
    public double Declared { get; }

    // The hint under the field, naming the value that applies when this is left empty.
    public string FollowsLabel => $"Follows the provider ({Declared:0}%)";

    // The operator's own number, or null to follow the level above. Null is stored as an absence rather than a
    // copy of the current value, so a later change to the provider's default still carries.
    [ObservableProperty]
    private double? _threshold;
}
