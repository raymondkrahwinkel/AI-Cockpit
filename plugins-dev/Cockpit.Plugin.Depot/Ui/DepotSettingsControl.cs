using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;

// IPluginSettingsView.Save has no room for anything but a bool, but a row's own Sign-in click (DepotConnectionRowControl
// .SignInAsync) needs to know *why* a save was refused — a name collision refuses the whole batch, and the operator
// staring at the row that lost deserves to know which name it collided with. Tuple alias, not a new record type: this
// stays entirely internal plumbing between the two files in this folder, the same "named tuple over a one-off type"
// call DepotMemorySource.BuildRegistrationPairs already makes for its own connection/registration pairing.
using DepotSaveResult = (bool Success, string? DuplicateName);

namespace Cockpit.Plugin.Depot.Ui;

/// <summary>
/// The plugin's settings view (opened from the gear in the plugin manager): a manageable list of Depot connection
/// rows (AC-243). Implements <see cref="IPluginSettingsView"/>, so the host renders the Save/Close footer and
/// <see cref="Save"/> persists on Save — the connection metadata to storage, and (AC-501) each connection's own
/// memory-source registration. Since AC-504 a connection's MCP server is offered per-project by
/// <see cref="DepotPlugin.GetMcpServers(string?, IReadOnlyList{string})"/> rather than pushed into the shared
/// registry here, so Save only has to reclaim a removed or renamed connection's <em>old</em> "Depot: &lt;old
/// name&gt;" registry entry — left behind by an install that ran before AC-504, or by
/// <see cref="DepotPlugin.Initialize"/>'s own reclaim missing a rename that happened after the app started — never
/// to add a new one. A connection removed or renamed here also has its old memory-source scheme reclaimed the same
/// save, so neither a stale MCP entry nor a stale picker row lingers until a restart.
/// </summary>
internal sealed class DepotSettingsControl : UserControl, IPluginSettingsView
{
    private readonly ICockpitHost _host;
    private readonly DepotSettings _settings;
    private readonly StackPanel _connectionsPanel;
    private readonly List<DepotConnectionRowControl> _rows = [];

    // Not readonly: this must become the new "before" state after every successful save, or _SyncMemorySources'
    // diff stays pinned to how this dialog looked when it opened. Sign-in (AC-499) can save the same open view many
    // times in a row — a rename followed by a rename back to the original name, each saved separately, would
    // otherwise diff the second save against the dialog's *opening* snapshot instead of the first save's result:
    // renaming back "matches" that original snapshot, so both the remove-old-scheme and add-new-scheme passes skip
    // it, leaving the intermediate scheme registered forever and the reverted one never re-added.
    private IReadOnlyList<DepotConnectionRegistration> _originalConnections;

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
        // through the exact same route this dialog's Save button uses — the MCP-registry reclaim and
        // _SyncMemorySources sync that live there run every time something signs in, not only on an explicit Save —
        // but the row also needs the duplicate-name detail Save()'s plain bool cannot carry, to tell the operator
        // which name collided instead of just that the save failed.
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

    public bool Save() => _SaveDetailed().Success;

    /// <summary>
    /// The real implementation behind <see cref="Save"/>, returning the duplicate name a collision was refused on
    /// (if any) so <see cref="DepotConnectionRowControl.SignInAsync"/> can say what went wrong instead of just that
    /// something did.
    /// </summary>
    private DepotSaveResult _SaveDetailed()
    {
        var candidates = _rows
            .Where(row => !row.IsBlank)
            .Select(row => row.ToRegistration())
            .Where(registration => !string.IsNullOrWhiteSpace(registration.Name) && !string.IsNullOrWhiteSpace(registration.Url))
            .ToList();

        // Two rows named alike (case-insensitively — the same comparer ProjectMemorySourceRegistry.Register uses
        // for scheme collisions, and McpServersViewModel.Save uses for the host dialog's own duplicate-name refusal)
        // would collide on the same "Depot: <name>" identity once GetMcpServers/BuildRegistrationPairs derive from
        // this list. Refuse the whole save rather than silently keep the first and drop the rest: dropping a row
        // here also rips its memory-source registration out of the registry (_SyncMemorySources below) and its
        // MCP-registry entry out of storage for whichever row loses the race — a save that discards data is worse
        // than a save that does nothing and says why.
        if (candidates
                .GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1) is { } duplicate)
        {
            return (false, duplicate.Key);
        }

        var keptNames = candidates.Select(registration => registration.McpServerName).ToHashSet(StringComparer.Ordinal);

        // A connection removed, or renamed (which changes McpServerName), leaves its old MCP-registry entry behind
        // unless reclaimed here — the same orphan-cleanup KubernetesSettingsControl.Save does for a cluster's
        // secret, applied to a registry entry instead. Only reclaiming, never adding: since AC-504 a connection's
        // server is offered per-project by DepotPlugin.GetMcpServers, not pushed into this registry.
        var orphanedNames = _originalConnections
            .Select(connection => connection.McpServerName)
            .Where(name => !keptNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Memory sources are in-memory registry entries, not disk-persisted like the MCP registry below, so the
        // sync is a direct synchronous call rather than a fire-and-forget task — there is no store round trip here
        // to race.
        _SyncMemorySources(candidates);

        _settings.Connections = candidates;

        // This save's result becomes the next save's "before" state — see the field's own remarks for why a
        // readonly opening snapshot broke a second save on the same open view.
        _originalConnections = candidates;

        // Fire-and-forget keeps Save() synchronous (the IPluginSettingsView contract); RemoveMcpServer's own
        // load-modify-save round trip against the shared store is the only I/O left here now that Save no longer
        // adds anything to that store.
        //
        // shortcut: fire-and-forget with no lock between this call and a second Save() landing before it completes;
        // ceiling = safe only while the reclaim removes the *old* name and a sign-in looks up the *new* one, and
        // Depot adds nothing else to this registry (AC-504) — the first time Save() here adds to this registry
        // again, this becomes a real lost update; upgrade = one sequential await-chain per call, per the
        // AddMcpServer/RemoveMcpServer race BuildTraps.md documents for AC-243, not a lock.
        _ = _ReclaimOrphanedMcpRegistryEntriesAsync(orphanedNames);

        return (true, null);
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
        //
        // _host is threaded through both calls (AC-503) so CheckReachability (and AC-502's ListLocationsAsync/
        // SignInAsync) are wired up on every registration this sync ever hands the host — the same as
        // DepotPlugin.Initialize's own startup pass.
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
        //
        // Plain == below, not a hand-rolled field comparison: ProjectMemorySourceRegistration's own equality
        // override (AC-502) already ignores ListLocationsAsync/SignInAsync/CheckReachability — every call to
        // BuildRegistrationPairs builds fresh closures over its own connection even when nothing changed, and two
        // such closures are never delegate-equal, which is exactly what that override exists to look past.
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

    private async Task _ReclaimOrphanedMcpRegistryEntriesAsync(IReadOnlyList<string> orphanedNames)
    {
        foreach (var orphanedName in orphanedNames)
        {
            await _host.RemoveMcpServer(orphanedName).ConfigureAwait(false);
        }
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Avalonia.Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
}
