using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Controls;

public sealed class TermsNoticeView : UserControl
{
    public static readonly StyledProperty<TermsNotice?> NoticeProperty =
        AvaloniaProperty.Register<TermsNoticeView, TermsNotice?>(nameof(Notice));

    public TermsNoticeView()
    {
        IsVisible = false;
    }

    public TermsNotice? Notice
    {
        get => GetValue(NoticeProperty);
        set => SetValue(NoticeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NoticeProperty)
        {
            Content = Notice is { } notice ? _Body(notice) : null;
            IsVisible = Notice is not null;
        }
    }

    private static StackPanel _Body(TermsNotice notice)
    {
        var body = new StackPanel { Spacing = 4 };
        foreach (var text in new[] { notice.Text, notice.SubscriptionText, notice.ApiKeyText })
        {
            if (text is { Length: > 0 })
            {
                var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
                line.Classes.Add("optionsHint");
                body.Children.Add(line);
            }
        }

        body.Children.Add(_Link(notice.Url, "Provider terms"));
        if (notice.ApiTermsUrl is { } apiUrl)
        {
            body.Children.Add(_Link(apiUrl, "API / endpoint terms"));
        }

        return body;
    }

    private static Button _Link(Uri uri, string label)
    {
        var link = new Button { Content = label + " (" + uri.Host + ")" };
        link.Classes.Add("Subtle");
        link.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(link)?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(uri);
            }
        };
        return link;
    }
}
