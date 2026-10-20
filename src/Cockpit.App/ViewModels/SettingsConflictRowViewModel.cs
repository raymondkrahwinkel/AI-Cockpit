using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Configuration;

namespace Cockpit.App.ViewModels;

// AC-1510: one setting in the conflict dialog, answered by picking this machine's value or the other one.
public sealed partial class SettingsConflictRowViewModel(SettingsConflict conflict) : ObservableObject
{
    [ObservableProperty]
    private bool _keepMine;

    [ObservableProperty]
    private bool _takeTheirs;

    public string Key => conflict.Key;

    public string Label => conflict.Key.StartsWith("Profiles.", StringComparison.Ordinal) ? $"Profile {conflict.Key["Profiles.".Length..]}"
        : conflict.Key.StartsWith("Projects.", StringComparison.Ordinal) ? $"Project {_ProjectName(conflict)}"
        : conflict.Key;

    public string Mine => _Shown(conflict.Mine);

    public string Theirs => _Shown(conflict.Theirs);

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
