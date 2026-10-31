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
        var values = conflict.CarrierValues ?? new Dictionary<string, string?> { ["Other machine"] = conflict.Theirs };
        Carriers = [.. values.Select(pair => new SettingsConflictCarrierValueViewModel(
            pair.Key,
            _Shown(pair.Value),
            values.Count > 1 ? $"{_Names(values.Keys.Where(id => id != pair.Key))} will be updated to this value." : string.Empty,
            () => _SelectCarrier(pair.Key)))];
        Heading = $"{_Names(values.Keys)} {(values.Count > 1 ? "disagree" : "disagrees")} with this cockpit about “{Label}”.";
        KeepMineText = $"Keep this cockpit's value ({Mine}) and write it to {(values.Count > 1 ? "all" : "the server")}";
    }

    [ObservableProperty]
    private bool _keepMine;

    [ObservableProperty]
    private bool _takeTheirs;

    [ObservableProperty]
    private string? _selectedCarrier;

    public IReadOnlyList<SettingsConflictCarrierValueViewModel> Carriers { get; }

    public string Key => _conflict.Key;

    // What the mockup's warning line says: who disagrees about which setting, and that nothing is overwritten meanwhile.
    public string Heading { get; }

    public string KeepMineText { get; }

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
            foreach (var carrier in Carriers)
            {
                carrier.IsSelected = false;
            }
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
        foreach (var carrier in Carriers)
        {
            carrier.IsSelected = carrier.Id == id;
        }
    }

    private static string _Names(IEnumerable<string> ids) => string.Join(" and ", ids);

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

public sealed partial class SettingsConflictCarrierValueViewModel(string id, string value, string hint, Action choose) : ObservableObject
{
    public string Id => id;

    public string Value => value;

    public string Title => $"Use {id}";

    public string Hint => hint;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Choose() => choose();
}
