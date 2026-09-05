using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Material.Icons;
using Microsoft.Extensions.Logging;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions.CompanionTools;

namespace Cockpit.App.Services;

// AC-220: takes a screenshot into the session in view (like push-to-talk, not one the operator picks) as a
// pending attachment; the global hotkey and the composer's button share this path. Never ends in silence:
// cancel, privacy-prompt refusal, or an incompatible session each get their own toast.
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

    // Puts the selection surface in front of the operator, or null (headless/design-time) to take the whole
    // capture instead of losing screenshots; swappable for testing without a desktop. Destination name travels
    // per-call, not closed over at construction, since it names whichever session this call is for.
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

    // AC-239: the companion window's contribution, through AC-238's first-party host — this coordinator's
    // second consumer, alongside the global hotkey and the composer button. There is no panel of its own to
    // name, so it takes the session in view, exactly as the hotkey does.
    public CompanionToolRegistration CreateCompanionTool() =>
        new("cockpit.screenshot", "Screenshot", _ => _CreateCompanionToolView())
        {
            IconKind = MaterialIconKind.Monitor,
        };

    // Never hidden on an unsupported platform (AC-238's ShowWhenDisabled reasoning): in the companion window,
    // hidden and broken look the same, so this stays put and greys out with a reason instead.
    private Control _CreateCompanionToolView()
    {
        var button = new Button
        {
            Content = CockpitIcons.Icon(MaterialIconKind.Monitor),
            Padding = new Thickness(7, 6),
        };
        ToolTip.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, "Screenshot");
        button.Click += (_, _) => _ = CaptureIntoSelectedSessionAsync();

        void Refresh()
        {
            button.IsEnabled = IsSupported;
            ToolTip.SetTip(button, IsSupported
                ? "Take a screenshot into the active session"
                : "Screen capture is not available on this platform.");
        }

        Refresh();

        // Support settles after a D-Bus round trip on Linux (AC-326); this button can exist before that
        // answers, same as the composer's own (`_RewireScreenshotsWhenSupportSettlesAsync`).
        _ = SupportSettled.ContinueWith(_ => Dispatcher.UIThread.Post(Refresh), TaskScheduler.Default);

        return button;
    }

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

    // Runs the picker and puts the result on the given session — the composer button's path, naming its own
    // panel rather than the selected one, since the clicked button and the session in view may differ in a grid.
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

    // Puts the selection surface over the frozen capture and returns the marked-out region, cropped (AC-329) —
    // or nothing if dismissed. The whole capture is used as-is where there is no window to show a surface on,
    // so a headless/design-time test harness that only wants the bytes doesn't lose screenshots.
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

        // Cropped first, then marked: marks are in the crop's coordinates, so cropping after would leave the
        // kept part bare. Off the UI thread: a redaction walks every pixel of every box — millions on a
        // multi-monitor desktop — which would freeze the cockpit right when the operator is waiting to see it.
        var marked = await Task.Run(() =>
        {
            var cropped = _editor.Crop(capture.Image, chosen.Region);
            return _editor.Burn(cropped, chosen.Marks);
        }).ConfigureAwait(true);

        // Saved after cropping, not before, so a region that never fit isn't remembered; boxes are not kept,
        // since what was worth hiding once may not apply to today's screen.
        await _settings.SaveAsync(settings with { LastRegion = chosen.Region }).ConfigureAwait(true);

        return marked;
    }

    // The preview gate behind Confirm() (AC-566): the one place all three confirm paths run through, so it
    // can't be skipped by a different key or click. Renders the exact bytes the real crop would produce and
    // asks before they leave the selection window.
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
