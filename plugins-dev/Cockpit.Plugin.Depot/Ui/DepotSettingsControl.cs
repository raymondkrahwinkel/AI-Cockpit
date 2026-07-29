using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;

namespace Cockpit.Plugin.Depot.Ui;

/// <summary>
/// The plugin's settings view (opened from the gear in the plugin manager): a manageable list of Depot connection
/// rows (AC-243). Implements <see cref="IPluginSettingsView"/>, so the host renders the Save/Close footer and
/// <see cref="Save"/> persists on Save — the connection metadata to storage, each connection as an OAuth
/// <see cref="McpServerContribution"/> in the shared MCP registry, and (AC-501) each connection's own memory-source
/// registration. A connection removed or renamed here has its old MCP-registry entry and its old memory-source
/// scheme both reclaimed the same save, so neither a stale "Depot: &lt;old name&gt;" MCP entry nor a stale picker
/// row lingers until a restart.
/// </summary>
internal sealed class DepotSettingsControl : UserControl, IPluginSettingsView
{
    private readonly ICockpitHost _host;
    private readonly DepotSettings _settings;
    private readonly StackPanel _connectionsPanel;
    private readonly List<DepotConnectionRowControl> _rows = [];
    private readonly IReadOnlyList<DepotConnectionRegistration> _originalConnections;

    public DepotSettingsControl(ICockpitHost host, DepotSettings settings)
    {
        _host = host;
        _settings = settings;
        _connectionsPanel = new StackPanel { Spacing = 4 };

        _originalConnections = settings.Connections;
        if (_originalConnections.Count == 0)
        {
            _AddRow(existing: null);
        }
        else
        {
            foreach (var connection in _originalConnections)
            {
                _AddRow(connection);
            }
        }

        var addConnection = new Button { Content = "+ Add connection" };
        addConnection.Click += (_, _) => _AddRow(existing: null);

        // No ScrollViewer here: the host dialog already wraps every settings view in one — see
        // KubernetesSettingsControl's identical note.
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("Depot connections"),
                _Hint("Each connection is contributed to the cockpit's MCP servers as \"Depot: <name>\", using Depot's own OAuth sign-in — the plugin never holds a token. Sign in below once a connection is saved."),
                _connectionsPanel,
                addConnection,
            },
        };

        _ = _RefreshAuthStatesAsync();
    }

    private void _AddRow(DepotConnectionRegistration? existing)
    {
        var row = new DepotConnectionRowControl(_host, existing);
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

    public bool Save()
    {
        var candidates = _rows
            .Where(row => !row.IsBlank)
            .Select(row => row.ToRegistration())
            .Where(registration => !string.IsNullOrWhiteSpace(registration.Name) && !string.IsNullOrWhiteSpace(registration.Url))
            .ToList();

        // Two rows saved under the same name would upsert the very same registry entry from two racing calls
        // below — keep the first and drop the rest rather than let whichever AddMcpServer call happens to finish
        // last silently decide which row's URL wins.
        var registrations = new List<DepotConnectionRegistration>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (seenNames.Add(candidate.McpServerName))
            {
                registrations.Add(candidate);
            }
        }

        var keptNames = registrations.Select(registration => registration.McpServerName).ToHashSet(StringComparer.Ordinal);

        // A connection removed, or renamed (which changes McpServerName), leaves its old MCP-registry entry behind
        // unless reclaimed here — the same orphan-cleanup KubernetesSettingsControl.Save does for a cluster's
        // secret, applied to a registry entry instead.
        var orphanedNames = _originalConnections
            .Select(connection => connection.McpServerName)
            .Where(name => !keptNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Memory sources are in-memory registry entries, not disk-persisted like the MCP registry below, so the
        // sync is a direct synchronous call rather than a fire-and-forget task — there is no store round trip here
        // to race.
        _SyncMemorySources(registrations);

        _settings.Connections = registrations;

        // AddMcpServer/RemoveMcpServer each do their own load-modify-save round trip against the shared store with
        // no locking across separate calls — firing several at once (a rename is a Remove and an Add together)
        // races two calls into reading the same stale snapshot, and whichever SaveAsync finishes last silently
        // overwrites the other's write. Chaining them into one sequential fire-and-forget task keeps Save()
        // synchronous (the IPluginSettingsView contract) while making sure each call sees the previous one's
        // result before it reads.
        _ = _SyncMcpRegistryAsync(orphanedNames, registrations);

        return true;
    }

    // The live-refresh half of AC-501: a connection's memory source used to be registered once at Initialize and
    // never touched again, so an operator adding, renaming or removing a connection here saw no effect until an app
    // restart. Diffed by connection Id (not by list position, which a removal ahead of a connection would shift out
    // from under it) against the registrations BuildRegistrationPairs would have produced for the connections this
    // view started from.
    private void _SyncMemorySources(IReadOnlyList<DepotConnectionRegistration> registrations)
    {
        // A plain Dictionary from BuildRegistrationPairs' own Connection.Id would throw on a duplicate key if
        // storage ever held two connections under the same id (corrupted or hand-edited settings) — building it by
        // hand keeps a duplicate merely overwriting the earlier entry instead of crashing Save().
        var before = new Dictionary<string, ProjectMemorySourceRegistration>(StringComparer.Ordinal);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(_originalConnections, _host))
        {
            before[pair.Connection.Id] = pair.Registration;
        }

        var after = DepotMemorySource.BuildRegistrationPairs(registrations, _host);
        var afterById = new Dictionary<string, ProjectMemorySourceRegistration>(StringComparer.Ordinal);
        foreach (var pair in after)
        {
            afterById[pair.Connection.Id] = pair.Registration;
        }

        // Two full passes, not one interleaved remove-then-add per connection: two connections swapping names (or
        // otherwise trading schemes) would let an Add below claim a scheme a later connection in this same save
        // still held under its own before-registration, since that connection's own Remove had not run yet —
        // Register would then refuse the Add and the operator would lose that source from the picker until a
        // restart, with nothing surfacing why. Retiring every stale scheme first removes that ordering dependency.
        foreach (var (id, oldRegistration) in before)
        {
            if (!afterById.TryGetValue(id, out var stillCurrent) || stillCurrent != oldRegistration)
            {
                _host.RemoveProjectMemorySource(oldRegistration.Scheme);
            }
        }

        foreach (var (connection, newRegistration) in after)
        {
            if (before.TryGetValue(connection.Id, out var oldRegistration) && oldRegistration == newRegistration)
            {
                // Unchanged: re-adding it would only hit Register's "scheme already taken" refusal, since this very
                // content is already the one registered.
                continue;
            }

            _host.AddProjectMemorySource(newRegistration);
        }
    }

    private async Task _SyncMcpRegistryAsync(IReadOnlyList<string> orphanedNames, IReadOnlyList<DepotConnectionRegistration> registrations)
    {
        foreach (var orphanedName in orphanedNames)
        {
            await _host.RemoveMcpServer(orphanedName).ConfigureAwait(false);
        }

        foreach (var registration in registrations)
        {
            await _host.AddMcpServer(new McpServerContribution(
                Name: registration.McpServerName,
                Url: $"{registration.Url}/mcp")
            {
                OAuthAuthority = registration.Url,
            }).ConfigureAwait(false);
        }
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Avalonia.Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
}
