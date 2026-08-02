using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;

namespace Cockpit.App.Services;

// Takes a screenshot into the selected session (AC-220): the desktop's own picker opens, and what comes back
// lands on the session in view as a pending attachment — the operator types the sentence that goes with it
// and sends when they mean to. The same flow behind the global hotkey and the composer's button.
// The responsibility is the cockpit's, not the operator's — the same choice push-to-talk makes: the capture
// goes to `CockpitViewModel.SelectedSession` rather than asking which session it was for.
//
// Nothing here is allowed to end in silence. The capture runs the OS picker, so it can be cancelled, refused
// by a privacy prompt or land on a session that cannot carry an image — each of those gets a toast saying
// which, because the operator pressed a key and is owed an answer.
public sealed class ScreenshotCoordinator : ISingletonService
{
    private readonly GlobalHotkeyCoordinator _hotkeys;
    private readonly IScreenshotCapture _capture;
    private readonly CockpitViewModel _cockpit;
    private readonly IToastService _toasts;
    private readonly IScreenshotSettingsStore _settings;
    private readonly IScreenshotImageEditor _editor;
    private readonly IDesktopWindows _windows;
    private readonly ILogger<ScreenshotCoordinator> _logger;

    // Guards against a second capture while the picker is already open — the hotkey is easy to press twice.
    private bool _isCapturing;

    // What puts the selection surface in front of the operator, or null where there is no window to put one
    // over — a headless or design-time graph, which takes the whole capture rather than losing screenshots
    // altogether. Swappable so the crop-and-remember path can be tested without a desktop. The destination name
    // travels alongside the capture rather than being closed over once at construction, because it names
    // whichever session this particular call is for — the composer button's session, not necessarily the one
    // selected when the coordinator was built.
    private Func<ScreenCapture, CaptureRect?, string, Task<ScreenshotSelection?>>? _showSelection;

    public ScreenshotCoordinator(
        GlobalHotkeyCoordinator hotkeys,
        IScreenshotCapture capture,
        CockpitViewModel cockpit,
        IToastService toasts,
        IScreenshotSettingsStore settings,
        IScreenshotImageEditor editor,
        IDesktopWindows windows,
        ILogger<ScreenshotCoordinator> logger)
    {
        _hotkeys = hotkeys;
        _capture = capture;
        _cockpit = cockpit;
        _toasts = toasts;
        _settings = settings;
        _editor = editor;
        _windows = windows;
        _logger = logger;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            _showSelection = (capture, lastRegion, destination) => ScreenshotSelectionWindow.PickAsync(
                capture, lastRegion, _windows, window,
                previewGate: (chosen, selectionWindow) => _ShowPreviewAsync(capture, chosen, destination, selectionWindow));
        }

        hotkeys.Pressed += (_, id) =>
        {
            if (id == GlobalHotkeys.Screenshot)
            {
                // Marshalled like push-to-talk's: the key fires on the D-Bus loop or the hook thread, and what
                // this touches next is a view model.
                Dispatcher.UIThread.Post(() => _ = CaptureIntoSelectedSessionAsync());
            }
        };

        hotkeys.TriggerDescriptionsChanged += (_, _) => Dispatcher.UIThread.Post(HandleTriggerDescriptionsChanged);
    }

    // Test seam: stands in for the selection surface, which needs a desktop to be put over.
    internal void UseSelection(Func<ScreenCapture, CaptureRect?, string, Task<ScreenshotSelection?>> showSelection) =>
        _showSelection = showSelection;

    // Test seam, like push-to-talk's: puts what the desktop bound where the operator can see it. What the cases are is `GlobalHotkeyCoordinator.DescribeTrigger`'s; the words for this key are here.
    internal void HandleTriggerDescriptionsChanged() =>
        _cockpit.ScreenshotHotkeyTrigger = _hotkeys.DescribeTrigger(
            GlobalHotkeys.Screenshot,
            unboundMessage: "Your desktop has not bound it yet. Look for “Take a screenshot” in its own shortcut settings.",
            unsupportedMessage: "Not available on macOS — use the button in the composer.",
            failedMessage: "It is switched on but could not be registered — see the log. The button in the composer still works.");

    // Whether this platform can capture at all — what the composer's button reads to disable itself with a reason rather than offering something that cannot work.
    public bool IsSupported => _capture.IsSupported;

    // Completes once `IsSupported` means anything (AC-326). On Linux the answer is a D-Bus round
    // trip, and this coordinator is built in the same statement that wires the composer's button — so whoever
    // read it there has to come back and read it again.
    public Task SupportSettled => _capture.SupportSettled;

    // Runs the picker and puts the result on the session in view — the global hotkey's path, which has no
    // session of its own to name and so takes the selected one, exactly as push-to-talk does.
    public Task CaptureIntoSelectedSessionAsync(CancellationToken cancellationToken = default)
    {
        // Resolved before the picker opens, not after: the operator can click another session while dragging a
        // region, and the screenshot belongs to the one they were looking at when they asked for it.
        var session = _cockpit.SelectedSession;
        if (session is null)
        {
            _toasts.Show("No session is selected, so there is nowhere to put a screenshot.", ToastSeverity.Warning);
            return Task.CompletedTask;
        }

        return CaptureIntoAsync(session, cancellationToken);
    }

    // Runs the picker and puts the result on the given session — the composer button's path, which names its
    // own panel rather than the selected one: in a grid the button you clicked and the session in view are not
    // necessarily the same, and the screenshot belongs to the composer it was asked from.
    // Safe to call from a command or a hotkey; never throws, since both callers discard the task.
    public async Task CaptureIntoAsync(SessionPanelViewModel session, CancellationToken cancellationToken = default)
    {
        if (_isCapturing)
        {
            return;
        }

        _isCapturing = true;
        try
        {
            // Logged either side of the picker, because without it the only evidence a capture ever ran is a
            // timeout two minutes later — which is exactly how long diagnosing the first real failure took.
            _logger.LogInformation("Screen capture starting for session '{Session}'.", session.Title);

            var capture = await _capture.CaptureAsync(cancellationToken).ConfigureAwait(true);
            if (capture is null)
            {
                _logger.LogInformation("Screen capture produced nothing — cancelled, or the picker was never completed.");
                // Cancelled. Silent on purpose: pressing Escape on the picker is the ordinary way to change your
                // mind, and a toast for it would be nagging. A platform that cannot capture at all throws
                // instead, and lands in the catch below where it gets said out loud.
                return;
            }

            if (await _PickAsync(capture, session).ConfigureAwait(true) is not { } png)
            {
                _logger.LogInformation("The selection was dismissed, so nothing was taken.");
                return;
            }

            if (await session.InjectScreenshotAsync(png).ConfigureAwait(true) is { } reason)
            {
                _logger.LogInformation("Screen capture of {Bytes} bytes was not taken: {Reason}", png.Length, reason);
                _toasts.Show(reason, ToastSeverity.Warning);
            }
            else
            {
                _logger.LogInformation("Screen capture of {Bytes} bytes went into session '{Session}'.", png.Length, session.Title);
            }
        }
        catch (OperationCanceledException)
        {
            // The cockpit is shutting down, or the caller gave up waiting. Not something to report.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The screen capture failed.");
            _toasts.Show($"The screenshot could not be taken: {exception.Message}", ToastSeverity.Error);
        }
        finally
        {
            _isCapturing = false;
        }
    }

    // Puts the selection surface over the frozen capture and returns the region the operator marked out, cropped
    // (AC-329) — or nothing, when they dismissed it.
    // The whole capture is used as-is where there is no window to put a surface over: a headless or design-time
    // graph has nothing to show it on, and failing there would take screenshots away from a test harness that
    // only ever wanted the bytes.
    private async Task<byte[]?> _PickAsync(ScreenCapture capture, SessionPanelViewModel session)
    {
        if (_showSelection is not { } show)
        {
            return capture.Image;
        }

        var settings = await _settings.LoadAsync().ConfigureAwait(true);
        if (await show(capture, settings.LastRegion, session.Title).ConfigureAwait(true) is not { } chosen)
        {
            return null;
        }

        // Cropped first, then marked: the marks are in the crop's coordinates, and doing it the other way round
        // would obscure part of a picture that is about to be thrown away and leave the kept part bare.
        // Off the UI thread: a redaction walks every pixel of every box, and "everything" on a multi-monitor
        // desktop is millions of them. Doing that on the thread that draws would freeze the cockpit at the one
        // moment the operator is waiting to see their screenshot land.
        var marked = await Task.Run(() =>
        {
            var cropped = _editor.Crop(capture.Image, chosen.Region);
            return _editor.Burn(cropped, chosen.Marks);
        }).ConfigureAwait(true);

        // Saved after the crop rather than before: a region that turned out not to fit was never restored, and
        // remembering one the operator never actually got is worse than remembering nothing. The boxes are not
        // kept — what was worth hiding once is not the same thing next time, and offering yesterday's redaction
        // over today's screen would be a promise nobody checked.
        await _settings.SaveAsync(settings with { LastRegion = chosen.Region }).ConfigureAwait(true);

        return marked;
    }

    // The preview gate behind Confirm() (AC-566): the one place all three ways to confirm run through, so a
    // setting switched off costs nothing and one switched on cannot be skipped by picking a different key or
    // click. Renders exactly the bytes the real crop would produce — the same `_editor`, the same
    // region and marks — and asks before they leave the selection window.
    private async Task<bool> _ShowPreviewAsync(ScreenCapture capture, ScreenshotSelection chosen, string destination, Window owner)
    {
        var settings = await _settings.LoadAsync().ConfigureAwait(true);
        if (!settings.PreviewEnabled)
        {
            return true;
        }

        var burned = await Task.Run(() =>
        {
            var cropped = _editor.Crop(capture.Image, chosen.Region);
            return _editor.Burn(cropped, chosen.Marks);
        }).ConfigureAwait(true);

        return await ScreenshotPreviewWindow.ShowAsync(burned, destination, owner).ConfigureAwait(true);
    }
}
