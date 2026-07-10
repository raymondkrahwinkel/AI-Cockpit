using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the MCP-servers dialog (#26): list the shared MCP servers, edit each one's transport/command or
/// URL and auth, and add/remove entries. Save persists the whole edited list through
/// <see cref="IMcpServerStore"/> — one registry that later feeds both the local-LLM tool-loop and the
/// Claude CLI. The view closes via <see cref="CloseRequested"/>.
/// </summary>
public partial class McpServersViewModel : ViewModelBase
{
    private readonly IMcpServerStore? _store;

    public event Action? CloseRequested;

    public ObservableCollection<EditableMcpServerViewModel> Servers { get; } = [];

    [ObservableProperty]
    private EditableMcpServerViewModel? _selectedServer;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public McpServersViewModel()
    {
        var sample = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "filesystem", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "."] });
        Servers.Add(sample);
        SelectedServer = sample;
    }

    public McpServersViewModel(IMcpServerStore store)
    {
        _store = store;
    }

    public async Task LoadAsync()
    {
        if (_store is null)
        {
            return;
        }

        var servers = await _store.LoadAsync();
        Servers.Clear();
        foreach (var server in servers)
        {
            Servers.Add(new EditableMcpServerViewModel(server));
        }

        SelectedServer = Servers.FirstOrDefault();
    }

    [RelayCommand]
    private void AddServer()
    {
        var added = new EditableMcpServerViewModel(new McpServerConfig { Name = "new server", Command = "npx" });
        Servers.Add(added);
        SelectedServer = added;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedServer))]
    private void RemoveServer()
    {
        if (SelectedServer is null)
        {
            return;
        }

        var index = Servers.IndexOf(SelectedServer);
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.Count == 0 ? null : Servers[Math.Min(index, Servers.Count - 1)];
    }

    private bool HasSelectedServer => SelectedServer is not null;

    partial void OnSelectedServerChanged(EditableMcpServerViewModel? value) => RemoveServerCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_store is null)
        {
            return;
        }

        if (Servers.Any(server => !server.IsValid))
        {
            StatusMessage = "Every server needs a name, plus a command (stdio) or a URL (http).";
            return;
        }

        await _store.SaveAsync(Servers.Select(server => server.ToConfig()).ToList());
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
