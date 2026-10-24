using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

// AC-1446 (F5.6b): the Options dialog opened on a server, its Connect keys and Audit log pages. Every change goes
// through the server's IConnectKeyAdministration, which audits it with this connection's key; a change updates the
// list here rather than reading it again, so the audit holds the change and not a read after it.
public sealed partial class ServerAdminViewModel : ObservableObject
{
    private const int AuditPage = 200;

    private readonly IConnectKeyAdministration _admin;
    private readonly IPluginAdministration? _plugins;
    private readonly ISessionDialogService? _dialogService;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _profiles;
    private readonly IReadOnlyList<NodeProjectChoice> _projects;
    private readonly IServerProjects? _serverProjects;
    private readonly Func<int>? _runningSessions;
    private readonly ILogger? _logger;
    private ConnectKeyRowViewModel? _editing;

    public ServerAdminViewModel(string server, string keyLabel, IConnectKeyAdministration admin, IReadOnlyList<string> profiles, IReadOnlyList<NodeProjectChoice> projects, IPluginAdministration? plugins = null, IServerProjects? serverProjects = null, TimeProvider? time = null, ISessionDialogService? dialogService = null, Func<int>? runningSessions = null, ILogger? logger = null)
    {
        _logger = logger;
        _runningSessions = runningSessions;
        _serverProjects = serverProjects;
        Server = server;
        KeyLabel = keyLabel;
        _admin = admin;
        _time = time ?? TimeProvider.System;
        _profiles = profiles;
        _projects = [.. projects.Where(project => project.Id is not null)];
        _plugins = plugins;
        _dialogService = dialogService;
    }

    public string Server { get; }

    public string KeyLabel { get; }

    public string Title => $"Options · {Server}";

    public string Chip => $"administering the server · key {KeyLabel} (admin)";

    // AC-1473: the Profiles page, when this dialog was given the server's profiles.
    public ServerProfilesViewModel? Profiles { get; init; }

    // AC-1475: the Assistant page, when this dialog was given the server's assistant.
    public ServerAssistantViewModel? Assistant { get; init; }

    // AC-1511: whether a plugin offers its own view for this server, and what opens it. That view comes from the
    // plugin installed here, in this window, and reaches the plugin's backend part on the server.
    public Func<ServerPluginRowViewModel, bool> HasPluginSettings { get; init; } = _ => false;

    public Func<ServerPluginRowViewModel, Task>? OpenPluginSettings { get; init; }

    // AC-1590: whether this desktop has the plugin a server runs, and what installs it here from this desktop's own
    // stores. Absent means every plugin counts as present and no row offers the install.
    public Func<ServerPluginRowViewModel, bool> IsPluginOnDesktop { get; init; } = _ => true;

    public Func<ServerPluginRowViewModel, Task<DesktopInstallOutcome>>? InstallPluginOnDesktop { get; init; }

    public Action? RestartDesktop { get; init; }

    public ObservableCollection<ConnectKeyRowViewModel> Keys { get; } = [];

    public ObservableCollection<LockoutRowViewModel> Lockouts { get; } = [];

    public ObservableCollection<AuditRowViewModel> Audit { get; } = [];

    public ObservableCollection<ServerPluginRowViewModel> Plugins { get; } = [];

    public string PluginsCount => Plugins.Count.ToString(CultureInfo.InvariantCulture);

    public ObservableCollection<ScopeChoiceViewModel> EditorProfiles { get; } = [];

    public ObservableCollection<ScopeChoiceViewModel> EditorProjects { get; } = [];

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _lockoutPolicy = "";

    [ObservableProperty]
    private bool _isBusy;

    // AC-1525: a plugin change on the server waits for its restart, which this dialog offers once there is one.
    [ObservableProperty]
    private bool _needsRestart;

    // AC-1650: installed plugins the server's stores have a newer, runnable version of.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdates), nameof(UpdateLabel))]
    private int _availableUpdateCount;

    public bool HasAvailableUpdates => AvailableUpdateCount > 0;

    public string UpdateLabel => $"Update {AvailableUpdateCount}";

    // AC-1590: a plugin installed on this desktop loads its UI part at the next start of the desktop.
    [ObservableProperty]
    private bool _desktopNeedsRestart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorSaveLabel))]
    private bool _isEditorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorSaveLabel))]
    private bool _isIssuing;

    [ObservableProperty]
    private string _editorLabel = "";

    [ObservableProperty]
    private bool _editorIsAdmin;

    [ObservableProperty]
    private bool _editorHoldsAssistant;

    [ObservableProperty]
    private bool _editorAllProfiles = true;

    [ObservableProperty]
    private bool _editorAllProjects = true;

    [ObservableProperty]
    private bool _editorMayBypass;

    [ObservableProperty]
    private bool _editorMayAnswerPermissions = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssued))]
    private string? _issuedSecret;

    [ObservableProperty]
    private string _issuedSummary = "";

    [ObservableProperty]
    private bool _isIssuedSecretShown;

    public bool HasIssued => IssuedSecret is not null;

    public string EditorTitle => IsIssuing ? "Issue a key" : $"Scope of “{_editing?.Label}”";

    public string EditorSaveLabel => IsIssuing ? "Issue" : "Save scope";

    public string AssistantHolderHint => Keys.FirstOrDefault(key => key.Info.HoldsAssistant && key.IsLive) is { } holder
        ? $"({holder.Label} holds it now)"
        : "";

    public string KeysCount => Keys.Count(key => key.IsLive).ToString(CultureInfo.InvariantCulture);

    public bool HasLockouts => Lockouts.Count > 0;

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _RunAsync(async () =>
        {
            var overview = await _admin.ListAsync();
            _ShowKeys(overview.Keys);
            Lockouts.Clear();
            foreach (var lockout in overview.Lockouts)
            {
                Lockouts.Add(new LockoutRowViewModel(lockout, _time));
            }

            LockoutPolicy = _PolicyText(overview.Policy);
            OnPropertyChanged(nameof(HasLockouts));
            await _LoadAuditAsync(null);
            await _LoadPluginsAsync();
            if (_serverProjects is { } serverProjects)
            {
                await serverProjects.LoadAsync();
                _ShowProjects();
                await _LoadSharedAsync(serverProjects);
            }
        });
    }

    [RelayCommand]
    private Task LoadOlderAuditAsync() => _RunAsync(() => _LoadAuditAsync(Audit.LastOrDefault()?.Id));

    [RelayCommand]
    private void StartIssue()
    {
        _editing = null;
        IsIssuing = true;
        EditorLabel = "";
        EditorIsAdmin = false;
        EditorHoldsAssistant = false;
        _FillEditor(ConnectKeyScope.Default);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void StartScope(ConnectKeyRowViewModel row)
    {
        _editing = row;
        IsIssuing = false;
        EditorLabel = row.Label;
        EditorIsAdmin = row.Info.Capability == ConnectKeyCapability.Admin;
        EditorHoldsAssistant = row.Info.HoldsAssistant;
        _FillEditor(row.Info.Scope);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    [RelayCommand]
    private Task SaveEditorAsync() => _RunAsync(async () =>
    {
        var scope = _EditorScope();
        if (IsIssuing)
        {
            var capability = EditorIsAdmin ? ConnectKeyCapability.Admin : ConnectKeyCapability.Operate;
            var issued = await _admin.IssueAsync(new ConnectKeyRequest(EditorLabel.Trim(), capability, null, EditorHoldsAssistant, scope));
            _ShowIssued(issued, null);
        }
        else if (_editing is { } row)
        {
            if (!await _admin.SetScopeAsync(row.Prefix, scope))
            {
                Status = $"{row.Label} is no longer a live key.";
                return;
            }

            _Replace(row, row.Info with { Scope = scope });
        }

        IsEditorOpen = false;
        await _LoadAuditAsync(null);
    });

    // Rotating is a second key with the same capability and scope; the old one stays until it is revoked.
    [RelayCommand]
    private Task RotateAsync(ConnectKeyRowViewModel row) => _RunAsync(async () =>
    {
        var issued = await _admin.IssueAsync(new ConnectKeyRequest(_NextLabel(row.Label), row.Info.Capability, null, row.Info.HoldsAssistant, row.Info.Scope));
        _ShowIssued(issued, row.Label);
        await _LoadAuditAsync(null);
    });

    [RelayCommand]
    private Task RevokeAsync(ConnectKeyRowViewModel row) => _RunAsync(async () =>
    {
        if (await _admin.RevokeAsync(row.Prefix))
        {
            _Replace(row, row.Info with { RevokedAt = _time.GetUtcNow() });
        }
        else
        {
            Status = $"{row.Label} is no longer a live key.";
        }

        await _LoadAuditAsync(null);
    });

    [RelayCommand]
    private Task LiftAsync(LockoutRowViewModel lockout) => _RunAsync(async () =>
    {
        if (!await _admin.LiftLockoutAsync(lockout.Address))
        {
            Status = $"{lockout.Address} is no longer locked out.";
        }

        Lockouts.Remove(lockout);
        OnPropertyChanged(nameof(HasLockouts));
        await _LoadAuditAsync(null);
    });

    [RelayCommand]
    private void ToggleIssuedSecret() => IsIssuedSecretShown = !IsIssuedSecretShown;

    // AC-1525: enabling shows the consent for the bytes the server hashed, and the server pins only those.
    [RelayCommand]
    private Task TogglePluginAsync(ServerPluginRowViewModel row) => _RunAsync(async () =>
    {
        if (_plugins is null)
        {
            return;
        }

        if (!row.Enabled && (_dialogService is null || !await _dialogService.ShowPluginConsentAsync(new PluginConsentInfo(row.Name, row.Version, row.Author, "", row.Sha256))))
        {
            return;
        }

        await _plugins.SetEnabledAsync(row.FolderId, !row.Enabled, row.Sha256);
        row.Enabled = !row.Enabled;
        row.Refresh();
        _ChangedPlugins();
    });

    // AC-1525: the server stops as on a SIGTERM and its supervisor starts it again; its running sessions stop with it,
    // so they are counted first, the way "Stop on server" asks once (AC-1456).
    [RelayCommand]
    private Task RestartServerAsync() => _RunAsync(async () => await _RestartServerAsync());

    [RelayCommand]
    private Task InstallPluginAsync() => _RunAsync(() => _OpenStoreAsync(null));

    // AC-1650: the same store dialog as "Install from store", opened on the updates the store index has for the installed plugins.
    [RelayCommand]
    private Task UpdatePluginsAsync() => _RunAsync(() => _OpenStoreAsync(PluginStoreFilter.UpdatesAvailable));

    private async Task _OpenStoreAsync(PluginStoreFilter? filter)
    {
        var manager = await _CreateStoreManagerAsync();
        if (manager is null || _dialogService is null)
        {
            return;
        }

        await manager.LoadAsync();
        await _dialogService.ShowPluginStoreDialogAsync(manager, filter);
        await _LoadPluginsAsync();
        if (manager.NeedsRestart)
        {
            _ChangedPlugins();
        }
    }

    private async Task<PluginManagerViewModel?> _CreateStoreManagerAsync(bool quiet = false)
    {
        if (_plugins is not IPluginStoreCatalog catalog || _dialogService is null)
        {
            Status = quiet ? Status : "The server's plugin store is not available.";
            return null;
        }

        var stores = await catalog.GetStoresAsync();
        if (stores.Count == 0)
        {
            Status = quiet ? Status : "No plugin store is configured on the server.";
            return null;
        }

        PluginManagerViewModel? manager = null;
        manager = new PluginManagerViewModel(_plugins, _dialogService, [.. stores.Select(store => PluginStoreConfig.Remote(store.Id))], Server, _plugins is IServerRestart ? new ServerRestartService(async () =>
        {
            // Only reachable from the dialog this manager opened, so it is set by then.
            if (manager is null)
            {
                return;
            }

            try
            {
                var restarted = await _RestartServerAsync();
                manager.NeedsRestart = !restarted;
                manager.StatusMessage = restarted ? Status : manager.StatusMessage;
            }
            catch (Exception exception)
            {
                manager.StatusMessage = exception.Message;
            }
        }) : null);
        return manager;
    }

    // AC-1650: counts with the store dialog's own rules (newer, compatible, not staged), so the button and its "Update all" agree.
    private async Task _ShowAvailableUpdatesAsync()
    {
        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = await _CreateStoreManagerAsync(quiet: true);
            if (manager is not null)
            {
                await manager.LoadAsync();
                await manager.BrowseStoresAsync();
                foreach (var row in manager.AvailablePlugins.Where(row => row.CanUpdate))
                {
                    available.TryAdd(PluginFolderName.Normalize(row.Id), row.Entry.LatestVersion);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The plugin list must keep working when the check fails; the failure is logged, not shown.
            _logger?.LogWarning(exception, "Checking the server's plugin stores for updates failed.");
        }

        foreach (var row in Plugins)
        {
            row.AvailableVersion = available.GetValueOrDefault(row.FolderId);
        }

        AvailableUpdateCount = Plugins.Count(row => row.AvailableVersion is not null);
    }

    [RelayCommand]
    private Task InstallPluginOnDesktopAsync(ServerPluginRowViewModel row) => _RunAsync(async () =>
    {
        if (InstallPluginOnDesktop is null)
        {
            return;
        }

        var outcome = await InstallPluginOnDesktop(row);
        Status = outcome.Message;
        row.OnDesktop = outcome.Installed;
        DesktopNeedsRestart |= outcome.Installed && outcome.NeedsRestart;
    });

    [RelayCommand]
    private void RestartDesktopNow() => RestartDesktop?.Invoke();

    [RelayCommand]
    private Task OpenPluginSettingsAsync(ServerPluginRowViewModel row) =>
        _RunAsync(() => OpenPluginSettings?.Invoke(row) ?? Task.CompletedTask);

    [RelayCommand]
    private Task RemovePluginAsync(ServerPluginRowViewModel row) => _RunAsync(async () =>
    {
        if (_plugins is null)
        {
            return;
        }

        await _plugins.RemoveAsync(row.FolderId);
        Plugins.Remove(row);
        OnPropertyChanged(nameof(PluginsCount));
        _ChangedPlugins();
    });

    // Done forgets the key: after this nothing in the cockpit holds it.
    [RelayCommand]
    private void DismissIssued()
    {
        IssuedSecret = null;
        IssuedSummary = "";
        IsIssuedSecretShown = false;
    }

    private void _ShowIssued(IssuedConnectKey issued, string? rotatedFrom)
    {
        Keys.Add(new ConnectKeyRowViewModel(issued.Key, _time));
        _KeysChanged();
        var capability = CapabilityName(issued.Key.Capability);
        var expires = issued.Key.ExpiresAt is { } at ? $" · expires {at.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture)}" : "";
        IssuedSummary = rotatedFrom is null
            ? $"{issued.Key.Label} · {capability}{expires}."
            : $"{issued.Key.Label} · {capability}{expires} · same scope as {rotatedFrom}. Revoke the old key once {rotatedFrom} uses the new one.";
        IsIssuedSecretShown = false;
        IssuedSecret = issued.Secret;
    }

    private async Task _LoadAuditAsync(long? before)
    {
        var entries = await _admin.ReadAuditAsync(before, AuditPage);
        if (before is null)
        {
            Audit.Clear();
        }

        var labels = Keys.ToDictionary(key => key.Prefix, key => key.Info, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            Audit.Add(new AuditRowViewModel(entry, labels, _time));
        }
    }

    private void _ChangedPlugins()
    {
        NeedsRestart = _plugins is IServerRestart;
        Status = "This change takes effect after the server restarts.";
    }

    // False when the operator kept the sessions running.
    private async Task<bool> _RestartServerAsync()
    {
        if (_plugins is not IServerRestart server)
        {
            return false;
        }

        var running = _runningSessions?.Invoke() ?? 0;
        if (running > 0 && (_dialogService is null || !await _dialogService.ShowConfirmationDialogAsync(
            $"Restart {Server}",
            running == 1
                ? $"1 session is running on {Server}. Restarting the server stops it; this cockpit stays open."
                : $"{running} sessions are running on {Server}. Restarting the server stops them; this cockpit stays open.",
            "Restart server")))
        {
            return false;
        }

        await server.RestartAsync();
        NeedsRestart = false;
        Status = $"{Server} is restarting; it reconnects when it is back.";
        return true;
    }

    private async Task _LoadPluginsAsync()
    {
        if (_plugins is null)
        {
            return;
        }

        Plugins.Clear();
        foreach (var plugin in await _plugins.GetInstalledAsync())
        {
            var row = new ServerPluginRowViewModel(plugin);
            row.HasSettings = HasPluginSettings(row);
            row.OnDesktop = IsPluginOnDesktop(row);
            Plugins.Add(row);
        }

        OnPropertyChanged(nameof(PluginsCount));
        await _ShowAvailableUpdatesAsync();
    }

    private async Task _RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Status = "";
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void _ShowKeys(IEnumerable<ConnectKeyInfo> keys)
    {
        Keys.Clear();
        foreach (var key in keys)
        {
            Keys.Add(new ConnectKeyRowViewModel(key, _time));
        }

        _KeysChanged();
    }

    private void _Replace(ConnectKeyRowViewModel row, ConnectKeyInfo info)
    {
        var index = Keys.IndexOf(row);
        if (index >= 0)
        {
            Keys[index] = new ConnectKeyRowViewModel(info, _time);
        }

        _KeysChanged();
    }

    private void _KeysChanged()
    {
        OnPropertyChanged(nameof(KeysCount));
        OnPropertyChanged(nameof(AssistantHolderHint));
    }

    private void _FillEditor(ConnectKeyScope scope)
    {
        EditorAllProfiles = scope.AllowAllProfiles;
        EditorAllProjects = scope.AllowAllProjects;
        EditorMayBypass = scope.MayStartBypassProfiles;
        EditorMayAnswerPermissions = scope.MayAnswerPermissions;
        EditorProfiles.Clear();
        foreach (var label in _profiles.Union(scope.AllowedProfileLabels, StringComparer.Ordinal))
        {
            EditorProfiles.Add(new ScopeChoiceViewModel(label, label, scope.AllowedProfileLabels.Contains(label, StringComparer.Ordinal)));
        }

        EditorProjects.Clear();
        foreach (var project in _projects)
        {
            EditorProjects.Add(new ScopeChoiceViewModel(project.Id ?? "", project.Name, scope.AllowedProjectIds.Contains(project.Id ?? "", StringComparer.Ordinal)));
        }

        foreach (var unknown in scope.AllowedProjectIds.Where(id => _projects.All(project => project.Id != id)))
        {
            EditorProjects.Add(new ScopeChoiceViewModel(unknown, unknown, true));
        }
    }

    private ConnectKeyScope _EditorScope() => new()
    {
        AllowAllProfiles = EditorAllProfiles,
        AllowAllProjects = EditorAllProjects,
        AllowedProfileLabels = [.. EditorProfiles.Where(choice => choice.IsChecked).Select(choice => choice.Id)],
        AllowedProjectIds = [.. EditorProjects.Where(choice => choice.IsChecked).Select(choice => choice.Id)],
        MayStartBypassProfiles = EditorMayBypass,
        MayAnswerPermissions = EditorMayAnswerPermissions,
    };

    // "phone" becomes "phone-2", and "phone-2" becomes "phone-3".
    private string _NextLabel(string label)
    {
        var dash = label.LastIndexOf('-');
        var stem = dash > 0 && int.TryParse(label[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _) ? label[..dash] : label;
        var next = 2;
        while (Keys.Any(key => string.Equals(key.Label, $"{stem}-{next}", StringComparison.Ordinal)))
        {
            next++;
        }

        return $"{stem}-{next}";
    }

    private static string _PolicyText(ConnectKeyPolicy policy) =>
        $"admin only · {policy.FailuresBeforeLockout} refusals in {policy.FailureWindow.TotalMinutes:0} min → {policy.FirstLockout.TotalMinutes:0} min, doubling up to {policy.MaxLockout.TotalMinutes:0} min";

    internal static string CapabilityName(ConnectKeyCapability capability) => capability == ConnectKeyCapability.Admin ? "admin" : "operate";
}

public sealed partial class ServerPluginRowViewModel(InstalledPlugin plugin) : ObservableObject
{
    public string FolderId { get; } = plugin.Discovered.FolderId;

    public string Name { get; } = plugin.Discovered.Manifest.Name;

    public string Id { get; } = plugin.Discovered.Manifest.Id;

    public string Version { get; } = plugin.Discovered.Manifest.Version;

    public string? Author { get; } = plugin.Discovered.Manifest.Author;

    // AC-1525: the server's own hash of the bytes on its disk, which the consent shows and the server pins.
    public string Sha256 { get; } = plugin.Discovered.Sha256;

    public bool HasSettings { get; set; }

    // AC-1590: this desktop has the plugin; false offers "Install on this desktop".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingOnDesktop))]
    private bool _onDesktop = true;

    public bool MissingOnDesktop => !OnDesktop;

    // AC-1650: the newer version the server's stores offer, or null.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    private string? _availableVersion;

    public bool HasUpdate => AvailableVersion is not null;

    public string UpdateText => $"{AvailableVersion} available";

    [ObservableProperty]
    private bool _enabled = plugin.Registration?.Enabled ?? false;

    public string State => Enabled ? "Enabled" : "Disabled";

    public void Refresh() => OnPropertyChanged(nameof(State));
}

// AC-1446: one row of the Connect keys table: a prefix and a label, never more of the key.
public sealed class ConnectKeyRowViewModel(ConnectKeyInfo info, TimeProvider time)
{
    private const string KeyPrefix = "ck_";

    public ConnectKeyInfo Info { get; } = info;

    public string Prefix => Info.Prefix;

    public string Label => Info.Label;

    public bool IsLive => Info.RevokedAt is null;

    public bool IsBootstrap => Info.IsBootstrap;

    public bool IsAdmin => Info.Capability == ConnectKeyCapability.Admin;

    public string Capability => ServerAdminViewModel.CapabilityName(Info.Capability);

    public double RowOpacity => IsLive ? 1 : 0.55;

    public bool CanRotate => IsLive && !IsBootstrap;

    public bool CanRevoke => IsLive && !IsBootstrap;

    // Scope is the mockup's for an operate key; an admin key may always issue itself a wider one.
    public bool CanScope => CanRevoke && !IsAdmin;

    public string Detail => IsBootstrap
        ? $"{KeyPrefix}{Prefix}… · emergency key, from container secret"
        : Info.HoldsAssistant ? $"{KeyPrefix}{Prefix}… · holds the assistant" : $"{KeyPrefix}{Prefix}…";

    public string ScopeText
    {
        get
        {
            if (!IsLive)
            {
                return "—";
            }

            var scope = Info.Scope;
            var everything = scope.AllowAllProfiles && scope.AllowAllProjects;
            if (IsBootstrap)
            {
                return "Everything";
            }

            var reach = everything
                ? "Everything"
                : $"{(scope.AllowAllProjects ? "all projects" : _Count(scope.AllowedProjectIds.Count, "project"))} · {(scope.AllowAllProfiles ? "all profiles" : _Count(scope.AllowedProfileLabels.Count, "profile"))}";
            return scope.MayStartBypassProfiles ? $"{reach} · bypass allowed" : everything ? reach : $"{reach} · no bypass";
        }
    }

    public string ExpiresText => Info switch
    {
        { RevokedAt: { } revoked } => $"Revoked {revoked.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture)}",
        { IsBootstrap: true } => "— (rotate the secret)",
        { ExpiresAt: { } expires } => expires.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture),
        _ => "—",
    };

    public string LastUsedText
    {
        get
        {
            if (Info.LastUsedAt is not { } used)
            {
                return "—";
            }

            var when = time.GetUtcNow() - used < TimeSpan.FromMinutes(1) ? "now" : AuditRowViewModel.When(used, time);
            return Info.LastUsedFrom is { } from ? $"{when} · {from}" : when;
        }
    }

    public bool WasUsedJustNow => Info.LastUsedAt is { } used && time.GetUtcNow() - used < TimeSpan.FromMinutes(1);

    private static string _Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

// AC-1446: an address locked out of the server, by the same per-address rule as the door (an IPv6 /64 as one).
public sealed class LockoutRowViewModel(ConnectKeyLockout lockout, TimeProvider time)
{
    public string Address => lockout.Address;

    public string Detail =>
        $"locked until {lockout.LockedUntil.ToLocalTime().ToString(time.GetUtcNow().Date == lockout.LockedUntil.UtcDateTime.Date ? "HH:mm" : "d MMM HH:mm", CultureInfo.InvariantCulture)} · {lockout.RefusedWhileLockedOut} refusals";
}

// AC-1446: one audit line in the mockup's words: when, from where, which key, and what happened.
public sealed class AuditRowViewModel
{
    public AuditRowViewModel(ConnectKeyAuditEntry entry, IReadOnlyDictionary<string, ConnectKeyInfo> keys, TimeProvider time)
    {
        Id = entry.Id;
        Time = When(entry.At, time);
        Address = entry.Address;
        Key = entry.Actor ?? "—";
        What = _What(entry, keys);
        IsRefusal = entry.Outcome.StartsWith("refused", StringComparison.Ordinal);
        IsLockout = entry.Outcome.StartsWith("lockout started", StringComparison.Ordinal);
    }

    public long Id { get; }

    public string Time { get; }

    public string Address { get; }

    public string Key { get; }

    public string What { get; }

    public bool IsRefusal { get; }

    public bool IsLockout { get; }

    public bool IsPlain => !IsRefusal && !IsLockout;

    // Today as a clock time, earlier as a date with one.
    internal static string When(DateTimeOffset at, TimeProvider time)
    {
        var local = at.ToLocalTime();
        return local.Date == time.GetUtcNow().ToLocalTime().Date
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    private static string _What(ConnectKeyAuditEntry entry, IReadOnlyDictionary<string, ConnectKeyInfo> keys)
    {
        var subject = entry.Subject is { } subjectPrefix && keys.TryGetValue(subjectPrefix, out var key) ? key.Label : entry.Subject;
        const string lockoutStarted = "lockout started until ";
        return entry.Outcome switch
        {
            "issued" when entry.Subject is { } prefix && keys.TryGetValue(prefix, out var issued) => $"issued · {issued.Label} ({ServerAdminViewModel.CapabilityName(issued.Capability)})",
            "issued" or "revoked" or "scope changed" or "cloned" or "clone failed" or "changed" or "removed" => $"{entry.Outcome} · {subject}",
            "copied" => $"assistant profile copied from {subject}",
            "connected" => "connected",
            { } outcome when outcome.StartsWith(lockoutStarted, StringComparison.Ordinal)
                && DateTimeOffset.TryParse(outcome[lockoutStarted.Length..], CultureInfo.InvariantCulture, DateTimeStyles.None, out var until) =>
                $"lockout started until {until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}",
            { } outcome when entry.Action == "lift_connect_lockout" && !outcome.Contains("WARNING", StringComparison.Ordinal) => $"lockout lifted · {entry.Subject}",
            { } outcome when entry.Action is { } action && outcome is "called" or "tool error" => $"{action.Replace("api:", "", StringComparison.Ordinal)}{(outcome == "called" ? "" : " · tool error")}",
            { } outcome => outcome,
        };
    }
}

// AC-1446: a profile or project the scope editor can tick.
public sealed partial class ScopeChoiceViewModel(string id, string name, bool isChecked) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isChecked = isChecked;
}
