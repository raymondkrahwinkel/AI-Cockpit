using Avalonia.Controls;
using Cockpit.Plugin.Discord.Settings;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord.Tests;

// AC-1084: the line between "installed but never set up" and "invalid". A fresh Discord refused every Apply — its
// default audience is one-user with an empty id, which `AssistantChannelAccess` rejects — so Options would not
// close until it was configured or removed. Untouched now stages as nothing to save; touched still validates.
[Collection("avalonia")]
public class DiscordSettingsControlStagingTests
{
    [Fact]
    public void AFreshInstall_StagesWithNothingToSave_AndWritesNothing()
    {
        var storage = new FakePluginStorage();
        var settings = new DiscordChannelSettings(storage);
        var view = new DiscordChannelSettingsControl(new FakeCockpitHost(), settings);

        var staged = view.TryStage(out var commit, out var error);

        Assert.True(staged);
        Assert.Null(error);
        // No commit at all, not an empty one: the host reads that as "nothing to save" and never writes.
        Assert.Null(commit);
        Assert.Null(settings.Access);
        Assert.True(string.IsNullOrEmpty(settings.BotToken));
        Assert.Equal(0ul, settings.ChannelId);
    }

    // The other half, and the one that keeps this from swallowing real mistakes: the moment the operator puts
    // anything in, the checks that were there before apply again.
    [Fact]
    public void AHalfFilledInstall_IsStillRefused_WithTheFieldItIsMissing()
    {
        var settings = new DiscordChannelSettings(new FakePluginStorage());
        var view = new DiscordChannelSettingsControl(new FakeCockpitHost(), settings);

        // A user id typed and nothing else — exactly the state a fresh install is one keystroke away from.
        _SingleUserId(view).Text = "123456789012345678";

        var staged = view.TryStage(out var commit, out var error);

        Assert.False(staged);
        Assert.Null(commit);
        Assert.Equal("A bot token is required.", error);
    }

    // Fully filled in stages a real write, and still writes nothing until the host runs it — the staged contract
    // (AC-1003) that the blank branch above must not have quietly broken.
    [Fact]
    public void AFilledInInstall_StagesARealCommit_AndOnlyTheCommitWrites()
    {
        var settings = new DiscordChannelSettings(new FakePluginStorage());
        var view = new DiscordChannelSettingsControl(new FakeCockpitHost(), settings);

        _SingleUserId(view).Text = "123456789012345678";
        _BotToken(view).Text = "a-bot-token";
        _ChannelId(view).Text = "987654321098765432";

        Assert.True(view.TryStage(out var commit, out var error));
        Assert.Null(error);
        Assert.NotNull(commit);
        Assert.Null(settings.Access);

        commit!();

        Assert.Equal("a-bot-token", settings.BotToken);
        Assert.Equal(987654321098765432ul, settings.ChannelId);
        Assert.Equal(AssistantChannelAudience.SingleUser, settings.Access!.Value.Access.Audience);
    }

    // A channel that was configured once and then emptied is not a fresh install: there is something stored to
    // undo, so clearing the fields must be refused rather than read as "never set up" and silently ignored.
    [Fact]
    public void AConfiguredChannelWithItsFieldsCleared_IsRefused_NotTreatedAsNeverSetUp()
    {
        var storage = new FakePluginStorage();
        var settings = new DiscordChannelSettings(storage);
        settings.SaveAccess(
            AssistantChannelAccess.ForSingleUser("123456789012345678").Access!,
            AssistantChannelVerbosity.FinalAnswerOnly);
        settings.BotToken = "a-bot-token";
        settings.ChannelId = 987654321098765432;

        var view = new DiscordChannelSettingsControl(new FakeCockpitHost(), settings);
        _SingleUserId(view).Text = string.Empty;
        _BotToken(view).Text = string.Empty;
        _ChannelId(view).Text = string.Empty;

        var staged = view.TryStage(out var commit, out var error);

        Assert.False(staged);
        Assert.Null(commit);
        Assert.NotNull(error);
    }

    private static TextBox _SingleUserId(DiscordChannelSettingsControl view) => _TextBox(view, 0);

    private static TextBox _BotToken(DiscordChannelSettingsControl view) => _TextBox(view, 3);

    private static TextBox _ChannelId(DiscordChannelSettingsControl view) => _TextBox(view, 4);

    // By position in the panel the view builds — the control exposes none of its boxes. Walked over its own
    // children rather than the visual tree, which stays empty until something lays the view out. Order: single-user
    // id, specific-user ids, everyone confirmation, bot token, channel id.
    private static TextBox _TextBox(DiscordChannelSettingsControl view, int index) =>
        _Descendants(view).OfType<TextBox>().ElementAt(index);

    private static IEnumerable<Control> _Descendants(Control control)
    {
        var children = control switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            ContentControl { Content: Control child } => [child],
            Decorator { Child: { } child } => [child],
            _ => [],
        };

        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in _Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
