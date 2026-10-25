using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugin.Slack.Settings;

namespace Cockpit.Plugin.Slack.UI;

// One linked owner is the complete authorization UI. The tokens and verbosity remain independently editable.
internal sealed class SlackChannelSettingsControl : UserControl, IPluginSettingsView
{
    private readonly SlackChannelSettings _settings;
    private readonly TextBox _userId;
    private readonly ComboBox _verbosity;
    private readonly TextBox _botToken;
    private readonly TextBox _appLevelToken;
    private readonly TextBlock _errorText;
    private readonly TextBlock _linkText;
    private readonly TextBlock _migrationNoticeText;

    public SlackChannelSettingsControl(ICockpitUiHost host, SlackChannelSettings settings)
    {
        _settings = settings;
        var current = settings.Access;

        _userId = new TextBox
        {
            Text = current?.Access.UserIds.FirstOrDefault() ?? string.Empty,
            PlaceholderText = "U0123ABCDEF",
        };
        var userIdHint = new TextBlock
        {
            Text = "Not your Slack name — the member id. " + SlackUserId.HowToFind,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.8,
        };

        _verbosity = new ComboBox
        {
            ItemsSource = new[]
            {
                "A — the finished answer only",
                "B — everything, tool use included",
                "C — short status lines instead of full tool traffic",
            },
            SelectedIndex = (int)settings.Verbosity,
        };

        _botToken = new TextBox { Text = settings.BotToken, PasswordChar = '•', PlaceholderText = "Slack bot token (xoxb-…)" };
        _appLevelToken = new TextBox { Text = settings.AppLevelToken, PasswordChar = '•', PlaceholderText = "Slack app-level token (xapp-…)" };
        _errorText = new TextBlock
        {
            Foreground = _Brush("CockpitStatusErrorBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _linkText = new TextBlock
        {
            Text = "Link your own account to receive messages.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.8,
            IsVisible = string.IsNullOrWhiteSpace(_userId.Text),
        };
        _migrationNoticeText = new TextBlock
        {
            Text = settings.MigrationNotice ?? string.Empty,
            Foreground = _Brush("CockpitStatusErrorBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = settings.MigrationNotice is not null,
        };

        var botConnectionHeading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock { Text = "Bot connection", FontWeight = FontWeight.Bold },
                host.CreateHelpHint("setup", "create-application"),
            },
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    _migrationNoticeText,
                    _linkText,
                    new TextBlock { Text = "Your Slack member id", FontWeight = FontWeight.Bold },
                    _userId,
                    userIdHint,
                    new TextBlock { Text = "How much of the conversation to relay", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) },
                    _verbosity,
                    botConnectionHeading,
                    _botToken,
                    _appLevelToken,
                    _errorText,
                },
            },
        };
    }

    private bool _IsBlank =>
        _settings.Access is null
        && string.IsNullOrWhiteSpace(_userId.Text)
        && string.IsNullOrWhiteSpace(_botToken.Text)
        && string.IsNullOrWhiteSpace(_appLevelToken.Text);

    public bool TryStage(out Action? commit, out string? error)
    {
        commit = null;

        if (_IsBlank)
        {
            _linkText.IsVisible = true;
            _errorText.IsVisible = false;
            error = null;
            return true;
        }

        var userId = _userId.Text?.Trim() ?? string.Empty;
        if (SlackUserId.Validate(userId) is { } userIdError)
        {
            return _Fail(out commit, out error, userIdError);
        }

        var result = AssistantChannelAccess.ForSingleUser(userId);
        if (result.Access is not { } access)
        {
            return _Fail(out commit, out error, result.Error ?? "A Slack member id is required.");
        }

        var verbosity = (AssistantChannelVerbosity)_verbosity.SelectedIndex;
        var botToken = _botToken.Text?.Trim() ?? string.Empty;
        var appLevelToken = _appLevelToken.Text?.Trim() ?? string.Empty;
        commit = () =>
        {
            _settings.SaveAccess(access, verbosity);
            _settings.BotToken = botToken;
            _settings.AppLevelToken = appLevelToken;
            _migrationNoticeText.IsVisible = false;
            _linkText.IsVisible = false;
        };

        error = null;
        _errorText.IsVisible = false;
        return true;
    }

    private bool _Fail(out Action? commit, out string? error, string message)
    {
        commit = null;
        error = message;
        _errorText.Text = message;
        _errorText.IsVisible = true;
        _linkText.IsVisible = string.IsNullOrWhiteSpace(_userId.Text);
        return false;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
