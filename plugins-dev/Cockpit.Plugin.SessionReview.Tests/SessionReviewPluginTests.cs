using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using NSubstitute;

namespace Cockpit.Plugin.SessionReview.Tests;

// AC-961: an intent from another plugin carries the pane and its directory as strings; AC-1395's backend
// forwards them to the UI part over the channel. Reads the payload as raw JSON, since the shared record
// compiles into both assemblies this project references, ambiguous to a using here.
public class SessionReviewPluginTests
{
    [Fact]
    public async Task TheOpenIntent_PublishesTheNamedPaneAndDirectoryOnTheChannel()
    {
        var channel = Substitute.For<IPluginBackendChannel>();
        var host = Substitute.For<ICockpitHost>();
        host.Channel.Returns(channel);

        Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>? handler = null;
        host.When(h => h.RegisterIntentHandler(
                SessionReviewPlugin.OpenIntentAction,
                Arg.Any<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>()))
            .Do(call => handler = call.Arg<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>());

        var published = default(JsonElement);
        channel.When(c => c.Publish("open", Arg.Any<JsonElement>()))
            .Do(call => published = call.Arg<JsonElement>());

        using var plugin = new SessionReviewPlugin();
        plugin.Initialize(host);
        Assert.NotNull(handler);

        await handler(new PluginIntent(
            "git-status",
            "session-review",
            SessionReviewPlugin.OpenIntentAction,
            new Dictionary<string, string> { ["paneId"] = "pane-9", ["workingDirectory"] = "/repo" }));

        Assert.Equal("pane-9", published.GetProperty("paneId").GetString());
        Assert.Equal("/repo", published.GetProperty("workingDirectory").GetString());
    }
}
