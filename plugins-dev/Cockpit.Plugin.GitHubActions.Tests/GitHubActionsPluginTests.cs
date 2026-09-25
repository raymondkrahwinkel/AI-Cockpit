using System.Text.Json;
using NSubstitute;
using Cockpit.Plugin.GitHubActions.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.GitHubActions.Tests;

// AC-1394: before this split, the UI parts shelled out to `gh`/`git` themselves and the backend had no behaviour
// at all — this pins the new wiring. GetRecentRunsAsync fails soft on a non-repository directory without needing
// gh or git to run, so the round trip is exercised without shelling out (same reasoning as CiWorkflowRunClientTests).
public class GitHubActionsPluginTests
{
    [Fact]
    public async Task Initialize_RegistersRecentRuns_AndAnswersOverTheChannel()
    {
        Func<JsonElement, CancellationToken, Task<JsonElement>>? handler = null;
        var channel = Substitute.For<IPluginBackendChannel>();
        channel.Handle(GitHubActionsChannel.RecentRuns, Arg.Do<Func<JsonElement, CancellationToken, Task<JsonElement>>>(h => handler = h))
            .Returns(Substitute.For<IDisposable>());
        var host = Substitute.For<ICockpitHost>();
        host.Channel.Returns(channel);

        using var plugin = new GitHubActionsPlugin();
        plugin.Initialize(host);

        var registeredHandler = handler ?? throw new InvalidOperationException("Initialize did not register a RecentRuns handler.");
        var notARepo = Path.Combine(Path.GetTempPath(), $"github-actions-plugin-test-{Guid.NewGuid():n}");
        Directory.CreateDirectory(notARepo);
        try
        {
            var payload = JsonSerializer.SerializeToElement(new GitHubActionsRunsRequest(notARepo, 5), GitHubActionsChannel.Json);

            var answer = await registeredHandler(payload, CancellationToken.None);

            Assert.Empty(answer.Deserialize<IReadOnlyList<CiRun>>(GitHubActionsChannel.Json) ?? []);
        }
        finally
        {
            Directory.Delete(notARepo, recursive: true);
        }
    }
}
