using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Cockpit.Plugin.Diagram.Collab;

// AC-910: the operator's free-text box for "Ask the agent…", one copy instead of the two identical ones
// AC-849's pin flyout used to carry. An empty box submits nothing — there is no such thing as an empty ask.
internal static class AskFlyout
{
    public static void Show(Control anchor, string placeholder, Action<string> onAsk)
    {
        var question = new TextBox { Width = 260, PlaceholderText = placeholder };
        var confirm = new Button { Content = "Ask", Classes = { "Compact" }, HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(12), Children = { question, confirm } },
        };

        void Submit()
        {
            var text = question.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            flyout.Hide();
            onAsk(text);
        }

        confirm.Click += (_, _) => Submit();
        question.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                Submit();
            }
        };

        flyout.ShowAt(anchor);
        question.Focus();
    }
}
