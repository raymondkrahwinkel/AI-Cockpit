using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord.Settings;

// The plugin's settings view (opened from the gear in the plugin manager): AC-1023 §3's three-level access
// model verbatim (the warning texts are AssistantChannelAccess's own constants), the bot token/channel id, and
// the verbosity picker (AC-669 §1.4). Direction only from the not-yet-reviewed Depot AC-1027 mockup.
internal sealed class DiscordChannelSettingsControl : UserControl, IPluginSettingsView
{
    private readonly DiscordChannelSettings _settings;

    private readonly RadioButton _singleUserOption;
    private readonly RadioButton _specificUsersOption;
    private readonly RadioButton _everyoneOption;

    private readonly TextBox _singleUserId;
    private readonly TextBox _specificUserIds;
    private readonly CheckBox _specificUsersWarningAck;
    private readonly TextBox _everyoneConfirmation;

    private readonly ComboBox _verbosity;
    private readonly TextBox _botToken;
    private readonly TextBox _channelId;
    private readonly TextBlock _errorText;
    private readonly TextBlock _notConfiguredText;

    public DiscordChannelSettingsControl(ICockpitHost host, DiscordChannelSettings settings)
    {
        _settings = settings;
        var current = settings.Access;

        _singleUserOption = new RadioButton { GroupName = "audience", Content = "Only this one Discord account" };
        _specificUsersOption = new RadioButton { GroupName = "audience", Content = "Several specific Discord accounts" };
        _everyoneOption = new RadioButton { GroupName = "audience", Content = "Everyone in this channel" };

        _singleUserId = new TextBox { PlaceholderText = "123456789012345678" };
        var singleUserIdHint = new TextBlock
        {
            Text = "Not your Discord name — the user id. " + DiscordUserId.HowToFind,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.8,
        };
        _specificUserIds = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            PlaceholderText = "One Discord user id per line",
        };
        var specificUserIdsHint = new TextBlock
        {
            Text = "Not Discord names — user ids. " + DiscordUserId.HowToFind,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.8,
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
                singleUserIdHint,
                _specificUsersOption,
                specificUsersWarningText,
                _specificUserIds,
                specificUserIdsHint,
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

        _botToken = new TextBox { Text = settings.BotToken, PasswordChar = '•', PlaceholderText = "Discord bot token" };
        _channelId = new TextBox
        {
            Text = settings.ChannelId == 0 ? string.Empty : settings.ChannelId.ToString(),
            PlaceholderText = "Discord text channel id to relay into",
        };

        _errorText = new TextBlock { Foreground = _Brush("CockpitStatusErrorBrush"), TextWrapping = TextWrapping.Wrap, IsVisible = false };

        // AC-1084: what a never-configured install says for itself, on its own page. Not an error colour — it
        // reports a state the operator chose by installing and has not finished, not something that went wrong.
        _notConfiguredText = new TextBlock
        {
            Text = "Not set up yet. Nothing is relayed to Discord until a bot token and a channel id are saved here.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.8,
            IsVisible = false,
        };

        // AC-1032/AC-1033: the `?` beside the heading, pointing at this plugin's own setup walkthrough —
        // creating the application, the Message Content Intent, inviting the bot, finding the channel id.
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
                    _notConfiguredText,
                    new TextBlock { Text = "Who may talk to the assistant here?", FontWeight = FontWeight.Bold },
                    audiencePanel,
                    new TextBlock { Text = "How much of the conversation to relay", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) },
                    _verbosity,
                    botConnectionHeading,
                    _botToken,
                    _channelId,
                    _errorText,
                },
            },
        };

        _notConfiguredText.IsVisible = _IsBlank;
    }

    // AC-1084: nothing entered and nothing stored — the line `ClusterRowControl.IsBlank` draws for a Kubernetes
    // row. Only the fields that make the channel work, so a stray verbosity pick cannot turn a fresh install back
    // into a refusing one; the audience radio does count, so picking "everyone" and typing nothing still refuses.
    private bool _IsBlank =>
        _settings.Access is null
        && _singleUserOption.IsChecked == true
        && string.IsNullOrWhiteSpace(_singleUserId.Text)
        && string.IsNullOrWhiteSpace(_specificUserIds.Text)
        && string.IsNullOrWhiteSpace(_everyoneConfirmation.Text)
        && string.IsNullOrWhiteSpace(_botToken.Text)
        && string.IsNullOrWhiteSpace(_channelId.Text);

    public bool TryStage(out Action? commit, out string? error)
    {
        commit = null;

        // AC-1084: a plugin installed but never set up is not an invalid one. Staging with no commit is how the
        // host reads "nothing to save" (PluginSettingsStaging), so a fresh install neither writes nor blocks the
        // operator's Apply — while a half-filled one drops through to the checks below exactly as before.
        if (_IsBlank)
        {
            _notConfiguredText.IsVisible = true;
            _errorText.IsVisible = false;
            error = null;
            return true;
        }

        _notConfiguredText.IsVisible = false;

        // AC-1048: caught here, before AssistantChannelAccess even sees the value — a display name or anything
        // else that is not a Discord snowflake is refused with what the field actually needs, not just "invalid".
        if (_singleUserOption.IsChecked == true && !string.IsNullOrWhiteSpace(_singleUserId.Text)
            && DiscordUserId.Validate(_singleUserId.Text.Trim()) is { } singleUserIdError)
        {
            return _Fail(out commit, out error, singleUserIdError);
        }

        if (_specificUsersOption.IsChecked == true)
        {
            foreach (var userId in _ParseUserIds(_specificUserIds.Text))
            {
                if (DiscordUserId.Validate(userId) is { } listUserIdError)
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

        if (!ulong.TryParse(_channelId.Text, out var channelId) || channelId == 0)
        {
            return _Fail(out commit, out error, "A valid Discord channel id is required.");
        }

        var access = result.Access!;
        var verbosity = (AssistantChannelVerbosity)_verbosity.SelectedIndex;
        var token = _botToken.Text.Trim();

        commit = () =>
        {
            _settings.SaveAccess(access, verbosity);
            _settings.BotToken = token;
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
    // internal SDK plumbing, not part of the plugin contract (same pattern as GitStatusHeaderControl._Brush).
    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
