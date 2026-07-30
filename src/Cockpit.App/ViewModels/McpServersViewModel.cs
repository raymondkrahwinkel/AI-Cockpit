using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>
    /// Cancelled once the dialog is going away by any route — a successful Save, Cancel, or the window's own close
    /// button (<see cref="OnWindowClosed"/>) — so a row's in-flight interactive sign-in has somewhere to hear that
    /// there is no view model left to report back to (AC-499 review fix, finding 6). Handed to every row as
    /// <see cref="EditableMcpServerViewModel"/>'s <c>dialogClosing</c> constructor argument.
    /// </summary>
    private readonly CancellationTokenSource _dialogLifetime = new();

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
            new McpServerConfig { Name = "filesystem", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "."] },
            saveAllForSignIn: _SaveAllForSignInAsync,
            isDialogBusy: _IsAnyRowBusy,
            dialogClosing: _dialogLifetime.Token);
        Servers.Add(sample);
        _AttachRow(sample);
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
        var hidden = servers.Where(server => internalNames.Contains(server.Name.Trim())).ToList();

        foreach (var existing in Servers)
        {
            _DetachRow(existing);
        }

        Servers.Clear();
        foreach (var server in servers.Where(server => !internalNames.Contains(server.Name.Trim())))
        {
            var row = new EditableMcpServerViewModel(
                server, _oauthCoordinator, saveAllForSignIn: _SaveAllForSignInAsync,
                isDialogBusy: _IsAnyRowBusy, dialogClosing: _dialogLifetime.Token);
            Servers.Add(row);
            _AttachRow(row);
        }

        SelectedServer = Servers.FirstOrDefault();

        // Saying it out loud, because the next Save writes only what is on screen and these are not. For a leftover
        // an older build wrote that is the intended tidy-up; for a server the operator configured under a name a
        // plugin has since taken, it is their entry being deleted — and until now both happened without a word.
        StatusMessage = hidden.Count == 0
            ? string.Empty
            : $"Hidden here because the cockpit already runs a server by that name: {string.Join(", ", hidden.Select(server => server.Name.Trim()))}. Saving removes them — rename yours first if you meant to keep it.";

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
            new McpServerConfig { Id = McpServerIdentity.NewId(), Name = _UnusedServerName(), Command = "npx" },
            _oauthCoordinator,
            isPersisted: false,
            saveAllForSignIn: _SaveAllForSignInAsync,
            isDialogBusy: _IsAnyRowBusy,
            dialogClosing: _dialogLifetime.Token);
        Servers.Add(added);
        _AttachRow(added);
        SelectedServer = added;
    }

    private string _UnusedServerName()
    {
        const string baseName = "new server";

        // Trimmed, because that is what a save writes and what the duplicate check compares — otherwise a row typed
        // as "new server " is invisible here and the operator is refused a save over a clash they cannot see.
        var taken = Servers
            .Select(server => server.Name.Trim())
            .Concat(_internalProviders.SelectMany(_NamesOf))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        _DetachRow(SelectedServer);
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.Count == 0 ? null : Servers[Math.Min(index, Servers.Count - 1)];
    }

    /// <summary>Whether any row's sign-in/sign-out is in flight — busy is dialog-wide (AC-499 review fix, finding
    /// 6), because two rows racing their own save-then-authorize can overwrite each other's resync snapshot (see
    /// <see cref="_SaveAllForSignInAsync"/>). Handed to each row as its <c>isDialogBusy</c> constructor argument.</summary>
    private bool _IsAnyRowBusy() => Servers.Any(server => server.IsAuthBusy);

    private void _AttachRow(EditableMcpServerViewModel row) => row.PropertyChanged += _OnRowPropertyChanged;

    private void _DetachRow(EditableMcpServerViewModel row) => row.PropertyChanged -= _OnRowPropertyChanged;

    // Nobody re-evaluates a CanExecute on its own — a row going busy has to say so to every command that reads
    // _IsAnyRowBusy: Save, Cancel, and every other row's own Sign in/Sign out (AC-499 review fix, finding 6).
    private void _OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditableMcpServerViewModel.IsAuthBusy))
        {
            return;
        }

        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        foreach (var server in Servers)
        {
            server.NotifyDialogBusyChanged();
        }
    }

    private bool HasSelectedServer => SelectedServer is not null;

    partial void OnSelectedServerChanged(EditableMcpServerViewModel? value) => RemoveServerCommand.NotifyCanExecuteChanged();

    /// <summary>Save and Cancel are refused while any row is mid sign-in/sign-out (AC-499 review fix, finding 6) —
    /// closing the dialog then would abandon an interactive browser round trip whose result and errors would land
    /// on a discarded view model, and a second Save would race the first row's own save-then-authorize.</summary>
    private bool CanSaveOrCancel => !_IsAnyRowBusy();

    [RelayCommand(CanExecute = nameof(CanSaveOrCancel))]
    private async Task SaveAsync()
    {
        if (await _SaveAllForSignInAsync() is not null)
        {
            _dialogLifetime.Cancel();
            CloseRequested?.Invoke();
        }
    }

    /// <summary>
    /// Validates and persists the whole edited list without closing the dialog (AC-499) — the one save route, shared
    /// by the Save button and a row's own sign-in (<see cref="EditableMcpServerViewModel.SignInAsync"/> saves through
    /// this before it authorizes, so a token is never filed under a name the operator has not actually saved). Only
    /// the Save button closes the dialog on success; a sign-in needs the dialog to stay open.
    /// <para>
    /// On a successful write this also resyncs every row's stored name and auth status against the reloaded list
    /// (<see cref="_ResyncRowsAfterDialogSaveAsync"/>) — not just the row that asked for the save — and clears
    /// <see cref="StatusMessage"/>, since whatever it said before (a validation refusal, a hidden-servers notice
    /// this save just acted on) no longer describes the dialog. Returns what <see cref="IMcpServerStore.LoadAsync"/>
    /// reports back on success. Returns null on failure, with <see cref="StatusMessage"/> set to why — except from
    /// the parameterless design-time constructor's <c>_store is null</c> guard just below, which no real dialog ever
    /// reaches (there is no store to have failed, so there is nothing to say).
    /// </para>
    /// <para>
    /// A write that succeeds but cannot be read back afterward is reported as exactly that — "saved, but not
    /// confirmed" — never folded into the same wording as "nothing was saved" (AC-499 review fix, finding 2): the
    /// two are different facts for whoever is about to sign in on top of this, and only one of them is safe to
    /// treat as "try again from scratch".
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<McpServerConfig>?> _SaveAllForSignInAsync()
    {
        if (_store is null)
        {
            return null;
        }

        if (Servers.Any(server => !server.IsValid))
        {
            StatusMessage = "Every server needs a name, plus a command (stdio) or a URL (http).";
            return null;
        }

        // Names have to be unique because everything downstream treats one as an identity: the credential store files
        // a token under it, and each agent's config is keyed by it so a repeat silently drops a server the operator
        // ticked. Refusing the save is the only place that can still be said plainly.
        if (Servers.GroupBy(server => server.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
        {
            StatusMessage = $"Two servers are called \"{duplicate.Key}\". Names identify a server to the agents, so each one needs its own.";
            return null;
        }

        // The cockpit's own loopback servers and a plugin's endpoints are not in this list — they are filtered out of
        // it — but they share the same namespace, and the catalog's merge lets them win. An operator's server that
        // takes one of those names is dropped from the fan-out, hidden from this dialog on the next open, and gone
        // from the store on the save after that: configured, saved, ticked, and silently not there.
        var reserved = _internalProviders.SelectMany(_NamesOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Servers.FirstOrDefault(server => reserved.Contains(server.Name.Trim())) is { } clash)
        {
            StatusMessage = $"\"{clash.Name.Trim()}\" is a name the cockpit already uses for one of its own servers. Pick another, or it will quietly lose to that one.";
            return null;
        }

        try
        {
            await _store.SaveAsync(Servers.Select(server => server.ToConfig()).ToList());
        }
        catch (Exception)
        {
            // A sign-in relies on this to tell it a save did not happen (AC-499) — without a caught failure here it
            // would go on to authorize a token under a name the store never received.
            StatusMessage = "Couldn't save. Try again.";
            return null;
        }

        try
        {
            var reloaded = await _store.LoadAsync();
            await _ResyncRowsAfterDialogSaveAsync(reloaded);
            StatusMessage = string.Empty;
            return reloaded;
        }
        catch (Exception)
        {
            // The write above already happened — this only failed to confirm it back, which used to escape this
            // method uncaught (AC-499 review fix, finding 2): a sign-in's own outer catch turned that into "Sign-in
            // failed. Try again." even though the write had gone through, and the Save button's [RelayCommand]
            // rethrew it into the CommunityToolkit default (an async-void continuation on the UI dispatcher) with
            // nothing telling the operator anything landed on disk.
            StatusMessage = "Saved, but couldn't read the servers back to confirm. Try again to check.";
            return null;
        }
    }

    /// <summary>
    /// Resyncs every row against the entry carrying its own id in <paramref name="reloaded"/> (AC-499 review fix,
    /// finding 1; matched by id since AC-403) — not just the row whose sign-in triggered this save. A row whose id
    /// is not in the reloaded list resyncs against null, which is how a row the store did not take reports itself as
    /// unsaved rather than borrowing the standing of whatever sat at its index.
    /// <para>
    /// This used to match by list position, for want of anything else: a name was a row's only handle and a save
    /// rewrote every one of them at once, so the order the store round-tripped was the only thing tying a row to
    /// what had just been written for it. Matching on an id that a rename cannot move says the same thing without
    /// depending on save order at all.
    /// </para>
    /// </summary>
    private async Task _ResyncRowsAfterDialogSaveAsync(IReadOnlyList<McpServerConfig> reloaded)
    {
        var byId = reloaded
            .GroupBy(server => server.IdentityKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var resyncs = Servers.Select(server =>
            server.ResyncAfterDialogSaveAsync(byId.GetValueOrDefault(server.Id)));
        await Task.WhenAll(resyncs).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSaveOrCancel))]
    private void Cancel()
    {
        _dialogLifetime.Cancel();
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Called from the dialog window's own Closed handler — the one close route (the OS close button, Escape) that
    /// never runs <see cref="SaveCommand"/> or <see cref="CancelCommand"/>, and so would otherwise leave
    /// <see cref="_dialogLifetime"/> live under a view model nothing shows any more (AC-499 review fix, finding 6).
    /// Idempotent: <see cref="CancellationTokenSource.Cancel()"/> on an already-cancelled source is a no-op, so this
    /// is safe to call after Save or Cancel already cancelled it too.
    /// </summary>
    internal void OnWindowClosed() => _dialogLifetime.Cancel();
}
