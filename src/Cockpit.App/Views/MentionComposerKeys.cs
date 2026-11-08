using Avalonia.Controls;
using Avalonia.Input;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// The picker keys and the splice of an accepted mention, shared by every composer that hosts the @-list.
internal static class MentionComposerKeys
{
    // The open picker gets first refusal on these keys; true when it took the key.
    public static bool Handle(MentionPickerViewModel? picker, TextBox box, KeyEventArgs e)
    {
        if (picker is not { IsOpen: true })
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Up:
                picker.Move(-1);
                break;
            case Key.Down:
                picker.Move(1);
                break;
            case Key.Tab:
            case Key.Enter:
                if (picker.Accept() is { } acceptance)
                {
                    Insert(box, acceptance);
                }

                break;
            case Key.Escape:
                picker.Dismiss();
                break;
            default:
                return false;
        }

        e.Handled = true;
        return true;
    }

    // Replaces [TokenStart..caret] with '@' + InsertText + a trailing space.
    public static void Insert(TextBox box, MentionAcceptance acceptance)
    {
        var current = box.Text ?? string.Empty;
        var end = Math.Clamp(box.CaretIndex, 0, current.Length);
        var start = Math.Clamp(acceptance.TokenStart, 0, end);
        var replacement = $"@{acceptance.Item.InsertText} ";
        box.Text = current[..start] + replacement + current[end..];
        box.CaretIndex = start + replacement.Length;
        box.SelectionStart = box.CaretIndex;
        box.SelectionEnd = box.CaretIndex;
    }
}
