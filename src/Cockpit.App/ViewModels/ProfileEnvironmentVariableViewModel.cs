using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One editable row of a profile's spawn environment variables (AC-22): key, value and whether the value is
/// a credential — a secret persists encrypted and its field masks. <see cref="IsKeyValid"/> drives the row's
/// inline hint and the dialog's save gate.
/// </summary>
public partial class ProfileEnvironmentVariableViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _key;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private bool _isSecret;

    public ProfileEnvironmentVariableViewModel(string key = "", string value = "", bool isSecret = false)
    {
        _key = key;
        _value = value;
        _isSecret = isSecret;
    }

    /// <summary>A POSIX-style variable name; an empty (still unfilled) key gets the same hint as a wrong one.</summary>
    public bool IsKeyValid => ProfileEnvironmentVariable.IsValidKey(Key);

    partial void OnKeyChanged(string value) => OnPropertyChanged(nameof(IsKeyValid));

    public ProfileEnvironmentVariable ToDomain() => new(Key, Value, IsSecret);
}
