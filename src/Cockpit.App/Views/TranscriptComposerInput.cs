using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-1165: the input half of the SessionView/AssistantChatView twin, once — key handling, paste, mentions.
// Each callback below is the one place the two views' DataContext types genuinely differ (SessionViewModel
// directly vs. AssistantChatViewModel, which wraps a SessionViewModel of its own).
internal sealed class TranscriptComposerInput
{
    private readonly TextBox _inputBox;
    private readonly Func<Task<Bitmap?>> _tryGetPastedBitmap;
    private readonly Func<Task<string?>> _tryGetPastedText;
    private readonly Func<bool> _hasComposer;
    private readonly Func<MentionPickerViewModel?> _mentionPicker;
    private readonly Func<bool> _recallLastQueuedMessage;
    private readonly Func<ICommand?> _resolveStopIfBusy;
    private readonly Func<ICommand?> _sendCommand;
    private readonly Func<Action<byte[]>?> _resolvePastedImageSink;

    // Whether the matching KeyDown already handled this keystroke — a tunnelled KeyDown's own `e.Handled`
    // cannot be read back from the later KeyUp, so this is how OnKeyUp tells caret-driven typing (unhandled,
    // falls through to the TextBox's default editing) apart from a programmatic text mutation.
    private bool _lastInputKeyWasHandled;

    internal TranscriptComposerInput(
        TextBox inputBox,
        Func<Task<Bitmap?>> tryGetPastedBitmap,
        Func<Task<string?>> tryGetPastedText,
        Func<bool> hasComposer,
        Func<MentionPickerViewModel?> mentionPicker,
        Func<bool> recallLastQueuedMessage,
        Func<ICommand?> resolveStopIfBusy,
        Func<ICommand?> sendCommand,
        Func<Action<byte[]>?> resolvePastedImageSink)
    {
        _inputBox = inputBox;
        _tryGetPastedBitmap = tryGetPastedBitmap;
        _tryGetPastedText = tryGetPastedText;
        _hasComposer = hasComposer;
        _mentionPicker = mentionPicker;
        _recallLastQueuedMessage = recallLastQueuedMessage;
        _resolveStopIfBusy = resolveStopIfBusy;
        _sendCommand = sendCommand;
        _resolvePastedImageSink = resolvePastedImageSink;
    }

    internal void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _OnKeyDownCore(e);
        _lastInputKeyWasHandled = e.Handled;
    }

    private void _OnKeyDownCore(KeyEventArgs e)
    {
        // AC-740: the open picker gets first refusal on these five keys, ahead of every handler below — Up
        // otherwise recalls, Escape otherwise stops the turn, and Enter otherwise sends.
        if (_mentionPicker() is { IsOpen: true } picker)
        {
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
        if (e.Key == Key.Up && string.IsNullOrEmpty(_inputBox.Text) && _recallLastQueuedMessage())
        {
            e.Handled = true;
            return;
        }

        // Esc interrupts the running turn (like the claude TUI), mirroring the Stop button. Only while
        // a turn is in flight, so Esc is otherwise free to do its normal thing (clear selection, etc.).
        if (e.Key == Key.Escape)
        {
            if (_resolveStopIfBusy() is { } stop && stop.CanExecute(null))
            {
                stop.Execute(null);
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
        if (_sendCommand() is { } send && send.CanExecute(null))
        {
            send.Execute(null);
        }
    }

    private static bool _IsPasteGesture(KeyEventArgs e) =>
        e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control);

    // Handles CTRL+V: a clipboard bitmap becomes a PNG pending attachment, otherwise text is inserted
    // normally. The image sink can resolve to null (AssistantChatView, no session yet to attach to) —
    // that drops the image silently rather than crash or fall through to a text insert.
    private async Task _HandlePasteAsync()
    {
        if (!_hasComposer())
        {
            return;
        }

        try
        {
            var bitmap = await _tryGetPastedBitmap();
            if (bitmap is not null)
            {
                using (bitmap)
                {
                    if (_resolvePastedImageSink() is { } addPastedImage)
                    {
                        using var stream = new MemoryStream();
                        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                        addPastedImage(stream.ToArray());
                    }
                }

                return;
            }

            var text = await _tryGetPastedText();
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
        var start = Math.Min(_inputBox.SelectionStart, _inputBox.SelectionEnd);
        var end = Math.Max(_inputBox.SelectionStart, _inputBox.SelectionEnd);
        var current = _inputBox.Text ?? string.Empty;
        _inputBox.Text = current[..start] + text + current[end..];
        _inputBox.CaretIndex = start + text.Length;
        _inputBox.SelectionStart = _inputBox.CaretIndex;
        _inputBox.SelectionEnd = _inputBox.CaretIndex;
    }

    // AC-740: re-evaluates the @-mention token once the TextBox has applied the keystroke (character typed,
    // backspace, caret moved). `_lastInputKeyWasHandled` tells this apart from a programmatic mutation (voice,
    // recall, a pasted block), all of which raise no KeyUp here except Ctrl+V, already marked handled above.
    internal void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_lastInputKeyWasHandled || _mentionPicker() is not { } picker)
        {
            return;
        }

        picker.OnTextChanged(_inputBox.Text ?? string.Empty, _inputBox.CaretIndex);
    }

    // Splices an accepted mention into the text: replaces [TokenStart..caret] with '@' + path + a trailing space.
    private void _InsertMention(MentionAcceptance acceptance)
    {
        var current = _inputBox.Text ?? string.Empty;
        var end = Math.Clamp(_inputBox.CaretIndex, 0, current.Length);
        var start = Math.Clamp(acceptance.TokenStart, 0, end);
        var replacement = $"@{acceptance.Path} ";
        _inputBox.Text = current[..start] + replacement + current[end..];
        _inputBox.CaretIndex = start + replacement.Length;
        _inputBox.SelectionStart = _inputBox.CaretIndex;
        _inputBox.SelectionEnd = _inputBox.CaretIndex;
    }
}
