using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.Controls;

// AC-722: one transcript row, shared by SessionView and AssistantChatView — see the XAML header comment for why.
public partial class TranscriptRowView : UserControl
{
    // The handful of numbers the assistant window's narrower, avatar-less frame needs — see Theme.axaml's
    // ".compact" selectors.
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<TranscriptRowView, bool>(nameof(Compact));

    // AC-935: reply is Assistant-chat-only for now — SessionView never sets this, so its panes render no reply
    // affordance at all. Default false rather than gating on the host type, so turning it on for sessions later
    // is one attribute here instead of a code change.
    public static readonly StyledProperty<bool> ReplyEnabledProperty =
        AvaloniaProperty.Register<TranscriptRowView, bool>(nameof(ReplyEnabled));

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public bool ReplyEnabled
    {
        get => GetValue(ReplyEnabledProperty);
        set => SetValue(ReplyEnabledProperty, value);
    }

    public TranscriptRowView()
    {
        InitializeComponent();
#if DEBUG
        Cockpit.App.Diagnostics.LeakTracker.Register(this);
#endif
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Copies a tool result's formatted text to the clipboard (T6).
    private void _OnCopyResultClick(object? sender, RoutedEventArgs e) => _CopyRowText(sender, entry => entry.ResultDisplayText);

    // Copies an assistant reply's markdown source to the clipboard — the per-reply hover action. On a user row
    // this includes the image chip's own label (AC-778), matching what the row used to have baked into `Text`.
    private void _OnCopyMessageClick(object? sender, RoutedEventArgs e) => _CopyRowText(sender, entry => entry.TextWithImageSuffix);

    // Both copy buttons sit on this row, so the sender's DataContext is the row's own view model.
    private void _CopyRowText(object? sender, Func<TranscriptEntryViewModel, string> select)
    {
        if (sender is Control { DataContext: TranscriptEntryViewModel entry }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(select(entry));
        }
    }

    // AC-778: opens the mini-gallery for this row's own images, starting at the first one.
    private void _OnImagesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TranscriptEntryViewModel { Images: { Count: > 0 } images } }
            && TopLevel.GetTopLevel(this) is Window owner)
        {
            ImagePreviewWindow.Show(images, 0, owner);
        }
    }

    // AC-935: the citation above a reply — jumps back to the message it answered.
    private void _OnJumpToReplyTargetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TranscriptEntryViewModel { ReplyTo: { } target } }
            && this.FindAncestorOfType<AssistantChatView>() is { } chat)
        {
            chat.ScrollToMessage(target);
        }
    }

    // AC-935: the "answered" marker on a replied-to row — jumps forward to its most recent reply.
    private void _OnJumpToLatestReplyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TranscriptEntryViewModel { LatestReply: { } reply } }
            && this.FindAncestorOfType<AssistantChatView>() is { } chat)
        {
            chat.ScrollToMessage(reply);
        }
    }
}
