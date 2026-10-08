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

    // AC-1475: the editor's fields, shared with the Assistant page.
    public ServerProfileEditorViewModel Editor { get; } = new();

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

    public string EditorTitle => IsCreating ? "New profile" : $"Edit “{_editing?.Label}”";

    public string EditorSaveLabel => IsCreating ? "Create" : "Save";

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
        Editor.Fill(null);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void StartEdit(ServerProfileRowViewModel row)
    {
        _editing = row;
        IsCreating = false;
        EditorLabel = row.Label;
        Editor.Fill(row.Profile);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    [RelayCommand]
    private Task SaveEditorAsync() => _RunAsync(async () =>
    {
        if (IsCreating)
        {
            var made = await profiles.CreateAsync(new RemoteNewProfile(EditorLabel.Trim(), EditorProvider ?? "", Editor.Patch()));
            Rows.Add(new ServerProfileRowViewModel(made));
        }
        else if (_editing is { } row)
        {
            if (await profiles.UpdateAsync(row.Label, Editor.Patch() ?? new RemoteProfilePatch()) is not { } changed)
            {
                Status = $"The server no longer has a profile “{row.Label}”.";
                return;
            }

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
        ProfileSignInKind.NotSignedIn => "Not signed in",
        ProfileSignInKind.Expired => "sign-in expired",
        _ when Profile.HasApiKey || Profile.Environment.Any(variable => variable.IsSecret) => "key from secret",
        _ => "not checked",
    };

    public bool IsSignInBad => Profile.SignIn == ProfileSignInKind.Expired;

    public bool IsSignInGood => !IsSignInBad && SignInText != "not checked";

    public bool IsSignInUnknown => SignInText == "not checked";
}

// AC-1473: one environment variable in the editor; a secret one shows that it is set, never what it is. AC-1475: one
// added here names its own key.
public sealed partial class ServerProfileVariableViewModel(RemoteProfileVariable variable) : ObservableObject
{
    private readonly string _original = variable.Value ?? "";

    [ObservableProperty]
    private string _key = variable.Key;

    public bool IsSecret { get; } = variable.IsSecret;

    public bool IsPlain => !IsSecret;

    public bool IsNew { get; init; }

    public bool IsExisting => !IsNew;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _value = variable.Value ?? "";

    public bool IsChanged => !IsSecret && (IsNew || Value != _original);
}
