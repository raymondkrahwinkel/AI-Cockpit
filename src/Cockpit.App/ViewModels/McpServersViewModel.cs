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
    private readonly IReadOnlyList<ICockpitInternalMcpProvider> _internalProviders;

    /// <summary>
    /// Answers each row's OAuth standing (AC-355) — null in the parameterless design-time constructor, so the
    /// previewer renders with status simply unshown rather than needing a fake coordinator of its own.
    /// </summary>
    private readonly IMcpOAuthCoordinator? _oauthCoordinator;

    public event Action? CloseRequested;

    public ObservableCollection<EditableMcpServerViewModel> Servers { get; } = [];

    [ObservableProperty]
    private EditableMcpServerViewModel? _selectedServer;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public McpServersViewModel()
    {
        _internalProviders = [];
        var sample = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "filesystem", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "."] });
        Servers.Add(sample);
        SelectedServer = sample;
    }

    public McpServersViewModel(IMcpServerStore store, IEnumerable<ICockpitInternalMcpProvider> internalProviders, IMcpOAuthCoordinator? oauthCoordinator = null)
    {
        _store = store;
        _internalProviders = [.. internalProviders];
        _oauthCoordinator = oauthCoordinator;
    }

    public async Task LoadAsync()
    {
        if (_store is null)
        {
            return;
        }

        // The cockpit's own loopback servers (session-status, orchestrator, a plugin's endpoint) are not the
        // operator's to edit here (AC-40): they are answered live to the session fan-out, controlled from Options or
        // the owning plugin's settings, and are matched by name so an entry an older build left in the store is
        // hidden too — and dropped from the store on the next Save.
        var internalNames = _internalProviders
            .SelectMany(_NamesOf)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var servers = await _store.LoadAsync();
        Servers.Clear();
        foreach (var server in servers.Where(server => !internalNames.Contains(server.Name)))
        {
            Servers.Add(new EditableMcpServerViewModel(server, _oauthCoordinator));
        }

        SelectedServer = Servers.FirstOrDefault();

        // Reads storage only (AC-355) — cheap enough to run for every row up front, so the list shows each OAuth
        // server's standing without the operator having to select one first.
        await Task.WhenAll(Servers.Select(server => server.RefreshAuthStateAsync()));
    }

    // A provider that throws while listing its servers must not break the manager dialog — it just means its names
    // are not filtered out this time (matching the catalog's own defensive guard on the same call).
    private static IEnumerable<string> _NamesOf(ICockpitInternalMcpProvider provider)
    {
        try
        {
            return provider.GetServers().Select(server => server.Name);
        }
        catch
        {
            return [];
        }
    }

    [RelayCommand]
    private void AddServer()
    {
        // A distinct placeholder, because the name is a key elsewhere and not just a label: a token is filed under
        // it, and the fan-out writes each server into the agent's config by it (last one wins). Two rows both called
        // "new server" therefore collapse into one mounted server while both sit ticked in the checklist, and that
        // is invisible until an agent is missing tools it was promised.
        var added = new EditableMcpServerViewModel(
            new McpServerConfig { Name = _UnusedServerName(), Command = "npx" },
            _oauthCoordinator,
            isPersisted: false);
        Servers.Add(added);
        SelectedServer = added;
    }

    private string _UnusedServerName()
    {
        const string baseName = "new server";
        var taken = Servers.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = baseName;
        for (var suffix = 2; taken.Contains(candidate); suffix++)
        {
            candidate = $"{baseName} {suffix}";
        }

        return candidate;
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

        // Names have to be unique because everything downstream treats one as an identity: the credential store files
        // a token under it, and each agent's config is keyed by it so a repeat silently drops a server the operator
        // ticked. Refusing the save is the only place that can still be said plainly.
        if (Servers.GroupBy(server => server.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
        {
            StatusMessage = $"Two servers are called \"{duplicate.Key}\". Names identify a server to the agents, so each one needs its own.";
            return;
        }

        await _store.SaveAsync(Servers.Select(server => server.ToConfig()).ToList());
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
