using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The identity gate a channel plugin cannot get round: exactly one linked account, with legacy wider audiences
/// denied before they are migrated.
/// </summary>
public class AssistantChannelAccessTests
{
    [Fact]
    public void TheDefaultLevel_AllowsItsOneAccountAndNobodyElse()
    {
        var access = Assert.IsType<AssistantChannelAccess>(AssistantChannelAccess.ForSingleUser("117").Access);

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
    public void SettingsRoundTrip_AndComeBackAsTheLinkedOwner()
    {
        var storage = new FakePluginStorage();
        var saved = Assert.IsType<AssistantChannelAccess>(AssistantChannelAccess.ForSingleUser("117").Access);

        AssistantChannelStorage.Save(storage, saved, AssistantChannelVerbosity.StatusLines);
        var loaded = AssistantChannelStorage.Load(storage);

        Assert.NotNull(loaded);
        Assert.Equal(AssistantChannelAudience.SingleUser, loaded.Value.Access.Audience);
        Assert.Equal(AssistantChannelVerbosity.StatusLines, loaded.Value.Verbosity);
        Assert.True(loaded.Value.Access.IsAllowed("117"));
        Assert.False(loaded.Value.Access.IsAllowed("118"));
    }

    [Fact]
    public void NothingStoredYet_IsNoChannelToOpen_RatherThanADefaultOne()
    {
        Assert.Null(AssistantChannelStorage.Load(new FakePluginStorage()));
        Assert.Null(AssistantChannelStorage.LoadBotToken(new FakePluginStorage()));
    }

    public static TheoryData<AssistantChannelAudience, string[], string?, string?, string?> MigrationCases => new()
    {
        { AssistantChannelAudience.SingleUser, ["117"], null, "117", null },
        { AssistantChannelAudience.SingleUser, ["117"], "42", "117", "Channel 42 was disconnected; the assistant now talks only in DMs with 117." },
        { AssistantChannelAudience.SpecificUsers, ["117"], "42", "117", "Channel 42 was disconnected; the assistant now talks only in DMs with 117." },
        { AssistantChannelAudience.Everyone, ["117", "118"], "42", null, "No account is linked. Previously allowed: 117, 118. Link your own account to receive messages." },
    };

    [Theory]
    [MemberData(nameof(MigrationCases))]
    public void LegacyAudience_IsMigratedToOneOwnerOrUnlinkedWithoutSilentLoss(
        AssistantChannelAudience audience,
        string[] userIds,
        string? channelId,
        string? expectedLinkedId,
        string? expectedNotice)
    {
        var storage = new FakePluginStorage();
        storage.Set("assistantChannel.audience", audience.ToString());
        storage.Set("assistantChannel.userIds", userIds);
        if (channelId is not null)
        {
            storage.Set("discordChannelId", channelId);
        }

        var before = AssistantChannelStorage.Load(storage);
        Assert.NotNull(before);
        Assert.Equal(audience == AssistantChannelAudience.SingleUser, before.Value.Access.IsAllowed("117"));

        var notice = AssistantChannelStorage.MigrateToSingleUser(storage, "discordChannelId");

        Assert.Equal(expectedNotice, notice);
        Assert.Equal(string.Empty, storage.Get<string>("discordChannelId") ?? string.Empty);
        Assert.Equal(expectedNotice, AssistantChannelStorage.LoadMigrationNotice(storage));
        Assert.Null(AssistantChannelStorage.MigrateToSingleUser(storage, "discordChannelId"));
        Assert.Equal(expectedNotice, AssistantChannelStorage.LoadMigrationNotice(storage));

        var migrated = AssistantChannelStorage.Load(storage);
        if (expectedLinkedId is null)
        {
            Assert.Null(migrated);
            return;
        }

        Assert.NotNull(migrated);
        Assert.Equal(AssistantChannelAudience.SingleUser, migrated.Value.Access.Audience);
        Assert.Equal([expectedLinkedId], migrated.Value.Access.UserIds);
        Assert.True(migrated.Value.Access.IsAllowed(expectedLinkedId));
        Assert.False(migrated.Value.Access.IsAllowed("999"));
    }

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
