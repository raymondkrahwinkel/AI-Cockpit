using Cockpit.Plugins.Abstractions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// What the two <c>StartSessionAsync</c> overloads do when a host has not implemented them (#AC-312). Both refuse, and
/// the named one deliberately does not fall back to the unnamed one: plugins bind to the host's copy of the SDK, so a
/// host older than the named overload has no such member to run a fallback in — the call never gets that far.
/// <c>minHostVersion</c> is what keeps that plugin off that host. A default written to look like a safety net would
/// only make the gap harder to see, and delegating in the same direction an implementation does is how the pair turns
/// into a stack overflow.
/// </summary>
public class CockpitActionsStartSessionDefaultsTests
{
    [Fact]
    public async Task AHostThatImplementsNeither_RefusesBoth()
    {
        ICockpitActions actions = new NoSessions();

        await Assert.ThrowsAsync<NotSupportedException>(() => actions.StartSessionAsync("Claude"));
        await Assert.ThrowsAsync<NotSupportedException>(() => actions.StartSessionAsync("Claude", null, null, "AC-312"));
    }

    [Fact]
    public async Task AHostThatOnlyStartsUnnamedSessions_RefusesTheNamedOne_RatherThanDroppingTheName()
    {
        ICockpitActions actions = new UnnamedSessionsOnly();

        Assert.Equal("started", (await actions.StartSessionAsync("Claude")));

        // Silently starting an unnamed session here would leave the caller believing its name had been applied.
        await Assert.ThrowsAsync<NotSupportedException>(() => actions.StartSessionAsync("Claude", null, null, "AC-312"));
    }

    private sealed class NoSessions : ICockpitActions
    {
        public bool HasActiveSession => false;

        public Task InjectIntoActiveSessionAsync(string text) => Task.CompletedTask;

        public Task SetClipboardTextAsync(string text) => Task.CompletedTask;
    }

    private sealed class UnnamedSessionsOnly : ICockpitActions
    {
        public bool HasActiveSession => false;

        public Task InjectIntoActiveSessionAsync(string text) => Task.CompletedTask;

        public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

        public Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null) =>
            Task.FromResult("started");
    }
}
