using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Tracking;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="TrackerProviderRegistry"/> — the AC-154 tracker registry: the first registration of a tracker id wins, a
/// later one for the same id is refused, and the snapshot lists them in registration order.
/// </summary>
public class TrackerProviderRegistryTests
{
    private sealed class FakeProvider(string trackerId) : ITrackerProvider
    {
        public string TrackerId => trackerId;

        public Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    [Fact]
    public void Register_FirstWins_RefusesADuplicateId_AndSnapshotsInOrder()
    {
        var registry = new TrackerProviderRegistry();
        var first = new FakeProvider("youtrack");

        registry.Register(first).Should().BeTrue();
        registry.Register(new FakeProvider("youtrack")).Should().BeFalse();
        registry.Register(new FakeProvider("github-issues")).Should().BeTrue();

        registry.Providers.Should().HaveCount(2);
        registry.Providers[0].Should().BeSameAs(first);
        registry.Providers[1].TrackerId.Should().Be("github-issues");
    }
}
