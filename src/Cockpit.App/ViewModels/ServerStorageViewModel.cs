using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Remote;

namespace Cockpit.App.ViewModels;

// AC-2035: Options › Storage on a server. The switch acts at once, like every other server page; a refused or failed
// change puts the box back to what the server holds.
public sealed partial class ServerStorageViewModel(IServerStorageSettings storage) : ObservableObject
{
    private bool _applying;

    [ObservableProperty]
    private bool _automaticDockerCleanup = true;

    [ObservableProperty]
    private string _status = "";

    [RelayCommand]
    private Task LoadAsync() => _RunAsync(async () => _Show(await storage.GetAutomaticDockerCleanupAsync()), null);

    partial void OnAutomaticDockerCleanupChanged(bool value)
    {
        if (!_applying)
        {
            _ = _RunAsync(() => storage.SetAutomaticDockerCleanupAsync(value), !value);
        }
    }

    private void _Show(bool value)
    {
        _applying = true;
        AutomaticDockerCleanup = value;
        _applying = false;
    }

    private async Task _RunAsync(Func<Task> call, bool? restoreOnFailure)
    {
        try
        {
            await call();
            Status = "";
        }
        catch (Exception exception)
        {
            if (restoreOnFailure is { } previous)
            {
                _Show(previous);
            }

            Status = exception.Message;
        }
    }
}
