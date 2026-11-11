using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Mcp;

namespace Cockpit.App.ViewModels;

// Connect keys and audit on the remote window's ordinary Servers category.
public sealed partial class ConnectKeysViewModel : ObservableObject
{
    private const int AuditPage = 200;

    private readonly IConnectKeyAdministration _admin;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _profiles;
    private readonly IReadOnlyList<NodeProjectChoice> _projects;
    private ConnectKeyRowViewModel? _editing;
    private ConnectKeyPolicy _keyPolicy = ConnectKeyPolicy.Default;

    public ConnectKeysViewModel(IConnectKeyAdministration admin, IReadOnlyList<string> profiles, IReadOnlyList<NodeProjectChoice> projects, TimeProvider? time = null, string? serverVersion = null, string? desktopVersion = null)
    {
        ServerVersion = serverVersion ?? "unknown";
        DesktopVersion = desktopVersion ?? AboutInfo.FromAssembly(typeof(ConnectKeysViewModel).Assembly).VersionText;
        _admin = admin;
        _time = time ?? TimeProvider.System;
        _profiles = profiles;
        _projects = [.. projects.Where(project => project.Id is not null)];
    }

    public string ServerVersion { get; }

    public string DesktopVersion { get; }

    public ObservableCollection<ConnectKeyRowViewModel> Keys { get; } = [];

    public ObservableCollection<LockoutRowViewModel> Lockouts { get; } = [];

    public ObservableCollection<AuditRowViewModel> Audit { get; } = [];

    public ObservableCollection<ScopeChoiceViewModel> EditorProfiles { get; } = [];

    public ObservableCollection<ScopeChoiceViewModel> EditorProjects { get; } = [];

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private string _lockoutPolicy = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorSaveLabel))]
    private bool _isEditorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorSaveLabel), nameof(CanHoldAssistant))]
    private bool _isIssuing;

    [ObservableProperty]
    private string _editorLabel = "";

    [ObservableProperty]
    private bool _editorIsAdmin;

    [ObservableProperty]
    private bool _editorHoldsAssistant;

    // AC-2003: only the owner's own key reaches a personal subscription login or holds the assistant.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHoldAssistant))]
    private bool _editorIssuedToOwner;

    [ObservableProperty]
    private decimal _editorExpiresInDays = ConnectKeyPolicy.Default.DefaultExpiryDays;

    [ObservableProperty]
    private decimal _editorMaximumExpiryDays = ConnectKeyPolicy.Default.MaxExpiryDays;

    [ObservableProperty]
    private bool _editorNeverExpires;

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

    public bool CanHoldAssistant => IsIssuing && EditorIssuedToOwner;

    // An older key is marked once, with Save; a mark is not taken back here, the key is revoked instead.
    public bool CanMarkOwnDevice => IsIssuing || _editing is { Info.IssuedToOwner: false };

    public string OwnerHint => "Only your own key starts a profile on your personal subscription. This server is for one person; marking someone else's key as yours is on you.";

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
            _keyPolicy = overview.Policy;
            EditorMaximumExpiryDays = overview.Policy.MaxExpiryDays;
            _ShowKeys(overview.Keys);
            Lockouts.Clear();
            foreach (var lockout in overview.Lockouts)
            {
                Lockouts.Add(new LockoutRowViewModel(lockout, _time));
            }

            LockoutPolicy = _PolicyText(overview.Policy);
            OnPropertyChanged(nameof(HasLockouts));
            await _LoadAuditAsync(null);
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
        EditorIssuedToOwner = false;
        OnPropertyChanged(nameof(CanMarkOwnDevice));
        EditorExpiresInDays = _keyPolicy.DefaultExpiryDays;
        EditorNeverExpires = false;
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
        EditorIssuedToOwner = row.Info.IssuedToOwner;
        OnPropertyChanged(nameof(CanMarkOwnDevice));
        _FillEditor(row.Info.Scope);
        IsEditorOpen = true;
    }

    partial void OnEditorIssuedToOwnerChanged(bool value)
    {
        EditorHoldsAssistant = EditorHoldsAssistant && value;
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
            int? expiresInDays = EditorNeverExpires ? null : decimal.ToInt32(EditorExpiresInDays);
            var issued = await _admin.IssueAsync(new ConnectKeyRequest(EditorLabel.Trim(), capability, expiresInDays, EditorHoldsAssistant, scope, EditorNeverExpires, EditorIssuedToOwner));
            _ShowIssued(issued, null);
        }
        else if (_editing is { } row)
        {
            // The mark first: a refused mark (not the owner's key calling) leaves the scope untouched too.
            var marked = EditorIssuedToOwner && !row.Info.IssuedToOwner && await _admin.MarkOwnDeviceAsync(row.Prefix);
            if (!await _admin.SetScopeAsync(row.Prefix, scope))
            {
                Status = $"{row.Label} is no longer a live key.";
                StatusIsError = true;
                return;
            }

            _Replace(row, row.Info with { Scope = scope, IssuedToOwner = row.Info.IssuedToOwner || marked });
        }

        IsEditorOpen = false;
        await _LoadAuditAsync(null);
    });

    // Rotating is a second key with the same capability and scope; the old one stays until it is revoked.
    [RelayCommand]
    private Task RotateAsync(ConnectKeyRowViewModel row) => _RunAsync(async () =>
    {
        var issued = await _admin.IssueAsync(new ConnectKeyRequest(_NextLabel(row.Label), row.Info.Capability, null, row.Info.HoldsAssistant, row.Info.Scope, IssuedToOwner: row.Info.IssuedToOwner));
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
            StatusIsError = true;
        }

        await _LoadAuditAsync(null);
    });

    [RelayCommand]
    private Task LiftAsync(LockoutRowViewModel lockout) => _RunAsync(async () =>
    {
        if (!await _admin.LiftLockoutAsync(lockout.Address))
        {
            Status = $"{lockout.Address} is no longer locked out.";
            StatusIsError = true;
        }

        Lockouts.Remove(lockout);
        OnPropertyChanged(nameof(HasLockouts));
        await _LoadAuditAsync(null);
    });

    [RelayCommand]
    private void ToggleIssuedSecret() => IsIssuedSecretShown = !IsIssuedSecretShown;

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
        var expires = issued.Key.ExpiresAt is { } at ? $" · expires {at.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture)}" : " · never expires";
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

    private async Task _RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Status = "";
        StatusIsError = false;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Status = exception is IRemoteCallFailure { Status: HttpStatusCode.NotFound } && _ServerIsOlderThanDesktop()
                ? $"The server runs {ServerVersion}; this desktop runs {DesktopVersion}. This server does not know this feature yet; update the server."
                : exception.Message;
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool _ServerIsOlderThanDesktop()
    {
        var serverVersion = _ParseSemVerCore(ServerVersion);
        var desktopVersion = _ParseSemVerCore(DesktopVersion);
        return serverVersion is not null && desktopVersion is not null && serverVersion < desktopVersion;
    }

    private static Version? _ParseSemVerCore(string value)
    {
        var core = value.AsSpan();
        var suffix = core.IndexOfAny('-', '+');
        core = suffix < 0 ? core : core[..suffix];
        if (!Version.TryParse(core, out var version) || version.Revision >= 0)
        {
            return null;
        }

        return version.Build < 0 ? new Version(version.Major, version.Minor, 0) : version;
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

    public string Capability => ConnectKeysViewModel.CapabilityName(Info.Capability);

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
        _ => "Never",
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
            "issued" when entry.Subject is { } prefix && keys.TryGetValue(prefix, out var issued) => $"issued · {issued.Label} ({ConnectKeysViewModel.CapabilityName(issued.Capability)})",
            "issued" or "revoked" or "scope changed" or "own device" or "cloned" or "clone failed" or "changed" or "removed" => $"{entry.Outcome} · {subject}",
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
