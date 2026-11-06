using System.Collections.ObjectModel;
using Cockpit.Core.Abstractions.Assistant;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

public sealed partial class SessionMcpDialogViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    private readonly Func<CancellationToken, Task<SessionMcpSetResult>> _read;
    private readonly Func<string, CancellationToken, Task<SessionMcpSetResult>> _remove;
    private readonly Func<string, CancellationToken, Task<SessionMcpSetResult>> _add;
    private readonly Func<string, CancellationToken, Task<string>> _signIn;

    public SessionMcpDialogViewModel()
        : this(
            "Research session",
            _ => Task.FromResult(new SessionMcpSetResult(true, "Operator", [
                new SessionMcpServerState("Depot", "connected"),
                new SessionMcpServerState("YouTrack", "sign-in needed", "Needs a sign-in."),
                new SessionMcpServerState("Weather", "not reachable", "The server did not respond."),
            ], null) { AvailableServers = [new("GitHub", "available"), new("Depot account", "sign-in needed")] }),
            (_, _) => Task.FromResult(new SessionMcpSetResult(true, "Operator", [], null)),
            (_, _) => Task.FromResult(new SessionMcpSetResult(true, "Operator", [], null)),
            (_, _) => Task.FromResult("Authorized"))
    {
        _Apply(_read(CancellationToken.None).GetAwaiter().GetResult());
    }

    public SessionMcpDialogViewModel(
        string sessionName,
        Func<CancellationToken, Task<SessionMcpSetResult>> read,
        Func<string, CancellationToken, Task<SessionMcpSetResult>> remove,
        Func<string, CancellationToken, Task<SessionMcpSetResult>>? add = null,
        Func<string, CancellationToken, Task<string>>? signIn = null)
    {
        SessionName = sessionName;
        _read = read;
        _remove = remove;
        _add = add ?? ((_, _) => Task.FromResult(new SessionMcpSetResult(false, null, [], "Adding servers is unavailable.")));
        _signIn = signIn ?? ((_, _) => Task.FromResult("Unavailable"));
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    public string SessionName { get; }

    public ObservableCollection<SessionMcpServerState> Servers { get; } = [];

    public ObservableCollection<SessionMcpServerState> AvailableServers { get; } = [];

    public bool CanAdd => Manager == "Operator";

    public bool SelectedNeedsSignIn => SelectedServer?.NeedsSignIn == true;

    public bool CanAddSelected => CanAdd && SelectedServer is not null && !SelectedNeedsSignIn && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedNeedsSignIn), nameof(CanAddSelected))]
    private SessionMcpServerState? _selectedServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(CanAddSelected))]
    private string? _manager;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddSelected))]
    private bool _isBusy;

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            _Apply(await _read(_closing.Token).ConfigureAwait(true));
        }
        catch (Exception)
        {
            Error = "The MCP set could not be loaded. Try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignInAsync(SessionMcpServerState? server)
    {
        server ??= SelectedServer;
        if (server is null || IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var outcome = await _signIn(server.Name, _closing.Token).ConfigureAwait(true);
            _Apply(await _read(CancellationToken.None).ConfigureAwait(true));
            if (outcome != "Authorized")
            {
                Error = "Sign-in did not finish. Try again.";
            }
        }
        catch (Exception)
        {
            Error = "Sign-in failed. Try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (SelectedServer is not { } server || !CanAddSelected)
        {
            return;
        }
        IsBusy = true;
        try
        {
            _Apply(await _add(server.Name, CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception)
        {
            Error = "The server could not be added. Try again.";
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
        catch (Exception)
        {
            Error = "The server could not be removed. Try again.";
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
        var selectedName = SelectedServer?.Name;
        AvailableServers.Clear();
        foreach (var available in result.AvailableServers)
        {
            AvailableServers.Add(available);
        }
        SelectedServer = AvailableServers.FirstOrDefault(server => server.Name == selectedName) ?? AvailableServers.FirstOrDefault();
        Servers.Clear();
        foreach (var server in result.Servers)
        {
            Servers.Add(server);
        }
    }
}
