using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class SessionView : UserControl
{
    // Follow the newest transcript row while parked at the bottom; pause when the user scrolls up to
    // read history, resume once they scroll back down (#21). Avalonia has no built-in stick-to-bottom.
    private bool _stickToBottom = true;

    /// <summary>
    /// Ticks the composer's tool-activity elapsed time once a second (AC-532), so "running 0:12" counts up
    /// instead of freezing at whatever it read on first render — and, since AC-531, the background-work
    /// pop-out's own per-task elapsed times alongside it. Lives here rather than in the view model: the derived
    /// state (which tool, since when) has to stay dispatcher-free to be unit-testable outside a running Avalonia
    /// app (<c>Cockpit.Core.Tests</c> calls <c>SessionViewModel.Apply</c> directly, with no platform initialized),
    /// so only this purely cosmetic re-tick — a no-op when nothing is running — lives in the view.
    /// </summary>
    private DispatcherTimer? _activityAgeTicker;

    // AC-528: whether the tail-following decision below was caused by the operator, latched directly from input
    // rather than guessed from which ScrollChanged deltas moved (three delta-based guesses each broke on a
    // different real case — a wheel-up, a passive Extent re-estimate on growth, and one on shrink, all confirmed
    // by measurement against the real SessionView/VirtualizingStackPanel). _userScrolled is one-shot: a wheel tick
    // or a scrollbar press is a momentary gesture that produces exactly one ScrollChanged (measured 60/60 wheel
    // ticks, 1/1 track clicks), so it is set on the input event and cleared the moment the next ScrollChanged
    // reads it. _draggingThumb is level-triggered instead: a thumb drag is a held gesture that can span several
    // ScrollChanged events with no new input event in between (measured 2 ScrollChanged from one drag, only the
    // first preceded by a fresh PointerPressed) — Thumb.DragStarted/DragCompleted bracket the whole gesture, so
    // every ScrollChanged inside it reads true.
    private bool _userScrolled;
    private bool _draggingThumb;
    private bool _resnapping;
    private ScrollBar? _verticalScrollBar;

    public SessionView()
    {
        InitializeComponent();

        // Tunnel (not bubble) so the flag is set before the ScrollChanged it causes fires — ScrollChanged only
        // raises from LayoutUpdated, which Avalonia always processes after the input that triggered it has
        // finished dispatching, so tunnel vs. bubble does not actually matter for ordering here; tunnel is used
        // anyway so this reads the same as the other input pre-emption in this file (line ~34).
        TranscriptScroll.AddHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel, RoutingStrategies.Tunnel);
        // PageUp/PageDown scrolling exists on ScrollViewer itself (Avalonia), but TranscriptScroll is not
        // Focusable today so this path is not reachable yet — wired for parity/if that ever changes, not because
        // it fires now.
        TranscriptScroll.AddHandler(InputElement.KeyDownEvent, _OnTranscriptKeyDown, RoutingStrategies.Tunnel);

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
        Dispatcher.UIThread.Post(() => InputBox.Focus());

        TranscriptScroll.ScrollChanged += _OnTranscriptScrollChanged;
        // Land on the newest row if the panel re-attaches with an existing transcript.
        Dispatcher.UIThread.Post(() => { if (_stickToBottom) TranscriptScroll.ScrollToEnd(); });

        _activityAgeTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _activityAgeTicker.Tick += _OnActivityAgeTick;
        _activityAgeTicker.Start();

        // Scoped to the scrollbar itself (not the whole transcript) so clicking a row's own button — Copy, a
        // tool-chip toggle — never latches this: those clicks do not tunnel through the scrollbar, so they cannot
        // set it, and a stray true here would wrongly block the "keep following" action on the next unrelated
        // passive ScrollChanged (a streamed-in row), reproducing the exact bug this latch replaces.
        _verticalScrollBar = TranscriptScroll.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(s => s.Name == "PART_VerticalScrollBar");
        _verticalScrollBar?.AddHandler(InputElement.PointerPressedEvent, _OnScrollbarPressed, RoutingStrategies.Tunnel);
        TranscriptScroll.AddHandler(Thumb.DragStartedEvent, _OnThumbDragStarted);
        TranscriptScroll.AddHandler(Thumb.DragCompletedEvent, _OnThumbDragCompleted);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TranscriptScroll.ScrollChanged -= _OnTranscriptScrollChanged;

        _activityAgeTicker?.Stop();
        _activityAgeTicker = null;

        _verticalScrollBar?.RemoveHandler(InputElement.PointerPressedEvent, _OnScrollbarPressed);
        TranscriptScroll.RemoveHandler(Thumb.DragStartedEvent, _OnThumbDragStarted);
        TranscriptScroll.RemoveHandler(Thumb.DragCompletedEvent, _OnThumbDragCompleted);
        _verticalScrollBar = null;
        _draggingThumb = false;

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

    /// <summary>Latches that the operator, not passive content growth, caused whatever ScrollChanged comes next —
    /// a wheel tick and a scrollbar press (track click, or the start of a thumb drag) are both momentary (one-shot)
    /// gestures that measured out to exactly one resulting ScrollChanged.</summary>
    private void _OnTranscriptWheel(object? sender, PointerWheelEventArgs e) => _userScrolled = true;

    private void _OnScrollbarPressed(object? sender, PointerPressedEventArgs e) => _userScrolled = true;

    private void _OnTranscriptKeyDown(object? sender, KeyEventArgs e) => _userScrolled = true;

    private void _OnThumbDragStarted(object? sender, VectorEventArgs e) => _draggingThumb = true;

    private void _OnThumbDragCompleted(object? sender, VectorEventArgs e) => _draggingThumb = false;

    private void _OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // The re-snap below drives its own layout passes synchronously, and each one raises ScrollChanged straight
        // back into here — unguarded that recurses until the stack overflows and takes the whole host down
        // (measured). Those nested events are the convergence itself, i.e. geometry that is still mid-flight: the
        // outer call re-derives from the settled result the moment the re-snap returns, so there is nothing here to
        // do but stay out of the way.
        if (_resnapping)
        {
            return;
        }

        // AC-528: three delta-based guesses at "was this the operator" each broke on a real, measured case — a
        // wheel-up misread as content growth, a passive Extent re-estimate on growth misread the same way, and one
        // on shrink that overshoots the offset clamp in the same layout pass (dOffset past dExtent by tens to
        // thousands of px once a fold/group change lands in the same pass as a streamed row). Guessing the cause
        // from which deltas moved cannot distinguish these from a real scroll, because the panel's own geometry
        // correction is not bounded by anything the deltas expose. So: stop guessing, latch the cause instead.
        // _userScrolled/_draggingThumb are set directly by input handlers above — never by this method or by the
        // re-snap below, so the re-snap can never poison its own next decision.
        var userCaused = _userScrolled || _draggingThumb;
        _userScrolled = false; // one-shot: consumed by whichever ScrollChanged reads it next; _draggingThumb is not

        // AC-528: ScrollToEnd() alone cannot reach the bottom of a virtualized list. Under a VirtualizingStackPanel,
        // Extent is an *estimate* (EstimateElementSizeU averages only the realized rows), so it shifts the moment a
        // jump changes which rows are realized — while ScrollToEnd's target, Extent - Viewport, was computed from
        // the pre-jump estimate. When a Focus-level group folds, that estimate first undershoots, CoerceOffset
        // clamps the offset to the shrunken maximum, and the next pass restores the extent but not the offset:
        // measured, the offset stayed pinned at 1605.0 while ScrollToEnd re-targeted 1669.0/1664.0 every pass
        // (Extent alternating 2027.0/2022.0) — a ~59-64px gap that never closed and re-invalidated layout forever
        // ("Infinite layout loop detected"). Not scroll anchoring: unregistering every IScrollAnchorProvider
        // candidate first was measured and reproduced the identical numbers.
        //
        // ScrollIntoView is Avalonia's own answer to exactly this lag — VirtualizingStackPanel.ScrollIntoView
        // re-runs the layout pass and re-issues BringIntoView until the extent has caught up (see its own "the
        // scroll extent might have been out of date" comment). It converges the estimate but stops short when the
        // last row is taller than the viewport, because BringIntoView then aligns that row's top (measured: the
        // streamed-text test stops following at tick 33 without the line below) — so ScrollToEnd runs after it, now
        // against a settled Extent. Together: gap 0.0 at every measured point, and no retry cap needed.
        if (!userCaused && (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) && _stickToBottom)
        {
            _resnapping = true;
            try
            {
                TranscriptItems.ScrollIntoView(TranscriptItems.ItemCount - 1);
                TranscriptScroll.ScrollToEnd();
            }
            finally
            {
                _resnapping = false;
            }
        }

        // Re-derive from where the viewport actually ended up rather than from which deltas moved — that geometry
        // is the same after a user scroll, a streamed-in row, a composer band toggling, or the re-snap just above,
        // so one derivation covers all of them and nothing needs to guess at the cause.
        _stickToBottom = TranscriptScrollAnchor.IsAtBottom(
            TranscriptScroll.Offset.Y, TranscriptScroll.Extent.Height, TranscriptScroll.Viewport.Height);

        // Offer the jump-to-newest button only while scrolled up (i.e. not following the tail).
        ScrollToBottomButton.IsVisible = !_stickToBottom;
    }

    private void _OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        TranscriptScroll.ScrollToEnd();
        ScrollToBottomButton.IsVisible = false;
    }

    /// <summary>Whole-row click expands (or, on the selected row, collapses) a background task's detail in the
    /// pop-out (AC-531) — the clicked row's DataContext is the task itself, same idiom as the delegated-tasks
    /// dialog's row click.</summary>
    private void _OnBackgroundTaskPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: BackgroundTaskViewModel task } && DataContext is SessionViewModel vm)
        {
            vm.ToggleBackgroundTaskSelection(task);
        }
    }

    /// <summary>Copies a tool result's formatted text to the clipboard (T6).</summary>
    private void _OnCopyResultClick(object? sender, RoutedEventArgs e) => _CopyRowText(sender, entry => entry.ResultDisplayText);

    /// <summary>Copies an assistant reply's markdown source to the clipboard — the per-reply hover action.</summary>
    private void _OnCopyMessageClick(object? sender, RoutedEventArgs e) => _CopyRowText(sender, entry => entry.Text);

    /// <summary>Both copy buttons sit on a transcript row, so the sender's DataContext is that row — copy the
    /// selected text from it to the clipboard.</summary>
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

    /// <summary>
    /// Handles CTRL+V ourselves: a bitmap on the clipboard becomes a PNG pending attachment on the
    /// view model; otherwise any clipboard text is inserted into the input as a normal text paste.
    /// <see cref="SessionViewModel.AddPastedImage"/> itself gates on <see cref="SessionViewModel.CanPasteImages"/>
    /// (#64) — a session whose driver cannot actually send images gets a transcript notice instead of a
    /// silently vanishing attachment, since CTRL+V has no button here to hide.
    /// </summary>
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

    /// <summary>Inserts text at the caret, replacing any current selection — mirrors a normal paste.</summary>
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

    /// <summary>
    /// KeyDown for the push-to-talk hotkey. <see cref="SessionViewModel.BeginVoiceHold"/> itself
    /// guards against OS key-repeat re-triggering a capture restart while the key stays held, so this
    /// only marks the event handled when a hold actually started — an ignored press (voice off, or
    /// already holding) leaves the key free for anything else bound to it. No-ops when global
    /// push-to-talk is active (see <see cref="PushToTalkKeyGate"/>) so the global coordinator's hold
    /// does not fire twice.
    /// </summary>
    private void _OnPushToTalkKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SessionViewModel vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled, vm.OpenMicActive)
            && vm.BeginVoiceHold())
        {
            e.Handled = true;
        }
    }

    /// <summary>KeyUp for the push-to-talk hotkey: ends the hold, transcribes with cleanup, and appends the result to the input box.</summary>
    private void _OnPushToTalkKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is SessionViewModel vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled, vm.OpenMicActive))
        {
            e.Handled = true;
            _ = vm.EndVoiceHoldAsync(applyCleanup: true);
        }
    }
}
