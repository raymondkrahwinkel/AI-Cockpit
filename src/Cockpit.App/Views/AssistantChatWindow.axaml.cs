using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// The pop-out chat window (AC-543 criteria 7–9, 11): a peephole onto the assistant's own standing session,
// never its owner. See `AssistantChatViewModel`'s class remarks for how each criterion is met —
// this code-behind only wires the interactions XAML bindings cannot: drag-to-move (no OS title bar), Enter-to-
// send, opening the peephole once attached, and keeping the transcript scrolled to the newest row.
public partial class AssistantChatWindow : Window
{
    // ponytail: always-follow rather than SessionView's stick-only-while-at-bottom (TranscriptScrollAnchor):
    // this window is a narrow peephole meant to be read as it comes in, not scrolled back through at length.
    // Upgrade to the same anchor-tracking SessionView uses if this window grows a "scrolled up to read
    // history" use case.
    private NotifyCollectionChangedEventHandler? _transcriptHandler;
    private PropertyChangedEventHandler? _sessionHandler;
    private SessionViewModel? _attachedSession;

    public AssistantChatWindow()
    {
        InitializeComponent();

        // Enter sends; Shift+Enter inserts a newline — the same convention as the main session composer
        // (SessionView._OnInputKeyDown). Tunnel so this pre-empts the TextBox's own Enter handling.
        InputBox.AddHandler(InputElement.KeyDownEvent, _OnInputKeyDown, RoutingStrategies.Tunnel);

        Opened += _OnOpened;
    }

    // Deliberately does not call anything on close beyond InitializeComponent's own teardown: closing this
    // window must never end the assistant's conversation (criterion 7). AssistantChatViewModel.Dispose only
    // detaches this peephole's own event subscription, never the session — see its own remarks.
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is AssistantChatViewModel vm)
        {
            vm.PropertyChanged -= _OnViewModelPropertyChanged;
            _DetachTranscript();
            vm.Dispose();
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

        Dispatcher.UIThread.Post(() => InputBox.Focus());
        Dispatcher.UIThread.Post(TranscriptScroll.ScrollToEnd);
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

        _transcriptHandler ??= (_, _) => Dispatcher.UIThread.Post(TranscriptScroll.ScrollToEnd);
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
            Dispatcher.UIThread.Post(TranscriptScroll.ScrollToEnd);
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

    // No OS title bar (WindowDecorations="None"), so the header itself is the drag handle — same idiom
    // CockpitWindowChrome uses for every other chromeless window in this app, just not reused from there since
    // that helper builds a title bar with no room for this window's read-aloud toggle.
    private void _OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Button and not ToggleButton)
        {
            BeginMoveDrag(e);
        }
    }

    private void _OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Saves the conversation as a text file, so it can be handed to somebody who was not in the room.
    // A save dialog rather than a fixed folder: this exists to be shared, and where a file lands decides whether
    // it is findable. Silent on cancel — closing a file picker is an answer, not a failure worth reporting.
    private async void _OnExportClick(object? sender, RoutedEventArgs e)
    {
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

    private void _OnInputKeyDown(object? sender, KeyEventArgs e)
    {
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

    // Copies a tool result's formatted text to the clipboard — same idiom as SessionView._OnCopyResultClick.
    private void _OnCopyResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TranscriptEntryViewModel entry }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(entry.ResultDisplayText);
        }
    }
}
