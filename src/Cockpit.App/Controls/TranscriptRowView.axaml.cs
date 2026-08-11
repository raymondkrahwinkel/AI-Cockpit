using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-722: one transcript row, shared by SessionView and AssistantChatWindow — see the XAML header comment for why.
public partial class TranscriptRowView : UserControl
{
    public static readonly StyledProperty<SessionViewModel?> SessionProperty =
        AvaloniaProperty.Register<TranscriptRowView, SessionViewModel?>(nameof(Session));

    // The handful of numbers the assistant window's narrower, avatar-less frame needs — see Theme.axaml's
    // ".compact" selectors.
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<TranscriptRowView, bool>(nameof(Compact));

    public SessionViewModel? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public TranscriptRowView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Copies a tool result's formatted text to the clipboard (T6).
    private void _OnCopyResultClick(object? sender, RoutedEventArgs e) => _CopyRowText(sender, entry => entry.ResultDisplayText);

    // Copies an assistant reply's markdown source to the clipboard — the per-reply hover action.
    private void _OnCopyMessageClick(object? sender, RoutedEventArgs e) => _CopyRowText(sender, entry => entry.Text);

    // Both copy buttons sit on this row, so the sender's DataContext is the row's own view model.
    private void _CopyRowText(object? sender, Func<TranscriptEntryViewModel, string> select)
    {
        if (sender is Control { DataContext: TranscriptEntryViewModel entry }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(select(entry));
        }
    }
}
