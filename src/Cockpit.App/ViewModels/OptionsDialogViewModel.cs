using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The Options → Voice "Assistant" block (AC-543): the master on/off switch, the Assistant Profile picker, the
/// push-to-talk hotkey, and the independent read-replies-aloud switch. Self-contained the same way
/// <see cref="SecurityOptionsViewModel"/> is — every constructor argument is optional so it also constructs for
/// design-time preview and for a unit test that only cares about a couple of them, without needing the whole
/// <c>CockpitViewModel</c> dependency graph in scope.
/// </summary>
/// <remarks>
/// <b>Off does not touch the profile.</b> <see cref="IsEnabled"/> persists through <see cref="_settingsStore"/>,
/// which owns the <c>assistant</c> section; the Assistant Profile slot lives in its own <c>assistantProfile</c>
/// section behind <see cref="_profileStore"/> (AC-543's slot design) and is never written by this switch. Turning
/// the feature off and back on always finds the same profile still pointed at — that is the whole reason the two
/// are separate stores rather than one flag next to the profile.
/// <para>
/// <b>Speaking and being enabled are two decisions, not one.</b> <see cref="SpeakReplies"/> persists on its own
/// and is read back independently of <see cref="IsEnabled"/> — someone using the assistant as a text-only
/// assistant in a shared room needs the assistant on and speech off at the same time, which a derived flag could
/// never express (criterion 9).
/// </para>
/// </remarks>
public sealed partial class AssistantOptionsViewModel(
    IAssistantSettingsStore? settingsStore = null,
    IAssistantProfileStore? profileStore = null,
    ISessionProfileStore? sessionProfileStore = null,
    IConsentAuditLog? consentAuditLog = null) : ObservableObject
{
    private readonly IAssistantSettingsStore? _settingsStore = settingsStore;
    private readonly IAssistantProfileStore? _profileStore = profileStore;
    private readonly ISessionProfileStore? _sessionProfileStore = sessionProfileStore;
    private readonly IConsentAuditLog? _consentAuditLog = consentAuditLog;

    /// <summary>True only while <see cref="RefreshAsync"/> is seeding properties from disk, so the change handlers below do not write the same value straight back out.</summary>
    private bool _loading;

    /// <summary>
    /// The settings as last loaded (or saved), kept around so a save from this view model only overwrites the
    /// three fields it owns. <see cref="AssistantSettings.AlwaysOnCostAcknowledged"/> lives in the same record but
    /// is the indicator's to set (AC-543 comment 5), and constructing a bare <c>new AssistantSettings()</c> on
    /// every save would silently reset it to its default underneath it.
    /// </summary>
    private AssistantSettings _lastLoadedSettings = new();

    /// <summary>The Assistant Profile's fixed name (criterion: shown under its slot name, never the record's own label).</summary>
    public static string ProfileSlotDisplayName => AssistantProfileSlot.DisplayName;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _speakReplies = true;

    [ObservableProperty]
    private string _pushToTalkKeyName = "F10";

    /// <summary>The three reading levels the chat window can render at (AC-138) — the same list an SDK session's own header dropdown offers.</summary>
    public IReadOnlyList<ReadingLevelOption> ReadingLevels => SessionOptionCatalog.ReadingLevels;

    [ObservableProperty]
    private ReadingLevelOption _selectedReadingLevel = SessionOptionCatalog.DefaultReadingLevel;

    /// <summary>Existing session profiles the Assistant Profile slot can be repointed at. Never includes the slot itself — it is not a session profile and does not live in this list (criterion 5).</summary>
    public ObservableCollection<SessionProfile> AvailableProfiles { get; } = [];

    [ObservableProperty]
    private SessionProfile? _selectedProfile;

    /// <summary>Why the slot has no record right now. Empty while <see cref="SelectedProfile"/> is set — the UI shows one or the other, never both.</summary>
    [ObservableProperty]
    private string? _profileUnsetReason;

    /// <summary>
    /// The profile in <see cref="AvailableProfiles"/> that <see cref="RefreshProfileSnapshotCommand"/> would take a
    /// fresh copy from, or <see langword="null"/> when the list holds none by that name any more. Matched by label,
    /// and used for nothing else — see the command for why that is safe here and would not be anywhere else.
    /// </summary>
    private SessionProfile? _profileToRefreshFrom;

    /// <summary>
    /// The line under the picker saying, in words, that the assistant runs on a <em>copy</em> of the chosen profile
    /// and what that costs. <see langword="null"/> while the slot has no record — there is then nothing to have
    /// copied, and <see cref="ProfileUnsetReason"/> is what the page shows instead.
    /// </summary>
    /// <remarks>
    /// <b>The price of the slot design, written down where the choice is made.</b> The slot carries a whole
    /// <see cref="SessionProfile"/> taken at the moment it was picked, deliberately: it is found by nothing —
    /// not by label, not by position in the profile list — so renaming or deleting a profile cannot quietly cut the
    /// assistant loose (AC-410, and <see cref="AssistantProfileSlot"/>'s own remarks). What that buys in
    /// robustness it costs in freshness: an edit to the profile the copy came from does not reach the assistant.
    /// Measured on a real config, the profile said <c>bypassPermissions</c> and the assistant's copy still said
    /// <c>default</c>, and no surface anywhere said the two were different things. Silence was the actual defect;
    /// this line and the button beside it are the fix.
    /// </remarks>
    [ObservableProperty]
    private string? _profileSnapshotNote;

    /// <summary>Whether there is a live profile of that name to take a fresh copy from. False leaves the button disabled rather than absent, so the note above it still reads.</summary>
    public bool CanRefreshProfileSnapshot => _profileToRefreshFrom is not null;

    /// <summary>
    /// Takes a fresh copy of the profile the slot's record was named after — the one click that makes an edit to
    /// that profile reach the assistant.
    /// </summary>
    /// <remarks>
    /// <b>Why matching by label is not AC-410 creeping back in.</b> AC-410's bug was a lookup that could
    /// <em>lose</em>: the slot was resolved by name on every read, so a rename made it read as gone and the
    /// assistant lost its profile without anyone touching the assistant. Nothing here reads that way. The slot is
    /// still resolved by nothing at all — <see cref="IAssistantProfileStore.LoadAsync"/> hands back the stored
    /// record whatever any list says — and the label is consulted in exactly one place: to offer this button a
    /// candidate. A rename therefore costs the button, never the profile; a deleted profile costs the button,
    /// never the profile. The write it performs is the same
    /// <see cref="IAssistantProfileStore.RepointAsync"/> that picking from the dropdown performs, and it happens
    /// only when the operator clicks.
    /// <para>
    /// <b>And why it is a button rather than an automatic refresh.</b> Doing this silently on open would mean a
    /// profile renamed away and a <em>new</em> one created under the old name would repoint the assistant at a
    /// record it was never pointed at — the impostor case that is precisely why the slot does not resolve by name.
    /// A click is the operator saying that this is the profile they meant.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRefreshProfileSnapshot))]
    private async Task RefreshProfileSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_profileStore is null || _profileToRefreshFrom is not { } record)
        {
            return;
        }

        var slot = await _profileStore.RepointAsync(record, cancellationToken).ConfigureAwait(true);
        ProfileUnsetReason = slot.UnsetReason;
        _ApplyProfileSnapshotNote(slot);
    }

    /// <summary>
    /// Works out what the note says and what the button would copy from, for the slot as it now stands. Called
    /// from the load and again after a refresh, so a slot that was repointed does not leave a note describing the
    /// record it no longer holds.
    /// </summary>
    private void _ApplyProfileSnapshotNote(AssistantProfileSlot slot)
    {
        _profileToRefreshFrom = slot.Profile is { } record
            ? AvailableProfiles.FirstOrDefault(profile => profile.Label == record.Label)
            : null;

        ProfileSnapshotNote = slot.Profile switch
        {
            null => null,
            // Says what it is and what it costs, in the two states that differ. Both name the record, because the
            // first question anyone asks of a copy is what it is a copy of.
            { Label: var label } when _profileToRefreshFrom is not null =>
                $"The assistant runs on its own copy of \"{label}\", taken when you picked it. Later edits to that profile — a permission mode, a model — reach the assistant only when you refresh here, and then at its next start.",
            { Label: var label } =>
                $"The assistant runs on its own copy of \"{label}\". No profile by that name is in the list any more, so there is nothing to refresh from — the copy keeps working, and picking a profile above replaces it.",
        };

        OnPropertyChanged(nameof(CanRefreshProfileSnapshot));
        RefreshProfileSnapshotCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The consent-bypass switches, one row per source (#AC-575). Filled by <see cref="RefreshAsync"/> from names
    /// the host stamps — never from anything the operator types — see <see cref="_RebuildConsentBypassRowsAsync"/>.
    /// </summary>
    public ObservableCollection<ConsentBypassSourceViewModel> ConsentBypassSources { get; } = [];

    /// <summary>Whether any source is switched on — the one-line summary above the list, so the page says so before the rows are read.</summary>
    public bool HasConsentBypass => ConsentBypassSources.Any(row => row.BypassLowRisk || row.BypassDangerous);

    /// <summary>Loads settings, the profile slot, and the profiles it can be repointed at. Safe to call with no stores wired (design-time/tests) — it then simply leaves the defaults in place.</summary>
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

            if (_sessionProfileStore is not null)
            {
                AvailableProfiles.Clear();
                foreach (var profile in await _sessionProfileStore.LoadAsync(cancellationToken).ConfigureAwait(true))
                {
                    AvailableProfiles.Add(profile);
                }
            }

            if (_profileStore is not null)
            {
                var slot = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(true);
                SelectedProfile = slot.Profile is null
                    ? null
                    : AvailableProfiles.FirstOrDefault(p => p.Label == slot.Profile.Label) ?? slot.Profile;
                ProfileUnsetReason = slot.UnsetReason;
                _ApplyProfileSnapshotNote(slot);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Rebuilds the bypass rows (#AC-575). Three sources, all host-stamped, none of them free text:
    /// <list type="number">
    /// <item>the cockpit's own consent callers, from <see cref="ConsentSourceCatalog"/> — the same constants those
    /// gates build their <c>ConsentSource</c> from, so a renamed label moves the switch with it;</item>
    /// <item>every source that has actually asked, read off the consent trail. This is what puts plugins on the
    /// list: a plugin asks through <c>ICockpitHost</c>, which stamps its plugin id host-side, and there is no
    /// compile-time list of installed plugins to enumerate instead. The trail's label is only ever <em>shown</em> —
    /// the switch is keyed on the stamped id, so a plugin that labels itself "Terminal MCP" gets a row of its own
    /// under its own id rather than a share of the terminal's;</item>
    /// <item>anything already switched on, so a source whose plugin is currently uninstalled still has a row to be
    /// switched off from, instead of a permission that is set and invisible.</item>
    /// </list>
    /// </summary>
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

    /// <summary>
    /// The in-flight repoint started by the most recent <see cref="SelectedProfile"/> change, or <see langword="null"/>
    /// once none is pending. A test seam: the change handler below is <c>async void</c> (an <c>ObservableProperty</c>
    /// partial method cannot return <see cref="Task"/>), so this is what lets a caller await the write it triggers
    /// instead of racing it.
    /// </summary>
    public Task? PendingProfileRepoint { get; private set; }

    // Repoints the slot at the newly chosen record (criteria 2–4: this is the only write that produces a
    // configured slot, and it can only replace the whole record, never edit the one behind it into a different
    // provider). A failed repoint leaves the previous selection in place rather than clearing it — an empty slot
    // with no reason is exactly what AssistantProfileStore's contract rules out.
    partial void OnSelectedProfileChanged(SessionProfile? value)
    {
        if (_loading || _profileStore is null || value is null)
        {
            return;
        }

        PendingProfileRepoint = _RepointProfileAsync(value);
    }

    private async Task _RepointProfileAsync(SessionProfile record)
    {
        var slot = await _profileStore!.RepointAsync(record).ConfigureAwait(true);
        ProfileUnsetReason = slot.UnsetReason;
        // The note names the record the slot now holds, so picking a different profile moves it rather than
        // leaving a sentence about the previous one under the new selection.
        _ApplyProfileSnapshotNote(slot);
    }

    /// <summary>
    /// Raised once a change here is actually on disk. The key and the on/off flag are read when the hotkey is
    /// armed and when the assistant is asked for, so without this a setting would be a field that remembers what
    /// you typed and changes nothing until the next restart — the failure <c>GlobalHotkeyCoordinator</c> already
    /// re-arms on <c>VoiceSettingsSaved</c> to avoid for F9.
    /// </summary>
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

/// <summary>
/// One source on the assistant's consent-bypass list (#AC-575): who asks, and the two switches for it.
/// </summary>
/// <remarks>
/// <b>Two checkboxes, not a three-state picker.</b> <see cref="BypassDangerous"/> covers shell commands, session
/// hand-offs with the operator's rights and arbitrary egress; <see cref="BypassLowRisk"/> covers the idempotent
/// rest. A dropdown with "nothing / harmless / everything" would put the second one mouse movement away from the
/// first, and they are not the same decision — so they are not the same control. Neither implies the other: the
/// broker asks for exactly the one that matches the request's risk.
/// </remarks>
/// <param name="key">The host-stamped identity the switch is stored under — a plugin id, or a host source's label. Never shown as the primary name, and never editable.</param>
/// <param name="label">What to call it on screen.</param>
/// <param name="isOrphan">
/// Whether this row's key is neither in <see cref="ConsentSourceCatalog.HostSources"/> nor was ever seen asking
/// in the consent trail — a switched-on source this build no longer recognises, most often because its key
/// changed underneath it (a plugin id, say). The row still works — it can still be switched off — it is only
/// marked so the operator does not mistake it for a second, live source.
/// </param>
public sealed partial class ConsentBypassSourceViewModel(string key, string label, bool isOrphan = false) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    public bool IsOrphan { get; } = isOrphan;

    /// <summary>
    /// The stamped key, shown under the label when the two differ — which is exactly when the label came from a
    /// plugin. A plugin that names itself after a cockpit source then reads as its own id on this list rather than
    /// borrowing the other's name; the switch was already keyed on the id, this is so the operator can see that.
    /// Never set alongside <see cref="IsOrphan"/> — an orphan's label is its own key (see the rebuild that
    /// constructs it), so the two are never both non-null.
    /// </summary>
    public string? KeyDetail => string.Equals(Key, Label, StringComparison.Ordinal) ? null : Key;

    /// <summary>Skip the card for this source's low-risk, idempotent actions.</summary>
    [ObservableProperty]
    private bool _bypassLowRisk;

    /// <summary>Skip it for this source's dangerous actions too. Off by default, and never turned on by the switch above.</summary>
    [ObservableProperty]
    private bool _bypassDangerous;

    /// <summary>Set by the page that owns the row, so a tick persists straight away like every other switch on this dialog.</summary>
    internal Action? Changed { get; set; }

    partial void OnBypassLowRiskChanged(bool value) => Changed?.Invoke();

    partial void OnBypassDangerousChanged(bool value) => Changed?.Invoke();
}
