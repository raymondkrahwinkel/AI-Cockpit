using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Depot;

// Depot as a project memory source (AC-165/166) with a settings view for connecting one or more instances
// (AC-243); each connection registers its own memory source (AC-501, see `DepotMemorySource`). Since AC-504
// each connection's MCP server is offered per-project rather than pushed into the shared registry.
public sealed class DepotPlugin : ICockpitPlugin, IPluginMcpProvider, IFirstStartPlugin
{
    // Kept from Initialize so GetMcpServers can read the current connection list each time the host asks, rather
    // than a snapshot taken once — the same reason YouTrackPlugin keeps its own settings reference.
    private DepotSettings? _settings;

    // Kept from Initialize (AC-503) so GetMcpServers below can hand BuildRegistrationPairs the host it needs
    // to wire CheckReachability, even though this call site only reads Registration.Scheme back out.
    private ICockpitHost? _host;

    // AC-1509: the host Initialize gave, once it ran; until then a first-start sign-in only writes storage and its backup target.
    private ICockpitHost? _initializedHost;

    // AC-1509: the Depot a first-start sign-in is signing in to, offered to the host before it is a connection.
    private volatile DepotConnectionRegistration? _pending;

    private readonly List<IDisposable> _handlers = [];

    private Inbox.DepotInbox? _inbox;
    private Notes.DepotNotes? _notes;
    private Events.DepotEvents? _events;
    private Notes.Live.DepotNoteLive? _noteLive;
    private ILogger? _logger;
    private Files.DepotFileSourceProvider? _fileSources;

    // A 100 MiB file on a slow link must not hit the default 100 second timeout.
    private readonly HttpClient _publishHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    // DEP-264: the grant Depot redeems an installation token with, accepted only from its own built-in client.
    private const string InstallTokenGrant = "urn:depot:grant-type:install_token";

    private const string DepotClientId = "depot";

    internal const string InvalidInstallTokenMessage = "That installation token is not valid any more. Make a new one in Depot under Account.";

    internal Events.DepotEvents Events => _events ?? throw new InvalidOperationException("Depot events are not initialized.");

    public PluginMetadata Metadata { get; } = new(
        Id: "depot",
        DisplayName: "Depot",
        Author: "Cockpit",
        Description: "Lets a project's memory live in a Depot project instead of a folder, and connects one or more Depot instances so a session can reach them.");

    public void ConfigureServices(IServiceCollection services)
    {
        // Register this plugin as the source of its own MCP servers (AC-504): the host's McpServerCatalog
        // asks each IPluginMcpProvider when assembling a session, rather than this plugin pushing servers
        // into the shared registry. Same instance the host initializes, so GetMcpServers sees Initialize's settings.
        services.AddSingleton<IPluginMcpProvider>(this);
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new DepotSettings(host.Storage);
        _settings = settings;
        _host = host;
        _initializedHost = host;

        foreach (var connection in settings.Connections)
        {
            host.AddStartupStep($"Connecting to {connection.Name}…", cancellationToken =>
                DepotProjectListingSource.For(host, connection).ListAsync(cancellationToken));
        }

        // AC-1665: signing in as someone else must not leave the previous account's project list to be shown.
        host.McpServerAccountChanged += (_, serverName) =>
        {
            foreach (var connection in settings.Connections.Where(candidate => candidate.McpServerName == serverName))
            {
                DepotProjectListingSource.Forget(host, connection);
                _RecheckLiveNotes(connection.Id);
            }

            // The Projects page keeps what the last read said, "sign in" included, until something tells it to read again.
            foreach (var source in host.SharedProjectSources.OfType<DepotSharedProjectSource>().Where(candidate => candidate.McpServerName == serverName))
            {
                source.RaiseAccountChanged();
            }
            _notes?.Refresh();
        };

        // AC-1394: the settings view and the global-toolbar shortcut to it moved to DepotUi.InitializeUi — this
        // channel action is what that view's own Save/Sign-in route now asks for instead of touching the
        // project-memory-source/shared-project-source/MCP registries directly.
        _handlers.Add(host.Channel.Handle(DepotChannel.SaveConnections, (payload, cancellationToken) =>
            _SaveConnectionsAsync(host, settings, payload, cancellationToken)));
        _handlers.Add(host.Channel.Handle(DepotChannel.ConnectWithInstallToken, (payload, cancellationToken) =>
            _ConnectWithInstallTokenAsync(host, settings, payload, cancellationToken)));
        _handlers.Add(host.Channel.Handle(DepotChannel.Branding, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotBrandingRequest>(DepotChannel.Json)
                ?? throw new InvalidOperationException("Depot branding request is missing.");
            var connection = settings.Connections.FirstOrDefault(candidate => candidate.Id == request.ConnectionId);
            var branding = connection is null
                ? DepotBrandingSnapshot.Default
                : await DepotBranding.For(host).GetAsync(connection, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new DepotBrandingAnswer(branding.Name, branding.Logo), DepotChannel.Json);
        }));

        // AC-1568: the file browser's Depot calls, one handler per action.
        var browser = new Browser.DepotBrowserService(host, () => settings.Connections);
        _fileSources = new Files.DepotFileSourceProvider(host, () => settings.Connections, () => _noteLive, () => _notes);
        _handlers.Add(host.AddFileSource(_fileSources));
        _HandleBrowser(host, DepotBrowserChannel.Projects, browser.ProjectsAsync);
        _HandleBrowser(host, DepotBrowserChannel.Tree, browser.TreeAsync);
        _HandleBrowser(host, DepotBrowserChannel.Read, browser.ReadAsync);
        _HandleBrowser(host, DepotBrowserChannel.DownloadUrl, browser.DownloadUrlAsync);
        _HandleBrowser(host, DepotBrowserChannel.Share, browser.ShareAsync);
        _HandleBrowser(host, DepotBrowserChannel.Unshare, browser.UnshareAsync);
        _HandleBrowser(host, DepotBrowserChannel.Upload, browser.UploadAsync);

        // AC-1574: publishing a local folder or file to a Depot project, one handler per action.
        var publisher = new Publishing.DepotPublishService(host, () => settings.Connections, settings, host.Cache, _publishHttp);
        _HandlePublish(host, DepotPublishChannel.List, publisher.ListAsync);
        _HandlePublish(host, DepotPublishChannel.Add, publisher.AddAsync);
        _HandlePublish(host, DepotPublishChannel.Remove, publisher.RemoveAsync);
        _HandlePublish(host, DepotPublishChannel.Plan, publisher.PlanAsync);
        _HandlePublish(host, DepotPublishChannel.Run, publisher.RunAsync);

        // AC-1579: the "New meeting note" dialog's Depot calls, one handler per action.
        var meetingNotes = new Notes.DepotMeetingNoteService(host, () => settings.Connections);
        _HandleMeetingNote(host, DepotMeetingNoteChannel.Options, meetingNotes.OptionsAsync);
        _HandleMeetingNote(host, DepotMeetingNoteChannel.Project, meetingNotes.ProjectAsync);
        _HandleMeetingNote(host, DepotMeetingNoteChannel.Check, meetingNotes.CheckAsync);
        _HandleMeetingNote(host, DepotMeetingNoteChannel.Create, meetingNotes.CreateAsync);
        _HandleMeetingNote(host, DepotMeetingNoteChannel.Link, meetingNotes.LinkAsync);

        // AC-1586: the Depot inbox, over REST and the inbox hub with a host-only token for depot.inbox alone.
        _inbox = new Inbox.DepotInbox(
            () => settings.Connections,
            connection => host.CreateAuthorizedHandler(connection.McpServerName, Inbox.DepotInbox.Scope),
            (connection, userId, onPush, onClosed) => new Inbox.SignalRDepotInboxHub(
                connection, userId, () => host.CreateAuthorizedHandler(connection.McpServerName, Inbox.DepotInbox.Scope), onPush, onClosed),
            page =>
            {
                host.Channel.Publish(DepotInboxChannel.Changed, JsonSerializer.SerializeToElement(page, DepotChannel.Json));
                _notes?.OnInboxPage(page);
            },
            _logger = host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Depot"));
        var inbox = _inbox;
        _handlers.Add(host.Channel.Handle(DepotInboxChannel.Unread, async (_, cancellationToken) =>
            JsonSerializer.SerializeToElement(await inbox.UnreadAsync(cancellationToken).ConfigureAwait(false), DepotChannel.Json)));
        _HandleInbox(host, DepotInboxChannel.List, inbox.ListAsync);
        _HandleInbox(host, DepotInboxChannel.MarkRead, inbox.MarkReadAsync);

        // AC-1897: live notes over /hubs/notes, with the same host-only token the events hub uses; a room's Y.Doc loads on the first note.
        _noteLive = new Notes.Live.DepotNoteLive(
            () => settings.Connections,
            connection => host.CreateAuthorizedHandler(connection.McpServerName),
            (connection, userId, onClosed) => new Notes.Live.SignalRDepotNotesHub(
                connection, userId, () => host.CreateAuthorizedHandler(connection.McpServerName), onClosed),
            clientId => new Notes.Live.JintNoteRoom(clientId),
            status => host.Channel.Publish(DepotNoteLiveChannel.Status, JsonSerializer.SerializeToElement(status, DepotChannel.Json)),
            _logger,
            async (connection, cancellationToken) =>
                await host.SignInMcpServerAsync(connection.McpServerName, cancellationToken).ConfigureAwait(false) == PluginMcpSignInOutcome.Authorized);
        var noteLive = _noteLive;
        _handlers.Add(host.Channel.Handle(DepotNoteLiveChannel.Open, (payload, _) =>
        {
            noteLive.Open(payload.Deserialize<DepotNoteLiveRequest>(DepotChannel.Json) ?? throw new ArgumentException("The request is empty.", nameof(payload)));
            return Task.FromResult(JsonSerializer.SerializeToElement(true, DepotChannel.Json));
        }));
        _handlers.Add(host.Channel.Handle(DepotNoteLiveChannel.Close, async (payload, _) =>
        {
            await noteLive.CloseAsync(payload.Deserialize<DepotNoteLiveRequest>(DepotChannel.Json) ?? throw new ArgumentException("The request is empty.", nameof(payload))).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(true, DepotChannel.Json);
        }));
        var notes = new Notes.DepotNotes(host, () => settings.Connections,
            page => host.Channel.Publish(DepotNotesChannel.Changed, JsonSerializer.SerializeToElement(page, DepotChannel.Json)), _logger);
        _notes = notes;
        _handlers.Add(host.Channel.Handle(DepotNotesChannel.Live, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotNotesRequest>(DepotChannel.Json)
                ?? throw new ArgumentException("The request names no Depot connection.", nameof(payload));
            return JsonSerializer.SerializeToElement(await notes.ListAsync(request, cancellationToken).ConfigureAwait(false), DepotChannel.Json);
        }));
        notes.Start();

        // AC-1581: a /n/{token} link to its place and role (DEP-534); the UI part's one route function asks here.
        // AC-1958: /cockpit/note-link is in Depot's bearer group too, so it takes the depot token, not depot.host.
        var noteLinks = new Notes.DepotNoteLinks(() => settings.Connections,
            connection => host.CreateAuthorizedHandler(connection.McpServerName), _logger);
        _handlers.Add(host.Channel.Handle(DepotNoteLinkChannel.Resolve, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotNoteLinkRequest>(DepotChannel.Json)
                ?? throw new ArgumentException("The request is empty.", nameof(payload));
            return JsonSerializer.SerializeToElement(await noteLinks.ResolveAsync(request, cancellationToken).ConfigureAwait(false), DepotChannel.Json);
        }));

        _events = new Events.DepotEvents(
            () => settings.Connections,
            connection => host.CreateAuthorizedHandler(connection.McpServerName),
            (connection, userId, onEvent, onReconnected, onClosed) => new Events.SignalRDepotEventHub(
                connection, userId, () => host.CreateAuthorizedHandler(connection.McpServerName), onEvent, onReconnected, onClosed),
            _logger);

        // AC-499: declared unconditionally, even with zero connections — fixes the doorless-dead-end bug where
        // zero connections meant no "Depot" option anywhere and no way to reach this plugin's settings.
        host.AddProjectMemorySourceFamily(new ProjectMemorySourceFamily(DepotMemorySource.Scheme, "Depot")
        {
            EmptyHint = "No Depot server configured yet.",
            ConfigureAsync = _ =>
            {
                host.Channel.Publish(DepotChannel.OpenSettings, JsonSerializer.SerializeToElement(true, DepotChannel.Json));
                return Task.CompletedTask;
            },
        });

        // No connections configured yet means no memory source at all (AC-501) — the row behaves exactly as it did
        // before this plugin existed, rather than always offering a fixed "Depot project" nothing points at yet.
        foreach (var registration in DepotMemorySource.BuildRegistrations(settings.Connections, host))
        {
            host.AddProjectMemorySource(registration);
        }

        // AC-245: one shared-project source per connection, so the Projects workspace can list what this connection
        // shares beside the local projects. Same zero-connections-means-nothing rule as the memory sources above.
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            host.AddSharedProjectSource(new DepotSharedProjectSource(pair.Connection, pair.Registration.Scheme, host, _events));
        }

        // AC-1508: each connection's account space is a place a cockpit backup can be kept.
        foreach (var connection in settings.Connections)
        {
            host.AddBackupTarget(new Backup.DepotBackupTarget(host, () => settings.Connections, connection.Id));
        }

        // AC-1832: one selected Depot carries settings. The generic host owns one carrier, so a newly selected
        // connection becomes the carrier on the next start; the carrier itself stops immediately when turned off.
        if (settings.Connections.FirstOrDefault(connection => connection.SynchroniseSettings) is { } selected)
        {
            host.AddSettingsCarrier(new Backup.DepotSettingsCarrier(host, () => settings.Connections, selected.Id).Registration(selected));
        }

        // AC-504: session delivery now asks via GetMcpServers instead of the shared registry, so reclaim what
        // AC-243 pushed there. Sequential, not fire-and-forget: RemoveMcpServer's load-modify-save has no
        // locking across calls, so parallel calls would race on the same stale snapshot and drop a connection.
        _ = _ReclaimPushedMcpServersSequentiallyAsync(host, settings.Connections);
    }

    // AC-1509: route C before Initialize, on the host the first-start phase gives. The settings are read here too, so the
    // host's sign-in finds the pending Depot through GetMcpServers before Initialize ran.
    public void InitializeFirstStart(ICockpitHost host)
    {
        var settings = new DepotSettings(host.Storage);
        _settings ??= settings;
        _host ??= host;

        var firstStart = new FirstStart.DepotFirstStart(
            host, settings, connection => _pending = connection, () => _initializedHost, () => _events);
        _handlers.Add(host.Channel.Handle(DepotChannel.FirstStartSignIn, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotFirstStartSignInRequest>(DepotChannel.Json)
                ?? throw new ArgumentException("The request names no Depot.", nameof(payload));
            return JsonSerializer.SerializeToElement(await firstStart.SignInAsync(request, cancellationToken).ConfigureAwait(false), DepotChannel.Json);
        }));
        _handlers.Add(host.Channel.Handle(DepotChannel.FirstStartManifest, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotFirstStartManifestRequest>(DepotChannel.Json)
                ?? throw new ArgumentException("The request names no organization.", nameof(payload));
            return JsonSerializer.SerializeToElement(await firstStart.ReadManifestAsync(request, cancellationToken).ConfigureAwait(false), DepotChannel.Json);
        }));
    }

    private void _HandleBrowser<T>(
        ICockpitHost host, string action, Func<DepotBrowserRequest, CancellationToken, Task<DepotBrowserAnswer<T>>> handler) =>
        _handlers.Add(host.Channel.Handle(action, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotBrowserRequest>(DepotChannel.Json)
                ?? throw new ArgumentException("The request is empty.", nameof(payload));
            return JsonSerializer.SerializeToElement(await handler(request, cancellationToken).ConfigureAwait(false), DepotChannel.Json);
        }));

    private void _RecheckLiveNotes(string connectionId)
    {
        try
        {
            _noteLive?.AccountChanged(connectionId);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Checking the live notes of a changed Depot account failed.");
        }
    }

    private void _HandleInbox(ICockpitHost host, string action, Func<DepotInboxRequest, CancellationToken, Task<DepotInboxPage>> handler) =>
        _handlers.Add(host.Channel.Handle(action, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotInboxRequest>(DepotChannel.Json)
                ?? throw new ArgumentException("The request is empty.", nameof(payload));
            return JsonSerializer.SerializeToElement(await handler(request, cancellationToken).ConfigureAwait(false), DepotChannel.Json);
        }));

    private void _HandleMeetingNote<T>(
        ICockpitHost host, string action, Func<DepotMeetingNoteRequest, CancellationToken, Task<DepotBrowserAnswer<T>>> handler) =>
        _handlers.Add(host.Channel.Handle(action, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotMeetingNoteRequest>(DepotChannel.Json)
                ?? throw new ArgumentException("The request is empty.", nameof(payload));
            return JsonSerializer.SerializeToElement(await handler(request, cancellationToken).ConfigureAwait(false), DepotChannel.Json);
        }));

    private void _HandlePublish<T>(
        ICockpitHost host, string action, Func<DepotPublishRequest, CancellationToken, Task<DepotBrowserAnswer<T>>> handler) =>
        _handlers.Add(host.Channel.Handle(action, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<DepotPublishRequest>(DepotChannel.Json)
                ?? throw new ArgumentException("The request is empty.", nameof(payload));
            return JsonSerializer.SerializeToElement(await handler(request, cancellationToken).ConfigureAwait(false), DepotChannel.Json);
        }));

    private static async Task _ReclaimPushedMcpServersSequentiallyAsync(ICockpitHost host, IReadOnlyList<DepotConnectionRegistration> connections)
    {
        foreach (var connection in connections)
        {
            await host.RemoveMcpServer(connection.McpServerName).ConfigureAwait(false);
        }
    }

    // AC-1394: the settings view's own Save/Sign-in route, moved here from Ui.DepotSettingsControl — that view
    // now only validates and asks for this; the write itself happens here, not there. `settings.Connections` is
    // read fresh (DepotSettings.Connections' own doc) before anything writes it, so `before` is genuinely the
    // pre-save state the diff below needs — writing it from the UI first, ahead of this call, would have this
    // read see its own "after" and turn the whole diff into a no-op.
    private async Task<JsonElement> _SaveConnectionsAsync(
        ICockpitHost host, DepotSettings settings, JsonElement payload, CancellationToken cancellationToken)
    {
        var request = payload.Deserialize<DepotSaveConnectionsRequest>(DepotChannel.Json)
            ?? throw new ArgumentException("The request names no connections.", nameof(payload));

        await WriteConnectionsAsync(
            host,
            settings,
            [.. request.Connections.Select(connection => new DepotConnectionRegistration(connection.Id, connection.Name, connection.Url, connection.SynchroniseSettings))],
            cancellationToken, _events, () => _fileSources?.NotifyChanged()).ConfigureAwait(false);
        _notes?.Refresh();
        return JsonSerializer.SerializeToElement(new DepotSaveConnectionsAnswer(true), DepotChannel.Json);
    }

    // AC-1511: a connection signed in with an installation token instead of a browser — the only way on a server.
    // The connection is written first, since the host finds the server to redeem at under its registered name; a
    // token Depot refuses puts the list back as it was, so it never leaves a connection that cannot sign in.
    private async Task<JsonElement> _ConnectWithInstallTokenAsync(
        ICockpitHost host, DepotSettings settings, JsonElement payload, CancellationToken cancellationToken)
    {
        var request = payload.Deserialize<DepotInstallTokenRequest>(DepotChannel.Json)
            ?? throw new ArgumentException("The request names no connection.", nameof(payload));
        var name = request.Connection.Name.Trim();
        var url = DepotUrlNormalizer.Normalize(request.Connection.Url);
        if (name.Length == 0
            || !Uri.TryCreate(url, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(request.Token))
        {
            return _InstallTokenAnswer(false, "Enter a name, the Depot address and an installation token.");
        }

        var before = settings.Connections;
        var existing = before.FirstOrDefault(connection => !string.IsNullOrEmpty(request.Connection.Id) && connection.Id == request.Connection.Id)
            ?? before.FirstOrDefault(connection => string.Equals(connection.Url, url, StringComparison.OrdinalIgnoreCase));
        if (before.Any(connection => connection != existing && string.Equals(connection.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return _InstallTokenAnswer(false, $"\"{name}\" is already the name of another connection. Choose another name.");
        }

        var id = existing?.Id ?? (string.IsNullOrWhiteSpace(request.Connection.Id) ? Guid.NewGuid().ToString("n") : request.Connection.Id);
        var connection = new DepotConnectionRegistration(id, name, url, request.Connection.SynchroniseSettings);
        await WriteConnectionsAsync(
            host,
            settings,
            existing is null ? [.. before, connection] : [.. before.Select(candidate => candidate.Id == existing.Id ? connection : candidate)],
            cancellationToken, _events, () => _fileSources?.NotifyChanged()).ConfigureAwait(false);

        var outcome = await host.SignInMcpServerWithGrantAsync(
            connection.McpServerName,
            InstallTokenGrant,
            new Dictionary<string, string> { ["install_token"] = request.Token, ["client_id"] = DepotClientId },
            cancellationToken).ConfigureAwait(false);
        if (outcome == PluginMcpSignInOutcome.Authorized)
        {
            return _InstallTokenAnswer(true, "Signed in.");
        }

        await WriteConnectionsAsync(host, settings, before, cancellationToken, _events, () => _fileSources?.NotifyChanged()).ConfigureAwait(false);
        return _InstallTokenAnswer(false, outcome switch
        {
            PluginMcpSignInOutcome.Declined => InvalidInstallTokenMessage,
            PluginMcpSignInOutcome.Unreachable => $"Couldn't reach {url} to sign in. Check the address and try again.",
            _ => "Signing in with an installation token isn't available on this Cockpit.",
        });
    }

    private static JsonElement _InstallTokenAnswer(bool success, string message) =>
        JsonSerializer.SerializeToElement(new DepotInstallTokenAnswer(success, message), DepotChannel.Json);

    // The write both channel actions share: storage first, then the registries diffed against what was there.
    internal static async Task WriteConnectionsAsync(
        ICockpitHost host, DepotSettings settings, IReadOnlyList<DepotConnectionRegistration> after,
        CancellationToken cancellationToken, Events.DepotEvents? events = null, Action? filesChanged = null)
    {
        var before = settings.Connections;

        // The connection list itself is a plain storage write — the same slice the UI part's own Storage shares
        // (ICockpitUiHost's own remarks) — done here, before the sync below, so a row's Sign-in (which runs right
        // after this call returns) finds its connection already in storage under its registered MCP server name.
        settings.Connections = after;
        filesChanged?.Invoke();

        var keptNames = after.Select(connection => connection.McpServerName).ToHashSet(StringComparer.Ordinal);
        var orphanedNames = before
            .Select(connection => connection.McpServerName)
            .Where(name => !keptNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _SyncMemorySources(host, before, after);
        _SyncSharedProjectSources(host, before, after, events);
        _SyncBackupTargets(host, settings, before, after);

        foreach (var orphanedName in orphanedNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.RemoveMcpServer(orphanedName).ConfigureAwait(false);
        }
    }

    // AC-1508: a target reads its connection by id on every call, so a rename needs nothing; only additions and removals do.
    private static void _SyncBackupTargets(
        ICockpitHost host, DepotSettings settings, IReadOnlyList<DepotConnectionRegistration> before, IReadOnlyList<DepotConnectionRegistration> after)
    {
        var kept = after.Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in before.Where(connection => !kept.Contains(connection.Id)))
        {
            host.RemoveBackupTarget(Backup.DepotBackupTarget.IdFor(gone.Id));
        }

        var had = before.Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var added in after.Where(connection => !had.Contains(connection.Id)))
        {
            host.AddBackupTarget(new Backup.DepotBackupTarget(host, () => settings.Connections, added.Id));
        }
    }

    // The live-refresh half of AC-501: a connection's memory source used to be registered once at Initialize and
    // never touched again, so an operator adding, renaming or removing a connection through the settings view saw
    // no effect until an app restart. Diffed by connection Id (not by list position, which a removal ahead of a
    // connection would shift out from under it).
    private static void _SyncMemorySources(ICockpitHost host, IReadOnlyList<DepotConnectionRegistration> before, IReadOnlyList<DepotConnectionRegistration> after)
    {
        // A plain Dictionary from BuildRegistrationPairs' own Connection.Id would throw on a duplicate key if
        // storage ever held two connections under the same id (corrupted or hand-edited settings) — building it by
        // hand keeps a duplicate merely overwriting the earlier entry instead of crashing this handler.
        var beforeById = new Dictionary<string, ProjectMemorySourceRegistration>(StringComparer.Ordinal);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(before, host))
        {
            beforeById[pair.Connection.Id] = pair.Registration;
        }

        var afterPairs = DepotMemorySource.BuildRegistrationPairs(after, host);
        var afterById = new Dictionary<string, ProjectMemorySourceRegistration>(StringComparer.Ordinal);
        foreach (var pair in afterPairs)
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
        foreach (var (id, oldRegistration) in beforeById)
        {
            if (!afterById.TryGetValue(id, out var stillCurrent) || stillCurrent != oldRegistration)
            {
                host.RemoveProjectMemorySource(oldRegistration.Scheme);
            }
        }

        foreach (var (connection, newRegistration) in afterPairs)
        {
            if (beforeById.TryGetValue(connection.Id, out var oldRegistration) && oldRegistration == newRegistration)
            {
                // Unchanged: re-adding it would only hit Register's "scheme already taken" refusal, since this very
                // content is already the one registered.
                continue;
            }

            host.AddProjectMemorySource(newRegistration);
        }
    }

    // AC-245: the same before/after diff-by-connection-Id _SyncMemorySources runs, applied to shared-project
    // sources instead. A separate pass rather than folded into that method: the two registries are independent
    // (ISharedProjectSourceRegistry, not IProjectMemorySourceRegistry) and keeping the diff loops apart means a
    // future change to one shape never has to reason about the other's equality rules.
    private static void _SyncSharedProjectSources(
        ICockpitHost host,
        IReadOnlyList<DepotConnectionRegistration> before,
        IReadOnlyList<DepotConnectionRegistration> after,
        Events.DepotEvents? events)
    {
        // Diffed by (Key, Connection) rather than by Key alone: DepotSharedProjectSource is a plain class with no
        // value equality of its own, and the first connection's Key is always the bare "depot" scheme regardless of
        // its name — a rename of that one connection would leave Key identical before and after, so comparing Key
        // alone would skip the refresh and leave a stale SourceName registered until a restart. Connection (a
        // record) already carries the value equality that catches a name/URL change; Key alone still matters for
        // the two-connections-trading-schemes ordering case _SyncMemorySources documents above.
        var beforeById = new Dictionary<string, (DepotConnectionRegistration Connection, string Key)>(StringComparer.Ordinal);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(before, host))
        {
            beforeById[pair.Connection.Id] = (pair.Connection, pair.Registration.Scheme);
        }

        var afterById = new Dictionary<string, (DepotConnectionRegistration Connection, string Key)>(StringComparer.Ordinal);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(after, host))
        {
            afterById[pair.Connection.Id] = (pair.Connection, pair.Registration.Scheme);
        }

        // Retire every stale key first, same ordering reason _SyncMemorySources documents: two connections trading
        // keys in one save must not have the second's Add refused by the first's still-registered old key.
        foreach (var (id, oldEntry) in beforeById)
        {
            if (!afterById.TryGetValue(id, out var current) || current.Key != oldEntry.Key || current.Connection != oldEntry.Connection)
            {
                host.RemoveSharedProjectSource(oldEntry.Key);
            }
        }

        foreach (var (id, entry) in afterById)
        {
            if (beforeById.TryGetValue(id, out var oldEntry) && oldEntry.Key == entry.Key && oldEntry.Connection == entry.Connection)
            {
                // Unchanged: re-adding would only hit Register's "key already taken" refusal.
                continue;
            }

            host.AddSharedProjectSource(events is null
                ? new DepotSharedProjectSource(entry.Connection, entry.Key, host)
                : new DepotSharedProjectSource(entry.Connection, entry.Key, host, events));
        }
    }

    // Every connection this plugin has configured (AC-504), unscoped by project. Session delivery never reaches
    // this overload — the catalog always calls the scoped overload below — but the host falls back to it for
    // OAuth sign-in from this plugin's own settings view, which has no project to scope a call by.
    public IReadOnlyList<McpServerContribution> GetMcpServers() =>
        _settings is null ? [] : [.. _settings.Connections.Append(_pending).OfType<DepotConnectionRegistration>().Select(_ContributionFor)];

    // The connection(s) whose memory-source scheme is among `projectMemorySchemes` (AC-504). `projectId`
    // is unused: the schemes already say which of this plugin's connections the project points at.
    public IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId, IReadOnlyList<string> projectMemorySchemes)
    {
        if (_settings is null || _host is null || projectMemorySchemes.Count == 0)
        {
            return [];
        }

        var schemes = new HashSet<string>(projectMemorySchemes, StringComparer.OrdinalIgnoreCase);
        return DepotMemorySource.BuildRegistrationPairs(_settings.Connections, _host)
            .Where(pair => schemes.Contains(pair.Registration.Scheme))
            .Select(pair => _ContributionFor(pair.Connection))
            .ToList();
    }

    // AC-499: connection.Url is already the normalized base by the time it gets here — normalized on save and
    // migrated on load. This only appends /mcp; it must not call DepotUrlNormalizer.Normalize again, since
    // that is no longer safe to repeat (a base whose path genuinely ends in /mcp would lose that segment).
    private static McpServerContribution _ContributionFor(DepotConnectionRegistration connection)
    {
        return new(Name: connection.McpServerName, Url: $"{connection.Url}/mcp")
        {
            // AC-403: this connection's own id, so the host files its OAuth token under something the operator
            // cannot edit — the Name is free-text, and renaming used to strand sign-in under the old name.
            Id = connection.Id,
            IncludeAgentName = true,
            // Scheme+host+port of the stored base URL: Depot's protected-resource metadata names the origin as
            // its authorization_servers entry, not a subpath. Falls back to the base URL if unparseable (defensive only).
            OAuthAuthority = DepotUrlNormalizer.Origin(connection.Url) ?? connection.Url,
            // AC-1586/AC-1647: Depot's built-in client may request the host-only inbox and Cockpit API scopes;
            // neither rides on a token handed to a session.
            OAuthClientId = DepotClientId,
            OAuthScopes = $"depot {Inbox.DepotInbox.Scope} depot.host offline_access",
            OAuthHostOnlyScopes = $"{Inbox.DepotInbox.Scope} depot.host",
        };
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _publishHttp.Dispose();
        _ = _notes?.DisposeAsync().AsTask().ContinueWith(
            closing => _logger?.LogWarning(closing.Exception?.GetBaseException(), "Closing Depot notes failed."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        // ICockpitPlugin has only a synchronous Dispose; closing the hubs is started, not waited on, so a shutdown on the UI
        // thread never blocks on a socket. Each service cancels its own retries first, so nothing reopens one.
        _ = _inbox?.DisposeAsync().AsTask().ContinueWith(
            closing => _logger?.LogWarning(closing.Exception?.GetBaseException(), "Closing the Depot inbox failed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        _ = _noteLive?.DisposeAsync().AsTask().ContinueWith(
            closing => _logger?.LogWarning(closing.Exception?.GetBaseException(), "Closing the live notes failed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        _ = _events?.DisposeAsync().AsTask().ContinueWith(
            closing => _logger?.LogWarning(closing.Exception?.GetBaseException(), "Closing Depot events failed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        _handlers.Clear();
    }
}
