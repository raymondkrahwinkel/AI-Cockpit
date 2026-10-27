using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Configuration;

namespace Cockpit.App.ViewModels;

// AC-1510: one setting in the conflict dialog, answered by picking this machine's value or the other one.
public sealed partial class SettingsConflictRowViewModel : ObservableObject
{
    private readonly SettingsConflict _conflict;

    public SettingsConflictRowViewModel(SettingsConflict conflict)
    {
        _conflict = conflict;
        Carriers = [.. (conflict.CarrierValues ?? new Dictionary<string, string?> { ["Other machine"] = conflict.Theirs })
            .Select(pair => new SettingsConflictCarrierValueViewModel(pair.Key, _Shown(pair.Value), () => _SelectCarrier(pair.Key)))];
    }

    [ObservableProperty]
    private bool _keepMine;

    [ObservableProperty]
    private bool _takeTheirs;

    [ObservableProperty]
    private string? _selectedCarrier;

    public IReadOnlyList<SettingsConflictCarrierValueViewModel> Carriers { get; }

    public string Key => _conflict.Key;

    public string Label => _conflict.Key.StartsWith("Profiles.", StringComparison.Ordinal) ? $"Profile {_conflict.Key["Profiles.".Length..]}"
        : _conflict.Key.StartsWith("Projects.", StringComparison.Ordinal) ? $"Project {_ProjectName(_conflict)}"
        : _conflict.Key;

    public string Mine => _Shown(_conflict.Mine);

    public string Theirs => _Shown(_conflict.Theirs);

    public bool HasChoice => KeepMine || SelectedCarrier is not null;

    partial void OnKeepMineChanged(bool value)
    {
        if (value)
        {
            SelectedCarrier = null;
            foreach (var carrier in Carriers) carrier.IsSelected = false;
        }
    }

    partial void OnTakeTheirsChanged(bool value)
    {
        if (value && Carriers.FirstOrDefault() is { } carrier)
        {
            _SelectCarrier(carrier.Id);
        }
    }

    private void _SelectCarrier(string id)
    {
        KeepMine = false;
        TakeTheirs = false;
        SelectedCarrier = id;
        foreach (var carrier in Carriers) carrier.IsSelected = carrier.Id == id;
    }

    // A profile or project is named rather than dumped; a removed one says so.
    private static string _Shown(string? json) => JsonNode.Parse(json ?? "null") switch
    {
        null => "removed",
        JsonObject => "changed",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        var node => node.ToJsonString(),
    };

    private static string _ProjectName(SettingsConflict conflict) =>
        (JsonNode.Parse(conflict.Mine ?? conflict.Theirs ?? "null") as JsonObject)?["Name"] is JsonValue name && name.TryGetValue<string>(out var text)
            ? text
            : conflict.Key["Projects.".Length..];
}

public sealed partial class SettingsConflictCarrierValueViewModel(string id, string value, Action choose) : ObservableObject
{
    public string Id => id;

    public string Value => value;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Choose() => choose();
}
