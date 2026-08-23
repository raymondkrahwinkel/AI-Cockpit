using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack.Settings;

// The plugin's settings view (opened from the gear in the plugin manager): AC-1023 §3's three-level access
// model verbatim (the warning texts are AssistantChannelAccess's own constants), the bot/app-level tokens and
// channel id, and the verbosity picker (AC-669 §1.4).
internal sealed class SlackChannelSettingsControl : UserControl, IPluginSettingsView
{
    private readonly SlackChannelSettings _settings;

    private readonly RadioButton _singleUserOption;
    private readonly RadioButton _specificUsersOption;
    private readonly RadioButton _everyoneOption;

    private readonly TextBox _singleUserId;
    private readonly TextBox _specificUserIds;
    private readonly CheckBox _specificUsersWarningAck;
    private readonly TextBox _everyoneConfirmation;

    private readonly ComboBox _verbosity;
    private readonly TextBox _botToken;
    private readonly TextBox _appLevelToken;
    private readonly TextBox _channelId;
    private readonly TextBlock _errorText;

    public SlackChannelSettingsControl(ICockpitHost host, SlackChannelSettings settings)
    {
        _settings = settings;
        var current = settings.Access;

        _singleUserOption = new RadioButton { GroupName = "audience", Content = "Only this one Slack account" };
        _specificUsersOption = new RadioButton { GroupName = "audience", Content = "Several specific Slack accounts" };
        _everyoneOption = new RadioButton { GroupName = "audience", Content = "Everyone in this channel" };

        _singleUserId = new TextBox { PlaceholderText = "Slack member id, e.g. U0123ABCDEF — profile photo → ⋮ → Copy member ID" };
        _specificUserIds = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            PlaceholderText = "One Slack member id per line — profile photo → ⋮ → Copy member ID",
        };
        var specificUsersWarningText = new TextBlock
        {
            Text = AssistantChannelAccess.MultipleUsersWarning,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.8,
        };
        _specificUsersWarningAck = new CheckBox { Content = "I understand" };

        var everyoneWarningText = new TextBlock
        {
            Text = AssistantChannelAccess.EveryoneWarning,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.8,
        };
        _everyoneConfirmation = new TextBox { PlaceholderText = AssistantChannelAccess.EveryoneConfirmationPhrase };

        switch (current?.Access.Audience)
        {
            case AssistantChannelAudience.SpecificUsers:
                _specificUsersOption.IsChecked = true;
                _specificUserIds.Text = string.Join('\n', current.Value.Access.UserIds);
                break;
            case AssistantChannelAudience.Everyone:
                _everyoneOption.IsChecked = true;
                break;
            default:
                _singleUserOption.IsChecked = true;
                _singleUserId.Text = current?.Access.UserIds.FirstOrDefault() ?? string.Empty;
                break;
        }

        var audiencePanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _singleUserOption,
                _singleUserId,
                _specificUsersOption,
                specificUsersWarningText,
                _specificUserIds,
                _specificUsersWarningAck,
                _everyoneOption,
                everyoneWarningText,
                _everyoneConfirmation,
            },
        };

        _verbosity = new ComboBox
        {
            ItemsSource = new[]
            {
                "A — the finished answer only",
                "B — everything, tool use included",
                "C — short status lines instead of full tool traffic",
            },
            SelectedIndex = (int)(current?.Verbosity ?? AssistantChannelVerbosity.FinalAnswerOnly),
        };

        _botToken = new TextBox { Text = settings.BotToken, PasswordChar = '•', PlaceholderText = "Slack bot token (xoxb-…)" };
        _appLevelToken = new TextBox { Text = settings.AppLevelToken, PasswordChar = '•', PlaceholderText = "Slack app-level token (xapp-…)" };
        _channelId = new TextBox { Text = settings.ChannelId, PlaceholderText = "Slack channel id to relay into (e.g. C0123456789)" };

        _errorText = new TextBlock { Foreground = _Brush("CockpitStatusErrorBrush"), TextWrapping = TextWrapping.Wrap, IsVisible = false };

        // AC-1032/AC-1033: the `?` beside the heading, pointing at this plugin's own setup walkthrough —
        // creating the app, Socket Mode, Interactivity, bot scopes/install, inviting the bot to the channel.
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
                    new TextBlock { Text = "Who may talk to the assistant here?", FontWeight = FontWeight.Bold },
                    audiencePanel,
                    new TextBlock { Text = "How much of the conversation to relay", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) },
                    _verbosity,
                    botConnectionHeading,
                    _botToken,
                    _appLevelToken,
                    _channelId,
                    _errorText,
                },
            },
        };
    }

    public bool TryStage(out Action? commit, out string? error)
    {
        commit = null;

        // AC-1048: caught here, before AssistantChannelAccess even sees the value — a display name or anything
        // else that is not a Slack member id is refused with what the field actually needs, not just "invalid".
        if (_singleUserOption.IsChecked == true && !string.IsNullOrWhiteSpace(_singleUserId.Text)
            && SlackUserId.Validate(_singleUserId.Text.Trim()) is { } singleUserIdError)
        {
            return _Fail(out commit, out error, singleUserIdError);
        }

        if (_specificUsersOption.IsChecked == true)
        {
            foreach (var userId in _ParseUserIds(_specificUserIds.Text))
            {
                if (SlackUserId.Validate(userId) is { } listUserIdError)
                {
                    return _Fail(out commit, out error, listUserIdError);
                }
            }
        }

        var result = _singleUserOption.IsChecked == true
            ? AssistantChannelAccess.ForSingleUser(_singleUserId.Text ?? string.Empty)
            : _specificUsersOption.IsChecked == true
                ? AssistantChannelAccess.ForUsers(_ParseUserIds(_specificUserIds.Text), _specificUsersWarningAck.IsChecked == true)
                : AssistantChannelAccess.ForEveryone(_everyoneConfirmation.Text ?? string.Empty);

        if (!result.Ok)
        {
            return _Fail(out commit, out error, result.Error!);
        }

        if (string.IsNullOrWhiteSpace(_botToken.Text))
        {
            return _Fail(out commit, out error, "A bot token is required.");
        }

        if (string.IsNullOrWhiteSpace(_appLevelToken.Text))
        {
            return _Fail(out commit, out error, "An app-level token is required for Socket Mode.");
        }

        if (string.IsNullOrWhiteSpace(_channelId.Text))
        {
            return _Fail(out commit, out error, "A Slack channel id is required.");
        }

        var access = result.Access!;
        var verbosity = (AssistantChannelVerbosity)_verbosity.SelectedIndex;
        var botToken = _botToken.Text.Trim();
        var appLevelToken = _appLevelToken.Text.Trim();
        var channelId = _channelId.Text.Trim();

        commit = () =>
        {
            _settings.SaveAccess(access, verbosity);
            _settings.BotToken = botToken;
            _settings.AppLevelToken = appLevelToken;
            _settings.ChannelId = channelId;
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
        return false;
    }

    private static IReadOnlyList<string> _ParseUserIds(string? text) =>
        [.. (text ?? string.Empty).Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // AC-334/AC-337: a plugin's own theme lookup rather than a hardcoded Brushes.X — a colour lives in
    // Theme.axaml, and each plugin keeps this tiny copy since Cockpit.Plugins.Abstractions.Theming.ThemeBrush is
    // internal SDK plumbing, not part of the plugin contract (same pattern as DiscordChannelSettingsControl).
    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
