using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One usage signal on a settings screen (AC-233): what the provider calls it, what it declared, and the number
/// the operator put in its place — or nothing, which means "follow the level above".
/// <para>
/// Rendered from the declaration rather than written per provider, so a provider that adds a signal appears here
/// without a line of host code. The host still knows nothing about what any of them mean.
/// </para>
/// </summary>
public sealed partial class UsageThresholdRowViewModel : ObservableObject
{
    public UsageThresholdRowViewModel(string signalKey, string label, string? description, double declared, double? current)
    {
        SignalKey = signalKey;
        Label = string.IsNullOrWhiteSpace(description) ? label : description;
        Declared = declared;
        _threshold = current;
    }

    /// <summary>Which signal this row sets, as the provider named it.</summary>
    public string SignalKey { get; }

    /// <summary>What the operator reads — the signal's description where it has one, else its short label.</summary>
    public string Label { get; }

    /// <summary>What the provider itself declared, shown as the placeholder so "following" is visible rather than implied.</summary>
    public double Declared { get; }

    /// <summary>The hint under the field, naming the value that applies when this is left empty.</summary>
    public string FollowsLabel => $"Follows the provider ({Declared:0}%)";

    /// <summary>
    /// The operator's own number, or null to follow the level above. Null is stored as an absence rather than a
    /// copy of the current value, so a later change to the provider's default still carries.
    /// </summary>
    [ObservableProperty]
    private double? _threshold;
}
