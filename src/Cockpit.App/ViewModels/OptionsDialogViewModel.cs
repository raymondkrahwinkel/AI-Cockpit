using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;

namespace Cockpit.App.ViewModels;

// The Options → Voice "Assistant" block (AC-543): the master on/off switch, the name of the assistant's own
// profile (edited in `AssistantProfileDialogViewModel`, opened from here), the push-to-talk hotkey,
// and the independent read-replies-aloud switch. Self-contained the same way
// `SecurityOptionsViewModel` is — every constructor argument is optional so it also constructs for
// design-time preview and for a unit test that only cares about a couple of them, without needing the whole
// `CockpitViewModel` dependency graph in scope.
// *Off does not touch the profile.* `IsEnabled` persists through `_settingsStore`,
// which owns the `assistant` section; the Assistant Profile slot lives in its own `assistantProfile`
// section behind `_profileStore` (AC-543's slot design) and is never written by this switch. Turning
// the feature off and back on always finds the same profile still pointed at — that is the whole reason the two
// are separate stores rather than one flag next to the profile.
//
// *Speaking and being enabled are two decisions, not one.* `SpeakReplies` persists on its own
// and is read back independently of `IsEnabled` — someone using the assistant as a text-only
// assistant in a shared room needs the assistant on and speech off at the same time, which a derived flag could
// never express (criterion 9).
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

    // The settings as last loaded (or saved), kept around so a save from this view model only overwrites the
    // three fields it owns. `AssistantSettings.AlwaysOnCostAcknowledged` lives in the same record but
    // is the indicator's to set (AC-543 comment 5), and constructing a bare `new AssistantSettings()` on
    // every save would silently reset it to its default underneath it.
    private AssistantSettings _lastLoadedSettings = new();

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _speakReplies = true;

    [ObservableProperty]
    private string _pushToTalkKeyName = "F10";

    // The three reading levels the chat window can render at (AC-138) — the same list an SDK session's own header dropdown offers.
    public IReadOnlyList<ReadingLevelOption> ReadingLevels => SessionOptionCatalog.ReadingLevels;

    [ObservableProperty]
    private ReadingLevelOption _selectedReadingLevel = SessionOptionCatalog.DefaultReadingLevel;

    // What the assistant's own profile is called and what it runs on — `ProfileDisplay.Format`'s wording, the
    // same convention the chip and every other profile surface uses. `null` while the slot has no
    // record, where `ProfileUnsetReason` is what the page shows instead.
    // A line rather than a picker. The slot holds a whole `SessionProfile` of its own — it always did —
    // and this page used to present it as a selection from the profile list, which is what made an operator believe
    // their edits to that list reached the assistant. Naming it as the assistant's own profile, editable in its own
    // dialog, is what removes the belief rather than annotating it.
    [ObservableProperty]
    private string? _profileLabel;

    // Why the slot has no record right now. Empty while `ProfileLabel` is set — the UI shows one or the other, never both.
    [ObservableProperty]
    private string? _profileUnsetReason;

    // The consent-bypass switches, one row per source (#AC-575). Filled by `RefreshAsync` from names
    // the host stamps — never from anything the operator types — see `_RebuildConsentBypassRowsAsync`.
    public ObservableCollection<ConsentBypassSourceViewModel> ConsentBypassSources { get; } = [];

    // Whether any source is switched on — the one-line summary above the list, so the page says so before the rows are read.
    public bool HasConsentBypass => ConsentBypassSources.Any(row => row.BypassLowRisk || row.BypassDangerous);

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
                SelectedReadingLevel = SessionOptionCatalog.ResolveReadingLevel(_lastLoadedSettings.ReadingLevel);
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

    // Rebuilds the bypass rows (#AC-575). Three sources, all host-stamped, none of them free text:
    // - the cockpit's own consent callers, from `ConsentSourceCatalog` — the same constants those
    // gates build their `ConsentSource` from, so a renamed label moves the switch with it;
    // - every source that has actually asked, read off the consent trail. This is what puts plugins on the
    // list: a plugin asks through `ICockpitHost`, which stamps its plugin id host-side, and there is no
    // compile-time list of installed plugins to enumerate instead. The trail's label is only ever *shown* —
    // the switch is keyed on the stamped id, so a plugin that labels itself "Terminal MCP" gets a row of its own
    // under its own id rather than a share of the terminal's;
    // - anything already switched on, so a source whose plugin is currently uninstalled still has a row to be
    // switched off from, instead of a permission that is set and invisible.
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

        // Recognised: named by the catalog, or seen actually asking in the trail. Snapshotted before the loop
        // below adds anything switched on that is neither — most often a stored key from before a source's own
        // id changed underneath it (#K11: a plugin's key moved from "kubernetes" to "plugin:kubernetes", so an
        // existing config still names the old one). That row keeps working — it can still be switched off — but
        // it is marked as a leftover rather than silently migrated to the new key, which would re-enable a bypass
        // under a name the operator never ticked.
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

    partial void OnIsEnabledChanged(bool value) => _SaveSettings();

    partial void OnSpeakRepliesChanged(bool value) => _SaveSettings();

    partial void OnPushToTalkKeyNameChanged(string value) => _SaveSettings();

    partial void OnSelectedReadingLevelChanged(ReadingLevelOption value) => _SaveSettings();

    // Raised once a change here is actually on disk. The key and the on/off flag are read when the hotkey is
    // armed and when the assistant is asked for, so without this a setting would be a field that remembers what
    // you typed and changes nothing until the next restart — the failure `GlobalHotkeyCoordinator` already
    // re-arms on `VoiceSettingsSaved` to avoid for F9.
    public event EventHandler? Saved;

    private void _SaveSettings()
    {
        if (_loading || _settingsStore is null)
        {
            return;
        }

        _lastLoadedSettings = _lastLoadedSettings with
        {
            IsEnabled = IsEnabled,
            SpeakReplies = SpeakReplies,
            PushToTalkKeyName = string.IsNullOrWhiteSpace(PushToTalkKeyName) ? "F10" : PushToTalkKeyName.Trim(),
            ReadingLevel = SelectedReadingLevel.Value,
            // Written from the rows rather than merged into what was loaded: the rows already carry every stored
            // key (see _RebuildConsentBypassRowsAsync), so this is a full replacement and unticking a box actually
            // removes the permission instead of leaving it on disk under a row that no longer shows it.
            ConsentBypassSources = [.. ConsentBypassSources.Where(row => row.BypassLowRisk).Select(row => row.Key)],
            ConsentBypassDangerousSources = [.. ConsentBypassSources.Where(row => row.BypassDangerous).Select(row => row.Key)],
        };

        OnPropertyChanged(nameof(HasConsentBypass));
        _ = _SaveAndAnnounceAsync(_lastLoadedSettings);
    }

    // Announced after the write, not alongside it: every subscriber re-reads the store, so a signal raised while
    // the save was still in flight would have them acting on the values it is replacing.
    private async Task _SaveAndAnnounceAsync(AssistantSettings settings)
    {
        await _settingsStore!.SaveAsync(settings).ConfigureAwait(true);
        Saved?.Invoke(this, EventArgs.Empty);
    }
}

// One source on the assistant's consent-bypass list (#AC-575): who asks, and the two switches for it.
// *Two checkboxes, not a three-state picker.* `BypassDangerous` covers shell commands, session
// hand-offs with the operator's rights and arbitrary egress; `BypassLowRisk` covers the idempotent
// rest. A dropdown with "nothing / harmless / everything" would put the second one mouse movement away from the
// first, and they are not the same decision — so they are not the same control. Neither implies the other: the
// broker asks for exactly the one that matches the request's risk.
//
// `key`: The host-stamped identity the switch is stored under — a plugin id, or a host source's label. Never shown as the primary name, and never editable.
// `label`: What to call it on screen.
// `isOrphan`:
// Whether this row's key is neither in `ConsentSourceCatalog.HostSources` nor was ever seen asking
// in the consent trail — a switched-on source this build no longer recognises, most often because its key
// changed underneath it (a plugin id, say). The row still works — it can still be switched off — it is only
// marked so the operator does not mistake it for a second, live source.
public sealed partial class ConsentBypassSourceViewModel(string key, string label, bool isOrphan = false) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    public bool IsOrphan { get; } = isOrphan;

    // The stamped key, shown under the label when the two differ — which is exactly when the label came from a
    // plugin. A plugin that names itself after a cockpit source then reads as its own id on this list rather than
    // borrowing the other's name; the switch was already keyed on the id, this is so the operator can see that.
    // Never set alongside `IsOrphan` — an orphan's label is its own key (see the rebuild that
    // constructs it), so the two are never both non-null.
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
