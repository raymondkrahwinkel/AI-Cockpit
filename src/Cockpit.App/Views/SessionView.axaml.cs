using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class SessionView : UserControl
{
    // Follow the newest transcript row while parked at the bottom; pause when the user scrolls up to
    // read history, resume once they scroll back down (#21). Avalonia has no built-in stick-to-bottom.
    private bool _stickToBottom = true;

    // True while `_FollowNewest` is moving the viewport itself, so the scroll changes it
    // causes are never mistaken for the operator's — and never re-enter it.
    private bool _following;

    // An operator gesture that can scroll happened, and the scroll change it produces is the next one to arrive.
    // The delta fields cannot stand in for this: a virtualising panel corrects its own offset after arrange, and
    // such a correction moves the offset with the extent and viewport both standing still — which is precisely
    // the fingerprint the old code called "a real user scroll". Three rounds of this ticket died on that
    // ambiguity. The wheel is a single event, so this is a one-shot flag; a scrollbar drag lasts as long as the
    // button is held, which is what `_pointerHeld` is for.
    // "The next one to arrive" is the whole claim, and it needs an expiry to stay true — see _OnTranscriptWheel.
    private bool _wheelTurned;

    private bool _pointerHeld;

    // Ticks the composer's tool-activity elapsed time once a second (AC-532), so "running 0:12" counts up
    // instead of freezing at whatever it read on first render — and, since AC-531, the background-work
    // pop-out's own per-task elapsed times alongside it. Lives here rather than in the view model: the derived
    // state (which tool, since when) has to stay dispatcher-free to be unit-testable outside a running Avalonia
    // app (`Cockpit.Core.Tests` calls `SessionViewModel.Apply` directly, with no platform initialized),
    // so only this purely cosmetic re-tick — a no-op when nothing is running — lives in the view.
    private DispatcherTimer? _activityAgeTicker;

    public SessionView()
    {
        InitializeComponent();

        // Enter sends the message; Shift+Enter inserts a newline. Tunnel so we pre-empt the
        // TextBox's own Enter handling (which would otherwise insert a newline).
        InputBox.AddHandler(InputElement.KeyDownEvent, _OnInputKeyDown, RoutingStrategies.Tunnel);

        // Push-to-talk (F9 by default): tunnel on the whole panel, not just the input box, so it fires
        // regardless of which control inside the panel has focus — the operator should not have to
        // click into the input first to dictate.
        AddHandler(InputElement.KeyDownEvent, _OnPushToTalkKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, _OnPushToTalkKeyUp, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Focus the input as soon as a session panel appears, so a freshly created session is ready to
        // type in without a click (L10). Deferred so focus lands after the panel is laid out.
        // Not while the operator is in another window (AC-636): in single-pane/zoom mode a session closing swaps
        // which pane is realised, and this attach would then take the keyboard out of the assistant's chat pop-out
        // — the same steal as the selection path's, one control further down.
        // AC-650: and not for a pane that is not the selection. RestoreSessionPanesAsync attaches every
        // restored pane's view in the same burst; every one of them used to post this same Focus() regardless
        // of selection, so each attach tore the keyboard away from the last (Avalonia's TextBox teardown on
        // that hand-off, TextBoxTextInputMethodClient.SetPresenter, is what became ruinously slow). Only the
        // one CockpitView already means to focus (via SelectedSession) claims it here; the others still
        // restore fully, they just do not fight over the caret.
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

        _activityAgeTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _activityAgeTicker.Tick += _OnActivityAgeTick;
        _activityAgeTicker.Start();
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

        base.OnDetachedFromVisualTree(e);
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

        // Offer the jump-to-newest button only while scrolled up (i.e. not following the tail).
        ScrollToBottomButton.IsVisible = !_stickToBottom;
    }

    // A wheel turn at the bottom of the transcript moves nothing, so it raises no ScrollChanged at all — and the
    // flag, cleared only in that handler, then stands until some later and entirely unrelated change comes to
    // consume it. Measured (AC-621): park at the newest row, roll one click further down, send a message four
    // lines or longer, and the row you just sent is read as the operator scrolling away from the tail — the
    // follow stops, 62px short of the bottom at eight lines, 817px at sixty. Without the wheel turn first, the
    // same message never breaks it. So expire the flag on the event that always happens, the end of this turn's
    // layout work: Background is below Layout and Render, so every ScrollChanged the wheel genuinely did cause
    // has already been raised — and one that never came can no longer be charged to the operator.
    private void _OnTranscriptWheel(object? sender, PointerWheelEventArgs e)
    {
        _wheelTurned = true;
        Dispatcher.UIThread.Post(() => _wheelTurned = false, DispatcherPriority.Background);
    }

    private void _OnTranscriptPointerPressed(object? sender, PointerPressedEventArgs e) => _pointerHeld = true;

    private void _OnTranscriptPointerReleased(object? sender, RoutedEventArgs e) => _pointerHeld = false;

    private void _OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        _FollowNewest();
        ScrollToBottomButton.IsVisible = false;
    }

    // Puts the viewport on the newest row. It asks for the row rather than for an offset, because the offset
    // this used to use — `ScrollToEnd()`, i.e. `Extent - Viewport` — is computed from an estimate the
    // panel then corrects on its next arrange: measured at four window sizes with a folded run streaming in, it
    // left the transcript some 300px short of the bottom the panel would accept, which is the row that kept
    // half-hiding under the composer hairline (AC-528, criterion 5). Worse, the correction raises another
    // ScrollChanged, so following it lands right back on a fresh estimate — measured, that is a layout loop the
    // manager gives up on ("Infinite layout loop detected"). Asking for the last row terminates instead: once it
    // is in view the guard above says so and this does nothing.
    // The last row that is actually on screen, which is not the last row. A reading level hides rows: at Focus the
    // steps of a folded run collapse behind their anchor, and the newest row is then very often one of them.
    //
    // Following a hidden row cannot terminate. It has no height to bring into view, so the follow is never
    // satisfied, asks for it again on the next scroll change, and each ask realises the row and its template
    // afresh — read off a hung session's stacks as ScrollIntoView → Measure → ApplyTemplate → styling, over and
    // over. That is the Focus pane freezing, and why Developer never froze: there, every row is visible and the
    // follow converges on the first try.
    private int _NewestVisibleIndex()
    {
        for (var i = TranscriptItems.ItemCount - 1; i >= 0; i--)
        {
            if (TranscriptItems.ItemsView[i] is TranscriptEntryViewModel { IsRowVisible: true })
            {
                return i;
            }
        }

        return -1;
    }

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
            // ScrollIntoView forces a synchronous layout pass of its own. While a reply streams, the newest row is
            // the one on screen, so asking for it costs that pass on every repaint for a row that is already
            // realised — measured, roughly three layout passes per frame where one would do. Ask only when the
            // row genuinely is not there, which is the case this call exists for: a jump from far up the history.
            if (TranscriptItems.ContainerFromIndex(newestIndex) is null)
            {
                TranscriptItems.ScrollIntoView(newestIndex);
            }

            // ScrollIntoView brings the row's rect into view, and a rect taller than the viewport is already
            // "in view" the moment its top edge is: a streaming reply several viewports tall therefore stops the
            // viewport moving at all, while the row's bottom — what _NewestRowIsFullyVisible asks about — stays
            // permanently below it. That leaves the follow unsatisfiable, so every ScrollChanged for the rest of
            // the session calls back in here and drives another layout pass over a row that is measured whole:
            // measured, the cost per delta climbs with the reply rather than settling, which is the SDK pane
            // freezing with memory running away. Close the residue by hand — it is the row's own measured bottom,
            // not an extent estimate, so it converges instead of chasing a figure the panel keeps correcting.
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

    // Whether the newest row is on screen in full — the transcript's honest answer to "are we at the bottom",
    // and the reason this does not ask `Extent`. The transcript virtualises, so `Extent` is an
    // estimate assembled from whichever rows happen to be realised; measured across four window sizes with a
    // folded run streaming in, `Extent - Viewport` sat some 300px above any offset the panel would accept,
    // which is a bottom the operator can never reach and a follow that can therefore never resume (AC-528).
    // The last row's own bottom edge is a measurement rather than an estimate, and it is also exactly what
    // criterion 5 is about: the newest row clear of the composer hairline, not half under it.
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

    // Copies a tool result's formatted text to the clipboard (T6).
    private void _OnCopyResultClick(object? sender, RoutedEventArgs e) => _CopyRowText(sender, entry => entry.ResultDisplayText);

    // Copies an assistant reply's markdown source to the clipboard — the per-reply hover action.
    private void _OnCopyMessageClick(object? sender, RoutedEventArgs e) => _CopyRowText(sender, entry => entry.Text);

    // Both copy buttons sit on a transcript row, so the sender's DataContext is that row — copy the
    // selected text from it to the clipboard.
    private void _CopyRowText(object? sender, Func<TranscriptEntryViewModel, string> select)
    {
        if (sender is Control { DataContext: TranscriptEntryViewModel entry }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(select(entry));
        }
    }

    private void _OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_IsPasteGesture(e))
        {
            // The clipboard read is async but the default TextBox paste runs synchronously on this
            // same KeyDown. To avoid a race where the default paste dumps binary/plaintext before
            // our async read decides, we take over the whole paste: suppress the default now, then
            // async-read the clipboard and route it ourselves (image -> attachment, text -> insert).
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

    // Handles CTRL+V ourselves: a bitmap on the clipboard becomes a PNG pending attachment on the
    // view model; otherwise any clipboard text is inserted into the input as a normal text paste.
    // `SessionViewModel.AddPastedImage` itself gates on `SessionViewModel.CanPasteImages`
    // (#64) — a session whose driver cannot actually send images gets a transcript notice instead of a
    // silently vanishing attachment, since CTRL+V has no button here to hide.
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
                    bitmap.Save(stream);
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

    // KeyDown for the push-to-talk hotkey. `SessionViewModel.BeginVoiceHold` itself
    // guards against OS key-repeat re-triggering a capture restart while the key stays held, so this
    // only marks the event handled when a hold actually started — an ignored press (voice off, or
    // already holding) leaves the key free for anything else bound to it. No-ops when global
    // push-to-talk is active (see `PushToTalkKeyGate`) so the global coordinator's hold
    // does not fire twice.
    private void _OnPushToTalkKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SessionViewModel vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled)
            && vm.BeginVoiceHold())
        {
            e.Handled = true;
        }
    }

    // KeyUp for the push-to-talk hotkey: ends the hold, transcribes, and appends the result to the input box.
    private void _OnPushToTalkKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is SessionViewModel vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled))
        {
            e.Handled = true;
            _ = vm.EndVoiceHoldAsync();
        }
    }
}
