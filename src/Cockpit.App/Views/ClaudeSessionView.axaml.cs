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
}
