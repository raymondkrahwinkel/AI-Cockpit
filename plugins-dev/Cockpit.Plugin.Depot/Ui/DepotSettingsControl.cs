using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

// IPluginSettingsView.Save has no room for anything but a bool, but a row's own Sign-in click needs to know *why* a
// save was refused (a name or URL collision). Tuple alias, not a new record type, same internal-plumbing idiom
// DepotMemorySource.BuildRegistrationPairs already uses for its own connection/registration pairing.
using DepotSaveResult = (bool Success, string? FailureReason);

namespace Cockpit.Plugin.Depot.UI;

// The plugin's settings view (opened from the gear in the plugin manager): a manageable list of Depot connection
// rows (AC-243). Implements `IPluginSettingsView`, so the host renders the Save/Close footer and
// `Save` persists on Save — the connection metadata to storage. AC-1394: the memory-source/shared-project-source
// registry sync and the orphaned-MCP-registry reclaim that used to run right here, against `ICockpitHost`
// directly, now run on the backend part (`DepotPlugin`), asked over the plugin's channel — this view only writes
// the connection list itself (through the storage it shares with the backend part) and tells the backend part a
// save happened.
internal sealed class DepotSettingsControl : UserControl, IPluginSettingsView
{
    private readonly ICockpitUiHost _host;
    private readonly DepotSettings _settings;
    private readonly StackPanel _connectionsPanel;
    private readonly List<DepotConnectionRowControl> _rows = [];

    public DepotSettingsControl(ICockpitUiHost host, DepotSettings settings)
    {
        _host = host;
        _settings = settings;
        _connectionsPanel = new StackPanel { Spacing = 4 };

        var existingConnections = settings.Connections;
        if (existingConnections.Count == 0)
        {
            _AddRow(existing: null);
        }
        else
        {
            foreach (var connection in existingConnections)
            {
                _AddRow(connection);
            }
        }

        var addConnection = new Button { Content = "+ Add connection" };
        addConnection.Click += (_, _) => _AddRow(existing: null);

        var connectionsHeading = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { _Label("Depot connections"), host.CreateHelpHint("connections", "instance-url") },
        };

        // No ScrollViewer here: the host dialog already wraps every settings view in one — see
        // KubernetesSettingsControl's identical note.
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                connectionsHeading,
                _Hint("Each connection is offered to a session on the project whose memory it holds, as \"Depot: <name>\", using Depot's own OAuth sign-in — the plugin never holds a token."),
                _connectionsPanel,
                addConnection,
            },
        };

        _ = _RefreshAuthStatesAsync();
    }

    private void _AddRow(DepotConnectionRegistration? existing)
    {
        // _SaveDetailed, not Save, is threaded through: a row's own Sign-in click (AC-499) persists the whole list
        // through the exact same route this dialog's Save button uses — the backend-part sync that lives there
        // runs every time something signs in, not only on an explicit Save — but the row also needs the
        // duplicate-name detail Save()'s plain bool cannot carry, to tell the operator which name collided instead
        // of just that the save failed.
        var row = new DepotConnectionRowControl(_host, existing, _settings, _SaveDetailed);
        row.RemoveRequested += () =>
        {
            _rows.Remove(row);
            _connectionsPanel.Children.Remove(row);
        };
        _rows.Add(row);
        _connectionsPanel.Children.Add(row);
    }

    private async Task _RefreshAuthStatesAsync()
    {
        foreach (var row in _rows.ToList())
        {
            await row.RefreshAuthStateAsync().ConfigureAwait(true);
        }
    }

    // AC-1004, criterion 3 — where the old `Save()` ended up. Validation: the duplicate name and URL refusals
    // (AC-499/AC-248), which read the rows and nothing else, are `_TryValidate`. Commit: writing the connection
    // list and telling the backend part is `_Write`. The one exception is `_SaveDetailed` below, and it says why.
    public bool TryStage(out Action? commit, out string? error)
    {
        if (!_TryValidate(out var candidates, out error))
        {
            commit = null;
            return false;
        }

        commit = () => _Write(candidates);
        return true;
    }

    // Validate-and-write in one call, kept for `DepotConnectionRowControl.SignInAsync`: a row's own Sign-in
    // persists the whole list on the spot (AC-499) rather than waiting for the footer, and reports the refusal
    // itself.
    //
    // shortcut (AC-1004, considered and kept): this is the one write in the plugin that does not go through the
    // host. It cannot simply be staged — the host files the token under the connection's *registered* MCP server
    // name, so the connection has to be in storage before the browser opens, and no Cancel can un-issue a token
    // that came back. Sign-in is an action the operator takes, not a value they are editing, and the row says
    // "Saving…" before the browser opens rather than doing it behind their back.
    // ceiling = a Depot embedded in the Options screen (AC-1005) would let a Sign-in click write the connection
    // list while the rest of that screen is still staged, so Cancel takes back everything except this;
    // upgrade = AC-1005 decides between disabling Sign-in until Apply and telling the operator, in the row, that
    // signing in saves the connections first — both need the embedding contract that ticket introduces.
    private DepotSaveResult _SaveDetailed()
    {
        if (!_TryValidate(out var candidates, out var reason))
        {
            return (false, reason);
        }

        _Write(candidates);
        return (true, null);
    }

    // Refuses, with the reason, or hands back the connections to write. Reads the rows and nothing else.
    private bool _TryValidate(out List<DepotConnectionRegistration> candidates, out string? error)
    {
        error = null;
        candidates = _rows
            .Where(row => !row.IsBlank)
            .Select(row => row.ToRegistration())
            .Where(registration => !string.IsNullOrWhiteSpace(registration.Name) && !string.IsNullOrWhiteSpace(registration.Url))
            .ToList();

        // Two rows named alike (case-insensitively — the same comparer ProjectMemorySourceRegistry.Register uses
        // for scheme collisions, and McpServersViewModel.Save uses for the host dialog's own duplicate-name refusal)
        // would collide on the same "Depot: <name>" identity once the backend part derives from this list. Refuse
        // the whole save rather than silently keep the first and drop the rest: dropping a row here also rips its
        // memory-source registration out of the registry and its MCP-registry entry out of storage for whichever
        // row loses the race — a save that discards data is worse than a save that does nothing and says why.
        if (candidates
                .GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1) is { } duplicateName)
        {
            error = $"\"{duplicateName.Key}\" is used by another row above. Rename one of them and try again.";
            return false;
        }

        // AC-248: two rows pointed at the same (already-normalized) Url would register the same shared-project
        // source twice under two names — refused the same way as the name collision above, own message since
        // renaming does not fix a URL collision.
        if (candidates
                .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1) is { } duplicateUrl)
        {
            error = $"This Depot instance is already connected as \"{duplicateUrl.First().Name}\". Remove one of the rows instead of adding a second connection to the same instance.";
            return false;
        }

        return true;
    }

    // Everything that persists, run by the host once the operator confirms (AC-1003) — or straight away by
    // `_SaveDetailed` for a Sign-in click.
    private void _Write(List<DepotConnectionRegistration> candidates)
    {
        // The connection list itself is written by the backend part's SaveConnections handler, not here — it
        // needs to read the pre-save state itself before anything overwrites it (DepotPlugin's own comment on
        // _SaveConnectionsAsync explains why).
        var payload = JsonSerializer.SerializeToElement(
            new DepotSaveConnectionsRequest([.. candidates.Select(candidate => new DepotConnectionPayload(candidate.Id, candidate.Name, candidate.Url))]),
            DepotChannel.Json);

        // ponytail: TryStage's commit is a synchronous Action (IPluginSettingsView predates the plugin split), so
        // it cannot await the channel call that asks the backend part to sync the memory-source and
        // shared-project-source registries and reclaim an orphaned MCP-registry entry. Blocking is safe today (the
        // channel answers in-process with no thread hop); a fire-and-forget InvokeAsync would silently swallow a
        // failed RemoveMcpServer. Upgrade path: a Task-returning ICockpitUiHost.OnSettingsSaved/commit, tracked for
        // every plugin, not just this one — the same ceiling DiscordUi.InitializeUi's own OnSettingsSaved callback
        // documents.
        _host.Channel.InvokeAsync(DepotChannel.SaveConnections, payload).GetAwaiter().GetResult();
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Avalonia.Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
}
