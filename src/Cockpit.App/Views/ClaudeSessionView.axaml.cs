using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class ClaudeSessionView : UserControl
{
    // Follow the newest transcript row while parked at the bottom; pause when the user scrolls up to
    // read history, resume once they scroll back down (#21). Avalonia has no built-in stick-to-bottom.
    private bool _stickToBottom = true;

    public ClaudeSessionView()
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
        Dispatcher.UIThread.Post(() => InputBox.Focus());

        TranscriptScroll.ScrollChanged += _OnTranscriptScrollChanged;
        // Land on the newest row if the panel re-attaches with an existing transcript.
        Dispatcher.UIThread.Post(() => { if (_stickToBottom) TranscriptScroll.ScrollToEnd(); });
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TranscriptScroll.ScrollChanged -= _OnTranscriptScrollChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void _OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Content grew/shrank (a new row streamed in): keep following the bottom if we were parked
        // there. Don't re-derive the stick state from a content-driven change — only a real user
        // scroll (offset moves without the extent moving) flips whether we follow.
        if (e.ExtentDelta.Y != 0)
        {
            if (_stickToBottom)
            {
                TranscriptScroll.ScrollToEnd();
            }

            return;
        }

        if (e.OffsetDelta.Y != 0)
        {
            _stickToBottom = TranscriptScrollAnchor.IsAtBottom(
                TranscriptScroll.Offset.Y, TranscriptScroll.Extent.Height, TranscriptScroll.Viewport.Height);
        }

        // Offer the jump-to-newest button only while scrolled up (i.e. not following the tail).
        ScrollToBottomButton.IsVisible = !_stickToBottom;
    }

    private void _OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        TranscriptScroll.ScrollToEnd();
        ScrollToBottomButton.IsVisible = false;
    }

    /// <summary>Copies a tool result's formatted text to the clipboard (T6) — the button sits on the row,
    /// so its DataContext is the row whose result was rendered.</summary>
    private void _OnCopyResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TranscriptEntryViewModel entry }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(entry.ResultDisplayText);
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
            && DataContext is ClaudeSessionViewModel recallVm
            && recallVm.RecallLastQueuedMessage())
        {
            e.Handled = true;
            return;
        }

        // Esc interrupts the running turn (like the claude TUI), mirroring the Stop button. Only while
        // a turn is in flight, so Esc is otherwise free to do its normal thing (clear selection, etc.).
        if (e.Key == Key.Escape)
        {
            if (DataContext is ClaudeSessionViewModel { IsBusy: true } busyVm && busyVm.StopCommand.CanExecute(null))
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
        if (DataContext is ClaudeSessionViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
        }
    }

    private static bool _IsPasteGesture(KeyEventArgs e) =>
        e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control);

    /// <summary>
    /// Handles CTRL+V ourselves: a bitmap on the clipboard becomes a PNG pending attachment on the
    /// view model; otherwise any clipboard text is inserted into the input as a normal text paste.
    /// </summary>
    private async System.Threading.Tasks.Task _HandlePasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || DataContext is not ClaudeSessionViewModel vm)
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
    /// KeyDown for the push-to-talk hotkey. <see cref="ClaudeSessionViewModel.BeginVoiceHold"/> itself
    /// guards against OS key-repeat re-triggering a capture restart while the key stays held, so this
    /// only marks the event handled when a hold actually started — an ignored press (voice off, or
    /// already holding) leaves the key free for anything else bound to it.
    /// </summary>
    private void _OnPushToTalkKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is ClaudeSessionViewModel vm && _MatchesPushToTalkKey(e.Key, vm.PushToTalkKeyName) && vm.BeginVoiceHold())
        {
            e.Handled = true;
        }
    }

    /// <summary>KeyUp for the push-to-talk hotkey: ends the hold, transcribes with cleanup, and appends the result to the input box.</summary>
    private void _OnPushToTalkKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is ClaudeSessionViewModel vm && _MatchesPushToTalkKey(e.Key, vm.PushToTalkKeyName))
        {
            e.Handled = true;
            _ = vm.EndVoiceHoldAsync(applyCleanup: true);
        }
    }

    private static bool _MatchesPushToTalkKey(Key key, string configuredKeyName) =>
        Enum.TryParse<Key>(configuredKeyName, ignoreCase: true, out var configuredKey) && key == configuredKey;
}
