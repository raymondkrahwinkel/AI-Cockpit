using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

public partial class SessionView : UserControl
{
    // AC-1121: the follow lives in TranscriptFollower, shared with AssistantChatView. It ran twice, and every
    // repair since AC-528 has landed on one half.
    private TranscriptFollower? _follower;

    // AC-1165: the input half — key handling, paste, mentions — shared with AssistantChatView the same way.
    private readonly TranscriptComposerInput _composerInput;

    // Watched for minimise: the paused renderer never commits recycled-row removal, so a minimised
    // streaming pane keeps every row instead of one viewport's worth (overnight multi-GB growth).
    // _ApplyRendererPause suspends realisation the same way an inactive tab already does.
    private Window? _hostWindow;

    // The two independent reasons a renderer can be paused, kept apart so lifting one never lifts the other.
    // _windowMinimised is read from the change notification rather than from _hostWindow, so this never rests on
    // when Avalonia writes the property relative to raising the event.
    private bool _windowMinimised;

    // AC-883: on macOS the OS can take the render clock away — screen lock, display sleep, a Space switch, full
    // occlusion — and WindowState stays Normal throughout, so minimising is not the coverage there that it is on
    // Windows and X11. DiagnosticsBackgroundService's probe is the one thing in the process that sees it.
    private bool _renderClockPaused;

    // Set by a test before this is attached; otherwise resolved from the container on attach, and only on macOS —
    // see _ResolveDiagnostics. Null leaves this pane on exactly the pre-AC-883 behaviour: minimising, nothing else.
    internal DiagnosticsBackgroundService? Diagnostics { get; set; }

    // AC-532: ticks the composer's tool-activity elapsed time each second so "running 0:12" counts
    // up (and, AC-531, the background-work pop-out's per-task times). Lives here, not the view
    // model, since the derived state must stay dispatcher-free for platform-less unit tests.
    private DispatcherTimer? _activityAgeTicker;

    private ScrollViewer? _transcriptScroll;

    // The session this pane's affordances follow, kept so the unsubscribe can find the same one again.
    private SessionViewModel? _watchedSession;

    // The transcript's scroll owner. It lives inside TranscriptItems' own template since AC-686, so the virtualising
    // panel measures against the viewport rather than the infinite height an enclosing ScrollViewer hands it — and a
    // name inside a template is not a code-behind field, so it is resolved from the template instead.
    internal ScrollViewer? TranscriptScroll =>
        _transcriptScroll ??= _ResolveTranscriptScroll();

    // ApplyTemplate, not just a visual-tree walk: the attach that wires the scroll handlers asks for this first,
    // and the transcript has not been measured then, so its template child does not exist yet.
    // AC-1130: FirstOrDefault, because a throw here used to skip the whole of OnDetachedFromVisualTree.
    private ScrollViewer? _ResolveTranscriptScroll()
    {
        TranscriptItems.ApplyTemplate();
        return TranscriptItems.GetVisualChildren().OfType<ScrollViewer>().FirstOrDefault();
    }

    // Internal so a view test can assert the structural claim of AC-1121: no scroll change is ever answered
    // with a follow, so `Following` is never true while one is being handled.
    internal TranscriptFollower Follower =>
        _follower ??= new TranscriptFollower(TranscriptItems, () => TranscriptScroll);

    public SessionView()
    {
        InitializeComponent();
#if DEBUG
        Cockpit.App.Diagnostics.LeakTracker.Register(this);
#endif

        _composerInput = new TranscriptComposerInput(
            InputBox,
            tryGetPastedBitmap: () => TopLevel.GetTopLevel(this)?.Clipboard?.TryGetBitmapAsync() ?? Task.FromResult<Bitmap?>(null),
            tryGetPastedText: () => TopLevel.GetTopLevel(this)?.Clipboard?.TryGetTextAsync() ?? Task.FromResult<string?>(null),
            hasComposer: () => DataContext is SessionViewModel,
            mentionPicker: () => (DataContext as SessionViewModel)?.MentionPicker,
            recallLastQueuedMessage: () => DataContext is SessionViewModel recallVm && recallVm.RecallLastQueuedMessage(),
            resolveStopIfBusy: () => DataContext is SessionViewModel { IsBusy: true } busyVm ? busyVm.StopCommand : null,
            sendCommand: () => (DataContext as SessionViewModel)?.SendCommand,
            resolvePastedImageSink: () => DataContext is SessionViewModel vm ? vm.AddPastedImage : null);

        // Enter sends the message; Shift+Enter inserts a newline. Tunnel so we pre-empt the
        // TextBox's own Enter handling (which would otherwise insert a newline).
        InputBox.AddHandler(InputElement.KeyDownEvent, _composerInput.OnKeyDown, RoutingStrategies.Tunnel);

        // AC-740: re-evaluates the @-mention token once the TextBox has applied the keystroke (character typed,
        // backspace, caret moved). Bubble is fine — nothing else claims KeyUp on this control.
        InputBox.KeyUp += _composerInput.OnKeyUp;

        // Push-to-talk (F9 by default): tunnel on the whole panel, not just the input box, so it fires
        // regardless of which control inside the panel has focus — the operator should not have to
        // click into the input first to dictate.
        AddHandler(InputElement.KeyDownEvent, _OnPushToTalkKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, _OnPushToTalkKeyUp, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Focus the input as soon as a session panel appears (L10), deferred past layout. AC-636:
        // not while in another window, else this steals the keyboard from the chat pop-out. AC-650:
        // not for a non-selected pane, else a restore burst tears focus from pane to pane.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is SessionPanelViewModel { IsSelected: false })
            {
                return;
            }

            if (!AutoFocus.WouldTakeTheKeyboardFromAnotherWindow(this))
            {
                InputBox.Focus();
            }
        });

        if (TranscriptScroll is { } scroll)
        {
            scroll.ScrollChanged += _OnTranscriptScrollChanged;
            // Tunnel, and handled events too: the ScrollViewer's own presenter marks the wheel handled while
            // scrolling on it, and a scrollbar thumb handles its own pointer press.
            scroll.AddHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
            scroll.AddHandler(InputElement.PointerPressedEvent, _OnTranscriptPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            scroll.AddHandler(InputElement.PointerReleasedEvent, _OnTranscriptPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            scroll.AddHandler(InputElement.PointerCaptureLostEvent, _OnTranscriptPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        // Land on the newest row if the panel re-attaches with an existing transcript.
        Follower.RequestFollow();

        // AC-996: a pane re-attaching onto a session that is already waiting on a permission — reselected in the
        // sidebar because it went to needs-attention, most likely — must show the way to it straight away.
        _WatchSession(DataContext as SessionViewModel);
        Dispatcher.UIThread.Post(_UpdateJumpAffordance, DispatcherPriority.Background);

        _activityAgeTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _activityAgeTicker.Tick += _OnActivityAgeTick;
        _activityAgeTicker.Start();

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            _hostWindow = window;
            window.PropertyChanged += _OnHostWindowPropertyChanged;
            _windowMinimised = window.WindowState == WindowState.Minimized;

            _ResolveDiagnostics();
            if (Diagnostics is { } diagnostics)
            {
                diagnostics.RenderersShouldPauseChanged += _OnRenderersShouldPauseChanged;
                _renderClockPaused = diagnostics.RenderersShouldPause;
            }

            _ApplyRendererPause();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _activityAgeTicker?.Stop();
        _activityAgeTicker = null;

        _WatchSession(null);

        if (_hostWindow is { } hostWindow)
        {
            hostWindow.PropertyChanged -= _OnHostWindowPropertyChanged;
            _hostWindow = null;
        }

        // The service outlives every pane, so a missed unsubscribe here keeps this whole view tree alive — the
        // exact class of leak this file already exists to fix.
        if (Diagnostics is { } diagnostics)
        {
            diagnostics.RenderersShouldPauseChanged -= _OnRenderersShouldPauseChanged;
        }

        // Last, because resolving the scroll owner is the one step here that can come up empty — and before
        // AC-1130 it was first, so a template without one leaked everything above (the timer, the session, the
        // window, and a process-lifetime singleton's handler).
        if (_transcriptScroll is { } scroll)
        {
            scroll.ScrollChanged -= _OnTranscriptScrollChanged;
            scroll.RemoveHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel);
            scroll.RemoveHandler(InputElement.PointerPressedEvent, _OnTranscriptPointerPressed);
            scroll.RemoveHandler(InputElement.PointerReleasedEvent, _OnTranscriptPointerReleased);
            scroll.RemoveHandler(InputElement.PointerCaptureLostEvent, _OnTranscriptPointerReleased);
        }

        base.OnDetachedFromVisualTree(e);

        // Forces the compositor to flush teardown even on an inactive tab, whose paused renderer
        // would otherwise never commit and leave the subtree's scene visuals uncollectable
        // (TranscriptLeakHuntTests). AC-878: shared via CompositorTeardown.
        CompositorTeardown.Flush(e.RootVisual);
    }

    private void _OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            _windowMinimised = e.GetNewValue<WindowState>() == WindowState.Minimized;
            _ApplyRendererPause();
        }
    }

    // macOS only, and gated here rather than only inside the decision: on Windows and X11 the pane must not even
    // subscribe. An inert delegate is still a delegate held by a process-lifetime singleton, and those platforms
    // have no problem to solve. A test sets Diagnostics itself, so it can exercise the macOS path off a Mac.
    private void _ResolveDiagnostics()
    {
        if (Diagnostics is null && OperatingSystem.IsMacOS())
        {
            Diagnostics = Program.Services?.GetService<DiagnosticsBackgroundService>();
        }
    }

    private void _OnRenderersShouldPauseChanged(object? sender, bool paused) => SetRenderClockPaused(paused);

    // Internal so a view test can drive the edge; production reaches it through the event above.
    internal void SetRenderClockPaused(bool paused)
    {
        _renderClockPaused = paused;
        _ApplyRendererPause();
    }

    // Paused: collapse the scroll owner so the virtualising panel dematerialises its rows and stops building new
    // ones — no churn the paused renderer can never clean up. Restored once every reason has lifted (AC-883).
    // The scroll owner, not TranscriptItems, whose IsVisible is already bound to HasTranscript.
    private void _ApplyRendererPause()
    {
        // AC-1130: a pause signal can arrive before attach has built the scroll owner, and this is reachable from
        // the internal test seam before then.
        if (TranscriptScroll is not { } scroll)
        {
            return;
        }

        var paused = _windowMinimised || _renderClockPaused;
        scroll.IsVisible = !paused;

        if (!paused)
        {
            // RequestFollow re-reads the follow when the step runs, not now: AC-953's scroll handover happens on
            // this very attach, and a post queued while sticky used to jump to the tail after it.
            Follower.RequestFollow();
        }

        // Rows dematerialise while paused, so what is reachable changed even though nothing scrolled (AC-996).
        Dispatcher.UIThread.Post(_UpdateJumpAffordance, DispatcherPriority.Background);
    }

    private void _OnActivityAgeTick(object? sender, EventArgs e)
    {
        if (DataContext is not SessionViewModel vm)
        {
            return;
        }

        vm.RefreshActiveToolActivityAge();
        // AC-531: same ticker, one more thing to re-tick — the background-work pop-out's elapsed times need to
        // count up too, and a second DispatcherTimer for the same one-second cadence would be pure duplication.
        vm.RefreshBackgroundTaskAges();
    }

    // AC-1121: no follow from here. Avalonia raises this from LayoutUpdated, so the ScrollIntoView the old
    // handler reached ran a nested layout pass inside the pass that raised it — the chain in AC-1178's stacks.
    private void _OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        Follower.NoteScrollChanged(e.ViewportDelta.Y);
        _UpdateJumpAffordance();
    }

    // Offers whichever destination is out of reach: the tail while scrolled up (#21), or a consent
    // card off-screen (AC-996, was a dead-end chevron). Deliberately no auto-scroll to the card.
    private void _UpdateJumpAffordance()
    {
        var awaiting = _PendingPermissionIndex() is var pending && pending >= 0 && !_RowTopIsInView(pending);

        ScrollToBottomIcon.Kind = awaiting ? MaterialIconKind.ShieldAlertOutline : MaterialIconKind.ChevronDown;
        ToolTip.SetTip(ScrollToBottomButton, awaiting
            ? "A tool is waiting for your approval — jump to it"
            : "Jump to the newest message");
        ScrollToBottomButton.IsVisible = awaiting || !Follower.StickToBottom;
    }

    // The newest row still waiting on the operator, as an index into what the transcript is showing. -1 for none.
    // Read off the status rather than off the pending flag alone: this exists to give needs-attention somewhere to
    // point, and the two move together — `PermissionRequested` sets both, and whatever clears one clears the other.
    private int _PendingPermissionIndex()
    {
        if (DataContext is not SessionViewModel { SessionStatus: SessionStatus.NeedsAttention } session)
        {
            return -1;
        }

        for (var index = session.VisibleTranscript.Count - 1; index >= 0; index--)
        {
            if (session.VisibleTranscript[index].IsPendingPermission)
            {
                return index;
            }
        }

        return -1;
    }

    // Whether this row's top edge is on screen — enough to see a consent card and reach for it. Not
    // TranscriptFollower.NewestRowIsFullyVisible: that one must keep answering "yes" for a row taller than the viewport or the
    // follow can never terminate (AC-528), and it is only ever asked about the last row.
    private bool _RowTopIsInView(int index)
    {
        if (TranscriptScroll is not { IsVisible: true } scroll || TranscriptItems.ContainerFromIndex(index) is not { } row)
        {
            return false;
        }

        var top = row.TranslatePoint(new Point(0, 0), scroll);
        return top is { } point && point.Y >= -1 && point.Y < scroll.Viewport.Height;
    }

    // A permission arrives without scrolling anything, so no ScrollChanged comes to re-evaluate the button —
    // this is the only notice the view gets that there is now something to point at.
    // Deliberately diverges from AssistantChatView, which has no jump-button shield (AC-996) to keep in sync.
    private void _OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The status as well as the flag: whatever takes the session off needs-attention has to take the alarm
        // off the button with it, and that can happen without the pending flag itself moving.
        if (e.PropertyName is nameof(SessionViewModel.HasPendingPermission) or nameof(SessionViewModel.SessionStatus))
        {
            // After the row it added has been laid out, otherwise its container is not there to be measured yet.
            Dispatcher.UIThread.Post(_UpdateJumpAffordance, DispatcherPriority.Background);
        }
    }

    private void _WatchSession(SessionViewModel? session)
    {
        if (ReferenceEquals(_watchedSession, session))
        {
            return;
        }

        // The view model outlives the pane, so a missed unsubscribe here holds this whole view tree alive.
        if (_watchedSession is { } previous)
        {
            previous.PropertyChanged -= _OnSessionPropertyChanged;
        }

        _watchedSession = session;
        if (session is not null)
        {
            session.PropertyChanged += _OnSessionPropertyChanged;
        }

        Follower.Watch(session);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _WatchSession(DataContext as SessionViewModel);
    }

    private void _OnTranscriptWheel(object? sender, PointerWheelEventArgs e) => Follower.NoteWheel();

    private void _OnTranscriptPointerPressed(object? sender, PointerPressedEventArgs e) => Follower.NotePointerHeld(true);

    private void _OnTranscriptPointerReleased(object? sender, RoutedEventArgs e) => Follower.NotePointerHeld(false);

    private void _OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        // AC-996: when something is waiting to be approved, that card is the destination — and it is the newest
        // row in all but the rare case of an older prompt still open, which is the only reason for the branch.
        var pending = _PendingPermissionIndex();
        if (pending >= 0 && pending != TranscriptItems.ItemCount - 1)
        {
            TranscriptItems.ScrollIntoView(pending);
            _UpdateJumpAffordance();
            return;
        }

        Follower.StickToBottom = true;
        Follower.RequestFollow();
        _UpdateJumpAffordance();
    }



    // KeyDown for push-to-talk. BeginVoiceHold itself guards OS key-repeat, so this only marks the
    // event handled when a hold actually started — an ignored press leaves the key free elsewhere.
    // No-ops when global push-to-talk is active (PushToTalkKeyGate) to avoid firing twice.
    private void _OnPushToTalkKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SessionViewModel vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled)
            && vm.BeginVoiceHold())
        {
            _holdingKey = e.Key;
            e.Handled = true;
        }
    }

    // The key whose press opened a microphone here, until its own release ends the hold — see `_OnPushToTalkKeyUp`.
    private Key? _holdingKey;

    // KeyUp for push-to-talk: ends the hold, transcribes, appends to input. AC-557: ends only what
    // this view's own KeyDown started — asks the key that started it, not the gate again, since the
    // gate's answer can change mid-hold and an unended hold keeps the microphone forever.
    private void _OnPushToTalkKeyUp(object? sender, KeyEventArgs e)
    {
        if (_holdingKey == e.Key && DataContext is SessionViewModel vm)
        {
            _holdingKey = null;
            e.Handled = true;
            _ = vm.EndVoiceHoldAsync();
        }
    }
}
