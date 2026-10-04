using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

// AC-1473 (F5.6b3): Options › Profiles on a server. A save sends the fields the editor changed, never the profile or
// the list read back: neither holds the server's secrets, so writing them back would wipe those.
public sealed partial class ServerProfilesViewModel(IServerProfiles profiles) : ObservableObject
{
    private ServerProfileRowViewModel? _editing;

    public ObservableCollection<ServerProfileRowViewModel> Rows { get; } = [];

    public ObservableCollection<ServerProfileVariableViewModel> EditorVariables { get; } = [];

    public ObservableCollection<string> EditorProviders { get; } = [];

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorSaveLabel))]
    private bool _isEditorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorSaveLabel))]
    private bool _isCreating;

    [ObservableProperty]
    private string _editorLabel = "";

    [ObservableProperty]
    private string? _editorProvider;

    [ObservableProperty]
    private string _editorModel = "";

    [ObservableProperty]
    private string _editorPermissionMode = "";

    [ObservableProperty]
    private string _editorMcpServers = "";

    public string EditorTitle => IsCreating ? "New profile" : $"Edit “{_editing?.Label}”";

    public string EditorSaveLabel => IsCreating ? "Create" : "Save";

    public bool HasEditorVariables => EditorVariables.Count > 0;

    [RelayCommand]
    public Task LoadAsync() => _RunAsync(async () =>
    {
        var listed = await profiles.ListAsync();
        Rows.Clear();
        foreach (var profile in listed)
        {
            Rows.Add(new ServerProfileRowViewModel(profile));
        }
    });

    [RelayCommand]
    private void StartNew()
    {
        _editing = null;
        IsCreating = true;
        EditorProviders.Clear();
        foreach (var provider in Rows.Where(row => row.Profile.ConfiguredOnServer).Select(row => row.Profile.Provider).Distinct(StringComparer.Ordinal))
        {
            EditorProviders.Add(provider);
        }

        EditorLabel = "";
        EditorProvider = EditorProviders.FirstOrDefault();
        _Fill(null);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void StartEdit(ServerProfileRowViewModel row)
    {
        _editing = row;
        IsCreating = false;
        EditorLabel = row.Label;
        _Fill(row.Profile);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    [RelayCommand]
    private Task SaveEditorAsync() => _RunAsync(async () =>
    {
        if (IsCreating)
        {
            var made = await profiles.CreateAsync(new RemoteNewProfile(EditorLabel.Trim(), EditorProvider ?? "", _Patch(null)));
            Rows.Add(new ServerProfileRowViewModel(made));
        }
        else if (_editing is { } row)
        {
            var changed = row.Profile with { Model = EditorModel };
            await Task.CompletedTask;

            var index = Rows.IndexOf(row);
            if (index >= 0)
            {
                Rows[index] = new ServerProfileRowViewModel(changed);
            }
        }

        IsEditorOpen = false;
    });

    [RelayCommand]
    private Task DeleteAsync(ServerProfileRowViewModel row) => _RunAsync(async () =>
    {
        await profiles.DeleteAsync(row.Label);
        Rows.Remove(row);
    });

    private void _Fill(RemoteProfile? profile)
    {
        EditorModel = profile?.Model ?? "";
        EditorPermissionMode = profile?.PermissionMode ?? "";
        EditorMcpServers = _McpText(profile?.McpServers);
        EditorVariables.Clear();
        foreach (var variable in profile?.Environment ?? [])
        {
            EditorVariables.Add(new ServerProfileVariableViewModel(variable));
        }

        OnPropertyChanged(nameof(HasEditorVariables));
    }

    // Only what differs from what the server sent; a new profile names what was filled in.
    private RemoteProfilePatch _Patch(RemoteProfile? original)
    {
        var plain = EditorVariables.Where(variable => !variable.IsSecret).ToList();
        return new RemoteProfilePatch
        {
            Model = _Changed(EditorModel, original?.Model),
            PermissionMode = _Changed(EditorPermissionMode, original?.PermissionMode),
            McpServers = EditorMcpServers.Trim() == _McpText(original?.McpServers) ? null : new RemoteMcpSelection(_McpNames(EditorMcpServers)),
            Environment = plain.Any(variable => variable.IsChanged) ? [.. plain.Select(variable => new RemoteProfileVariable(variable.Key, variable.Value))] : null,
        };
    }

    private static string? _Changed(string edited, string? original) =>
        edited.Trim() == (original ?? "") ? null : edited.Trim();

    // Blank is every enabled server, as a profile without a selection has it.
    private static IReadOnlyList<string>? _McpNames(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static string _McpText(IReadOnlyList<string>? names) => names is null ? "" : string.Join(", ", names);

    private async Task _RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Status = "";
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// AC-1473: one profile in the list; its sign-in column only reads what the server says.
public sealed class ServerProfileRowViewModel(RemoteProfile profile)
{
    public RemoteProfile Profile { get; } = profile;

    public string Label => Profile.Label;

    public string Detail => Profile.Model is { Length: > 0 } model ? $"{Profile.Provider} · {model}" : Profile.Provider;

    public string SignInText => Profile.SignIn switch
    {
        ProfileSignInKind.SignedIn => "signed in",
        ProfileSignInKind.Expired => "sign-in expired",
        _ when Profile.HasApiKey || Profile.Environment.Any(variable => variable.IsSecret) => "key from secret",
        _ => "not checked",
    };

    public bool IsSignInBad => Profile.SignIn == ProfileSignInKind.Expired;

    public bool IsSignInGood => !IsSignInBad && SignInText != "not checked";

    public bool IsSignInUnknown => SignInText == "not checked";
}

// AC-1473: one environment variable in the editor; a secret one shows that it is set, never what it is.
public sealed partial class ServerProfileVariableViewModel(RemoteProfileVariable variable) : ObservableObject
{
    private readonly string _original = variable.Value ?? "";

    public string Key { get; } = variable.Key;

    public bool IsSecret { get; } = variable.IsSecret;

    public bool IsPlain => !IsSecret;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _value = variable.Value ?? "";

    public bool IsChanged => !IsSecret && Value != _original;
}
