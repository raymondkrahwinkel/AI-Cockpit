using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels.Onboarding;

// Whether `Cockpit.Core.HostExecutableProbe` found this provider's own CLI on PATH (AC-510[b]
// criterion 1) — never whether it works or is logged in, only the owning plugin can know that, and only once it
// is installed (see the probe's own remarks).
public enum ProviderDetectionState
{
    // Nothing local to look for — a cloud provider behind an API key entered after install (`ProviderHostExecutables` lists none for it).
    NotApplicable,

    // A file with the expected name is on PATH.
    Found,

    // Nothing with the expected name is on PATH.
    NotFound,
}

// Reuse StorePluginRowViewModel so onboarding and the plugin store compute provider compatibility identically
// (AC-510[b]).
public sealed partial class ProviderPickerRowViewModel : ObservableObject
{
    public ProviderPickerRowViewModel(StorePluginRowViewModel row, ProviderDetectionState detection)
    {
        Row = row;
        Detection = detection;

        // A suggestion, never a decision already made (AC-510[b] criterion 4): only a CLI provider this host actually
        // found, not incompatible, starts checked — Skip/Next must be free to leave it exactly that, a suggestion
        // nobody acted on.
        IsSelected = detection == ProviderDetectionState.Found && !row.IsIncompatible && !row.IsInstalled;
    }

    public StorePluginRowViewModel Row { get; }

    public ProviderDetectionState Detection { get; }

    [ObservableProperty]
    private bool _isSelected;

    // Whether the checkbox can be changed at all — nothing to opt into when this host cannot run the plugin regardless (AC-181); an already-installed row stays selectable, since installing it anyway is exactly how the "already installed" outcome (criterion 2) is reached honestly rather than staged for a screenshot.
    public bool CanSelect => !Row.IsIncompatible;

    public string DetectionLabel => Detection switch
    {
        ProviderDetectionState.Found => "Found on this machine",
        ProviderDetectionState.NotFound => "Not found on this machine",
        _ => "Cloud provider — no local install to find",
    };

    // Theme brush key for the detection pill — green once found, faint otherwise. Never implies "works" (criterion 1), only "found".
    public string DetectionBrushKey => Detection == ProviderDetectionState.Found ? "CockpitStatusDoneBrush" : "CockpitTextFaintBrush";

    public bool ShowDetectionPill => Detection != ProviderDetectionState.NotApplicable;

    // Set once the batch install's result for this row comes back — null before then, so the view shows nothing rather than a stale line.
    [ObservableProperty]
    private string? _outcomeText;

    [ObservableProperty]
    private string _outcomeBrushKey = "CockpitTextSecondaryBrush";

    public bool HasOutcome => !string.IsNullOrEmpty(OutcomeText);

    partial void OnOutcomeTextChanged(string? value) => OnPropertyChanged(nameof(HasOutcome));

    // Applies one provisioning outcome to this row (AC-510[b] criterion 2) — the provisioning seam's own four shapes
    // (`PluginProvisionOutcome`), translated to a line the operator reads instead of a raw result object.
    public void ApplyOutcome(PluginProvisionResult result)
    {
        (OutcomeText, OutcomeBrushKey) = result.Outcome switch
        {
            PluginProvisionOutcome.Installed =>
                ("Installed — open the plugin store afterwards to approve it. It does nothing until you do.", "CockpitStatusDoneBrush"),
            PluginProvisionOutcome.Staged =>
                ("Already installed — the new bytes take effect after you restart the cockpit.", "CockpitStatusWaitingBrush"),
            PluginProvisionOutcome.Incompatible =>
                ($"Can't install: {result.Error}", "CockpitStatusErrorBrush"),
            _ => ($"Couldn't install: {result.Error ?? "unknown error"}", "CockpitStatusErrorBrush"),
        };
    }
}
