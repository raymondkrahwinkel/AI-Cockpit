using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Core.Tests.Assistant;

// AC-1379 acceptance 4: the assistant's host and its chat-channel seam are implemented outside the app. What the app
// may still carry is named in full: two scene fakes, and the wrapper that makes the host's UI-thread hops for a channel.
public class AssistantHostLayerTests
{
    private static readonly HashSet<string> _AllowedInTheApp =
    [
        "Cockpit.App.Screenshotter+_FakeAssistantSessionHost",
        "Cockpit.App.ViewModels.CockpitViewModel+_ChatLeakSimHost",
        "Cockpit.App.Services.UiThreadAssistantSessionHost",
    ];

    [Theory]
    [InlineData(typeof(IAssistantSessionHost))]
    [InlineData(typeof(IAssistantChannelGateway))]
    public void TheApp_ImplementsNeitherTheHostNorTheChannelGateway(Type seam)
    {
        var implementations = typeof(CockpitViewModel).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && seam.IsAssignableFrom(type))
            .Select(type => type.FullName)
            .Where(name => !_AllowedInTheApp.Contains(name ?? string.Empty));

        Assert.Empty(implementations);
    }
}
