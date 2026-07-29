using System.Text.RegularExpressions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The one channel a session driver publishes on (AC-308). Every driver had grown its own unbounded one, which is the
/// one place a child process could grow host memory with nothing saying so. What matters here is not only that there
/// is a ceiling, but that reaching it is <em>reported</em> — a silently dropped event is a hole in the transcript
/// nobody can see, which is worse than the memory it saved.
/// </summary>
public partial class PluginSessionEventPublisherTests
{
    private static PluginAssistantTextDelta Delta(string text = "x") =>
        new() { SessionId = "session-1", BlockIndex = 0, Text = text };

    [Fact]
    public void Publish_UpToCapacity_Succeeds()
    {
        var publisher = new PluginSessionEventPublisher();

        for (var index = 0; index < PluginSessionEventPublisher.Capacity; index++)
        {
            Assert.True(publisher.Publish(Delta()), $"the first {PluginSessionEventPublisher.Capacity} events fit");
        }

        Assert.Equal(0, publisher.PendingDroppedCount);
    }

    [Fact]
    public void Publish_PastCapacity_CountsTheLossInsteadOfHidingIt()
    {
        var publisher = new PluginSessionEventPublisher();
        for (var index = 0; index < PluginSessionEventPublisher.Capacity; index++)
        {
            publisher.Publish(Delta());
        }

        Assert.False(publisher.Publish(Delta("dropped")), "the host is too far behind for this one");
        Assert.False(publisher.Publish(Delta("dropped too")));

        Assert.Equal(2, publisher.PendingDroppedCount);
    }

    [Fact]
    public async Task Publish_AfterTheHostCatchesUp_ReportsTheGapIntoTheStream()
    {
        var publisher = new PluginSessionEventPublisher();
        for (var index = 0; index < PluginSessionEventPublisher.Capacity; index++)
        {
            publisher.Publish(Delta());
        }

        Assert.False(publisher.Publish(Delta("lost")));
        Assert.Equal(1, publisher.PendingDroppedCount);

        // The host catches up, then one more event goes through: that event takes its slot and the notice follows it.
        var drained = new List<PluginSessionEvent>();
        await foreach (var sessionEvent in publisher.Events)
        {
            drained.Add(sessionEvent);
            if (drained.Count == 3)
            {
                Assert.True(publisher.Publish(Delta("after the gap")));
                Assert.Equal(0, publisher.PendingDroppedCount);
                publisher.TryComplete();
            }
        }

        Assert.Contains("1 event(s)", Assert.Single(drained.OfType<PluginSessionError>()).Message);
    }

    [Fact]
    public void PendingDroppedCount_SurvivesAStillFullChannel()
    {
        // If the notice itself cannot be written the count must stay: losing it would lose the only record that
        // anything went missing at all.
        var publisher = new PluginSessionEventPublisher();
        for (var index = 0; index < PluginSessionEventPublisher.Capacity; index++)
        {
            publisher.Publish(Delta());
        }

        publisher.Publish(Delta("lost"));
        publisher.Publish(Delta("also lost"));

        Assert.Equal(2, publisher.PendingDroppedCount);
    }

    /// <summary>
    /// A driver that builds its own channel is back to a queue nothing bounds and nothing counts. A tripwire on the
    /// shape a driver actually writes, like the link-opener and plugin-version guards — it would miss a channel built
    /// through some other indirection, and the publisher is what carries the rule.
    /// </summary>
    [Fact]
    public void NoDriver_BuildsAnEventChannelOfItsOwn()
    {
        var pluginSources = _PluginSourceFiles().ToList();
        Assert.True(System.Linq.Enumerable.Count(pluginSources) > 50,
            "the repo ships twenty plugins — finding almost none means the walk broke, not that the rule holds");

        var offenders = pluginSources
            .Where(path => OwnEventChannelRegex().IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"Channel\s*\.\s*Create(Unbounded|Bounded)\s*<\s*PluginSessionEvent\s*>")]
    private static partial Regex OwnEventChannelRegex();

    private static IEnumerable<string> _PluginSourceFiles()
    {
        var pluginsDev = _LocateRepositoryFolder("plugins-dev")
            ?? throw new InvalidOperationException("No plugins-dev directory above the test output — this test reads the repo it belongs to.");

        return Directory.EnumerateFiles(pluginsDev, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string? _LocateRepositoryFolder(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
