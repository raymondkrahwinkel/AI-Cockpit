using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// The strip's notification pop-out: one panel with a row per open question, permission or finished agent.
public partial class CompanionNotificationsPanel : UserControl
{
    private readonly Action<CompanionAttentionItem> _dismiss;
    private readonly Action<CompanionAttentionItem> _open;

    // The runtime XAML loader needs a public constructor.
    public CompanionNotificationsPanel()
        : this(Program.Services!.GetRequiredService<CompanionAttentionFeed>())
    {
    }

    internal CompanionNotificationsPanel(CompanionAttentionFeed feed)
        : this(feed.Items, feed.Dismiss, feed.OpenInCockpit)
    {
    }

    internal CompanionNotificationsPanel(
        ObservableCollection<CompanionAttentionItem> items, Action<CompanionAttentionItem> dismiss, Action<CompanionAttentionItem> open)
    {
        _dismiss = dismiss;
        _open = open;
        InitializeComponent();
        Rows.ItemsSource = items;
        items.CollectionChanged += (_, _) => _Count(items);
        _Count(items);
        CloseButton.Click += (_, _) => CompanionPanelChrome.CloseOf(this)();
    }

    private void _Count(ObservableCollection<CompanionAttentionItem> items)
    {
        EmptyText.IsVisible = items.Count == 0;
        var waiting = items.Count(item => item.NeedsYou);
        HeaderText.Text = waiting == 0 ? "Notifications" : $"{waiting} need{(waiting == 1 ? "s" : string.Empty)} you";
    }

    // The first answer of a question is the filled one, as the mockup's primary pill.
    private void OnOptionLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string label } button && _ItemOf(button) is { } item)
        {
            button.Classes.Set("primary", item.Options.Count > 0 && item.Options[0] == label);
        }
    }

    private static CompanionAttentionItem? _ItemOf(object? sender) =>
        (sender as Visual)?.GetVisualAncestors().OfType<StyledElement>().Select(element => element.DataContext).OfType<CompanionAttentionItem>().FirstOrDefault();

    private async void OnAllow(object? sender, RoutedEventArgs e)
    {
        if (_ItemOf(sender) is { } item)
        {
            await item.RespondAsync(allow: true);
        }
    }

    private async void OnDeny(object? sender, RoutedEventArgs e)
    {
        if (_ItemOf(sender) is { } item)
        {
            await item.RespondAsync(allow: false);
        }
    }

    private async void OnOption(object? sender, RoutedEventArgs e)
    {
        if (_ItemOf(sender) is { } item && (sender as Control)?.DataContext is string label)
        {
            await item.AnswerAsync(label);
        }
    }

    private async void OnSendReply(object? sender, RoutedEventArgs e)
    {
        if (_ItemOf(sender) is { } item)
        {
            await item.AnswerAsync(item.Reply);
        }
    }

    private void OnReplyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSendReply(sender, e);
            e.Handled = true;
        }
    }

    private void OnDismiss(object? sender, RoutedEventArgs e)
    {
        if (_ItemOf(sender) is { } item)
        {
            _dismiss(item);
        }
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (_ItemOf(sender) is { } item)
        {
            _open(item);
        }
    }
}
