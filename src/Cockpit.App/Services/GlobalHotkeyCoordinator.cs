using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Hotkeys;
using Cockpit.Core.Toasts;

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
/// <para>
/// AC-71: neither <see cref="IGlobalHotkeyService"/> backend can say whether some other cockpit instance
/// already has a key — the OS-level "armed" it reports is not the same claim as "and nobody else is". An
/// <see cref="IHotkeyExclusivityGuard"/> claim per hotkey id is what actually answers that, cross-process and
/// the same on every platform; only a binding this instance holds the claim for reaches
/// <see cref="IGlobalHotkeyService.StartAsync"/>. A binding refused a claim retries on a timer rather than
/// waiting for a restart, since the holder releasing it (the other instance closing) is exactly the case that
/// used to need one.
/// </para>
/// </remarks>
public sealed class GlobalHotkeyCoordinator : ISingletonService, IDisposable
{
    private static readonly TimeSpan DefaultConflictRetryInterval = TimeSpan.FromSeconds(15);

    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IVoiceSettingsStore _voiceSettingsStore;
    private readonly IScreenshotSettingsStore _screenshotSettingsStore;
    private readonly IHotkeyExclusivityGuard _guard;
    private readonly IToastService _toasts;
    private readonly ILogger<GlobalHotkeyCoordinator> _logger;
    private readonly TimeSpan _conflictRetryInterval;

    /// <summary>This instance's live claims, by hotkey id — held until the feature switches off or the process exits.</summary>
    private readonly Dictionary<string, IDisposable> _claims = [];

    /// <summary>Hotkey ids asked for whose claim another instance holds — what a toast is owed for, once, and what the retry timer watches.</summary>
    private IReadOnlySet<string> _conflicted = new HashSet<string>();

    /// <summary>Serializes arm attempts: the retry timer and a settings save can otherwise land on <see cref="_claims"/> at the same time.</summary>
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private Timer? _retryTimer;
    private bool _disposed;

    /// <param name="conflictRetryInterval">
    /// How often a conflicted binding tries again. Overridable for tests, which cannot wait fifteen seconds to
    /// watch a retry happen — the same reasoning as <see cref="ScheduledResumeCoordinator"/>'s tick interval.
    /// </param>
    public GlobalHotkeyCoordinator(
        IGlobalHotkeyService hotkeys,
        IVoiceSettingsStore voiceSettingsStore,
        IScreenshotSettingsStore screenshotSettingsStore,
        IHotkeyExclusivityGuard guard,
        IToastService toasts,
        ILogger<GlobalHotkeyCoordinator> logger,
        TimeSpan? conflictRetryInterval = null)
    {
        _hotkeys = hotkeys;
        _voiceSettingsStore = voiceSettingsStore;
        _screenshotSettingsStore = screenshotSettingsStore;
        _guard = guard;
        _toasts = toasts;
        _logger = logger;
        _conflictRetryInterval = conflictRetryInterval ?? DefaultConflictRetryInterval;

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
    /// The keys the operator has switched on, whether or not arming them worked. Kept apart from
    /// <see cref="_armed"/> precisely so the two can disagree: that disagreement is a hotkey the operator asked
    /// for and did not get, which is the one thing this class used to be unable to say out loud (AC-332).
    /// </summary>
    private IReadOnlySet<string> _asked = new HashSet<string>();

    /// <summary>
    /// The line a settings screen shows about one hotkey. Three truths behind it, and a fourth that has to come
    /// first: the operator never switched it on, in which case there is nothing to report — telling them their
    /// desktop has not bound a key they never asked for sends them into their shortcut settings looking for
    /// nothing. Then: Windows armed the key it was given, a Wayland compositor bound whatever it chose (or is
    /// still waiting for the operator to choose), and macOS has no global hotkey at all.
    /// </summary>
    /// <param name="unboundMessage">Shown when the key is armed but no desktop has bound it yet — name the shortcut the operator should look for.</param>
    /// <param name="unsupportedMessage">Shown on a platform with no global hotkey at all, where the honest thing is to point at what does work.</param>
    /// <param name="failedMessage">Shown when the operator switched the key on and arming it did not work — the case that used to look exactly like never having switched it on.</param>
    /// <remarks>
    /// The failed case was silence until AC-332. A key the operator had switched on and the desktop had refused
    /// read the same as one they never enabled: an empty line, no error, and a shortcut that simply did nothing
    /// when pressed. That is the failure this whole reporting path exists to prevent, and it had it too.
    /// </remarks>
    public string DescribeTrigger(
        string hotkeyId, string unboundMessage, string unsupportedMessage, string failedMessage) =>
        IsArmed(hotkeyId)
            ? TriggerDescriptionFor(hotkeyId) ?? (OperatingSystem.IsMacOS() ? unsupportedMessage : unboundMessage)
            : _asked.Contains(hotkeyId) ? failedMessage : string.Empty;

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

        // Serializes against the retry timer (below) and a concurrent settings save — both call this method, and
        // both touch _claims/_asked/_armed.
        var gateAcquired = false;

        try
        {
            await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;

            var bindings = await _LoadBindingsAsync(cancellationToken).ConfigureAwait(false);

            // Noted before the attempt, so that a refusal further down still leaves a record of what was asked
            // for. Without it a key the desktop turned away is indistinguishable from one nobody wanted.
            _asked = bindings.Select(binding => binding.Id).ToHashSet();

            if (GlobalHotkeyConflictCheck.Describe(bindings) is { } clash)
            {
                _logger.LogWarning("Global hotkey conflict: {Conflict}", clash);
            }

            _ReleaseClaimsNoLongerAsked();
            var (owned, conflicted) = _ClaimBindings(bindings);
            _ReportConflicts(conflicted);

            await _hotkeys.StartAsync(owned, cancellationToken).ConfigureAwait(false);
            _armed = owned.Select(binding => binding.Id).ToHashSet();

            foreach (var binding in owned)
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
            if (gateAcquired)
            {
                // Managed before the gate opens, not after: two ApplyAsync calls racing past the release would
                // otherwise both see the same _conflicted set and could create two retry timers, or one could
                // dispose the timer the instant after the other decided it was still needed.
                _ManageRetryTimer();
                _applyGate.Release();
            }

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

    /// <summary>Drops the claim for any hotkey id no longer asked for — a feature just switched off, or a settings read changed what it wants.</summary>
    private void _ReleaseClaimsNoLongerAsked()
    {
        foreach (var goneId in _claims.Keys.Where(id => !_asked.Contains(id)).ToList())
        {
            _claims.Remove(goneId, out var claim);
            claim?.Dispose();
        }
    }

    /// <summary>
    /// Splits the requested bindings into what this instance may arm and what another cockpit instance already
    /// holds. A binding this instance already claimed (the ordinary re-arm path) is not asked for again — only a
    /// binding new to <see cref="_claims"/> goes through <see cref="IHotkeyExclusivityGuard.TryAcquire"/>.
    /// </summary>
    private (List<GlobalHotkeyBinding> Owned, List<GlobalHotkeyBinding> Conflicted) _ClaimBindings(
        IReadOnlyList<GlobalHotkeyBinding> bindings)
    {
        var owned = new List<GlobalHotkeyBinding>();
        var conflicted = new List<GlobalHotkeyBinding>();

        foreach (var binding in bindings)
        {
            if (!_claims.ContainsKey(binding.Id))
            {
                if (_guard.TryAcquire(binding.Id) is not { } claim)
                {
                    conflicted.Add(binding);
                    continue;
                }

                _claims[binding.Id] = claim;
            }

            owned.Add(binding);
        }

        return (owned, conflicted);
    }

    /// <summary>
    /// Logs every conflicted binding, and tells the operator about the ones new to the conflicted set — once,
    /// not on every retry tick, since a conflict that has not resolved yet is not new information.
    /// </summary>
    private void _ReportConflicts(IReadOnlyList<GlobalHotkeyBinding> conflicted)
    {
        foreach (var binding in conflicted)
        {
            _logger.LogWarning(
                "Global hotkey {HotkeyId} could not be armed: another cockpit instance already holds '{Key}'.",
                binding.Id,
                binding.KeyName);

            if (!_conflicted.Contains(binding.Id))
            {
                _toasts.Show(
                    $"“{binding.Description}” is switched on but another cockpit instance already has that key. " +
                    "It will arm here once that instance releases it.",
                    ToastSeverity.Warning);
            }
        }

        _conflicted = conflicted.Select(binding => binding.Id).ToHashSet();
    }

    /// <summary>
    /// Keeps a retry running for as long as some binding is conflicted, and stops it the moment none is — the
    /// re-arm that used to need a restart when the other instance closed.
    /// </summary>
    private void _ManageRetryTimer()
    {
        if (_disposed)
        {
            return;
        }

        if (_conflicted.Count == 0)
        {
            _retryTimer?.Dispose();
            _retryTimer = null;
            return;
        }

        _retryTimer ??= new Timer(_OnRetryTick, null, _conflictRetryInterval, _conflictRetryInterval);
    }

    private void _OnRetryTick(object? state) => _ = _RetryConflictedAsync();

    /// <summary>
    /// A tick's worth of the ordinary re-arm path, kept deliberately cheaper: it only tries to claim what is
    /// still conflicted, and stops right there if nothing new came free. An idle tick otherwise ran the full
    /// <see cref="ApplyAsync"/> path every fifteen seconds for as long as one binding stayed conflicted — which
    /// meant re-registering an already-working binding with the OS service, and blanking <see cref="_armed"/>
    /// (so the settings screen briefly reported it as failed) for a key nothing was wrong with.
    /// </summary>
    private async Task _RetryConflictedAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _applyGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and this wait — the coordinator is shutting down and no longer
            // cares what this tick would have found.
            return;
        }

        bool anyNewlyClaimed;
        try
        {
            if (_disposed)
            {
                return;
            }

            anyNewlyClaimed = false;
            foreach (var hotkeyId in _conflicted)
            {
                if (_guard.TryAcquire(hotkeyId) is { } claim)
                {
                    _claims[hotkeyId] = claim;
                    anyNewlyClaimed = true;
                }
            }
        }
        finally
        {
            _applyGate.Release();
        }

        // Something conflicted just got claimed — hand it to the ordinary arm path, which reloads settings and
        // registers the full set with the OS service. Nothing new claimed means nothing changed: stop here.
        if (anyNewlyClaimed)
        {
            await ApplyAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Under the same gate as every arm attempt: a retry tick or a settings save already past the
        // disposed-check above must finish (or itself see _disposed after acquiring the gate) before this
        // clears the claims it might otherwise still be touching.
        _applyGate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _retryTimer?.Dispose();
            _retryTimer = null;

            foreach (var claim in _claims.Values)
            {
                claim.Dispose();
            }

            _claims.Clear();
        }
        finally
        {
            _applyGate.Release();
        }

        _applyGate.Dispose();
    }
}
