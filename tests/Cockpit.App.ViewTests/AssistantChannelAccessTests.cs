using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The identity gate a channel plugin cannot get round (AC-1023 §3): one account by default, a warning to
/// acknowledge before a list, a sentence to type over before everyone. No platform anywhere in it — a user id is
/// whatever string the plugin calls one.
/// </summary>
public class AssistantChannelAccessTests
{
    [Fact]
    public void TheDefaultLevel_AllowsItsOneAccountAndNobodyElse()
    {
        var access = AssistantChannelAccess.ForSingleUser("117").Access!;

        Assert.Equal(AssistantChannelAudience.SingleUser, access.Audience);
        Assert.True(access.IsAllowed("117"));
        Assert.False(access.IsAllowed("118"));
        Assert.False(access.IsAllowed(null));
        Assert.False(access.IsAllowed(" "));
    }

    [Fact]
    public void TheDefaultLevel_NeedsAnAccount()
    {
        var result = AssistantChannelAccess.ForSingleUser("  ");

        Assert.False(result.Ok);
        Assert.Null(result.Access);
    }

    /// <summary>
    /// Criterion 3: widening to a list without the warning having been acknowledged does not happen at all.
    /// </summary>
    [Fact]
    public void WideningToAList_WithoutTheWarningAcknowledged_IsRefusedWithThatWarning()
    {
        var result = AssistantChannelAccess.ForUsers(["117", "118"], warningAcknowledged: false);

        Assert.False(result.Ok);
        Assert.Null(result.Access);
        Assert.Equal(AssistantChannelAccess.MultipleUsersWarning, result.Error);
        Assert.Contains("terms of service", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void WideningToAList_WithTheWarningAcknowledged_AllowsEveryoneNamedAndNobodyElse()
    {
        var access = AssistantChannelAccess.ForUsers(["117", "118"], warningAcknowledged: true).Access!;

        Assert.Equal(AssistantChannelAudience.SpecificUsers, access.Audience);
        Assert.True(access.IsAllowed("117"));
        Assert.True(access.IsAllowed("118"));
        Assert.False(access.IsAllowed("119"));
    }

    [Fact]
    public void AListWithNobodyOnIt_IsRefused()
    {
        var result = AssistantChannelAccess.ForUsers([" "], warningAcknowledged: true);

        Assert.False(result.Ok);
        Assert.Null(result.Access);
    }

    /// <summary>
    /// Criterion 3, the heavier half: clicking through is not enough — the sentence has to be typed over.
    /// </summary>
    [Fact]
    public void OpeningToEveryone_WithoutTheConfirmationPhrase_IsRefusedWithTheHeavierWarning()
    {
        var result = AssistantChannelAccess.ForEveryone("yes");

        Assert.False(result.Ok);
        Assert.Null(result.Access);
        Assert.Equal(AssistantChannelAccess.EveryoneWarning, result.Error);
        Assert.NotEqual(AssistantChannelAccess.MultipleUsersWarning, result.Error);
    }

    [Fact]
    public void OpeningToEveryone_WithTheConfirmationPhrase_AllowsAnyAccount()
    {
        var access = AssistantChannelAccess.ForEveryone($"  {AssistantChannelAccess.EveryoneConfirmationPhrase}  ").Access!;

        Assert.Equal(AssistantChannelAudience.Everyone, access.Audience);
        Assert.True(access.IsAllowed("117"));
        Assert.True(access.IsAllowed("someone-nobody-has-met"));

        // Still not a nameless sender: a platform that hands over no id is not "everyone", it is "unknown".
        Assert.False(access.IsAllowed(null));
    }

    // ── storage ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Criterion 4: the bot token is written as a credential, never as a plain setting.
    /// </summary>
    [Fact]
    public void TheBotToken_IsStoredAsASecret_AndNeverAsAPlainValue()
    {
        var storage = new FakePluginStorage();

        AssistantChannelStorage.SaveBotToken(storage, "bot-abc123");

        Assert.DoesNotContain(storage.Plain.Values, value => value is string text && text.Contains("bot-abc123", StringComparison.Ordinal));
        Assert.Contains("bot-abc123", storage.Secrets.Values);
        Assert.Equal("bot-abc123", AssistantChannelStorage.LoadBotToken(storage));
    }

    [Fact]
    public void SettingsRoundTrip_AndComeBackWithoutTheWideningWarningsBeingAskedAgain()
    {
        var storage = new FakePluginStorage();
        var saved = AssistantChannelAccess.ForUsers(["117", "118"], warningAcknowledged: true).Access!;

        AssistantChannelStorage.Save(storage, saved, AssistantChannelVerbosity.StatusLines);
        var loaded = AssistantChannelStorage.Load(storage);

        Assert.NotNull(loaded);
        Assert.Equal(AssistantChannelAudience.SpecificUsers, loaded.Value.Access.Audience);
        Assert.Equal(AssistantChannelVerbosity.StatusLines, loaded.Value.Verbosity);
        Assert.True(loaded.Value.Access.IsAllowed("118"));
        Assert.False(loaded.Value.Access.IsAllowed("119"));
    }

    [Fact]
    public void NothingStoredYet_IsNoChannelToOpen_RatherThanADefaultOne()
    {
        Assert.Null(AssistantChannelStorage.Load(new FakePluginStorage()));
        Assert.Null(AssistantChannelStorage.LoadBotToken(new FakePluginStorage()));
    }

    /// <summary>
    /// A named level whose names went missing must not fall through to letting everyone in.
    /// </summary>
    [Fact]
    public void ANamedLevelWithNoNamesLeft_ReadsAsNotConfigured()
    {
        var storage = new FakePluginStorage();
        storage.Set("assistantChannel.audience", nameof(AssistantChannelAudience.SpecificUsers));
        storage.Set("assistantChannel.userIds", Array.Empty<string>());

        Assert.Null(AssistantChannelStorage.Load(storage));
    }

    // Two buckets on purpose: what a test can only tell apart by which call wrote it.
    private sealed class FakePluginStorage : IPluginStorage
    {
        public Dictionary<string, object?> Plain { get; } = [];

        public Dictionary<string, string> Secrets { get; } = [];

        public T? Get<T>(string key) => Plain.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => Plain[key] = value;

        public void SetSecret(string key, string value) => Secrets[key] = value;

        public string? GetSecret(string key) => Secrets.GetValueOrDefault(key);
    }
}
