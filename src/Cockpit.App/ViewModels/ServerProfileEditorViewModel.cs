using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

// AC-1473: the per-field editor of a server profile, shared by Options › Profiles and, since AC-1475, › Assistant. It
// sends what differs from what the server sent and nothing else: what it read holds no secret, so writing it back
// would wipe those on the server.
public sealed partial class ServerProfileEditorViewModel : ObservableObject
{
    private RemoteProfile? _original;
    private bool _variablesTouched;

    public ObservableCollection<ServerProfileVariableViewModel> Variables { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelChanged))]
    private string _model = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPermissionModeChanged))]
    private string _permissionMode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMcpServersChanged))]
    private string _mcpServers = "";

    public bool HasVariables => Variables.Count > 0;

    public bool IsModelChanged => _original is not null && _Changed(Model, _original.Model) is not null;

    public bool IsPermissionModeChanged => _original is not null && _Changed(PermissionMode, _original.PermissionMode) is not null;

    public bool IsMcpServersChanged => _original is not null && McpServers.Trim() != _McpText(_original.McpServers);

    public void Fill(RemoteProfile? profile)
    {
        _original = profile;
        _variablesTouched = false;
        Model = profile?.Model ?? "";
        PermissionMode = profile?.PermissionMode ?? "";
        McpServers = _McpText(profile?.McpServers);
        Variables.Clear();
        foreach (var variable in profile?.Environment ?? [])
        {
            Variables.Add(new ServerProfileVariableViewModel(variable));
        }

        OnPropertyChanged(nameof(HasVariables));
        OnPropertyChanged(nameof(IsModelChanged));
        OnPropertyChanged(nameof(IsPermissionModeChanged));
        OnPropertyChanged(nameof(IsMcpServersChanged));
    }

    // Only what differs from what the server sent; a new profile names what was filled in. Null when nothing does.
    public RemoteProfilePatch? Patch()
    {
        var plain = Variables.Where(variable => !variable.IsSecret).ToList();
        var patch = new RemoteProfilePatch
        {
            Model = _Changed(Model, _original?.Model),
            PermissionMode = _Changed(PermissionMode, _original?.PermissionMode),
            McpServers = McpServers.Trim() == _McpText(_original?.McpServers) ? null : new RemoteMcpSelection(_McpNames(McpServers)),
            Environment = _variablesTouched || plain.Any(variable => variable.IsChanged) ? [.. plain.Select(variable => new RemoteProfileVariable(variable.Key.Trim(), variable.Value))] : null,
        };
        return patch == new RemoteProfilePatch() ? null : patch;
    }

    [RelayCommand]
    private void AddVariable()
    {
        Variables.Add(new ServerProfileVariableViewModel(new RemoteProfileVariable("", "")) { IsNew = true });
        _variablesTouched = true;
        OnPropertyChanged(nameof(HasVariables));
    }

    [RelayCommand]
    private void RemoveVariable(ServerProfileVariableViewModel variable)
    {
        if (variable.IsSecret)
        {
            return;
        }

        Variables.Remove(variable);
        _variablesTouched = true;
        OnPropertyChanged(nameof(HasVariables));
    }

    private static string? _Changed(string edited, string? original) =>
        edited.Trim() == (original ?? "") ? null : edited.Trim();

    // Blank is every enabled server, as a profile without a selection has it.
    private static IReadOnlyList<string>? _McpNames(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static string _McpText(IReadOnlyList<string>? names) => names is null ? "" : string.Join(", ", names);
}
