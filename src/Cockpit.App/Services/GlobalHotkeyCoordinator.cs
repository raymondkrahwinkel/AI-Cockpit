using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Hotkeys;

namespace Cockpit.App.Services;

/// <summary>
/// Owns the cockpit's desktop-wide keys: reads what each feature wants, arms them as one set, and hands the
/// presses on to whoever asked for them. The single arm point exists because
/// <see cref="IGlobalHotkeyService.StartAsync"/> registers a whole set — two features each arming their own
/// key would mean the second wiping out the first.
/// </summary>
/// <remarks>
/// Feature coordinators (<see cref="VoicePushToTalkCoordinator"/>, <see cref="ScreenshotCoordinator"/>)
/// subscribe here and filter on the hotkey id rather than talking to the OS service themselves. The events are
/// re-raised, not forwarded through a shared subscription list, so a coordinator built later still hears
/// everything: this one subscribes once, for the life of the app.
/// <para>
/// Threading is unchanged from the service's: <see cref="Pressed"/>/<see cref="Released"/> arrive on the
/// backend's own thread, never the UI thread. Marshalling stays each subscriber's job, as it was.
/// </para>
/// </remarks>
public sealed class GlobalHotkeyCoordinator : ISingletonService
{
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IVoiceSettingsStore _voiceSettingsStore;
    private readonly IScreenshotSettingsStore _screenshotSettingsStore;
    private readonly ILogger<GlobalHotkeyCoordinator> _logger;

    public GlobalHotkeyCoordinator(
        IGlobalHotkeyService hotkeys,
        IVoiceSettingsStore voiceSettingsStore,
        IScreenshotSettingsStore screenshotSettingsStore,
        ILogger<GlobalHotkeyCoordinator> logger)
    {
        _hotkeys = hotkeys;
        _voiceSettingsStore = voiceSettingsStore;
        _screenshotSettingsStore = screenshotSettingsStore;
        _logger = logger;

        _hotkeys.Pressed += (_, id) => Pressed?.Invoke(this, id);
        _hotkeys.Released += (_, id) => Released?.Invoke(this, id);
        _hotkeys.TriggerDescriptionsChanged += (_, _) => TriggerDescriptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A registered hotkey went down; the argument is its <see cref="GlobalHotkeyBinding.Id"/>.</summary>
    public event EventHandler<string>? Pressed;

    /// <summary>A registered hotkey came back up; the argument is its <see cref="GlobalHotkeyBinding.Id"/>.</summary>
    public event EventHandler<string>? Released;

    /// <summary>Raised when what the desktop reports about any binding changes — see <see cref="IGlobalHotkeyService.TriggerDescriptionsChanged"/>.</summary>
    public event EventHandler? TriggerDescriptionsChanged;

    /// <summary>How the given hotkey is actually triggered, to show the operator — or null when nothing is armed for it.</summary>
    public string? TriggerDescriptionFor(string hotkeyId) => _hotkeys.TriggerDescriptionFor(hotkeyId);

    /// <summary>
    /// Whether the operator has this hotkey switched on. Distinct from having a trigger description, which is
    /// null both for a key nobody asked for and for one the desktop has not bound yet — and telling an operator
    /// their desktop has not bound a key they never enabled is a confusing thing to say.
    /// </summary>
    public bool IsArmed(string hotkeyId) => _armed.Contains(hotkeyId);

    private IReadOnlySet<string> _armed = new HashSet<string>();

    /// <summary>
    /// The line a settings screen shows about one hotkey. Three truths behind it, and a fourth that has to come
    /// first: the operator never switched it on, in which case there is nothing to report — telling them their
    /// desktop has not bound a key they never asked for sends them into their shortcut settings looking for
    /// nothing. Then: Windows armed the key it was given, a Wayland compositor bound whatever it chose (or is
    /// still waiting for the operator to choose), and macOS has no global hotkey at all.
    /// </summary>
    /// <param name="unboundMessage">Shown when the key is armed but no desktop has bound it yet — name the shortcut the operator should look for.</param>
    /// <param name="unsupportedMessage">Shown on a platform with no global hotkey at all, where the honest thing is to point at what does work.</param>
    public string DescribeTrigger(string hotkeyId, string unboundMessage, string unsupportedMessage) =>
        !IsArmed(hotkeyId)
            ? string.Empty
            : TriggerDescriptionFor(hotkeyId) ?? (OperatingSystem.IsMacOS() ? unsupportedMessage : unboundMessage);

    /// <summary>
    /// Arms exactly the keys the operator has switched on. Also the re-arm path: the OS service replaces its
    /// whole registration, so saving a changed key comes back through here rather than needing a restart.
    /// </summary>
    /// <remarks>
    /// Never throws. Its callers discard the task (app startup, a settings save), so anything thrown here used
    /// to land on a task nobody observes and be gone — and what it took with it was the hotkey. Reading the
    /// settings goes through the shared <c>cockpit.json</c>, which a write elsewhere in this process can briefly
    /// lock; on 2026-07-15 that raced at startup and F9 was dead for the whole session with not one line in the
    /// log to say so. It still cannot arm if the read fails — but now it says which.
    /// </remarks>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        // Dropped before the attempt, not after it fails. A re-arm that throws leaves nothing registered with the
        // desktop, and the old set would then have the settings screen reporting a key that no longer fires —
        // the very silence this whole reporting path exists to prevent, dressed as a working hotkey.
        _armed = new HashSet<string>();

        try
        {
            var bindings = await _LoadBindingsAsync(cancellationToken).ConfigureAwait(false);

            if (GlobalHotkeyConflictCheck.Describe(bindings) is { } clash)
            {
                _logger.LogWarning("Global hotkey conflict: {Conflict}", clash);
            }

            await _hotkeys.StartAsync(bindings, cancellationToken).ConfigureAwait(false);
            _armed = bindings.Select(binding => binding.Id).ToHashSet();

            foreach (var binding in bindings)
            {
                _logger.LogInformation(
                    "Global hotkey {HotkeyId} armed: asked for '{Key}', triggered by '{Trigger}'.",
                    binding.Id,
                    binding.KeyName,
                    _hotkeys.TriggerDescriptionFor(binding.Id) ?? "<nothing — this platform or desktop has not bound it>");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The global hotkeys could not be armed; they will not fire until the cockpit is restarted.");
        }
        finally
        {
            // In a finally, so the failure path reports too. Raised whether or not a description changed: a
            // feature whose key is now switched off, a platform that binds nothing at all (macOS), and an arm
            // that threw all produce no change event of their own — and each is a case where what the settings
            // screen says has to be re-read rather than left standing as a key that no longer fires.
            TriggerDescriptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What each feature wants, if it wants anything — a feature that is switched off contributes no binding, so nothing is registered with the desktop for it.</summary>
    private async Task<IReadOnlyList<GlobalHotkeyBinding>> _LoadBindingsAsync(CancellationToken cancellationToken)
    {
        var bindings = new List<GlobalHotkeyBinding>();

        var voice = await _voiceSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (voice is { IsEnabled: true, GlobalPushToTalk: true })
        {
            bindings.Add(new GlobalHotkeyBinding(GlobalHotkeys.PushToTalk, "Push to talk (hold)", voice.PushToTalkKeyName));
        }

        var screenshots = await _screenshotSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (screenshots.GlobalHotkeyEnabled)
        {
            bindings.Add(new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", screenshots.HotkeyKeyName));
        }

        return bindings;
    }
}
