using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Hotkeys;
using Cockpit.Core.Toasts;

namespace Cockpit.App.Services;

// AC-1013: owns the cockpit's desktop-wide keys, arming them as one set since `StartAsync` registers a whole
// set at once (two features arming their own key would wipe each other out). Feature coordinators subscribe
// and filter by hotkey id; threading stays the service's — `Pressed`/`Released` fire off the UI thread.
public sealed class GlobalHotkeyCoordinator : ISingletonService, IDisposable
{
    private static readonly TimeSpan DefaultConflictRetryInterval = TimeSpan.FromSeconds(15);

    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IVoiceSettingsStore _voiceSettingsStore;
    private readonly IScreenshotSettingsStore _screenshotSettingsStore;
    private readonly IAssistantSettingsStore _assistantSettingsStore;
    private readonly IHotkeyExclusivityGuard _guard;
    private readonly IToastService _toasts;
    private readonly ILogger<GlobalHotkeyCoordinator> _logger;
    private readonly TimeSpan _conflictRetryInterval;

    // This instance's live claims, by hotkey id — held until the feature switches off or the process exits.
    private readonly Dictionary<string, IDisposable> _claims = [];

    // Hotkey ids asked for whose claim another instance holds — what a toast is owed for, once, and what the retry timer watches.
    private IReadOnlySet<string> _conflicted = new HashSet<string>();

    // Serializes arm attempts: the retry timer and a settings save can otherwise land on `_claims` at the same time.
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private Timer? _retryTimer;
    private bool _disposed;

    // `conflictRetryInterval`:
    // How often a conflicted binding tries again. Overridable for tests, which cannot wait fifteen seconds to
    // watch a retry happen — the same reasoning as `ScheduledResumeCoordinator`'s tick interval.
    public GlobalHotkeyCoordinator(
        IGlobalHotkeyService hotkeys,
        IVoiceSettingsStore voiceSettingsStore,
        IScreenshotSettingsStore screenshotSettingsStore,
        IAssistantSettingsStore assistantSettingsStore,
        IHotkeyExclusivityGuard guard,
        IToastService toasts,
        ILogger<GlobalHotkeyCoordinator> logger,
        TimeSpan? conflictRetryInterval = null)
    {
        _hotkeys = hotkeys;
        _voiceSettingsStore = voiceSettingsStore;
        _screenshotSettingsStore = screenshotSettingsStore;
        _assistantSettingsStore = assistantSettingsStore;
        _guard = guard;
        _toasts = toasts;
        _logger = logger;
        _conflictRetryInterval = conflictRetryInterval ?? DefaultConflictRetryInterval;

        _hotkeys.Pressed += (_, id) => Pressed?.Invoke(this, id);
        _hotkeys.Released += (_, id) => Released?.Invoke(this, id);
        _hotkeys.TriggerDescriptionsChanged += (_, _) => TriggerDescriptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // A registered hotkey went down; the argument is its `GlobalHotkeyBinding.Id`.
    public event EventHandler<string>? Pressed;

    // A registered hotkey came back up; the argument is its `GlobalHotkeyBinding.Id`.
    public event EventHandler<string>? Released;

    // Raised when what the desktop reports about any binding changes — see `IGlobalHotkeyService.TriggerDescriptionsChanged`.
    public event EventHandler? TriggerDescriptionsChanged;

    // How the given hotkey is actually triggered, to show the operator — or null when nothing is armed for it.
    public string? TriggerDescriptionFor(string hotkeyId) => _hotkeys.TriggerDescriptionFor(hotkeyId);

    // Whether the operator has this hotkey switched on. Distinct from having a trigger description, which is
    // null both for a key nobody asked for and for one the desktop has not bound yet — and telling an operator
    // their desktop has not bound a key they never enabled is a confusing thing to say.
    public bool IsArmed(string hotkeyId) => _armed.Contains(hotkeyId);

    private IReadOnlySet<string> _armed = new HashSet<string>();

    // The keys the operator has switched on, whether or not arming them worked. Kept apart from
    // `_armed` precisely so the two can disagree: that disagreement is a hotkey the operator asked
    // for and did not get, which is the one thing this class used to be unable to say out loud (AC-332).
    private IReadOnlySet<string> _asked = new HashSet<string>();

    // The line a settings screen shows about one hotkey: never switched on reports nothing, an armed key
    // reports what Windows/Wayland actually bound (or macOS's lack of a global hotkey), and `failedMessage`
    // covers the AC-332 case — switched on but arming failed, which used to read exactly like never enabled.
    public string DescribeTrigger(
        string hotkeyId, string unboundMessage, string unsupportedMessage, string failedMessage) =>
        IsArmed(hotkeyId)
            ? TriggerDescriptionFor(hotkeyId) ?? (OperatingSystem.IsMacOS() ? unsupportedMessage : unboundMessage)
            : _asked.Contains(hotkeyId) ? failedMessage : string.Empty;

    // Arms exactly the keys switched on; also the re-arm path since the OS service replaces its whole
    // registration. Never throws — callers discard the task, so an unobserved exception used to silently
    // lose the hotkey. On 2026-07-15 a `cockpit.json` read/write race at startup left F9 dead with no log line; it still cannot arm on a failed read, but now it says which.
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

            // In a finally, so the failure path reports too. Raised unconditionally: a switched-off feature,
            // macOS binding nothing, and an arm that threw all produce no change event of their own, yet each
            // is a case where the settings screen must re-read rather than show a key that no longer fires.
            TriggerDescriptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // What each feature wants, if it wants anything — a feature that is switched off contributes no binding, so nothing is registered with the desktop for it.
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

        // The assistant key, and unlike dictation's it is not gated on voice being on: the assistant answers typed
        // input too, and the hold is what wakes it. Gated only on the feature itself, which is off by default —
        // so nothing is registered with the desktop for an operator who never switched the assistant on.
        var assistant = await _assistantSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (assistant.IsEnabled)
        {
            bindings.Add(new GlobalHotkeyBinding(GlobalHotkeys.AssistantPushToTalk, "Talk to the assistant (hold)", assistant.PushToTalkKeyName));
        }

        return bindings;
    }

    // Drops the claim for any hotkey id no longer asked for — a feature just switched off, or a settings read changed what it wants.
    private void _ReleaseClaimsNoLongerAsked()
    {
        foreach (var goneId in _claims.Keys.Where(id => !_asked.Contains(id)).ToList())
        {
            _claims.Remove(goneId, out var claim);
            claim?.Dispose();
        }
    }

    // Splits the requested bindings into what this instance may arm and what another cockpit instance already
    // holds. A binding this instance already claimed (the ordinary re-arm path) is not asked for again — only a
    // binding new to `_claims` goes through `IHotkeyExclusivityGuard.TryAcquire`.
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

    // Logs every conflicted binding, and tells the operator about the ones new to the conflicted set — once,
    // not on every retry tick, since a conflict that has not resolved yet is not new information.
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

    // Keeps a retry running for as long as some binding is conflicted, and stops it the moment none is — the
    // re-arm that used to need a restart when the other instance closed.
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

    // Cheaper than the ordinary re-arm path: only claims what is still conflicted, stopping if nothing came
    // free. Without this an idle tick ran the full `ApplyAsync` every 15s while one binding stayed
    // conflicted, re-registering an already-working binding and briefly reporting it as failed.
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
