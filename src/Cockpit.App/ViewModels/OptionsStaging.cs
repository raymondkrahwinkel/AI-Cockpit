using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cockpit.App.ViewModels;

// This list only answers "has the operator changed anything", for the footer's indicator and the warning on Escape
// (AC-999).
internal static class OptionsStaging
{
    // A character no setting can contain, so two different sets of values cannot join into the same string.
    private const char Separator = (char)1;

    // Bound two-way to a control the operator edits, and persisted on Apply. Paths, because three of them live
    // on the sub-view models the dialog reaches through (`Security`, `AssistantOptions`).
    public static readonly string[] EditedProperties =
    [
        "AutoCloseOnExit",
        "CheckForUpdatesOnStartup",
        "CloneRoot",
        "CombineQueuedMessages",
        "DiscordNotificationsEnabled",
        "GlobalFocusRailLayout",
        "GlobalSingleSessionLayout",
        "GlobalStackSessionsVertically",
        "IdleThresholdMinutes",
        "IncludeNightlyBuilds",
        "LocalNotificationsEnabled",
        "LogDiagnosticSnapshots",
        "MemoryBudgetPercent",
        "MinimizeToTrayOnClose",
        "NotifyOnCiFailure",
        "NotifyOnSessionFinished",
        "NotifyOnSessionIdle",
        "NotifyWhenAllSessionsIdle",
        "OrchestratorMcpEnabled",
        "RenderBackendSelection",
        "ScreenshotGlobalHotkeyEnabled",
        "ScreenshotHotkeyKeyName",
        "ScreenshotPreviewEnabled",
        "SelectedInputDevice",
        "SelectedOutputDevice",
        "SelectedReadAloudLanguage",
        "SelectedSttLanguage",
        "SelectedTerminalShell",
        "SelectedTranscriptionModel",
        "SelectedTtsVoice",
        "SelectedVoiceBackendPreference",
        "SessionIdleMinutes",
        "ShowDebugControls",
        "ShowTimestamps",
        "ShowUsagePillContext",
        "ShowUsagePillRateWindows",
        "ShowUsagePillSessionUsage",
        "TerminalCustomFontFamily",
        "TerminalCustomShell",
        "TerminalFontSelection",
        "TerminalFontSize",
        "VoiceAutoSubmit",
        "VoiceCustomModelName",
        "VoiceEnabled",
        "VoiceGlobalPushToTalk",
        "VoiceOpenMicSilenceTimeoutMs",
        "VoicePushToTalkKeyName",
        "VoiceStopReadAloudLevelThreshold",
        "VoiceStopReadAloudWhenSpeaking",
        "VoiceTtsSpeed",
        "WakeAgentsByDefault",
        "WebhookUrl",
        "WorktreeRoot",
        "Security.AllowedDiscoveryRangesText",
        "Security.LockWithOperatingSystem",
        "Security.NodeEndpointEnabled",
        "Security.ShellAccessEnabled",
        "Security.TerminalAccessEnabled",
        "AssistantOptions.AlwaysOnTop",
        "AssistantOptions.ConsentBypassAll",
        "AssistantOptions.IsEnabled",
        "AssistantOptions.PushToTalkKeyName",
        "AssistantOptions.SelectedReadingLevel",
        "AssistantOptions.SpeakReplies",
    ];

    // Cancel does not undo what these feed, which is exactly why each one has to be named here rather than simply left
    // off the list above: a new control lands in a category on purpose or the guard test fails.
    public static readonly string[] ImmediateOrTransient =
    [
        "IsTestingMic",
        "OptionsSearchText",
        "BackupIncludesCredentials",
        "BackupIncludesProfiles",
        "Security.PairWithNodeAddress",
    ];

    // The handlers in `OptionsDialog.axaml.cs` that act on the spot and are not undone by Cancel (AC-999 §6).
    public static readonly string[] ImmediateActionHandlers =
    [
        "OnEnableEncryption",
        "OnDisableEncryption",
        "OnChangePassword",
        "OnCheckForUpdates",
        "OnOpenUpdate",
        "OnCreateBackup",
        "OnRestoreBackup",
        "OnExportAssistantMemory",
        "OnImportAssistantMemory",
        "OnRefreshDiagnostics",
        "OnCopyDiagnostics",
    ];

    // Click handlers that, unlike the ones above, *are* undone by Cancel — they only fill in a field the Profiles
    // fingerprint already covers (`EditableProfileViewModel.ConfigDir`/`DefaultWorkingDirectory`/`ExecutablePath`,
    // reverted with the rest of the profile by `Profiles.LoadAsync()`).
    public static readonly string[] ReversibleValueHandlers =
    [
        "OnBrowseProfileConfigDir",
        "OnBrowseProfileWorkingDirectory",
        "OnBrowseProfileExecutable",
    ];

    // A cheap value-identity of everything staged, compared against the same string taken when the dialog
    // opened. Cheaper and less brittle than mirroring 60-odd properties into a buffer object, and it is only
    // ever used for equality — the string itself is never shown or stored.
    public static string Fingerprint(CockpitViewModel cockpit)
    {
        var parts = new List<string>(EditedProperties.Length + 2);
        foreach (var path in EditedProperties)
        {
            parts.Add(_Format(_Read(cockpit, path)));
        }

        // Two collections the dialog edits in place, which no property path reaches.
        parts.Add(string.Join(Separator, cockpit.ShortcutRows.Select(row => row.Gesture)));
        parts.Add(cockpit.UsageThresholdSettings is { } thresholds
            ? string.Join(
                Separator,
                thresholds.Providers.Concat(thresholds.AssistantProviders)
                    .SelectMany(provider => provider.Signals)
                    .Select(signal => $"{signal.SignalKey}={_Format(signal.Threshold)}"))
            : string.Empty);

        // Profiles (AC-1001): a full serialization of every edited row's would-be-saved shape, added/removed rows
        // included, rather than a hand-kept list of property paths — the same reasoning `ToProfile()` already gives for
        // not exposing a typed selection per provider.
        parts.Add(cockpit.Profiles is { } profiles
            ? string.Join(Separator, profiles.Profiles.Select(profile => _ProfileFingerprint(profile.ToProfile())))
            : string.Empty);

        return string.Join(Separator, parts);
    }

    private static string _ProfileFingerprint(Cockpit.Core.Profiles.SessionProfile profile) =>
        JsonSerializer.Serialize(profile) + Separator + JsonSerializer.Serialize(profile.ProviderConfig, profile.ProviderConfig.GetType());

    private static object? _Read(object? root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is null)
            {
                return null;
            }

            current = current.GetType().GetProperty(segment)?.GetValue(current);
        }

        return current;
    }

    // Reference types that are not `IFormattable` are combo-box items picked from a fixed catalogue, so which
    // instance is selected is the change worth seeing — identity says that without every option type having to
    // override ToString.
    private static string _Format(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        // `bool` is neither a string nor `IFormattable`, and boxing it into the identity branch below would give
        // the same value a different answer on every call — every setting would read as changed.
        ValueType boxed => boxed.ToString() ?? string.Empty,
        _ => RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture),
    };
}
