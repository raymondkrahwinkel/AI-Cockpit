using System.Collections.Specialized;
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
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// The pop-out chat window (AC-543 criteria 7–9, 11): a peephole onto the assistant's own standing session,
// never its owner. See `AssistantChatViewModel`'s class remarks for how each criterion is met —
// this code-behind only wires the interactions XAML bindings cannot: drag-to-move (no OS title bar), Enter-to-
// send, opening the peephole once attached, and keeping the transcript scrolled to the newest row.
public partial class AssistantChatWindow : Window
{
    // Stick-to-bottom, mirroring SessionView (AC-528): follow the tail only while the operator is at it.
    // _wheelTurned/_pointerHeld separate an operator scroll from the layout's own (a streaming row growing).
    private bool _stickToBottom = true;
    private bool _wheelTurned;
    private bool _pointerHeld;

    private NotifyCollectionChangedEventHandler? _transcriptHandler;
    private PropertyChangedEventHandler? _sessionHandler;
    private SessionViewModel? _attachedSession;

    private ScrollViewer? _transcriptScroll;

    // The two independent reasons this window's renderer can be paused, kept apart so lifting one never lifts the
    // other. See SessionView's own fields — same pause, same AC-883 reasoning, one window further out.
    private bool _windowMinimised;
    private bool _renderClockPaused;

    // Set by a test before this is shown; otherwise resolved from the container on open, and only on macOS — see
    // SessionView._ResolveDiagnostics for why Windows and X11 must not even subscribe.
    internal DiagnosticsBackgroundService? Diagnostics { get; set; }

    // Cockpit serves no external UI-Automation tree (see NoChildrenWindowPeer) — the assistant has its own in-app
    // voice channel, and exposing one to external UIA clients leaks the transcript (Avalonia #8240). The window
    // still returns a real root peer; only its children are hidden.
    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new NoChildrenWindowPeer(this);

    // The transcript's scroll owner. It lives inside TranscriptItems' own template since AC-774, so the
    // virtualising panel measures against the viewport rather than the infinite height an enclosing ScrollViewer
    // hands it — mirrors SessionView.axaml.cs's own TranscriptScroll exactly (AC-686). A name inside a template
    // is not a code-behind field, so it is resolved from the template instead.
    internal ScrollViewer TranscriptScroll =>
        _transcriptScroll ??= _ResolveTranscriptScroll();

    // ApplyTemplate, not just a visual-tree walk: this is first asked for from the attach that wires the scroll
    // handlers, and at that point the transcript has not been measured, so its template child does not exist yet.
    private ScrollViewer _ResolveTranscriptScroll()
    {
        TranscriptItems.ApplyTemplate();
        return TranscriptItems.GetVisualChildren().OfType<ScrollViewer>().First();
    }

    // True while this itself is moving the viewport, so ScrollIntoView's own layout pass is never
    // mistaken for a reason to re-enter (SessionView._following, AC-528).
    private bool _following;

    // The last row (SessionView._NewestVisibleIndex): this window binds to `Session.VisibleTranscript` too, so a
    // folded tool call or a Thinking row is not an item here — following one of those could never terminate,
    // having no height to bring into view.
    private int _NewestVisibleIndex() => TranscriptItems.ItemCount - 1;

    // AC-777: ScrollToEnd() jumped to Extent, which a virtualizing panel only estimates until the next arrange —
    // see ticket for the full analysis.
    private void _FollowNewest()
    {
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
            if (TranscriptItems.ContainerFromIndex(newestIndex) is null)
            {
                TranscriptItems.ScrollIntoView(newestIndex);
            }

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

    // Whether the newest visible row is on screen in full — see SessionView's own copy for why this measures the
    // row rather than asking `Extent`, which is the estimate that caused AC-777 in the first place.
    private bool _NewestRowIsFullyVisible()
    {
        var newestIndex = _NewestVisibleIndex();
        if (newestIndex < 0)
        {
            return true;
        }

        if (TranscriptItems.ContainerFromIndex(newestIndex) is not { } newest)
        {
            return false;
        }

        var bottom = newest.TranslatePoint(new Point(0, newest.Bounds.Height), TranscriptScroll);
        return bottom is not null && bottom.Value.Y <= TranscriptScroll.Viewport.Height + 1.0;
    }

    public AssistantChatWindow()
    {
        InitializeComponent();
        WindowResizeGrip.Apply(this);

        // Enter sends; Shift+Enter inserts a newline — the same convention as the main session composer
        // (SessionView._OnInputKeyDown). Tunnel so this pre-empts the TextBox's own Enter handling.
        InputBox.AddHandler(InputElement.KeyDownEvent, _OnInputKeyDown, RoutingStrategies.Tunnel);

        // AC-740: re-evaluates the @-mention token once the TextBox has applied the keystroke — same split as
        // SessionView's.
        InputBox.KeyUp += _OnInputKeyUp;

        Opened += _OnOpened;
    }

    // Deliberately does not call anything on close beyond InitializeComponent's own teardown: closing this
    // window must never end the assistant's conversation (criterion 7). AssistantChatViewModel.Dispose only
    // detaches this peephole's own event subscription, never the session — see its own remarks.
    protected override void OnClosed(EventArgs e)
    {
        TranscriptScroll.ScrollChanged -= _OnTranscriptScrollChanged;
        TranscriptScroll.RemoveHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel);
        TranscriptScroll.RemoveHandler(InputElement.PointerPressedEvent, _OnTranscriptPointerPressed);
        TranscriptScroll.RemoveHandler(InputElement.PointerReleasedEvent, _OnTranscriptPointerReleased);
        TranscriptScroll.RemoveHandler(InputElement.PointerCaptureLostEvent, _OnTranscriptPointerReleased);

        if (DataContext is AssistantChatViewModel vm)
        {
            vm.PropertyChanged -= _OnViewModelPropertyChanged;
            _DetachTranscript();
            vm.Dispose();
        }

        // The service outlives this window, so a missed unsubscribe keeps the whole closed window alive.
        if (Diagnostics is { } diagnostics)
        {
            diagnostics.RenderersShouldPauseChanged -= _OnRenderersShouldPauseChanged;
        }

        base.OnClosed(e);
    }

    private void _OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is AssistantChatViewModel vm)
        {
            vm.PropertyChanged += _OnViewModelPropertyChanged;
            _AttachTranscript(vm.Session);

            // Criterion 1: opening the chip is the "operator handling" allowed to lazily start the assistant.
            // EnsureOpenedAsync only ever attaches to (or restarts) the host's own standing session — it never
            // resets one, so this never contradicts criterion 7.
            _ = vm.EnsureOpenedAsync();
        }

        // AC-777: AppendText never touches CollectionChanged, so a growing reply needs ScrollChanged too — see
        // ticket for the full analysis.
        TranscriptScroll.ScrollChanged += _OnTranscriptScrollChanged;

        // Tunnel + handledEventsToo so a child's own scroller cannot hide the gesture; all removed in OnClosed.
        TranscriptScroll.AddHandler(InputElement.PointerWheelChangedEvent, _OnTranscriptWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        TranscriptScroll.AddHandler(InputElement.PointerPressedEvent, _OnTranscriptPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TranscriptScroll.AddHandler(InputElement.PointerReleasedEvent, _OnTranscriptPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TranscriptScroll.AddHandler(InputElement.PointerCaptureLostEvent, _OnTranscriptPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

        Dispatcher.UIThread.Post(() => InputBox.Focus());
        Dispatcher.UIThread.Post(() => { if (_stickToBottom) _FollowNewest(); });

        _windowMinimised = WindowState == WindowState.Minimized;

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

    // Same leak, same fix as SessionView (see its _ApplyRendererPause): while this window is minimised its renderer
    // is paused, so the transcript's recycled rows never get the compositor commit that removes their scene visuals
    // and pile up. Collapse the scroll owner while minimised so the panel dematerialises its rows and stops building
    // new ones; restore on un-minimise. Guarded on the resolved scroll so an early WindowState init cannot touch the
    // template before _OnOpened has built it.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && _transcriptScroll is not null)
        {
            _windowMinimised = change.GetNewValue<WindowState>() == WindowState.Minimized;
            _ApplyRendererPause();
        }
    }

    private void _OnRenderersShouldPauseChanged(object? sender, bool paused) => SetRenderClockPaused(paused);

    // Internal so a view test can drive the edge; production reaches it through the event above. Guarded on the
    // resolved scroll for the same reason OnPropertyChanged is — a signal can arrive before _OnOpened built it.
    internal void SetRenderClockPaused(bool paused)
    {
        _renderClockPaused = paused;

        if (_transcriptScroll is not null)
        {
            _ApplyRendererPause();
        }
    }

    private void _ApplyRendererPause()
    {
        var paused = _windowMinimised || _renderClockPaused;
        TranscriptScroll.IsVisible = !paused;

        if (!paused && _stickToBottom)
        {
            Dispatcher.UIThread.Post(_FollowNewest);
        }
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

        // Only an operator scroll re-derives whether we stick; content growing on its own just gets followed.
        if (byOperator)
        {
            _stickToBottom = _NewestRowIsFullyVisible()
                || TranscriptScrollAnchor.IsAtBottom(
                    TranscriptScroll.Offset.Y, TranscriptScroll.Extent.Height, TranscriptScroll.Viewport.Height);
        }
        else if (_stickToBottom)
        {
            _FollowNewest();
        }

        ScrollToBottomButton.IsVisible = !_stickToBottom;
    }

    // A wheel turn at the bottom raises no ScrollChanged to consume the flag, so expire it after this turn's
    // layout (Background sits below Layout/Render) rather than in the handler — SessionView's fix.
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

    // AC-935: a reply's citation and a replied-to row's marker both jump here. `_stickToBottom` has to come off
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

        _stickToBottom = false;
        TranscriptItems.ScrollIntoView(index);
        ScrollToBottomButton.IsVisible = true;
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

        _transcriptHandler ??= (_, _) => Dispatcher.UIThread.Post(() => { if (_stickToBottom) _FollowNewest(); });
        session.Transcript.CollectionChanged += _transcriptHandler;

        _sessionHandler ??= _OnSessionPropertyChanged;
        session.PropertyChanged += _sessionHandler;
        _attachedSession = session;
    }

    // Scrolls the Allow/Deny row into view the moment a permission starts waiting (AC-545).
    // The transcript handler above cannot do this. A permission does not arrive as a new row — it turns a tool row
    // that is already in the list into a pending one — so nothing is added, `CollectionChanged` stays quiet,
    // and the view stays exactly where it was. With the progress line and the composer below it, the buttons then
    // sit just under the fold: found only by someone who thinks to scroll, on the one control the whole consent
    // design says must be in front of the operator. Same failure as AC-543's missing Allow row, a scroll offset
    // further along.
    private void _OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.HasPendingPermission)
            && sender is SessionViewModel { HasPendingPermission: true })
        {
            // A consent to act on outranks reading history: resume the follow so the Allow/Deny row shows (AC-545).
            _stickToBottom = true;
            Dispatcher.UIThread.Post(_FollowNewest);
        }
    }

    private void _DetachTranscript()
    {
        if (_attachedSession is { } session)
        {
            if (_transcriptHandler is { } handler)
            {
                session.Transcript.CollectionChanged -= handler;
            }

            if (_sessionHandler is { } sessionHandler)
            {
                session.PropertyChanged -= sessionHandler;
            }
        }

        _attachedSession = null;
    }

    // No OS title bar (WindowResizeGrip.Apply, AC-636/AC-678), so the header is the drag handle — same idiom
    // CockpitWindowChrome uses elsewhere, just not reused since that helper's bar has no room for the
    // read-aloud toggle. WindowResizeGrip covers the edges/corners the header does not.
    private void _OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Button and not ToggleButton)
        {
            BeginMoveDrag(e);
        }
    }

    // AC-895: a session badge click focuses that session and brings the main window forward — same shape as the
    // main window's own OnWidgetHeaderPressed (CockpitView.axaml.cs), reused because a Button here would need
    // transparent chrome to keep today's look.
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
    }

    private void _OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Saves the conversation as a text file, so it can be handed to somebody who was not in the room.
    // A save dialog rather than a fixed folder: this exists to be shared, and where a file lands decides whether
    // it is findable. Silent on cancel — closing a file picker is an answer, not a failure worth reporting.
    private async void _OnExportClick(object? sender, RoutedEventArgs e)
    {
        // AC-776: this row now lives inside the merged history/export Flyout — a row click does not close it on
        // its own (see PluginToolbarHost's own flyout.Hide(), same reason).
        FlyoutBase.GetAttachedFlyout(HistoryButton)?.Hide();

        if (DataContext is not AssistantChatViewModel vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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

    // Loads the spawn trail the moment the flyout opens, not when the window opens — most window opens never
    // touch this affordance, and the trail is a file read (`AssistantChatViewModel.LoadSpawnLogCommand`)
    // that owes nothing to a window that only wants to chat.
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
