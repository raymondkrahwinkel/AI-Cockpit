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
    // Follow the newest transcript row while parked at the bottom; pause when the user scrolls up to
    // read history, resume once they scroll back down (#21). Avalonia has no built-in stick-to-bottom.
    private bool _stickToBottom = true;

    // True while `_FollowNewest` is moving the viewport itself, so the scroll changes it
    // causes are never mistaken for the operator's — and never re-enter it.
    private bool _following;

    // Marks a real scroll gesture happened; the next ScrollChanged is its consequence. Delta fields
    // can't stand in — a panel's own offset correction has the same fingerprint (three rounds of
    // this ticket died on that ambiguity). Needs an expiry — see _OnTranscriptWheel.
    private bool _wheelTurned;

    private bool _pointerHeld;

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
    internal ScrollViewer TranscriptScroll =>
        _transcriptScroll ??= _ResolveTranscriptScroll();

    // ApplyTemplate, not just a visual-tree walk: this is first asked for from the attach that wires the scroll
    // handlers, and at that point the transcript has not been measured, so its template child does not exist yet.
    private ScrollViewer _ResolveTranscriptScroll()
    {
        TranscriptItems.ApplyTemplate();
        return TranscriptItems.GetVisualChildren().OfType<ScrollViewer>().First();
    }

    public SessionView()
    {
        InitializeComponent();
#if DEBUG
        Cockpit.App.Diagnostics.LeakTracker.Register(this);
#endif

        // Enter sends the message; Shift+Enter inserts a newline. Tunnel so we pre-empt the
        // TextBox's own Enter handling (which would otherwise insert a newline).
        InputBox.AddHandler(InputElement.KeyDownEvent, _OnInputKeyDown, RoutingStrategies.Tunnel);

        // AC-740: re-evaluates the @-mention token once the TextBox has applied the keystroke (character typed,
        // backspace, caret moved). Bubble is fine — nothing else claims KeyUp on this control.
        InputBox.KeyUp += _OnInputKeyUp;

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

        TranscriptScroll.ScrollChanged += _OnTranscriptScrollChanged;
        // Tunnel, and handled events too: the ScrollViewer's own presenter marks the wheel handled while
        // scrolling on it, and a scrollbar thumb handles its own pointer press.
        TranscriptScroll.AddHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        TranscriptScroll.AddHandler(InputElement.PointerPressedEvent, _OnTranscriptPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TranscriptScroll.AddHandler(InputElement.PointerReleasedEvent, _OnTranscriptPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TranscriptScroll.AddHandler(InputElement.PointerCaptureLostEvent, _OnTranscriptPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Land on the newest row if the panel re-attaches with an existing transcript.
        Dispatcher.UIThread.Post(() => { if (_stickToBottom) _FollowNewest(); });

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
        TranscriptScroll.ScrollChanged -= _OnTranscriptScrollChanged;
        TranscriptScroll.RemoveHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel);
        TranscriptScroll.RemoveHandler(InputElement.PointerPressedEvent, _OnTranscriptPointerPressed);
        TranscriptScroll.RemoveHandler(InputElement.PointerReleasedEvent, _OnTranscriptPointerReleased);
        TranscriptScroll.RemoveHandler(InputElement.PointerCaptureLostEvent, _OnTranscriptPointerReleased);

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
        var paused = _windowMinimised || _renderClockPaused;
        TranscriptScroll.IsVisible = !paused;

        if (!paused && _stickToBottom)
        {
            Dispatcher.UIThread.Post(_FollowNewest);
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

    private void _OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Our own correction, and the layout passes it drives: not something to draw conclusions from.
        if (_following)
        {
            return;
        }

        var byOperator = _wheelTurned || _pointerHeld;
        _wheelTurned = false;

        if (byOperator)
        {
            // Only the operator can stop the follow, and only the operator can resume it, so only a change they
            // caused re-derives it. Where they ended up is the same fact after a wheel turn as after a scrollbar
            // drag — no need to tell those two apart.
            _stickToBottom = _NewestRowIsFullyVisible()
                || TranscriptScrollAnchor.IsAtBottom(
                    TranscriptScroll.Offset.Y, TranscriptScroll.Extent.Height, TranscriptScroll.Viewport.Height);
        }
        else if (_stickToBottom)
        {
            // Content grew or shrank (a row streamed in), or the viewport resized (the composer's activity line,
            // the starting banner, the usage-warning and pending-resume bars all dock above the transcript and
            // take their band out of it, AC-459). Nobody scrolled: keep the newest row in view.
            _FollowNewest();
        }

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
        ScrollToBottomButton.IsVisible = awaiting || !_stickToBottom;
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
    // _NewestRowIsFullyVisible: that one must keep answering "yes" for a row taller than the viewport or the
    // follow can never terminate (AC-528), and it is only ever asked about the last row.
    private bool _RowTopIsInView(int index)
    {
        if (!TranscriptScroll.IsVisible || TranscriptItems.ContainerFromIndex(index) is not { } row)
        {
            return false;
        }

        var top = row.TranslatePoint(new Point(0, 0), TranscriptScroll);
        return top is { } point && point.Y >= -1 && point.Y < TranscriptScroll.Viewport.Height;
    }

    // A permission arrives without scrolling anything, so no ScrollChanged comes to re-evaluate the button —
    // this is the only notice the view gets that there is now something to point at.
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
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _WatchSession(DataContext as SessionViewModel);
    }

    // A wheel turn at the bottom raises no ScrollChanged, so its flag stood until an unrelated later
    // change consumed it (AC-621: misread an outgoing message as scrolling away). Expire it at
    // end-of-turn layout instead, after any real ScrollChanged the wheel caused has fired.
    private void _OnTranscriptWheel(object? sender, PointerWheelEventArgs e)
    {
        _wheelTurned = true;
        Dispatcher.UIThread.Post(() => _wheelTurned = false, DispatcherPriority.Background);
    }

    private void _OnTranscriptPointerPressed(object? sender, PointerPressedEventArgs e) => _pointerHeld = true;

    private void _OnTranscriptPointerReleased(object? sender, RoutedEventArgs e) => _pointerHeld = false;

    private void _OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        // AC-996: when something is waiting to be approved, that card is the destination — and it is the newest
        // row in all but the rare case of an older prompt still open, which is the only reason for the branch.
        var pending = _PendingPermissionIndex();
        if (pending >= 0 && pending != _NewestVisibleIndex())
        {
            TranscriptItems.ScrollIntoView(pending);
            _UpdateJumpAffordance();
            return;
        }

        _stickToBottom = true;
        _FollowNewest();
        _UpdateJumpAffordance();
    }

    // Asks for the row, not an offset: ScrollToEnd()'s Extent-Viewport estimate left the transcript
    // ~300px short of bottom (AC-528) and its own correction re-triggered an infinite layout loop.
    // AC-800: also the last row VisibleTranscript shows — a hidden row could never terminate (AC-611).
    private int _NewestVisibleIndex() => TranscriptItems.ItemCount - 1;

    // AC-1111 measurement scaffold — remove before the PR.
    internal record FollowNewestMeasurement(
        int FirstRealised, int LastRealised, int RealisedBefore, int NewestIndex, int RealisedAfter, double ElapsedMs,
        long AllocatedBytes);

    internal static Action<FollowNewestMeasurement>? FollowNewestProbe;

    internal static bool SkipScrollIntoView;

    private void _FollowNewest()
    {
        // ScrollIntoView drives a layout pass then and there, and the ScrollViewer raises ScrollChanged from that
        // pass — so without this guard the handler calls itself until the stack runs out (measured: it does).
        if (_following || TranscriptItems.ItemCount == 0 || _NewestRowIsFullyVisible())
        {
            return;
        }

        var newestIndex = _NewestVisibleIndex();
        if (newestIndex < 0)
        {
            return;
        }

        _following = true;
        try
        {
            // ScrollIntoView forces a synchronous layout pass; asking for an already-realised row
            // (as happens every repaint while streaming) costs that pass for nothing — measured,
            // ~3 layout passes per frame where one would do. Only ask when the row genuinely isn't there.
            if (TranscriptItems.ContainerFromIndex(newestIndex) is null && !SkipScrollIntoView)
            {
                // AC-1111 measurement scaffold — remove before the PR.
                var realisedBefore = TranscriptItems.GetRealizedContainers()
                    .Select(TranscriptItems.IndexFromContainer).Where(index => index >= 0).ToList();
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                TranscriptItems.ScrollIntoView(newestIndex);
                watch.Stop();
                FollowNewestProbe?.Invoke(new FollowNewestMeasurement(
                    realisedBefore.Count == 0 ? -1 : realisedBefore.Min(),
                    realisedBefore.Count == 0 ? -1 : realisedBefore.Max(),
                    realisedBefore.Count,
                    newestIndex,
                    TranscriptItems.GetRealizedContainers().Count(),
                    watch.Elapsed.TotalMilliseconds,
                    GC.GetTotalAllocatedBytes(precise: true) - allocated));
            }

            // ScrollIntoView treats a rect as in-view once its top edge is, so a row taller than the
            // viewport leaves its bottom permanently below — unsatisfiable, re-triggering a layout
            // pass on every ScrollChanged (the SDK freeze). Closes the residue by hand instead.
            if (_NewestRowIsFullyVisible())
            {
                return;
            }

            if (TranscriptItems.ContainerFromIndex(newestIndex) is not { } newest ||
                newest.TranslatePoint(new Point(0, newest.Bounds.Height), TranscriptScroll) is not { } bottom)
            {
                return;
            }

            var shortfall = bottom.Y - TranscriptScroll.Viewport.Height;
            if (shortfall > 0)
            {
                TranscriptScroll.Offset = TranscriptScroll.Offset.WithY(TranscriptScroll.Offset.Y + shortfall);
            }
        }
        finally
        {
            _following = false;
        }
    }

    // Whether the newest row is fully on screen — not Extent, which is only an estimate that
    // (measured, AC-528) sat ~300px above a reachable bottom. The row's own bottom edge is measured.
    private bool _NewestRowIsFullyVisible()
    {
        // The newest row the reading level actually shows — following one it hides can never terminate, see
        // _NewestVisibleIndex. Nothing shown at all is trivially "at the bottom".
        var newestIndex = _NewestVisibleIndex();
        if (newestIndex < 0)
        {
            return true;
        }

        // Not realised means it is off-screen below, which is the whole point of virtualisation — so not at the
        // bottom. (Above the viewport it cannot be: it is the last row.)
        if (TranscriptItems.ContainerFromIndex(newestIndex) is not { } newest)
        {
            return false;
        }

        var bottom = newest.TranslatePoint(new Point(0, newest.Bounds.Height), TranscriptScroll);
        // A pixel of slack for layout rounding, in the same spirit as TranscriptScrollAnchor's tolerance.
        return bottom is not null && bottom.Value.Y <= TranscriptScroll.Viewport.Height + 1.0;
    }

    // Whether the matching KeyDown already handled this keystroke — a tunnelled KeyDown's own `e.Handled` cannot
    // be read back from the later KeyUp, so this is how `_OnInputKeyUp` tells caret-driven typing (unhandled,
    // falls through to the TextBox's default editing) apart from a programmatic text mutation.
    private bool _lastInputKeyWasHandled;

    private void _OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        _OnInputKeyDownCore(e);
        _lastInputKeyWasHandled = e.Handled;
    }

    private void _OnInputKeyDownCore(KeyEventArgs e)
    {
        // AC-740: the open picker gets first refusal on these five keys, ahead of every handler below — Up
        // otherwise recalls, Escape otherwise stops the turn, and Enter otherwise sends.
        if (DataContext is SessionViewModel { MentionPicker.IsOpen: true } mentionVm)
        {
            var picker = mentionVm.MentionPicker;
            switch (e.Key)
            {
                case Key.Up:
                    picker.Move(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    picker.Move(1);
                    e.Handled = true;
                    return;
                case Key.Tab:
                case Key.Enter:
                    if (picker.Accept() is { } acceptance)
                    {
                        _InsertMention(acceptance);
                    }

                    e.Handled = true;
                    return;
                case Key.Escape:
                    picker.Dismiss();
                    e.Handled = true;
                    return;
            }
        }

        if (_IsPasteGesture(e))
        {
            // Clipboard read is async but the default TextBox paste runs synchronously on this same
            // KeyDown, so the default is suppressed and the whole paste is routed by hand instead.
            e.Handled = true;
            _ = _HandlePasteAsync();
            return;
        }

        // Arrow Up on an empty input recalls the most recently queued message back into the box for
        // editing (mirrors shell history). Guarded on an empty input so it never clobbers text you are
        // typing and Up otherwise moves the caret as usual.
        if (e.Key == Key.Up
            && string.IsNullOrEmpty(InputBox.Text)
            && DataContext is SessionViewModel recallVm
            && recallVm.RecallLastQueuedMessage())
        {
            e.Handled = true;
            return;
        }

        // Esc interrupts the running turn (like the claude TUI), mirroring the Stop button. Only while
        // a turn is in flight, so Esc is otherwise free to do its normal thing (clear selection, etc.).
        if (e.Key == Key.Escape)
        {
            if (DataContext is SessionViewModel { IsBusy: true } busyVm && busyVm.StopCommand.CanExecute(null))
            {
                busyVm.StopCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
        // Enter mirrors the Send button: SendAsync queues the message itself when a turn is in flight
        // (T8), so gate only on there being something to send — not on IsBusy, which used to block
        // Enter while busy and left queueing reachable via the Send button only.
        if (DataContext is SessionViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
        }
    }

    private static bool _IsPasteGesture(KeyEventArgs e) =>
        e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control);

    // Handles CTRL+V: a clipboard bitmap becomes a PNG pending attachment, otherwise text is
    // inserted normally. AddPastedImage gates on CanPasteImages (#64) — a driver that can't send
    // images gets a transcript notice instead of a silently vanishing attachment.
    private async System.Threading.Tasks.Task _HandlePasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || DataContext is not SessionViewModel vm)
        {
            return;
        }

        try
        {
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                using (bitmap)
                {
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                    vm.AddPastedImage(stream.ToArray());
                }

                return;
            }

            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                _InsertText(text);
            }
        }
        catch (Exception)
        {
            // Clipboard unavailable (locked by another app, unsupported content): drop the paste
            // rather than crash the UI thread.
        }
    }

    // Inserts text at the caret, replacing any current selection — mirrors a normal paste.
    private void _InsertText(string text)
    {
        var start = Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd);
        var end = Math.Max(InputBox.SelectionStart, InputBox.SelectionEnd);
        var current = InputBox.Text ?? string.Empty;
        var next = current[..start] + text + current[end..];
        InputBox.Text = next;
        InputBox.CaretIndex = start + text.Length;
        InputBox.SelectionStart = InputBox.CaretIndex;
        InputBox.SelectionEnd = InputBox.CaretIndex;
    }

    // AC-740: re-evaluates the @-mention token after a keystroke the TextBox itself handled (typed, backspace,
    // caret moved) — `_lastInputKeyWasHandled` tells this apart from a programmatic mutation (voice, recall, a
    // pasted block), all of which raise no KeyUp here except Ctrl+V, already marked handled above.
    private void _OnInputKeyUp(object? sender, KeyEventArgs e)
    {
        if (_lastInputKeyWasHandled || DataContext is not SessionViewModel vm)
        {
            return;
        }

        vm.MentionPicker.OnTextChanged(InputBox.Text ?? string.Empty, InputBox.CaretIndex);
    }

    // Splices an accepted mention into the text: replaces [TokenStart..caret] with '@' + path + a trailing space.
    private void _InsertMention(MentionAcceptance acceptance)
    {
        var current = InputBox.Text ?? string.Empty;
        var end = Math.Clamp(InputBox.CaretIndex, 0, current.Length);
        var start = Math.Clamp(acceptance.TokenStart, 0, end);
        var replacement = $"@{acceptance.Path} ";
        InputBox.Text = current[..start] + replacement + current[end..];
        InputBox.CaretIndex = start + replacement.Length;
        InputBox.SelectionStart = InputBox.CaretIndex;
        InputBox.SelectionEnd = InputBox.CaretIndex;
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
