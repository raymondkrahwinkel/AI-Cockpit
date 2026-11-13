using System.Globalization;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Vault;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// AC-2347: what Options › Vault and the status-bar meter show. Names, references and times only; no value is read here.
internal sealed class VaultOverviewViewModel : ObservableObject
{
    internal const string AllProviders = "All providers";
    internal const string AllEvents = "All events";

    private readonly Dictionary<string, VaultProviderInfo> _providers = new(StringComparer.Ordinal);
    private IVaultOverviewService? _hub;
    private ISessionRegistry? _sessions;
    private IProjectStore? _projects;
    private Action<string>? _goToSession;
    private Action? _openEncryption;
    private VaultAvailability _availability = VaultAvailability.EncryptionOff;
    private IReadOnlyList<VaultProviderRow> _providerRows = [];
    private IReadOnlyList<VaultPermissionRow> _permissions = [];
    private IReadOnlyList<VaultAuditRow> _allAudit = [];
    private IReadOnlyList<VaultAuditRow> _audit = [];
    private IReadOnlyList<VaultSessionGroup> _groups = [];
    private string _auditProvider = AllProviders;
    private string _auditEvent = AllEvents;
    private int _openRequests;
    private bool _isRefreshing;
    private bool _isRefreshPending;

    public VaultAvailability Availability
    {
        get => _availability;
        private set
        {
            if (SetProperty(ref _availability, value))
            {
                OnPropertyChanged(nameof(IsBlocked));
                AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsBlocked => _availability != VaultAvailability.Available;

    public event EventHandler? AvailabilityChanged;

    public IReadOnlyList<VaultProviderRow> Providers => _providerRows;

    public IReadOnlyList<VaultPermissionRow> Permissions => _permissions;

    public bool HasNoProviders => _providerRows.Count == 0;

    public bool HasNoPermissions => _permissions.Count == 0;

    public bool HasNoAudit => _audit.Count == 0;

    public string PermissionsCount => _permissions.Count.ToString(CultureInfo.InvariantCulture);

    public IReadOnlyList<VaultAuditRow> Audit => _audit;

    public IReadOnlyList<string> AuditProviderChoices { get; private set; } = [AllProviders];

    public IReadOnlyList<string> AuditEventChoices { get; } =
        [AllEvents, "Requested", "Allowed", "Denied", "Injected", "Masked", "Revoked"];

    public string AuditProvider
    {
        get => _auditProvider;
        set
        {
            if (SetProperty(ref _auditProvider, value ?? AllProviders))
            {
                _FilterAudit();
            }
        }
    }

    public string AuditEvent
    {
        get => _auditEvent;
        set
        {
            if (SetProperty(ref _auditEvent, value ?? AllEvents))
            {
                _FilterAudit();
            }
        }
    }

    // The sessions holding something, for the status-bar panel.
    public IReadOnlyList<VaultSessionGroup> Groups => _groups;

    public int SecretsInUse => _groups.Sum(group => group.Items.Count);

    public bool HasSecretsInUse => SecretsInUse > 0;

    public string MeterText => $"Secrets in use: {SecretsInUse}";

    public string PanelSummary => $"{SecretsInUse} {(SecretsInUse == 1 ? "item" : "items")} in {_groups.Count} {(_groups.Count == 1 ? "session" : "sessions")}";

    // Sessions with an open vault card, set by the window; the meter shows the amber chip while it is above nought.
    public int OpenRequests
    {
        get => _openRequests;
        set
        {
            if (SetProperty(ref _openRequests, value))
            {
                OnPropertyChanged(nameof(HasOpenRequests));
            }
        }
    }

    public bool HasOpenRequests => _openRequests > 0;

    public event EventHandler? Changed;

    // Opens Options › Vault; set by the window.
    public Action? OpenOverview { get; set; }

    // The live graph: the hub, the sessions it grants to and the two things only the window can do.
    internal void Attach(IVaultOverviewService hub, ISessionRegistry sessions, IProjectStore? projects, Action<string> goToSession, Action openEncryption, SecurityOptionsViewModel security)
    {
        (_hub, _sessions, _projects, _goToSession, _openEncryption) = (hub, sessions, projects, goToSession, openEncryption);
        hub.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(providers: false));
        hub.AvailabilityChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(providers: true));
        sessions.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(providers: false));
        // Turning encryption on or off in Options › Security reopens or closes the vault without a restart.
        security.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SecurityOptionsViewModel.IsEncrypted))
            {
                Dispatcher.UIThread.Post(() => _ = RefreshAsync(providers: true));
            }
        };
        _ = RefreshAsync(providers: true);
    }

    public void OpenEncryptionSettings() => _openEncryption?.Invoke();

    public void GoToSession(string paneId) => _goToSession?.Invoke(paneId);

    // `providers` asks every provider again (a network call for some); the grants and the audit are always read again.
    public async Task RefreshAsync(bool providers)
    {
        if (_hub is null)
        {
            return;
        }

        if (_isRefreshing)
        {
            _isRefreshPending = true;
            return;
        }

        _isRefreshing = true;
        try
        {
            do
            {
                _isRefreshPending = false;
                await _ReadAsync(_hub, providers).ConfigureAwait(true);
                providers = false;
            }
            while (_isRefreshPending);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task _ReadAsync(IVaultOverviewService hub, bool providers)
    {
        Availability = await hub.EvaluateAsync().ConfigureAwait(true);
        if (IsBlocked)
        {
            _providers.Clear();
            _Apply([], [], new Dictionary<string, (string, string?)>(), new Dictionary<string, string>());
            return;
        }

        if (providers)
        {
            await _ReadProvidersAsync(hub).ConfigureAwait(true);
        }

        var live = new Dictionary<string, (string Title, string? ProjectId)>(StringComparer.Ordinal);
        foreach (var session in (_sessions?.All ?? []).Concat(_sessions?.Assistant is { } assistant ? [assistant] : []))
        {
            live[session.PaneId] = (session.Title, session.ProjectId);
        }

        var projectNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_projects is not null)
        {
            var settings = await _projects.LoadAsync().ConfigureAwait(true);
            foreach (var id in live.Values.Select(session => session.ProjectId).OfType<string>().Distinct())
            {
                projectNames[id] = settings.Find(id)?.Name ?? id;
            }
        }

        var grants = await hub.ListGrantsAsync().ConfigureAwait(true);
        var audit = await hub.ReadAuditAsync().ConfigureAwait(true);
        _Apply(grants, audit, live.ToDictionary(pair => pair.Key, pair => (pair.Value.Title, pair.Value.ProjectId)), projectNames);
    }

    private async Task _ReadProvidersAsync(IVaultOverviewService hub)
    {
        _providers.Clear();
        foreach (var provider in await hub.ReadProvidersAsync(CancellationToken.None).ConfigureAwait(true))
        {
            _providers[provider.Id] = provider;
        }
    }

    // Builds every view of the same facts: the Options tabs and the status-bar panel.
    private void _Apply(
        IReadOnlyList<VaultGrantInfo> grants,
        IReadOnlyList<VaultAuditEvent> audit,
        IReadOnlyDictionary<string, (string Title, string? ProjectId)> live,
        IReadOnlyDictionary<string, string> projectNames)
    {
        var livePanes = live.Select(pair => (PaneId: pair.Key, pair.Value.Title, pair.Value.ProjectId)).ToList();
        string ProjectName(string id) => projectNames.GetValueOrDefault(id) ?? id;
        string ProviderName(string reference) => _providers.Values.FirstOrDefault(provider => provider.ReferenceScheme == reference.Split(':', 2)[0])?.DisplayName ?? reference.Split(':', 2)[0];
        string ItemName(string reference) => _providers.Values.SelectMany(entry => entry.Items).FirstOrDefault(item => item.Reference == reference)?.DisplayName ?? reference;
        string SessionName(string? paneId) => paneId is null ? string.Empty : live.TryGetValue(paneId, out var found) ? found.Title : "Ended session";
        DateTimeOffset? Latest(string kind, string name, IReadOnlySet<string> panes) =>
            audit.Where(entry => entry.Kind == kind && entry.Item is { } item && item.Split(", ").Contains(name) && (entry.Session is { } pane && panes.Contains(pane)))
                .Select(entry => (DateTimeOffset?)entry.At).DefaultIfEmpty(null).Max();

        var rows = new List<VaultPermissionRow>();
        foreach (var grant in grants.OrderBy(grant => grant.Scope == IVaultOverviewService.ProjectScope).ThenBy(grant => grant.Owner, StringComparer.Ordinal))
        {
            var name = ItemName(grant.Reference);
            var isProject = grant.Scope == IVaultOverviewService.ProjectScope;
            var panes = isProject
                ? livePanes.Where(pane => pane.ProjectId == grant.Owner).Select(pane => pane.PaneId).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>([grant.Owner], StringComparer.Ordinal);
            var who = isProject ? $"Project {ProjectName(grant.Owner)}" : SessionName(grant.Owner) + (live.GetValueOrDefault(grant.Owner).ProjectId is { } project ? $" · {ProjectName(project)}" : string.Empty);
            Func<Task>? revoke = _hub is null ? null : isProject
                ? () => _hub.RevokeProjectAsync(grant.Owner, grant.Reference)
                : () => _hub.RevokeSessionAsync(grant.Owner, grant.Reference);
            rows.Add(new VaultPermissionRow(name, ProviderName(grant.Reference), who, isProject ? "every session" : null,
                isProject ? "Always" : "This session", isProject, _When(Latest("Allowed", name, panes)), _When(Latest("Injected", name, panes)), revoke));
        }

        var groups = new List<VaultSessionGroup>();
        foreach (var (paneId, title, projectId) in livePanes)
        {
            var held = grants.Where(grant => (grant.Scope == IVaultOverviewService.SessionScope && grant.Owner == paneId) || (grant.Scope == IVaultOverviewService.ProjectScope && projectId is not null && grant.Owner == projectId))
                .GroupBy(grant => grant.Reference, StringComparer.Ordinal).ToList();
            if (held.Count == 0)
            {
                continue;
            }

            var items = held.Select(group =>
            {
                var name = ItemName(group.Key);
                var bySession = group.Any(grant => grant.Scope == IVaultOverviewService.SessionScope);
                var since = _When(Latest("Allowed", name, new HashSet<string>([paneId])) ?? Latest("Injected", name, new HashSet<string>([paneId])));
                var scope = bySession ? "allowed for this session" : $"always allowed for {ProjectName(projectId!)}";
                return new VaultHeldItem(name, ProviderName(group.Key), $"{ProviderName(group.Key)} · since {since} · {scope}", bySession,
                    _hub is null ? null : () => _hub.RevokeSessionAsync(paneId, group.Key),
                    _hub is null ? null : () => _hub.RevokeEverywhereAsync(group.Key));
            }).ToList();
            groups.Add(new VaultSessionGroup(paneId, title, projectId is null ? string.Empty : ProjectName(projectId), items));
        }

        var inUse = rows.GroupBy(row => row.Item).ToDictionary(group => group.Key, group => (Count: group.Count(), Session: group.Any(row => !row.IsAlways)));
        var providerRows = _providers.Values.Select(entry =>
        {
            var status = entry.Status;
            var detail = status.State switch
            {
                VaultProviderState.Connected => string.Join(" · ", new[] { status.Account, status.Scope }.Where(part => !string.IsNullOrEmpty(part))),
                VaultProviderState.Error => status.Error ?? "The provider reported an error.",
                _ => "Not connected. Connect it on its own page under Plugins.",
            };
            var items = entry.Items.Select(item => new VaultItemRow(item.DisplayName, item.Reference,
                inUse.TryGetValue(item.DisplayName, out var use) ? $"{use.Count}" + (use.Session ? " · in use" : string.Empty) : "none", inUse.ContainsKey(item.DisplayName))).ToList();
            return new VaultProviderRow(entry.DisplayName, detail, status.State switch { VaultProviderState.Connected => "Connected", VaultProviderState.Error => "Error", _ => "Not connected" }, status.State, items);
        }).ToList();

        string ProviderOfItem(string? item) => string.Join(", ", (item ?? string.Empty).Split(", ").Select(part =>
            _providers.Values.FirstOrDefault(entry => entry.Items.Any(candidate => candidate.DisplayName == part || candidate.Reference == part))?.DisplayName ?? string.Empty).Where(name => name.Length > 0).Distinct());
        var auditRows = audit.OrderByDescending(entry => entry.At).Select(entry => new VaultAuditRow(
            _When(entry.At), entry.Kind, entry.Item ?? string.Empty, SessionName(entry.Session), _AuditDetail(entry), ProviderOfItem(entry.Item))).ToList();

        _providerRows = providerRows;
        _permissions = rows;
        _groups = groups;
        _allAudit = auditRows;
        AuditProviderChoices = [AllProviders, .. providerRows.Select(row => row.Name)];
        _FilterAudit();
        _Notify();
    }

    private void _Notify()
    {
        foreach (var property in new[] { nameof(Providers), nameof(HasNoProviders), nameof(Permissions), nameof(HasNoPermissions), nameof(PermissionsCount), nameof(Groups), nameof(SecretsInUse), nameof(HasSecretsInUse), nameof(MeterText), nameof(PanelSummary), nameof(AuditProviderChoices) })
        {
            OnPropertyChanged(property);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string _AuditDetail(VaultAuditEvent entry) => entry.Kind switch
    {
        "Masked" => entry.Count is { } count ? $"{count} {(count == 1 ? "occurrence" : "occurrences")} in the output" : string.Empty,
        "VaultWiped" => IVaultOverviewService.WipedMessage,
        _ => string.Empty,
    };

    private void _FilterAudit()
    {
        _audit = _allAudit
            .Where(row => (_auditProvider == AllProviders || row.Provider.Split(", ").Contains(_auditProvider)) && (_auditEvent == AllEvents || row.Event == _auditEvent))
            .ToList();
        OnPropertyChanged(nameof(Audit));
        OnPropertyChanged(nameof(HasNoAudit));
    }

    // Today as a time, earlier as a date.
    private static string _When(DateTimeOffset? at)
    {
        if (at is not { } value)
        {
            return "—";
        }

        var local = value.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : local.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    // The scenes show made-up facts through the same views.
    internal void Apply(
        VaultAvailability availability,
        IReadOnlyList<VaultProviderRow> providers,
        IReadOnlyList<VaultPermissionRow> permissions,
        IReadOnlyList<VaultAuditRow> audit,
        IReadOnlyList<VaultSessionGroup> groups)
    {
        Availability = availability;
        _providerRows = providers;
        _permissions = permissions;
        _groups = groups;
        _allAudit = audit;
        AuditProviderChoices = [AllProviders, .. providers.Select(row => row.Name)];
        _FilterAudit();
        _Notify();
    }
}

internal sealed record VaultProviderRow(string Name, string Detail, string StatusText, VaultProviderState State, IReadOnlyList<VaultItemRow> Items)
{
    public bool IsConnected => State == VaultProviderState.Connected;

    public bool HasItems => Items.Count > 0;
}

internal sealed record VaultItemRow(string Name, string Reference, string Permissions, bool InUse);

internal sealed record VaultPermissionRow(
    string Item, string Provider, string Who, string? WhoNote, string Scope, bool IsAlways, string Granted, string LastUsed, Func<Task>? Revoke);

internal sealed record VaultAuditRow(string Time, string Event, string Item, string Session, string Detail, string Provider);

internal sealed record VaultSessionGroup(string PaneId, string Title, string Project, IReadOnlyList<VaultHeldItem> Items);

// `RevokeSession` is what "Revoke for this session" does; an item held only through a project has nothing to revoke for one session.
internal sealed record VaultHeldItem(string Name, string Provider, string Detail, bool HeldBySession, Func<Task>? RevokeSession, Func<Task>? RevokeEverywhere);
