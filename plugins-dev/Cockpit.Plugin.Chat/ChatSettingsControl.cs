using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Chat;

/// <summary>
/// The channels a flow may post to. A row is a name, a service and an incoming webhook — and the webhook is masked,
/// because anyone who reads it over your shoulder can post as you for as long as it lives.
/// </summary>
internal sealed class ChatSettingsControl : UserControl, IPluginSettingsView
{
    private readonly ChatSettings _settings;
    private readonly StackPanel _rows = new() { Spacing = 6 };

    public ChatSettingsControl(ChatSettings settings)
    {
        _settings = settings;

        var add = new Button { Content = "+ Add channel" };
        add.Click += (_, _) => _AddRow(new ChatChannel(string.Empty, ChatService.Slack, string.Empty));

        foreach (var channel in settings.Channels)
        {
            _AddRow(channel);
        }

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Channels", FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "A name a flow can use, and the incoming webhook to post to. Slack: create one under \"Incoming Webhooks\" in your app's settings. Discord: Channel settings → Integrations → Webhooks.",
                        FontSize = 11,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    _rows,
                    add,
                    new TextBlock
                    {
                        Text = "A webhook is a credential: anyone who has it can post as you. It stays here rather than in a flow, so a flow can be exported or backed up without it.",
                        FontSize = 11,
                        Opacity = 0.45,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
    }

    public bool Save()
    {
        _settings.Channels = _rows.Children
            .OfType<ChannelRow>()
            .Select(row => row.ToChannel())
            .Where(channel => channel.IsComplete)
            .ToList();

        return true;
    }

    private void _AddRow(ChatChannel channel)
    {
        var row = new ChannelRow(channel);
        row.RemoveRequested += (_, _) => _rows.Children.Remove(row);
        _rows.Children.Add(row);
    }

    private sealed class ChannelRow : UserControl
    {
        private readonly TextBox _name;
        private readonly ComboBox _service;
        private readonly TextBox _webhook;

        public ChannelRow(ChatChannel channel)
        {
            _name = new TextBox { Text = channel.Name, Watermark = "releases", Width = 130 };

            _service = new ComboBox
            {
                Width = 110,
                ItemsSource = new[] { nameof(ChatService.Slack), nameof(ChatService.Discord) },
                SelectedIndex = channel.Service == ChatService.Discord ? 1 : 0,
            };

            _webhook = new TextBox
            {
                Text = channel.WebhookUrl,
                Watermark = "https://hooks.slack.com/services/…",
                PasswordChar = '•',
                MinWidth = 220,
            };

            var remove = new Button { Content = "✕", Classes = { "Subtle" } };
            ToolTip.SetTip(remove, "Remove this channel");
            remove.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);

            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { _name, _service, _webhook, remove },
            };
        }

        public event EventHandler? RemoveRequested;

        public ChatChannel ToChannel() => new(
            _name.Text?.Trim() ?? string.Empty,
            _service.SelectedIndex == 1 ? ChatService.Discord : ChatService.Slack,
            _webhook.Text?.Trim() ?? string.Empty);
    }
}
