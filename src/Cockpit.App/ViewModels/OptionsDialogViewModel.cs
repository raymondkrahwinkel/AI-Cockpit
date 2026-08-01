using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
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
    ISessionProfileStore? sessionProfileStore = null) : ObservableObject
{
    private readonly IAssistantSettingsStore? _settingsStore = settingsStore;
    private readonly IAssistantProfileStore? _profileStore = profileStore;
    private readonly ISessionProfileStore? _sessionProfileStore = sessionProfileStore;

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

    /// <summary>Existing session profiles the Assistant Profile slot can be repointed at. Never includes the slot itself — it is not a session profile and does not live in this list (criterion 5).</summary>
    public ObservableCollection<SessionProfile> AvailableProfiles { get; } = [];

    [ObservableProperty]
    private SessionProfile? _selectedProfile;

    /// <summary>Why the slot has no record right now. Empty while <see cref="SelectedProfile"/> is set — the UI shows one or the other, never both.</summary>
    [ObservableProperty]
    private string? _profileUnsetReason;

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
            }
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnIsEnabledChanged(bool value) => _SaveSettings();

    partial void OnSpeakRepliesChanged(bool value) => _SaveSettings();

    partial void OnPushToTalkKeyNameChanged(string value) => _SaveSettings();

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
        };

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
