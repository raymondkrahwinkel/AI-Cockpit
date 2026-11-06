using System.Collections.ObjectModel;
using Cockpit.Core.Abstractions.Assistant;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

public sealed partial class SessionMcpDialogViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<SessionMcpSetResult>> _read;
    private readonly Func<string, CancellationToken, Task<SessionMcpSetResult>> _remove;

    public SessionMcpDialogViewModel()
        : this(
            "Research session",
            _ => Task.FromResult(new SessionMcpSetResult(true, "Assistant", [
                new SessionMcpServerState("Depot", "connected"),
                new SessionMcpServerState("YouTrack", "sign-in needed", "Needs a sign-in."),
                new SessionMcpServerState("Weather", "not reachable", "The server did not respond."),
            ], null)),
            (_, _) => Task.FromResult(new SessionMcpSetResult(true, "Assistant", [], null)))
    {
        _Apply(_read(CancellationToken.None).GetAwaiter().GetResult());
    }

    public SessionMcpDialogViewModel(
        string sessionName,
        Func<CancellationToken, Task<SessionMcpSetResult>> read,
        Func<string, CancellationToken, Task<SessionMcpSetResult>> remove)
    {
        SessionName = sessionName;
        _read = read;
        _remove = remove;
    }

    public string SessionName { get; }

    public ObservableCollection<SessionMcpServerState> Servers { get; } = [];

    [ObservableProperty]
    private string? _manager;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            _Apply(await _read(CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(SessionMcpServerState? server)
    {
        if (server is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _Apply(await _remove(server.Name, CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void _Apply(SessionMcpSetResult result)
    {
        Error = result.Error;
        if (!result.Ok)
        {
            return;
        }

        Manager = result.Manager;
        Servers.Clear();
        foreach (var server in result.Servers)
        {
            Servers.Add(server);
        }
    }
}
