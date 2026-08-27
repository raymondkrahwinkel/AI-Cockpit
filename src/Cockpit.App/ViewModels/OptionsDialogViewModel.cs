using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;

namespace Cockpit.App.ViewModels;

// Self-contained the same way `SecurityOptionsViewModel` is — every constructor argument is optional so it also
// constructs for design-time preview and for a unit test that only cares about a couple of them, without needing the
// whole `CockpitViewModel` dependency graph in scope (AC-543).
public sealed partial class AssistantOptionsViewModel(
    IAssistantSettingsStore? settingsStore = null,
    IAssistantProfileStore? profileStore = null,
    IConsentAuditLog? consentAuditLog = null) : ObservableObject
{
    private readonly IAssistantSettingsStore? _settingsStore = settingsStore;
    private readonly IAssistantProfileStore? _profileStore = profileStore;
    private readonly IConsentAuditLog? _consentAuditLog = consentAuditLog;

    // True only while `RefreshAsync` is seeding properties from disk, so the change handlers below do not write the same value straight back out.
    private bool _loading;

    // The settings as last loaded (or saved), kept around so a save from this view model only overwrites the three
    // fields it owns (AC-543).
    private AssistantSettings _lastLoadedSettings = new();

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _speakReplies = true;

    [ObservableProperty]
    private string _pushToTalkKeyName = "F10";

    // Whether the chat pop-out stays above every other window (AC-681). Mirrors `AssistantSettings.AlwaysOnTop`'s
    // own default so the checkbox does not flash unchecked before `RefreshAsync` loads the real value.
    [ObservableProperty]
    private bool _alwaysOnTop = true;

    // The three reading levels the chat window can render at (AC-138) — the same list an SDK session's own header dropdown offers.
    public IReadOnlyList<ReadingLevelOption> ReadingLevels => SessionOptionCatalog.ReadingLevels;

    [ObservableProperty]
    private ReadingLevelOption _selectedReadingLevel = SessionOptionCatalog.DefaultReadingLevel;

    // `null` while the slot has no record, where `ProfileUnsetReason` is what the page shows instead.
    [ObservableProperty]
    private string? _profileLabel;

    // Why the slot has no record right now. Empty while `ProfileLabel` is set — the UI shows one or the other, never both.
    [ObservableProperty]
    private string? _profileUnsetReason;

    // "Allow all" (#AC-637): skip the card for every source and both risk classes, on by default. The per-source
    // rows below are hidden while it is on rather than cleared — off puts back exactly what was ticked before.
    [ObservableProperty]
    private bool _consentBypassAll = true;

    // The consent-bypass switches, one row per source (#AC-575). Filled by `RefreshAsync` from names
    // the host stamps — never from anything the operator types — see `_RebuildConsentBypassRowsAsync`.
    public ObservableCollection<ConsentBypassSourceViewModel> ConsentBypassSources { get; } = [];

    // Whether anything is switched on — the one-line summary above the list, so the page says so before the rows are read.
    public bool HasConsentBypass =>
        ConsentBypassAll || ConsentBypassSources.Any(row => row.BypassLowRisk || row.BypassDangerous);

    // Loads the settings and the assistant's own profile. Safe to call with no stores wired (design-time/tests) — it then simply leaves the defaults in place.
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _loading = true;
        try
        {
            if (_settingsStore is not null)
            {
                _lastLoadedSettings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
                IsEnabled = _lastLoadedSettings.IsEnabled;
                SpeakReplies = _lastLoadedSettings.SpeakReplies;
                PushToTalkKeyName = _lastLoadedSettings.PushToTalkKeyName;
                AlwaysOnTop = _lastLoadedSettings.AlwaysOnTop;
                SelectedReadingLevel = SessionOptionCatalog.ResolveReadingLevel(_lastLoadedSettings.ReadingLevel);
                ConsentBypassAll = _lastLoadedSettings.ConsentBypassAll;
                await _RebuildConsentBypassRowsAsync(cancellationToken).ConfigureAwait(true);
            }

            if (_profileStore is not null)
            {
                var slot = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(true);
                ProfileLabel = slot.Profile is { } record
                    ? ProfileDisplay.Format(record.Label, record.Provider, ProfileDisplay.ModelOf(record))
                    : null;
                ProfileUnsetReason = slot.UnsetReason;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    // This is what puts plugins on the list: a plugin asks through `ICockpitHost`, which stamps its plugin id
    // host-side, and there is no compile-time list of installed plugins to enumerate instead (AC-575).
    private async Task _RebuildConsentBypassRowsAsync(CancellationToken cancellationToken)
    {
        var lowRisk = _lastLoadedSettings.ConsentBypassSources.ToHashSet(StringComparer.Ordinal);
        var dangerous = _lastLoadedSettings.ConsentBypassDangerousSources.ToHashSet(StringComparer.Ordinal);

        // Key -> the name to show for it. First writer wins, so the catalog's own wording is never overwritten by
        // whatever a later trail entry called the same source.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in ConsentSourceCatalog.HostSources)
        {
            names[source] = source;
        }

        if (_consentAuditLog is not null)
        {
            foreach (var entry in await _consentAuditLog.ReadRecentAsync(500, cancellationToken).ConfigureAwait(true))
            {
                // The same key the broker matches on (ConsentService._SourceKey) — built from the one definition, so
                // a plugin id and a host label can never collide into a single shared row.
                names.TryAdd(ConsentSourceCatalog.KeyFor(entry.PluginId, entry.SourceLabel), entry.SourceLabel);
            }
        }

        // That row keeps working — it can still be switched off — but it is marked as a leftover rather than silently
        // migrated to the new key, which would re-enable a bypass under a name the operator never ticked.
        var recognized = new HashSet<string>(names.Keys, StringComparer.Ordinal);

        foreach (var key in lowRisk.Concat(dangerous))
        {
            names.TryAdd(key, key);
        }

        ConsentBypassSources.Clear();
        // Recognised sources alphabetically, then orphans at the end — a leftover never displaces the real source
        // it is easy to mistake it for.
        var ordered = names
            .OrderBy(pair => recognized.Contains(pair.Key) ? 0 : 1)
            .ThenBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase);
        foreach (var (key, label) in ordered)
        {
            var row = new ConsentBypassSourceViewModel(key, label, isOrphan: !recognized.Contains(key))
            {
                BypassLowRisk = lowRisk.Contains(key),
                BypassDangerous = dangerous.Contains(key),
            };
            row.Changed = _SaveSettings;
            ConsentBypassSources.Add(row);
        }

        OnPropertyChanged(nameof(HasConsentBypass));
    }

    partial void OnConsentBypassAllChanged(bool value) => _SaveSettings();

    partial void OnIsEnabledChanged(bool value) => _SaveSettings();

    partial void OnSpeakRepliesChanged(bool value) => _SaveSettings();

    partial void OnPushToTalkKeyNameChanged(string value) => _SaveSettings();

    partial void OnAlwaysOnTopChanged(bool value) => _SaveSettings();

    partial void OnSelectedReadingLevelChanged(ReadingLevelOption value) => _SaveSettings();

    // The key and the on/off flag are read when the hotkey is armed and when the assistant is asked for, so without
    // this a setting would be a field that remembers what you typed and changes nothing until the next restart — the
    // failure `GlobalHotkeyCoordinator` already re-arms on `VoiceSettingsSaved` to avoid for F9.
    public event EventHandler? Saved;

    // AC-999: while the Options dialog is staging, these are values held in the view model and written once on
    // Apply. `_SaveSettings` still folds them into `_lastLoadedSettings` — that is the buffer — and only the
    // write to disk waits.
    public bool SuspendPersistence { get; set; }

    // The Options dialog's Apply (AC-999). Writes whatever the buffer holds, even when nothing was touched: a
    // no-op rewrite of the values just read is cheaper than tracking whether one was.
    public Task SaveStagedAsync()
    {
        if (_settingsStore is null)
        {
            return Task.CompletedTask;
        }

        _Compose();
        return _SaveAndAnnounceAsync(_lastLoadedSettings);
    }

    // Fills the buffer with what a fresh cockpit would show (AC-999); writes nothing, so Cancel still undoes it.
    public void RestoreDefaults()
    {
        var defaults = new AssistantSettings();
        IsEnabled = defaults.IsEnabled;
        SpeakReplies = defaults.SpeakReplies;
        PushToTalkKeyName = defaults.PushToTalkKeyName;
        AlwaysOnTop = defaults.AlwaysOnTop;
        SelectedReadingLevel = SessionOptionCatalog.ReadingLevels.FirstOrDefault(level => level.Value == defaults.ReadingLevel)
                               ?? SessionOptionCatalog.DefaultReadingLevel;
        ConsentBypassAll = defaults.ConsentBypassAll;
        foreach (var row in ConsentBypassSources)
        {
            row.BypassLowRisk = false;
            row.BypassDangerous = false;
        }
    }

    private void _SaveSettings()
    {
        if (_loading || _settingsStore is null)
        {
            return;
        }

        _Compose();

        if (SuspendPersistence)
        {
            return;
        }

        _ = _SaveAndAnnounceAsync(_lastLoadedSettings);
    }

    private void _Compose()
    {
        _lastLoadedSettings = _lastLoadedSettings with
        {
            IsEnabled = IsEnabled,
            SpeakReplies = SpeakReplies,
            PushToTalkKeyName = string.IsNullOrWhiteSpace(PushToTalkKeyName) ? "F10" : PushToTalkKeyName.Trim(),
            AlwaysOnTop = AlwaysOnTop,
            ReadingLevel = SelectedReadingLevel.Value,
            // Written from the rows rather than merged into what was loaded: the rows already carry every stored
            // key (see _RebuildConsentBypassRowsAsync), so this is a full replacement and unticking a box actually
            // removes the permission instead of leaving it on disk under a row that no longer shows it.
            ConsentBypassSources = [.. ConsentBypassSources.Where(row => row.BypassLowRisk).Select(row => row.Key)],
            ConsentBypassDangerousSources = [.. ConsentBypassSources.Where(row => row.BypassDangerous).Select(row => row.Key)],
            ConsentBypassAll = ConsentBypassAll,
        };

        OnPropertyChanged(nameof(HasConsentBypass));
    }

    // Announced after the write, not alongside it: every subscriber re-reads the store, so a signal raised while
    // the save was still in flight would have them acting on the values it is replacing.
    private async Task _SaveAndAnnounceAsync(AssistantSettings settings)
    {
        await _settingsStore!.SaveAsync(settings).ConfigureAwait(true);
        Saved?.Invoke(this, EventArgs.Empty);
    }
}

// Never shown as the primary name, and never editable (AC-575).
public sealed partial class ConsentBypassSourceViewModel(string key, string label, bool isOrphan = false) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    public bool IsOrphan { get; } = isOrphan;

    // A plugin that names itself after a cockpit source then reads as its own id on this list rather than borrowing the
    // other's name; the switch was already keyed on the id, this is so the operator can see that.
    public string? KeyDetail => string.Equals(Key, Label, StringComparison.Ordinal) ? null : Key;

    // Skip the card for this source's low-risk, idempotent actions.
    [ObservableProperty]
    private bool _bypassLowRisk;

    // Skip it for this source's dangerous actions too. Off by default, and never turned on by the switch above.
    [ObservableProperty]
    private bool _bypassDangerous;

    // Set by the page that owns the row, so a tick persists straight away like every other switch on this dialog.
    internal Action? Changed { get; set; }

    partial void OnBypassLowRiskChanged(bool value) => Changed?.Invoke();

    partial void OnBypassDangerousChanged(bool value) => Changed?.Invoke();
}
