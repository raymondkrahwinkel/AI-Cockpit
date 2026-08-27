using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Help;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// AC-543 (criteria 7-9, 11): chat surface, a peephole onto its own standing session. Wires what
// XAML can't: Enter-to-send, attaching to the session, transcript auto-scroll. Disposing the view
// model stays the host's business, not this view's — a host swap (AC-953) must not end the conversation.
public partial class AssistantChatView : UserControl
{
    // AC-1121: the follow lives in TranscriptFollower, shared with SessionView. It existed here as a second copy
    // and the two drifted both ways — this half carried AC-953's fix, the other half AC-996's.
    private TranscriptFollower? _follower;

    private PropertyChangedEventHandler? _sessionHandler;
    private SessionViewModel? _attachedSession;

    private ScrollViewer? _transcriptScroll;

    // The two independent reasons this view's rows can be dropped, kept apart so lifting one never lifts the
    // other. See SessionView's own fields — same pause, same AC-883 reasoning. The host-minimised one is pushed
    // in by whoever hosts this (AssistantChatWindow); a docked host has no "minimised" of its own.
    private bool _hostMinimised;
    private bool _renderClockPaused;

    private AssistantChatViewModel? _attachedViewModel;

    // Set by a test before this is shown; otherwise resolved from the container on attach, and only on macOS —
    // see SessionView._ResolveDiagnostics for why Windows and X11 must not even subscribe.
    internal DiagnosticsBackgroundService? Diagnostics { get; set; }

    // AC-774: lives inside TranscriptItems' own template so the virtualising panel measures
    // against the viewport, not an enclosing ScrollViewer's infinite height (mirrors SessionView's
    // TranscriptScroll, AC-686) — resolved from the template since a template name isn't a field.
    internal ScrollViewer? TranscriptScroll =>
        _transcriptScroll ??= _ResolveTranscriptScroll();

    // ApplyTemplate, not just a visual-tree walk: this is first asked for from the attach that wires the scroll
    // handlers, and at that point the transcript has not been measured, so its template child does not exist yet.
    // AC-1130: FirstOrDefault, not First — a throw here used to abort the whole of OnDetachedFromVisualTree.
    private ScrollViewer? _ResolveTranscriptScroll()
    {
        TranscriptItems.ApplyTemplate();
        return TranscriptItems.GetVisualChildren().OfType<ScrollViewer>().FirstOrDefault();
    }

    private TranscriptFollower Follower =>
        _follower ??= new TranscriptFollower(TranscriptItems, () => TranscriptScroll);

    public AssistantChatView()
    {
        InitializeComponent();
#if DEBUG
        Cockpit.App.Diagnostics.LeakTracker.Register(this);
#endif

        // AC-1040: the assistant's page, beside the title. Absent rather than dead when the page is not shipped.
        if (Program.Services?.GetService<HelpService>() is { } help)
        {
            TitleRow.Children.Add(new HelpHint(
                help,
                new HelpAddress("assistant", "what-it-can-do"),
                origin: "a “?” in the assistant window"));
        }

        // Enter sends; Shift+Enter inserts a newline — the same convention as the main session composer
        // (SessionView._OnInputKeyDown). Tunnel so this pre-empts the TextBox's own Enter handling.
        InputBox.AddHandler(InputElement.KeyDownEvent, _OnInputKeyDown, RoutingStrategies.Tunnel);

        // AC-740: re-evaluates the @-mention token once the TextBox has applied the keystroke — same split as
        // SessionView's.
        InputBox.KeyUp += _OnInputKeyUp;
    }

    // What used to be the window's `Opened` handler. Everything wired here comes off again in
    // OnDetachedFromVisualTree, so this view survives being moved between hosts (AC-953) without leaking.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is AssistantChatViewModel vm)
        {
            _attachedViewModel = vm;
            vm.PropertyChanged += _OnViewModelPropertyChanged;
            _AttachTranscript(vm.Session);

            // Criterion 1: opening the chip is the "operator handling" allowed to lazily start the assistant.
            // EnsureOpenedAsync only ever attaches to (or restarts) the host's own standing session — it never
            // resets one, so this never contradicts criterion 7.
            _ = vm.EnsureOpenedAsync();
        }

        if (TranscriptScroll is { } scroll)
        {
            scroll.ScrollChanged += _OnTranscriptScrollChanged;
            // Tunnel + handledEventsToo so a child's own scroller cannot hide the gesture; all removed on detach.
            scroll.AddHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
            scroll.AddHandler(InputElement.PointerPressedEvent, _OnTranscriptPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            scroll.AddHandler(InputElement.PointerReleasedEvent, _OnTranscriptPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            scroll.AddHandler(InputElement.PointerCaptureLostEvent, _OnTranscriptPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        Dispatcher.UIThread.Post(() => InputBox.Focus());

        // Posted, not run here: the transcript has not been arranged yet, so neither following the tail nor
        // restoring AC-953's handed-over position has any row to measure against until it has.
        if (_attachedViewModel?.TranscriptAnchor is { } anchor)
        {
            Dispatcher.UIThread.Post(() => _RestoreScrollAnchor(anchor));
        }
        else
        {
            Follower.RequestFollow();
        }

        if (Diagnostics is null && OperatingSystem.IsMacOS())
        {
            Diagnostics = Program.Services?.GetService<DiagnosticsBackgroundService>();
        }

        if (Diagnostics is { } diagnostics)
        {
            diagnostics.RenderersShouldPauseChanged += _OnRenderersShouldPauseChanged;
            _renderClockPaused = diagnostics.RenderersShouldPause;
        }

        _ApplyRendererPause();
    }

    // What used to be the window's `OnClosed`, minus the view model's Dispose: ending the conversation is the
    // host's call (AssistantChatWindow does it on close), never a consequence of leaving the visual tree.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Before the handlers come off, while the rows are still arranged and measurable (AC-953).
        _CaptureScrollAnchor();

        _DetachTranscript();

        // The one attached on the way in, not whatever DataContext reads now — a DataContext swapped while
        // attached would otherwise leave the old view model still holding this handler.
        if (_attachedViewModel is { } vm)
        {
            vm.PropertyChanged -= _OnViewModelPropertyChanged;
            _attachedViewModel = null;
        }

        // The service outlives this view, so a missed unsubscribe keeps the whole detached view alive.
        if (Diagnostics is { } diagnostics)
        {
            diagnostics.RenderersShouldPauseChanged -= _OnRenderersShouldPauseChanged;
        }

        // Last, because resolving the scroll owner is the one step here that can come up empty (AC-1130).
        if (_transcriptScroll is { } scroll)
        {
            scroll.ScrollChanged -= _OnTranscriptScrollChanged;
            scroll.RemoveHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel);
            scroll.RemoveHandler(InputElement.PointerPressedEvent, _OnTranscriptPointerPressed);
            scroll.RemoveHandler(InputElement.PointerReleasedEvent, _OnTranscriptPointerReleased);
            scroll.RemoveHandler(InputElement.PointerCaptureLostEvent, _OnTranscriptPointerReleased);
        }

        base.OnDetachedFromVisualTree(e);

        // AC-878: investigated as a SessionView-fix candidate, deliberately left without one — see
        // AssistantChatLeakHuntTests for the evidence this view does not carry that leak.
    }

    // Pushed in by the host: AC-883's minimised-window pause, which only a window has.
    internal void SetHostMinimised(bool minimised)
    {
        _hostMinimised = minimised;
        _ApplyRendererPause();
    }

    private void _OnRenderersShouldPauseChanged(object? sender, bool paused) => SetRenderClockPaused(paused);

    // Internal so a view test can drive the edge; production reaches it through the event above.
    internal void SetRenderClockPaused(bool paused)
    {
        _renderClockPaused = paused;
        _ApplyRendererPause();
    }

    // Same leak/fix as SessionView's _ApplyRendererPause: while minimised the renderer is paused so
    // recycled rows never get the compositor commit that removes their visuals. Collapse the
    // scroll owner while paused so the panel dematerialises rows instead of piling them up.
    private void _ApplyRendererPause()
    {
        // AC-1130: a pause signal can arrive before attach has built the scroll owner, and both seams above are
        // reachable before then.
        if (TranscriptScroll is not { } scroll)
        {
            return;
        }

        var paused = _hostMinimised || _renderClockPaused;
        scroll.IsVisible = !paused;

        if (!paused)
        {
            // RequestFollow re-reads the follow when the step runs, not now: AC-953's scroll handover happens on
            // this very attach, and a post queued while sticky used to jump to the tail after it.
            Follower.RequestFollow();
        }
    }

    // AC-1121: no follow from here — Avalonia raises this from LayoutUpdated, so following it queued a nested
    // layout pass inside the pass that raised it.
    private void _OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        Follower.NoteScrollChanged(e.ViewportDelta.Y);
        ScrollToBottomButton.IsVisible = !Follower.StickToBottom;
    }

    private void _OnTranscriptWheel(object? sender, PointerWheelEventArgs e) => Follower.NoteWheel();

    private void _OnTranscriptPointerPressed(object? sender, PointerPressedEventArgs e) => Follower.NotePointerHeld(true);

    private void _OnTranscriptPointerReleased(object? sender, RoutedEventArgs e) => Follower.NotePointerHeld(false);

    private void _OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        Follower.StickToBottom = true;
        Follower.RequestFollow();
        ScrollToBottomButton.IsVisible = false;
    }

    // AC-1022: lets the reply button send focus to the composer after setting its target.
    internal void FocusInput() => InputBox.Focus();

    // AC-935: a reply's citation and a replied-to row's marker both jump here. The follow has to come off
    // first, or the ScrollChanged handler reads "not an operator gesture, still sticky" and follows straight
    // back to the newest row — the jump-to-newest chevron is the way back, same as after a manual scroll.
    internal void ScrollToMessage(TranscriptEntryViewModel target)
    {
        if (DataContext is not AssistantChatViewModel { Session: { } session })
        {
            return;
        }

        var index = session.VisibleTranscript.IndexOf(target);
        if (index < 0)
        {
            return;
        }

        Follower.StickToBottom = false;
        TranscriptItems.ScrollIntoView(index);
        ScrollToBottomButton.IsVisible = true;
    }

    // AC-953: hands this view's scroll position to whatever view the next host builds. Null while following the
    // tail — a fresh view follows it too, so there is nothing to carry — and null when nothing is realised to
    // measure, which is what a view that was never arranged looks like.
    private void _CaptureScrollAnchor()
    {
        if (_attachedViewModel is not { } vm)
        {
            return;
        }

        vm.TranscriptAnchor = Follower.StickToBottom ? null : _TopVisibleRow();
    }

    // The first row whose bottom edge is still below the viewport's top — the one the operator is reading from.
    // Only realised containers can be measured, and the topmost visible row is realised by definition.
    private TranscriptScrollPosition? _TopVisibleRow()
    {
        if (TranscriptScroll is not { } scroll)
        {
            return null;
        }

        for (var index = 0; index < TranscriptItems.ItemCount; index++)
        {
            if (TranscriptItems.ContainerFromIndex(index) is not { } row
                || row.TranslatePoint(new Point(0, 0), scroll) is not { } top)
            {
                continue;
            }

            if (top.Y + row.Bounds.Height > 0)
            {
                return new TranscriptScrollPosition(index, top.Y);
            }
        }

        return null;
    }

    // AC-953 handover: two steps because ScrollIntoView only brings the row *into* view — from
    // below it lands at the bottom, not where it was — so an offset correction places it exactly.
    private void _RestoreScrollAnchor(TranscriptScrollPosition anchor)
    {
        if (anchor.Index < 0 || anchor.Index >= TranscriptItems.ItemCount || TranscriptScroll is not { } scroll)
        {
            return;
        }

        // Before the move, not after: nothing here is an operator gesture, so the handler would otherwise read
        // "still sticky" and the next request would follow straight back to the tail.
        Follower.StickToBottom = false;
        TranscriptItems.ScrollIntoView(anchor.Index);

        if (TranscriptItems.ContainerFromIndex(anchor.Index) is { } row
            && row.TranslatePoint(new Point(0, 0), scroll) is { } top)
        {
            scroll.Offset = scroll.Offset.WithY(Math.Max(0, scroll.Offset.Y + top.Y - anchor.Offset));
        }

        ScrollToBottomButton.IsVisible = !Follower.NewestRowIsFullyVisible();
    }

    // The host can flip Session from null to a real one after EnsureOpenedAsync's lazy start completes
    // (or, on reopen, it may already be set) — re-follow whichever transcript is live so a newly-started
    // session's replies scroll into view without the operator having to scroll manually.
    private void _OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantChatViewModel.Session) || sender is not AssistantChatViewModel vm)
        {
            return;
        }

        _AttachTranscript(vm.Session);
    }

    private void _AttachTranscript(SessionViewModel? session)
    {
        if (ReferenceEquals(_attachedSession, session))
        {
            return;
        }

        _DetachTranscript();
        if (session is null)
        {
            return;
        }

        _sessionHandler ??= _OnSessionPropertyChanged;
        session.PropertyChanged += _sessionHandler;
        _attachedSession = session;
        Follower.Watch(session);
    }

    // AC-545: scrolls Allow/Deny into view when waiting — a permission turns an existing tool row
    // pending rather than adding one, so CollectionChanged stays quiet and the transcript handler can't.
    private void _OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.HasPendingPermission)
            && sender is SessionViewModel { HasPendingPermission: true })
        {
            // A consent to act on outranks reading history: resume the follow so the Allow/Deny row shows (AC-545).
            Follower.StickToBottom = true;
            Follower.RequestFollow();
        }
    }

    private void _DetachTranscript()
    {
        if (_attachedSession is { } session)
        {
            if (_sessionHandler is { } sessionHandler)
            {
                session.PropertyChanged -= sessionHandler;
            }
        }

        _attachedSession = null;
        Follower.Watch(null);
    }

    // AC-895: session badge click focuses that session and brings the main window forward, same
    // shape as CockpitView's OnWidgetHeaderPressed (a Button would need transparent chrome to
    // match). Also handles the AC-949 Sessions-flyout rows, which share the same DataContext type.
    private void _OnSessionSegmentPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SessionPanelViewModel session } || DataContext is not AssistantChatViewModel vm)
        {
            return;
        }

        vm.SelectSessionCommand.Execute(session);

        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            WindowActivation.BringToFront(main);
        }

        // AC-949: close the pop-out so it doesn't linger over a window that just moved to the background.
        // No-op for a badge click (its flyout isn't open).
        SessionListButton.Flyout?.Hide();
    }

    // Docked, the TopLevel is MainWindow — closing it would take the whole cockpit with it. The button is hidden
    // there (Undock stands in its place), so this only ever guards a keyboard or automation route to it.
    private void _OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssistantChatViewModel { IsDocked: false })
        {
            return;
        }

        (TopLevel.GetTopLevel(this) as Window)?.Close();
    }

    // AC-1009: same docked guard as Close — a docked panel has no window of its own to minimise.
    private void _OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssistantChatViewModel { IsDocked: false })
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    // Saves the conversation as a text file, so it can be handed to somebody who was not in the room.
    // A save dialog rather than a fixed folder: this exists to be shared, and where a file lands decides whether
    // it is findable. Silent on cancel — closing a file picker is an answer, not a failure worth reporting.
    private async void _OnExportClick(object? sender, RoutedEventArgs e)
    {
        // AC-776: this row now lives inside the merged history/export Flyout — a row click does not close it on
        // its own (see PluginToolbarHost's own flyout.Hide(), same reason).
        FlyoutBase.GetAttachedFlyout(HistoryButton)?.Hide();

        if (DataContext is not AssistantChatViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the conversation",
            SuggestedFileName = $"assistant-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            DefaultExtension = "txt",
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.TranscriptAsText());
    }

    // Loads the spawn trail the moment the flyout opens, not when the view opens — most opens never touch this
    // affordance, and the trail is a file read (`AssistantChatViewModel.LoadSpawnLogCommand`) that owes nothing
    // to a window that only wants to chat.
    private void _OnSpawnLogFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is AssistantChatViewModel vm && vm.LoadSpawnLogCommand.CanExecute(null))
        {
            vm.LoadSpawnLogCommand.Execute(null);
        }
    }

    // Whether the matching KeyDown already handled this keystroke — see SessionView's own field for the full
    // reasoning; same split here.
    private bool _lastInputKeyWasHandled;

    private void _OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        _OnInputKeyDownCore(e);
        _lastInputKeyWasHandled = e.Handled;
    }

    private void _OnInputKeyDownCore(KeyEventArgs e)
    {
        // AC-740: the open picker gets first refusal on these five keys, ahead of every handler below.
        if (DataContext is AssistantChatViewModel { MentionPicker.IsOpen: true } mentionVm)
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

        // CTRL+V taken over whole (AC-630), for the same reason SessionView does: the clipboard read is async
        // while the TextBox's own paste is not, so leaving the default in place races binary content into the box.
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = _HandlePasteAsync();
            return;
        }

        // Arrow-Up on an empty box pulls the most recently queued message back for editing — the session pane's
        // gesture, on the same queue. Guarded on an empty box so it never clobbers what is being typed.
        if (e.Key == Key.Up
            && string.IsNullOrEmpty(InputBox.Text)
            && DataContext is AssistantChatViewModel recallVm
            && recallVm.RecallLastQueuedMessage())
        {
            e.Handled = true;
            return;
        }

        // AC-942: Esc interrupts the running turn, mirroring the Stop button — the mention-picker block above
        // already claimed Esc while the picker is open, so this only fires once that picker is closed.
        if (e.Key == Key.Escape)
        {
            if (DataContext is AssistantChatViewModel { Session.IsBusy: true } busyVm && busyVm.StopCommand.CanExecute(null))
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
        if (DataContext is AssistantChatViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
        }
    }

    // The view owns the clipboard read and the view model only ever sees PNG bytes — the same split SessionView
    // keeps. The vision gate stays `SessionViewModel.AddPastedImage`'s, so a provider that cannot see images says
    // so in the transcript instead of dropping the paste silently.
    private async System.Threading.Tasks.Task _HandlePasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || DataContext is not AssistantChatViewModel vm)
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
                    // No session yet (the assistant is unavailable, or its lazy start failed) means nothing to
                    // attach to — the transcript notice AddPastedImage would give needs a transcript.
                    if (vm.Session is { } session)
                    {
                        using var stream = new MemoryStream();
                        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                        session.AddPastedImage(stream.ToArray());
                    }
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
            // Clipboard unavailable (locked by another app, unsupported content): drop the paste rather than
            // crash the UI thread.
        }
    }

    // Inserts text at the caret, replacing any current selection — mirrors a normal paste.
    private void _InsertText(string text)
    {
        var start = Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd);
        var end = Math.Max(InputBox.SelectionStart, InputBox.SelectionEnd);
        var current = InputBox.Text ?? string.Empty;
        InputBox.Text = current[..start] + text + current[end..];
        InputBox.CaretIndex = start + text.Length;
        InputBox.SelectionStart = InputBox.CaretIndex;
        InputBox.SelectionEnd = InputBox.CaretIndex;
    }

    // AC-740: re-evaluates the @-mention token after a keystroke the TextBox handled itself — see SessionView's
    // own for the full reasoning.
    private void _OnInputKeyUp(object? sender, KeyEventArgs e)
    {
        if (_lastInputKeyWasHandled || DataContext is not AssistantChatViewModel vm)
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
}
