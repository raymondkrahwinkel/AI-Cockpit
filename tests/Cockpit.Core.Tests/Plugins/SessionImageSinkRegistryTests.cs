using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The generic session-image sink registry (AC-14): delivers an image-bearing message to every registered sink, and isolates a sink that throws so it never breaks the send or the others.</summary>
public class SessionImageSinkRegistryTests
{
    private static readonly SessionImageDispatch Dispatch = new(
        "pane-1",
        [new SessionImageAttachment("image/png", "AAAA", "pasted-image-1.png")]);

    [Fact]
    public async Task NotifyAsync_DeliversToEveryRegisteredSink()
    {
        var registry = new SessionImageSinkRegistry(NullLogger<SessionImageSinkRegistry>.Instance);
        var seen = new List<string>();
        registry.Register(new SessionImageSinkRegistration(d => { seen.Add($"a:{d.PaneId}"); return Task.CompletedTask; }));
        registry.Register(new SessionImageSinkRegistration(d => { seen.Add($"b:{d.PaneId}"); return Task.CompletedTask; }));

        await registry.NotifyAsync(Dispatch);

        seen.Should().BeEquivalentTo("a:pane-1", "b:pane-1");
    }

    [Fact]
    public async Task NotifyAsync_ASinkThatThrows_DoesNotBreakTheOthersOrPropagate()
    {
        var registry = new SessionImageSinkRegistry(NullLogger<SessionImageSinkRegistry>.Instance);
        var reachedSecond = false;
        registry.Register(new SessionImageSinkRegistration(_ => throw new InvalidOperationException("boom")));
        registry.Register(new SessionImageSinkRegistration(_ => { reachedSecond = true; return Task.CompletedTask; }));

        var act = () => registry.NotifyAsync(Dispatch);

        await act.Should().NotThrowAsync();
        reachedSecond.Should().BeTrue();
    }
}
