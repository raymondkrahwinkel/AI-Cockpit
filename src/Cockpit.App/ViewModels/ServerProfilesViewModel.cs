using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

// AC-1585: Options › Profiles on a server is the same editor as this cockpit's own (ManageProfilesDialogViewModel in
// ProfilesEditor), with the server behind each of its services. The server's page has no Apply, so Save writes here.
public sealed partial class ServerProfilesViewModel(ServerProfileServices services) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Count))]
    private ManageProfilesDialogViewModel? _editor;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private bool _isBusy;

    public int Count => Editor?.Profiles.Count ?? 0;

    [RelayCommand]
    public Task LoadAsync() => _RunAsync(_ReloadAsync);

    [RelayCommand]
    private Task SaveAsync() => _RunAsync(async () =>
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (!editor.Validate())
        {
            Status = editor.StatusMessage;
            StatusIsError = true;
            return;
        }

        await editor.PersistAsync();
        await _ReloadAsync();
        Status = "Saved on the server.";
    });

    // Read back from the server after each save as well, so a masked secret and a renamed label show as stored.
    private async Task _ReloadAsync()
    {
        await services.PrepareAsync();
        var editor = services.CreateEditor();
        await editor.LoadAsync();
        Editor = editor;
    }

    private async Task _RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Status = "";
        StatusIsError = false;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            StatusIsError = true;
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
